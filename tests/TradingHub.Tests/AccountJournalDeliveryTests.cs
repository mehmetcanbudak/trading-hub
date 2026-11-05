using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed class AccountJournalDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-journal-delivery-" + Guid.NewGuid().ToString("N"));
    private static readonly AccountFill Fill = new("native-execution-1", FakeBybitProtocol.Now.UtcDateTime, "BTCUSDT", false, 1, 110, 1, .05m, "exchange-1");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Failed_append_is_not_an_in_memory_duplicate_and_retries_as_a_committed_fill()
    {
        var journal = new AccountJournal(_root);
        string path = Path.Combine(_root, "account.jsonl");
        Directory.CreateDirectory(path);
        int changed = 0;
        journal.Changed += _ => changed++;
        var failure = Record.Exception(() => journal.Add("account", Fill));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(0, journal.CountOf("account"));
        Assert.Empty(journal.FillsOf("account"));
        Assert.Equal(0, changed);
        Directory.Delete(path);
        Assert.True(journal.Add("account", Fill));
        Assert.False(journal.Add("account", Fill));
        Assert.Equal(Fill, Assert.Single(journal.FillsOf("account")));
        Assert.Equal(Fill, Assert.Single(new AccountJournal(_root).FillsOf("account")));
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_write_or_uncertain_flush_can_retry_without_losing_the_fill_or_legacy_prefix(bool failFlush)
    {
        var prefix = Fill with { Id = "previous", Time = Fill.Time.AddMinutes(-1), Price = 100 };
        Assert.True(new AccountJournal(_root).Add("account", prefix));
        int opens = 0;
        var journal = new AccountJournal(_root, path =>
        {
            var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            return Interlocked.Increment(ref opens) == 1 ? new FailedAppend(file, failFlush) : file;
        });
        Assert.Throws<IOException>(() => journal.Add("account", Fill));
        Assert.Equal(prefix, Assert.Single(journal.FillsOf("account")));
        Assert.True(journal.Add("account", Fill));
        Assert.False(journal.Add("account", Fill));
        Assert.Equal(new[] { prefix, Fill }, journal.FillsOf("account"));
        Assert.Equal(new[] { prefix, Fill }, new AccountJournal(_root).FillsOf("account"));
    }

    [Fact]
    public async Task Concurrent_duplicate_delivery_commits_once_and_preserves_logical_order()
    {
        var journal = new AccountJournal(_root);
        var accepted = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => journal.Add("account", Fill))));
        Assert.Equal(1, accepted.Count(value => value));
        var second = Fill with { Id = "native-execution-2", Time = Fill.Time.AddSeconds(1), Price = 111 };
        Assert.True(journal.Add("account", second));
        Assert.Equal(new[] { Fill, second }, new AccountJournal(_root).FillsOf("account"));
    }

    [Fact]
    public async Task Actual_Bybit_publication_cannot_become_idle_until_the_failed_journal_delivery_is_committed()
    {
        string journalRoot = Path.Combine(_root, "journal");
        var journal = new AccountJournal(journalRoot);
        string blocked = Path.Combine(journalRoot, "account.jsonl");
        Directory.CreateDirectory(blocked);
        using var protocol = new FakeBybitProtocol { NetPosition = 1 };
        using var http = new HttpClient(protocol);
        await using var session = new BybitTradingSession(protocol.Settings, Path.Combine(_root, "provider", "context.json"), http,
            clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, PointValue = 1 };
        using var data = new InstrumentData(instrument, new SimulatedFeed());
        data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime, 110, 1, TradeSide.Buy));
        using var broker = new BybitBroker(data, session, "Fake Bybit");
        session.FillReceived += fill => journal.Add("account", new AccountFill(fill.Id, fill.Time, fill.Symbol, fill.Buy,
            fill.Quantity, fill.Price, 1, fill.Fee ?? 0, fill.OrderId));
        await session.RefreshAsync();
        var exit = broker.Exit(OrderTypes.Limit, OrderDirections.Sell, 1, 110);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (exit.ExchangeOrderId.Length == 0 || !session.OrdersReady)
        {
            await session.RefreshAsync(deadline.Token);
            await Task.Delay(1, deadline.Token);
        }
        protocol.Fill("delivered-after-retry", 1, 110, .05m, closedQuantity: 1);
        protocol.NetPosition = 0;
        await session.RefreshAsync();
        Assert.False(session.ExecutionsReady);
        Assert.Equal(0, journal.CountOf("account"));
        Assert.False(session.TryBeginIdleShutdown(out _));
        await session.RefreshAsync();
        Assert.False(session.ExecutionsReady); // An in-memory duplicate is not a successful delivery acknowledgment.
        Assert.False(session.TryBeginIdleShutdown(out _));
        Directory.Delete(blocked);
        Task? drain = null;
        while (drain == null)
        {
            await session.RefreshAsync(deadline.Token);
            if (session.TryBeginIdleShutdown(out var completion)) drain = completion;
            else await Task.Delay(1, deadline.Token);
        }
        await drain.WaitAsync(deadline.Token);
        var committed = Assert.Single(new AccountJournal(journalRoot).FillsOf("account"));
        Assert.Equal("delivered-after-retry", committed.Id);
        Assert.Equal(110m, committed.Price);
        Assert.Equal(.05m, committed.Commission);
        Assert.Equal(FakeBybitProtocol.Now.UtcDateTime, committed.Time);
    }

    private sealed class FailedAppend(FileStream inner, bool failFlush) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (!failFlush)
            {
                inner.Write(buffer[..Math.Min(17, buffer.Length)]);
                throw new IOException("Fake partial append failure");
            }
            inner.Write(buffer);
        }
        public override void Flush() => throw new IOException("Fake durable flush failure");
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
