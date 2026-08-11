namespace StockResearchAgent.Api.Services.ResearchEngine.Evaluation;

public class CatalystEvaluator : ICatalystEvaluator
{
    public EvaluatorKind Kind => EvaluatorKind.catalyst;

    public EvaluatorOutput Evaluate(EvaluationContext context)
    {
        var news = context.Snapshot.NewsContext;
        var signals = new List<string>();
        double bull = 0, bear = 0;
        var weights = context.LearningData.Weights;

        // ── Earnings calendar proximity boost ──────────────────────────
        // Upcoming earnings are a major catalyst regardless of news sentiment.
        // Boost both bull and bear (earnings = high volatility both directions).
        // Checked BEFORE news-empty short-circuit so earnings-only catalysts still score.
        if (context.DaysUntilEarnings is not null)
        {
            var daysOut = context.DaysUntilEarnings.Value;
            double earningsBoost = daysOut switch
            {
                0 => 10,    // Reporting today — maximum catalyst impact
                1 => 8,     // Tomorrow — very high
                2 => 6,     // 2 days out — strong
                3 => 5,     // 3 days — moderate-strong
                <= 5 => 3,  // Within a week — moderate
                <= 7 => 2,  // Week out — mild awareness
                _ => 0,
            };

            if (earningsBoost > 0)
            {
                bull += earningsBoost;
                bear += earningsBoost;
                var epsNote = context.EstimatedEps is not null
                    ? $", est EPS=${context.EstimatedEps:F2}"
                    : "";
                signals.Add($"Catalyst: earnings in {daysOut}d{epsNote} — volatility boost +{earningsBoost:F0}");
            }
        }

        // No news and no earnings — nothing to score
        if (news.Count == 0 && bull == 0)
        {
            signals.Add("Catalyst: no recent news or upcoming earnings");
            return new EvaluatorOutput
            {
                Kind = Kind,
                DebugSignals = signals,
                DebugInformation = new EvaluatorReasoning
                {
                    EvaluatorName = nameof(CatalystEvaluator),
                    Summary = "No directional catalyst contribution.",
                    Reasons = signals,
                },
            };
        }

        if (news.Count == 0)
        {
            // Earnings-only catalyst — no news to score, just return earnings boost
            return new EvaluatorOutput
            {
                Kind = Kind,
                BullishContribution = Math.Clamp(bull, 0, 25),
                BearishContribution = Math.Clamp(bear, 0, 25),
                DebugSignals = signals,
                DebugInformation = new EvaluatorReasoning
                {
                    EvaluatorName = nameof(CatalystEvaluator),
                    Summary = "Earnings calendar catalyst — upcoming report boosts volatility expectation.",
                    Reasons = signals,
                },
            };
        }

        var sources = news.Select(n => n.SourceName).Distinct().Count();
        if (sources >= 3) { bull += 3; bear += 3; signals.Add($"Catalyst: {sources} sources confirming"); }
        else if (sources >= 2) { bull += 1; bear += 1; signals.Add($"Catalyst: {sources} sources"); }

        foreach (var item in news)
        {
            var catKey = item.CatalystType is not null ? $"catalyst_{item.CatalystType}" : null;
            var catW = catKey is not null ? weights.GetValueOrDefault(catKey, 1.0) : 1.0;
            var impactScore = item.ImportanceScore * catW * 3;

            if (item.Sentiment == "bullish")
                bull += impactScore;
            else if (item.Sentiment == "bearish")
                bear += impactScore;

            var preview = item.Title.Length > 50 ? item.Title[..50] : item.Title;
            signals.Add($"Catalyst: \"{preview}\" ({item.Sentiment ?? "neutral"}, imp={item.ImportanceScore:F0})");
        }

        return new EvaluatorOutput
        {
            Kind = Kind,
            BullishContribution = Math.Clamp(bull, 0, 25),
            BearishContribution = Math.Clamp(bear, 0, 25),
            DebugSignals = signals,
            DebugInformation = new EvaluatorReasoning
            {
                EvaluatorName = nameof(CatalystEvaluator),
                Summary = "Catalyst contribution based on directional news sentiment and importance.",
                Reasons = signals,
                SupportingFeatureIds = context.Intelligence.Features
                    .Where(f => f.FeatureId.Contains("event", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.FeatureId)
                    .ToList(),
                SupportingEvidenceIds = context.Intelligence.Evidence
                    .Where(e => e.EvidenceId.Contains("event", StringComparison.OrdinalIgnoreCase))
                    .Select(e => e.EvidenceId)
                    .ToList(),
            },
        };
    }

    public double ScoreCatalystStrength(EvaluationContext context)
    {
        var news = context.Snapshot.NewsContext;
        if (news.Count == 0 && context.DaysUntilEarnings is null) return 0;
        if (news.Count == 0)
        {
            // Earnings-only catalyst strength
            double earningsOnly = context.DaysUntilEarnings <= 5 ? 6 : context.DaysUntilEarnings <= 7 ? 3 : 0;
            return Math.Clamp(earningsOnly, 0, 25);
        }

        double strength = 0;
        strength += news.Count switch
        {
            >= 10 => 6,
            >= 5 => 4,
            >= 3 => 2,
            >= 1 => 1,
            _ => 0,
        };

        var maxImportance = news.Max(n => n.ImportanceScore);
        strength += maxImportance switch
        {
            >= 85 => 8,
            >= 65 => 5,
            >= 45 => 3,
            >= 25 => 1,
            _ => 0,
        };

        var mostRecent = news
            .Select(n => DateTimeOffset.TryParse(n.PublishedAt, out var dt) ? dt : (DateTimeOffset?)null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        var hoursOld = (DateTimeOffset.UtcNow - mostRecent).TotalHours;
        strength += hoursOld switch
        {
            <= 2 => 5,
            <= 6 => 4,
            <= 12 => 3,
            <= 24 => 2,
            <= 48 => 1,
            _ => 0,
        };

        var catalystTypes = news
            .Where(n => n.CatalystType is not null)
            .Select(n => n.CatalystType!)
            .Distinct()
            .ToList();

        var highVelocityTypes = new HashSet<string> { "earnings", "merger", "acquisition", "fda", "guidance", "buyback" };
        if (catalystTypes.Any(t => highVelocityTypes.Contains(t)))
            strength += 4;
        else if (catalystTypes.Count >= 2)
            strength += 2;
        else if (catalystTypes.Count >= 1)
            strength += 1;

        var sourceCount = news.Select(n => n.SourceName).Distinct().Count();
        if (sourceCount >= 4) strength += 2;
        else if (sourceCount >= 2) strength += 1;

        // Earnings calendar proximity — strongest single catalyst signal
        if (context.DaysUntilEarnings is not null && context.DaysUntilEarnings <= 5)
            strength += 6;
        else if (context.DaysUntilEarnings is not null && context.DaysUntilEarnings <= 7)
            strength += 3;

        return Math.Clamp(strength, 0, 25);
    }
}
