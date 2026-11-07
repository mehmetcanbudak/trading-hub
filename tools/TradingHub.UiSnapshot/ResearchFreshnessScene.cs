using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

internal static class ResearchFreshnessScene
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);
    private sealed record CheckResult(string Description, bool Passed, string Observed);
    private sealed record FileImage(long Length, DateTime LastWriteUtc, string Sha256)
    {
        public static FileImage Read(string path) => new(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
    private readonly record struct ExpectedFill(DateTime Day, decimal Entry, decimal Exit);

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        var checks = new List<CheckResult>();
        var mutations = new List<object>();
        var backtests = new List<object>();
        var contextReads = new List<object>();
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<Task>();
        StrategiesView? view = null;
        StrategyCatalog? catalog = null;
        string? previousStrategy = null;
        string? fixtureError = null;
        string nonce = Guid.NewGuid().ToString("N");
        string feed = "research-freshness-" + nonce;
        string symbol = "FRESH-" + nonce;
        string otherSymbol = "CONTROL-" + nonce;
        string contextSymbol = "CONTEXT-" + nonce;
        string contextControl = "CONTEXT-CONTROL-" + nonce;
        string className = "ResearchFreshnessProbe" + nonce;
        string sourcePath = Path.Combine(AppPaths.Strategies, className + ".cs");
        var first = DateTime.UtcNow.Date.AddDays(-4);
        var contextDay = first.AddDays(-2);
        var previousDay = contextDay.AddDays(-1);

        void Check(bool passed, string description, string observed)
        {
            checks.Add(new(description, passed, observed));
            Console.WriteLine($"{(passed ? "OK" : "FAIL")} research freshness: {description}. {observed}");
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Freshness fixture precondition failed. " + message);
        }
        void OwnDirectory(string path)
        {
            if (directories.Contains(path)) return;
            Require(!Directory.Exists(path) && !File.Exists(path), "An owned directory must start absent: " + path);
            directories.Add(path);
        }
        void OwnFile(string path)
        {
            Require(!File.Exists(path), "An owned file must start absent: " + path);
            files.Add(path);
        }
        string ContextPath(string source, string market) => Path.Combine(AppPaths.Root, "Context", source + "_" + market + ".json");
        void OwnContext(string source, string market)
        {
            string path = ContextPath(source, market);
            OwnFile(path);
            OwnFile(path + ".tmp");
        }
        void Wait(Task task)
        {
            pending.Add(task);
            try
            {
                var watchdog = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (watchdog.Elapsed > TimeSpan.FromSeconds(60))
                        throw new TimeoutException("A finite freshness consumer did not complete before the safety watchdog.");
                    pump(0.01);
                }
                task.GetAwaiter().GetResult();
            }
            finally
            {
                if (task.IsCompleted) pending.Remove(task);
            }
        }
        Instrument InstrumentFor(string source, string market) => new()
        {
            FeedId = source, Symbol = market, Exchange = "TEST", TickSize = 1m, VolumeStep = 1m, PointValue = 1m,
        };
        void WriteDay(Instrument instrument, DateTime day, params (int Hour, int Minute, decimal Price)[] prints)
        {
            var provenance = new TradeProvenance(DataQuality.Raw, SourceIdentity: instrument.FeedId + ":" + instrument.Symbol);
            var trades = prints.Select((print, index) => new Trade((long)DateOnly.FromDateTime(day).DayNumber * 100 + index + 1,
                day.AddHours(print.Hour).AddMinutes(print.Minute), print.Price, 1m, TradeSide.Buy, provenance)).ToArray();
            HistoricalData.WriteDay(AppPaths.Ticks, instrument, DateOnly.FromDateTime(day), trades, replace: true);
        }
        void WriteTradingDay(Instrument instrument, DateTime day, decimal entry, decimal exit) =>
            WriteDay(instrument, day, (12, 0, entry), (12, 1, entry), (12, 2, exit), (12, 3, exit));
        void CorrectPreservingMetadata(string label, string path, Action correction)
        {
            var before = FileImage.Read(path);
            correction();
            File.SetLastWriteTimeUtc(path, before.LastWriteUtc);
            var after = FileImage.Read(path);
            Require(after.Length == before.Length && after.LastWriteUtc == before.LastWriteUtc,
                label + " must preserve the physical source length and timestamp.");
            Require(after.Sha256 != before.Sha256, label + " must really change physical source content.");
            mutations.Add(new { label, path, before, after, metadataPreserved = true, contentChanged = true });
        }
        void Choose(string market, DateTime from, DateTime inclusiveTo)
        {
            var sources = Field<ComboBox>(view!, "_dataSource");
            sources.SelectedItem = sources.Items.OfType<ComboBoxItem>().Single(item => item.Tag is SymbolData data && data.FeedId == feed && data.Symbol == market);
            Field<CalendarDatePicker>(view!, "_from").SelectedDate = from;
            Field<CalendarDatePicker>(view!, "_to").SelectedDate = inclusiveTo;
            Field<TextBox>(view!, "_period").Text = "M1";
            Field<NumericUpDown>(view!, "_capital").Value = 10_000m;
            Field<NumericUpDown>(view!, "_commPct").Value = 0;
            Field<NumericUpDown>(view!, "_commUnit").Value = 0;
            Field<NumericUpDown>(view!, "_slippage").Value = 0;
            Field<ComboBox>(view!, "_timeZone").SelectedIndex = 0;
            Field<NumericUpDown>(view!, "_sessionStart").Value = 0;
        }
        StrategyRun Backtest(string stage, string strategyId)
        {
            var existing = StrategyStore.List(strategyId).Select(run => run.Id).ToHashSet(StringComparer.Ordinal);
            Wait((Task)Call(view!, "RunBacktestAsync")!);
            var saved = StrategyStore.List(strategyId).Where(run => !existing.Contains(run.Id)).ToArray();
            Require(saved.Length == 1, stage + " must finish and save one actual view backtest. " + Field<TextBlock>(view!, "_runStatus").Text);
            var run = StrategyStore.Load(strategyId, saved[0].Id)
                ?? throw new InvalidOperationException(stage + " did not reopen from saved history.");
            Require(run is { Kind: RunKind.Backtest, Trades: not null, Metrics: not null }, stage + " must reopen as a complete backtest.");
            var trades = run.Trades!;
            var visible = Field<ListBox>(view!, "_tradesList").Items.OfType<ClosedTrade>().ToArray();
            Check(visible.Select(trade => (trade.EntryTime, trade.EntryPrice, trade.ExitTime, trade.ExitPrice, trade.NetPnl))
                    .SequenceEqual(trades.Select(trade => (trade.EntryTime, trade.EntryPrice, trade.ExitTime, trade.ExitPrice, trade.NetPnl))),
                stage + " displays the same executions that reopen from saved history", $"Displayed {visible.Length}, saved {trades.Count}");
            backtests.Add(new
            {
                stage, run.Id, run.Settings, run.Ticks, netProfit = run.Metrics!.NetProfit,
                fills = trades.Select(trade => new { trade.EntryTime, trade.EntryPrice, trade.ExitTime, trade.ExitPrice, trade.Quantity, trade.NetPnl }),
            });
            return run;
        }
        void ExpectBacktest(string stage, StrategyRun run, string market, DateTime from, DateTime to, long ticks, decimal net, params ExpectedFill[] expected)
        {
            Require(run.Settings.FeedId == feed && run.Settings.Symbol == market && run.Settings.FromUtc == from && run.Settings.ToUtc == to,
                stage + " must retain the requested source and exact range.");
            var trades = run.Trades!;
            var metrics = run.Metrics!;
            bool fills = trades.Count == expected.Length && trades.Zip(expected).All(pair =>
                pair.First.EntryTime == pair.Second.Day.AddHours(12).AddMinutes(1) && pair.First.EntryPrice == pair.Second.Entry &&
                pair.First.ExitTime == pair.Second.Day.AddHours(12).AddMinutes(3) && pair.First.ExitPrice == pair.Second.Exit &&
                pair.First.Quantity == 1m && pair.First.IsLong);
            Check(run.Ticks == ticks && metrics.NetProfit == net && fills,
                stage, $"Expected {ticks} ticks, {expected.Length} executions, net {net}; observed {run.Ticks} ticks, {trades.Count} executions, net {metrics.NetProfit}; exits [{string.Join(", ", trades.Select(trade => trade.ExitPrice))}]");
        }
        void ExpectHours(string stage, string source, string market, params (DateTime Time, decimal Close)[] expected)
        {
            var actual = MarketConditions.HourlyBefore(source, market, first);
            contextReads.Add(new { stage, source, market, untilUtc = first, hours = actual.Select(hour => new { hour.Time, hour.Close }).ToArray() });
            Check(actual.SequenceEqual(expected), stage,
                "Expected [" + string.Join(", ", expected.Select(hour => $"{hour.Time:yyyy-MM-dd HH}: {hour.Close}")) +
                "]; observed [" + string.Join(", ", actual.Select(hour => $"{hour.Time:yyyy-MM-dd HH}: {hour.Close}")) + "]");
        }

        try
        {
            foreach (string variable in new[] { "TRADINGHUB_DATA_DIR", "TRADINGHUB_STRATEGIES_DIR", "TRADINGHUB_INDICATORS_DIR", "TRADINGHUB_BACKTESTS_DIR", "TRADINGHUB_LIVE_DIR", "TRADINGHUB_KEYCHAIN_FILE" })
                Require(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)), "Program must supply " + variable);
            Require(Environment.GetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD") == "off", "Program must disable the sleep guard.");
            OwnDirectory(Path.Combine(AppPaths.Ticks, feed));
            OwnDirectory(Path.Combine(AppPaths.Backtests, className));
            OwnFile(sourcePath);
            OwnContext(feed, symbol);
            OwnContext(feed, otherSymbol);
            var instrument = InstrumentFor(feed, symbol);
            var control = InstrumentFor(feed, otherSymbol);
            WriteTradingDay(instrument, first, 100, 104);
            WriteTradingDay(instrument, first.AddDays(1), 200, 206);
            WriteTradingDay(instrument, first.AddDays(2), 300, 308);
            WriteTradingDay(control, first, 400, 405);
            WriteTradingDay(control, first.AddDays(1), 500, 507);
            WriteTradingDay(control, first.AddDays(2), 600, 611);
            var unchangedTicks = new[]
            {
                TickFile.DayPath(AppPaths.Ticks, feed, symbol, DateOnly.FromDateTime(first)),
                TickFile.DayPath(AppPaths.Ticks, feed, symbol, DateOnly.FromDateTime(first.AddDays(2))),
            }.Concat(Enumerable.Range(0, 3).Select(offset => TickFile.DayPath(AppPaths.Ticks, feed, otherSymbol, DateOnly.FromDateTime(first.AddDays(offset)))))
                .ToDictionary(path => path, FileImage.Read);

            window.Navigate("strategies");
            view = (StrategiesView)window.Page("strategies")!;
            catalog = App.Services.Strategies;
            previousStrategy = Field<StrategyFile?>(view, "_current")?.Path;
            Require(Path.GetFullPath(StrategyStore.Root) == Path.GetFullPath(AppPaths.Backtests), "The saved-run root must be Program's isolated backtest store.");
            File.WriteAllText(sourcePath, $$"""
                using TradingHub.Market;
                using TradingHub.Strategies;
                [Strategy("Research freshness consumer")]
                public sealed class {{className}} : Strategy
                {
                    protected override void OnTick(Trade trade)
                    {
                        if (trade.Time.Minute == 0 && IsFlat) Buy(1);
                        else if (trade.Time.Minute == 2 && IsLong) ClosePosition();
                    }
                }
                """);
            Wait(view.OpenFileAsync(sourcePath));
            var file = catalog.Files.Single(file => file.Path == sourcePath);
            Require(file.LastCompile?.Success == true, "The actual view did not compile the owned strategy. " +
                string.Join("; ", file.LastCompile?.Diagnostics.Select(diagnostic => diagnostic.Message) ?? []));
            string strategyId = file.Id;
            Require(strategyId == className, "The compiled fixture must retain its uniquely owned strategy identity.");
            Call(view, "ShowTab", "backtest");
            Choose(otherSymbol, first, first.AddDays(2));
            ExpectBacktest("Unchanged-symbol baseline", Backtest("control-before", strategyId), otherSymbol, first, first.AddDays(3), 12, 23,
                new(first, 400, 405), new(first.AddDays(1), 500, 507), new(first.AddDays(2), 600, 611));
            Choose(symbol, first, first.AddDays(2));
            ExpectBacktest("Original actual-view backtest", Backtest("ticks-before", strategyId), symbol, first, first.AddDays(3), 12, 18,
                new(first, 100, 104), new(first.AddDays(1), 200, 206), new(first.AddDays(2), 300, 308));
            ExpectBacktest("Unchanged same-range backtest", Backtest("ticks-unchanged", strategyId), symbol, first, first.AddDays(3), 12, 18,
                new(first, 100, 104), new(first.AddDays(1), 200, 206), new(first.AddDays(2), 300, 308));

            string corrected = TickFile.DayPath(AppPaths.Ticks, feed, symbol, DateOnly.FromDateTime(first.AddDays(1)));
            CorrectPreservingMetadata("Tick-source correction", corrected, () => WriteTradingDay(instrument, first.AddDays(1), 200, 209));
            Require(HistoricalData.Read(AppPaths.Ticks, feed, symbol, first.AddDays(1), first.AddDays(2)).Select(trade => trade.Price).SequenceEqual(new[] { 200m, 200m, 209m, 209m }),
                "The public disk reader must see the corrected tick prices before the next view run.");
            // No selection, range change, refresh or recompile may replace the warmed view between these runs.
            ExpectBacktest("Next same-range backtest sees corrected tick prices", Backtest("ticks-corrected", strategyId), symbol, first, first.AddDays(3), 12, 21,
                new(first, 100, 104), new(first.AddDays(1), 200, 209), new(first.AddDays(2), 300, 308));
            pump(0.01);
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "research_freshness_tick_correction.png"));

            HistoricalData.DeleteDays(AppPaths.Ticks, feed, symbol, new[] { DateOnly.FromDateTime(first.AddDays(1)) });
            Require(!HistorySources.HasDay(AppPaths.Ticks, feed, symbol, DateOnly.FromDateTime(first.AddDays(1))) &&
                    HistoricalData.Read(AppPaths.Ticks, feed, symbol, first, first.AddDays(3)).Count() == 8,
                "Partial deletion must remove just the middle day from the public same-range disk read.");
            ExpectBacktest("Next same-range backtest excludes the deleted day", Backtest("ticks-partial-deletion", strategyId), symbol, first, first.AddDays(3), 8, 12,
                new(first, 100, 104), new(first.AddDays(2), 300, 308));
            pump(0.01);
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "research_freshness_partial_deletion.png"));
            Check(unchangedTicks.All(pair => FileImage.Read(pair.Key) == pair.Value), "Tick mutation leaves other symbols and days untouched", "Physical lengths, timestamps and content digests checked for five controls");
            Choose(otherSymbol, first, first.AddDays(2));
            ExpectBacktest("Unchanged-symbol next backtest", Backtest("control-after", strategyId), otherSymbol, first, first.AddDays(3), 12, 23,
                new(first, 400, 405), new(first.AddDays(1), 500, 507), new(first.AddDays(2), 600, 611));
            Choose(symbol, first, first);
            ExpectBacktest("Unchanged first-day next backtest", Backtest("first-day-control", strategyId), symbol, first, first.AddDays(1), 4, 4, new ExpectedFill(first, 100, 104));
            Choose(symbol, first.AddDays(2), first.AddDays(2));
            ExpectBacktest("Unchanged last-day next backtest", Backtest("last-day-control", strategyId), symbol, first.AddDays(2), first.AddDays(3), 4, 8, new ExpectedFill(first.AddDays(2), 300, 308));

            foreach (string market in new[] { contextSymbol, contextControl })
            {
                OwnDirectory(Path.GetDirectoryName(TickFile.DayPath(AppPaths.Ticks, "rithmic", market, DateOnly.FromDateTime(contextDay)))!);
                OwnDirectory(Path.GetDirectoryName(TickFile.DayPath(AppPaths.Ticks, HistorySources.Databento, market, DateOnly.FromDateTime(contextDay)))!);
                OwnContext("rithmic", market);
                OwnContext("rithmic-solo", market);
                OwnContext(HistorySources.Databento, market);
            }
            var primary = InstrumentFor("rithmic", contextSymbol);
            var secondary = InstrumentFor(HistorySources.Databento, contextSymbol);
            WriteDay(primary, previousDay, (9, 59, 90));
            WriteDay(secondary, previousDay, (9, 59, 990));
            WriteDay(primary, contextDay, (10, 59, 110), (11, 59, 120));
            WriteDay(secondary, contextDay, (10, 59, 910), (12, 59, 210), (13, 59, 220));
            var primaryControl = InstrumentFor("rithmic", contextControl);
            var secondaryControl = InstrumentFor(HistorySources.Databento, contextControl);
            WriteDay(primaryControl, previousDay, (9, 59, 50));
            WriteDay(secondaryControl, previousDay, (9, 59, 950));
            WriteDay(primaryControl, contextDay, (10, 59, 51), (11, 59, 61));
            WriteDay(secondaryControl, contextDay, (10, 59, 951), (12, 59, 101), (13, 59, 111));
            string primaryPath = TickFile.DayPath(AppPaths.Ticks, "rithmic", contextSymbol, DateOnly.FromDateTime(contextDay));
            string secondaryPath = TickFile.DayPath(AppPaths.Ticks, HistorySources.Databento, contextSymbol, DateOnly.FromDateTime(contextDay));
            Require(HistorySources.DayFiles(AppPaths.Ticks, "rithmic", contextSymbol, DateOnly.FromDateTime(contextDay)).SequenceEqual(new[] { primaryPath, secondaryPath }),
                "The combined source must really have two separate physical constituents in priority order.");
            var unchangedContext = new[] { primaryPath }.Concat(new[] { previousDay, contextDay }.SelectMany(day =>
                new[] { "rithmic", HistorySources.Databento }.Select(source => TickFile.DayPath(AppPaths.Ticks, source, contextControl, DateOnly.FromDateTime(day)))))
                .Concat(new[] { "rithmic", HistorySources.Databento }.Select(source => TickFile.DayPath(AppPaths.Ticks, source, contextSymbol, DateOnly.FromDateTime(previousDay))))
                .ToDictionary(path => path, FileImage.Read);
            long combinedLength = HistorySources.DayFiles(AppPaths.Ticks, "rithmic", contextSymbol, DateOnly.FromDateTime(contextDay)).Sum(path => new FileInfo(path).Length);
            ExpectHours("Combined context baseline", "rithmic", contextSymbol,
                (previousDay.AddHours(9), 90), (contextDay.AddHours(10), 110), (contextDay.AddHours(11), 120), (contextDay.AddHours(12), 210), (contextDay.AddHours(13), 220));
            ExpectHours("Unchanged combined context", "rithmic", contextSymbol,
                (previousDay.AddHours(9), 90), (contextDay.AddHours(10), 110), (contextDay.AddHours(11), 120), (contextDay.AddHours(12), 210), (contextDay.AddHours(13), 220));
            ExpectHours("Primary-only context baseline", "rithmic-solo", contextSymbol,
                (previousDay.AddHours(9), 90), (contextDay.AddHours(10), 110), (contextDay.AddHours(11), 120));
            ExpectHours("Secondary-only context baseline", HistorySources.Databento, contextSymbol,
                (previousDay.AddHours(9), 990), (contextDay.AddHours(10), 910), (contextDay.AddHours(12), 210), (contextDay.AddHours(13), 220));
            ExpectHours("Unchanged-context-symbol baseline", "rithmic", contextControl,
                (previousDay.AddHours(9), 50), (contextDay.AddHours(10), 51), (contextDay.AddHours(11), 61), (contextDay.AddHours(12), 101), (contextDay.AddHours(13), 111));
            Require(File.Exists(ContextPath("rithmic", contextSymbol)) && File.Exists(ContextPath(HistorySources.Databento, contextSymbol)),
                "The initial hourly consumers must persist their closed-day caches before correction.");

            CorrectPreservingMetadata("Combined secondary-source correction", secondaryPath,
                () => WriteDay(secondary, contextDay, (10, 59, 910), (12, 59, 211), (13, 59, 220)));
            Require(HistorySources.DayFiles(AppPaths.Ticks, "rithmic", contextSymbol, DateOnly.FromDateTime(contextDay)).Sum(path => new FileInfo(path).Length) == combinedLength,
                "The combined source's aggregate length must also remain unchanged.");
            Require(HistorySources.ReadDay(AppPaths.Ticks, "rithmic", contextSymbol, DateOnly.FromDateTime(contextDay))
                .Select(trade => (trade.Time.Hour, trade.Price)).SequenceEqual(new[] { (10, 110m), (11, 120m), (12, 211m), (13, 220m) }),
                "The public composed disk reader must see the correction without the lower-priority overlapping print.");
            ExpectHours("Next combined context sees the equal-metadata constituent correction", "rithmic", contextSymbol,
                (previousDay.AddHours(9), 90), (contextDay.AddHours(10), 110), (contextDay.AddHours(11), 120), (contextDay.AddHours(12), 211), (contextDay.AddHours(13), 220));
            ExpectHours("Next secondary-only context sees its own correction", HistorySources.Databento, contextSymbol,
                (previousDay.AddHours(9), 990), (contextDay.AddHours(10), 910), (contextDay.AddHours(12), 211), (contextDay.AddHours(13), 220));
            ExpectHours("Primary-only context does not acquire secondary values", "rithmic-solo", contextSymbol,
                (previousDay.AddHours(9), 90), (contextDay.AddHours(10), 110), (contextDay.AddHours(11), 120));
            ExpectHours("Other-symbol context remains unchanged", "rithmic", contextControl,
                (previousDay.AddHours(9), 50), (contextDay.AddHours(10), 51), (contextDay.AddHours(11), 61), (contextDay.AddHours(12), 101), (contextDay.AddHours(13), 111));
            Check(unchangedContext.All(pair => FileImage.Read(pair.Key) == pair.Value),
                "Context correction changes only its owned secondary physical day", "Primary, earlier-day and other-symbol lengths, timestamps and digests remain unchanged");
        }
        catch (Exception error)
        {
            fixtureError = error.ToString();
            Check(false, "The fixture must reach its real consumers without an incidental failure", fixtureError);
        }
        finally
        {
            if (pending.Count > 0)
            {
                if (view != null) Field<CancellationTokenSource?>(view, "_cts")?.Cancel();
                while (pending.Any(task => !task.IsCompleted)) pump(0.01);
            }
            foreach (string path in files)
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception error) { Check(false, "Owned fixture file cleanup", path + ": " + error.Message); }
            }
            foreach (string path in directories)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
                catch (Exception error) { Check(false, "Owned fixture directory cleanup", path + ": " + error.Message); }
            }
            if (view != null && previousStrategy != null && File.Exists(previousStrategy))
            {
                try { Wait(view.OpenFileAsync(previousStrategy)); }
                catch (Exception error) { Check(false, "Restore the previous selected strategy", error.Message); }
            }
            catalog?.Refresh();
            string receipt = JsonSerializer.Serialize(new
            {
                scene = "research-freshness", fixtureError,
                fixturePreconditions = new { firstTickDayUtc = first, contextDayUtc = contextDay, mutations },
                checks, backtests, contextReads,
                authority = "Actual StrategiesView backtests, reopened StrategyStore runs, HistoricalData mutations and MarketConditions.HourlyBefore. Metadata checks establish only fixture preconditions. No IB source or stamp is changed.",
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outDir, "research-freshness-evidence.json"), receipt);
        }
        return checks.Count(check => !check.Passed);
    }
}
