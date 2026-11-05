using System.Text.Json;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public sealed class LiveLedgerConfirmedCostsTests
{
    private static readonly DateTime Start = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Confirmed_costs_are_conserved_across_partial_close_reverse_curve_and_fee_correction()
    {
        var ledger = new LiveLedger("confirmed-fee-fixture", 10);
        ledger.Apply(Fill("a", 0, true, 3, 100, 3));
        Assert.Equal(-3m, ledger.Realized);
        ledger.Apply(Fill("b", 1, false, 1, 110, 1));
        Assert.Equal(96m, ledger.Realized);
        Assert.Equal(98m, Assert.Single(ledger.Trades).NetPnl);
        ledger.Apply(Fill("c", 2, false, 4, 90, 4));
        Assert.Equal(-2m, ledger.Position);
        Assert.Equal(90m, ledger.AveragePrice);
        Assert.Equal(-108m, ledger.Realized);
        ledger.Apply(Fill("d", 3, true, 2, 80, 2));
        Assert.Equal(0m, ledger.Position);
        Assert.Equal(10m, ledger.Fees);
        Assert.Equal(90m, ledger.Realized);
        Assert.True(ledger.CostsComplete);
        Assert.Equal(new[] { 2m, 4m, 4m }, ledger.Trades.Select(t => t.Commission));
        Assert.Equal(ledger.Realized, ledger.Trades.Sum(t => t.NetPnl));
        Assert.Equal(1090m, ledger.Curve(1000)[^1].Equity);

        var corrected = Fill("b", 1, false, 1, 110, 2);
        ledger.Apply(corrected);
        long revision = ledger.Revision;
        ledger.Apply(corrected);
        Assert.Equal(revision, ledger.Revision);
        Assert.Equal(4, ledger.Fills.Count);
        Assert.Equal(11m, ledger.Fees);
        Assert.Equal(89m, ledger.Realized);
        Assert.Equal(ledger.Realized, ledger.Trades.Sum(t => t.NetPnl));
        Assert.Equal(1089m, ledger.Curve(1000)[^1].Equity);
        Assert.Equal(Start.AddSeconds(1), ledger.Execution("b")!.Time);
    }

    [Fact]
    public void Legacy_rows_and_foreign_currency_fees_remain_unknown_not_confirmed_zero()
    {
        const string legacy = "{\"Time\":\"2026-09-07T12:00:00Z\",\"Buy\":true,\"Quantity\":1,\"Price\":100}";
        var fill = JsonSerializer.Deserialize<LiveFill>(legacy)!;
        Assert.Null(fill.ExecutionId);
        Assert.Null(fill.Fee);
        Assert.Null(fill.AccountFee);
        var ledger = new LiveLedger("legacy-cost-fixture", 10);
        ledger.Apply(fill);
        ledger.Apply(new LiveFill(Start.AddSeconds(1), false, 1, 110, ExecutionId: "foreign-fee", Fee: 2, FeeCurrency: "OTHER"));
        Assert.False(ledger.CostsComplete);
        Assert.Equal(100m, ledger.Realized);
        Assert.Equal(2m, ledger.Execution("foreign-fee")!.Fee);
        Assert.Null(ledger.Execution("foreign-fee")!.AccountFee);
        Assert.DoesNotContain("ExecutionId", JsonSerializer.Serialize(fill));
        Assert.DoesNotContain("AccountFee", JsonSerializer.Serialize(fill));
    }

    private static LiveFill Fill(string id, int seconds, bool buy, decimal quantity, decimal price, decimal fee) =>
        new(Start.AddSeconds(seconds), buy, quantity, price, "FUTURE", 10, id, fee, "USD", fee);
}
