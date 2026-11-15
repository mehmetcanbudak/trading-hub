// Behavior adapted from the KVP capability reference (commit df8844a); see THIRD_PARTY_NOTICES.md.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Feeds.Derivatives;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Third-party estimate of Hyperliquid BTC liquidation levels (HyperTracker position snapshots binned by a public levels
/// service), drawn to the right of the last bar: longs below the price, shorts above, stronger where more notional sits.
/// A bin disappears once the price traded through it after the snapshot; snapshots older than three hours are not drawn.
/// Off by default: nothing is fetched unless the user enables third-party liquidation levels in Settings.
/// </summary>
[DisplayName("Liquidation levels (third-party estimate)")]
[Category(IndicatorCategories.Derivatives)]
[Description("Where open Hyperliquid BTC positions would be liquidated, from a third-party service's published snapshot (an estimate, not exchange data): longs below, shorts above, to the right of the last bar. Requires opting in under Settings → Third-party data.")]
public sealed class LiquidationLevels : Indicator
{
    private static readonly TimeSpan TimerInterval = TimeSpan.FromSeconds(5);
    private readonly Action _tick;
    private readonly RenderFont _font = new("Arial", 8);
    private bool _loading;
    private LiquidationLevelsResult _result = new(null, null);

    public LiquidationLevels() : base(true)
    {
        DataSeries[0] = new ValueDataSeries("LIQLV", "Levels") { VisualType = VisualMode.Hide, ShowZeroValue = false };
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        _tick = Refresh;
    }

    [Display(Name = "Band width (px)", GroupName = "Visualization", Order = 10)]
    [Range(30, 600)]
    public int BandWidth { get; set; } = 160;

    [Display(Name = "Group by (price, 0 = the service's bins)", GroupName = "Visualization", Order = 20)]
    [Range(0, 1_000_000)]
    public decimal Group { get; set; }

    [Display(Name = "Show totals from the price", GroupName = "Visualization", Order = 30)]
    public bool Totals { get; set; } = true;

    [Display(Name = "Long liquidations color", GroupName = "Colors", Order = 40)]
    public Color LongColor { get; set; } = Color.FromArgb(255, 240, 82, 90);

    [Display(Name = "Short liquidations color", GroupName = "Colors", Order = 41)]
    public Color ShortColor { get; set; } = Color.FromArgb(255, 38, 194, 129);

    /// <summary>The service; defaults to the application's opt-in instance (null while disabled).</summary>
    [Browsable(false)]
    public LiquidationLevelsService? Service { get; set; }

    [Browsable(false)]
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private LiquidationLevelsService? Levels => Service ?? LiquidationLevelsService.Shared;

    private bool Supported => LiquidationLevelsService.Supports(InstrumentInfo?.BaseAsset ?? "", InstrumentInfo?.Instrument ?? "");

    protected override void OnInitialize() => SubscribeToTimer(TimerInterval, _tick);

