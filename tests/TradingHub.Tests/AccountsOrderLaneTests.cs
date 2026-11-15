using System.Reflection;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Accounts-page Cancel/Close on native Rithmic and Tradovate accounts use the account's FIFO order lane, like the chart's
/// gestures: a close issued after a queued chart gesture runs after it, never ahead of it while an earlier lane entry is
/// still inside a slow durable write.
/// </summary>
public sealed partial class AccountsRithmicCloseRegressionTests
{
    [Fact]
    public async Task Accounts_rithmic_close_runs_after_a_chart_market_buy_queued_behind_a_slow_durable_write()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 1, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        fixture.Profile.AllowRealOrders = true;
        var chart = fixture.Chart("ES");
        await Until(() => chart.PositionReady && chart.OrdersReady);
        var row = fixture.Position();
        var captured = Assert.IsType<RithmicPositionTarget>(row.Ref);
        Assert.Same(chart.OrderContext, captured.Broker.OrderContext);
        var lanes = fixture.Services.OrderLanes;

        using var durableWrite = new ManualResetEventSlim();
        int closesWhenBuyRan = -1;
        // G0: an earlier gesture on this account, still inside a slow durable write.
        var g0 = lanes.RunAsync(chart, "Slow durable write", () => durableWrite.Wait(Deadline));
        // G1: the confirmed chart market buy, queued behind it.
        var g1 = lanes.RunAsync(chart, "Buy MKT 1", () =>
        {
            closesWhenBuyRan = captured.Confirmation.Context.PositionCloses().Count;
            return OrderActions.Manual(chart, OrderTypes.Market, OrderDirections.Buy, 1);
        });
        Task<string?>? close = null;
        try
        {
            // The Accounts close, issued after the buy: it queues behind G1 instead of starting now.
            close = fixture.Center.CloseAsync(fixture.Account, row);
            Assert.Equal(new[] { "Slow durable write", "Buy MKT 1", "Close position" }, lanes.Pending);
            Assert.False(close.IsCompleted);
            Assert.Empty(captured.Confirmation.Context.PositionCloses());
            Assert.Empty(Tags(server));
        }
        finally { durableWrite.Set(); }

