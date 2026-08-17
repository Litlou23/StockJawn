using System.Text.Json;

namespace StockResearchAgent.Api.Services.Backtesting;

/// <summary>
/// Simulates portfolio management during backtests. Tracks cash,
/// open/closed positions, equity curve, and enforces the same risk
/// rules as the live pipeline: position sizing, max open positions,
/// daily loss limit, sector concentration, repeat-loser blacklist,
/// stop-loss, take-profit, trailing stop, and time stop.
///
/// No new services — this is a plain C# class instantiated per run.
/// </summary>
public class SimulatedPortfolio
{
    private readonly SimPortfolioConfig _config;

    public double StartingCash { get; }
    public double Cash { get; private set; }
    public List<SimPosition> OpenPositions { get; } = [];
    public List<SimPosition> ClosedPositions { get; } = [];
    public List<EquitySnapshot> EquityCurve { get; } = [];

    // ── Per-day tracking ──
    private double _dailyRealizedLoss;
    private DateOnly _currentDay;

    public SimulatedPortfolio(double startingCash, SimPortfolioConfig? config = null)
    {
        StartingCash = startingCash;
        Cash = startingCash;
        _config = config ?? new SimPortfolioConfig();
    }

    // ── Entry logic ────────────────────────────────────────────

    /// <summary>
    /// Try to open a new position. Returns true if the trade was opened,
    /// false if blocked by a portfolio constraint (cash, position limit,
    /// sector concentration, blacklist, etc.)
    /// </summary>
    public bool TryOpenPosition(
        string ticker,
        string direction,
        string timeframe,
        DateOnly entryDate,
        double entryPrice,
        int confidence,
        double expectedValue,
        double riskRewardRatio,
        string? sector = null,
        string? scoreDebug = null,
        double? metaProbability = null,
        int? metaModelVersion = null)
    {
        // ── Cash check ──
        if (Cash < _config.MinPositionDollars)
            return false;

        // ── Max open positions ──
        if (OpenPositions.Count >= _config.MaxOpenPositions)
            return false;

        // ── Confidence floor ──
        if (confidence < _config.MinConfidence)
            return false;

        // ── EV gate ──
        if (expectedValue < _config.MinExpectedValue)
            return false;

        // ── Daily loss limit ──
        if (_config.DailyLossLimitEnabled && _dailyRealizedLoss >= Cash * _config.DailyLossLimitPct)
            return false;

        // ── Duplicate ticker check ──
        if (OpenPositions.Any(p => string.Equals(p.Ticker, ticker, StringComparison.OrdinalIgnoreCase)))
            return false;

        // ── Sector concentration ──
        if (sector is not null)
        {
            var sectorCount = OpenPositions.Count(p =>
                string.Equals(p.Sector, sector, StringComparison.OrdinalIgnoreCase));
            if (sectorCount >= _config.MaxPerSector)
                return false;
        }

        // ── Repeat-loser blacklist ──
        var recentLosses = ClosedPositions.Count(p =>
            string.Equals(p.Ticker, ticker, StringComparison.OrdinalIgnoreCase)
            && p.PnlPercent < 0
            && p.ExitDate.HasValue
            && entryDate.DayNumber - p.ExitDate.Value.DayNumber <= _config.BlacklistDays);
        if (recentLosses >= 2)
            return false;

        // ── Stop-loss cooldown — don't re-enter stopped-out tickers ──
        var recentStopOut = ClosedPositions.Any(p =>
            string.Equals(p.Ticker, ticker, StringComparison.OrdinalIgnoreCase)
            && p.ExitReason is "stop_loss" or "trailing_stop"
            && p.ExitDate.HasValue
            && entryDate.DayNumber - p.ExitDate.Value.DayNumber <= 1);
        if (recentStopOut)
            return false;

        // ── Bearish direction filter ──
        if (direction == "bearish" && confidence < _config.MinBearishConfidence)
            return false;

        // ── Position sizing (simplified Kelly-confidence scaling) ──
        var fraction = ComputePositionFraction(confidence, expectedValue);
        var maxDollars = Cash * fraction;
        var quantity = Math.Floor(maxDollars / entryPrice * 100) / 100; // round down to 2 decimals
        if (quantity < 0.01 || quantity * entryPrice < _config.MinPositionDollars)
            return false;

        var dollarsInvested = Math.Round(quantity * entryPrice, 2);

        // ── Determine SL/TP thresholds ──
        var (slPct, tpPct, trailActivate, trailPct, maxHoldDays) = GetRiskParams(timeframe);

        var position = new SimPosition
        {
            Ticker = ticker,
            Direction = direction,
            Timeframe = timeframe,
            EntryDate = entryDate,
            EntryPrice = entryPrice,
            Quantity = quantity,
            DollarsInvested = dollarsInvested,
            Confidence = confidence,
            ExpectedValue = expectedValue,
            RiskRewardRatio = riskRewardRatio,
            Sector = sector,
            StopLossPct = slPct,
            TakeProfitPct = tpPct,
            TrailActivatePct = trailActivate,
            TrailPct = trailPct,
            MaxHoldDays = maxHoldDays,
            HighWaterMark = entryPrice,
            ScoreDebug = scoreDebug,
            MetaProbability = metaProbability,
            MetaModelVersion = metaModelVersion,
        };

        Cash -= dollarsInvested;
        OpenPositions.Add(position);
        return true;
    }

