using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Venues;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Cross-venue symbol mapping, the different-asset guard, the composite book and CVD per market.</summary>
public sealed class AggregateVenueTests
{
    private static readonly DateTime Start = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Venues_are_matched_by_base_asset_with_one_preferred_usd_quote_each_and_mixed_quotes_disclosed()
    {
        using var okx = new Venue("okx-swap", "OKX", spot: false, 0.1m, ("BTC-USDT-SWAP", "BTC", "USDT"), ("BTC-USDC-SWAP", "BTC", "USDC"), ("ETH-USDT-SWAP", "ETH", "USDT"));
        using var coinbase = new Venue("coinbase", "Coinbase", spot: true, 0.01m, ("BTC-USDT", "BTC", "USDT"), ("BTC-USD", "BTC", "USD"), ("BTC-EUR", "BTC", "EUR")) { Preferred = "USD" };
        using var hyperliquid = new Venue("hyperliquid", "Hyperliquid", spot: false, 1m, ("BTC", "BTC", "USD"), ("kPEPE", "1000PEPE", "USD"));
        using var feed = new AggregateFeed([okx, coinbase, hyperliquid]);

        var listed = await feed.GetInstrumentsAsync(CancellationToken.None);

        // Contracts that lost their venue's composite slot stay listed under their own symbol (pre-fold identities).
        Assert.Equal(new[] { "1000PEPEUSDT", "BTC-USDC-SWAP", "BTC-USDT", "BTCEUR", "BTCUSDT", "ETHUSDT" }, listed.Select(i => i.Symbol));
        var btc = listed.Single(i => i.Symbol == "BTCUSDT");
        Assert.Equal(0.01m, btc.TickSize);
        Assert.Contains("USDT, USD quotes combined without conversion", btc.Description);
        Assert.Contains("non-executable", btc.Description);
        Assert.False(feed.GetCoverage(btc, AggregateOperation.Instruments).IsPartial);
        Assert.True(feed.GetCoverage(listed.Single(i => i.Symbol == "ETHUSDT"), AggregateOperation.Instruments).IsPartial);

        var sink = new Sink();
        using var live = feed.Subscribe(btc, sink);
        Assert.Equal("BTC-USDT-SWAP", okx.Subscribed?.Symbol);   // USDT preferred over USDC by default.
        Assert.Equal("BTC-USD", coinbase.Subscribed?.Symbol);    // The venue's own preferred quote wins.
        Assert.Equal("BTC", hyperliquid.Subscribed?.Symbol);
    }

