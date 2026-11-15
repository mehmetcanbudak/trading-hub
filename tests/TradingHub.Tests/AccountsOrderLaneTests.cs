using System.Reflection;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// Why a Rithmic buy cannot follow an Accounts close to the venue: while the close reserves the contract (here its
    /// position report is held after its sell filled), a buy reaching native admission is refused as new exposure.
    /// </summary>
    [Fact]
    public async Task Rithmic_buy_admitted_while_a_close_reserves_the_contract_is_refused()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        fixture.Profile.AllowRealOrders = true;
        var chart = fixture.Chart("ES");
        await Until(() => chart.PositionReady && chart.OrdersReady);
        var row = fixture.Position();
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PositionUpdateRelease = positionRelease;
        Task<string?>? close = null;
        Order? buy = null;
        try
        {
            close = fixture.Center.CloseAsync(fixture.Account, row);
            await server.PositionUpdateRequested.Task.WaitAsync(Deadline);
            await Until(() => fixture.Session.OrdersOf("ACC1").Any(order => !order.IsBuy && order.PriceType == 2 && order.Done));
            Assert.Equal(2m, fixture.Net);
            buy = OrderActions.Manual(chart, OrderTypes.Market, OrderDirections.Buy, 1);
            await Until(() => buy.Status == OrderStatus.Rejected, () => $"buy {buy.Status}, {chart.LastError}");
            Assert.Contains("A bounded Rithmic close reserves this native contract; new exposure is disabled.", chart.LastError);
            Assert.Single(Tags(server));
        }
        finally
        {
            positionRelease.TrySetResult();
            if (close != null) await close.WaitAsync(Deadline);
        }
        Assert.Null(await close);
        await Until(() => fixture.Net == 0);
        Assert.Single(Tags(server)); // the refused buy never reached the venue
    }

    [Fact]
    public async Task Accounts_tradovate_cancel_and_close_run_after_a_chart_market_buy_queued_behind_a_slow_durable_write()
    {
        using var tradovate = new FakeTradovate();
        tradovate.SeedPosition(1);
        var normalHttp = tradovate.Server.OnHttp!;
        var buyReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = new List<string>(); // order requests in arrival order
        tradovate.Server.OnHttpAsync = async (request, _) =>
        {
            if (request.Route is "/v1/order/placeorder" or "/v1/order/cancelorder" or "/v1/order/liquidateposition")
            {
                lock (arrivals) arrivals.Add(request.Route);
                // The buy's request reached the venue; its reply is still outstanding.
                if (request.Route == "/v1/order/placeorder") await buyReply.Task;
            }
            return normalHttp(request);
        };
        string[] Arrivals() { lock (arrivals) return arrivals.ToArray(); }
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
        Task<string?>? cancel = null;
        Task<string?>? close = null;
        try
        {
            cancel = fixture.Center.CancelAsync(account, working);
            close = fixture.Center.CloseAsync(account, position);
            Assert.Equal(new[] { "Slow durable write", "Buy MKT 1", "Cancel order", "Close position" }, lanes.Pending);
            Assert.False(cancel.IsCompleted);
            Assert.False(close.IsCompleted);
            Assert.Empty(Arrivals());
        }
        finally { durableWrite.Set(); }

        try
        {
            Assert.True(await g0.WaitAsync(Deadline));
            await buy.WaitAsync(Deadline);
            await Until(() => Arrivals().Length > 0);
            // The Accounts cancel and close queue behind the buy's request until the venue answers it.
            await Until(() => Arrivals().Length > 1 || session.QueuedSends(101) > 0);
            Assert.Equal(new[] { "/v1/order/placeorder" }, Arrivals());
        }
        finally { buyReply.TrySetResult(); }

        Assert.Null(await cancel.WaitAsync(Deadline));
        Assert.Null(await close.WaitAsync(Deadline));
        Assert.Empty(lanes.Pending);
        await Until(() => Arrivals().Length == 3);
        Assert.Equal(new[] { "/v1/order/placeorder", "/v1/order/cancelorder", "/v1/order/liquidateposition" }, Arrivals());
        // liquidateposition is sized by the venue when it processes it, after the earlier buy: 1 + 1 closed, flat.
        await Until(() => tradovate.NetPosition == 0 && session.PositionFor(101, 555)?.Net == 0);
    }

    /// <summary>Quit after a confirmed Close position that waits for an earlier buy's reply: the close is sent before
    /// shutdown completes (and closes brokers and connections).</summary>
    [Fact]
    public async Task Quit_sends_a_tradovate_close_queued_behind_an_unanswered_buy_before_shutdown_completes()
    {
        using var tradovate = new FakeTradovate { TimestampPositions = true };
        tradovate.SeedPosition(1);
        var normalHttp = tradovate.Server.OnHttp!;
        var buyReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int holdBuy = 1;
        Task? quit = null;
        var arrivals = new List<(string Action, bool QuitDone)>();
        tradovate.Server.OnHttpAsync = async (request, _) =>
        {
            if (request.Route == "/v1/order/placeorder")
            {
                string action = JsonNode.Parse(request.Body)!["action"]!.GetValue<string>();
                lock (arrivals) arrivals.Add((action, Volatile.Read(ref quit)?.IsCompleted == true));
                if (action == "Buy" && Interlocked.Exchange(ref holdBuy, 0) == 1) await buyReply.Task;
            }
            return normalHttp(request);
        };
        (string Action, bool QuitDone)[] Arrivals() { lock (arrivals) return arrivals.ToArray(); }
        using var feed = new TradovateFeed(() => tradovate.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        using var orders = new NativeOrderTestFixture();
        using var rithmic = new FakeRithmicServer();
        await using var fixture = new AccountsCloseFixture(rithmic);
        Private<Dictionary<string, TradovateTradingSession>>(fixture.Services, "_tradovateSessions")["quit-tradovate"] = session;
        session.Start();
        await Until(() => session.SnapshotReady);
        using var data = new InstrumentData(new Instrument
        {
            Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, QuoteAsset = "USD",
        }, new SimulatedFeed()) { RecordLive = false };
        using var broker = orders.Tradovate(data, session, Assert.Single(session.Accounts), "Quit Tradovate");
        await Until(() => broker.PositionReady && broker.OrdersReady && broker.Position.Volume == 1);

        OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 1);
        await Until(() => Arrivals().Length == 1);
        OrderActions.Flatten(broker);
        try
        {
            Volatile.Write(ref quit, fixture.Services.ShutdownAsync());
        }
        finally { buyReply.TrySetResult(); }
        await quit!.WaitAsync(AppServices.NativeSendDrainLimit + Deadline);
        Assert.Contains(("Sell", false), Arrivals());
    }

    /// <summary>Quit right after Close position on Binance, while the close still reads the venue position: the close is
    /// sent before shutdown completes.</summary>
    [Fact]
    public async Task Quit_sends_a_binance_close_that_is_still_reading_the_position_before_shutdown_completes()
    {
        using var binance = new FakeBinance(futures: true);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
        using var orders = new NativeOrderTestFixture();
        using var broker = orders.Binance(new InstrumentData(instrument, new SimulatedFeed()), binance.Settings, "Quit Binance");
        using var rithmic = new FakeRithmicServer();
        await using var fixture = new AccountsCloseFixture(rithmic);
        Private<Dictionary<string, ITradingAccount>>(fixture.Services, "_realBrokers")["quit-binance"] = broker;
        await Until(() => broker.PositionReady && broker.OrdersReady);
        var opening = OrderActions.Manual(broker, OrderTypes.Market, OrderDirections.Buy, 0.01m);
        await Until(() => broker.Position.Volume == 0.01m && opening.ExchangeOrderId.Length > 0);

        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int hold = 1;
        binance.BeforeGetReply = async (request, _) =>
        {
            if (request.Route == "/fapi/v2/positionRisk" && Volatile.Read(ref hold) == 1) await positionRelease.Task;
        };
        bool sellAtQuit = false;
        Task observed;
        try
        {
            OrderActions.Flatten(broker);
            var quit = fixture.Services.ShutdownAsync();
            observed = quit.ContinueWith(_ => sellAtQuit = binance.Placed.Any(order => order.Side == "SELL"),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        finally
        {
            Volatile.Write(ref hold, 0);
            positionRelease.TrySetResult();
        }
        await observed.WaitAsync(AppServices.NativeSendDrainLimit + Deadline);
        Assert.True(sellAtQuit, "Shutdown completed before the queued Binance close was sent.");
    }

    private static T Private<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