    // ── Exit logic — process a day's candle for all open positions ──

    /// <summary>
    /// Check all open positions against a day's candle data and close
    /// any that hit SL/TP/trailing stop/time stop. Call once per
    /// simulated trading day.
    /// </summary>
    public void ProcessDay(DateOnly day, Dictionary<string, HistoricalCandle> candleMap)
    {
        // Reset daily loss if new day
        if (day != _currentDay)
        {
            _dailyRealizedLoss = 0;
            _currentDay = day;
        }

        var toClose = new List<(SimPosition pos, double exitPrice, string exitReason)>();

        foreach (var pos in OpenPositions)
        {
            if (!candleMap.TryGetValue(pos.Ticker, out var candle))
                continue;

            var isBullish = pos.Direction == "bullish";
            var entryPrice = pos.EntryPrice;

            // Track max favorable/adverse excursion
            if (isBullish)
            {
                var favorable = (candle.High - entryPrice) / entryPrice;
                var adverse = (entryPrice - candle.Low) / entryPrice;
                pos.MaxFavorablePercent = Math.Max(pos.MaxFavorablePercent, favorable);
                pos.MaxAdversePercent = Math.Max(pos.MaxAdversePercent, adverse);
            }
            else
            {
                var favorable = (entryPrice - candle.Low) / entryPrice;
                var adverse = (candle.High - entryPrice) / entryPrice;
                pos.MaxFavorablePercent = Math.Max(pos.MaxFavorablePercent, favorable);
                pos.MaxAdversePercent = Math.Max(pos.MaxAdversePercent, adverse);
            }

            // Update high-water mark
            if (isBullish && candle.High > pos.HighWaterMark)
                pos.HighWaterMark = candle.High;
            else if (!isBullish && candle.Low < pos.HighWaterMark)
                pos.HighWaterMark = candle.Low;

            // ── Stop-loss check ──
            var stopPrice = isBullish
                ? entryPrice * (1 - pos.StopLossPct)
                : entryPrice * (1 + pos.StopLossPct);

            if (isBullish && candle.Low <= stopPrice)
            {
                toClose.Add((pos, stopPrice, "stop_loss"));
                continue;
            }
            if (!isBullish && candle.High >= stopPrice)
            {
                toClose.Add((pos, stopPrice, "stop_loss"));
                continue;
            }

            // ── Take-profit check ──
            var targetPrice = isBullish
                ? entryPrice * (1 + pos.TakeProfitPct)
                : entryPrice * (1 - pos.TakeProfitPct);

            if (pos.TakeProfitPct > 0)
            {
                if (isBullish && candle.High >= targetPrice)
                {
                    toClose.Add((pos, targetPrice, "take_profit"));
                    continue;
                }
                if (!isBullish && candle.Low <= targetPrice)
                {
                    toClose.Add((pos, targetPrice, "take_profit"));
                    continue;
                }
            }

            // ── Trailing stop ──
            if (pos.TrailActivatePct > 0 && pos.TrailPct > 0)
            {
                double hwmGain;
                if (isBullish)
                    hwmGain = (pos.HighWaterMark - entryPrice) / entryPrice;
                else
                    hwmGain = (entryPrice - pos.HighWaterMark) / entryPrice;

                if (hwmGain >= pos.TrailActivatePct)
                {
                    double trailFloor;
                    if (isBullish)
                    {
                        trailFloor = Math.Max(
                            pos.HighWaterMark * (1 - pos.TrailPct),
                            entryPrice * 1.001);
                        if (candle.Low <= trailFloor)
                        {
                            toClose.Add((pos, trailFloor, "trailing_stop"));
                            continue;
                        }
                    }
                    else
                    {
                        trailFloor = Math.Min(
                            pos.HighWaterMark * (1 + pos.TrailPct),
                            entryPrice * 0.999);
                        if (candle.High >= trailFloor)
                        {
                            toClose.Add((pos, trailFloor, "trailing_stop"));
                            continue;
                        }
                    }
                }
            }

            // ── Time stop ──
            var daysHeld = day.DayNumber - pos.EntryDate.DayNumber;
            if (pos.MaxHoldDays > 0 && daysHeld >= pos.MaxHoldDays)
            {
                // Only close if not moving enough (same logic as live pipeline)
                var pnlPct = isBullish
                    ? (candle.Close - entryPrice) / entryPrice
                    : (entryPrice - candle.Close) / entryPrice;

                var minMove = pos.Timeframe switch
                {
                    "1_day" => 0.005,
                    "3_day" => 0.008,
                    _ => 0.01,
                };

                if (pnlPct < minMove)
                {
                    toClose.Add((pos, candle.Close, "time_stop"));
                    continue;
                }
            }
        }

        // ── Close positions ──
        foreach (var (pos, exitPrice, exitReason) in toClose)
        {
            ClosePosition(pos, day, exitPrice, exitReason);
        }

        // ── Record equity snapshot ──
        var unrealizedValue = 0.0;
        foreach (var pos in OpenPositions)
        {
            if (candleMap.TryGetValue(pos.Ticker, out var c))
            {
                var isBullish = pos.Direction == "bullish";
                var markToMarket = isBullish
                    ? pos.Quantity * c.Close
                    : pos.Quantity * (2 * pos.EntryPrice - c.Close);
                unrealizedValue += markToMarket;
            }
            else
            {
                unrealizedValue += pos.DollarsInvested; // no candle = assume flat
            }
        }

        EquityCurve.Add(new EquitySnapshot
        {
            Date = day,
            Cash = Math.Round(Cash, 2),
            InvestedValue = Math.Round(unrealizedValue, 2),
            TotalEquity = Math.Round(Cash + unrealizedValue, 2),
            OpenPositionCount = OpenPositions.Count,
        });
    }

