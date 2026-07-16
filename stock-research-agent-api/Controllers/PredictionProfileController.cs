using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using StockResearchAgent.Api.Models;
using StockResearchAgent.Api.Services.Supabase;

namespace StockResearchAgent.Api.Controllers;

[ApiController]
[Route("api/profiles")]
public class PredictionProfileController : ControllerBase
{
    private readonly PredictionProfileRepository _profileRepo;
    private readonly ResearchRepository _repo;

    public PredictionProfileController(PredictionProfileRepository profileRepo, ResearchRepository repo)
    {
        _profileRepo = profileRepo;
        _repo = repo;
    }

    [HttpGet]
    public async Task<IActionResult> ListProfiles()
    {
        var profiles = await _profileRepo.GetAllProfilesAsync();
        return Ok(profiles);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetProfile(string id)
    {
        var profile = await _profileRepo.GetProfileByIdAsync(id);
        if (profile is null) return NotFound();

        var configs = await _profileRepo.GetProfileConfigsAsync(id);
        return Ok(new { profile, configs });
    }

    [HttpPost]
    public async Task<IActionResult> CreateProfile([FromBody] CreateProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Profile name is required." });

        var profile = await _profileRepo.CreateProfileAsync(
            request.Name,
            request.Description,
            ProfileRole.challenger,
            request.LearningEnabled,
            hypothesis: request.Hypothesis,
            experimentStatus: request.ExperimentStatus);

        if (profile is null)
            return Conflict(new { error = "A profile with that name may already exist." });

        // Apply initial weight configs if provided
        if (request.Weights is { Count: > 0 })
            await _profileRepo.SetProfileConfigsAsync(profile.Id, request.Weights);

        return CreatedAtAction(nameof(GetProfile), new { id = profile.Id }, profile);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProfile(string id, [FromBody] UpdateProfileRequest request)
    {
        var existing = await _profileRepo.GetProfileByIdAsync(id);
        if (existing is null) return NotFound();

        var ok = await _profileRepo.UpdateProfileAsync(
            id,
            request.Name ?? existing.ProfileName,
            request.Description ?? existing.Description,
            request.IsEnabled ?? existing.IsEnabled,
            request.LearningEnabled ?? existing.LearningEnabled,
            hypothesis: request.Hypothesis,
            experimentStatus: request.ExperimentStatus);

        return ok ? Ok(new { updated = true }) : StatusCode(500, new { error = "Update failed." });
    }

    [HttpPut("{id}/config")]
    public async Task<IActionResult> UpdateConfig(string id, [FromBody] Dictionary<string, double> weights)
    {
        var profile = await _profileRepo.GetProfileByIdAsync(id);
        if (profile is null) return NotFound();

        if (profile.Role == ProfileRole.champion)
            return BadRequest(new { error = "Cannot directly modify champion weights. Use the learning engine." });

        await _profileRepo.SetProfileConfigsAsync(id, weights);
        return Ok(new { updated = true, configCount = weights.Count });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProfile(string id)
    {
        var profile = await _profileRepo.GetProfileByIdAsync(id);
        if (profile is null) return NotFound();

        if (profile.Role == ProfileRole.champion)
            return BadRequest(new { error = "Cannot delete the champion profile." });

        var ok = await _profileRepo.DeleteProfileAsync(id);
        return ok ? Ok(new { deleted = true }) : StatusCode(500, new { error = "Delete failed." });
    }

    [HttpPost("{id}/promote")]
    public async Task<IActionResult> PromoteProfile(string id)
    {
        var profile = await _profileRepo.GetProfileByIdAsync(id);
        if (profile is null) return NotFound();

        if (profile.Role == ProfileRole.champion)
            return BadRequest(new { error = "Profile is already the champion." });

        if (profile.ExperimentStatus != ExperimentStatus.testing && profile.ExperimentStatus != ExperimentStatus.completed)
            return BadRequest(new { error = "Only profiles in testing or completed status can be promoted." });

        var ok = await _profileRepo.PromoteToChampionAsync(id);
        return ok ? Ok(new { promoted = true }) : StatusCode(500, new { error = "Promotion failed." });
    }

    [HttpPost("{id}/archive")]
    public async Task<IActionResult> ArchiveProfile(string id)
    {
        var profile = await _profileRepo.GetProfileByIdAsync(id);
        if (profile is null) return NotFound();

        if (profile.Role == ProfileRole.champion)
            return BadRequest(new { error = "Cannot archive the champion profile." });

        var ok = await _profileRepo.ArchiveProfileAsync(id);
        return ok ? Ok(new { archived = true }) : StatusCode(500, new { error = "Archive failed." });
    }

    [HttpPost("{id}/start-testing")]
    public async Task<IActionResult> StartTesting(string id)
    {
        var profile = await _profileRepo.GetProfileByIdAsync(id);
        if (profile is null) return NotFound();

        if (profile.Role == ProfileRole.champion)
            return BadRequest(new { error = "Champion is always active." });

        if (profile.ExperimentStatus != ExperimentStatus.draft)
            return BadRequest(new { error = "Only draft profiles can be moved to testing." });

        var ok = await _profileRepo.UpdateProfileAsync(
            id, profile.ProfileName, profile.Description, isEnabled: true, profile.LearningEnabled,
            experimentStatus: ExperimentStatus.testing);

        return ok ? Ok(new { started = true }) : StatusCode(500, new { error = "Failed to start testing." });
    }

    [HttpPost("{id}/complete")]
    public async Task<IActionResult> CompleteExperiment(string id)
    {
        var profile = await _profileRepo.GetProfileByIdAsync(id);
        if (profile is null) return NotFound();

        if (profile.Role == ProfileRole.champion)
            return BadRequest(new { error = "Champion cannot be completed." });

        if (profile.ExperimentStatus != ExperimentStatus.testing)
            return BadRequest(new { error = "Only testing profiles can be marked completed." });

        var ok = await _profileRepo.UpdateProfileAsync(
            id, profile.ProfileName, profile.Description, isEnabled: false, profile.LearningEnabled,
            experimentStatus: ExperimentStatus.completed);

        return ok ? Ok(new { completed = true }) : StatusCode(500, new { error = "Failed to complete experiment." });
    }

    [HttpPost("{id}/clone")]
    public async Task<IActionResult> CloneProfile(string id, [FromBody] CloneProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "New profile name is required." });

        var cloned = await _profileRepo.CloneProfileAsync(id, request.Name, request.Description, request.Hypothesis);
        if (cloned is null)
            return BadRequest(new { error = "Source profile not found or name conflict." });

        return CreatedAtAction(nameof(GetProfile), new { id = cloned.Id }, cloned);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetProfileStats()
    {
        var profiles = await _profileRepo.GetAllProfilesAsync();
        var stats = new List<object>();
        foreach (var p in profiles)
        {
            var preds = await _repo.GetRecentPredictionsAsync(limit: 10000, profileId: p.Id);
            var evaluated = preds.Count(pr => pr.Status is "closed" or "superseded");
            stats.Add(new { profileId = p.Id, totalPredictions = preds.Count, evaluatedPredictions = evaluated });
        }
        return Ok(stats);
    }

    [HttpGet("champion-weights")]
    public async Task<IActionResult> GetChampionWeights()
    {
        var overrides = await _repo.GetActiveWeightOverridesAsync();
        var weights = overrides.ToDictionary(o => o.SignalName, o => o.EffectiveWeight);
        return Ok(weights);
    }

    [HttpGet("analytics")]
    public async Task<IActionResult> GetProfileAnalytics(
        [FromQuery] string? profileIds = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? ticker = null,
        [FromQuery] string? predictionType = null)
    {
        var ids = string.IsNullOrEmpty(profileIds)
            ? (await _profileRepo.GetAllProfilesAsync()).Select(p => p.Id).ToList()
            : profileIds.Split(',').ToList();

        var fromDate = DateTimeOffset.TryParse(from, out var f) ? f : DateTimeOffset.UtcNow.AddDays(-90);
        var toDate = DateTimeOffset.TryParse(to, out var t2) ? t2 : DateTimeOffset.UtcNow;

        var results = new List<object>();
        foreach (var pid in ids)
        {
            var profile = await _profileRepo.GetProfileByIdAsync(pid);
            if (profile is null) continue;

            var preds = await _repo.GetPredictionsByDateRangeAsync(fromDate, toDate, profileId: pid);
            if (!string.IsNullOrEmpty(ticker))
                preds = preds.Where(p => p.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrEmpty(predictionType))
                preds = preds.Where(p => p.PredictionType.ToString().Equals(predictionType, StringComparison.OrdinalIgnoreCase)).ToList();

            if (preds.Count == 0) { results.Add(new { profileId = pid, profileName = profile.ProfileName, role = profile.Role.ToString(), total = 0 }); continue; }

            var outcomes = await _repo.GetOutcomesForPredictionsAsync(preds.Select(p => p.Id).ToList());
            var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);

            var withOutcome = preds.Where(p => outcomeMap.ContainsKey(p.Id)).ToList();
            var bulls = withOutcome.Where(p => p.PredictionType.ToString().Contains("bullish", StringComparison.OrdinalIgnoreCase)).ToList();
            var bears = withOutcome.Where(p => p.PredictionType.ToString().Contains("bearish", StringComparison.OrdinalIgnoreCase)).ToList();
            var neutrals = withOutcome.Where(p => p.PredictionType.ToString().Contains("neutral", StringComparison.OrdinalIgnoreCase)).ToList();

            double acc(List<PredictionCandidate> set) => set.Count == 0 ? 0 : Math.Round(100.0 * set.Count(p => outcomeMap[p.Id].DirectionCorrect == true) / set.Count, 1);
            int wins = withOutcome.Count(p => outcomeMap[p.Id].DirectionCorrect == true);
            int losses = withOutcome.Count(p => outcomeMap[p.Id].DirectionCorrect == false);
            double avgReturn = withOutcome.Count > 0 ? Math.Round(withOutcome.Where(p => outcomeMap[p.Id].PercentMove.HasValue).Select(p => outcomeMap[p.Id].PercentMove!.Value).DefaultIfEmpty(0).Average(), 2) : 0;

            // Confidence calibration: group predictions by confidence bucket, measure actual accuracy
            var calibration = withOutcome
                .GroupBy(p => (int)(p.ConfidenceScore / 10) * 10)
                .OrderBy(g => g.Key)
                .Select(g => new {
                    bucket = g.Key,
                    predicted = g.Key + 5,
                    actual = Math.Round(100.0 * g.Count(p => outcomeMap[p.Id].DirectionCorrect == true) / g.Count(), 1),
                    count = g.Count()
                }).ToList();

            // Expected value by confidence bucket
            var evByBucket = withOutcome
                .Where(p => outcomeMap[p.Id].PercentMove.HasValue)
                .GroupBy(p => (int)(p.ConfidenceScore / 10) * 10)
                .OrderBy(g => g.Key)
                .Select(g => new {
                    bucket = g.Key,
                    avgEv = Math.Round(g.Average(p => outcomeMap[p.Id].PercentMove!.Value), 2),
                    count = g.Count()
                }).ToList();

            // Accuracy over time (weekly)
            var weekly = withOutcome
                .GroupBy(p => p.CreatedAt.ToString("yyyy-'W'") + System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(p.CreatedAt.DateTime, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday).ToString("D2"))
                .OrderBy(g => g.Key)
                .Select(g => new {
                    week = g.Key,
                    accuracy = Math.Round(100.0 * g.Count(p => outcomeMap[p.Id].DirectionCorrect == true) / g.Count(), 1),
                    count = g.Count()
                }).ToList();

            // Per-ticker breakdown
            var byTicker = withOutcome
                .GroupBy(p => p.Ticker)
                .OrderByDescending(g => g.Count())
                .Take(20)
                .Select(g => new {
                    ticker = g.Key,
                    total = g.Count(),
                    correct = g.Count(p => outcomeMap[p.Id].DirectionCorrect == true),
                    accuracy = Math.Round(100.0 * g.Count(p => outcomeMap[p.Id].DirectionCorrect == true) / g.Count(), 1),
                    avgReturn = Math.Round(g.Where(p => outcomeMap[p.Id].PercentMove.HasValue).Select(p => outcomeMap[p.Id].PercentMove!.Value).DefaultIfEmpty(0).Average(), 2)
                }).ToList();

            results.Add(new {
                profileId = pid,
                profileName = profile.ProfileName,
                role = profile.Role.ToString(),
                total = preds.Count,
                evaluated = withOutcome.Count,
                wins, losses,
                winRate = wins + losses > 0 ? Math.Round(100.0 * wins / (wins + losses), 1) : 0,
                bullAccuracy = acc(bulls), bullCount = bulls.Count,
                bearAccuracy = acc(bears), bearCount = bears.Count,
                neutralAccuracy = acc(neutrals), neutralCount = neutrals.Count,
                avgReturn,
                avgEv = withOutcome.Where(p => p.ExpectedValuePercent.HasValue).Select(p => p.ExpectedValuePercent!.Value).DefaultIfEmpty(0).Average(),
                calibration, evByBucket, weekly, byTicker,
            });
        }
        return Ok(results);
    }

    [HttpGet("predictions")]
    public async Task<IActionResult> GetProfilePredictions(
        [FromQuery] string? profileId = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? ticker = null,
        [FromQuery] string? predictionType = null,
        [FromQuery] string? outcome = null,
        [FromQuery] int limit = 100)
    {
        var fromDate = DateTimeOffset.TryParse(from, out var f) ? f : DateTimeOffset.UtcNow.AddDays(-90);
        var toDate = DateTimeOffset.TryParse(to, out var t2) ? t2 : DateTimeOffset.UtcNow;

        var preds = await _repo.GetPredictionsByDateRangeAsync(fromDate, toDate, profileId: profileId);
        if (!string.IsNullOrEmpty(ticker))
            preds = preds.Where(p => p.Ticker.Equals(ticker, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(predictionType))
            preds = preds.Where(p => p.PredictionType.ToString().Equals(predictionType, StringComparison.OrdinalIgnoreCase)).ToList();

        var predIds = preds.Select(p => p.Id).ToList();
        var outcomes = predIds.Count > 0 ? await _repo.GetOutcomesForPredictionsAsync(predIds) : new List<PredictionOutcome>();
        var outcomeMap = outcomes.ToDictionary(o => o.PredictionId);

        // Filter by outcome result
        if (!string.IsNullOrEmpty(outcome))
        {
            preds = outcome.ToLower() switch
            {
                "win" => preds.Where(p => outcomeMap.ContainsKey(p.Id) && outcomeMap[p.Id].DirectionCorrect == true).ToList(),
                "loss" => preds.Where(p => outcomeMap.ContainsKey(p.Id) && outcomeMap[p.Id].DirectionCorrect == false).ToList(),
                "pending" => preds.Where(p => !outcomeMap.ContainsKey(p.Id)).ToList(),
                _ => preds
            };
        }

        var result = preds.Take(limit).Select(p => {
            var oc = outcomeMap.GetValueOrDefault(p.Id);
            return new {
                id = p.Id,
                ticker = p.Ticker,
                predictionType = p.PredictionType.ToString(),
                confidence = p.ConfidenceScore,
                risk = p.RiskScore,
                expectedValue = p.ExpectedValuePercent,
                entryPrice = p.EntryReferencePrice,
                targetPrice = p.TargetPrice,
                stopPrice = p.StopPrice,
                status = p.Status,
                profileId = p.ProfileId,
                createdAt = p.CreatedAt,
                outcome = oc is not null ? new {
                    directionCorrect = oc.DirectionCorrect,
                    percentMove = oc.PercentMove,
                    outcomeScore = oc.OutcomeScore,
                    targetHit = oc.TargetHit,
                    stopHit = oc.StopHit,
                    maxFavorable = oc.MaxFavorablePercent,
                    maxAdverse = oc.MaxAdversePercent,
                    lesson = oc.Lesson,
                } : null
            };
        });

        return Ok(result);
    }

    // ── Request DTOs ────────────────────────────────────────────────

    public record CreateProfileRequest
    {
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public bool LearningEnabled { get; init; }
        public Dictionary<string, double>? Weights { get; init; }
        public string? Hypothesis { get; init; }
        public ExperimentStatus? ExperimentStatus { get; init; }
    }

    public record UpdateProfileRequest
    {
        public string? Name { get; init; }
        public string? Description { get; init; }
        public bool? IsEnabled { get; init; }
        public bool? LearningEnabled { get; init; }
        public string? Hypothesis { get; init; }
        public ExperimentStatus? ExperimentStatus { get; init; }
    }

    public record CloneProfileRequest
    {
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string? Hypothesis { get; init; }
    }
}
