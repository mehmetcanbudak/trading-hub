using TradingHub.Data;
using TradingHub.Feeds.Longbridge;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class LongbridgeObservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecreatedFeedPreservesDistinctIdenticalPrintsThroughDurableCanonicalDedupe(bool live)
    {
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-longbridge-observations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = LongbridgeFeedTests.Settings();
            using var http = new HttpClient(new LongbridgeOAuthTests.HttpFixture());
            const string profile = "longbridge-persisted-profile";
            LongbridgeTapeResult initial;
            IReadOnlyList<Trade> initialTrades;
            Instrument instrument;
            var firstTransport = new IdenticalTapeTransport();
            using (var first = new LongbridgeFeed(settings, LongbridgeFeedTests.Session(settings, http), firstTransport, feedId: profile))
            {
                instrument = Assert.Single(await first.GetInstrumentsAsync(default));
                initial = await first.GetRecentTradesAsync(instrument, default);
                initialTrades = initial.Trades;
                if (live)
                {
                    var sink = new LongbridgeFeedTests.Sink();
                    using var lease = first.Subscribe(instrument, sink);
                    firstTransport.EmitIdenticalTape();
                    initialTrades = sink.Trades.ToArray();
                }
            }
            var day = DateOnly.FromDateTime(initialTrades[0].Time);
            HistoricalData.WriteDay(root, instrument, day, initialTrades);
            var originalIds = initialTrades.Select(trade => trade.Provenance.SourceEventId!).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(2, initialTrades.Count);
            Assert.Equal(2, originalIds.Count);

            LongbridgeTapeResult recreated;
            IReadOnlyList<Trade> recreatedTrades;
            var secondTransport = new IdenticalTapeTransport();
            using (var second = new LongbridgeFeed(settings, LongbridgeFeedTests.Session(settings, http), secondTransport, feedId: profile))
            {
                var sameInstrument = Assert.Single(await second.GetInstrumentsAsync(default));
                recreated = await second.GetRecentTradesAsync(sameInstrument, default);
                recreatedTrades = recreated.Trades;
                if (live)
                {
                    var sink = new LongbridgeFeedTests.Sink();
                    using var lease = second.Subscribe(sameInstrument, sink);
                    secondTransport.EmitIdenticalTape();
                    recreatedTrades = sink.Trades.ToArray();
                }
            }
            Assert.Equal(initialTrades.Select(trade => trade.Id), recreatedTrades.Select(trade => trade.Id));
            Assert.Equal(initialTrades.Select(trade => trade.Provenance.SourceIdentity), recreatedTrades.Select(trade => trade.Provenance.SourceIdentity));
            var observations = initialTrades.Concat(recreatedTrades).ToArray();
            Assert.All(observations, trade =>
            {
                Assert.Equal(initialTrades[0].Time, trade.Time);
                Assert.Equal(201m, trade.Price);
                Assert.Equal(7m, trade.Volume);
                Assert.Equal(DataQuality.Raw | DataQuality.Partial | DataQuality.InferredSide, trade.Provenance.Quality);
                Assert.StartsWith("observation:", trade.Provenance.SourceEventId!);
            });
            var identities = observations.Select(trade => trade.Provenance.SourceEventId!).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(4, identities.Count);
            Assert.False(initial.HasProviderTradeIds);
            Assert.False(recreated.HasProviderTradeIds);
            Assert.False(recreated.Coverage.Complete);
            Assert.Contains("overlap", recreated.Coverage.Message, StringComparison.OrdinalIgnoreCase);

            HistoricalData.WriteDay(root, instrument, day, recreatedTrades);
            // Replaying the same observation objects is idempotent; a new fetch is not claimed to be the same provider events.
            HistoricalData.WriteDay(root, instrument, day, observations);
            var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var persisted = HistoricalData.Read(root, profile, instrument.Symbol, from, from.AddDays(1)).ToArray();
            Assert.Equal(4, persisted.Length);
            Assert.Equal(28m, persisted.Sum(trade => trade.Volume));
            Assert.True(identities.SetEquals(persisted.Select(trade => trade.Provenance.SourceEventId!)));
            var store = new TradeStore(instrument);
            store.Replace(persisted);
            Assert.Equal(4, store.Count);
            Assert.True(identities.SetEquals(Enumerable.Range(0, store.Count).Select(index => store.GetProvenance(index).SourceEventId!)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private sealed class IdenticalTapeTransport : ILongbridgeTransport
    {
        private readonly LongbridgeFeedTests.Transport _metadata = new();
        public event Action<byte, ReadOnlyMemory<byte>>? Push
        {
            add => _metadata.Push += value;
            remove => _metadata.Push -= value;
        }
        public event Action<TradingHub.Feeds.FeedStatus, string?>? Status
        {
            add => _metadata.Status += value;
            remove => _metadata.Status -= value;
        }
        public Task<byte[]> RequestAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            if (command != (byte)LongbridgeCommand.Trades) return _metadata.RequestAsync(command, body, ct);
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(LongbridgeFeedTests.Message(writer =>
            {
                writer.Text(1, "AAPL.US");
                writer.Bytes(2, LongbridgeFeedTests.Trade("201.00", 7, 1793793600, 2));
                writer.Bytes(2, LongbridgeFeedTests.Trade("201.00", 7, 1793793600, 2));
            }));
        }
        public void EmitIdenticalTape() => _metadata.Emit(LongbridgeCommand.PushTrades, LongbridgeFeedTests.Message(writer =>
        {
            writer.Text(1, "AAPL.US");
            writer.Number(2, 100);
            writer.Bytes(3, LongbridgeFeedTests.Trade("201.00", 7, 1793793600, 2));
            writer.Bytes(3, LongbridgeFeedTests.Trade("201.00", 7, 1793793600, 2));
        }));
        public void Dispose() => _metadata.Dispose();
    }
}
