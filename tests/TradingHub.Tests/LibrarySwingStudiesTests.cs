using TradingHub.Data;
using TradingHub.Indicators;
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

    // ------------------------------------------------------------------ display-only drawing filters

    private static T Filtered<T>(Func<T> plain, Func<T> filtered, IReadOnlyList<Ohlc> bars, ChartPeriod? period = null) where T : ClosedBarStudy =>
        (T)AssertDisplayOnly(plain, filtered, bars, period);

    private static PaintbarsDataSeries Paint(ClosedBarStudy study, string id) => Assert.IsType<PaintbarsDataSeries>(study.DataSeries.Single(s => s.Id == id));

    [Fact]
    public void Shift_sweep_drawing_toggles_only_hide_drawings()
    {
        StructureShiftSweeps Plain() => new() { Length = 1 };
        static bool Bos(StudyTag t) => t.Text == "BOS";
        static bool Choch(StudyTag t) => t.Text == "CHoCH";

        var noBos = Filtered(Plain, () => new StructureShiftSweeps { Length = 1, ShowBos = false }, Shifts(), ChartPeriod.M1);
        Assert.False(noBos.IsShown(Assert.Single(noBos.Scene.Tags, Bos)));
        Assert.True(noBos.IsShown(Assert.Single(noBos.Scene.Tags, Choch)));
        Assert.False(noBos.IsShown(Assert.Single(noBos.Scene.Marks, m => m.Kind == StudyMarkKind.TriangleUp)));
        Assert.False(noBos.IsShown(Assert.Single(noBos.Scene.Lines, l => l.Alert == null && l.Style == LineDashStyle.Solid)));
        Assert.True(noBos.IsShown(Assert.Single(noBos.Scene.Lines, l => l.Alert == null && l.Style == LineDashStyle.Dash)));

        var noChoch = Filtered(Plain, () => new StructureShiftSweeps { Length = 1, ShowChoch = false }, Shifts(), ChartPeriod.M1);
        Assert.False(noChoch.IsShown(Assert.Single(noChoch.Scene.Tags, Choch)));
        Assert.False(noChoch.IsShown(Assert.Single(noChoch.Scene.Marks, m => m.Kind == StudyMarkKind.TriangleDown)));
        Assert.True(noChoch.IsShown(Assert.Single(noChoch.Scene.Tags, Bos)));

        var noSweeps = Filtered(Plain, () => new StructureShiftSweeps { Length = 1, ShowSweeps = false }, Shifts(), ChartPeriod.M1);
        Assert.False(noSweeps.IsShown(Assert.Single(noSweeps.Scene.Marks, m => m.Text == "Sweep")));
        Assert.All(noSweeps.Scene.Marks.Where(m => m.Text != "Sweep"), m => Assert.True(noSweeps.IsShown(m)));

        // the protected level is hidden but stays alertable
        var noProtected = Filtered(Plain, () => new StructureShiftSweeps { Length = 1, ShowProtected = false }, Shifts(), ChartPeriod.M1);
        Assert.Equal(2, noProtected.Scene.Lines.Count(l => l.Alert == "Protected swing"));
        Assert.All(noProtected.Scene.Lines, l => Assert.Equal(l.Alert != "Protected swing", noProtected.IsShown(l)));
        Assert.Contains(noProtected.GetAlertLevels(), l => l.Name == "Protected swing" && l.Price == 18);

        // the BOS at 5 precedes the first CHoCH (9): hidden, while the CHoCH stays
        var late = Filtered(Plain, () => new StructureShiftSweeps { Length = 1, HideBosBeforeFirstChoch = true }, Shifts(), ChartPeriod.M1);
        Assert.False(late.IsShown(Assert.Single(late.Scene.Tags, Bos)));
        Assert.False(late.IsShown(Assert.Single(late.Scene.Marks, m => m.Kind == StudyMarkKind.TriangleUp)));
        Assert.True(late.IsShown(Assert.Single(late.Scene.Tags, Choch)));
        Assert.False(new StructureShiftSweeps().HideBosBeforeFirstChoch);

        // over a long walk a BOS after the first CHoCH is shown again
        StructureShiftSweeps Walked() => new() { Length = 3, MaximumObjects = 5000 };
        var walked = Filtered(Walked, () => new StructureShiftSweeps { Length = 3, MaximumObjects = 5000, HideBosBeforeFirstChoch = true }, Walk(400));
        int firstChoch = walked.Scene.Tags.Where(Choch).Min(t => t.Available);
        Assert.All(walked.Scene.Tags.Where(Bos), t => Assert.Equal(t.Available > firstChoch, walked.IsShown(t)));
        Assert.Contains(walked.Scene.Tags, t => Bos(t) && walked.IsShown(t));
        Filtered(Walked, () => new StructureShiftSweeps { Length = 3, MaximumObjects = 5000, ShowBos = false, ShowChoch = false, ShowSweeps = false, ShowProtected = false }, Walk(400));
    }

    [Fact]
    public void Anchored_structure_drawing_toggles_only_hide_drawings()
    {
        AnchoredMarketStructure Plain() => new() { MaximumObjects = 5000 };
        var bars = Walk(400);
        static bool Verdict(StudyMark m) => m.Kind is StudyMarkKind.Square or StudyMarkKind.Cross;

        // swing marks: circles (short term), ITH/ITL, LTH/LTL
        var noShortSwings = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, ShortSwings = false }, bars);
        Assert.Contains(noShortSwings.Scene.Marks, m => m.Kind == StudyMarkKind.Circle);
        Assert.All(noShortSwings.Scene.Marks, m => Assert.Equal(m.Kind != StudyMarkKind.Circle, noShortSwings.IsShown(m)));
        var noMidSwings = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, IntermediateSwings = false }, bars);
        Assert.Contains(noMidSwings.Scene.Marks, m => m.Text is "ITH" or "ITL");
        Assert.All(noMidSwings.Scene.Marks, m => Assert.Equal(m.Text is not ("ITH" or "ITL"), noMidSwings.IsShown(m)));
        var noLongSwings = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, LongSwings = false }, bars);
        Assert.All(noLongSwings.Scene.Marks, m => Assert.Equal(m.Text is not ("LTH" or "LTL"), noLongSwings.IsShown(m)));

        // structure of one tier: its break segments and labels, but not the long-term swing levels (alertable)
        var noShort = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, ShortStructure = false }, bars);
        Assert.Contains(noShort.Scene.Tags, t => t.Text.StartsWith("ST "));
        Assert.All(noShort.Scene.Tags, t => Assert.Equal(!t.Text.StartsWith("ST "), noShort.IsShown(t)));
        Assert.All(noShort.Scene.Lines, l => Assert.Equal(l.Alert != null || l.Style != LineDashStyle.Dot, noShort.IsShown(l)));
        var noMid = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, IntermediateStructure = false }, bars);
        Assert.Contains(noMid.Scene.Tags, t => t.Text.StartsWith("IT "));
        Assert.All(noMid.Scene.Tags, t => Assert.Equal(!t.Text.StartsWith("IT "), noMid.IsShown(t)));
        var noLong = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, LongStructure = false }, bars);
        Assert.All(noLong.Scene.Tags, t => Assert.Equal(!t.Text.StartsWith("LT "), noLong.IsShown(t)));
        Assert.All(noLong.Scene.Lines.Where(l => l.Alert != null), l => Assert.True(noLong.IsShown(l)));

        var unvalidated = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, ValidateBreaks = false }, bars);
        Assert.NotEmpty(unvalidated.Scene.Boxes);
        Assert.All(unvalidated.Scene.Boxes, b => Assert.False(unvalidated.IsShown(b)));
        Assert.All(unvalidated.Scene.Marks, m => Assert.Equal(!Verdict(m), unvalidated.IsShown(m)));

        var noLabels = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, ShowBreakLabels = false }, bars);
        Assert.All(noLabels.Scene.Tags, t => Assert.False(noLabels.IsShown(t)));
        Assert.All(noLabels.Scene.Lines, l => Assert.True(noLabels.IsShown(l)));

        var noBoxes = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, StructureBoxes = false }, bars);
        Assert.All(noBoxes.Scene.Boxes, b => Assert.False(noBoxes.IsShown(b)));
        Assert.Contains(noBoxes.Scene.Marks, m => Verdict(m) && noBoxes.IsShown(m));

        var noTint = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, BreakTint = false }, bars);
        Assert.NotEmpty(noTint.Scene.Tints);
        Assert.All(noTint.Scene.Tints, t => Assert.False(noTint.IsTintShown(t.Key, t.Value)));
        var higherOff = Filtered(Plain, () => new AnchoredMarketStructure { MaximumObjects = 5000, IntermediateStructure = false, LongStructure = false }, bars);
        Assert.All(higherOff.Scene.Tints, t => Assert.False(higherOff.IsTintShown(t.Key, t.Value)));
        var tinted = Filtered(Plain, Plain, bars);
        Assert.All(tinted.Scene.Tints, t => Assert.True(tinted.IsTintShown(t.Key, t.Value)));
    }

    [Fact]
    public void Scatter_panel_drawing_toggles_only_hide_drawings()
    {
        StructureScatterPanel Plain() => new() { PivotLength = 3, MaximumObjects = 5000 };
        var noStructure = Filtered(Plain, () => new StructureScatterPanel { PivotLength = 3, MaximumObjects = 5000, ShowStructure = false }, Walk(400));
        Assert.NotEmpty(noStructure.Scene.Lines); Assert.NotEmpty(noStructure.Scene.Tags);
        Assert.All(noStructure.Scene.Lines, l => Assert.False(noStructure.IsShown(l)));
        Assert.All(noStructure.Scene.Tags, t => Assert.False(noStructure.IsShown(t)));

        // the bar colors are still computed, only not painted
        var noHeat = Filtered(Plain, () => new StructureScatterPanel { PivotLength = 3, MaximumObjects = 5000, Heatmap = false }, Walk(400));
        var paint = Paint(noHeat, "StructureScatter.Paint");
        Assert.False(paint.Visible);
        Assert.Contains(Enumerable.Range(0, noHeat.Bars.Count), bar => paint[bar] != null);
        Assert.True(Paint(Plain(), "StructureScatter.Paint").Visible);
    }

    [Fact]
    public void Trend_panel_drawing_toggles_only_hide_drawings()
    {
        StructureTrendPanel Plain() => new() { SwingLength = 2, MaximumObjects = 5000 };
        var bars = Walk(400);

        var noStructure = Filtered(Plain, () => new StructureTrendPanel { SwingLength = 2, MaximumObjects = 5000, ShowStructure = false }, bars);
        Assert.NotEmpty(noStructure.Scene.Tags);
        Assert.All(noStructure.Scene.Tags, t => Assert.False(noStructure.IsShown(t)));
        Assert.All(noStructure.Scene.Lines, l => Assert.Equal(l.Style == LineDashStyle.Dot, noStructure.IsShown(l)));

        // sweeps are still detected (strategy series identical), only not drawn
        var noLiquidity = Filtered(Plain, () => new StructureTrendPanel { SwingLength = 2, MaximumObjects = 5000, ShowLiquidity = false }, bars);
        Assert.NotEmpty(noLiquidity.Scene.Marks);
        Assert.All(noLiquidity.Scene.Marks, m => Assert.False(noLiquidity.IsShown(m)));
        Assert.All(noLiquidity.Scene.Lines, l => Assert.Equal(l.Style != LineDashStyle.Dot, noLiquidity.IsShown(l)));
        var sweep = Series(noLiquidity, "StructureTrend.Sweep");
        Assert.Contains(Enumerable.Range(0, noLiquidity.Bars.Count), bar => sweep[bar] != 0);

        // gaps are hidden but their live edges stay alertable
        var noFvg = Filtered(Plain, () => new StructureTrendPanel { SwingLength = 2, MaximumObjects = 5000, ShowFvg = false }, bars);
        Assert.NotEmpty(noFvg.Scene.Boxes);
        Assert.All(noFvg.Scene.Boxes, b => Assert.False(noFvg.IsShown(b)));
        Assert.Contains(noFvg.GetAlertLevels(), l => l.Name.Contains("FVG"));

        var noPaint = Filtered(Plain, () => new StructureTrendPanel { SwingLength = 2, MaximumObjects = 5000, ColorCandles = false }, bars);
        var paint = Paint(noPaint, "StructureTrend.Paint");
        Assert.False(paint.Visible);
        Assert.Contains(Enumerable.Range(0, noPaint.Bars.Count), bar => paint[bar] != null);
    }

    [Fact]
    public void Wave_extension_drawing_toggles_only_hide_drawings()
    {
        WaveExtensions Plain() => new() { SwingLength = 1, RequireSubwaves = false };
        static bool Target(StudyLine l) => l.Alert?.StartsWith("Wave 5 target") == true;

        var noTargets = Filtered(Plain, () => new WaveExtensions { SwingLength = 1, RequireSubwaves = false, ShowTargets = false }, Waves(), ChartPeriod.M1);
        Assert.Equal(2, noTargets.Scene.Lines.Count(Target));
        Assert.All(noTargets.Scene.Lines, l => Assert.Equal(!Target(l), noTargets.IsShown(l)));

        var noLabels = Filtered(Plain, () => new WaveExtensions { SwingLength = 1, RequireSubwaves = false, ShowLabels = false }, Waves(), ChartPeriod.M1);
        Assert.Equal(5, noLabels.Scene.Tags.Count);
        Assert.All(noLabels.Scene.Tags, t => Assert.False(noLabels.IsShown(t)));

        // the zone (114.55-120.45) is reached at bar 13: the box and its mark are hidden, the zone alert existed until then
        var reached = Waves().Take(13).Append(new Ohlc(130, 131, 118, 119)).Append(new Ohlc(119, 120, 118, 119)).ToArray();
        var noZone = Filtered(Plain, () => new WaveExtensions { SwingLength = 1, RequireSubwaves = false, ShowZone = false }, reached, ChartPeriod.M1);
        Assert.False(noZone.IsShown(Assert.Single(noZone.Scene.Boxes)));
        Assert.False(noZone.IsShown(Assert.Single(noZone.Scene.Marks, m => m.Text == "Zone reached" && m.Available == 13)));
        var live = Filtered(Plain, () => new WaveExtensions { SwingLength = 1, RequireSubwaves = false, ShowZone = false }, Waves(), ChartPeriod.M1);
        Assert.Contains(live.GetAlertLevels(), l => l.Name.StartsWith("Extension retracement zone"));

        var noPath = Filtered(Plain, () => new WaveExtensions { SwingLength = 1, RequireSubwaves = false, ShowPath = false }, Waves(), ChartPeriod.M1);
        Assert.NotEmpty(noPath.Scene.Paths);
        Assert.All(noPath.Scene.Paths, p => Assert.False(noPath.IsShown(p)));

        // wave 1 (10) ≈ wave 3 (11): the watch tag is hidden by its own toggle or with the targets; the targets stay alertable
        static bool Watch(StudyTag t) => t.Text == "Watch: possible extended wave 5";
        var noWatch = Filtered(Plain, () => new WaveExtensions { SwingLength = 1, RequireSubwaves = false, ShowWatch = false }, WatchWaves(), ChartPeriod.M1);
        Assert.False(noWatch.IsShown(Assert.Single(noWatch.Scene.Tags, Watch)));
        Assert.All(noWatch.Scene.Lines, l => Assert.True(noWatch.IsShown(l)));
        var watchNoTargets = Filtered(Plain, () => new WaveExtensions { SwingLength = 1, RequireSubwaves = false, ShowTargets = false }, WatchWaves(), ChartPeriod.M1);
        Assert.False(watchNoTargets.IsShown(Assert.Single(watchNoTargets.Scene.Tags, Watch)));
        Assert.Equal(2, watchNoTargets.GetAlertLevels().Count(l => l.Name.StartsWith("Wave 5 target")));
    }

    // zigzag 100 (bar 1), 110 (3), 105 (5), 116 (7), 111 (9), each confirmed one bar later: wave 4 with waves 1 and 3 within 10 %
    private static Ohlc[] WatchWaves() => new Ohlc[]
    {
        new(103, 104, 101, 102), new(102, 103, 100, 101), new(102, 106, 102, 105), new(105, 110, 105, 109), new(108, 108, 106, 107),
        new(107, 107, 105, 106), new(107, 112, 107, 111), new(111, 116, 111, 115), new(115, 115, 113, 114), new(114, 114, 111, 112),
        new(112, 113, 112, 113), new(113, 113, 112, 112),
    };
}
