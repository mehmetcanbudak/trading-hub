using System.Globalization;
using System.Text.Json;
using TradingHub.App.Lab;
using TradingHub.App.Services;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.IbSnapshotCrashFixture;

internal static class Program
{
    private static int Main(string[] args)
    {
        // This executable never accepts an implicit store or an unmarked/user-owned root.
        if (args.Length < 3 || !Path.IsPathFullyQualified(args[0]) || !Guid.TryParseExact(args[1], "N", out _))
        {
            Console.Error.WriteLine("The fixture requires an absolute root, its owner token and an operation.");
            return 64;
        }
        string root = Path.GetFullPath(args[0]);
        string relative = Path.GetRelativePath(Path.GetTempPath(), root);
        if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relative) || !Path.GetFileName(root).StartsWith("TradingHub-ib-snapshot-crash-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"The fixture root '{root}' is not an owned child of temporary directory '{Path.GetTempPath()}'.");
            return 64;
        }
        try
        {
            string marker = Path.Combine(root, ".ib-snapshot-fixture-root");
            if (!File.Exists(marker) || File.ReadAllText(marker) != args[1] ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                Console.Error.WriteLine($"The fixture root '{root}' has no matching ownership marker or is a linked directory.");
                return 64;
            }
            string data = Path.Combine(root, "data");
            if ((args[2] is "write" or "write-context") && args.Length == 3)
            {
                if (args[2] == "write-context") data = Path.Combine(data, "Ticks");
                var preview = IbBarImport.PreviewFolder(Path.Combine(root, "replacement-csv"), data, "MNQ");
                IbBarImport.Commit(preview, data, IbBarImport.ImportPolicy.ReplaceCoveredMinutes, beforePublish: index =>
                {
                    if (index != 1) return;
                    // Public Commit has already replaced its first target. Only the owning test kills this process.
                    Console.WriteLine("before-publish:1");
                    Console.Out.Flush();
                    if (Console.ReadLine() != "continue") throw new IOException("The owning test closed the crash checkpoint.");
                });
                throw new InvalidOperationException("The owning test did not stop the writer at its crash checkpoint.");
            }
            if (args[2] == "read" && args.Length == 5)
            {
                var from = DateTime.ParseExact(args[3], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var to = DateTime.ParseExact(args[4], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                Console.WriteLine("reader-started");
                Console.Out.Flush();
                // Both are complete public range consumers, not journal/file inspections.
                var historical = HistoricalData.Read(data, "ib", "MNQ", from, to).Select(Row.From).ToArray();
                var ibRange = IbBarStore.LoadRange(data, "MNQ", from, to).Select(Row.From).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new Snapshot(historical, ibRange)));
                Console.Out.Flush();
                return 0;
            }
            // AppPaths resolves existing ancestor links, so expected store paths must use the same normalization.
            string expectedRoot = new StorageLocationStore(root, root).BootstrapRoot;
            string expectedData = Path.Combine(expectedRoot, "data");
            if (args[2] == "context-read" && args.Length == 4)
            {
                // Environment overrides are supplied before this process starts, never after AppPaths initialization.
                if (AppPaths.Root != expectedData || AppPaths.Ticks != Path.Combine(expectedData, "Ticks"))
                    throw new InvalidOperationException(
                        $"The context reader's app stores are not its explicit owned fixture. Expected root '{expectedData}' and ticks '{Path.Combine(expectedData, "Ticks")}'. " +
                        $"Actual root '{AppPaths.Root}' and ticks '{AppPaths.Ticks}'.");
                var until = DateTime.ParseExact(args[3], "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                Console.WriteLine("context-reader-started");
                Console.Out.Flush();
                var hours = MarketConditions.HourlyBefore("ib", "MNQ", until);
                Console.WriteLine(JsonSerializer.Serialize(hours.Select(h => new ContextRow(h.Time, h.Close)).ToArray()));
                Console.Out.Flush();
                return 0;
            }
            if (args[2] == "lab" && args.Length == 3)
            {
                if (AppPaths.Root != expectedData || AppPaths.Ticks != Path.Combine(expectedData, "Ticks") ||
                    AppPaths.Strategies != Path.Combine(expectedRoot, "strategies") ||
                    Environment.GetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR") != Path.Combine(root, "backtests"))
                    throw new InvalidOperationException(
                        $"The laboratory's stores are not its explicit owned fixture. Expected root '{expectedData}', ticks '{Path.Combine(expectedData, "Ticks")}', strategies '{Path.Combine(expectedRoot, "strategies")}' and backtests override '{Path.Combine(root, "backtests")}'. " +
                        $"Actual root '{AppPaths.Root}', ticks '{AppPaths.Ticks}', strategies '{AppPaths.Strategies}' and backtests override '{Environment.GetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR")}'.");
                Console.WriteLine("lab-started");
                Console.Out.Flush();
                return LabServer.RunAsync().GetAwaiter().GetResult();
            }
            Console.Error.WriteLine($"The fixture operation '{args[2]}' does not accept {args.Length - 3} extra arguments.");
            return 64;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private sealed record Row(long Id, DateTime Time, decimal Price, decimal Volume, TradeSide Side,
        TradeProvenance Provenance, DateTime AvailableAt, bool IsObservedTrade)
    {
        public static Row From(Trade trade) => new(trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side,
            trade.Provenance, trade.AvailableAt, trade.IsObservedTrade);
    }

    private sealed record Snapshot(Row[] Historical, Row[] IbRange);
    private sealed record ContextRow(DateTime Time, decimal Close);
}
