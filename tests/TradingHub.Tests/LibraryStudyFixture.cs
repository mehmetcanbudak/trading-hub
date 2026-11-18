using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Deterministic markets and causality checks shared by the library study tests.</summary>
internal static class LibraryStudyFixture
{
    internal readonly record struct Ohlc(decimal Open, decimal High, decimal Low, decimal Close, decimal Volume = 20);

    /// <summary>A chart of explicit bars (tick 1) starting at <paramref name="start"/>; every bar but the last is closed.</summary>
    internal static ChartModel Model(DateTime start, ChartPeriod period, SessionSettings? session, IReadOnlyList<Ohlc> bars, decimal tick = 1)
    {
        var instrument = new Instrument { Symbol = "LIBRARY-STUDY-FIXTURE", FeedId = "sim", TickSize = tick, VolumeStep = 1 };
        IMarketDataFeed feed = session == null ? new SimulatedFeed() : new SessionFeed(session);
        var data = new InstrumentData(instrument, feed);
        for (int i = 0; i < bars.Count; i++)
            foreach (var trade in Prints(start, period, i, bars[i])) data.Trades.Add(trade);
        return new ChartModel(data, period, new ChartSettings());
    }

    /// <summary>The open, high, low and close prints of bar <paramref name="index"/>, so observed footprint rows exist across its range.</summary>
    internal static Trade[] Prints(DateTime start, ChartPeriod period, int index, Ohlc bar)
    {
        DateTime time = start + period.ApproxDuration * index; long id = 4L * index + 1;
        return new[]
        {
            new Trade(id, time, bar.Open, bar.Volume / 4, TradeSide.Buy),
            new Trade(id + 1, time.AddSeconds(1), bar.High, bar.Volume / 4, TradeSide.Buy),
            new Trade(id + 2, time.AddSeconds(2), bar.Low, bar.Volume / 4, TradeSide.Sell),
            new Trade(id + 3, time.AddSeconds(3), bar.Close, bar.Volume / 4, TradeSide.Sell),
        };
    }

    /// <summary>Appends bar <paramref name="index"/> to a live chart print by print, as a feed would, updating after each.</summary>
    internal static void Append(ChartModel model, DateTime start, ChartPeriod period, int index, Ohlc bar, Action<int>? afterPrint = null)
    {
        var prints = Prints(start, period, index, bar);
        for (int k = 0; k < prints.Length; k++)
        {
            model.Data.Trades.Add(prints[k]); model.Update(); model.Update();
            afterPrint?.Invoke(k);
        }
    }

