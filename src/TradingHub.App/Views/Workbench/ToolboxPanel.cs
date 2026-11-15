using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services.Workbench;

namespace TradingHub.App.Views.Workbench;

/// <summary>
/// Self-contained calculators: risk-based position size (with the chart position drawing's sizing rules), approximate
/// isolated liquidation price, funding cost, and a New York session clock with the next 8-hour funding settlement.
/// Calculators only compute numbers; they never read an account or place orders.
/// </summary>
internal sealed class ToolboxPanel : WorkbenchPanel
{
    private readonly Ui.Segmented<TradeDirection> _sizeSide, _liqSide;
    private readonly Ui.Segmented<bool> _riskUnit;
    private readonly NumericUpDown _balance, _risk, _fee, _pointValue, _step, _leverage, _maintenance, _fundNotional, _fundRate, _fundEvery, _fundHold;
    private readonly NumericUpDown _entry = Price("ToolEntry"), _stop = Price("ToolStop"), _target = Price("ToolTarget"), _liqEntry = Price("ToolLiquidationEntry");
    private readonly StackPanel _sizeOut = new() { Name = "ToolSizeResult", Spacing = 4 };
    private readonly StackPanel _liqOut = new() { Name = "ToolLiquidationResult", Spacing = 4 };
    private readonly StackPanel _fundOut = new() { Name = "ToolFundingResult", Spacing = 4 };
    private readonly StackPanel _clock = new() { Name = "ToolSessionClock", Spacing = 6 };
    private readonly TextBlock _instrumentNote = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private DateTime _lastClock;
    private TradeDirection _sizeDirection = TradeDirection.Long, _liquidationDirection = TradeDirection.Long;

