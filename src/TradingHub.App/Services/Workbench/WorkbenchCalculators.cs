namespace TradingHub.App.Services.Workbench;

public enum TradeDirection { Long, Short }

public enum RiskUnit { Percent, Amount }

/// <summary>Inputs of the position-size calculator. Prices in quote units; <see cref="PointValue"/> is P&amp;L per 1.0 price move per unit.</summary>
public sealed record PositionSizeInput(decimal Balance, decimal Risk, RiskUnit Unit, TradeDirection Side, decimal Entry, decimal Stop,
    decimal? Target = null, decimal FeePercentPerSide = 0, decimal PointValue = 1, decimal QuantityStep = 0);

public sealed record PositionSizeResult(bool IsValid, string? Error, decimal Quantity, decimal RawQuantity, decimal Budget,
    decimal Risk, decimal? Reward, decimal Notional, decimal? Leverage, decimal StopDistance, decimal StopDistancePercent,
    decimal? RewardRiskRatio, decimal? BreakevenWinRatePercent, IReadOnlyList<string> Warnings);

public sealed record LiquidationInput(TradeDirection Side, decimal Entry, decimal Leverage, decimal MaintenanceMarginPercent);

public sealed record LiquidationResult(bool IsValid, string? Error, decimal Price, decimal DistancePercent, decimal InitialMarginPercent, string? Warning);

public sealed record FundingCostInput(decimal Notional, decimal RatePercent, decimal IntervalHours, decimal HoldHours);

public sealed record FundingCostResult(bool IsValid, string? Error, int Settlements, decimal Cost, decimal PerDay, decimal AnnualizedPercent, bool LongsPay);

/// <summary>A New York trading session (local hours, wrapping past midnight when From &gt; To).</summary>
public sealed record TradingSession(string Name, int From, int To)
{
    public bool Contains(double hour) => From < To ? hour >= From && hour < To : hour >= From || hour < To;
}

