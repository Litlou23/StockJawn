using Microsoft.Extensions.Options;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

public record WeightUpdateValidation(bool Approved, string Reason);

/// <summary>
/// Gates every weight mutation in the learning loop.
/// Checks sample size, statistical significance, accuracy trend,
/// and regime consistency before allowing an update through.
/// </summary>
public class WeightUpdateValidator
{
    private readonly LearningGuardrailOptions _opts;
    private readonly ResearchRepository _repo;
    private readonly ILogger<WeightUpdateValidator> _logger;

    public WeightUpdateValidator(
        IOptions<LearningGuardrailOptions> opts,
        ResearchRepository repo,
        ILogger<WeightUpdateValidator> logger)
    {
        _opts = opts.Value;
        _repo = repo;
        _logger = logger;
    }

    /// <summary>
    /// Validate a signal-level weight adjustment (Stage 4 / 4b).
    /// </summary>
    public async Task<WeightUpdateValidation> ValidateSignalWeightUpdateAsync(
        string signalName,
        int sampleSize,
        double accuracy,
        double proposedMovement)
    {
        if (_opts.Frozen)
            return Reject(signalName, "weight_frozen", "Learning is frozen — all updates blocked");

        // 1. Sample size
        if (sampleSize < _opts.MinSampleSize)
            return Reject(signalName, "sample_size",
                $"Sample {sampleSize} < minimum {_opts.MinSampleSize}");

        // 2. Confidence interval — is accuracy meaningfully different from 50%?
        if (_opts.EnforceConfidenceInterval)
        {
            var z = ComputeZScore(accuracy, sampleSize);
            if (Math.Abs(z) < _opts.RequiredZScore)
                return Reject(signalName, "confidence_interval",
                    $"z={z:F2} < required {_opts.RequiredZScore:F2} (accuracy {accuracy:P1}, n={sampleSize})");
        }

        // 3. Accuracy trend — block upward moves if recent accuracy is declining
        if (_opts.EnforceAccuracyTrend && proposedMovement > 0)
        {
            var trendResult = await CheckAccuracyTrendAsync(signalName);
            if (trendResult is not null)
                return trendResult;
        }

        // 4. Regime consistency — throttle if performance varies wildly across regimes
        if (_opts.EnforceRegimeConsistency)
        {
            var regimeResult = await CheckRegimeConsistencyAsync(signalName, proposedMovement);
            if (regimeResult is not null)
                return regimeResult;
        }

        // 5. Cap magnitude
        var clampedMovement = Math.Clamp(proposedMovement, -_opts.MaxDailyMovement, _opts.MaxDailyMovement);
        if (Math.Abs(clampedMovement) < Math.Abs(proposedMovement))
        {
            _logger.LogInformation(
                "[guardrail] {Signal}: movement clamped {Proposed:F4} → {Clamped:F4}",
                signalName, proposedMovement, clampedMovement);
        }

        return new WeightUpdateValidation(true,
            $"Approved: n={sampleSize}, accuracy={accuracy:P1}, movement={clampedMovement:F4}");
    }

    /// <summary>
    /// Validate calibration factor update (Stage 3b).
    /// </summary>
    public WeightUpdateValidation ValidateCalibrationUpdate(
        int totalWeight, double avgError, double proposedMovement)
    {
        if (_opts.Frozen)
            return Reject("calibration_factor", "weight_frozen", "Learning is frozen");

        if (totalWeight < _opts.MinCalibrationSample)
            return Reject("calibration_factor", "sample_size",
                $"Sample {totalWeight} < minimum {_opts.MinCalibrationSample}");

        if (Math.Abs(proposedMovement) > _opts.MaxCalibrationMovement)
        {
            _logger.LogInformation(
                "[guardrail] calibration_factor: movement clamped {Proposed:F4} → {Clamped:F4}",
                proposedMovement, Math.Clamp(proposedMovement, -_opts.MaxCalibrationMovement, _opts.MaxCalibrationMovement));
        }

        return new WeightUpdateValidation(true,
            $"Approved: n={totalWeight}, avgError={avgError:P1}");
    }

    /// <summary>
    /// Validate risk-cap-boost update (Stage 3c).
    /// </summary>
    public WeightUpdateValidation ValidateCapBoostUpdate(
        int sampleSize, double calError, double proposedMovement)
    {
        if (_opts.Frozen)
            return Reject("risk_cap_boost", "weight_frozen", "Learning is frozen");

        if (sampleSize < _opts.MinCapBoostSample)
            return Reject("risk_cap_boost", "sample_size",
                $"Sample {sampleSize} < minimum {_opts.MinCapBoostSample}");

        if (Math.Abs(proposedMovement) > _opts.MaxCapBoostMovement)
        {
            _logger.LogInformation(
                "[guardrail] risk_cap_boost: movement clamped {Proposed:F1} → {Clamped:F1}",
                proposedMovement, Math.Clamp(proposedMovement, -_opts.MaxCapBoostMovement, _opts.MaxCapBoostMovement));
        }

        return new WeightUpdateValidation(true,
            $"Approved: n={sampleSize}, calError={calError:P1}");
    }

