using TradingHub.App.Services;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed class LiveValuationTests
{
    [Fact]
    public void A_missing_mark_is_unavailable_for_open_exposure_but_does_not_erase_the_legacy_estimate()
    {
        var ledger = new LiveLedger("valuation-fixture", 10m);
        var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var scope = new ExecutionScope("valuation-fixture", "sim", "paper", "test:TEST");
        ledger.Apply(new LiveFill(time, true, 2m, 100m)
        {
            Kind = LiveFillKind.Execution, OrderId = "entry", ExecutionId = new(scope, "entry"),
            Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
        });

        // A known mark values the long two: (105 - 100) * 2 * 10 = 100 gross unrealized.
        Assert.Equal(100m, ledger.KnownOpenPnL(105m));
        Assert.Equal(100m, ledger.OpenPnL(105m));
        Assert.Null(ledger.KnownOpenPnL(0));
        Assert.Equal(100m, ledger.OpenPnL(0));

        ledger.Apply(new LiveFill(time.AddMinutes(1), false, 2m, 105m)
        {
            Kind = LiveFillKind.Execution, OrderId = "exit", ExecutionId = new(scope, "exit"),
            Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
        });
        Assert.Equal(0m, ledger.Position);
        Assert.Equal(0m, ledger.KnownOpenPnL(0)); // Flat exposure has a confirmed zero open value without a mark.
    }

    [Fact]
    public void Stopped_exposure_has_no_current_open_valuation_but_flat_history_has_confirmed_zero()
    {
        var ledger = new LiveLedger("stopped-valuation-fixture", 10m);
        var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var scope = new ExecutionScope("valuation-fixture", "sim", "paper", "test:TEST");
        ledger.Apply(new LiveFill(time, true, 2m, 100m)
        {
            Kind = LiveFillKind.Execution, OrderId = "entry", ExecutionId = new(scope, "entry"),
            Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
        });
        var entry = new LiveEntry
        {
            Key = "stopped-valuation-fixture", Source = "fixture", Ledger = ledger,
            Info = new LiveRunnerInfo { Key = "stopped-valuation-fixture", PointValue = 10m },
        };

        Assert.Null(entry.OpenPnL);
        Assert.Null(entry.Net); // Confirmed execution costs do not imply a current mark for a stopped open position.
        ledger.Apply(new LiveFill(time.AddMinutes(1), false, 2m, 105m)
        {
            Kind = LiveFillKind.Execution, OrderId = "exit", ExecutionId = new(scope, "exit"),
            Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
        });
        Assert.Equal(0m, entry.OpenPnL);
        Assert.Equal(94m, entry.Net); // Flat history needs no market mark: 100 gross - 6 fees.
    }
}
