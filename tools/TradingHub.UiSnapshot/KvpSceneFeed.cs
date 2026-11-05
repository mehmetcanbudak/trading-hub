using System.Reflection;
using TradingHub.App.Services;
using TradingHub.Feeds;
using TradingHub.Market;

// Deterministic provider substrate for actual UI consumers. It has no network or credential path.
internal class KvpSceneFeed(string id, params Instrument[] instruments) : IMarketDataFeed
{
    private readonly List<(Instrument Instrument, IMarketDataSink Sink)> _sinks = new();
    public string Id => id;
    public string DisplayName => "Isolated KVP market fixture";
    public bool SupportsHistory => true;
    public bool IsLive => false;
    public DateTime Now { get; set; } = new(2026, 11, 4, 16, 0, 0, DateTimeKind.Utc);
    public List<Trade> Trades { get; } = new();
    public int InstrumentRequests { get; private set; }
    public Func<CancellationToken, Task<IReadOnlyList<Instrument>>>? InstrumentsResponse { get; set; }

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        InstrumentRequests++;
        return InstrumentsResponse?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<Instrument>>(instruments);
    }
    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var selected = Trades.Where(trade => trade.Time >= fromUtc && trade.Time < toUtc && (fromId == null || trade.Id > fromId)).ToArray();
        if (selected.Length > 0) onBatch(selected);
        progress?.Report(1);
        return Task.CompletedTask;
    }
    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        _sinks.Add((instrument, sink));
        sink.OnStatus(instrument, FeedStatus.Connected, "Deterministic isolated fake provider");
        return new Subscription(this, instrument, sink);
    }
    public void PushTrades(Instrument instrument, params Trade[] trades)
    {
        if (trades.Length > 0) Now = trades.Max(trade => trade.Time);
        foreach (var item in _sinks.Where(item => item.Instrument.Key == instrument.Key).ToArray()) item.Sink.OnTrades(item.Instrument, trades);
    }
    public void PushDepth(Instrument instrument, DepthUpdate update)
    {
        Now = update.ReceivedAt;
        foreach (var item in _sinks.Where(item => item.Instrument.Key == instrument.Key).ToArray()) item.Sink.OnDepth(item.Instrument, update);
    }
    public void PushStatus(Instrument instrument, FeedStatus status, string? message = null)
    {
        foreach (var item in _sinks.Where(item => item.Instrument.Key == instrument.Key).ToArray())
            item.Sink.OnStatus(item.Instrument, status, message);
    }
    public void Dispose() => _sinks.Clear();

    private sealed class Subscription(KvpSceneFeed owner, Instrument instrument, IMarketDataSink sink) : IDisposable
    {
        public void Dispose() => owner._sinks.Remove((instrument, sink));
    }
    public static void Register(AppServices services, ConnectionProfile profile, IMarketDataFeed feed)
    {
        services.Connections.Profiles.Add(profile);
        var feeds = (Dictionary<string, IMarketDataFeed>)typeof(ConnectionManager).GetField("_feeds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(services.Connections)!;
        feeds.Add(profile.Id, feed);
    }
}

internal sealed class KvpCandleSceneFeed(string id, params Instrument[] instruments) : KvpSceneFeed(id, instruments), IKlineHistory
{
    public List<Kline> Candles { get; } = new();
    public List<(DateTime From, DateTime To, int Minutes)> CandleRequests { get; } = new();
    public bool FailCandles { get; set; }
    public string CacheTag => "isolated-kvp-ui-v1-" + Id;
    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(Candles.Count > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(Candles[0].OpenTimeMs).UtcDateTime : Trades.Count > 0 ? Trades[0].Time : null);
    public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CandleRequests.Add((fromUtc, toUtc, minutes));
        if (FailCandles) throw new IOException("Isolated candle constituent unavailable");
        var selected = Candles.Where(candle => DateTimeOffset.FromUnixTimeMilliseconds(candle.OpenTimeMs).UtcDateTime >= fromUtc &&
            DateTimeOffset.FromUnixTimeMilliseconds(candle.OpenTimeMs).UtcDateTime < toUtc).ToArray();
        if (selected.Length > 0) onBatch(selected);
        progress?.Report(1);
        return Task.CompletedTask;
    }
}