    /// <summary>
    /// Validate pattern-based recommendation (Stage 4b).
    /// </summary>
    public WeightUpdateValidation ValidatePatternRecommendation(
        string signalName, int evidence, double confidence, double proposedMovement)
    {
        if (_opts.Frozen)
            return Reject(signalName, "weight_frozen", "Learning is frozen");

        if (evidence < _opts.MinPatternEvidence)
            return Reject(signalName, "pattern_evidence",
                $"Evidence {evidence} < minimum {_opts.MinPatternEvidence}");

        return new WeightUpdateValidation(true,
            $"Approved: evidence={evidence}, confidence={confidence:F2}");
    }

    /// <summary>
    /// Returns the effective daily movement limit, potentially throttled
    /// by regime inconsistency.
    /// </summary>
    public async Task<double> GetEffectiveDailyMovementAsync(string signalName)
    {
        if (!_opts.EnforceRegimeConsistency)
            return _opts.MaxDailyMovement;

        var spread = await ComputeRegimeSpreadAsync(signalName);
        if (spread > _opts.MaxRegimeSpread)
        {
            var throttled = _opts.MaxDailyMovement * _opts.RegimeThrottleFactor;
            _logger.LogInformation(
                "[guardrail] {Signal}: regime spread {Spread:P1} > {Max:P1}, throttling movement to {Throttled:F4}",
                signalName, spread, _opts.MaxRegimeSpread, throttled);
            return throttled;
        }

        return _opts.MaxDailyMovement;
    }

    // ── Internal checks ──────────────────────────────────────────────

    private async Task<WeightUpdateValidation?> CheckAccuracyTrendAsync(string signalName)
    {
        try
        {
            var recentObs = await _repo.GetSignalObservationsAsync(
                limit: 1000, windowDays: _opts.RecentWindowDays);
            var signalRecent = recentObs
                .Where(o => o.SignalName == signalName && o.Correct is not null)
                .ToList();

            if (signalRecent.Count < _opts.MinRecentSample)
                return null; // not enough recent data to evaluate trend

            var fullObs = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180);
            var signalFull = fullObs
                .Where(o => o.SignalName == signalName && o.Correct is not null)
                .ToList();

            if (signalFull.Count < _opts.MinSampleSize)
                return null;

            var recentAccuracy = (double)signalRecent.Count(o => o.Correct == true) / signalRecent.Count;
            var fullAccuracy = (double)signalFull.Count(o => o.Correct == true) / signalFull.Count;
            var decline = fullAccuracy - recentAccuracy;

            if (decline > _opts.MaxAccuracyDecline)
            {
                return Reject(signalName, "accuracy_declining",
                    $"Recent accuracy {recentAccuracy:P1} is {decline:P1} below overall {fullAccuracy:P1} " +
                    $"(threshold {_opts.MaxAccuracyDecline:P1}). Blocking weight increase.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[guardrail] Accuracy trend check failed for {Signal}", signalName);
        }

        return null;
    }

    private async Task<WeightUpdateValidation?> CheckRegimeConsistencyAsync(
        string signalName, double proposedMovement)
    {
        try
        {
            var spread = await ComputeRegimeSpreadAsync(signalName);
            // Don't block — just log. Throttling happens via GetEffectiveDailyMovementAsync.
            if (spread > _opts.MaxRegimeSpread)
            {
                _logger.LogInformation(
                    "[guardrail] {Signal}: regime spread {Spread:P1} exceeds {Max:P1}. " +
                    "Movement will be throttled.",
                    signalName, spread, _opts.MaxRegimeSpread);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[guardrail] Regime consistency check failed for {Signal}", signalName);
        }

        return null;
    }

    private async Task<double> ComputeRegimeSpreadAsync(string signalName)
    {
        var observations = await _repo.GetSignalObservationsAsync(limit: 5000, windowDays: 180);
        var byRegime = observations
            .Where(o => o.SignalName == signalName && o.Correct is not null
                     && !string.IsNullOrEmpty(o.MarketRegime))
            .GroupBy(o => o.MarketRegime!)
            .Where(g => g.Count() >= _opts.MinRegimeSample)
            .ToList();

        if (byRegime.Count < 2)
            return 0; // can't measure spread with fewer than 2 regimes

        var accuracies = byRegime
            .Select(g => (double)g.Count(o => o.Correct == true) / g.Count())
            .ToList();

        return accuracies.Max() - accuracies.Min();
    }

    /// <summary>
    /// Two-tailed z-score testing whether observed accuracy differs from 0.5.
    /// </summary>
    private static double ComputeZScore(double accuracy, int n)
    {
        if (n == 0) return 0;
        var p0 = 0.5;
        var se = Math.Sqrt(p0 * (1 - p0) / n);
        return se == 0 ? 0 : (accuracy - p0) / se;
    }

    private WeightUpdateValidation Reject(string signal, string check, string reason)
    {
        _logger.LogWarning("[guardrail] REJECTED {Signal} ({Check}): {Reason}", signal, check, reason);
        return new WeightUpdateValidation(false, reason);
    }
}
