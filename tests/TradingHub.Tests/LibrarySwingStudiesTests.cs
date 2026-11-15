using TradingHub.Data;
using TradingHub.Indicators.Native;
using static TradingHub.Tests.LibraryStudyFixture;

namespace TradingHub.Tests;

public class LibrarySwingStudiesTests
{
    private static readonly DateTime Start = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    // swing high 15 (bar 1, confirmed at 2), swing low 8 (bar 3, confirmed at 4), BOS up at 5, swing high 18 (bar 6, confirmed
    // at 7), CHoCH down at 9 through 8, swing low 6 (bar 9, confirmed at 10), sweep of 6 at 12 (wick 5, close 8); bar 13 forms
    private static Ohlc[] Shifts() => new Ohlc[]
    {
        new(10, 12, 9, 10), new(11, 15, 11, 14), new(13, 13, 10, 11), new(11, 12, 8, 9), new(10, 14, 10, 13), new(13, 17, 12, 16),
        new(16, 18, 14, 15), new(15, 16, 11, 12), new(12, 14, 9, 10), new(10, 13, 6, 7), new(7, 12, 7, 11), new(11, 13, 9, 12),
        new(12, 12, 5, 8), new(8, 10, 7, 9),
    };

    // zigzag lows/highs 100 (bar 1), 110 (3), 105 (5), 130 (7), 120 (9), 135 (11), each confirmed one bar later; bar 13 forms
    private static Ohlc[] Waves() => new Ohlc[]
    {
        new(103, 104, 101, 102), new(102, 103, 100, 101), new(102, 106, 102, 105), new(105, 110, 105, 109), new(108, 108, 106, 107),
        new(107, 107, 105, 106), new(107, 118, 107, 117), new(117, 130, 117, 128), new(127, 127, 122, 123), new(123, 125, 120, 121),
        new(123, 128, 123, 127), new(127, 135, 127, 134), new(132, 132, 129, 130), new(130, 131, 128, 129),
    };

    [Fact]
    public void Bos_and_choch_are_known_at_the_breaking_close_with_the_protected_level_and_sweep()
    {
        using var model = Model(Shifts());
        var study = Add(model, new StructureShiftSweeps { Length = 1 });
        Assert.Equal(2, study.Breaks.Count);
        var bos = study.Breaks[0];
        Assert.Equal((1, 5, 15m, 1, false), (bos.SwingBar, bos.Bar, bos.Level, bos.Direction, bos.Choch));
        var choch = study.Breaks[1];
        Assert.Equal((3, 9, 8m, -1, true), (choch.SwingBar, choch.Bar, choch.Level, choch.Direction, choch.Choch));
        var signal = Series(study, "StructureShift.Signal");
        Assert.Equal(1m, signal[5]); Assert.Equal(-2m, signal[9]); Assert.Equal(0m, signal[4]); Assert.Equal(0m, signal[8]);
        var trend = Series(study, "StructureShift.Trend");
        Assert.Equal(0m, trend[4]); Assert.Equal(1m, trend[5]); Assert.Equal(1m, trend[8]); Assert.Equal(-1m, trend[9]);
        Assert.Contains(study.Scene.Tags, t => t.Bar == 3 && t.Available == 5 && t.Price == 15 && t.Text == "BOS");
        Assert.Contains(study.Scene.Tags, t => t.Bar == 6 && t.Available == 9 && t.Price == 8 && t.Text == "CHoCH");

        // protected low 8 from the BOS, replaced by protected high 18 at the CHoCH
        var first = Assert.Single(study.Scene.Lines, l => l.Alert == "Protected swing" && l.Price == 8);
        Assert.Equal((3, 5, 9), (first.From, first.Available, first.To));
        var current = Assert.Single(study.Scene.Lines, l => l.Alert == "Protected swing" && l.Live);
        Assert.Equal((6, 9, 18m), (current.From, current.Available, current.Price));
        Assert.Equal(18m, Series(study, "StructureShift.Protected")[9]);
        Assert.Contains(study.GetAlertLevels(), l => l.Name == "Protected swing" && l.Price == 18);

        // the low at 6 is swept (wick 5, close 8) at bar 12, not broken
        Assert.Contains(study.Scene.Marks, m => m.Bar == 12 && m.Available == 12 && m.Price == 5 && m.Text == "Sweep");
        Assert.Equal(1m, Series(study, "StructureShift.Sweep")[12]);
        Assert.Equal(0m, Series(study, "StructureShift.Sweep")[11]);
    }

