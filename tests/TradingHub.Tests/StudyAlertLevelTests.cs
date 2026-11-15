using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>The horizontal levels built-in studies draw by hand are declared for alerts with stable, drawable identities.</summary>
public class StudyAlertLevelTests
{
    private static readonly DateTime Start = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    // A mother day, two inside days and three quieter days: every session revisits its range in a four-bar zigzag
    private static readonly (int Low, int High)[] Days = { (100, 200), (120, 180), (130, 170), (140, 160), (142, 158), (145, 155) };
    private const int BarsPerDay = 30;

    private static ChartModel Market()
    {
        var data = new InstrumentData(new Instrument { Symbol = "STUDY-LEVELS-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 }, new SimulatedFeed());
        long id = 1;
        for (int d = 0; d < Days.Length; d++)
            for (int m = 0; m < BarsPerDay; m++)
            {
                var (low, high) = Days[d];
                int phase = m % 8, step = (high - low) / 4;
                decimal price = low + step * (phase <= 4 ? phase : 8 - phase);
                // the mother day's peak carries its POC: no later session reaches it, so it stays naked
                decimal volume = d == 0 && m == 20 ? 1000 : 10;
                var time = Start.AddDays(d).AddMinutes(m);
                data.Trades.Add(new Trade(id++, time, price, volume / 2, TradeSide.Buy));
                data.Trades.Add(new Trade(id++, time.AddSeconds(30), price, volume / 2, TradeSide.Sell));
            }
        return new ChartModel(data, ChartPeriod.M1, new ChartSettings());
    }

    /// <summary>The PO3 sweep-and-return fixture of the causality tests, extended so its pattern stays active.</summary>
    private static ChartModel HigherTimeframeMarket()
    {
        (long Open, long High, long Low, long Close)[] bars = { (10, 12, 9, 11), (11, 12, 9, 10), (10, 11, 8, 10), (10, 11, 8, 10), (10, 12, 9, 11), (11, 12, 9, 11), (11, 13, 10, 12) };
        var data = new InstrumentData(new Instrument { Symbol = "STUDY-LEVELS-HTF", FeedId = "sim", TickSize = 1, VolumeStep = 1 }, new SimulatedFeed());
        long id = 1;
        for (int i = 0; i < bars.Length; i++)
        {
            var time = Start.AddMinutes(i);
            data.Trades.Add(new Trade(id++, time, bars[i].Close, 10, TradeSide.Buy));
            data.Trades.Add(new Trade(id++, time.AddSeconds(1), bars[i].Close, 10, TradeSide.Sell));
        }
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        for (int i = 0; i < bars.Length; i++)
        {
            model.Series[i].OpenTicks = bars[i].Open; model.Series[i].HighTicks = bars[i].High;
            model.Series[i].LowTicks = bars[i].Low; model.Series[i].CloseTicks = bars[i].Close;
        }
        return model;
    }

    public static TheoryData<string> Studies => new()
    {
        nameof(NakedPoc), nameof(CompositeVolumeProfile), nameof(VolumeProfile), nameof(PriorClosingVwap), nameof(VwapSuite),
        nameof(SessionBackgrounds), nameof(MarketStructure), nameof(HtfStructure), nameof(DoubleInsideDay), nameof(SessionVolumeProfile), nameof(RefillZones),
    };

    private static (ChartModel Model, Indicator Study) Create(string study) => study switch
    {
        nameof(NakedPoc) => (Market(), new NakedPoc()),
        nameof(CompositeVolumeProfile) => (Market(), new CompositeVolumeProfile { ShowBuyerSellerPoc = true, ShowOuterValueArea = true }),
        nameof(VolumeProfile) => (Market(), new VolumeProfile()),
        nameof(PriorClosingVwap) => (Market(), new PriorClosingVwap()),
        nameof(VwapSuite) => (Market(), new VwapSuite { UsePriorClosing = true }),
        nameof(SessionBackgrounds) => (Market(), new SessionBackgrounds
        {
            TimeZoneId = "UTC", FirstStart = TimeSpan.Zero, FirstEnd = TimeSpan.FromMinutes(15),
            SecondStart = TimeSpan.FromMinutes(15), SecondEnd = TimeSpan.FromMinutes(30), ThirdStart = TimeSpan.FromMinutes(5), ThirdEnd = TimeSpan.FromMinutes(25),
        }),
        nameof(MarketStructure) => (Market(), new MarketStructure { SwingRadius = 2, ShowEndedZones = true }),
        nameof(HtfStructure) => (HigherTimeframeMarket(), new HtfStructure { UseMappedPeriods = false, FirstPeriod = StudyTimeFrame.Minutes2, SecondPeriod = StudyTimeFrame.Minutes5 }),
        nameof(DoubleInsideDay) => (Market(), new DoubleInsideDay()),
        nameof(SessionVolumeProfile) => (Market(), new SessionVolumeProfile()),
        nameof(RefillZones) => (Market(), new RefillZones { MinimumVolume = 1, PriceGrouping = 1, AbsorptionOnly = false }),
        _ => throw new ArgumentOutOfRangeException(nameof(study)),
    };

    private static ChartLayout Render(ChartModel model)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1200, 700));
        return new ChartRenderer().Render(surface.Canvas, model, 1200, 700, new ChartRenderState());
    }

    [Theory]
    [MemberData(nameof(Studies))]
    public void Drawn_levels_are_alertable_with_stable_ids_and_can_be_picked_where_they_are_drawn(string name)
    {
        var (model, study) = Create(name);
        using var _ = model;
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
        model.Update(); model.Update();
        int bars = model.Series.Count;

        var levels = study.GetAlertLevels().ToList();
        Assert.NotEmpty(levels);
        Assert.Equal(levels.Count, levels.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var level in levels)
        {
            Assert.False(string.IsNullOrWhiteSpace(level.Name));
            Assert.True(level.Price > 0, $"{level.Id} price {level.Price}");
            Assert.InRange(level.FromBar, 0, bars - 1);
            Assert.True(level.ToBar >= level.FromBar, $"{level.Id} spans {level.FromBar}..{level.ToBar}");
            Assert.True(level.ToBar == int.MaxValue || level.ToBar < bars, $"{level.Id} ends at {level.ToBar}");
        }

        var operands = ChartAlertEngine.Lines(model).Where(o => o.Source == ChartAlertSource.Level).ToList();
        Assert.Equal(levels.Select(l => l.Id).Order(StringComparer.Ordinal), operands.Select(o => o.Field!).Order(StringComparer.Ordinal));
        foreach (var level in levels)
        {
            var operand = Assert.Single(operands, o => o.Field == level.Id);
            Assert.True(ChartAlertEngine.TryValue(model, operand, level.FromBar, out var value), level.Id);
            Assert.Equal(level.Price, value);
            Assert.Equal(level, ChartAlertEngine.LevelOf(model, operand));
        }

        study.RecalculateValues();
        model.Update(); model.Update();
        Assert.Equal(levels.Select(l => l.Id).Order(StringComparer.Ordinal), study.GetAlertLevels().Select(l => l.Id).Order(StringComparer.Ordinal));

        model.Viewport.BarWidth = 5;
        var layout = Render(model);
        int last = bars - 1;
        var picked = levels.LastOrDefault(l => l.FromBar <= layout.LastVisible && Math.Min(l.ToBar, last) >= layout.FirstVisible);
        Assert.False(string.IsNullOrEmpty(picked.Id), $"no level is visible in bars {layout.FirstVisible}..{layout.LastVisible}");
        int bar = Math.Max(picked.FromBar, layout.FirstVisible);
        model.Viewport.AutoScale = false;
        model.Viewport.PriceTop = (double)picked.Price + 25;
        model.Viewport.PriceBottom = (double)picked.Price - 25;
        layout = Render(model);
        var hit = ChartAlertEngine.LineAt(model, layout.CenterX(bar), ((IChartContainer)layout.Main).GetYByPrice(picked.Price, false), 10);
        Assert.NotNull(hit);
        Assert.Equal(ChartAlertSource.Level, hit.Source);
        Assert.Contains(levels, l => l.Id == hit.Field);
    }
}