    /// <summary>
    /// Force-close all remaining open positions at the given day's prices.
    /// Used at the end of a backtest run.
    /// </summary>
    public void CloseAllOpen(DateOnly day, Dictionary<string, HistoricalCandle> candleMap)
    {
        var toClose = new List<SimPosition>(OpenPositions);
        foreach (var pos in toClose)
        {
            var exitPrice = candleMap.TryGetValue(pos.Ticker, out var c)
                ? c.Close
                : pos.EntryPrice; // no candle = assume flat
            ClosePosition(pos, day, exitPrice, "end_of_backtest");
        }
    }

    /// <summary>Get all closed positions converted to BacktestTrade format.</summary>
    public List<BacktestTrade> GetTrades()
    {
        return ClosedPositions.Select(p =>
        {
            var isBullish = p.Direction == "bullish";
            var pnlPct = isBullish
                ? (p.ExitPrice!.Value - p.EntryPrice) / p.EntryPrice * 100
                : (p.EntryPrice - p.ExitPrice!.Value) / p.EntryPrice * 100;

            return new BacktestTrade
            {
                Ticker = p.Ticker,
                Direction = p.Direction,
                Timeframe = p.Timeframe,
                EntryDate = p.EntryDate,
                EntryPrice = Math.Round(p.EntryPrice, 2),
                ExitDate = p.ExitDate,
                ExitPrice = Math.Round(p.ExitPrice.Value, 2),
                ExitReason = p.ExitReason,
                PnlDollars = Math.Round(p.PnlDollars, 2),
                PnlPercent = Math.Round(pnlPct, 4),
                MaxFavorablePercent = Math.Round(p.MaxFavorablePercent * 100, 4),
                MaxAdversePercent = Math.Round(p.MaxAdversePercent * 100, 4),
                Confidence = p.Confidence,
                ExpectedValue = p.ExpectedValue,
                RiskRewardRatio = p.RiskRewardRatio,
                ScoreDebug = p.ScoreDebug,
                MetaProbability = p.MetaProbability,
                MetaModelVersion = p.MetaModelVersion,
            };
        }).ToList();
    }

