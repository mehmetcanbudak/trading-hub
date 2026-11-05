using TradingHub.Feeds;
using Avalonia.Controls;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Market;

// Uses the real application chart composition, subscription, writer and selected-range reader.
// Also available to the separate wide-book performance scene; there is no provider fallback.
internal sealed class KvpDepthHistoryFixture : IDisposable
{
    private readonly MainWindow _owner;
    private readonly Action<double> _pump;
    private readonly ChartTab? _previous;
    private bool _disposed;

    private KvpDepthHistoryFixture(MainWindow owner, Action<double> pump, ChartTab? previous,
        KvpSceneFeed feed, ChartTab chart)
    {
        _owner = owner;
        _pump = pump;
        _previous = previous;
        Feed = feed;
        Chart = chart;
    }

    public KvpSceneFeed Feed { get; }
    public ChartTab Chart { get; }
    public ChartModel Model => Chart.Model!;
    public ChartView View => Chart.View;
    public InstrumentData Data => Model.Data;
    public Instrument Instrument => Data.Instrument;

    public static KvpDepthHistoryFixture Open(MainWindow owner, Action<double> pump, string recordingRoot,
        Instrument instrument, IReadOnlyList<Trade> trades, DateTime now, ChartPeriod period,
        DepthRecordingOptions? recording = null)
    {
        var previous = owner.Trading.ActiveTab;
        var feed = new KvpSceneFeed(instrument.FeedId, instrument) { Now = now };
        feed.Trades.AddRange(trades);
        double hours = trades.Count == 0 ? 1 : Math.Max(1, (now - trades.Min(trade => trade.Time)).TotalHours + 1);
        var profile = new ConnectionProfile
        {
            Id = instrument.FeedId,
            Name = "Isolated depth/history consumer",
            Kind = ConnectionKind.Simulator,
            HistoryHours = hours,
        };
        KvpSceneFeed.Register(App.Services, profile, feed);
        var opening = owner.Trading.NewTabAsync(profile.Id, instrument.Symbol, period,
            new ChartSettings { TimeZoneId = "UTC", PriceStep = 1 });
        Wait(() => opening.IsCompleted, pump, "The isolated chart selection did not complete.");
        var chart = opening.GetAwaiter().GetResult();
        if (chart.Model == null) throw new InvalidOperationException("The selected fake provider did not produce a chart model.");
        var result = new KvpDepthHistoryFixture(owner, pump, previous, feed, chart);
        try
        {
            Wait(() =>
            {
                result.Data.Pump();
                result.Model.Update();
                return !result.Data.IsLoadingHistory && !result.Model.IsAggregating;
            }, pump, "The isolated chart history did not settle.");
            if (result.Data.HistoryError != null) throw new InvalidOperationException(result.Data.HistoryError);
            result.Data.RecordingRoot = Path.GetFullPath(recordingRoot);
            result.Data.RecordLive = false;
            result.Data.ConfigureDepthRecording(recording ?? new DepthRecordingOptions());
            owner.Navigate("trading");
            owner.Trading.Activate(chart);
            result.Pump(0.15);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Pump(double seconds = 0.02)
    {
        Data.Pump();
        Model.Update();
        View.InvalidateVisual();
        _pump(seconds);
        Data.Pump();
        Model.Update();
    }

    public void SetClock(DateTime now)
    {
        Feed.Now = now;
        Pump();
    }

    public void Push(DepthUpdate update, bool pump = true)
    {
        Feed.PushDepth(Instrument, update);
        if (pump) Pump();
    }

    public DepthUpdate PushBook(OrderBookBuilder book, DateTime receivedAt, long sequence,
        bool fullRefresh = false, long? previousSequence = null, DepthCoverage coverage = DepthCoverage.None,
        string? venue = null, bool pump = true)
    {
        var update = book.BuildUpdate(receivedAt.AddMilliseconds(-7),
            maxLevelsPerSide: 10_000, fullRefresh: fullRefresh,
            providerId: Instrument.FeedId, venue: venue ?? Instrument.Exchange,
            sequence: sequence, previousSequence: previousSequence,
            additionalCoverage: coverage, receivedAt: receivedAt);
        Push(update, pump);
        return update;
    }

    public void PushTrade(Trade trade)
    {
        Feed.PushTrades(Instrument, trade);
        Pump();
    }

    public void Status(DateTime now, FeedStatus status, string message)
    {
        Feed.Now = now;
        Feed.PushStatus(Instrument, status, message);
        Pump();
    }

    public void StopWriter(DateTime now)
    {
        Feed.Now = now;
        Data.Stop(); // The real owned writer drains before this returns.
        Model.Update();
        View.InvalidateVisual();
        _pump(0.05);
    }

    public void Select(DateTime from, DateTime to)
    {
        Model.EnsureDepthHistory(from, to);
        Wait(() =>
        {
            Model.Update();
            return !Model.IsLoadingDepthHistory;
        }, _pump, "The chart's selected recorded-depth reader did not settle.");
        Model.Invalidate();
        View.InvalidateVisual();
        _pump(0.08);
    }

    // Callers choose limits explicitly before measuring. This traverses the same builder/sink,
    // recording and chart heatmap path as ordinary received updates, not a kernel-only substitute.
    public DepthUpdate PushWideBook(DateTime receivedAt, int levelsPerSide, long sequence,
        bool fullRefresh = true, decimal size = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levelsPerSide, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levelsPerSide, 10_000);
        var book = new OrderBookBuilder();
        for (int i = 0; i < levelsPerSide; i++)
        {
            book.Set(BookSide.Bid, -5L - i, size + i);
            book.Set(BookSide.Ask, -3L + i, size + i);
        }
        return PushBook(book, receivedAt, sequence, fullRefresh);
    }

    private static void Wait(Func<bool> condition, Action<double> pump, string error)
    {
        var until = DateTime.UtcNow.AddSeconds(45);
        while (!condition() && DateTime.UtcNow < until) pump(0.02);
        if (!condition()) throw new TimeoutException(error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Data.Stop();
        _owner.Trading.CloseChart(Chart);
        Feed.Dispose();
        if (_previous != null && _owner.Trading.Tabs.Contains(_previous)) _owner.Trading.Activate(_previous);
    }
}