    internal static ChartModel Model(params Ohlc[] bars) => Model(new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), ChartPeriod.M1, null, bars);

    /// <summary>A seeded random walk with trending legs (prices on whole ticks around 1000).</summary>
    internal static List<Ohlc> Walk(int count, int seed = 7)
    {
        var random = new Random(seed); var list = new List<Ohlc>(count); decimal price = 1000; int drift = 1;
        for (int i = 0; i < count; i++)
        {
            if (i % 40 == 0) drift = random.Next(3) - 1;
            decimal open = price, close = Math.Max(10, open + random.Next(-6, 7) + drift * 2);
            decimal high = Math.Max(open, close) + random.Next(0, 5), low = Math.Min(open, close) - random.Next(0, 5);
            list.Add(new(open, high, low, close, 8 + random.Next(0, 40) * 4)); price = close;
        }
        return list;
    }

    internal static T Add<T>(ChartModel model, T indicator) where T : Indicator
    {
        var slot = model.AddIndicator(indicator, applyDefaultColors: false); Assert.Null(slot.Error); return indicator;
    }

    internal static OptionalValueDataSeries Series(Indicator indicator, string id) => Assert.IsType<OptionalValueDataSeries>(indicator.DataSeries.Single(x => x.Id == id));

    /// <summary>Renders the chart once; returns the PNG when <paramref name="png"/> is requested.</summary>
    internal static void Render(ChartModel model, int width = 1400, int height = 800)
    {
        model.Viewport.BarWidth = 8;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        new ChartRenderer().Render(surface.Canvas, model, width, height, new ChartRenderState());
        Assert.All(model.Indicators, slot => Assert.Null(slot.Error));
    }

    /// <summary>
    /// No look-ahead: everything a study knew with a shorter history is identical with more history (same source bar,
    /// availability, prices and text), and every series value at an earlier closed bar is unchanged.
    /// </summary>
    internal static void AssertPrefixInvariant(Func<Indicator> create, IReadOnlyList<Ohlc> bars, int cut, ChartPeriod? period = null, DateTime? start = null, SessionSettings? session = null)
    {
        var at = start ?? new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc); var p = period ?? ChartPeriod.M5;
        using var shortModel = Model(at, p, session, bars.Take(cut).ToList());
        using var fullModel = Model(at, p, session, bars);
        var shortStudy = Add(shortModel, create()); var fullStudy = Add(fullModel, create());
        int known = cut - 2; // the short run's last bar is forming
        if (shortStudy is ClosedBarStudy a && fullStudy is ClosedBarStudy b)
        {
            Assert.Equal(cut - 1, a.Bars.Count);
            foreach (var x in a.Scene.Boxes)
                Assert.Contains(b.Scene.Boxes, y => y.From == x.From && y.Available == x.Available && y.Top == x.Top && y.Bottom == x.Bottom && y.Text == x.Text && (x.Live || y.To == x.To));
            foreach (var x in a.Scene.Lines)
                Assert.Contains(b.Scene.Lines, y => y.From == x.From && y.Available == x.Available && y.Price == x.Price && y.EndPrice == x.EndPrice && y.Label == x.Label && (x.Live || y.To == x.To));
            foreach (var x in a.Scene.Marks) Assert.Contains(x, b.Scene.Marks);
            foreach (var x in a.Scene.Tags) Assert.Contains(x, b.Scene.Tags);
            Assert.All(a.Scene.Boxes.Concat<StudyShape>(a.Scene.Lines), s => Assert.True(s.Available <= known && s.From <= s.Available));
            Assert.All(a.Scene.Marks, m => Assert.True(m.Available <= known && m.Bar <= m.Available));
            Assert.All(a.Scene.Tags, m => Assert.True(m.Available <= known && m.Bar <= m.Available));
        }
        for (int s = 0; s < shortStudy.DataSeries.Count; s++)
        {
            if (shortStudy.DataSeries[s] is not ValueDataSeries x || fullStudy.DataSeries[s] is not ValueDataSeries y) continue;
            for (int bar = 0; bar <= known; bar++)
            {
                bool xa = x is not IValueDataSeriesAvailability xo || xo.IsAvailable(bar), ya = y is not IValueDataSeriesAvailability yo || yo.IsAvailable(bar);
                Assert.True(xa == ya && (!xa || x[bar] == y[bar]), $"{create().GetType().Name} series {x.Id} changed at bar {bar}: {x[bar]} -> {y[bar]}");
            }
        }
    }

    /// <summary>
    /// The live path: the second half of <paramref name="bars"/> arrives print by print. While only the forming bar receives
    /// prints nothing published changes; each new bar processes exactly the bar it closes; an earlier bar reported again and a
    /// retracted close both replay; the result always equals a fresh full calculation over the same bars.
    /// </summary>
    internal static void AssertLiveMatchesReplay(Func<ClosedBarStudy> create, IReadOnlyList<Ohlc> bars, ChartPeriod? period = null, DateTime? start = null, SessionSettings? session = null)
    {
        var at = start ?? new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc); var p = period ?? ChartPeriod.M5;
        int seeded = bars.Count / 2, last = bars.Count - 1;
        using var live = Model(at, p, session, bars.Take(seeded).ToList());
        var study = Add(live, create());
        string name = study.GetType().Name;
        for (int i = seeded; i < bars.Count; i++)
        {
            string published = "";
            Append(live, at, p, i, bars[i], k =>
            {
                Assert.Equal(i + 1, live.Series.Count);
                Assert.Equal(i, study.Bars.Count); // bar i forms; every earlier bar was processed once it had a successor
                string now = Snapshot(study, i);
                Assert.True(k == 0 || published == now, $"{name}: print {k} into forming bar {i} changed what the study publishes");
                published = now;
            });
        }

        using var fresh = Model(at, p, session, bars);
        string expected = Snapshot(Add(fresh, create()), bars.Count);
        Assert.True(expected == Snapshot(study, bars.Count), $"{name}: the live path differs from a fresh calculation");
        // an earlier bar reported again (an open-interest correction, for example) replays from the start
        study.HostCalculate(last / 2);
        Assert.True(expected == Snapshot(study, bars.Count), $"{name}: an earlier bar reported again did not replay to the same result");
        // a close that is retracted replays without it
        live.Series[last].IsClosed = true; study.HostCalculate(last);
        Assert.Equal(bars.Count, study.Bars.Count);
        live.Series[last].IsClosed = false; study.HostCalculate(last);
        Assert.Equal(last, study.Bars.Count);
        Assert.True(expected == Snapshot(study, bars.Count), $"{name}: a retracted close did not replay to the same result");
    }

    /// <summary>
    /// What a closed-bar study publishes: every scene shape with its ends, colors and fill, the dashboard, the alert levels and
    /// every series value and availability before bar <paramref name="bars"/>.
    /// </summary>
    internal static string Snapshot(ClosedBarStudy study, int bars)
    {
        var text = new System.Text.StringBuilder();
        foreach (var s in study.Scene.Boxes) text.AppendLine($"box {s.From}/{s.Available}/{s.To}/{s.EndAvailable}/{s.Top}/{s.Bottom}/{s.Text}/{s.Alert}/{s.Color.ToArgb()}/{s.FillAlpha}");
        foreach (var s in study.Scene.Lines) text.AppendLine($"line {s.From}/{s.Available}/{s.To}/{s.EndAvailable}/{s.Price}/{s.EndPrice}/{s.Label}/{s.Alert}/{s.Color.ToArgb()}");
        foreach (var m in study.Scene.Marks) text.AppendLine($"mark {m}");
        foreach (var t in study.Scene.Tags) text.AppendLine($"tag {t}");
        foreach (var (bar, color) in study.Scene.Tints.OrderBy(t => t.Key)) text.AppendLine($"tint {bar}/{color.ToArgb()}");
        foreach (var path in study.Scene.Paths) text.AppendLine($"path {path.Available}/{path.Color.ToArgb()}/{string.Join(";", path.Points)}");
        text.AppendLine($"dash {study.Scene.DashTitle}");
        foreach (var row in study.Scene.Dash) text.AppendLine($"row {row.Name}/{row.Value}/{row.Color.ToArgb()}");
        foreach (var level in study.GetAlertLevels()) text.AppendLine($"level {level}");
        foreach (var series in study.DataSeries.OfType<ValueDataSeries>())
            for (int bar = 0; bar < bars; bar++)
                text.AppendLine(series is IValueDataSeriesAvailability a && !a.IsAvailable(bar) ? $"{series.Id}[{bar}] -" : $"{series.Id}[{bar}] {series[bar]}");
        return text.ToString();
    }

    /// <summary>
    /// Display-only filters: with the filters on (<paramref name="filtered"/>) and off (<paramref name="plain"/>) every series
    /// value, every scene shape (bars, availability, prices, text, ends) and every alert level are identical; returns the
    /// filtered study so the caller can assert what <see cref="ClosedBarStudy.IsShown(StudyBox)"/> hides.
    /// </summary>
    internal static ClosedBarStudy AssertDisplayOnly(Func<ClosedBarStudy> plain, Func<ClosedBarStudy> filtered, IReadOnlyList<Ohlc> bars, ChartPeriod? period = null, DateTime? start = null, SessionSettings? session = null)
    {
        var at = start ?? new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc); var p = period ?? ChartPeriod.M5;
        using var a = Model(at, p, session, bars); using var b = Model(at, p, session, bars);
        var x = Add(a, plain()); var y = Add(b, filtered());
        Render(b);
        Assert.Equal(x.DataSeries.Count, y.DataSeries.Count);
        for (int s = 0; s < x.DataSeries.Count; s++)
        {
            if (x.DataSeries[s] is not ValueDataSeries u || y.DataSeries[s] is not ValueDataSeries v) continue;
            for (int bar = 0; bar < a.Series.Count; bar++)
            {
                bool ua = u is not IValueDataSeriesAvailability uo || uo.IsAvailable(bar), va = v is not IValueDataSeriesAvailability vo || vo.IsAvailable(bar);
                Assert.True(ua == va && (!ua || u[bar] == v[bar]), $"{x.GetType().Name} series {u.Id} differs at bar {bar} with display filters on");
            }
        }
        static string Box(StudyBox s) => $"{s.From}/{s.Available}/{s.To}/{s.EndAvailable}/{s.Top}/{s.Bottom}/{s.Text}/{s.Alert}";
        static string Line(StudyLine s) => $"{s.From}/{s.Available}/{s.To}/{s.EndAvailable}/{s.Price}/{s.EndPrice}/{s.Label}/{s.Alert}";
        Assert.Equal(x.Scene.Boxes.Select(Box), y.Scene.Boxes.Select(Box));
        Assert.Equal(x.Scene.Lines.Select(Line), y.Scene.Lines.Select(Line));
        Assert.Equal(x.Scene.Marks, y.Scene.Marks);
        Assert.Equal(x.Scene.Tags, y.Scene.Tags);
        Assert.Equal(x.Scene.Tints.OrderBy(t => t.Key), y.Scene.Tints.OrderBy(t => t.Key));
        Assert.Equal(x.Scene.Paths.Select(q => (q.Available, string.Join(";", q.Points))), y.Scene.Paths.Select(q => (q.Available, string.Join(";", q.Points))));
        Assert.Equal(x.GetAlertLevels(), y.GetAlertLevels());
        return y;
    }

    internal sealed class SessionFeed(SessionSettings session) : IMarketDataFeed, ISessionSettingsFeed
    {
        private readonly SimulatedFeed _inner = new();
        public string Id => "sim"; public string DisplayName => "Library study fixture"; public bool SupportsHistory => true; public bool IsLive => false;
        public SessionSettings GetSessionSettings(Instrument instrument) => session;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => _inner.GetInstrumentsAsync(ct);
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => _inner.LoadTradesAsync(instrument, fromUtc, toUtc, fromId, onBatch, progress, ct);
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => _inner.Subscribe(instrument, sink);
        public void Dispose() => _inner.Dispose();
    }
}