    [Fact]
    public async Task A_same_ticker_different_asset_is_omitted_from_history_and_candles_and_reported_partial()
    {
        using var first = new Venue("first", "First", spot: false, 1m, ("ABCUSDT", "ABC", "USDT"));
        using var second = new Venue("second", "Second", spot: false, 1m, ("ABC-USD", "ABC", "USD"));
        using var wrong = new Venue("wrong", "Wrong", spot: false, 1m, ("ABC", "ABC", "USDT"));
        first.Trades.Add(new Trade(1, Start.AddSeconds(1), 100, 1, TradeSide.Buy));
        second.Trades.Add(new Trade(1, Start.AddSeconds(2), 101, 2, TradeSide.Sell));
        wrong.Trades.Add(new Trade(1, Start.AddSeconds(3), 3, 50, TradeSide.Buy));
        long t = new DateTimeOffset(Start).ToUnixTimeMilliseconds();
        first.Klines.Add(new Kline(t, 100, 101, 99, 100, 5, 0, false));
        second.Klines.Add(new Kline(t, 101, 102, 100, 102, 7, 0, false));
        wrong.Klines.Add(new Kline(t, 3, 4, 2, 3, 900, 0, false));
        using var feed = new AggregateFeed([first, second, wrong]);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));

        var trades = new List<Trade>();
        await feed.LoadTradesAsync(instrument, Start, Start.AddMinutes(1), null, b => { trades.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(new[] { "first", "second" }, trades.Select(x => x.Provenance.SourceIdentity));
        Assert.All(trades, x => Assert.True(x.Provenance.IsPartial));
        var status = feed.GetCoverage(instrument, AggregateOperation.Trades).Constituents.Single(c => c.FeedId == "wrong");
        Assert.True(status.IsPartial);
        Assert.Contains("probable different asset", status.Message);

        var candles = new List<Kline>();
        await feed.LoadKlinesAsync(instrument, Start, Start.AddMinutes(1), 1, 0, b => { candles.AddRange(b); return true; }, null, CancellationToken.None);
        var candle = Assert.Single(candles);
        Assert.Equal((12m, 102m, 99m), (candle.Volume, candle.High, candle.Low));
        Assert.Equal("aggregate:first+second", candle.Provenance.SourceIdentity);
        Assert.True((candle.Provenance.Quality & DataQuality.Partial) != 0);
    }

    [Fact]
    public async Task Live_prints_far_from_the_reference_venue_are_omitted_until_the_price_converges()
    {
        var clock = new ManualClock(Start);
        using var first = new Venue("first", "First", spot: false, 1m, ("ABCUSDT", "ABC", "USDT"));
        using var second = new Venue("second", "Second", spot: false, 1m, ("ABCUSDT", "ABC", "USDT"));
        using var feed = new AggregateFeed([first, second], timeProvider: clock);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using var live = feed.Subscribe(instrument, sink);

        first.Sink!.OnTrades(first.Subscribed!, [new Trade(1, Start.AddSeconds(1), 100, 1, TradeSide.Buy)]);
        second.Sink!.OnTrades(second.Subscribed!, [new Trade(1, Start.AddSeconds(2), 150, 1, TradeSide.Buy)]);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(new[] { 100m }, sink.Trades.Select(x => x.Price));
        var rejected = feed.GetCoverage(instrument, AggregateOperation.Live).Constituents[1];
        Assert.True(rejected.IsPartial);
        Assert.Contains("probable different asset", rejected.Message);
        second.Sink.OnStatus(second.Subscribed!, FeedStatus.Connected, null);
        Assert.True(feed.GetCoverage(instrument, AggregateOperation.Live).Constituents[1].IsPartial);

        second.Sink.OnTrades(second.Subscribed!, [new Trade(2, Start.AddSeconds(3), 101, 2, TradeSide.Sell)]);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(new[] { 100m, 101m }, sink.Trades.Select(x => x.Price));
    }

    [Fact]
    public async Task Venues_keep_streaming_through_a_move_while_the_reference_venue_reconnects()
    {
        var clock = new ManualClock(Start);
        using var first = new Venue("first", "First", spot: false, 1m, ("ABCUSDT", "ABC", "USDT"));
        using var second = new Venue("second", "Second", spot: false, 1m, ("ABCUSDT", "ABC", "USDT"));
        using var feed = new AggregateFeed([first, second], timeProvider: clock);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using var live = feed.Subscribe(instrument, sink);
        first.Sink!.OnTrades(first.Subscribed!, [new Trade(1, Start.AddSeconds(1), 100, 1, TradeSide.Buy)]);
        second.Sink!.OnTrades(second.Subscribed!, [new Trade(1, Start.AddSeconds(1), 100, 1, TradeSide.Sell)]);

        // The reference venue drops out, then the market moves 10 %: its last price must not reject the other venue.
        first.Sink.OnStatus(first.Subscribed!, FeedStatus.Reconnecting, "fixture outage");
        second.Sink.OnTrades(second.Subscribed!, [new Trade(2, Start.AddSeconds(2), 110, 1, TradeSide.Buy)]);
        clock.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal(new[] { 100m, 100m, 110m }, sink.Trades.Select(x => x.Price));
        Assert.DoesNotContain("probable different asset", feed.GetCoverage(instrument, AggregateOperation.Live).Constituents[1].Message ?? "");
    }

    [Fact]
    public async Task A_quiet_reference_venue_stops_judging_the_others_after_the_freshness_window()
    {
        var clock = new ManualClock(Start);
        using var first = new Venue("first", "First", spot: false, 1m, ("ABCUSDT", "ABC", "USDT"));
        using var second = new Venue("second", "Second", spot: false, 1m, ("ABCUSDT", "ABC", "USDT"));
        using var feed = new AggregateFeed([first, second], timeProvider: clock);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using var live = feed.Subscribe(instrument, sink);
        first.Sink!.OnTrades(first.Subscribed!, [new Trade(1, Start.AddSeconds(1), 100, 1, TradeSide.Buy)]);
        clock.Advance(AggregateFeed.ReferenceFreshness + TimeSpan.FromSeconds(1));

        second.Sink!.OnTrades(second.Subscribed!, [new Trade(1, Start.AddSeconds(20), 110, 1, TradeSide.Sell)]);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(new[] { 100m, 110m }, sink.Trades.Select(x => x.Price));

        // Once the first venue prints again it is the reference, and a fresh far print is still omitted.
        first.Sink.OnTrades(first.Subscribed!, [new Trade(2, Start.AddSeconds(21), 110, 1, TradeSide.Buy)]);
        second.Sink.OnTrades(second.Subscribed!, [new Trade(2, Start.AddSeconds(22), 150, 1, TradeSide.Buy)]);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.Equal(new[] { 100m, 110m, 110m }, sink.Trades.Select(x => x.Price));
    }

    [Fact]
    public async Task A_converged_venue_is_no_longer_partial_and_later_prints_are_not_marked_partial()
    {
        var clock = new ManualClock(Start);
        using var first = new Venue("first", "First", spot: false, 1m, ("ABCUSDT", "ABC", "USDT"));
        using var second = new Venue("second", "Second", spot: false, 1m, ("ABCUSDT", "ABC", "USDT"));
        using var feed = new AggregateFeed([first, second], timeProvider: clock);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using var live = feed.Subscribe(instrument, sink);
        first.Sink!.OnStatus(first.Subscribed!, FeedStatus.Connected, null);
        second.Sink!.OnStatus(second.Subscribed!, FeedStatus.Connected, null);
        first.Sink.OnTrades(first.Subscribed!, [new Trade(1, Start.AddSeconds(1), 100, 1, TradeSide.Buy)]);
        second.Sink.OnTrades(second.Subscribed!, [new Trade(1, Start.AddSeconds(2), 150, 1, TradeSide.Buy)]);
        Assert.True(feed.GetCoverage(instrument, AggregateOperation.Live).Constituents[1].IsPartial);

        second.Sink.OnTrades(second.Subscribed!, [new Trade(2, Start.AddSeconds(3), 101, 1, TradeSide.Sell)]);
        clock.Advance(TimeSpan.FromMilliseconds(250));

        var coverage = feed.GetCoverage(instrument, AggregateOperation.Live);
        Assert.False(coverage.IsPartial);
        Assert.Equal(new[] { 100m, 101m }, sink.Trades.Select(x => x.Price));
        Assert.False(sink.Trades.Last().Provenance.IsPartial);
    }

    [Fact]
    public async Task Folded_usd_quote_contracts_stay_reachable_under_their_pre_fold_aggregate_symbols()
    {
        using var binance = new Venue("binance-futures", "Binance Futures", spot: false, 0.1m,
            ("BTCUSDT", "BTC", "USDT"), ("BTCUSDC", "BTC", "USDC"), ("XYZUSDC", "XYZ", "USDC"));
        using var bybit = new Venue("bybit-linear", "Bybit", spot: false, 0.1m, ("BTCUSDT", "BTC", "USDT"), ("BTCPERP", "BTC", "USDC"));
        using var feed = new AggregateFeed([binance, bybit]);

        var listed = await feed.GetInstrumentsAsync(CancellationToken.None);

        // BTCUSDT combines both venues' USDT contracts; the USDC contracts saved before the fold keep their symbols.
        Assert.Equal(new[] { "BTCPERP", "BTCUSDC", "BTCUSDT", "XYZUSDC", "XYZUSDT" }, listed.Select(i => i.Symbol));
        var sink = new Sink();
        using (feed.Subscribe(listed.Single(i => i.Symbol == "BTCUSDC"), sink))
        {
            Assert.Equal("BTCUSDC", binance.Subscribed?.Symbol);
            Assert.Null(bybit.Subscribed);
        }
        // A saved reference resolves without the picker: the symbol alone is enough.
        var saved = new Instrument { Symbol = "BTCPERP", FeedId = AggregateFeed.FeedId, TickSize = 0.1m, VolumeStep = 0.001m };
        using (feed.Subscribe(saved, sink)) Assert.Equal("BTCPERP", bybit.Subscribed?.Symbol);
        using (feed.Subscribe(listed.Single(i => i.Symbol == "XYZUSDC"), sink)) Assert.Equal("XYZUSDC", binance.Subscribed?.Symbol);
    }

    [Fact]
    public async Task Composite_book_sums_remapped_venue_levels_discloses_missing_venues_and_never_keeps_a_stale_book()
    {
        var clock = new ManualClock(Start);
        using var first = new Venue("first", "First", spot: false, 0.5m, ("BTCUSDT", "BTC", "USDT"));
        using var second = new Venue("second", "Second", spot: false, 0.1m, ("BTC-USD", "BTC", "USD"));
        using var feed = new AggregateFeed([first, second], timeProvider: clock, bookMode: AggregateBookMode.Consolidated);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        Assert.Equal((AggregateFeed.FeedId, true), (feed.BookFeedId, feed.IsBookConsolidated));
        Assert.Contains("composite of available venue books", instrument.Description);
        var sink = new Sink();
        using var live = feed.Subscribe(instrument, sink);
        Assert.True(feed.GetCoverage(instrument, AggregateOperation.Live).IsBookConsolidated);

        // First: 100.0 bid / 100.5 ask in 0.5 ticks; second: 100.0 and 99.9 bids, 100.5 ask in 0.1 ticks.
        first.Sink!.OnDepth(first.Subscribed!, Book(first.Subscribed!, [(100m, 1m)], [(100.5m, 2m)]));
        var one = Assert.Single(sink.Depth);
        Assert.True(one.IsUsable);
        Assert.Contains("1 of 2", one.Message);
        Assert.Contains("missing: Second", one.Message);
        Assert.True(one.Coverage.HasFlag(DepthCoverage.PartialBook));
        Assert.Equal(AggregateFeed.FeedId, one.ProviderId);

        second.Sink!.OnDepth(second.Subscribed!, Book(second.Subscribed!, [(100m, 3m), (99.9m, 4m)], [(100.5m, 5m)]));
        Assert.Single(sink.Depth); // Throttled to one composite publication per 200 ms.
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var both = sink.Depth.Last();
        Assert.Equal(new[] { (instrument.ToTicks(100m), 4m), (instrument.ToTicks(99.9m), 4m) }, both.Snapshot.Bids.Select(l => (l.PriceTicks, l.Size)));
        Assert.Equal((instrument.ToTicks(100.5m), 7m), (both.Snapshot.Asks[0].PriceTicks, both.Snapshot.Asks[0].Size));
        Assert.Contains("2 of 2", both.Message);

        second.Sink.OnStatus(second.Subscribed!, FeedStatus.Reconnecting, "fixture outage");
        var afterOutage = sink.Depth.Last();
        Assert.Equal(1m, afterOutage.Snapshot.Bids[0].Size);
        Assert.Contains("missing: Second", afterOutage.Message);
        first.Sink.OnDepth(first.Subscribed!, new DepthUpdate { Snapshot = DepthSnapshot.Empty, Coverage = DepthCoverage.Missing | DepthCoverage.Disconnected });
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.False(sink.Depth.Last().IsUsable);
        Assert.Contains("no venue", sink.Depth.Last().Message);
    }

    [Fact]
    public void Merged_books_keep_crossed_venue_quotes_visible_instead_of_hiding_them()
    {
        var merged = AggregateFeed.MergeBooks([
            new DepthSnapshot([new BookLevel(101, 1)], [new BookLevel(102, 1)], Start),
            new DepthSnapshot([new BookLevel(103, 2)], [new BookLevel(104, 1)], Start),
        ], Start);
        Assert.Equal(new long[] { 103, 101 }, merged.Bids.Select(l => l.PriceTicks));
        Assert.True(merged.BestBidTicks > merged.BestAskTicks);
    }

    [Fact]
    public void Cvd_by_market_splits_attributed_delta_per_market_exchange_and_market_type_and_keeps_candle_history_unattributed()
    {
        using var binance = new Venue("binance-futures", "Binance Futures", spot: false, 0.1m, ("BTCUSDT", "BTC", "USDT")) { VenueName = "Binance" };
        using var bybit = new Venue("bybit-linear", "Bybit", spot: false, 0.1m, ("BTCUSDT", "BTC", "USDT")) { VenueName = "Bybit" };
        using var spot = new Venue("binance-spot", "Binance Spot", spot: true, 0.1m, ("BTCUSDT", "BTC", "USDT")) { VenueName = "Binance" };
        using var feed = new AggregateFeed([binance, bybit, spot]);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = AggregateFeed.FeedId, TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        long id = 1;
        void Add(DateTime time, string source, decimal volume, TradeSide side, DataQuality quality = DataQuality.Raw) =>
            data.Trades.Add(new Trade(quality.HasFlag(DataQuality.Synthetic) ? -id++ : id++, time, 70000, volume, side,
                new TradeProvenance(quality, SourceIdentity: source, SourceEventId: (id).ToString())));
        Add(Start.AddSeconds(1), "binance-futures", 3, TradeSide.Buy);
        Add(Start.AddSeconds(2), "bybit-linear", 1, TradeSide.Sell);
        Add(Start.AddSeconds(3), "binance-spot", 2, TradeSide.Sell);
        Add(Start.AddSeconds(4), "aggregate:binance-futures+bybit-linear", 5, TradeSide.Buy, DataQuality.Synthetic | DataQuality.InferredSide);
        Add(Start.AddMinutes(1).AddSeconds(1), "bybit-linear", 4, TradeSide.Buy);
        using var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
        var cvd = new CvdByMarket();
        Assert.Null(model.AddIndicator(cvd, applyDefaultColors: false).Error);

        Assert.Equal(new[] { "Binance Futures", "Bybit", "Binance Spot" }, cvd.LaneNames);
        decimal Lane(string series, int bar) => ((ValueDataSeries)cvd.DataSeries.Single(s => s.Id == series))[bar];
        Assert.Equal((3m, -1m, -2m), (Lane("M0", 0), Lane("M1", 0), Lane("M2", 0)));
        Assert.Equal((3m, 3m, -2m), (Lane("M0", 1), Lane("M1", 1), Lane("M2", 1)));
        Assert.Equal((5m, 5m), (Lane("UNATTRIBUTED", 0), Lane("UNATTRIBUTED", 1)));
        Assert.Equal((5m, 9m), (Lane("AGG", 0), Lane("AGG", 1)));
        Assert.Equal(Lane("AGG", 1), Lane("M0", 1) + Lane("M1", 1) + Lane("M2", 1) + Lane("UNATTRIBUTED", 1));

        cvd.Grouping = CvdByMarket.CvdGrouping.Exchange;
        model.Update();
        Assert.Equal(new[] { "Binance", "Bybit" }, cvd.LaneNames);
        Assert.Equal((1m, 3m), (Lane("M0", 1), Lane("M1", 1)));
        cvd.Grouping = CvdByMarket.CvdGrouping.SpotVersusPerpetual;
        model.Update();
        Assert.Equal(new[] { "Perpetual", "Spot" }, cvd.LaneNames);
        Assert.Equal((6m, -2m), (Lane("M0", 1), Lane("M1", 1)));
    }

    private static DepthUpdate Book(Instrument instrument, (decimal Price, decimal Size)[] bids, (decimal Price, decimal Size)[] asks) => new()
    {
        Snapshot = new DepthSnapshot(bids.Select(b => new BookLevel(instrument.ToTicks(b.Price), b.Size)).ToArray(),
            asks.Select(a => new BookLevel(instrument.ToTicks(a.Price), a.Size)).ToArray(), Start),
        IsFullRefresh = true,
        Coverage = DepthCoverage.ReceivedChanges,
    };

    private sealed class Sink : IMarketDataSink
    {
        public List<Trade> Trades { get; } = new();
        public List<DepthUpdate> Depth { get; } = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) => Trades.AddRange(trades);
        public void OnDepth(Instrument instrument, DepthUpdate update) => Depth.Add(update);
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }

    private sealed class ManualClock(DateTime start) : TimeProvider
    {
        private readonly List<Timer> _timers = new();
        private DateTimeOffset _now = new(start);
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan by)
        {
            _now += by;
            _timestamp += by.Ticks;
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        private sealed class Timer(TimerCallback callback, object? state, TimeSpan period) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed && period > TimeSpan.Zero) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
        }
    }

    private sealed class Venue(string id, string name, bool spot, decimal tick, params (string Symbol, string Base, string Quote)[] symbols)
        : IMarketDataFeed, IKlineHistory, ICryptoVenue
    {
        public string Id => id;
        public string DisplayName { get; } = name;
        public string VenueName { get; init; } = name;
        string ICryptoVenue.Venue => VenueName;
        public bool IsSpot => spot;
        public string Preferred { get; init; } = "USDT";
        string ICryptoVenue.PreferredQuote => Preferred;
        public bool SupportsHistory => true;
        public bool IsLive => true;
        public List<Trade> Trades { get; } = new();
        public List<Kline> Klines { get; } = new();
        public IMarketDataSink? Sink { get; private set; }
        public Instrument? Subscribed { get; private set; }

        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(symbols
            .Select(s => new Instrument { Symbol = s.Symbol, FeedId = id, TickSize = tick, VolumeStep = 0.001m, BaseAsset = s.Base, QuoteAsset = s.Quote }).ToArray());

        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            onBatch(Trades.Where(t => t.Time >= fromUtc && t.Time <= toUtc).ToArray());
            return Task.CompletedTask;
        }

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            onBatch(Klines.ToArray());
            return Task.CompletedTask;
        }

        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(Start);

        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            Sink = sink;
            Subscribed = instrument;
            return new Subscription();
        }

        public void Dispose() { }
        private sealed class Subscription : IDisposable { public void Dispose() { } }
    }
}
