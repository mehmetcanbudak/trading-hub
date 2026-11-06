using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class IbSnapshotHeaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-ib-snapshot-header-" + Guid.NewGuid().ToString("N"));

    public IbSnapshotHeaderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Real_ib_warmup_rejects_a_closed_raw_file_with_catalog_mismatched_tick_metadata_without_charting_its_prices()
    {
        var instrument = IbBarImport.InstrumentFor("MNQ");
        var wrongMetadata = new Instrument { FeedId = "ib", Symbol = "MNQ", TickSize = 1m };
        var time = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(-2);
        string path = TickFile.DayPath(_root, "ib", "MNQ", DateOnly.FromDateTime(time));
        // A complete, closed VTK file, not a truncated append: MNQ's catalog tick is 0.25, not this header's 1.
        using (var writer = new TickFile.Writer(path, wrongMetadata))
            writer.Write(new Trade(101, time.AddSeconds(20), 421, 7, TradeSide.Buy));

        await using var gateway = new FakeIbGateway();
        using var feed = new IbFeed(new("127.0.0.1", gateway.Port, 31, Offline: false, ProfileId: "header-fixture"), _root)
        {
            RequestTimeout = TimeSpan.FromSeconds(2),
            TickRequestInterval = TimeSpan.Zero,
        };
        using var data = new InstrumentData(instrument, feed) { RecordingRoot = _root, RecordLive = false };
        data.Start(time);
        var session = await gateway.NextSessionAsync();
        using var responderStop = new CancellationTokenSource();
        Task responder = RespondToHistory();
        try
        {
            // The fake gateway returns no bars/ticks; only the invalid stored raw file could supply chart prices.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                data.Pump();
                if (!data.IsLoadingHistory) break;
                await Task.Delay(10, watchdog.Token);
            }
            using var chart = new ChartModel(data, ChartPeriod.M1);
            Assert.NotNull(data.HistoryError);
            Assert.Equal(0, data.Trades.Count);
            Assert.Equal(0, chart.Series.Count);
        }
        finally
        {
            responderStop.Cancel();
            await responder.WaitAsync(TimeSpan.FromSeconds(30));
        }

        async Task RespondToHistory()
        {
            try
            {
                while (true)
                {
                    var request = await session.Requests.Reader.ReadAsync(responderStop.Token);
                    if (request[0] == "20") await session.SendAsync(false, "17", request[1], "from", "to", "0");
                }
            }
            catch (OperationCanceledException) when (responderStop.IsCancellationRequested) { }
        }
    }
}