    [Fact]
    public void A_break_is_validated_by_the_first_close_above_the_atr_band()
    {
        // high 105 (bar 1, confirmed at 2) broken at 3; closes 107 and 110 stay inside level ± ATR(1); bar 6 closes 112 > 105 + 4
        using var model = Model(new(100, 102, 99, 101), new(101, 105, 100, 104), new(104, 104, 101, 102), new(102, 107, 102, 106),
            new(106, 108, 105, 107), new(107, 111, 106, 110), new(110, 113, 109, 112), new(112, 113, 111, 112));
        var study = Add(model, new AnchoredMarketStructure { SwingStrength = 1, ValidationAtrLength = 1, ValidationBand = 1 });
        var br = Assert.Single(study.Breaks);
        Assert.Equal((0, 1, 3, 105m, 1), (br.Tier, br.SwingBar, br.Bar, br.Level, br.Direction));
        Assert.Equal(6, br.Resolved); Assert.True(br.Valid);
        var box = Assert.Single(study.Scene.Boxes);
        Assert.Equal((3, 6, 111m, 105m, 6), (box.From, box.Available, box.Top, box.Bottom, box.To));
        Assert.Contains(study.Scene.Marks, m => m.Bar == 6 && m.Available == 6 && m.Kind == StudyMarkKind.Square);
        Assert.Equal(1m, Series(study, "AnchoredStructure.Validated")[6]);
        Assert.Equal(0m, Series(study, "AnchoredStructure.Validated")[5]);
        Assert.Equal(1m, Series(study, "AnchoredStructure.Break")[3]);
        Assert.Equal(1m, Series(study, "AnchoredStructure.ShortTrend")[3]);
    }

    [Fact]
    public void A_break_is_invalidated_by_the_first_close_below_the_atr_band()
    {
        // same break at 3; bar 4 closes 104 inside 105 ± 3; bar 5 closes 98 < 105 - 6
        using var model = Model(new(100, 102, 99, 101), new(101, 105, 100, 104), new(104, 104, 101, 102), new(102, 107, 102, 106),
            new(106, 106, 103, 104), new(104, 104, 98, 98), new(98, 99, 97, 98));
        var study = Add(model, new AnchoredMarketStructure { SwingStrength = 1, ValidationAtrLength = 1, ValidationBand = 1 });
        var br = Assert.Single(study.Breaks);
        Assert.Equal(3, br.Bar); Assert.Equal(5, br.Resolved); Assert.False(br.Valid);
        var box = Assert.Single(study.Scene.Boxes);
        Assert.Equal((3, 5, 107m, 103m), (box.From, box.Available, box.Top, box.Bottom));
        Assert.Contains(study.Scene.Marks, m => m.Bar == 5 && m.Kind == StudyMarkKind.Cross);
        Assert.Equal(1m, Series(study, "AnchoredStructure.Invalidated")[5]);
        Assert.Equal(0m, Series(study, "AnchoredStructure.Validated")[5]);
    }

    [Fact]
    public void Oscillator_stays_within_0_and_100_and_depends_only_on_the_past()
    {
        using var model = Model(Start, ChartPeriod.M5, null, Walk(400));
        var study = Add(model, new StructureRegimeOscillator());
        var osc = Series(study, "StructureRegime.Oscillator");
        int available = 0;
        for (int bar = 0; bar < 399; bar++)
        {
            if (!osc.IsAvailable(bar)) continue;
            available++;
            Assert.InRange(osc[bar], 0m, 100m);
        }
        Assert.True(available > 300);
        AssertPrefixInvariant(() => new StructureRegimeOscillator(), Walk(400), 250);
        AssertPrefixInvariant(() => new StructureRegimeOscillator { SwingStrength = 2, Smoothing = 1 }, Walk(400, 11), 180);
    }