public sealed record SessionClockRow(TradingSession Session, bool Live, TimeSpan Until, TimeSpan Left, DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// Self-contained trading calculators. They only compute numbers from the values entered: they never read an account,
/// never create or send orders and never change any setting.
/// </summary>
public static class WorkbenchCalculators
{
    /// <summary>
    /// Risk-based position size with the same linear rules as the chart's long/short position drawing:
    /// loss per unit = |entry − stop| × point value + round-trip fees per unit, and the quantity is rounded DOWN to the
    /// quantity step so the risk never exceeds the budget. Fees are a percentage of notional charged on entry and on the
    /// exit (stop for the risk, target for the reward). Excludes slippage, funding and FX.
    /// </summary>
    public static PositionSizeResult PositionSize(PositionSizeInput input)
    {
        var warnings = new List<string>();
        PositionSizeResult Invalid(string error) => new(false, error, 0, 0, 0, 0, null, 0, null, 0, 0, null, null, warnings);
        if (input.Entry <= 0 || input.Stop <= 0) return Invalid("Enter the entry and stop prices.");
        bool isLong = input.Side == TradeDirection.Long;
        if (isLong ? input.Stop >= input.Entry : input.Stop <= input.Entry)
            return Invalid(isLong ? "For a long, the stop must be below the entry." : "For a short, the stop must be above the entry.");
        if (input.PointValue <= 0) return Invalid("The point value must be greater than zero.");
        if (input.QuantityStep < 0) return Invalid("The quantity step cannot be negative.");
        if (input.FeePercentPerSide < 0) return Invalid("Fees cannot be negative.");
        if (input.Risk <= 0) return Invalid("Enter the risk.");
        if (input.Unit == RiskUnit.Percent && (input.Risk > 100 || input.Balance <= 0))
            return Invalid(input.Balance <= 0 ? "Enter the account balance." : "Risk must be greater than 0% and at most 100%.");
        try
        {
            decimal fee = input.FeePercentPerSide / 100;
            decimal distance = Math.Abs(input.Entry - input.Stop);
            decimal lossPerUnit = distance * input.PointValue + (input.Entry + input.Stop) * input.PointValue * fee;
            decimal budget = input.Unit == RiskUnit.Percent ? input.Balance * input.Risk / 100 : input.Risk;
            decimal raw = budget / lossPerUnit;
            decimal quantity = input.QuantityStep > 0 ? decimal.Floor(raw / input.QuantityStep) * input.QuantityStep : raw;
            if (input.QuantityStep > 0 && quantity == 0) warnings.Add("The risk budget is below one quantity step.");
            decimal notional = quantity * input.Entry * input.PointValue;
            decimal? leverage = input.Balance > 0 ? notional / input.Balance : null;
            if (leverage > 20) warnings.Add("The required leverage is above 20×: the liquidation price may be close to the stop. Check it with the liquidation calculator.");
            decimal? reward = null, ratio = null, breakeven = null;
            if (input.Target is { } target and > 0)
            {
                if (isLong ? target <= input.Entry : target >= input.Entry)
                    warnings.Add(isLong ? "For a long, the target must be above the entry." : "For a short, the target must be below the entry.");
                else
                {
                    decimal gainPerUnit = Math.Abs(target - input.Entry) * input.PointValue - (input.Entry + target) * input.PointValue * fee;
                    reward = quantity * gainPerUnit;
                    if (gainPerUnit > 0)
                    {
                        ratio = gainPerUnit / lossPerUnit;
                        breakeven = 100 / (1 + ratio.Value);
                    }
                    else warnings.Add("Fees exceed the move to the target.");
                }
            }
            return new(true, null, quantity, raw, budget, quantity * lossPerUnit, reward, notional, leverage, distance,
                distance / input.Entry * 100, ratio, breakeven, warnings);
        }
        catch (OverflowException) { return Invalid("The values are too large; reduce them."); }
    }

    /// <summary>
    /// Approximate isolated-margin liquidation price of a linear (USDT-margined) perpetual, ignoring fees, funding and
    /// unrealized P&amp;L of other positions. Liquidation happens when margin + P&amp;L equals the maintenance margin:
    /// long P = E × (1 − 1/L) / (1 − m); short P = E × (1 + 1/L) / (1 + m), with L the leverage and m the maintenance rate.
    /// Exchanges use tiered maintenance rates and the mark price, so the real level differs.
    /// </summary>
    public static LiquidationResult LiquidationPrice(LiquidationInput input)
    {
        LiquidationResult Invalid(string error) => new(false, error, 0, 0, 0, null);
        if (input.Entry <= 0) return Invalid("Enter the entry price.");
        if (input.Leverage < 1) return Invalid("Leverage must be at least 1×.");
        if (input.MaintenanceMarginPercent < 0 || input.MaintenanceMarginPercent >= 100) return Invalid("The maintenance margin rate must be between 0% and 100%.");
        decimal m = input.MaintenanceMarginPercent / 100, inverse = 1 / input.Leverage;
        decimal price = input.Side == TradeDirection.Long ? input.Entry * (1 - inverse) / (1 - m) : input.Entry * (1 + inverse) / (1 + m);
        string? warning = m >= inverse ? "The maintenance margin is at least the initial margin: the position would be liquidated immediately." : null;
        return new(true, null, price, (price / input.Entry - 1) * 100, inverse * 100, warning);
    }

    /// <summary>
    /// Funding paid or received while holding: settlements = ⌊hold / interval⌋, cost = notional × rate × settlements.
    /// A positive rate means longs pay shorts. Annualized = rate × (24 / interval) × 365.
    /// </summary>
    public static FundingCostResult FundingCost(FundingCostInput input)
    {
        FundingCostResult Invalid(string error) => new(false, error, 0, 0, 0, 0, true);
        if (input.Notional <= 0) return Invalid("Enter the position notional.");
        if (input.IntervalHours <= 0) return Invalid("The funding interval must be greater than zero.");
        if (input.HoldHours < 0) return Invalid("The holding time cannot be negative.");
        decimal rate = input.RatePercent / 100;
        int settlements = (int)Math.Min(int.MaxValue, decimal.Floor(input.HoldHours / input.IntervalHours));
        decimal perPeriod = input.Notional * rate;
        decimal perDay = perPeriod * 24 / input.IntervalHours;
        return new(true, null, settlements, perPeriod * settlements, perDay, rate * 24 / input.IntervalHours * 365 * 100, rate >= 0);
    }

    /// <summary>Sessions in New York time: Asia 18–02, London 02–08, New York morning 08–12, New York afternoon 12–16.</summary>
    public static readonly IReadOnlyList<TradingSession> Sessions = new TradingSession[]
    {
        new("Asia", 18, 2), new("London", 2, 8), new("New York morning", 8, 12), new("New York afternoon", 12, 16),
    };

    private static readonly TimeZoneInfo? NewYork = FindNewYork();

    private static TimeZoneInfo? FindNewYork()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return null;
    }

    /// <summary>New York wall-clock time (UTC−4 when the time-zone database is unavailable).</summary>
    public static DateTime NewYorkTime(DateTime utc) => NewYork != null
        ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), NewYork)
        : utc.AddHours(-4);

    /// <summary>Each session's state at <paramref name="utcNow"/>: live with time left, or the time until it starts, with its next UTC span.</summary>
    public static IReadOnlyList<SessionClockRow> SessionClock(DateTime utcNow)
    {
        var ny = NewYorkTime(utcNow);
        double hour = ny.Hour + ny.Minute / 60.0 + ny.Second / 3600.0;
        var rows = new List<SessionClockRow>();
        foreach (var s in Sessions)
        {
            bool live = s.Contains(hour);
            var until = TimeSpan.FromHours(((s.From - hour) % 24 + 24) % 24);
            var left = TimeSpan.FromHours(((s.To - hour) % 24 + 24) % 24);
            var start = live ? utcNow - TimeSpan.FromHours(((hour - s.From) % 24 + 24) % 24) : utcNow + until;
            var length = TimeSpan.FromHours(((s.To - s.From) % 24 + 24) % 24);
            rows.Add(new SessionClockRow(s, live, live ? TimeSpan.Zero : until, live ? left : TimeSpan.Zero, start, start + length));
        }
        return rows;
    }

    /// <summary>The next settlement of the common 8-hour funding schedule (00:00, 08:00 and 16:00 UTC).</summary>
    public static DateTime NextEightHourFunding(DateTime utcNow)
    {
        var day = utcNow.Date;
        int next = (utcNow.Hour / 8 + 1) * 8;
        return DateTime.SpecifyKind(day.AddHours(next), DateTimeKind.Utc);
    }
}
