using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

internal static class IbExcursionScene
{
    private static readonly DateTime Start = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    public static int Run(Action<double> pump, string output, bool replaceHistory = false)
    {
        string? isolated = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR");
        if (string.IsNullOrWhiteSpace(isolated) || Path.GetFullPath(isolated) != Path.GetFullPath(AppPaths.Root))
            throw new InvalidOperationException("IB excursion proof requires its explicit isolated data root.");
        string csv = Path.Combine(AppPaths.Root, "owned-excursion-csv");
        Directory.CreateDirectory(csv);
        File.WriteAllText(Path.Combine(csv, "MNQ.csv"), $"time,open,high,low,close,volume\n{new DateTimeOffset(Start).ToUnixTimeSeconds()},100,102,99,101,12\n");
        IbBarImport.Commit(IbBarImport.PreviewFolder(csv, AppPaths.Ticks, "MNQ"), AppPaths.Ticks);
        const string key = "owned-ib-excursion";
        var ledger = new LiveLedger(key, 2m);
        ledger.Apply(new LiveFill(Start, true, 2m, 100m, "MNQ", 2m));
        ledger.Apply(new LiveFill(Start.AddSeconds(45), false, 2m, 101m, "MNQ", 2m));
        var entry = new LiveEntry
        {
            Key = key, Source = "fixture", Ledger = ledger,
            Info = new LiveRunnerInfo { Key = key, FeedId = "ib", Symbol = "MNQ", PointValue = 2m },
        };
        (decimal Mae, decimal Mfe, bool Exact) Read(ClosedTrade trade)
        {
            var pending = App.Services.Live.ExcursionsAsync(new[] { (entry, trade) });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!pending.IsCompleted && DateTime.UtcNow < deadline) pump(0.01);
            if (!pending.IsCompleted) throw new TimeoutException("The actual IB excursion consumer did not finish.");
            return pending.GetAwaiter().GetResult()[LiveCenter.TradeId(key, trade)];
        }
        var trade = ledger.Trades.Single();
        var before = Read(trade);
        if (before != (-4m, 8m, true)) throw new InvalidOperationException($"Initial IB excursion control failed: {before}.");
        (decimal Mae, decimal Mfe, bool Exact) after;
        var expected = replaceHistory ? (-3m, 9m, true) : (-8m, 4m, true);
        if (replaceHistory)
        {
            string path = TickFile.DayPath(AppPaths.Ticks, IbBarStore.BarFolder, "MNQ", DateOnly.FromDateTime(Start));
            long length = new FileInfo(path).Length;
            var mtime = File.GetLastWriteTimeUtc(path);
            File.WriteAllText(Path.Combine(csv, "MNQ.csv"), $"time,open,high,low,close,volume\n{new DateTimeOffset(Start).ToUnixTimeSeconds()},100.25,102.25,99.25,101.25,12\n");
            IbBarImport.Commit(IbBarImport.PreviewFolder(csv, AppPaths.Ticks, "MNQ"), AppPaths.Ticks, IbBarImport.ImportPolicy.ReplaceCoveredMinutes);
            if (new FileInfo(path).Length != length) throw new InvalidOperationException("History replacement did not preserve file length.");
            File.SetLastWriteTimeUtc(path, mtime);
            if (File.GetLastWriteTimeUtc(path) != mtime) throw new InvalidOperationException("History replacement did not preserve mtime.");
            after = Read(trade);
        }
        else after = Read(trade with { EntryPrice = 101m, Pnl = 0m });
        bool correct = after == expected;
        string evidence = JsonSerializer.Serialize(new
        {
            before = new { before.Mae, before.Mfe, before.Exact },
            after = new { after.Mae, after.Mfe, after.Exact },
            expectedAfter = new { Mae = expected.Item1, Mfe = expected.Item2, Exact = expected.Item3 },
            passed = correct, historyReplacedWithSameMetadata = replaceHistory,
            authority = "Actual LiveCenter excursion consumer and public IB import; synthetic bars are not observed ticks",
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, replaceHistory ? "ib-excursion-import-evidence.json" : "ib-excursion-evidence.json"), evidence);
        Console.WriteLine(evidence);
        return correct ? 0 : 1;
    }

    public static int RunPanel(MainWindow window, Action<double> pump, string output)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR")))
            throw new InvalidOperationException("Live trade proof requires explicit isolated data and ledger roots.");
        string csv = Path.Combine(AppPaths.Root, "owned-table-csv");
        Directory.CreateDirectory(csv);
        string source = Path.Combine(csv, "MNQ.csv");
        File.WriteAllText(source, $"time,open,high,low,close,volume\n{new DateTimeOffset(Start).ToUnixTimeSeconds()},100,102,99,101,12\n");
        IbBarImport.Commit(IbBarImport.PreviewFolder(csv, AppPaths.Ticks, "MNQ"), AppPaths.Ticks, IbBarImport.ImportPolicy.ReplaceCoveredMinutes);
        const string key = "owned-ib-table";
        const string strategy = "Owned IB excursion proof";
        LiveStore.SaveRunners(new[] { new LiveRunnerInfo { Key = key, Strategy = strategy, FeedId = "ib", Symbol = "MNQ", PointValue = 2m } });
        foreach (var fill in new[] { new LiveFill(Start, true, 2m, 100m, "MNQ", 2m), new LiveFill(Start.AddSeconds(45), false, 2m, 101m, "MNQ", 2m) })
            if (LiveStore.AppendFill(key, fill) is { } error) throw new IOException(error);
        window.Navigate("live");
        pump(0.1);
        var view = window.GetVisualDescendants().OfType<LiveView>().Single(v => v.IsEffectivelyVisible);
        view.Select("trades");
        var panel = view.GetVisualDescendants().OfType<LiveTradesPanel>().Single();
        (string? Mae, string? Mfe) Cells()
        {
            var row = panel.GetVisualDescendants().OfType<Grid>().SingleOrDefault(grid =>
                grid.Children.OfType<TextBlock>().Any(text => Grid.GetColumn(text) == 1 && text.Text == strategy));
            return (row?.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 12).Text,
                row?.Children.OfType<TextBlock>().Single(text => Grid.GetColumn(text) == 13).Text);
        }
        string Money(decimal value) => value.ToString("+#,##0.00;-#,##0.00;0.00", UiCulture.Current);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Cells() != (Money(-4m), Money(8m)) && DateTime.UtcNow < deadline) pump(0.01);
        var before = Cells();
        if (before != (Money(-4m), Money(8m))) throw new InvalidOperationException($"Initial trade table control failed: {before}.");
        using (var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Live trade table did not render."))
            frame.Save(Path.Combine(output, "ib_live_trades_before.png"));

        string path = TickFile.DayPath(AppPaths.Ticks, IbBarStore.BarFolder, "MNQ", DateOnly.FromDateTime(Start));
        long length = new FileInfo(path).Length;
        var mtime = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(source, $"time,open,high,low,close,volume\n{new DateTimeOffset(Start).ToUnixTimeSeconds()},100.25,102.25,99.25,101.25,12\n");
        IbBarImport.Commit(IbBarImport.PreviewFolder(csv, AppPaths.Ticks, "MNQ"), AppPaths.Ticks, IbBarImport.ImportPolicy.ReplaceCoveredMinutes);
        if (new FileInfo(path).Length != length) throw new InvalidOperationException("Owned history replacement changed length.");
        File.SetLastWriteTimeUtc(path, mtime);
        var entry = App.Services.Live.Find(key) ?? throw new InvalidOperationException("Owned stopped runner disappeared.");
        var trade = entry.Ledger!.Trades.Single();
        var pending = App.Services.Live.ExcursionsAsync(new[] { (entry, trade) });
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (!pending.IsCompleted && DateTime.UtcNow < deadline) pump(0.01);
        if (!pending.IsCompleted) throw new TimeoutException("Source excursion control did not finish.");
        var fresh = pending.GetAwaiter().GetResult()[LiveCenter.TradeId(key, trade)];
        panel.Refresh(); // Exercise the existing public table refresh, not its private dictionary.
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (Cells() is { Mae: null } or { Mae: "—" } && DateTime.UtcNow < deadline) pump(0.01);
        pump(0.1);
        var after = Cells();
        bool passed = fresh == (-3m, 9m, true) && after == (Money(-3m), Money(9m));
        using (var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Refreshed trade table did not render."))
            frame.Save(Path.Combine(output, "ib_live_trades_after.png"));
        string evidence = JsonSerializer.Serialize(new { passed,
            before = new { before.Mae, before.Mfe }, after = new { after.Mae, after.Mfe },
            expectedAfter = new { Mae = Money(-3m), Mfe = Money(9m) },
            actualSource = new { fresh.Mae, fresh.Mfe, fresh.Exact },
            authority = "Actual Avalonia live trade table with a same-length/restored-mtime public IB import; synthetic bars, no broker or model calls" },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "ib-live-trades-evidence.json"), evidence);
        Console.WriteLine(evidence);
        return passed ? 0 : 1;
    }

    public static int RunRecordedPrefix(Action<double> pump, string output)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR")))
            throw new InvalidOperationException("Recorded-prefix proof requires an explicit isolated root.");
        const string symbol = "MES";
        const string key = "owned-ib-prefix";
        var start = Start.AddDays(-1);
        var instrument = IbBarImport.InstrumentFor(symbol);
        string path = TickFile.DayPath(AppPaths.Ticks, "ib", symbol, DateOnly.FromDateTime(start));
        using var recorderLease = TickFile.TryLock(AppPaths.Ticks, "ib", symbol, failOnNonSharingError: true, recordingWriter: true)
            ?? throw new InvalidOperationException("The owned recorder lease was not acquired.");
        using var writer = new TickFile.Writer(path, instrument);
        writer.Write(new Trade(1, start, 5000m, 1m, TradeSide.Buy));
        writer.Flush();
        var ledger = new LiveLedger(key, instrument.PointValue);
        ledger.Apply(new LiveFill(start, true, 2m, 5000m, symbol, instrument.PointValue));
        ledger.Apply(new LiveFill(start.AddSeconds(45), false, 2m, 5000.25m, symbol, instrument.PointValue));
        var entry = new LiveEntry { Key = key, Source = "fixture", Ledger = ledger,
            Info = new LiveRunnerInfo { Key = key, FeedId = "ib", Symbol = symbol, PointValue = instrument.PointValue } };
        var trade = ledger.Trades.Single();
        (decimal Mae, decimal Mfe, bool Exact) Read()
        {
            var pending = App.Services.Live.ExcursionsAsync(new[] { (entry, trade) });
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!pending.IsCompleted && DateTime.UtcNow < deadline) pump(0.01);
            if (!pending.IsCompleted) throw new TimeoutException("Recorded-prefix excursion consumer did not finish.");
            return pending.GetAwaiter().GetResult()[LiveCenter.TradeId(key, trade)];
        }
        var before = Read();
        writer.Write(new Trade(2, start.AddSeconds(15), 4999.75m, 1m, TradeSide.Sell));
        writer.Write(new Trade(3, start.AddSeconds(30), 5000.50m, 1m, TradeSide.Buy));
        writer.Write(new Trade(4, start.AddSeconds(45), 5000.25m, 1m, TradeSide.Sell));
        writer.Write(new Trade(5, start.AddSeconds(50), 5000.50m, 1m, TradeSide.Buy));
        writer.Flush();
        var after = Read();
        decimal factor = 2m * instrument.PointValue;
        bool passed = !before.Exact && after == (-0.25m * factor, 0.50m * factor, true);
        string evidence = JsonSerializer.Serialize(new { passed,
            before = new { before.Mae, before.Mfe, before.Exact }, after = new { after.Mae, after.Mfe, after.Exact },
            expectedBeforeExact = false, expectedAfter = new { Mae = -0.25m * factor, Mfe = 0.50m * factor, Exact = true },
            authority = "Actual LiveCenter and public TickFile writer under a real owned recorder lease; prefix withheld, then fully drained closed-trade prices" },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "ib-recorded-prefix-evidence.json"), evidence);
        Console.WriteLine(evidence);
        return passed ? 0 : 1;
    }
}