    [Fact]
    public void Scatter_legs_and_averages_come_from_the_confirmed_alternating_swings()
    {
        using var model = Model(Waves());
        var study = Add(model, new StructureScatterPanel { PivotLength = 1 });
        Assert.Equal(4, study.LegCount);
        Assert.Equal(new[] { 6, 8, 10, 12 }, study.Legs.Select(l => l.Available));
        Assert.Equal(new[] { true, false, true, false }, study.Legs.Select(l => l.Bullish));
        Assert.Equal(new[] { 10m / 100m * 100m, 5m / 110m * 100m, 25m / 105m * 100m, 10m / 130m * 100m }, study.Legs.Select(l => l.ImpulsePercent));
        Assert.Equal(new[] { 0.5m, 5m, 0.4m, 1.5m }, study.Legs.Select(l => l.Retracement));
        Assert.Equal(1.85m, study.AverageRetracement);
        decimal mean = (10m / 100m * 100m + 5m / 110m * 100m + 25m / 105m * 100m + 10m / 130m * 100m) / 4;
        Assert.Equal(Math.Round(mean, 10), Math.Round(study.AverageImpulse!.Value, 10));
        Assert.Equal((1, 3), (study.Legs[0].ImpulseFrom, study.Legs[0].ImpulseTo));
        Assert.True(Series(study, "StructureScatter.Percentile").IsAvailable(12));
        Assert.False(Series(study, "StructureScatter.Percentile").IsAvailable(5));
    }

    [Fact]
    public void Higher_period_trend_changes_only_after_the_breaking_higher_bar_completes()
    {
        // five M1 bars per M5 bar: M5 high 15 (bar 1) confirmed when M5 bar 2 completes; M5 bar 3 (chart 15-19) closes above;
        // it is known at the close of chart bar 20, the first bar of the next five minutes
        var bars = new List<Ohlc>();
        void Five(Ohlc first, decimal close) { bars.Add(first); for (int i = 0; i < 4; i++) bars.Add(new(close, close, close, close)); }
        Five(new(10, 12, 8, 10), 10); Five(new(10, 15, 10, 14), 14); Five(new(13, 13, 9, 11), 11); Five(new(12, 17, 12, 16), 16);
        Five(new(16, 18, 15, 17), 17); bars.Add(new(17, 17, 17, 17));
        using var model = Model(Start, ChartPeriod.M1, null, bars);
        var study = Add(model, new StructureTrendPanel { StructureFrame = StudyTimeFrame.Minutes5, SwingLength = 1, MaximumObjects = 5000 });
        var trend = Series(study, "StructureTrend.Trend");
        Assert.Equal(0m, trend[15]); Assert.Equal(0m, trend[19]); Assert.Equal(1m, trend[20]); Assert.Equal(1m, trend[24]);
        Assert.Equal(1m, Series(study, "StructureTrend.Signal")[20]);
        Assert.Equal(0m, Series(study, "StructureTrend.Signal")[15]);
        Assert.Contains(study.Scene.Lines, l => l.From == 5 && l.To == 20 && l.Available == 20 && l.Price == 15);
        Assert.Equal(1, study.FrameTrend(StudyTimeFrame.Minutes5));
        AssertPrefixInvariant(() => new StructureTrendPanel { StructureFrame = StudyTimeFrame.Minutes5, SwingLength = 1, MaximumObjects = 5000 }, bars, 20, ChartPeriod.M1);
    }

