using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Longbridge;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class LongbridgeFeedTests
{
    [Fact]
    public async Task ConfiguredMetadataUsesProviderBoardLotEnglishNameAndDstWithoutPriceGuesses()
    {
        var transport = new Transport();
        var settings = Settings();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        using var feed = new LongbridgeFeed(settings, Session(settings, http), transport, feedId: "longbridge-profile");
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        Assert.Equal("longbridge-profile", instrument.FeedId);
        Assert.Equal("Apple Inc.", instrument.Description);
        Assert.Equal(100m, instrument.VolumeStep);
        Assert.Equal(.01m, instrument.TickSize);
        Assert.Equal("NASDAQ", instrument.Exchange);
        Assert.Equal("USD", instrument.QuoteAsset);
        Assert.Equal(new DateTime(2026, 11, 4, 14, 30, 0, DateTimeKind.Utc), feed.GetSessionSettings(instrument).SessionStartUtc(new(2026, 11, 4)));
        Assert.Equal(new DateTime(2026, 7, 1, 13, 30, 0, DateTimeKind.Utc), feed.GetSessionSettings(instrument).SessionStartUtc(new(2026, 7, 1)));
        var unknownCalendar = feed.GetSessions(instrument, new(2026, 11, 4));
        Assert.False(unknownCalendar.CalendarConfirmed);
        Assert.Single(unknownCalendar.Windows);
        Assert.Empty(feed.GetSessions(instrument, new(2026, 11, 7)).Windows);
        var wrongLot = new Instrument { Symbol = instrument.Symbol, FeedId = instrument.FeedId, TickSize = .01m, VolumeStep = 1,
            Exchange = instrument.Exchange, QuoteAsset = instrument.QuoteAsset };
        await Assert.ThrowsAsync<ArgumentException>(() => feed.GetQuoteAsync(wrongLot, default));
    }

    [Fact]
    public async Task MissingTickMetadataAndMissingLotAreExplicitErrorsNotGuessedInstruments()
    {
        var settings = Settings();
        settings.Instruments["AAPL.US"].TickSize = 0;
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        Assert.Throws<InvalidOperationException>(() => new LongbridgeFeed(settings, Session(settings, http), new Transport()));
        settings = Settings();
        var transport = new Transport { LotSize = null };
        using var feed = new LongbridgeFeed(settings, Session(settings, http), transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => feed.GetInstrumentsAsync(default));
    }

    [Fact]
    public async Task QuotesNeverBecomeObservedTradesAndRecentTapeIsPartialWithInferredDirection()
    {
        var settings = Settings(); var transport = new Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        using var feed = new LongbridgeFeed(settings, Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        var sink = new Sink();
        using var subscription = feed.Subscribe(instrument, sink);
        var quote = await feed.GetQuoteAsync(instrument, default);
        Assert.Equal(201.25m, quote.LastPrice);
        Assert.Null(quote.Turnover);
        transport.Emit(LongbridgeCommand.PushQuote, Message(w => { w.Text(1, "AAPL.US"); w.Number(2, 50); w.Text(3, "202.10"); w.Number(7, 1793793600); w.Number(8, 5000); w.Number(12, 15); }));
        Assert.Empty(sink.Trades);
        var tape = await feed.GetRecentTradesAsync(instrument, default);
        Assert.False(tape.Coverage.Complete);
        Assert.False(tape.HasProviderTradeIds);
        Assert.Equal(new[] { 200.5m, 201m }, tape.Trades.Select(t => t.Price));
        Assert.Equal(new[] { TradeSide.None, TradeSide.Buy }, tape.Trades.Select(t => t.Side));
        Assert.All(tape.Trades, trade =>
        {
            Assert.Equal(DataQuality.Raw | DataQuality.Partial | DataQuality.InferredSide, trade.Provenance.Quality);
            Assert.Contains("recent-tape", trade.Provenance.SourceIdentity!);
        });
        await Assert.ThrowsAsync<NotSupportedException>(() => feed.LoadTradesAsync(instrument, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow,
            123, _ => true, null, default));
    }

    [Fact]
    public async Task ZeroVolumeCandlesStayZeroAndSyntheticHistoryNeverPublishesTapeOrFutureExtremes()
    {
        var settings = Settings(); var transport = new Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        using var feed = new LongbridgeFeed(settings, Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        var start = DateTimeOffset.FromUnixTimeSeconds(1793793600).UtcDateTime;
        var bars = new List<Kline>();
        await feed.LoadKlinesAsync(instrument, start, start.AddMinutes(3), 1, 0, batch => { bars.AddRange(batch); return true; }, null, default);
        Assert.Equal(2, bars.Count);
        Assert.Equal(0m, bars[0].Volume);
        Assert.False(bars[1].HasBuyVolume);
        Assert.Equal(DataQuality.Synthetic | DataQuality.Partial, bars[1].Provenance.Quality);
        var synthetic = await feed.LoadBarHistoryAsync(instrument, start, start.AddMinutes(3), default);
        Assert.NotEmpty(synthetic);
        Assert.All(synthetic, t => { Assert.True(t.Id < 0); Assert.False(t.IsObservedTrade); });
        await Assert.ThrowsAsync<NotSupportedException>(() => feed.LoadKlinesAsync(instrument, start, start.AddHours(1), 7, 0, _ => true, null, default));
        Assert.Null(await feed.FirstAvailableAsync(instrument, default));
    }

    [Fact]
    public async Task HistoryPaginationOverlapsLastOpenRatherThanSkippingTheNextBar()
    {
        var settings = Settings(); var transport = new Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        using var feed = new LongbridgeFeed(settings, Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        int requests = 0;
        transport.Override = (command, body, _) =>
        {
            Assert.Equal((byte)LongbridgeCommand.HistoryCandles, command);
            requests++;
            if (requests == 2)
            {
                byte[] expected = Message(w =>
                {
                    w.Text(1, "AAPL.US"); w.Number(2, 1); w.Number(3, 0); w.Number(4, 1);
                    w.Bytes(5, Message(offset => { offset.Number(1, 1); offset.Text(2, "20261104"); offset.Text(3, "2339"); offset.Number(4, 1000); }));
                    w.Number(7, 0);
                });
                Assert.Equal(expected, body.ToArray());
            }
            int first = requests == 1 ? 0 : 1000, count = requests == 1 ? 1000 : 2;
            return Task.FromResult(Message(w =>
            {
                w.Text(1, "AAPL.US");
                for (int i = first; i < first + count; i++)
                {
                    int index = i;
                    w.Bytes(2, Message(c => { c.Text(1, "201"); c.Text(2, "200"); c.Text(3, "199"); c.Text(4, "202"); c.Number(5, 1); c.Number(7, 1793793600 + index * 60); }));
                }
            }));
        };
        var from = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var bars = new List<Kline>();
        await feed.LoadKlinesAsync(instrument, from, from.AddDays(1), 1, 0, batch => { bars.AddRange(batch); return true; }, null, default);
        Assert.Equal(2, requests);
        Assert.Equal(1002, bars.Count);
        Assert.Equal(1793853600000L, bars[1000].OpenTimeMs);
    }

    [Fact]
    public async Task DepthPreservesReceivedPositionsRemovalsAndSourceCoverageWithoutInventingFullBook()
    {
        var settings = Settings(); var transport = new Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        using var feed = new LongbridgeFeed(settings, Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        var snapshot = await feed.GetDepthAsync(instrument, default);
        Assert.Equal(DepthCoverage.SnapshotOnly | DepthCoverage.PartialBook, snapshot.Coverage);
        Assert.Equal("NASDAQ", snapshot.Venue);
        Assert.Null(snapshot.Sequence);
        var sink = new Sink(); using var subscription = feed.Subscribe(instrument, sink);
        transport.Emit(LongbridgeCommand.PushDepth, DepthPush(100, (1, "202.00", 20), (2, "202.50", 30)));
        transport.Emit(LongbridgeCommand.PushDepth, DepthPush(120, (1, "202.10", 25)));
        var changed = sink.Depth.Last();
        Assert.Equal(new[] { 20210L, 20250L }, changed.Snapshot.Asks.Select(l => l.PriceTicks));
        Assert.Equal(DepthCoverage.ReceivedChanges | DepthCoverage.PartialBook, changed.Coverage);
        Assert.Equal(DepthGapReason.None, changed.GapBefore); // The provider does not promise contiguous sequence numbers.
        Assert.Contains(changed.Changes, c => c.PriceTicks == 20200 && c.Size == 0);
        Assert.All(changed.Changes, change => Assert.Equal(BookChangeOrigin.DerivedSnapshotDifference, change.Origin));
        transport.Emit(LongbridgeCommand.PushDepth, DepthPush(121, (2, "", 0)));
        Assert.Single(sink.Depth.Last().Snapshot.Asks);
        transport.Emit(LongbridgeCommand.PushDepth, DepthPush(119, (1, "100.00", 999)));
        var rejected = Assert.Single(sink.Depth, update => update.Coverage.HasFlag(DepthCoverage.OutOfOrder));
        Assert.Equal(DepthGapReason.Sequence, rejected.GapBefore);
        Assert.DoesNotContain(rejected.Snapshot.Asks, level => level.PriceTicks == 10000);
        Assert.True(sink.Depth.Last().IsFullRefresh);
        transport.Disconnect();
        Assert.True(sink.Depth.Last().Coverage.HasFlag(DepthCoverage.Disconnected));
        Assert.Equal(DepthGapReason.Disconnected, sink.Depth.Last().GapBefore);
        transport.Emit(LongbridgeCommand.PushDepth, DepthPush(1, (1, "202.20", 5)));
        Assert.True(sink.Depth.Last().Coverage.HasFlag(DepthCoverage.Disconnected));
        transport.Connect();
        Assert.True(sink.Depth.Last().IsFullRefresh);
        Assert.Equal(DepthGapReason.Disconnected, sink.Depth.Last().GapBefore);
        Assert.Empty(sink.Depth.Last().Changes);
        transport.Emit(LongbridgeCommand.PushDepth, DepthPush(2, (1, "202.20", 5)));
        Assert.Equal(new BookLevel(20220, 5), Assert.Single(sink.Depth.Last().Snapshot.Asks));
        Assert.False(sink.Depth.Last().IsFullRefresh);
    }

    [Fact]
    public async Task DisposedSubscriptionReceivesNoLatePushAndCandlePaginationRejectsNonprogress()
    {
        var settings = Settings(); var transport = new Transport { RepeatFullPages = true };
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        using var feed = new LongbridgeFeed(settings, Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        var sink = new Sink(); var subscription = feed.Subscribe(instrument, sink); subscription.Dispose();
        transport.Emit(LongbridgeCommand.PushTrades, Message(w => { w.Text(1, "AAPL.US"); w.Number(2, 1); w.Bytes(3, Trade("201", 2, 1793793600, 1)); }));
        Assert.Empty(sink.Trades);
        var start = DateTimeOffset.FromUnixTimeSeconds(1793793600).UtcDateTime;
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.LoadKlinesAsync(instrument, start, start.AddDays(1), 1, 0, _ => true, null, default));
    }

    [Fact]
    public void ConfiguredAsianSessionsPreserveLunchBreaksAndExplicitHolidayOverrides()
    {
        var metadata = new LongbridgeInstrumentMetadata
        {
            TickSize = .001m, VolumeStep = 100, Exchange = "SEHK", Currency = "HKD", TimeZoneId = "Asia/Hong_Kong",
            RegularSessions = new() { new(new(9, 30), new(12, 0)), new(new(13, 0), new(16, 0)) },
        };
        var normal = metadata.GetSessions(new(2026, 11, 4));
        Assert.False(normal.CalendarConfirmed);
        Assert.Equal(new[]
        {
            new LongbridgeSessionWindow(new(2026, 11, 4, 1, 30, 0, DateTimeKind.Utc), new(2026, 11, 4, 4, 0, 0, DateTimeKind.Utc)),
            new LongbridgeSessionWindow(new(2026, 11, 4, 5, 0, 0, DateTimeKind.Utc), new(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc)),
        }, normal.Windows);
        metadata.SessionOverrides[new(2026, 11, 5)] = new() { new(new(9, 30), new(12, 0)) };
        metadata.SessionOverrides[new(2026, 11, 6)] = new();
        Assert.Single(metadata.GetSessions(new(2026, 11, 5)).Windows);
        Assert.True(metadata.GetSessions(new(2026, 11, 5)).CalendarConfirmed);
        Assert.Empty(metadata.GetSessions(new(2026, 11, 6)).Windows);
        Assert.True(metadata.GetSessions(new(2026, 11, 6)).CalendarConfirmed);
    }

    [Fact]
    public async Task FormingCandleExtremesBecomeAvailableOnlyAfterTheBarCompletes()
    {
        var settings = Settings(); var transport = new Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        var clock = new LongbridgeOAuthTests.Clock { Utc = new(2026, 11, 4, 12, 0, 30, TimeSpan.Zero) };
        var session = new LongbridgeSession(settings, http, () => null, _ => { }, clock);
        using var feed = new LongbridgeFeed(settings, session, transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        var from = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var bars = new List<Kline>();
        await feed.LoadKlinesAsync(instrument, from, from.AddMinutes(3), 1, 0, batch => { bars.AddRange(batch); return true; }, null, default);
        Assert.Empty(bars);
        clock.Utc = clock.Utc.AddMinutes(2);
        await feed.LoadKlinesAsync(instrument, from, from.AddMinutes(3), 1, 0, batch => { bars.AddRange(batch); return true; }, null, default);
        Assert.Equal(2, bars.Count);
        Assert.Equal(202m, bars[1].High);
    }

    [Fact]
    public async Task RetiredContextCannotPublishAHeldResponseOrLaterTape()
    {
        var settings = Settings(); var transport = new Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        var session = Session(settings, http);
        using var feed = new LongbridgeFeed(settings, session, transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        var sink = new Sink(); using var subscription = feed.Subscribe(instrument, sink);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Override = (_, _, _) => { entered.TrySetResult(); return response.Task; };
        var quote = feed.GetQuoteAsync(instrument, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Retire();
        response.SetResult(Message(w => w.Bytes(1, Message(q => { q.Text(1, "AAPL.US"); q.Text(2, "201"); }))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => quote);
        transport.Emit(LongbridgeCommand.PushTrades, Message(w => { w.Text(1, "AAPL.US"); w.Number(2, 1); w.Bytes(3, Trade("201", 2, 1793793600, 1)); }));
        Assert.Empty(sink.Trades);
    }

    [Fact]
    public async Task MissingMeasurementsRemainUnknownAndOffGridTapeIsRejectedRatherThanRounded()
    {
        var settings = Settings(); var transport = new Transport();
        using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
        using var feed = new LongbridgeFeed(settings, Session(settings, http), transport);
        var instrument = Assert.Single(await feed.GetInstrumentsAsync(default));
        transport.Override = (_, _, _) => Task.FromResult(Message(w => w.Bytes(1, Message(quote =>
        { quote.Text(1, "AAPL.US"); quote.Text(2, ""); quote.Number(7, 0); }))));
        var quote = await feed.GetQuoteAsync(instrument, default);
        Assert.Null(quote.LastPrice);
        Assert.Null(quote.Timestamp);
        Assert.Null(quote.Volume);
        foreach (byte[] trade in new[]
        {
            Message(w => { w.Number(2, 4); w.Number(3, 1793793600); }),
            Trade("200.555", 4, 1793793600, 2),
        })
        {
            transport.Override = (_, _, _) => Task.FromResult(Message(w => { w.Text(1, "AAPL.US"); w.Bytes(2, trade); }));
            await Assert.ThrowsAsync<InvalidDataException>(() => feed.GetRecentTradesAsync(instrument, default));
        }
    }

    internal static LongbridgeSettings Settings() => new()
    {
        ClientId = "fixture-client", BaseUrl = "https://quote.invalid", QuoteWsUrl = "wss://quote.invalid/v2", Symbols = new() { "AAPL.US" },
        Instruments = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL.US"] = new() { TickSize = .01m, TimeZoneId = "America/New_York", RegularSessions = new() { new(new(9, 30), new(16, 0)) } },
        },
    };
    internal static LongbridgeSession Session(LongbridgeSettings settings, HttpClient http) => new(settings, http, () => null, _ => { },
        new LongbridgeOAuthTests.Clock { Utc = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero) });
    internal static byte[] Message(Action<Writer> build) { var writer = new Writer(); build(writer); return writer.Build(); }
    internal static byte[] Trade(string price, long volume, long timestamp, int direction) => Message(w => { w.Text(1, price); w.Number(2, volume); w.Number(3, timestamp); w.Number(5, direction); });
    internal static byte[] DepthPush(long sequence, params (int Position, string Price, long Volume)[] asks) => Message(w =>
    {
        w.Text(1, "AAPL.US"); w.Number(2, sequence);
        foreach (var item in asks) w.Bytes(3, Message(level => { level.Number(1, item.Position); if (item.Price.Length > 0) level.Text(2, item.Price); level.Number(3, item.Volume); }));
    });
    internal sealed class Writer
    {
        private readonly MemoryStream _bytes = new();
        private void Varint(ulong n) { while (n >= 128) { _bytes.WriteByte((byte)(n | 128)); n >>= 7; } _bytes.WriteByte((byte)n); }
        public void Number(int field, long n) { Varint((ulong)(field * 8)); Varint((ulong)n); }
        public void Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
        public void Bytes(int field, byte[] value) { Varint((ulong)(field * 8 + 2)); Varint((ulong)value.Length); _bytes.Write(value); }
        public byte[] Build() => _bytes.ToArray();
    }
    internal sealed class Transport : ILongbridgeTransport
    {
        public event Action<byte, ReadOnlyMemory<byte>>? Push;
        public event Action<FeedStatus, string?>? Status;
        public long? LotSize { get; set; } = 100;
        public bool RepeatFullPages { get; set; }
        public Func<byte, ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>>? Override { get; set; }
        public Task<byte[]> RequestAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Override != null) return Override(command, body, ct);
            return Task.FromResult((LongbridgeCommand)command switch
            {
                LongbridgeCommand.StaticInfo => Message(w => w.Bytes(1, Message(info =>
                { info.Text(1, "AAPL.US"); info.Text(2, "non-English name ignored"); info.Text(3, "Apple Inc."); info.Text(6, "NASDAQ"); info.Text(7, "USD"); if (LotSize.HasValue) info.Number(8, LotSize.Value); }))),
                LongbridgeCommand.Quote => Message(w => w.Bytes(1, Message(q => { q.Text(1, "AAPL.US"); q.Text(2, "201.25"); q.Number(7, 1793793600); q.Number(8, 1234); }))),
                LongbridgeCommand.Trades => Message(w => { w.Text(1, "AAPL.US"); w.Bytes(2, Trade("201", 7, 1793793601, 2)); w.Bytes(2, Trade("200.50", 4, 1793793600, 0)); }),
                LongbridgeCommand.HistoryCandles => Message(w =>
                {
                    w.Text(1, "AAPL.US");
                    for (int i = 0; i < (RepeatFullPages ? 500 : 1); i++)
                    {
                        w.Bytes(2, Candle(1793793600, 0)); w.Bytes(2, Candle(1793793660, 10));
                    }
                }),
                LongbridgeCommand.Depth => Message(w => { w.Text(1, "AAPL.US"); w.Bytes(2, Message(level => { level.Number(1, 1); level.Text(2, "202.00"); level.Number(3, 20); })); }),
                LongbridgeCommand.Subscribe or LongbridgeCommand.Unsubscribe => Array.Empty<byte>(),
                _ => throw new InvalidOperationException("Unexpected fake protocol command."),
            });
        }
        private static byte[] Candle(long time, long volume) => Message(w => { w.Text(1, "201"); w.Text(2, "200"); w.Text(3, "199"); w.Text(4, "202"); if (volume != 0) w.Number(5, volume); w.Number(7, time); });
        public void Emit(LongbridgeCommand command, byte[] body) => Push?.Invoke((byte)command, body);
        public void Disconnect() => Status?.Invoke(FeedStatus.Reconnecting, "Fixture interruption");
        public void Connect() => Status?.Invoke(FeedStatus.Connected, "Fixture connected");
        public void Dispose() { }
    }
    internal sealed class Sink : IMarketDataSink
    {
        public List<Trade> Trades { get; } = new();
        public List<DepthUpdate> Depth { get; } = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) => Trades.AddRange(trades);
        public void OnDepth(Instrument instrument, DepthUpdate update) => Depth.Add(update);
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }
}