    protected override void OnDispose() => UnsubscribeFromTimer(TimerInterval, _tick);

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == CurrentBar - 1 && _result.Snapshot == null) Refresh();
    }

    private void Refresh()
    {
        if (_loading || !Supported || Levels is not { } service) return;
        _result = service.Latest;
        if (Clock() - (_result.Snapshot?.FetchedUtc ?? DateTime.MinValue) < LiquidationLevelsService.MinimumRefresh) return;
        _loading = true;
        _ = Task.Run(async () =>
        {
            LiquidationLevelsResult result;
            try { result = await service.RefreshAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (ObjectDisposedException) { result = new(null, "Third-party liquidation levels were turned off."); }
            DoActionInGuiThread(() =>
            {
                _loading = false;
                _result = result;
                RedrawChart();
            });
        });
    }

    /// <summary>The bins still standing (traded-through bins removed), grouped by <see cref="Group"/>; null without a fresh snapshot.</summary>
    internal IReadOnlyList<LiquidationLevelBin>? Standing(out LiquidationLevelsSnapshot? snapshot)
    {
        snapshot = _result.Snapshot;
        if (snapshot == null || CurrentBar <= 0 || Clock() - snapshot.AsOfUtc > LiquidationLevelsService.MaximumSnapshotAge) return null;
        decimal low = decimal.MaxValue, high = decimal.MinValue;
        for (int b = CurrentBar - 1; b >= 0; b--)
        {
            var c = GetCandle(b);
            var end = b + 1 < CurrentBar ? GetCandle(b + 1).Time : Clock();
            if (end <= snapshot.AsOfUtc) break;
            low = Math.Min(low, c.Low);
            high = Math.Max(high, c.High);
        }
        var remaining = LiquidationLevelsService.Remaining(snapshot, low == decimal.MaxValue ? snapshot.Mark : low, high == decimal.MinValue ? snapshot.Mark : high);
        decimal step = Group > 0 ? Math.Max(snapshot.Step, Group) : snapshot.Step;
        return remaining.GroupBy(b => Math.Floor(b.Price / step) * step)
            .Select(g => new LiquidationLevelBin(g.Key, g.Sum(x => x.LongNotional), g.Sum(x => x.ShortNotional), g.Max(x => x.LargestLong), g.Max(x => x.LargestShort)))
            .OrderBy(b => b.Price).ToArray();
    }

    /// <summary>The three largest standing long and short bins, as alertable levels keyed by their bin price.</summary>
    public override IEnumerable<IndicatorLevel> GetAlertLevels()
    {
        if (Standing(out var snapshot) is not { } bins || snapshot == null) yield break;
        decimal step = Group > 0 ? Math.Max(snapshot.Step, Group) : snapshot.Step;
        foreach (var b in bins.Where(b => b.LongNotional > 0).OrderByDescending(b => b.LongNotional).Take(3))
            yield return new IndicatorLevel("liq-long:" + b.Price.ToString(CultureInfo.InvariantCulture), "Long liquidation level (third-party estimate)", b.Price + step / 2, CurrentBar - 1);
        foreach (var b in bins.Where(b => b.ShortNotional > 0).OrderByDescending(b => b.ShortNotional).Take(3))
            yield return new IndicatorLevel("liq-short:" + b.Price.ToString(CultureInfo.InvariantCulture), "Short liquidation level (third-party estimate)", b.Price + step / 2, CurrentBar - 1);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;
        var region = ChartInfo.PriceChartContainer.Region;
        void Note(string text) => FootprintStudyData.Legend(context, region, 0, text, FlowFormat.Dim);
        if (!Supported) { Note("Liquidation levels: the third-party service publishes BTC only."); return; }
        if (Levels == null) { Note("Third-party liquidation levels are off. Enable them in Settings → Third-party data."); return; }
        var bins = Standing(out var snapshot);
        if (snapshot == null) { Note(_loading ? "Loading third-party liquidation levels…" : _result.Message ?? "Liquidation levels: no snapshot yet."); return; }
        if (bins == null) { Note("Liquidation levels: the latest snapshot is older than three hours and is not drawn."); return; }
        decimal step = Group > 0 ? Math.Max(snapshot.Step, Group) : snapshot.Step;
        decimal top = ChartInfo.PriceChartContainer.High, bottom = ChartInfo.PriceChartContainer.Low;
        var visible = bins.Where(b => b.Price + step >= bottom && b.Price <= top).ToList();
        decimal max = visible.Count > 0 ? visible.Max(b => Math.Max(b.LongNotional, b.ShortNotional)) : 0;
        int x0 = ChartInfo.GetXByBar(CurrentBar - 1) + Math.Max(4, ChartInfo.PriceChartContainer.BarsWidth) + 6;
        int w = Math.Clamp(BandWidth, 30, 600);
        if (x0 + w > region.Right) x0 = Math.Max(region.Left, region.Right - w);
        foreach (var b in visible)
        {
            if (max <= 0) break;
            int ya = ChartInfo.GetYByPrice(b.Price + step, false), yb = ChartInfo.GetYByPrice(b.Price, false);
            int h = Math.Max(1, Math.Abs(yb - ya) - (Math.Abs(yb - ya) > 3 ? 1 : 0)), y = Math.Min(ya, yb);
            foreach (var (notional, largest, color) in new[] { (b.LongNotional, b.LargestLong, LongColor), (b.ShortNotional, b.LargestShort, ShortColor) })
            {
                if (notional <= 0) continue;
                double t = Math.Sqrt((double)(notional / max));
                int length = Math.Max(1, (int)(w * t));
                context.FillRectangle(FlowFormat.Alpha(color, (int)(60 + 180 * t)), new Rectangle(x0, y, length, h));
                // A diamond where a single position holds half or more of the bin.
                if (largest >= notional / 2 && notional >= max * 0.15m)
                    context.FillPolygon(Color.FromArgb(240, 250, 220, 120), new[] { new Point(x0 + length + 4, y + h / 2 - 3), new Point(x0 + length + 7, y + h / 2), new Point(x0 + length + 4, y + h / 2 + 3), new Point(x0 + length + 1, y + h / 2) });
                if (h >= 9 && t > 0.3) context.DrawString(FlowFormat.Compact(notional), _font, Color.FromArgb(230, 240, 240, 245), x0 + 2, y + h / 2 - 6);
            }
        }
        decimal price = GetCandle(CurrentBar - 1).Close;
        if (Totals)
        {
            string longs = FlowFormat.Compact(visible.Where(b => b.Price < price).Sum(b => b.LongNotional)), shorts = FlowFormat.Compact(visible.Where(b => b.Price > price).Sum(b => b.ShortNotional));
            string totals = UiText.Tr($"Longs below {longs} · shorts above {shorts}");
            context.DrawString(totals, _font, FlowFormat.Dim, Math.Min(x0, region.Right - context.MeasureString(totals, _font).Width - 4), region.Top + 4);
        }
        string label = UiText.Tr(LiquidationLevelsService.ProvenanceLabel), minutes = ((int)(Clock() - snapshot.AsOfUtc).TotalMinutes).ToString(CultureInfo.InvariantCulture);
        string note = UiText.Tr($"{label} · positions {minutes} min old · {snapshot.Attribution}");
        context.DrawString(note, _font, FlowFormat.Dim, Math.Min(x0, region.Right - context.MeasureString(note, _font).Width - 4), region.Bottom - 14);
    }
}