    // ── Private helpers ────────────────────────────────────────

    private void ClosePosition(SimPosition pos, DateOnly day, double exitPrice, string exitReason)
    {
        var isBullish = pos.Direction == "bullish";
        var pnlPerShare = isBullish
            ? exitPrice - pos.EntryPrice
            : pos.EntryPrice - exitPrice;
        var pnlDollars = Math.Round(pnlPerShare * pos.Quantity, 2);

        pos.ExitDate = day;
        pos.ExitPrice = exitPrice;
        pos.ExitReason = exitReason;
        pos.PnlDollars = pnlDollars;

        // Return capital + P&L
        Cash += pos.DollarsInvested + pnlDollars;

        // Track daily losses for daily loss limit
        if (pnlDollars < 0)
            _dailyRealizedLoss += Math.Abs(pnlDollars);

        OpenPositions.Remove(pos);
        ClosedPositions.Add(pos);
    }

    private double ComputePositionFraction(int confidence, double expectedValue)
    {
        // Linear confidence scaling: minFraction at ConfidenceFloor, maxFraction at ceiling
        var clampedConf = Math.Clamp(confidence,
            (int)_config.ConfidenceFloor, (int)_config.ConfidenceCeiling);
        var confRange = _config.ConfidenceCeiling - _config.ConfidenceFloor;
        var confT = confRange > 0
            ? (clampedConf - _config.ConfidenceFloor) / confRange
            : 0.5;
        var fraction = _config.MinFraction + confT * (_config.MaxFraction - _config.MinFraction);

        // EV adjustment
        if (expectedValue > 0.05)
            fraction = Math.Min(fraction + _config.EvBonus, _config.MaxFraction);
        else if (expectedValue < 0)
            fraction *= _config.EvPenalty;

        return Math.Clamp(fraction, _config.MinFraction, _config.MaxFraction);
    }

    private (double slPct, double tpPct, double trailActivate, double trailPct, int maxHoldDays) GetRiskParams(string timeframe)
    {
        return timeframe switch
        {
            "1_day" => (
                _config.StopLossDay, _config.TakeProfitDay,
                _config.TrailActivateDay, _config.TrailPctDay,
                1),
            "3_day" => (
                _config.StopLossSwing, _config.TakeProfitSwing,
                _config.TrailActivateSwing, _config.TrailPctSwing,
                3),
            "1_week" => (
                _config.StopLossSwing, _config.TakeProfitSwing,
                _config.TrailActivateSwing, _config.TrailPctSwing,
                5),
            _ => (
                _config.StopLossDay, _config.TakeProfitDay,
                _config.TrailActivateDay, _config.TrailPctDay,
                1),
        };
    }
}

// ── DTOs ────────────────────────────────────────────────────────

/// <summary>Configuration for the simulated portfolio, mirroring live scoring_weight_overrides.</summary>
public class SimPortfolioConfig
{
    // Portfolio constraints
    public int MaxOpenPositions { get; init; } = 6;
    public int MaxPerSector { get; init; } = 3;
    public int MinConfidence { get; init; } = 40;
    public int MinBearishConfidence { get; init; } = 55;
    public double MinExpectedValue { get; init; } = -0.01; // allow slightly negative to test
    public double MinPositionDollars { get; init; } = 10;
    public int BlacklistDays { get; init; } = 30;

    // Daily loss limit
    public bool DailyLossLimitEnabled { get; init; } = true;
    public double DailyLossLimitPct { get; init; } = 0.03;

    // Position sizing (linear confidence scaling)
    public double MinFraction { get; init; } = 0.02;
    public double MaxFraction { get; init; } = 0.20;
    public double ConfidenceFloor { get; init; } = 35;
    public double ConfidenceCeiling { get; init; } = 85;
    public double EvBonus { get; init; } = 0.03;
    public double EvPenalty { get; init; } = 0.50;

