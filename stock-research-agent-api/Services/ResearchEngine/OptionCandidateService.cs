using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.OptionsData;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Services.ResearchEngine;

/// <summary>
/// Generates paper option candidates from qualified stock candidates.
///
/// Extracted from DynamicPickOrchestrator to isolate the option-generation
/// loop (per-run and per-ticker caps, candidate scoring, audit trail) into
/// a focused service with only three dependencies.
/// </summary>
public class OptionCandidateService
{
    public const int MaxOptionCandidatesPerRun = 25;
    public const int MaxOptionCandidatesPerTickerPerRun = 1;
    private const string ThresholdPolicyVersion = "learning_options_v1";

    private readonly PaperOptionsService _paperOptions;
    private readonly CandidateGenerationAuditRepository _auditRepo;
    private readonly ILogger<OptionCandidateService> _logger;

    public OptionCandidateService(
        PaperOptionsService paperOptions,
        CandidateGenerationAuditRepository auditRepo,
        ILogger<OptionCandidateService> logger)
    {
        _paperOptions = paperOptions;
        _auditRepo = auditRepo;
        _logger = logger;
    }

    /// <summary>
    /// Selects which stock candidates qualify for options (per-run and per-ticker caps),
    /// generates option candidates via PaperOptionsService, and writes audit rows.
    /// </summary>
    public async Task<OptionGenerationResult> GenerateOptionCandidatesAsync(
        List<StockCandidateService.StockCandidateBuild> stockBuilds,
        string runId,
        List<string> errors,
        double? maxContractCost = null)
    {
        if (maxContractCost is not null)
            _logger.LogInformation("[option-candidate] Portfolio budget caps contracts at ${Budget:F2}", maxContractCost);

        // Select top candidates for option generation, applying per-run and per-ticker caps.
        var optionAttempts = stockBuilds
            .Where(b => b.SavedCandidate is not null && b.SavedCandidate.QualifiesForOptions)
            .OrderByDescending(b => b.SavedCandidate!.ScorePercentileInRun)
            .ThenByDescending(b => b.Prediction.ConfidenceScore)
            .ThenBy(b => b.Prediction.RiskScore)
            .ThenByDescending(b => b.SavedCandidate!.DataAvailability == "real")
            .ToList();

        var selectedForOptions = new List<StockCandidateService.StockCandidateBuild>();
        var tickerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var build in optionAttempts)
        {
            if (selectedForOptions.Count >= MaxOptionCandidatesPerRun) break;
            var ticker = build.SavedCandidate!.Ticker;
            tickerCounts.TryGetValue(ticker, out var currentPerTicker);
            if (currentPerTicker >= MaxOptionCandidatesPerTickerPerRun) continue;
            selectedForOptions.Add(build);
            tickerCounts[ticker] = currentPerTicker + 1;
        }

        var selectedIds = selectedForOptions
            .Select(b => b.Prediction.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var optionsGenerated = 0;
        var blockedOptionCandidates = 0;
        var auditRows = new List<CandidateGenerationAuditEntry>();

        foreach (var build in stockBuilds)
        {
            var savedStock = build.SavedCandidate;
            var optionCreated = false;
            var paperOptionCandidateId = (string?)null;
            var optionBlockReason = savedStock?.ExclusionReason;
            var optionChainAvailable = false;
            var marketDataAvailable = savedStock is not null && savedStock.EntryPrice is > 0;

            if (savedStock is not null && savedStock.QualifiesForOptions)
            {
                if (!selectedIds.Contains(build.Prediction.Id))
                {
                    optionBlockReason = "max_candidates_reached";
                    blockedOptionCandidates++;
                }
                else
                {
                    try
                    {
                        var resp = await _paperOptions.GenerateCandidatesAsync(new GenerateCandidatesRequest
                        {
                            PredictionId = savedStock.PredictionId ?? "",
                            DurationPreference = StockCandidateService.ChooseDuration(savedStock),
                            AutoSave = true,
                            PaperStockCandidateId = savedStock.Id,
                            CandidateMode = savedStock.CandidateMode,
                            QualityTier = savedStock.QualityTier,
                            IsActionable = savedStock.IsActionable,
                            ThresholdPolicyVersion = savedStock.ThresholdPolicyVersion,
                            InclusionReason = savedStock.InclusionReason,
                            ExclusionReason = savedStock.ExclusionReason,
                            ScorePercentileInRun = savedStock.ScorePercentileInRun,
                            MaxContractCost = maxContractCost,
                        });

                        optionChainAvailable = resp?.OptionChainAvailable == true;
                        marketDataAvailable = resp?.MarketDataAvailable == true || marketDataAvailable;

                        if (resp?.SavedCandidate is not null)
                        {
                            optionCreated = true;
                            paperOptionCandidateId = resp.SavedCandidate.Id;
                            optionsGenerated++;
                            optionBlockReason = null;
                        }
                        else
                        {
                            optionBlockReason = resp?.BlockReason ?? "unknown_error";
                            blockedOptionCandidates++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[option-candidate] Option generation failed for {Ticker}", savedStock.Ticker);
                        errors.Add($"option-gen {savedStock.Ticker}: {ex.Message}");
                        optionBlockReason = "unknown_error";
                        blockedOptionCandidates++;
                    }
                }
            }
            else if (savedStock is not null)
            {
                optionBlockReason = savedStock.ExclusionReason ?? "confidence_below_learning_threshold";
            }

            auditRows.Add(new CandidateGenerationAuditEntry
            {
                RunId = build.Prediction.RunId,
                Ticker = build.Prediction.Ticker,
                PredictionCandidateId = build.Prediction.Id,
                PaperStockCandidateId = savedStock?.Id,
                PaperOptionCandidateId = paperOptionCandidateId,
                PredictionType = build.Prediction.PredictionType.ToString(),
                ConfidenceScore = build.Prediction.ConfidenceScore,
                RiskScore = build.Prediction.RiskScore,
                ScorePercentileInRun = build.Ranking?.Percentile ?? 0,
                StockCandidateCreated = savedStock is not null,
                OptionCandidateCreated = optionCreated,
                CandidateMode = savedStock?.CandidateMode ?? StockCandidateService.DetermineCandidateMode(build.Prediction),
                QualityTier = savedStock?.QualityTier ?? StockCandidateService.DetermineQualityTier(build.Prediction.ConfidenceScore, build.Prediction.ActionabilityTier),
                OptionBlockReason = optionBlockReason,
                MarketDataAvailable = marketDataAvailable,
                OptionChainAvailable = optionChainAvailable,
                ThresholdPolicyVersion = ThresholdPolicyVersion,
            });
        }

        foreach (var audit in auditRows)
            await _auditRepo.SaveAsync(audit);

        return new OptionGenerationResult(optionsGenerated, blockedOptionCandidates, auditRows);
    }

    public async Task<List<PaperOutcomeEnhanced>> EvaluateAllOpenOptionsAsync()
    {
        return await _paperOptions.EvaluateAllOpenAsync();
    }

    public async Task<List<CandidateGenerationAuditEntry>> GetAuditsByRunAsync(string runId)
    {
        return await _auditRepo.GetByRunAsync(runId);
    }
}

/// <summary>
/// Result of option candidate generation for a single morning run.
/// </summary>
public record OptionGenerationResult(
    int OptionsGenerated,
    int BlockedCandidates,
    List<CandidateGenerationAuditEntry> AuditRows);
