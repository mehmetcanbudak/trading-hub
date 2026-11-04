using TradingHub.Data;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Market;

namespace TradingHub.Tests;

public sealed class IbBarImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ib-import-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "ticks");
    private string Csv => Path.Combine(_dir, "csv");
    private static readonly DateTime Start = new(2026, 10, 30, 23, 59, 0, DateTimeKind.Utc);
    private static long Unix => new DateTimeOffset(Start).ToUnixTimeSeconds();
    public IbBarImportTests() => Directory.CreateDirectory(Csv);
    public void Dispose() => Directory.Delete(_dir, true);
    private string Input(string name, params string[] rows)
    {
        string path = Path.Combine(Csv, name);
        File.WriteAllText(path, "time,open,high,low,close,volume,contract\n" + string.Join('\n', rows) + "\n");
        return path;
    }
    private string Recorded(DateTime time)
    {
        var instrument = IbBarImport.InstrumentFor("MNQ");
        var day = DateOnly.FromDateTime(time);
        HistoricalData.WriteDay(Root, instrument, day, new[] { new Trade(1000, time.AddSeconds(20), 100, 3, TradeSide.Buy) }, true);
        return TickFile.DayPath(Root, "ib", "MNQ", day);
    }
    [Fact]
    public async Task DefaultImportPreservesRecordedBytesAndComposesUtcDaysChronologicallyWithNegativeIds()
    {
        Input("MNQ1m_2026-10-30.csv", $"{Unix},100,101,99,100,12,MNQZ6", $"{Unix + 60},101,102,100,101,8,MNQZ6", $"{Unix + 120},101,102,100,101,0,MNQZ6");
        string path = Recorded(Start);
        var original = File.ReadAllBytes(path);
        var preview = IbBarImport.PreviewFolder(Csv, Root);
        Assert.Equal(2, preview.Days.Count); Assert.Equal(1, preview.Conflicts);
        var result = IbBarImport.Commit(preview, Root);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(1, result.FilesWritten);
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory, "transaction.json")));
        var rows = HistoricalData.Read(Root, "ib", "MNQ", Start, Start.AddMinutes(3)).ToList();
        Assert.Equal(5, rows.Count);
        Assert.Equal(1000, rows[0].Id);
        Assert.All(rows.Skip(1), row => Assert.True(row.Id < 0));
        Assert.Equal(rows.OrderBy(t => t.Time).Select(t => t.Time), rows.Select(t => t.Time));
        using var offline = new IbFeed(new("127.0.0.1", 1, 17, true), Root);
        var synthetic = await offline.LoadBarHistoryAsync(IbBarImport.InstrumentFor("MNQ"), Start, Start.AddMinutes(3), default);
        Assert.Equal(4, synthetic.Count);
        var store = new TradeStore(IbBarImport.InstrumentFor("MNQ"));
        foreach (var trade in synthetic) store.Add(trade);
        var candles = new CandleSeries(store.Instrument, ChartPeriod.M1, session: offline.GetSessionSettings(store.Instrument));
        candles.Rebuild(store);
        var candle = Assert.Single(candles.Candles);
        Assert.True(candle.IsSynthetic);
        Assert.True(candle.Levels.IsEmpty);
        Assert.False(candle.HasRealTrades);
        Assert.Equal(8, candle.Volume);
        var dataset = Assert.Single(HistorySources.Datasets(Root));
        Assert.Equal("ib", dataset.FeedId); Assert.Equal(2, dataset.Days.Count);
        var again = IbBarImport.Commit(IbBarImport.PreviewFolder(Csv, Root), Root);
        Assert.Equal(0, again.FilesWritten);
    }
    [Fact]
    public void ExplicitReplacePreservesOriginalsAndOnlyReplacesCoveredMinutes()
    {
        Input("MNQ1m_2026-10-30.csv", $"{Unix},100,101,99,100,12,MNQZ6");
        string path = Recorded(Start); byte[] before = File.ReadAllBytes(path);
        var preview = IbBarImport.PreviewFolder(Csv, Root);
        var result = IbBarImport.Commit(preview, Root, IbBarImport.ImportPolicy.ReplaceCoveredMinutes);
        Assert.Contains(Directory.EnumerateFiles(result.BackupDirectory, "*.original"), p => File.ReadAllBytes(p).SequenceEqual(before));
        var rows = HistoricalData.Read(Root, "ib", "MNQ", Start, Start.AddMinutes(1)).ToList();
        Assert.Equal(4, rows.Count); Assert.All(rows, row => Assert.True(row.Id < 0));
        Assert.Equal(12, rows.Sum(t => t.Volume));
    }
    [Fact]
    public void AllFilesValidateBeforeAnyPublicationAndMixedRootsAreRejected()
    {
        Input("MNQ_a.csv", $"{Unix},100,101,99,100,12,MNQZ6");
        Input("MNQ_b.csv", $"{Unix + 60},100,99,101,100,12,MNQZ6");
        Assert.Throws<InvalidDataException>(() => IbBarImport.PreviewFolder(Csv, Root));
        Assert.False(Directory.Exists(Root));
        File.Delete(Path.Combine(Csv, "MNQ_b.csv"));
        Input("MES_b.csv", $"{Unix + 60},100,101,99,100,12,MESZ6");
        Assert.Throws<InvalidDataException>(() => IbBarImport.PreviewFolder(Csv, Root, "MNQ"));
        Assert.False(Directory.Exists(Root));
    }
    [Fact]
    public void DuplicatePrecedenceIsOrdinalFilenameThenLastRowAndZeroVolumeIsSkipped()
    {
        Input("MNQ_a.csv", $"{Unix},100,101,99,100,12,MNQZ6");
        Input("MNQ_z.csv", $"{Unix},200,201,199,200,20,MNQZ6", $"{Unix},300,301,299,300,30,MNQZ6", $"{Unix + 60},100,101,99,100,0,MNQZ6");
        var preview = IbBarImport.PreviewFolder(Csv, Root);
        Assert.Equal(300, Assert.Single(preview.Bars).Open);
        IbBarImport.Commit(preview, Root);
        var rows = HistoricalData.Read(Root, "ib", "MNQ", Start, Start.AddMinutes(1)).ToList();
        Assert.Equal(30, rows.Sum(t => t.Volume)); Assert.Equal(301, rows.Max(t => t.Price));
    }
    [Fact]
    public void LeaseConflictStalePreviewAndCancellationNeverOverwriteHistory()
    {
        Input("MNQ_a.csv", $"{Unix},100,101,99,100,12,MNQZ6");
        string path = Recorded(Start); byte[] original = File.ReadAllBytes(path);
        var preview = IbBarImport.PreviewFolder(Csv, Root);
        using (var lease = TickFile.TryLock(Root, "ib", "MNQ"))
            Assert.Throws<IOException>(() => IbBarImport.Commit(preview, Root));
        Input("MNQ_a.csv", $"{Unix},100,101,99,100,13,MNQZ6");
        Assert.Contains("stale", Assert.Throws<IOException>(() => IbBarImport.Commit(preview, Root)).Message);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => IbBarImport.Commit(IbBarImport.PreviewFolder(Csv, Root), Root, ct: canceled.Token));
        Assert.Equal(original, File.ReadAllBytes(path));
    }
    [Fact]
    public void InjectedMultiFileCommitFailureRestoresOriginalsAndRetainsRecoveryJournal()
    {
        Input("MNQ_a.csv", $"{Unix},100,101,99,100,12,MNQZ6", $"{Unix + 60},100,101,99,100,12,MNQZ6");
        string first = Recorded(Start), second = Recorded(Start.AddMinutes(1));
        byte[] beforeFirst = File.ReadAllBytes(first), beforeSecond = File.ReadAllBytes(second);
        var preview = IbBarImport.PreviewFolder(Csv, Root);
        var failure = Assert.Throws<IOException>(() => IbBarImport.Commit(preview, Root, IbBarImport.ImportPolicy.ReplaceCoveredMinutes,
            beforePublish: count => { if (count == 1) throw new IOException("Fixture disk failure"); }));
        Assert.Contains("original files restored", failure.Message);
        Assert.Equal(beforeFirst, File.ReadAllBytes(first)); Assert.Equal(beforeSecond, File.ReadAllBytes(second));
        var journal = Assert.Single(Directory.EnumerateFiles(Path.Combine(Root, ".ib-import-backups"), "transaction.json", SearchOption.AllDirectories));
        Assert.Contains("rolled-back", File.ReadAllText(journal));
        Assert.Empty(TickFile.AvailableDays(Root, IbBarStore.BarFolder, "MNQ"));
    }
    [Fact]
    public void LegacySyntheticIbFilesRemainReadableAndRealMinutesWinWithoutIdRenumbering()
    {
        var instrument = IbBarImport.InstrumentFor("MNQ");
        var day = DateOnly.FromDateTime(Start);
        var legacy = new[] { new Trade(-100, Start, 100, 1, TradeSide.None), new Trade(1000, Start.AddSeconds(1), 101, 2, TradeSide.Buy) };
        HistoricalData.WriteDay(Root, instrument, day, legacy, true);
        var rows = HistorySources.ReadDay(Root, "ib", "MNQ", day);
        Assert.Equal(1000, Assert.Single(rows).Id);
    }
    [Fact]
    public void TruncatedRecordedHistoryFailsVisiblyBeforeImportAndDoesNotGetRepairedByReplacement()
    {
        Input("MNQ_a.csv", $"{Unix},100,101,99,100,12,MNQZ6");
        string path = Recorded(Start);
        byte[] original = File.ReadAllBytes(path);
        File.WriteAllBytes(path, original[..^1]);
        var truncated = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => IbBarImport.PreviewFolder(Csv, Root));
        Assert.Equal(truncated, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(Root, IbBarStore.BarFolder)));
    }
}