    [Fact]
    public void A_five_wave_impulse_is_found_at_the_confirming_bar_with_the_extended_wave_and_targets()
    {
        using var model = Model(Waves());
        var study = Add(model, new WaveExtensions { SwingLength = 1, RequireSubwaves = false });
        var impulse = Assert.Single(study.Impulses);
        Assert.Equal(12, impulse.Available); Assert.True(impulse.Bullish); Assert.Equal(3, impulse.ExtendedWave);
        Assert.Equal(25m / 15m, impulse.Ratio);
        Assert.Equal(new[] { 1, 3, 5, 7, 9, 11 }, impulse.Pivots.Select(p => p.Bar));
        Assert.Equal(1m, Series(study, "WaveExtensions.Impulse")[12]); Assert.Equal(0m, Series(study, "WaveExtensions.Impulse")[11]);
        Assert.Equal(3m, Series(study, "WaveExtensions.ExtendedWave")[12]);

        // wave-5 targets existed from the wave-4 confirmation (bar 10) until the wave-5 pivot replaced the pattern (bar 12)
        var equality = Assert.Single(study.Scene.Lines, l => l.Alert == "Wave 5 target (equality)");
        Assert.Equal((9, 10, 130m, 12), (equality.From, equality.Available, equality.Price, equality.To));
        var fib = Assert.Single(study.Scene.Lines, l => l.Alert == "Wave 5 target (0.618)");
        Assert.Equal((9, 10, 138.54m, 12), (fib.From, fib.Available, fib.Price, fib.To));
        Assert.Equal(130m, Series(study, "WaveExtensions.EqualityTarget")[11]);
        Assert.False(Series(study, "WaveExtensions.EqualityTarget").IsAvailable(9));
        Assert.Null(study.Targets);

        var zone = Assert.Single(study.Scene.Boxes, b => b.Alert == "Extension retracement zone");
        Assert.Equal((11, 12, 120.45m, 114.55m), (zone.From, zone.Available, zone.Top, zone.Bottom));
        Assert.Contains(study.Scene.Tags, t => t.Bar == 7 && t.Available == 12 && t.Text == "Ext. 3");
        Assert.Contains(study.Scene.Lines, l => l.From == 5 && l.To == 7 && l.Available == 12 && l.Price == 105 && l.EndPrice == 130);
    }

    [Fact]
    public void Shift_sweeps_never_redraw_backwards() =>
        AssertPrefixInvariant(() => new StructureShiftSweeps { Length = 3, MaximumObjects = 5000 }, Walk(400), 250);

    [Fact]
    public void Anchored_structure_never_redraws_backwards() =>
        AssertPrefixInvariant(() => new AnchoredMarketStructure { MaximumObjects = 5000 }, Walk(400), 250);

    [Fact]
    public void Scatter_panel_never_redraws_backwards() =>
        AssertPrefixInvariant(() => new StructureScatterPanel { PivotLength = 3, MaximumObjects = 5000 }, Walk(400), 250);

    [Fact]
    public void Trend_panel_never_redraws_backwards() =>
        AssertPrefixInvariant(() => new StructureTrendPanel { StructureFrame = StudyTimeFrame.Minutes15, LiquidityFrame = StudyTimeFrame.Hour, SwingLength = 2, MaximumObjects = 5000 }, Walk(400), 250);

    [Fact]
    public void Wave_extensions_never_redraw_backwards() =>
        AssertPrefixInvariant(() => new WaveExtensions { SwingLength = 2, SubLength = 1, MaximumObjects = 5000 }, Walk(400), 250);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Every_swing_study_renders(int index)
    {
        var period = new[] { ChartPeriod.M5, ChartPeriod.M15, ChartPeriod.H1 }[index];
        using var model = Model(Start, period, null, Walk(500, 3));
        Add(model, new StructureShiftSweeps { Length = 3 });
        Add(model, new AnchoredMarketStructure());
        Add(model, new StructureRegimeOscillator { ShowShort = true });
        Add(model, new StructureScatterPanel { PivotLength = 3 });
        Add(model, new StructureTrendPanel { StructureFrame = StudyTimeFrame.Hours4 });
        Add(model, new WaveExtensions { SwingLength = 2, SubLength = 1 });
        Render(model);
    }
}
