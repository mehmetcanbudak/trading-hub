using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class HistoryMutationOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "history-mutation-ownership-" + Guid.NewGuid().ToString("N"));
    private readonly Instrument _instrument = new() { FeedId = "rithmic", Symbol = "LEASE", TickSize = 1, VolumeStep = 1 };
    private static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = DateOnly.FromDateTime(Start);
    private string DayPath(DateOnly day) => TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);

    [Fact]
    public async Task Conflicting_mutations_preserve_accepted_ticks_archives_and_other_selected_days()
    {
        var original = new Trade(1, Start, 100, 2, TradeSide.Buy);
        var accepted = new Trade(2, Start.AddSeconds(1), 101, 3, TradeSide.Sell);
        var closedDay = Day.AddDays(-1);
        using (var writer = new TickFile.Writer(DayPath(Day), _instrument, includeProvenance: true)) writer.Write(original);
        string archive = TickArchive.ArchiveDay(_root, _instrument, Day);
        byte[] archived = File.ReadAllBytes(archive);
        HistoricalData.WriteDay(_root, _instrument, closedDay,
            new[] { new Trade(10, Start.AddDays(-1), 90, 4, TradeSide.Buy) });
        byte[] closed = File.ReadAllBytes(DayPath(closedDay));
        using var recorder = new TickRecorder(_root, _instrument);
        try
        {
            Assert.True(recorder.IsActive);
            recorder.WriteLive(new[] { accepted });
            await WaitForRowsAsync(recorder, DayPath(Day), 2);
            byte[] recorded = ReadBytes(DayPath(Day));
            var replacement = new[] { new Trade(3, Start, 999, 1, TradeSide.Buy) };

            Assert.Throws<IOException>(() => HistoricalData.DeleteDays(_root, _instrument.FeedId, _instrument.Symbol,
                new[] { closedDay, Day }));
            Assert.Throws<IOException>(() => HistoricalData.WriteDay(_root, _instrument, Day, replacement, replace: true));
            Assert.Throws<IOException>(() => TickArchive.ArchiveDay(_root, _instrument, Day));
            Assert.Throws<IOException>(() => TickArchive.ArchiveDays(_root, _instrument, new[] { closedDay, Day }));
            Assert.False(File.Exists(TickArchive.PathFor(DayPath(closedDay))));
            Assert.Throws<IOException>(() => TickFile.Repair(DayPath(Day), _instrument));
            Assert.Throws<IOException>(() =>
            {
                using var writer = new TickFile.Writer(DayPath(Day), _instrument);
                writer.Write(replacement[0]);
            });
            Assert.True(recorder.IsActive);
            Assert.Null(recorder.Error);
            Assert.Equal(recorded, ReadBytes(DayPath(Day)));
            Assert.Equal(archived, File.ReadAllBytes(archive));
            Assert.Equal(closed, File.ReadAllBytes(DayPath(closedDay)));
            Assert.Equal(new[] { original, accepted }, TickFile.ReadAppendPrefix(DayPath(Day), out int dropped, _instrument.TickSize));
            Assert.Equal(0, dropped);
        }
        finally { await DrainAsync(recorder); }

        var published = new Trade(3, Start, 999, 1, TradeSide.Buy);
        HistoricalData.WriteDay(_root, _instrument, Day, new[] { published }, replace: true);
        var stored = Assert.Single(TickFile.Read(DayPath(Day)));
        Assert.Equal((published.Id, published.Price), (stored.Id, stored.Price));
    }

    [Fact]
    public async Task Closed_day_publication_and_physical_deletion_remain_available_during_live_recording()
    {
        var accepted = new Trade(1, Start, 100, 2, TradeSide.Buy);
        var closedDay = Day.AddDays(-1);
        var downloaded = new Trade(10, Start.AddDays(-1), 90, 4, TradeSide.Sell,
            new TradeProvenance(DataQuality.Raw, SourceIdentity: "closed-day-download"));
        using var recorder = new TickRecorder(_root, _instrument);
        try
        {
            Assert.True(recorder.IsActive);
            recorder.WriteLive(new[] { accepted });
            await WaitForRowsAsync(recorder, DayPath(Day), 1);
            HistoricalData.WriteDay(_root, _instrument, closedDay, new[] { downloaded }, replace: true);
            Assert.Equal(1, TickArchive.ArchiveDays(_root, _instrument, new[] { closedDay }));
            string archive = TickArchive.PathFor(DayPath(closedDay));
            Assert.Equal(downloaded, Assert.Single(TickFile.Read(archive, out int dropped, requireComplete: true)));
            Assert.Equal(0, dropped);

            var secondary = new Instrument { FeedId = HistorySources.Databento, Symbol = _instrument.Symbol, TickSize = 1, VolumeStep = 1 };
            HistoricalData.WriteDay(_root, secondary, closedDay,
                new[] { new Trade(201, Start.AddDays(-1), 321, 5, TradeSide.Buy) });
            string secondaryPath = TickFile.DayPath(_root, secondary.FeedId, secondary.Symbol, closedDay);
            byte[] secondaryBytes = File.ReadAllBytes(secondaryPath);
            HistoricalData.DeleteDays(_root, _instrument.FeedId, _instrument.Symbol, new[] { closedDay });
            Assert.False(File.Exists(DayPath(closedDay)));
            Assert.False(File.Exists(archive));
            Assert.Equal(secondaryBytes, File.ReadAllBytes(secondaryPath));
            Assert.Equal(201, Assert.Single(HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, closedDay)).Id);
            Assert.True(recorder.IsActive);
            Assert.Null(recorder.Error);
            Assert.Equal(accepted, Assert.Single(TickFile.ReadAppendPrefix(DayPath(Day), out int liveDropped, _instrument.TickSize)));
            Assert.Equal(0, liveDropped);
        }
        finally { await DrainAsync(recorder); }
    }

    [Fact]
    public void Csv_owned_publication_reconciles_removed_days_and_refreshes_retained_archives()
    {
        Directory.CreateDirectory(_root);
        string input = Path.Combine(_root, "input.csv");
        File.WriteAllText(input, """
            time,open,high,low,close,volume
            2026-10-12 12:00:00,100,104,96,102,8
            2026-10-16 12:00:00,102,106,98,104,12
            """);
        CsvImport.Import(input, "CSVLEASE", _root, TimeSpan.Zero, 1, null, default);
        var instrument = new Instrument { FeedId = CsvImport.FeedId, Symbol = "CSVLEASE", TickSize = 1, VolumeStep = 1 };
        var retainedDay = new DateOnly(2026, 10, 13);
        var removedDay = new DateOnly(2026, 10, 18);
        string retainedArchive = TickArchive.ArchiveDay(_root, instrument, retainedDay);
        string removedArchive = TickArchive.ArchiveDay(_root, instrument, removedDay);
        File.WriteAllText(input, """
            time,open,high,low,close,volume
            2026-10-12 12:00:00,100,105,95,103,4
            2026-10-13 12:00:00,103,107,99,105,8
            2026-10-16 12:00:00,105,109,101,107,12
            """);

        var result = CsvImport.Import(input, instrument.Symbol, _root, TimeSpan.Zero, 1, null, default);
        var rows = HistoricalData.Read(_root, instrument.FeedId, instrument.Symbol,
            new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc)).ToArray();
        Assert.Equal(12, result.Trades);
        Assert.Equal(12, rows.Length);
        Assert.Equal(24m, rows.Sum(trade => trade.Volume));
        Assert.False(HistoricalData.HasDay(_root, instrument.FeedId, instrument.Symbol, removedDay));
        Assert.False(File.Exists(removedArchive));
        string retainedRaw = TickFile.DayPath(_root, instrument.FeedId, instrument.Symbol, retainedDay);
        Assert.Equal(TickFile.Read(retainedRaw), TickFile.Read(retainedArchive));
    }

    private async Task WaitForRowsAsync(TickRecorder recorder, string path, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            Assert.Null(recorder.Error);
            Assert.True(recorder.IsActive);
            var rows = TickFile.ReadAppendPrefix(path, out int dropped, _instrument.TickSize);
            Assert.Equal(0, dropped);
            if (rows.Count == count) return;
            await Task.Delay(5, timeout.Token);
        }
    }

    private static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static async Task DrainAsync(TickRecorder recorder)
    {
        recorder.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (recorder.IsActive) await Task.Delay(5, timeout.Token);
        Assert.Null(recorder.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