        Assert.True(await g0.WaitAsync(Deadline));
        var buy = await g1.WaitAsync(Deadline);
        Assert.Equal(0, closesWhenBuyRan);
        string? outcome = await close.WaitAsync(Deadline);
        Assert.Empty(lanes.Pending);
        Assert.Single(captured.Confirmation.Context.PositionCloses());
        Assert.True(outcome == null, outcome);
        // The buy's native submission completes asynchronously after its account call. Whichever reaches native admission
        // first, the buy never reaches the venue after the close: either it precedes the close's sell, or the close's
        // contract reservation (taken after the buy was handed over) refuses it and the account ends flat.
        string Diagnostics() => $"buy {buy.Status}/{buy.Filled} ({chart.LastError}), tags {Tags(server).Length}, net {fixture.Net}, server {server.Position}, orders " +
            string.Join("; ", fixture.Session.OrdersOf("ACC1").Select(order =>
                $"{(order.IsBuy ? "buy" : "sell")} {order.Quantity} type {order.PriceType} filled {order.Filled} done {order.Done} {order.Reason}"));
        await Until(() => (buy.Status == OrderStatus.Rejected || buy.Filled == 1) &&
            fixture.Session.OrdersOf("ACC1").Any(order => !order.IsBuy && order.PriceType == 2 && order.Done) &&
            fixture.Net == (buy.Status == OrderStatus.Rejected ? 0 : 1), Diagnostics);
        string[] tags = Tags(server);
        var orders = fixture.Session.OrdersOf("ACC1");
        var sell = Assert.Single(orders, order => !order.IsBuy);
        Assert.Equal(1m, sell.Filled); // exactly the confirmed contract
        if (buy.Status == OrderStatus.Rejected)
        {
            Assert.Equal(new[] { sell.UserTag }, tags);
            Assert.Equal(0, server.Position);
        }
        else
        {
            Assert.Equal(2, tags.Length);
            Assert.True(orders.Single(order => order.UserTag == tags[0]).IsBuy, Diagnostics());
            Assert.Equal(sell.UserTag, tags[1]);
            Assert.Equal(1, server.Position);
        }
    }

    [Fact]
    public async Task Accounts_tradovate_cancel_and_close_run_after_a_chart_market_buy_queued_behind_a_slow_durable_write()
    {
        using var tradovate = new FakeTradovate();
        var normalHttp = tradovate.Server.OnHttp!;
        Task? g1 = null;
        var arrivals = new List<(string Route, bool BuyHandedOff)>();
        tradovate.Server.OnHttp = request =>
        {
            if (request.Route is "/v1/order/placeorder" or "/v1/order/cancelorder" or "/v1/order/liquidateposition")
                lock (arrivals) arrivals.Add((request.Route, Volatile.Read(ref g1)?.IsCompleted == true));
            return request.Route == "/v1/order/liquidateposition" ? (200, "{}") : normalHttp(request);
        };
        using var feed = new TradovateFeed(() => tradovate.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        using var rithmic = new FakeRithmicServer();
        await using var fixture = new AccountsCloseFixture(rithmic);
        var profile = new ConnectionProfile
        {
            Id = "accounts-lane-tradovate-" + Guid.NewGuid().ToString("N"), Kind = ConnectionKind.Tradovate,
            Name = "Accounts lane Tradovate fixture", Enabled = true, TradovateUser = "demo",
            TradingNamespace = Guid.NewGuid().ToString("N"), TradingGeneration = Guid.NewGuid().ToString("N"),
        };
        fixture.Services.Connections.Profiles.Add(profile);
        Private<Dictionary<string, TradovateTradingSession>>(fixture.Services, "_tradovateSessions")[profile.Id] = session;
        session.Start();
        await Until(() => session.SnapshotReady);
        Assert.True(session.IsDemo);
        using var data = new InstrumentData(new Instrument
        {
            Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD",
        }, new SimulatedFeed()) { RecordLive = false };
        string accountId = AppServices.TradovateAccountId(profile, 101);
        var chart = fixture.Services.AccountFor(data, accountId);
        Assert.IsAssignableFrom<IThreadSafeOrderEntry>(chart);
        await Until(() => chart.PositionReady && chart.OrdersReady);
        var account = new AccountEntry(accountId, "DEMO101 · fixture (demo)", AccountKind.Tradovate, false, true, profile, "DEMO101");
        var working = new OrderLine("tradovate-9001", DateTime.UtcNow, tradovate.Contract, true, "Limit", 1, 4000, 0, 0, "Active", true,
            new TradovateOrderState { Id = 9001, AccountId = 101, ContractId = 555, Status = "Working" });
        var position = new PositionLine(tradovate.Contract, tradovate.Contract, 1, 5000, 0, 0,
            new TradovatePositionState { AccountId = 101, ContractId = 555, Symbol = tradovate.Contract, Net = 1 });
        var lanes = fixture.Services.OrderLanes;

        using var durableWrite = new ManualResetEventSlim();
        var g0 = lanes.RunAsync(chart, "Slow durable write", () => durableWrite.Wait(Deadline));
        var buy = lanes.RunAsync(chart, "Buy MKT 1", () => OrderActions.Manual(chart, OrderTypes.Market, OrderDirections.Buy, 1));
        Volatile.Write(ref g1, buy);
        Task? cancel = null;
        Task<string?>? close = null;
        try
        {
            cancel = fixture.Center.CancelAsync(account, working);
            close = fixture.Center.CloseAsync(account, position);
            Assert.Equal(new[] { "Slow durable write", "Buy MKT 1", "Cancel order", "Close position" }, lanes.Pending);
            Assert.False(cancel.IsCompleted);
            Assert.False(close.IsCompleted);
            lock (arrivals) Assert.Empty(arrivals);
        }
        finally { durableWrite.Set(); }

        Assert.True(await g0.WaitAsync(Deadline));
        await buy.WaitAsync(Deadline);
        await cancel.WaitAsync(Deadline);
        Assert.Null(await close.WaitAsync(Deadline));
        Assert.Empty(lanes.Pending);
        await Until(() => { lock (arrivals) return arrivals.Count == 3; });
        lock (arrivals)
        {
            // The Accounts requests started only after the chart's buy had been handed to its account.
            Assert.Equal(new[] { true, true }, arrivals.Where(a => a.Route != "/v1/order/placeorder").Select(a => a.BuyHandedOff));
            Assert.Equal(new[] { "/v1/order/cancelorder", "/v1/order/liquidateposition", "/v1/order/placeorder" },
                arrivals.Select(a => a.Route).OrderBy(route => route, StringComparer.Ordinal));
        }
    }

    private static T Private<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
