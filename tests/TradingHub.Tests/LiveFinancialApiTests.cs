using System.Text.Json.Nodes;
using TradingHub.App.Lab;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[CollectionDefinition("LiveFinancialApi", DisableParallelization = true)]
public sealed class LiveFinancialApiCollection { }

[Collection("LiveFinancialApi")]
public sealed class LiveFinancialApiTests
{
    [Fact]
    public void Legacy_history_remains_an_estimate_instead_of_becoming_confirmed_net_results()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-live-financial-api-" + Guid.NewGuid().ToString("N"));
        string previousRoot = LiveStore.Root;
        string? previousEnvironment = Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("TRADINGHUB_LIVE_DIR", root);
            LiveStore.Root = root;
            const string key = "legacy-api|test:TEST|sim";
            const string confirmedKey = "confirmed-api|test:TEST|sim";
            var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            LiveStore.SaveRunners(new[]
            {
                new LiveRunnerInfo { Key = key, Strategy = "Legacy fixture", FeedId = "test", Symbol = "TEST", Period = "M1", Account = "sim", PointValue = 10m },
                new LiveRunnerInfo { Key = confirmedKey, Strategy = "Confirmed fixture", FeedId = "test", Symbol = "TEST", Period = "M1", Account = "sim", PointValue = 10m, AvailableOpenPnL = 0 },
            });
            Assert.Null(LiveStore.AppendFill(key, new LiveFill(time, true, 2m, 100m)));
            Assert.Null(LiveStore.AppendFill(key, new LiveFill(time.AddMinutes(1), false, 2m, 105m)));
            var scope = new ExecutionScope("fixture", "sim", "paper", "test:TEST", "financial-api");
            Assert.Null(LiveStore.AppendFill(confirmedKey, new LiveFill(time, true, 2m, 100m)
            {
                Kind = LiveFillKind.Execution, OrderId = "entry-order", ExecutionId = new(scope, "entry"),
                Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
            }));
            Assert.Null(LiveStore.AppendFill(confirmedKey, new LiveFill(time.AddMinutes(1), false, 2m, 105m)
            {
                Kind = LiveFillKind.Execution, OrderId = "exit-order", ExecutionId = new(scope, "exit"),
                Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
            }));