    public ToolboxPanel(WorkbenchContext context) : base(context)
    {
        var s = context.Settings;
        _sizeSide = Side("ToolSizeSide", d => { _sizeDirection = d; Recalculate(); });
        _liqSide = Side("ToolLiquidationSide", d => { _liquidationDirection = d; Recalculate(); });
        _riskUnit = new Ui.Segmented<bool>(new (bool, string, string?)[] { (false, "% of balance", null), (true, "Amount", null) }) { Name = "ToolRiskUnit" };
        _riskUnit.Select(s.ToolRiskIsAmount);
        _riskUnit.SelectionChanged += v => { s.ToolRiskIsAmount = v; Save(); };
        _balance = Number("ToolBalance", s.ToolBalance, 0, 1_000_000_000, 100, "0.##", v => { s.ToolBalance = v; Save(); });
        _risk = Number("ToolRisk", s.ToolRisk, 0, 1_000_000_000, 0.25m, "0.####", v => { s.ToolRisk = v; Save(); });
        _fee = Number("ToolFee", s.ToolFeePercent, 0, 5, 0.01m, "0.####", v => { s.ToolFeePercent = v; Save(); });
        _pointValue = Number("ToolPointValue", 1, 0.000001m, 1_000_000, 1, "0.######", _ => Recalculate());
        _step = Number("ToolQuantityStep", 0, 0, 1_000_000, 0.001m, "0.########", _ => Recalculate());
        _leverage = Number("ToolLeverage", s.ToolLeverage, 1, 500, 1, "0.##", v => { s.ToolLeverage = v; Save(); });
        _maintenance = Number("ToolMaintenance", s.ToolMaintenancePercent, 0, 50, 0.1m, "0.###", v => { s.ToolMaintenancePercent = v; Save(); });
        _fundNotional = Number("ToolFundingNotional", s.ToolFundingNotional, 0, 1_000_000_000, 1000, "0.##", v => { s.ToolFundingNotional = v; Save(); });
        _fundRate = Number("ToolFundingRate", s.ToolFundingRatePercent, -5, 5, 0.001m, "0.#####", v => { s.ToolFundingRatePercent = v; Save(); });
        _fundEvery = Number("ToolFundingInterval", s.ToolFundingIntervalHours, 0.25m, 168, 1, "0.##", v => { s.ToolFundingIntervalHours = v; Save(); });
        _fundHold = Number("ToolFundingHold", s.ToolFundingHoldHours, 0, 100_000, 8, "0.##", v => { s.ToolFundingHoldHours = v; Save(); });
        foreach (var box in new[] { _entry, _stop, _target, _liqEntry }) box.ValueChanged += (_, _) => Recalculate();

        var size = Card("Position size", "The quantity whose loss at the stop equals the risk you choose, with the same linear rules as the chart's long/short position drawing (quantity rounded down to the step).",
            Ui.Field("Account balance", _balance), Ui.Field("Risk", Ui.H(8, _risk, _riskUnit)), Ui.Field("Side", _sizeSide),
            Ui.Row(8, Ui.Field("Entry price", _entry), Ui.Field("Stop price", _stop), Ui.Field("Target (optional)", _target)),
            Ui.Row(8, Ui.Field("Fee per side (%)", _fee), Ui.Field("Point value", _pointValue), Ui.Field("Quantity step", _step)),
            _instrumentNote, _sizeOut);
        var liquidation = Card("Liquidation price (approximate)", "Isolated margin on a linear perpetual: long ≈ entry × (1 − 1/leverage) / (1 − maintenance), short ≈ entry × (1 + 1/leverage) / (1 + maintenance). Ignores fees, funding and tiered margin.",
            Ui.Field("Side", _liqSide), Ui.Field("Entry price", _liqEntry), Ui.Row(8, Ui.Field("Leverage (×)", _leverage), Ui.Field("Maintenance margin (%)", _maintenance)), _liqOut);
        var funding = Card("Funding cost", "Funding paid (or received) while holding: notional × rate × settlements. Enter the current or expected rate per settlement.",
            Ui.Field("Position notional", _fundNotional), Ui.Row(8, Ui.Field("Rate per settlement (%)", _fundRate), Ui.Field("Interval (hours)", _fundEvery)),
            Ui.Field("Holding time (hours)", _fundHold), _fundOut);
        var clock = Card("Session clock", "Sessions in New York time shown with your local times; the next settlement of the common 8-hour funding schedule.", _clock);

        var grid = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 420 };
        foreach (var c in new[] { size, liquidation, funding, clock }) grid.Children.Add(c);
        Content = new ScrollViewer { Content = Ui.V(10, Note("Calculations run locally from the values entered; nothing is sent and no order is placed. Inputs are remembered."), grid) };
        foreach (var box in new[] { _balance, _risk, _fee, _leverage, _maintenance, _fundNotional, _fundRate, _fundEvery, _fundHold })
            box.ValueChanged += (_, _) => Recalculate();
        _riskUnit.SelectionChanged += _ => Recalculate();
        Recalculate();
        UpdateClock(context.Clock());
    }

    public override string Key => "toolbox";
    public override string Title => "Toolbox";
    public override MaterialIconKind Icon => MaterialIconKind.Calculator;

    private void Save() => Context.Save();

    private static NumericUpDown Price(string name) => new()
    {
        Name = name, Minimum = 0, Maximum = 1_000_000_000, Increment = 1, FormatString = "0.########", Value = null, MinWidth = 110,
        HorizontalAlignment = HorizontalAlignment.Stretch, ShowButtonSpinner = false,
    };

    private static Ui.Segmented<TradeDirection> Side(string name, Action<TradeDirection> changed)
    {
        var side = new Ui.Segmented<TradeDirection>(new (TradeDirection, string, string?)[] { (TradeDirection.Long, "Long", null), (TradeDirection.Short, "Short", null) }) { Name = name };
        side.Select(TradeDirection.Long);
        side.SelectionChanged += changed;
        return side;
    }

    private static Border Card(string title, string description, params Control[] body)
    {
        var stack = Ui.V(10, Ui.H2(title), new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Classes = { "faint", "small" } });
        foreach (var c in body) stack.Children.Add(c);
        return new Border { Child = stack, Padding = new Thickness(14), Margin = new Thickness(0, 0, 10, 10) }.Classes("card");
    }

    private static Control Result(string label, string value, bool big = false, IBrush? color = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = label, Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center });
        var v = new TextBlock { Text = value, FontWeight = FontWeight.SemiBold, FontSize = big ? 16 : 12.5, VerticalAlignment = VerticalAlignment.Center };
        if (color != null) v.Foreground = color;
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        return grid;
    }

    private static Control Warning(string text) => new TextBlock { Text = text, Foreground = Ui.Warn, TextWrapping = TextWrapping.Wrap, Classes = { "small" } };

    /// <summary>Prefills the instrument's point value and quantity step and the last price as entry.</summary>
    protected override void OnSelectionChanged()
    {
        if (Selection is not { } s) return;
        _pointValue.Value = s.Instrument.PointValue > 0 ? s.Instrument.PointValue : 1;
        _step.Value = s.Instrument.VolumeStep > 0 ? s.Instrument.VolumeStep : 0;
        _instrumentNote.Text = $"Point value and quantity step from {s.Instrument.Symbol}; change them for another market.";
        decimal last = s.Data.LastPrice;
        if (last > 0)
        {
            if (_entry.Value == null) _entry.Value = last;
            if (_liqEntry.Value == null) _liqEntry.Value = last;
        }
        Recalculate();
    }

    public override void Tick(DateTime now)
    {
        if (now - _lastClock >= TimeSpan.FromSeconds(1)) UpdateClock(now);
    }

    private void Recalculate()
    {
        var s = Context.Settings;
        SizeResult(new PositionSizeInput(_balance.Value ?? 0, _risk.Value ?? 0, s.ToolRiskIsAmount ? RiskUnit.Amount : RiskUnit.Percent, _sizeDirection,
            _entry.Value ?? 0, _stop.Value ?? 0, _target.Value, _fee.Value ?? 0, _pointValue.Value ?? 1, _step.Value ?? 0));
        var liq = WorkbenchCalculators.LiquidationPrice(new LiquidationInput(_liquidationDirection, _liqEntry.Value ?? 0, _leverage.Value ?? 1, _maintenance.Value ?? 0));
        _liqOut.Children.Clear();
        if (!liq.IsValid) _liqOut.Children.Add(Warning(liq.Error!));
        else
        {
            _liqOut.Children.Add(Result("Liquidation at about", WorkbenchFormat.Price(liq.Price), true, Ui.Down));
            _liqOut.Children.Add(Result("Distance from entry", WorkbenchFormat.Percent(liq.DistancePercent)));
            _liqOut.Children.Add(Result("Initial margin", WorkbenchFormat.Percent(liq.InitialMarginPercent).TrimStart('+')));
            if (liq.Warning != null) _liqOut.Children.Add(Warning(liq.Warning));
            _liqOut.Children.Add(Note("Approximate: exchanges use tiered maintenance rates and the mark price; trust the exchange's displayed liquidation price."));
        }
        var fund = WorkbenchCalculators.FundingCost(new FundingCostInput(_fundNotional.Value ?? 0, _fundRate.Value ?? 0, _fundEvery.Value ?? 8, _fundHold.Value ?? 0));
        _fundOut.Children.Clear();
        if (!fund.IsValid) _fundOut.Children.Add(Warning(fund.Error!));
        else
        {
            _fundOut.Children.Add(Result("Settlements", fund.Settlements.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            _fundOut.Children.Add(Result(fund.LongsPay ? "Longs pay / shorts receive" : "Longs receive / shorts pay", WorkbenchFormat.Number(Math.Abs(fund.Cost), 2), true));
            _fundOut.Children.Add(Result("Per day", WorkbenchFormat.Number(Math.Abs(fund.PerDay), 2)));
            _fundOut.Children.Add(Result("Annualized", WorkbenchFormat.Percent(fund.AnnualizedPercent)));
        }
    }

    private void SizeResult(PositionSizeInput input)
    {
        _sizeOut.Children.Clear();
        var r = WorkbenchCalculators.PositionSize(input);
        if (!r.IsValid)
        {
            _sizeOut.Children.Add(Warning(r.Error!));
            return;
        }
        _sizeOut.Children.Add(Result("Quantity", WorkbenchFormat.Number(r.Quantity, 8), true, Ui.Accent));
        _sizeOut.Children.Add(Result("Notional", WorkbenchFormat.Number(r.Notional, 2)));
        _sizeOut.Children.Add(Result("Stop distance", $"{WorkbenchFormat.Price(r.StopDistance)} ({r.StopDistancePercent:0.##}%)"));
        _sizeOut.Children.Add(Result("Loss at the stop (with fees)", WorkbenchFormat.Number(r.Risk, 2), color: Ui.Down));
        _sizeOut.Children.Add(Result("Leverage needed", r.Leverage is { } l ? $"{l:0.##}×" : WorkbenchFormat.Missing));
        if (r.Reward is { } reward) _sizeOut.Children.Add(Result("Profit at the target (with fees)", WorkbenchFormat.Number(reward, 2), color: Ui.Up));
        if (r.RewardRiskRatio is { } rr) _sizeOut.Children.Add(Result("Reward/risk", $"{rr:0.##} R"));
        if (r.BreakevenWinRatePercent is { } be) _sizeOut.Children.Add(Result("Break-even win rate", $"{be:0.#}%"));
        foreach (var w in r.Warnings) _sizeOut.Children.Add(Warning(w));
        _sizeOut.Children.Add(Note("Linear estimate; excludes slippage, funding and FX. No orders."));
    }

    private void UpdateClock(DateTime now)
    {
        _lastClock = now;
        _clock.Children.Clear();
        var ny = WorkbenchCalculators.NewYorkTime(now);
        _clock.Children.Add(Result("New York", ny.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), true));
        _clock.Children.Add(Result("Local", now.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        foreach (var row in WorkbenchCalculators.SessionClock(now))
        {
            string span = $"{row.StartUtc.ToLocalTime():HH:mm}–{row.EndUtc.ToLocalTime():HH:mm}";
            string state = row.Live ? $"live, {WorkbenchFormat.Countdown(row.Left)} left" : $"starts in {WorkbenchFormat.Countdown(row.Until)}";
            _clock.Children.Add(Result(row.Session.Name, $"{span} · {state}", color: row.Live ? Ui.Accent : null));
        }
        var next = WorkbenchCalculators.NextEightHourFunding(now);
        _clock.Children.Add(Note($"Next 8-hour funding settlement at {next.ToLocalTime():HH:mm} local (in {WorkbenchFormat.Countdown(next - now)}). Some venues settle every 1 or 4 hours."));
    }
}