    // Risk management — day trades
    public double StopLossDay { get; init; } = 0.02;
    public double TakeProfitDay { get; init; } = 0.03;
    public double TrailActivateDay { get; init; } = 0.015;
    public double TrailPctDay { get; init; } = 0.02;

    // Risk management — swing trades
    public double StopLossSwing { get; init; } = 0.03;
    public double TakeProfitSwing { get; init; } = 0.05;
    public double TrailActivateSwing { get; init; } = 0.03;
    public double TrailPctSwing { get; init; } = 0.025;

    /// <summary>
    /// Build config from backtest parameter overrides, falling back to defaults
    /// that match the current live scoring_weight_overrides values.
    /// </summary>
    public static SimPortfolioConfig FromOverrides(Dictionary<string, double>? overrides)
    {
        double Get(string key, double defaultValue)
            => overrides is not null && overrides.TryGetValue(key, out var v) ? v : defaultValue;

        return new SimPortfolioConfig
        {
            MaxOpenPositions = (int)Get("max_open_positions", 6),
            MaxPerSector = (int)Get("max_positions_per_sector", 3),
            MinConfidence = (int)Get("min_confidence_threshold", 40),
            MinBearishConfidence = (int)Get("min_bearish_confidence", 55),
            MinExpectedValue = Get("min_ev_threshold", 0.5) / 100.0,
            DailyLossLimitEnabled = Get("daily_loss_limit_enabled", 1) >= 1,
            DailyLossLimitPct = Get("daily_loss_limit_pct", 0.03),
            MinFraction = Get("sizing_min_fraction", 0.02),
            MaxFraction = Get("sizing_max_fraction", 0.20),
            ConfidenceFloor = Get("sizing_confidence_floor", 35),
            ConfidenceCeiling = Get("sizing_confidence_ceiling", 85),
            EvBonus = Get("sizing_ev_bonus", 0.03),
            EvPenalty = Get("sizing_ev_penalty", 0.50),
            StopLossDay = Get("risk_sl_day", 0.02),
            TakeProfitDay = Get("risk_tp_day", 0.03),
            TrailActivateDay = Get("risk_trail_activate_day", 0.015),
            TrailPctDay = Get("risk_trail_pct_day", 0.02),
            StopLossSwing = Get("risk_sl_swing", 0.03),
            TakeProfitSwing = Get("risk_tp_swing", 0.05),
            TrailActivateSwing = Get("risk_trail_activate_swing", 0.03),
            TrailPctSwing = Get("risk_trail_pct_swing", 0.025),
            BlacklistDays = (int)Get("repeat_loser_blacklist_days", 30),
        };
    }
}

public class SimPosition
{
    public string Ticker { get; init; } = "";
    public string Direction { get; init; } = "";
    public string Timeframe { get; init; } = "";
    public DateOnly EntryDate { get; init; }
    public double EntryPrice { get; init; }
    public double Quantity { get; init; }
    public double DollarsInvested { get; init; }
    public int Confidence { get; init; }
    public double ExpectedValue { get; init; }
    public double RiskRewardRatio { get; init; }
    public string? Sector { get; init; }
    public string? ScoreDebug { get; init; }

    // Meta-labeler advisory output (nullable when no model was loaded).
    public double? MetaProbability { get; init; }
    public int? MetaModelVersion { get; init; }

    // Risk params
    public double StopLossPct { get; init; }
    public double TakeProfitPct { get; init; }
    public double TrailActivatePct { get; init; }
    public double TrailPct { get; init; }
    public int MaxHoldDays { get; init; }

    // Tracking (mutable during simulation)
    public double HighWaterMark { get; set; }
    public double MaxFavorablePercent { get; set; }
    public double MaxAdversePercent { get; set; }

    // Exit data (set on close)
    public DateOnly? ExitDate { get; set; }
    public double? ExitPrice { get; set; }
    public string? ExitReason { get; set; }
    public double PnlDollars { get; set; }
    public double PnlPercent { get; set; }
}

public class EquitySnapshot
{
    public DateOnly Date { get; init; }
    public double Cash { get; init; }
    public double InvestedValue { get; init; }
    public double TotalEquity { get; init; }
    public int OpenPositionCount { get; init; }
}
