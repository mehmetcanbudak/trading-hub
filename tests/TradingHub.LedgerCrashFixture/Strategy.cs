using TradingHub.Strategies;

namespace TradingHub.Tests;

// Matches the fixture's legacy strategy identity without placing any orders.
internal sealed class Strategy : TradingHub.Strategies.Strategy
{
    protected override void OnBar(Bar bar) { }
}