            var response = LabServer.LivePortfolio(JsonNode.Parse("""{"recent_trades":1}""")!.AsObject());
            var rows = response["strategies"]!.AsArray();
            var confirmed = rows.Single(row => row!["key"]!.GetValue<string>() == confirmedKey)!;
            Assert.Equal(94m, confirmed["net"]!.GetValue<decimal>());
            Assert.Equal(94m, Assert.Single(confirmed["last_trades"]!.AsArray())!["pnl"]!.GetValue<decimal>());
            var strategy = rows.Single(row => row!["key"]!.GetValue<string>() == key)!;
            // The preserved legacy estimate is (105 - 100) * 2 * 10 = 100; fees and execution provenance are unknown.
            Assert.Null(strategy["net"]);
            Assert.Equal(100m, strategy["net_estimate"]!.GetValue<decimal>());
            Assert.Equal("estimated", strategy["financial_quality"]!.GetValue<string>());
            var trade = Assert.Single(strategy["last_trades"]!.AsArray())!;
            Assert.Null(trade["pnl"]);
            Assert.Equal(100m, trade["pnl_estimate"]!.GetValue<decimal>());
            Assert.Equal("estimated", trade["financial_quality"]!.GetValue<string>());
            Assert.Equal("authoritative", confirmed["financial_quality"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRADINGHUB_LIVE_DIR", previousEnvironment);
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public void Saved_open_valuations_remain_estimates_after_corrections_until_committed_exposure_is_flat()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-stopped-valuation-api-" + Guid.NewGuid().ToString("N"));
        string previousRoot = LiveStore.Root;
        string? previousEnvironment = Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("TRADINGHUB_LIVE_DIR", root);
            LiveStore.Root = root;
            const string key = "stopped-valuation-api|test:TEST|sim";
            DateTime time = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var scope = new ExecutionScope("fixture", "sim", "paper", "test:TEST", "stopped-valuation");
            var entry = new LiveFill(time, true, 2m, 100m)
            {
                Kind = LiveFillKind.Execution, OrderId = "entry-order", ExecutionId = new(scope, "entry"),
                Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
            };
            Assert.Null(LiveStore.AppendFill(key, entry));
            LiveStore.SaveRunners(new[]
            {
                new LiveRunnerInfo { Key = key, Strategy = "Stopped valuation fixture", FeedId = "test", Symbol = "TEST",
                    Period = "M1", Account = "sim", PointValue = 10m, Position = 2m, OpenPnL = 17m, AvailableOpenPnL = 17m },
            });
            JsonNode Row() => LabServer.LivePortfolio(new JsonObject())["strategies"]!.AsArray()
                .Single(row => row!["key"]!.GetValue<string>() == key)!;

            var saved = Row();
            Assert.Equal("authoritative", saved["financial_quality"]!.GetValue<string>());
            Assert.Null(saved["open_pnl"]);
            Assert.Null(saved["net"]);
            Assert.Equal(17m, saved["open_pnl_estimate"]!.GetValue<decimal>());
            Assert.Equal(14m, saved["net_estimate"]!.GetValue<decimal>());

            Assert.Null(LiveStore.AppendFill(key, entry with { Price = 102m, Fees = new[] { new ExecutionFee(4m, "USD") } }));
            var corrected = Row();
            Assert.Null(corrected["open_pnl"]);
            Assert.Null(corrected["net"]);
            Assert.Equal(13m, corrected["net_estimate"]!.GetValue<decimal>());

            Assert.Null(LiveStore.AppendFill(key, new LiveFill(time.AddMinutes(1), false, 2m, 105m)
            {
                Kind = LiveFillKind.Execution, OrderId = "exit-order", ExecutionId = new(scope, "exit"),
                Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
            }));
            var flat = Row();
            Assert.Equal(0m, flat["position"]!.GetValue<decimal>());
            Assert.Equal(0m, flat["open_pnl"]!.GetValue<decimal>());
            Assert.Equal(53m, flat["net"]!.GetValue<decimal>()); // (105 - 102) * 2 * 10 - 4 - 3.
            Assert.Equal(53m, flat["net_estimate"]!.GetValue<decimal>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRADINGHUB_LIVE_DIR", previousEnvironment);
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Recovered_context_conflicts_block_confirmed_financials_without_discarding_saved_estimates()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-conflicted-financial-api-" + Guid.NewGuid().ToString("N"));
        string previousRoot = LiveStore.Root;
        string? previousEnvironment = Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("TRADINGHUB_LIVE_DIR", root);
            LiveStore.Root = root;
            const string key = "conflicted-api|test:TEST|sim";
            DateTime time = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            Guid generation = LiveStore.CaptureLedgerGeneration(root, key);
            LiveFill Execution(string context, string id, bool buy, decimal quantity, decimal price, int minute) =>
                new(time.AddMinutes(minute), buy, quantity, price, "TEST", 1m)
                {
                    Kind = LiveFillKind.Execution, OrderId = id + "-order", ContextId = context,
                    OwnerId = context + "-owner", OwnerGeneration = generation,
                    ExecutionId = new(new ExecutionScope("fixture", context, "paper", "test:TEST", "saved-valuation"), id),
                    Fees = new[] { new ExecutionFee(quantity, "USD") }, FeesComplete = true, Currency = "USD",
                };
            var entryA = Execution("context-A", "entry-A", true, 2m, 100m, 0);
            var exitA = Execution("context-A", "exit-A", false, 2m, 110m, 1);
            var entryB = Execution("context-B", "entry-B", true, 1m, 200m, 2);
            foreach (var fill in new[] { entryA, exitA, entryB }) Assert.Null(LiveStore.AppendFill(key, fill));
            LiveStore.SaveRunners(new[]
            {
                new LiveRunnerInfo { Key = key, Strategy = "Saved valuation fixture", FeedId = "test", Symbol = "TEST",
                    Period = "M1", Account = "sim", PointValue = 1m, Position = 1m, OpenPnL = 10m, AvailableOpenPnL = 10m },
            });
            JsonNode Row() => LabServer.LivePortfolio(new JsonObject())["strategies"]!.AsArray()
                .Single(row => row!["key"]!.GetValue<string>() == key)!;
            Assert.Null(Row()["net"]);
            Assert.Null(Row()["open_pnl"]);
            Assert.Equal(25m, Row()["net_estimate"]!.GetValue<decimal>()); // Saved B open10 is an estimate, not a current mark.

            Assert.Null(LiveStore.AppendFill(key, entryA with { Quantity = 1m, Fees = new[] { new ExecutionFee(1m, "USD") } }));
            var recovered = LiveStore.LoadLedger(key, 1m);
            Assert.Equal(0m, recovered.Position); // A short1 plus B long1, not a settled flat ledger.
            Assert.True(recovered.OpenContextConflict);
            var stats = Assert.Single(LiveStore.Stats());
            Assert.Null(stats.CompleteNet);
            var conflicted = Row();
            Assert.Null(conflicted["net"]);
            Assert.Null(conflicted["open_pnl"]);

            Assert.Null(LiveStore.AppendFill(key, exitA with { Quantity = 1m, Fees = new[] { new ExecutionFee(1m, "USD") } }));
            var reconciled = LiveStore.LoadLedger(key, 1m);
            Assert.Equal(1m, reconciled.Position);
            Assert.False(reconciled.OpenContextConflict);
            Assert.Null(Row()["net"]);
            Assert.Null(Row()["open_pnl"]);
            Assert.Equal(17m, Row()["net_estimate"]!.GetValue<decimal>()); // Closed A10 minus fees3, plus saved B open10 estimate.
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRADINGHUB_LIVE_DIR", previousEnvironment);
            LiveStore.Root = previousRoot;
            Directory.Delete(root, recursive: true);
        }
    }

}
