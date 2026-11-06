using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class IbSnapshotCrashTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-ib-snapshot-crash-" + Guid.NewGuid().ToString("N"));
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private string Data => Path.Combine(_root, "data");
    private static readonly DateTime First = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Second = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    // A deadlock watchdog, not evidence of publication or reader ordering.
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(30);

    public IbSnapshotCrashTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".ib-snapshot-fixture-root"), _owner);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_fresh_range_consumer_recovers_the_complete_old_two_day_snapshot_after_an_interrupted_import()
    {
        var instrument = IbBarImport.InstrumentFor("MNQ");
        HistoricalData.WriteDay(Data, instrument, DateOnly.FromDateTime(First),
            new[] { new Trade(101, First.AddSeconds(20), 90, 3, TradeSide.Buy) }, replace: true);
        HistoricalData.WriteDay(Data, instrument, DateOnly.FromDateTime(Second),
            new[] { new Trade(201, Second.AddSeconds(20), 190, 5, TradeSide.Sell) }, replace: true);
        string original = Input("original-csv", "100,102,99,101,12", "200,202,199,201,20");
        IbBarImport.Commit(IbBarImport.PreviewFolder(original, Data, "MNQ"), Data);
        // Both batches cover the same two days; neither replaces the unrelated recorded minute.
        // New synthetic prices would be [300,299,302,301] and [400,399,402,401], with 7/11 volume per print.
        Input("replacement-csv", "300,302,299,301,28", "400,402,399,401,44");
        var before = await ReadInFreshProcess();
        AssertOldRange(before.Historical);
        Assert.Equal(before.Historical, before.IbRange);

        await CrashAfterFirstPublishedFile();

        var recovered = await ReadInFreshProcess();
        AssertOldRange(recovered.Historical);
        Assert.Equal(before.Historical, recovered.Historical);
        Assert.Equal(before.Historical, recovered.IbRange);
        var repeated = await ReadInFreshProcess();
        Assert.Equal(recovered.Historical, repeated.Historical);
        Assert.Equal(recovered.IbRange, repeated.IbRange);
    }

    [Fact]
    public async Task A_fresh_context_consumer_reports_failed_ib_recovery_instead_of_successful_empty_history()
    {
        // The child maps TRADINGHUB_DATA_DIR to Data; actual AppPaths.Ticks is the nested Ticks directory.
        string ticks = Path.Combine(Data, "Ticks");
        string original = Input("original-csv", "100,102,99,101,12", "200,202,199,201,20");
        IbBarImport.Commit(IbBarImport.PreviewFolder(original, ticks, "MNQ"), ticks);
        Input("replacement-csv", "300,302,299,301,28", "400,402,399,401,44");
        var before = await ReadContextInFreshProcess();
        Assert.True(before.ExitCode == 0, before.Error);
        Assert.Equal(new[] { new ContextRow(First, 101m), new ContextRow(Second, 201m) }, before.Rows);

        await CrashAfterFirstPublishedFile("write-context");

        // Index 1 was paused before its rename: the earlier day is already new and must stay new if recovery refuses.
        string earlierTarget = TickFile.DayPath(ticks, IbBarStore.BarFolder, "MNQ", DateOnly.FromDateTime(First));
        Assert.Equal(new[] { 300m, 299m, 302m, 301m }, TickFile.Read(earlierTarget).Select(t => t.Price).ToArray());
        byte[] earlierNew = File.ReadAllBytes(earlierTarget);
        string laterTarget = TickFile.DayPath(ticks, IbBarStore.BarFolder, "MNQ", DateOnly.FromDateTime(Second));
        string prepared = laterTarget + ".unrelated";
        // Replace only the later public target, after joining the crashed writer; leave recovery authority untouched.
        using (var writer = new TickFile.Writer(prepared, IbBarImport.InstrumentFor("MNQ")))
            writer.Write(new Trade(-999, Second.AddMinutes(2), 777.25m, 17, TradeSide.None));
        File.Move(prepared, laterTarget, true);
        byte[] unrelated = File.ReadAllBytes(laterTarget);

        var failed = await ReadContextInFreshProcess();
        Assert.Equal(1, failed.ExitCode);
        Assert.Null(failed.Rows); // No successful result, not even an empty context list.
        Assert.Equal(earlierNew, File.ReadAllBytes(earlierTarget));
        Assert.Equal(unrelated, File.ReadAllBytes(laterTarget));
    }

    [Fact]
    public async Task A_same_length_ib_import_refreshes_saved_context_in_a_fresh_process_even_when_target_mtimes_are_preserved()
    {
        string ticks = Path.Combine(Data, "Ticks");
        string original = Input("original-csv", "100,102,99,101,12", "200,202,199,201,20");
        IbBarImport.Commit(IbBarImport.PreviewFolder(original, ticks, "MNQ"), ticks);
        string[] targets =
        {
            TickFile.DayPath(ticks, IbBarStore.BarFolder, "MNQ", DateOnly.FromDateTime(First)),
            TickFile.DayPath(ticks, IbBarStore.BarFolder, "MNQ", DateOnly.FromDateTime(Second)),
        };
        long[] lengths = targets.Select(path => new FileInfo(path).Length).ToArray();
        DateTime[] mtimes = targets.Select(File.GetLastWriteTimeUtc).ToArray();
        var primed = await ReadContextInFreshProcess();
        Assert.True(primed.ExitCode == 0, primed.Error);
        Assert.Equal(new[] { new ContextRow(First, 101m), new ContextRow(Second, 201m) }, primed.Rows);

        // Price-only quarter-point shifts keep both VTK payload lengths unchanged; volumes remain 12 and 20.
        string replacement = Input("replacement-csv", "100.25,102.25,99.25,101.25,12", "200.25,202.25,199.25,201.25,20");
        IbBarImport.Commit(IbBarImport.PreviewFolder(replacement, ticks, "MNQ"), ticks, IbBarImport.ImportPolicy.ReplaceCoveredMinutes);
        for (int i = 0; i < targets.Length; i++) File.SetLastWriteTimeUtc(targets[i], mtimes[i]);
        Assert.Equal(lengths, targets.Select(path => new FileInfo(path).Length).ToArray());
        Assert.Equal(mtimes, targets.Select(File.GetLastWriteTimeUtc).ToArray());

        var refreshed = await ReadContextInFreshProcess();
        Assert.True(refreshed.ExitCode == 0, refreshed.Error);
        Assert.Equal(new[] { new ContextRow(First, 101.25m), new ContextRow(Second, 201.25m) }, refreshed.Rows);
    }

    [Fact]
    public async Task A_long_lived_lab_backtest_refreshes_cached_ib_ticks_after_a_same_metadata_import()
    {
        string ticks = Path.Combine(Data, "Ticks");
        // The signal is a genuine earlier print, not an OHLC anchor retrospectively available inside its own minute.
        var instrument = IbBarImport.InstrumentFor("MNQ");
        HistoricalData.WriteDay(ticks, instrument, DateOnly.FromDateTime(First),
            new[] { new Trade(101, First.AddSeconds(20), 90, 3, TradeSide.Buy) }, replace: true);
        string signalPath = TickFile.DayPath(ticks, "ib", "MNQ", DateOnly.FromDateTime(First));
        byte[] signalBytes = File.ReadAllBytes(signalPath);
        string original = Input("original-csv", "100,102,99,101,12", "200,202,199,201,20");
        IbBarImport.Commit(IbBarImport.PreviewFolder(original, ticks, "MNQ"), ticks);
        string[] targets =
        {
            TickFile.DayPath(ticks, IbBarStore.BarFolder, "MNQ", DateOnly.FromDateTime(First)),
            TickFile.DayPath(ticks, IbBarStore.BarFolder, "MNQ", DateOnly.FromDateTime(Second)),
        };
        long[] lengths = targets.Select(path => new FileInfo(path).Length).ToArray();
        DateTime[] mtimes = targets.Select(File.GetLastWriteTimeUtc).ToArray();
        string strategies = Path.Combine(_root, "strategies");
        Directory.CreateDirectory(strategies);
        File.WriteAllText(Path.Combine(strategies, "SnapshotCacheProbe.cs"), """
            using TradingHub.Market;
            using TradingHub.Strategies;

            public sealed class SnapshotCacheProbe : Strategy
            {
                private bool _entered;
                protected override void OnTick(Trade trade)
                {
                    if (_entered) return;
                    _entered = true;
                    EnterLong(1);
                }
            }
            """);
        var arguments = new
        {
            file = "SnapshotCacheProbe", feed = "ib", symbol = "MNQ",
            from = First.ToString("O", CultureInfo.InvariantCulture),
            to = Second.AddMinutes(2).ToString("O", CultureInfo.InvariantCulture),
            period = "M1", capital = 1000m, commission_percent = 0m, commission_per_unit = 0m,
            slippage_ticks = 0, timezone = "UTC", session_start_hour = 0, include_trades = true,
        };

        using var lab = Start("lab");
        Task<string> stderr = lab.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal("lab-started", await lab.StandardOutput.ReadLineAsync().WaitAsync(Watchdog));
            await LabRequest(lab, 1, "initialize", new
            {
                protocolVersion = "2025-06-18", capabilities = new { },
                clientInfo = new { name = "owned-ib-cache-fixture", version = "1" },
            });
            var before = await LabBacktest(lab, 2, arguments);
            AssertLabTrade(before, 100m, 201m, 202m); // MNQ: (201 - 100) * 1 contract * 2 per point.

            // Keep the very same MCP process/configuration alive while a separate public import changes its source.
            string replacement = Input("replacement-csv", "100.25,102.25,99.25,101.25,12", "200.50,202.50,199.50,201.50,20");
            IbBarImport.Commit(IbBarImport.PreviewFolder(replacement, ticks, "MNQ"), ticks, IbBarImport.ImportPolicy.ReplaceCoveredMinutes);
            for (int i = 0; i < targets.Length; i++) File.SetLastWriteTimeUtc(targets[i], mtimes[i]);
            Assert.Equal(lengths, targets.Select(path => new FileInfo(path).Length).ToArray());
            Assert.Equal(mtimes, targets.Select(File.GetLastWriteTimeUtc).ToArray());
            Assert.Equal(signalBytes, File.ReadAllBytes(signalPath));

            var after = await LabBacktest(lab, 3, arguments);
            AssertLabTrade(after, 100.25m, 201.50m, 202.50m);

            lab.StandardInput.Close();
            await lab.WaitForExitAsync().WaitAsync(Watchdog);
            Assert.True(lab.ExitCode == 0, await stderr.WaitAsync(Watchdog));
        }
        finally
        {
            await StopOwnedChild(lab);
            await stderr.WaitAsync(Watchdog);
        }
    }

    private static void AssertLabTrade(JsonElement result, decimal entry, decimal exit, decimal net)
    {
        var trade = Assert.Single(JsonSerializer.Deserialize<ClosedTrade[]>(result.GetProperty("sample_trades").GetRawText())!);
        Assert.True(trade.IsLong);
        Assert.Equal(1m, trade.Quantity);
        // Both source windows are known at close; nominal quarter-minute display anchors are not execution clocks.
        Assert.Equal(First.AddMinutes(2), trade.EntryTime);
        Assert.Equal(Second.AddMinutes(2), trade.ExitTime);
        Assert.Equal(entry, trade.EntryPrice);
        Assert.Equal(exit, trade.ExitPrice);
        Assert.Equal(0m, trade.Commission);
        Assert.Equal(net, trade.Pnl);
        Assert.Equal(net, trade.NetPnl);
        Assert.Equal(net, result.GetProperty("metrics").GetProperty("net_profit").GetDecimal());
    }

    private static async Task<JsonElement> LabBacktest(Process lab, int id, object arguments)
    {
        var result = await LabRequest(lab, id, "tools/call", new { name = "run_backtest", arguments });
        Assert.False(result.GetProperty("isError").GetBoolean(), result.GetRawText());
        var content = Assert.Single(result.GetProperty("content").EnumerateArray());
        using var document = JsonDocument.Parse(content.GetProperty("text").GetString()!);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> LabRequest(Process lab, int id, string method, object parameters)
    {
        await lab.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await lab.StandardInput.FlushAsync();
        string? line = await lab.StandardOutput.ReadLineAsync().WaitAsync(Watchdog);
        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line);
        var response = document.RootElement;
        Assert.Equal(id, response.GetProperty("id").GetInt32());
        Assert.False(response.TryGetProperty("error", out _), line);
        return response.GetProperty("result").Clone();
    }

    private static void AssertOldRange(Row[] rows)
    {
        // Literal display locations, prices and quantities survive recovery without inventing unknown aggressor sides.
        Assert.Equal(new[]
        {
            (First.AddSeconds(20), 90m, 3m, TradeSide.Buy, false),
            (First.AddMinutes(1), 100m, 3m, TradeSide.None, true),
            (First.AddMinutes(1).AddSeconds(15), 99m, 3m, TradeSide.None, true),
            (First.AddMinutes(1).AddSeconds(30), 102m, 3m, TradeSide.None, true),
            (First.AddMinutes(1).AddSeconds(45), 101m, 3m, TradeSide.None, true),
            (Second.AddSeconds(20), 190m, 5m, TradeSide.Sell, false),
            (Second.AddMinutes(1), 200m, 5m, TradeSide.None, true),
            (Second.AddMinutes(1).AddSeconds(15), 199m, 5m, TradeSide.None, true),
            (Second.AddMinutes(1).AddSeconds(30), 202m, 5m, TradeSide.None, true),
            (Second.AddMinutes(1).AddSeconds(45), 201m, 5m, TradeSide.None, true),
        }, rows.Select(row => (row.Time, row.Price, row.Volume, row.Side, row.Id < 0)).ToArray());
        Assert.Equal(101, rows[0].Id);
        Assert.Equal(201, rows[5].Id);
        Assert.All(rows, row => Assert.Equal("ib:MNQ", row.Provenance.SourceIdentity));
        Assert.All(new[] { rows[0], rows[5] }, row =>
        {
            Assert.True(row.IsObservedTrade);
            Assert.Equal(DataQuality.Raw, row.Provenance.Quality);
            Assert.Equal(1, row.Provenance.ObservedTradeCount);
            Assert.True(row.Provenance.HasKnownAvailability);
            Assert.Equal(row.Time, row.AvailableAt);
        });
        foreach (var (offset, open) in new[] { (1, First.AddMinutes(1)), (6, Second.AddMinutes(1)) })
            Assert.All(rows.Skip(offset).Take(4), row =>
            {
                Assert.False(row.IsObservedTrade);
                Assert.Equal(DataQuality.Synthetic | DataQuality.InferredSide, row.Provenance.Quality);
                Assert.Equal(0, row.Provenance.ObservedTradeCount);
                Assert.Equal(60, row.Provenance.ResolutionSeconds);
                Assert.Equal(0, row.Provenance.ResolutionOffsetSeconds);
                Assert.True(row.Provenance.HasKnownAvailability);
                Assert.Equal(open.AddMinutes(1), row.AvailableAt);
                Assert.Equal((open, open.AddMinutes(1)), row.Provenance.SourceWindow(row.Time));
            });
    }

    private string Input(string directory, string firstBar, string secondBar)
    {
        string folder = Path.Combine(_root, directory);
        Directory.CreateDirectory(folder);
        long first = new DateTimeOffset(First.AddMinutes(1)).ToUnixTimeSeconds();
        long second = new DateTimeOffset(Second.AddMinutes(1)).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(folder, "MNQ.csv"),
            $"time,open,high,low,close,volume\n{first},{firstBar}\n{second},{secondBar}\n");
        return folder;
    }

    private async Task CrashAfterFirstPublishedFile(string operation = "write")
    {
        using var writer = Start(operation);
        Task<string> stderr = writer.StandardError.ReadToEndAsync();
        try
        {
            string? checkpoint = await writer.StandardOutput.ReadLineAsync().WaitAsync(Watchdog);
            Assert.True(checkpoint == "before-publish:1", $"Writer checkpoint: {checkpoint ?? "<none>"}; " +
                (writer.HasExited ? await stderr.WaitAsync(Watchdog) : "child still running"));
            // Kill only the Process returned by our own launch, while Commit is blocked inside its public callback.
            writer.Kill();
            await writer.WaitForExitAsync().WaitAsync(Watchdog);
        }
        finally
        {
            await StopOwnedChild(writer);
            await stderr.WaitAsync(Watchdog);
        }
    }

    private async Task<Snapshot> ReadInFreshProcess()
    {
        using var reader = Start("read", First.ToString("O", CultureInfo.InvariantCulture), Second.AddMinutes(2).ToString("O", CultureInfo.InvariantCulture));
        Task<string> stderr = reader.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal("reader-started", await reader.StandardOutput.ReadLineAsync().WaitAsync(Watchdog));
            string? json = await reader.StandardOutput.ReadLineAsync().WaitAsync(Watchdog);
            await reader.WaitForExitAsync().WaitAsync(Watchdog);
            string error = await stderr.WaitAsync(Watchdog);
            Assert.True(reader.ExitCode == 0, $"Range reader exited with code {reader.ExitCode}: {error}");
            Assert.NotNull(json);
            return JsonSerializer.Deserialize<Snapshot>(json)!;
        }
        finally
        {
            await StopOwnedChild(reader);
            await stderr.WaitAsync(Watchdog);
        }
    }

    private async Task<ContextRead> ReadContextInFreshProcess()
    {
        using var reader = Start("context-read", Second.AddHours(1).ToString("O", CultureInfo.InvariantCulture));
        Task<string> stderr = reader.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal("context-reader-started", await reader.StandardOutput.ReadLineAsync().WaitAsync(Watchdog));
            string? json = await reader.StandardOutput.ReadLineAsync().WaitAsync(Watchdog);
            await reader.WaitForExitAsync().WaitAsync(Watchdog);
            string error = await stderr.WaitAsync(Watchdog);
            return new(reader.ExitCode, json == null ? null : JsonSerializer.Deserialize<ContextRow[]>(json), error);
        }
        finally
        {
            await StopOwnedChild(reader);
            await stderr.WaitAsync(Watchdog);
        }
    }

    private Process Start(string operation, params string[] arguments)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "IbSnapshotCrashFixture", "TradingHub.IbSnapshotCrashFixture.dll");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _root,
        };
        foreach (string argument in new[] { fixture, _root, _owner, operation }.Concat(arguments)) start.ArgumentList.Add(argument);
        foreach (string store in new[] { "DATA", "STRATEGIES", "INDICATORS", "BACKTESTS", "LIVE" })
            start.Environment["TRADINGHUB_" + store + "_DIR"] = Path.Combine(_root, store.ToLowerInvariant());
        start.Environment["TRADINGHUB_KEYCHAIN_FILE"] = Path.Combine(_root, "fake-keychain.json");
        start.Environment["TRADINGHUB_SLEEP_GUARD"] = "off";
        return Process.Start(start)!;
    }

    private static async Task StopOwnedChild(Process child)
    {
        if (!child.HasExited)
        {
            try { child.Kill(); }
            catch (InvalidOperationException) when (child.HasExited) { }
        }
        await child.WaitForExitAsync().WaitAsync(Watchdog);
    }

    private sealed record Row(long Id, DateTime Time, decimal Price, decimal Volume, TradeSide Side,
        TradeProvenance Provenance, DateTime AvailableAt, bool IsObservedTrade);
    private sealed record Snapshot(Row[] Historical, Row[] IbRange);
    private sealed record ContextRow(DateTime Time, decimal Close);
    private sealed record ContextRead(int ExitCode, ContextRow[]? Rows, string Error);
}
