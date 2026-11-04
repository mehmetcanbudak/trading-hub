using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// BinanceBroker against a fake Binance REST server (USD-M futures and spot routes). The order handle a caller gets
/// back must follow its order at the exchange until it finishes, also across Modify (cancel and replace). USD-M futures
/// stops are conditional orders, which Binance takes only through its Algo Order API; scenarios that concern both kinds
/// run once with a regular take-profit limit and once with a stop.
/// </summary>
public class BinanceBrokerTests
{
    private static readonly Instrument BtcUsdt = new() { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static async Task Until(Func<bool> condition, int ms = 12000,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
        Assert.True(condition(), $"Timed out waiting for {expression}.");
    }

    /// <summary>Waits for at least <paramref name="count"/> polls to be consumed, not merely requested.</summary>
    private static async Task Polls(FakeBinance fake, int count)
    {
        int start = fake.OpenOrderPolls;
        // PollLoop cannot request the next list until reconciliation, positions and Changed have completed.
        // The final request is a consumer barrier for the preceding count polls, including their algo lookups.
        await Until(() => fake.OpenOrderPolls >= start + count + 1);
    }

    private static async Task Observed(BinanceBroker broker, FakeBinance fake, Func<bool> condition, Action? change = null,
        [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed()
        {
            if (condition()) completed.TrySetResult();
        }
        broker.Changed += Changed;
        try
        {
            change?.Invoke();
            Changed(); // the state may already have been published before subscription
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(12));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"Timed out observing {expression}; broker error: {broker.LastError ?? "(none)"}; " +
                $"orders: {string.Join(" | ", broker.ActiveOrders.Select(o => $"{o.ExchangeOrderId}:{o.State}/{o.Status}, filled={o.Filled}"))}; " +
                $"recent requests: {string.Join(" | ", fake.Calls.TakeLast(20))}; " +
                $"client lookup replies: {string.Join(" | ", fake.ClientLookups.TakeLast(20))}; refused: {string.Join(" | ", fake.Refused.TakeLast(10))}");
        }
        finally { broker.Changed -= Changed; }
    }

    private static BinanceBroker Broker(FakeBinance fake) => new(new InstrumentData(BtcUsdt, new SimulatedFeed()), fake.Settings, "Binance test");

    private static long IdOf(Order o) => long.Parse(o.ExchangeOrderId, Inv);

    /// <summary>
    /// Waits until Binance accepted the order (it has an exchange id) or refused it; a refusal fails the test with what
    /// Binance answered.
    /// </summary>
    private static async Task Accepted(FakeBinance fake, Order o)
    {
        await Until(() => o.State != OrderStates.Active || o.ExchangeOrderId.Length > 0);
        Assert.True(o.State == OrderStates.Active && o.ExchangeOrderId.Length > 0, $"not accepted: {o.State}/{o.Status}; refused by Binance: {string.Join(" | ", fake.Refused)}");
    }

    /// <summary>
    /// A reduce-only exit selling <paramref name="qty"/>: a stop loss, which USD-M futures take only as a conditional
    /// (algo) order, or a take-profit limit, a regular order.
    /// </summary>
    private static Order Exit(BinanceBroker broker, bool conditional, decimal qty, decimal price) => conditional
        ? broker.Exit(OrderTypes.Stop, OrderDirections.Sell, qty, price, "Test: Stop loss")
        : broker.Exit(OrderTypes.Limit, OrderDirections.Sell, qty, price, "Test: Take profit");

    /// <summary>The price an order works at: a stop's trigger, otherwise its limit.</summary>
    private static decimal Level(Order o) => o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price;

    private static string CancelRoute(bool conditional) => conditional ? "DELETE /fapi/v1/algoOrder" : "DELETE /fapi/v1/order";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_native_fill_discards_queued_trailing_price_without_new_exposure(bool conditional)
    {
        using var fake = new FakeBinance(true);
        using var broker = Broker(fake);
        var order = Exit(broker, conditional, 2m, 59000m);
        await Accepted(fake, order);
        fake.UnknownPosts = 1;
        fake.HideOwnedOrders = true;
        broker.Modify(order, 59500m);
        await Until(() => fake.Sent.Count == 2 && order.ExchangeOrderId.Length == 0);
        broker.Modify(order, 59700m);
        fake.Fill(fake.Sent[1].ExchangeId, 2m);
        fake.HideOwnedOrders = false;
        await Until(() => order.Status == OrderStatus.Filled);
        await Polls(fake, 3);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(2m, order.Filled);
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(2, fake.Sent.Count);
        Assert.Empty(broker.ActiveOrders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Trailing_modify_coalesces_the_latest_price_until_prior_native_replacement_resolves(bool conditional)
    {
        using var fake = new FakeBinance(true);
        using var broker = Broker(fake);
        var order = Exit(broker, conditional, 2m, 59000m);
        await Accepted(fake, order);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int replies = 0;
        fake.BeforePostReply = ct =>
        {
            if (Interlocked.Increment(ref replies) != 1) return Task.CompletedTask;
            arrived.TrySetResult();
            return release.Task.WaitAsync(ct);
        };
        broker.Modify(order, 59500m);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(12));
        try
        {
            broker.Modify(order, 59600m);
            broker.Modify(order, 59700m);
            Assert.True(broker.IsReplacementPending(order));
            await Polls(fake, 2);
            Assert.Equal(2, fake.Sent.Count);
            if (!conditional) fake.Fill(fake.Sent[1].ExchangeId, 0.5m);
        }
        finally { release.TrySetResult(); }
        await Until(() => fake.Sent.Count == 3 && !broker.IsReplacementPending(order));
        Assert.Equal(3, fake.Sent.Count);
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(59700m, Level(order));
        Assert.Equal(59700m, fake.Sent[2].Level);
        Assert.Equal(conditional ? 2m : 1.5m, fake.Sent[2].Qty);
        Assert.Equal(conditional ? 0m : 0.5m, order.Filled);
        Assert.Equal(2m, order.QuantityToFill);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_trailing_modify_waits_for_exact_unknown_submission_recovery(bool conditional)
    {
        using var fake = new FakeBinance(true);
        using var broker = Broker(fake);
        var order = Exit(broker, conditional, 2m, 59000m);
        await Accepted(fake, order);
        fake.UnknownPosts = 1;
        fake.HideOwnedOrders = true;
        broker.Modify(order, 59500m);
        await Until(() => fake.Sent.Count == 2 && order.ExchangeOrderId.Length == 0);
        broker.Modify(order, 59700m);
        await Polls(fake, 4);
        Assert.Equal(2, fake.Sent.Count);
        Assert.True(broker.IsReplacementPending(order));
        Assert.Empty(order.ExchangeOrderId);
        fake.HideOwnedOrders = false;
        await Until(() => fake.Sent.Count == 3 && !broker.IsReplacementPending(order));
        Assert.Equal(3, fake.Sent.Count);
        Assert.Equal(59700m, Level(order));
        Assert.False(broker.IsReplacementPending(order));
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_discards_queued_trailing_price_without_placing_another_replacement(bool conditional)
    {
        using var fake = new FakeBinance(true);
        using var broker = Broker(fake);
        var order = Exit(broker, conditional, 2m, 59000m);
        await Accepted(fake, order);
        fake.UnknownPosts = 1;
        fake.HideOwnedOrders = true;
        broker.Modify(order, 59500m);
        await Until(() => fake.Sent.Count == 2 && order.ExchangeOrderId.Length == 0);
        broker.Modify(order, 59700m);
        broker.Cancel(order);
        fake.HideOwnedOrders = false;
        await Until(() => order.Canceled);
        await Polls(fake, 3);
        Assert.True(order.Canceled);
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(2, fake.Sent.Count);
        Assert.Empty(fake.Open);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definite_cancel_rejection_restores_native_values_before_replacement_resolves(bool conditional)
    {
        using var fake = new FakeBinance(true);
        using var broker = Broker(fake);
        var order = Exit(broker, conditional, 2m, 59000m);
        await Accepted(fake, order);
        string id = order.ExchangeOrderId;
        fake.RejectCancels = 1;
        broker.Replace(order, 59500m, 1m);
        Assert.True(broker.IsReplacementPending(order));
        await Until(() => !broker.IsReplacementPending(order));
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(id, order.ExchangeOrderId);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Equal(59000m, Level(order));
        Assert.Equal(2m, order.QuantityToFill);
        Assert.Equal(2m, order.Unfilled);
        Assert.Single(fake.Sent);
    }

    [Fact]
    public async Task Spot_stop_replacement_ACK_and_optimistic_values_do_not_confirm_working_capacity()
    {
        using var fake = new FakeBinance(false);
        using var broker = Broker(fake);
        var order = broker.Stop(OrderDirections.Sell, 2m, 59000m);
        await Accepted(fake, order);
        string first = order.ExchangeOrderId;
        fake.Fill(first, 0.5m);
        await Until(() => order.Filled == 0.5m);
        fake.HideOwnedOrders = true;
        bool observedResolution = false;
        broker.Changed += () =>
        {
            if (order.ExchangeOrderId != first && !broker.IsReplacementPending(order)) observedResolution = true;
        };
        broker.Replace(order, 59500m, 1m);
        Assert.True(broker.IsReplacementPending(order));
        Assert.Equal(59500m, order.TriggerPrice);
        Assert.Equal(1m, order.Unfilled);
        await Until(() => fake.Sent.Count == 2 && order.ExchangeOrderId != first);
        await Polls(fake, 4); // a bare ACK and repeated not-found snapshots cannot release replacement capacity
        Assert.Equal(OrderStates.Active, order.State);
        Assert.True(broker.IsReplacementPending(order));
        Assert.False(observedResolution);
        broker.Replace(order, 59600m, 1m);
        await Polls(fake, 2);
        Assert.Equal(2, fake.Sent.Count);
        fake.Fill(order.ExchangeOrderId, 0.25m);
        fake.HideOwnedOrders = false;
        await Until(() => !broker.IsReplacementPending(order) && observedResolution);
        Assert.False(broker.IsReplacementPending(order));
        Assert.True(observedResolution);
        Assert.Equal(1.5m, order.QuantityToFill); // total, not remaining, is the acknowledgment invariant
        Assert.Equal(0.75m, order.Filled);
        Assert.Equal(0.75m, order.Unfilled);
        Assert.Equal(59500m, order.TriggerPrice);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
    }

    private sealed class SubmissionStrategy : Strategy
    {
        public void Enter() => BuyLimit(59000m, 2m);
        public void Exit() => SellLimit(59010m, 2m);
    }

    [Fact]
    public async Task Unknown_submission_replay_books_each_owned_fill_and_realized_profit_once()
    {
        using var fake = new FakeBinance(true) { UnknownPosts = 1, HideOwnedOrders = true };
        var data = new InstrumentData(BtcUsdt, new SimulatedFeed());
        using var broker = new BinanceBroker(data, fake.Settings, "Binance accounting test");
        data.Trades.Add(new Trade(1, DateTime.UtcNow, 59000m, 1m, TradeSide.Buy));
        var strategy = new SubmissionStrategy();
        using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1) { Persist = false };
        await Until(() => broker.OrdersReady && broker.PositionReady);
        runner.Start();
        strategy.Enter();
        await Until(() => fake.Sent.Count == 1 && broker.LastError != null);
        var entry = Assert.Single(broker.ActiveOrders);
        string entryId = Assert.Single(fake.Sent).ExchangeId;
        fake.Fill(entryId, 1m);
        fake.HideOwnedOrders = false;
        await Until(() => runner.Ledger.Position == 1m);
        Assert.Equal(1m, runner.Ledger.Position);
        Assert.Same(entry, Assert.Single(broker.ActiveOrders));
        await Polls(fake, 3);
        Assert.Single(runner.Ledger.Fills);
        fake.Fill(entryId, 1m);
        await Until(() => runner.Ledger.Position == 2m);
        Assert.Equal(2m, runner.Ledger.Position);
        Assert.Equal(2, runner.Ledger.Fills.Count);
        await Until(() => broker.OrdersReady);
        fake.UnknownPosts = 1;
        fake.HideOwnedOrders = true;
        data.Trades.Add(new Trade(2, DateTime.UtcNow.AddSeconds(1), 59010m, 1m, TradeSide.Buy));
        // Match the established runner fixture: appending storage alone does not deliver the market-data event.
        var appended = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        ((Action<int, int>?)appended!.GetValue(data))?.Invoke(data.Trades.Count - 1, 1);
        Assert.Equal(59010m, runner.LastPrice);
        strategy.Exit();
        await Until(() => fake.Sent.Count == 2 && broker.LastError != null);
        string exitId = fake.Sent[1].ExchangeId;
        fake.Fill(exitId, 2m);
        fake.HideOwnedOrders = false;
        await Until(() => runner.Ledger.Position == 0m && runner.Ledger.Realized == 20m);
        await Polls(fake, 4);
        Assert.Equal(0m, runner.Ledger.Position);
        Assert.Equal(20m, runner.Ledger.Realized);
        Assert.Equal(3, runner.Ledger.Fills.Count);
        Assert.Equal(2, fake.Sent.Count);
    }

    [Theory]
    [InlineData(false, false, 503, -1007)]
    [InlineData(true, false, 503, -1007)]
    [InlineData(true, true, 503, -1007)]
    [InlineData(false, false, 400, -1007)]
    [InlineData(true, false, 400, -1006)]
    [InlineData(true, true, 503, 0)]
    public async Task Unknown_submission_replays_exact_identity_without_adopting_a_manual_lookalike(bool futures, bool conditional, int httpStatus, int errorCode)
    {
        using var fake = new FakeBinance(futures) { UnknownPosts = 1, UnknownPostStatus = httpStatus, UnknownPostCode = errorCode, HideOwnedOrders = true };
        using var broker = Broker(fake);
        var order = Exit(broker, conditional, 2m, 59000m);
        await Observed(broker, fake, () => fake.Sent.Count == 1 && broker.LastError?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Equal("", order.ExchangeOrderId);
        Assert.Contains(order, broker.ActiveOrders);
        Assert.False(broker.OrdersReady);
        string manual = conditional
            ? "algo:" + fake.AddExternalAlgo("SELL", "STOP_MARKET", 2m, 59000m).ToString(Inv)
            : fake.AddExternal("SELL", "LIMIT", 2m, 59000m).ToString(Inv);
        await Observed(broker, fake, () => broker.ActiveOrders.Any(o => o.ExchangeOrderId == manual));
        Assert.NotSame(order, broker.ActiveOrders.Single(o => o.ExchangeOrderId == manual));
        broker.Replace(order, 59500m, 2m);
        await Polls(fake, 4);
        Assert.Single(fake.Sent);
        Assert.Equal("", order.ExchangeOrderId); // repeated not-found queries are not rejection proof
        string clientId = conditional ? Assert.Single(fake.PlacedAlgo).ClientId : Assert.Single(fake.Placed).ClientId;
        var hiddenLookups = fake.ClientLookups;
        Assert.True(hiddenLookups.Count >= 4, $"Only {hiddenLookups.Count} exact-client lookups reached the fake provider.");
        Assert.All(hiddenLookups, lookup => Assert.Equal((clientId, 400), lookup));

        string accepted = Assert.Single(fake.Sent).ExchangeId;
        await Observed(broker, fake, () => order.Filled == 0.5m && order.ExchangeOrderId.Length > 0, () =>
        {
            fake.Fill(accepted, 0.5m); // an algo can trigger and partially fill before its response is recovered
            fake.HideOwnedOrders = false;
        });
        Assert.Equal(0.5m, order.Filled);
        await Polls(fake, 3);
        Assert.Equal(0.5m, order.Filled);
        Assert.Same(order, broker.ActiveOrders.Single(o => o.ExchangeOrderId == order.ExchangeOrderId));
        await Observed(broker, fake, () => order.Status == OrderStatus.Filled, () => fake.Fill(order.ExchangeOrderId, 1.5m));
        await Polls(fake, 3);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(2m, order.Filled);
        Assert.Single(fake.Sent);
        Assert.Contains(broker.ActiveOrders, o => o.ExchangeOrderId == manual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_submission_response_retains_cancel_until_delayed_client_binding(bool conditional)
    {
        using var fake = new FakeBinance(true) { HideOwnedOrders = true };
        var settings = fake.Settings;
        settings.RequestTimeout = TimeSpan.FromMilliseconds(200);
        fake.BeforePostReply = ct => Task.Delay(Timeout.Infinite, ct);
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance test");
        var order = Exit(broker, conditional, 2m, 59000m);
        await Observed(broker, fake,
            () => fake.Sent.Count == 1 && broker.LastError?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true,
            () => broker.Cancel(order));
        await Polls(fake, 12); // beyond the old bounded binding wait
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Equal("", order.ExchangeOrderId);
        Assert.Single(fake.Sent);
        string clientId = conditional ? Assert.Single(fake.PlacedAlgo).ClientId : Assert.Single(fake.Placed).ClientId;
        var hiddenLookups = fake.ClientLookups;
        Assert.True(hiddenLookups.Count >= 12, $"Only {hiddenLookups.Count} exact-client lookups reached the fake provider.");
        Assert.All(hiddenLookups, lookup => Assert.Equal((clientId, 400), lookup));
        Assert.DoesNotContain(CancelRoute(conditional), fake.Calls);
        await Observed(broker, fake, () => order.Status == OrderStatus.Canceled, () => fake.HideOwnedOrders = false);
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Single(fake.Sent);
        Assert.Contains(CancelRoute(conditional), fake.Calls);
        Assert.Empty(fake.Open);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_replacement_is_not_replaced_again_and_retains_the_original_fill_total(bool conditional)
    {
        using var fake = new FakeBinance(true);
        using var broker = Broker(fake);
        var order = Exit(broker, conditional, 2m, 59000m);
        await Accepted(fake, order);
        string first = order.ExchangeOrderId;
        if (!conditional)
        {
            fake.Fill(first, 0.5m);
            await Until(() => order.Filled == 0.5m);
        }
        fake.HideOwnedOrders = true;
        fake.UnknownPosts = 1;
        broker.Replace(order, 59500m, order.Unfilled);
        Assert.True(broker.IsReplacementPending(order));
        await Until(() => fake.Sent.Count == 2 && order.ExchangeOrderId.Length == 0);
        Assert.Equal(OrderStates.Active, order.State);
        broker.Replace(order, 59600m, order.Unfilled);
        await Polls(fake, 4);
        Assert.Equal(2, fake.Sent.Count);
        Assert.True(broker.IsReplacementPending(order));
        broker.Cancel(order);
        fake.HideOwnedOrders = false;
        await Until(() => order.Status == OrderStatus.Canceled);
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.False(broker.IsReplacementPending(order));
        Assert.Equal(conditional ? 0m : 0.5m, order.Filled);
        Assert.Equal(2, fake.Sent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definite_submission_rejection_is_terminal(bool conditional)
    {
        using var fake = new FakeBinance(true) { RejectPosts = 1 };
        using var broker = Broker(fake);
        var order = Exit(broker, conditional, 1m, 59000m);
        await Until(() => order.Status == OrderStatus.Rejected);
        Assert.Equal(OrderStates.Failed, order.State);
        Assert.Empty(fake.Sent);
        Assert.DoesNotContain(order, broker.ActiveOrders);
    }

    [Fact]
    public async Task A_market_exit_is_sent_as_a_broker_enforced_reduce_only_market_order()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var close = broker.Exit(OrderTypes.Market, OrderDirections.Sell, 1m, 0, "Test: close");
        await Until(() => close.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, close.Status);
        Assert.Equal(OrderTypes.Market, close.Type);
        var placed = Assert.Single(fake.Placed);
        Assert.Equal("MARKET", placed.Type);
        Assert.True(placed.ReduceOnly);
        Assert.Equal(1m, placed.Qty);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task Futures_market_order_handle_reaches_Filled_after_it_leaves_the_open_list()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Market(OrderDirections.Buy, 0.01m, "Test: entry");
        await Until(() => order.Status == OrderStatus.Filled);

        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(OrderStates.Done, order.State);
        Assert.Equal(0m, order.Unfilled);
        Assert.Equal(0.01m, order.Filled);
        var placed = Assert.Single(fake.Placed);
        Assert.Equal(placed.Id.ToString(Inv), order.ExchangeOrderId);
        Assert.Contains("GET /fapi/v1/order", fake.Calls); // the final state is looked up with a signed request
        Assert.DoesNotContain(order, broker.ActiveOrders);
    }

    [Fact]
    public async Task Spot_orders_reach_their_final_state_through_the_api_v3_routes()
    {
        using var fake = new FakeBinance(futures: false);
        using var broker = Broker(fake);
        var market = broker.Market(OrderDirections.Buy, 0.5m);
        var limit = broker.Limit(OrderDirections.Sell, 0.5m, 70000m);
        await Until(() => market.Status == OrderStatus.Filled && limit.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(limit));

        Assert.Equal(OrderStatus.Filled, market.Status); // the spot answer for a market order already says FILLED
        Assert.Equal(0m, market.Unfilled);
        Assert.Contains(limit, broker.ActiveOrders);

        fake.Fill(IdOf(limit), 0.5m);
        await Until(() => limit.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, limit.Status);
        Assert.Equal(OrderStates.Done, limit.State);
        Assert.Equal(0.5m, limit.Filled);
        Assert.DoesNotContain(limit, broker.ActiveOrders);
        Assert.Contains("GET /api/v3/order", fake.Calls);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("/fapi/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_fill_then_full_fill_update_the_same_handle()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Limit(OrderDirections.Buy, 3m, 60000m, "Test: entry");
        await Until(() => order.ExchangeOrderId.Length > 0);
        long id = IdOf(order);

        fake.Fill(id, 1m);
        await Until(() => order.Unfilled == 2m);
        Assert.Equal(2m, order.Unfilled);
        Assert.Equal(1m, order.Filled);
        Assert.Equal(OrderStatus.PartlyFilled, order.Status);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));

        fake.Fill(id, 2m);
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(3m, order.Filled);
        Assert.Equal(0m, order.Unfilled);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task Cancel_finishes_the_handle_as_Canceled_and_keeps_its_partial_fill()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Limit(OrderDirections.Sell, 2m, 61000m);
        await Until(() => order.ExchangeOrderId.Length > 0);
        long id = IdOf(order);
        fake.Fill(id, 0.5m); // filled a little right before the cancel

        broker.Cancel(order);
        await Until(() => order.Status == OrderStatus.Canceled);
        Assert.Equal(OrderStatus.Canceled, order.Status);
        Assert.Equal(OrderStates.Done, order.State);
        Assert.Equal(0.5m, order.Filled);
        Assert.Equal("CANCELED", fake.Order(id).Status);
        Assert.DoesNotContain(order, broker.ActiveOrders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_sent_before_Binance_answers_the_submission_still_cancels_the_order(bool conditional)
    {
        using var fake = new FakeBinance(futures: true) { PostDelayMs = 700 };
        using var broker = Broker(fake);
        var order = conditional ? broker.Stop(OrderDirections.Sell, 1m, 55000m, "Test: stop") : broker.Limit(OrderDirections.Sell, 1m, 65000m, "Test: limit");
        broker.Cancel(order); // no exchange order id yet
        await Until(() => order.State != OrderStates.Active);

        Assert.True(order.Status == OrderStatus.Canceled, $"{order.State}/{order.Status}; refused by Binance: {string.Join(" | ", fake.Refused)}");
        Assert.Contains(CancelRoute(conditional), fake.Calls);
        Assert.Empty(fake.Open);
        Assert.Empty(broker.ActiveOrders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modify_keeps_one_handle_that_follows_the_replacement_order(bool conditional)
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var exit = Exit(broker, conditional, 2m, 59000m);
        await Accepted(fake, exit);
        await Until(() => broker.ActiveOrders.Contains(exit));
        string first = exit.ExchangeOrderId;

        broker.Modify(exit, 59500m);
        await Until(() => exit.ExchangeOrderId != first && exit.ExchangeOrderId.Length > 0);
        Assert.NotEqual(first, exit.ExchangeOrderId);
        Assert.Equal(59500m, Level(exit));
        Assert.Equal(OrderStates.Active, exit.State);
        Assert.Equal("CANCELED", fake.View(first).Status);
        Assert.Contains(CancelRoute(conditional), fake.Calls);

        // Replace returns the same handle as well
        string second = exit.ExchangeOrderId;
        Assert.Same(exit, broker.Replace(exit, 59600m, exit.Unfilled));
        await Until(() => exit.ExchangeOrderId != second);
        var open = Assert.Single(fake.Open);
        Assert.Equal(open.ExchangeId, exit.ExchangeOrderId);
        Assert.Equal(59600m, open.Level);
        Assert.Equal(2m, open.Qty);
        Assert.True(open.ReduceOnly);

        await Polls(fake, 2);
        Assert.Same(exit, Assert.Single(broker.ActiveOrders)); // no orphan replacement object
        Assert.Equal(59600m, Level(exit));

        fake.Fill(open.ExchangeId, 2m); // a stop triggers and the market order it places fills
        await Until(() => exit.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, exit.Status);
        Assert.Equal(2m, exit.Filled);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task Modify_after_a_partial_fill_replaces_only_the_rest()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Limit(OrderDirections.Buy, 3m, 60000m);
        await Until(() => order.ExchangeOrderId.Length > 0);
        string first = order.ExchangeOrderId;
        fake.Fill(IdOf(order), 1m);
        await Until(() => order.Unfilled == 2m);

        broker.Modify(order, 60100m);
        await Until(() => order.ExchangeOrderId != first);
        var open = Assert.Single(fake.OpenOrders);
        Assert.Equal(2m, open.Qty);
        Assert.Equal(60100m, open.Price);
        Assert.Equal(open.Id.ToString(Inv), order.ExchangeOrderId);
        Assert.Equal(3m, order.QuantityToFill);
        Assert.Equal(1m, order.Filled);
        Assert.Equal(OrderStatus.PartlyFilled, order.Status);

        fake.Fill(open.Id, 2m);
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(3m, order.Filled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modify_of_an_order_that_already_filled_does_not_send_a_new_order(bool conditional)
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var exit = Exit(broker, conditional, 1m, 59000m);
        await Accepted(fake, exit);
        fake.Fill(exit.ExchangeOrderId, 1m); // filled (a stop: triggered and filled) before the broker's next poll

        broker.Modify(exit, 59200m); // the cancel fails at Binance: nothing is replaced
        await Until(() => exit.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, exit.Status);
        Assert.Equal(1m, exit.Filled);
        await Polls(fake, 1);
        Assert.Single(fake.Sent);
        Assert.Empty(fake.Open);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modify_refused_because_the_order_just_filled_sends_no_replacement(bool conditional)
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var exit = Exit(broker, conditional, 1m, 59000m);
        await Accepted(fake, exit);
        fake.FillWhenCanceled(exit.ExchangeOrderId); // it fills (a stop: triggers and fills) just before the cancel arrives

        broker.Modify(exit, 59200m);
        await Until(() => exit.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, exit.Status);
        Assert.Equal(1m, exit.Filled);
        Assert.Contains(CancelRoute(conditional), fake.Calls); // the cancel was tried and refused
        await Polls(fake, 1);
        Assert.Single(fake.Sent);
        Assert.Empty(fake.Open);
    }

    [Fact]
    public async Task External_open_orders_are_listed_with_stable_handles_until_they_finish()
    {
        using var fake = new FakeBinance(futures: true);
        long manual = fake.AddExternal("BUY", "LIMIT", 1m, 50000m);
        long old = fake.AddExternal("SELL", "STOP_MARKET", 0.2m, 0m, stopPrice: 52000m);
        using var broker = Broker(fake);
        await Until(() => broker.ActiveOrders.Count() == 2);

        var ext = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == manual.ToString(Inv));
        Assert.Equal(OrderDirections.Buy, ext.Direction);
        Assert.Equal(OrderTypes.Limit, ext.Type);
        Assert.Equal(50000m, ext.Price);
        Assert.Equal(1m, ext.Unfilled);
        Assert.Equal(OrderStatus.Placed, ext.Status);
        Assert.Equal("", ext.Comment);
        var stop = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == old.ToString(Inv));
        Assert.Equal(OrderTypes.Stop, stop.Type);
        Assert.Equal(52000m, stop.TriggerPrice);

        // an order sent through the broker is listed next to them
        var own = broker.Limit(OrderDirections.Sell, 1m, 70000m, "Test: target");
        await Until(() => own.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(own));
        await Polls(fake, 2);
        Assert.Equal(3, broker.ActiveOrders.Count());
        Assert.Same(ext, broker.ActiveOrders.Single(o => o.ExchangeOrderId == manual.ToString(Inv)));

        // canceled outside Trading Hub; the older one is no longer known to Binance at all
        fake.CancelAtExchange(manual);
        fake.CancelAtExchange(old, forget: true);
        await Until(() => ext.Status == OrderStatus.Canceled && stop.Status == OrderStatus.Canceled);
        Assert.Equal(OrderStatus.Canceled, ext.Status);
        Assert.Equal(OrderStatus.Canceled, stop.Status);
        Assert.Equal(0m, stop.Filled);
        Assert.Same(own, Assert.Single(broker.ActiveOrders));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_replacement_that_Binance_refuses_finishes_the_handle_as_Rejected(bool conditional)
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var exit = Exit(broker, conditional, 1m, 59000m);
        await Accepted(fake, exit);
        string first = exit.ExchangeOrderId;

        fake.RejectPosts = 1; // the new order is refused (a stop would trigger at once)
        broker.Modify(exit, 61000m);
        await Until(() => exit.State == OrderStates.Failed);
        Assert.Equal(OrderStates.Failed, exit.State);
        Assert.False(broker.IsReplacementPending(exit));
        Assert.Equal(OrderStatus.Rejected, exit.Status);
        Assert.Equal(first, exit.ExchangeOrderId);
        Assert.Equal("CANCELED", fake.View(first).Status); // the old order is gone at the exchange
        Assert.Empty(fake.Open);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task A_request_timeout_is_reported_and_polling_goes_on()
    {
        using var fake = new FakeBinance(futures: true) { StallOpenOrdersOnceMs = 5000 };
        long manual = fake.AddExternal("BUY", "LIMIT", 1m, 50000m);
        var settings = fake.Settings;
        settings.RequestTimeout = TimeSpan.FromSeconds(2); // the first open-order request times out
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance test");
        var errors = new ConcurrentQueue<string>();
        broker.Changed += () =>
        {
            if (broker.LastError is { } e) errors.Enqueue(e);
        };

        await Until(() => broker.ActiveOrders.Any(o => o.ExchangeOrderId == manual.ToString(Inv)));
        Assert.Contains(broker.ActiveOrders, o => o.ExchangeOrderId == manual.ToString(Inv)); // a later poll worked
        Assert.Contains(errors, e => e.Contains("Timeout", StringComparison.OrdinalIgnoreCase)); // the timeout was reported
        Assert.True(fake.Calls.Count(c => c == "GET /fapi/v1/openOrders") >= 2); // the stalled request was not the last
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replace_shows_the_new_price_and_quantity_at_once_and_polls_keep_them(bool conditional)
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var exit = Exit(broker, conditional, 2m, 61000m);
        await Accepted(fake, exit);
        string first = exit.ExchangeOrderId;

        var postArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.BeforePostReply = ct =>
        {
            postArrived.TrySetResult();
            return releasePost.Task.WaitAsync(ct);
        };
        Assert.Same(exit, broker.Replace(exit, 60500m, 1m));
        Assert.Equal(60500m, Level(exit)); // like the other brokers, the handle shows the request at once
        Assert.Equal(1m, exit.Unfilled);
        Assert.Equal(1m, exit.QuantityToFill);
        Assert.True(broker.IsReplacementPending(exit));

        await postArrived.Task.WaitAsync(TimeSpan.FromSeconds(12));
        int polls = fake.OpenOrderPolls;
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (fake.OpenOrderPolls > polls && broker.OrdersReady) polled.TrySetResult();
        }
        broker.Changed += OnChanged;
        try
        {
            await polled.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.Equal(first, exit.ExchangeOrderId); // the fixture still holds Binance's reply
            Assert.Equal(60500m, Level(exit)); // a completed poll did not put the old values back
            Assert.Equal(1m, exit.Unfilled);
            Assert.Same(exit, Assert.Single(broker.ActiveOrders));
            Assert.True(broker.IsReplacementPending(exit));
        }
        finally
        {
            broker.Changed -= OnChanged;
            releasePost.TrySetResult();
        }

        await Until(() => exit.ExchangeOrderId != first);
        var open = Assert.Single(fake.Open);
        Assert.Equal(open.ExchangeId, exit.ExchangeOrderId);
        Assert.Equal(1m, open.Qty);
        Assert.Equal(60500m, open.Level);
        await Polls(fake, 2);
        Assert.False(broker.IsReplacementPending(exit));
        Assert.Equal(60500m, Level(exit));
        Assert.Equal(1m, exit.Unfilled);
        Assert.Same(exit, Assert.Single(broker.ActiveOrders));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancel_that_times_out_but_went_through_still_gets_its_replacement(bool conditional)
    {
        using var fake = new FakeBinance(futures: true);
        var settings = fake.Settings;
        settings.RequestTimeout = TimeSpan.FromSeconds(2);
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance test");
        var exit = Exit(broker, conditional, 2m, 59000m);
        await Accepted(fake, exit);
        string first = exit.ExchangeOrderId;

        fake.StallCancelOnceMs = 5000; // Binance cancels it, but the answer never arrives in time
        broker.Modify(exit, 59500m);
        await Until(() => exit.ExchangeOrderId != first);
        Assert.NotEqual(first, exit.ExchangeOrderId);
        Assert.Equal("CANCELED", fake.View(first).Status);
        var open = Assert.Single(fake.Open);
        Assert.Equal(open.ExchangeId, exit.ExchangeOrderId);
        Assert.Equal(59500m, open.Level);
        Assert.Equal(2m, open.Qty);
        Assert.Equal(OrderStates.Active, exit.State);
        Assert.Equal(59500m, Level(exit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Modify_whose_cancel_cannot_be_confirmed_reports_the_lost_order_as_rejected(bool conditional)
    {
        using var fake = new FakeBinance(futures: true);
        var settings = fake.Settings;
        settings.RequestTimeout = TimeSpan.FromSeconds(2);
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance test");
        var errors = new ConcurrentQueue<string>();
        broker.Changed += () =>
        {
            if (broker.LastError is { } e) errors.Enqueue(e);
        };
        var exit = Exit(broker, conditional, 1m, 59000m);
        await Accepted(fake, exit);

        await Polls(fake, 1); // no lookup from an earlier poll is still on its way
        fake.StallCancelOnceMs = 5000; // canceled at Binance, answer too late…
        fake.FailLookups = 1; // …and asking right away fails too
        broker.Modify(exit, 59500m);
        await Until(() => exit.State == OrderStates.Failed);
        Assert.Equal(OrderStates.Failed, exit.State); // not a silent Canceled: the order is gone
        Assert.Equal(OrderStatus.Rejected, exit.Status);
        Assert.Contains(errors, e => e.Contains("replacement was not sent", StringComparison.Ordinal));
        Assert.Single(fake.Sent);
        Assert.Empty(fake.Open);
        Assert.Empty(broker.ActiveOrders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unanswered_cancel_that_Binance_carries_out_later_is_reported_as_a_failed_Modify(bool conditional)
    {
        using var fake = new FakeBinance(futures: true);
        var settings = fake.Settings;
        settings.RequestTimeout = TimeSpan.FromSeconds(2);
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance test");
        var exit = Exit(broker, conditional, 1m, 59000m);
        await Accepted(fake, exit);
        string id = exit.ExchangeOrderId;

        fake.StallCancelOnceMs = 5000;
        fake.StalledCancelIsCarriedOut = false; // not done yet when the client gives up
        broker.Modify(exit, 59500m);
        Assert.Equal(59500m, Level(exit)); // the request shows at once
        await Until(() => Level(exit) == 59000m); // asked right away: still working, so not replaced
        await Polls(fake, 1);
        Assert.True(broker.IsReplacementPending(exit)); // a working snapshot does not prove the uncertain cancel cannot arrive late
        Assert.Equal(OrderStates.Active, exit.State);
        Assert.Equal(59000m, Level(exit)); // not replaced: shown as it is at Binance again
        Assert.Single(fake.Sent);

        fake.CancelAtExchange(id); // Binance carries out the cancel late
        await Until(() => exit.State == OrderStates.Failed);
        Assert.Equal(OrderStatus.Rejected, exit.Status); // not a silent Canceled
        Assert.False(broker.IsReplacementPending(exit));
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task Deferred_market_cancel_queries_actual_execution_after_a_held_NEW_ack_before_sending_DELETE()
    {
        using var fake = new FakeBinance(true);
        var settings = fake.Settings;
        settings.PollInterval = TimeSpan.FromHours(1); // Only the queued cancel can perform the final-state lookup.
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance market cancel");
        await Until(() => broker.OrdersReady && broker.PositionReady);
        Assert.True(broker.OrdersReady && broker.PositionReady);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.BeforePostReply = ct =>
        {
            arrived.TrySetResult();
            return release.Task.WaitAsync(ct);
        };
        var order = broker.Market(OrderDirections.Sell, 0.02m);
        broker.Cancel(order);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(12));
        try
        {
            Assert.Empty(order.ExchangeOrderId);
            Assert.Equal(OrderStates.Active, order.State);
            Assert.Equal("FILLED", Assert.Single(fake.Placed).Status); // Engine execution precedes the held NEW ACK.
            Assert.DoesNotContain("DELETE /fapi/v1/order", fake.Calls);
        }
        finally { release.TrySetResult(); }
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(0.02m, order.Filled);
        Assert.Equal(0m, order.Unfilled);
        Assert.Contains("GET /fapi/v1/order", fake.Calls);
        Assert.DoesNotContain("DELETE /fapi/v1/order", fake.Calls);
        broker.Cancel(order); // A known terminal handle must not fall through to detached-ID cancellation.
        Assert.DoesNotContain("DELETE /fapi/v1/order", fake.Calls);
    }

    [Fact]
    public async Task Unknown_market_submission_that_replays_working_still_honors_its_deferred_cancel_and_partial_fill()
    {
        using var fake = new FakeBinance(true) { HoldMarketExecution = true, UnknownPosts = 1, HideOwnedOrders = true };
        using var broker = Broker(fake);
        var order = broker.Market(OrderDirections.Sell, 0.02m);
        broker.Cancel(order);
        await Until(() => fake.Placed.Count == 1 && broker.LastError?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Equal(OrderStates.Active, order.State);
        Assert.Empty(order.ExchangeOrderId);
        Assert.DoesNotContain("DELETE /fapi/v1/order", fake.Calls);
        fake.Fill(Assert.Single(fake.Placed).Id, 0.005m);
        fake.HideOwnedOrders = false;
        await Until(() => order.Canceled);
        Assert.True(order.Canceled);
        Assert.Equal(0.005m, order.Filled);
        Assert.Equal(0.015m, order.Unfilled);
        Assert.Contains("DELETE /fapi/v1/order", fake.Calls);
        Assert.Single(fake.Placed);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task A_failed_market_execution_lookup_does_not_suppress_cancellation_of_working_exposure()
    {
        using var fake = new FakeBinance(true) { HoldMarketExecution = true };
        var settings = fake.Settings;
        settings.PollInterval = TimeSpan.FromHours(1);
        using var broker = new BinanceBroker(new InstrumentData(BtcUsdt, new SimulatedFeed()), settings, "Binance uncertain market cancel");
        await Until(() => broker.OrdersReady && broker.PositionReady);
        Assert.True(broker.OrdersReady && broker.PositionReady);
        var order = broker.Market(OrderDirections.Sell, 0.02m);
        await Accepted(fake, order);
        fake.FailLookups = 1;
        broker.Cancel(order);
        await Until(() => order.Canceled);
        Assert.True(order.Canceled);
        Assert.Equal(0m, order.Filled);
        Assert.Contains("GET /fapi/v1/order", fake.Calls);
        Assert.Contains("DELETE /fapi/v1/order", fake.Calls);
        Assert.Single(fake.Placed);
    }

    [Fact]
    public async Task Cancel_of_a_market_order_sends_nothing_and_its_fill_is_reported()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var order = broker.Market(OrderDirections.Sell, 0.02m, "Test: exit");
        broker.Cancel(order); // it cannot be canceled: it fills at once
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(0.02m, order.Filled);
        Assert.DoesNotContain("DELETE /fapi/v1/order", fake.Calls);
    }

    [Fact]
    public async Task Futures_stops_are_conditional_orders_sent_through_the_algo_order_api()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var stop = broker.Stop(OrderDirections.Sell, 1m, 55000m, "Test: stop");
        var stopLoss = broker.Exit(OrderTypes.Stop, OrderDirections.Buy, 2m, 64000m, "Test: Stop loss");
        await Until(() => (stop.State != OrderStates.Active || stop.ExchangeOrderId.Length > 0) && (stopLoss.State != OrderStates.Active || stopLoss.ExchangeOrderId.Length > 0));

        // since 2025-12-09 Binance refuses STOP_MARKET on POST /fapi/v1/order (error -4120)
        Assert.True(stop.State == OrderStates.Active && stopLoss.State == OrderStates.Active,
            $"stop {stop.State}/{stop.Status}, stop loss {stopLoss.State}/{stopLoss.Status}; refused by Binance: {string.Join(" | ", fake.Refused)}");
        Assert.DoesNotContain("POST /fapi/v1/order", fake.Calls);
        var sent = fake.PlacedAlgo;
        Assert.Equal(2, sent.Count);
        var s = Assert.Single(sent, a => a.Side == "SELL");
        Assert.Equal(("STOP_MARKET", 1m, 55000m, false), (s.Type, s.Qty, s.TriggerPrice, s.ReduceOnly));
        var l = Assert.Single(sent, a => a.Side == "BUY");
        Assert.Equal(("STOP_MARKET", 2m, 64000m, true), (l.Type, l.Qty, l.TriggerPrice, l.ReduceOnly)); // a stop loss only reduces
        Assert.Equal("algo:" + s.AlgoId.ToString(Inv), stop.ExchangeOrderId); // algo ids are numbered apart from order ids
        Assert.Equal("algo:" + l.AlgoId.ToString(Inv), stopLoss.ExchangeOrderId);
        Assert.Equal((OrderStatus.Placed, 55000m, 1m), (stop.Status, stop.TriggerPrice, stop.Unfilled));

        await Polls(fake, 2); // the open algo orders list them: still these two handles, working
        Assert.Contains("GET /fapi/v1/openAlgoOrders", fake.Calls);
        Assert.Equal(2, broker.ActiveOrders.Count());
        Assert.Contains(stop, broker.ActiveOrders);
        Assert.Contains(stopLoss, broker.ActiveOrders);
        Assert.Equal(OrderStates.Active, stop.State);
        Assert.Equal(OrderStates.Active, stopLoss.State);
    }

    [Fact]
    public async Task A_futures_stop_is_canceled_through_the_algo_order_api()
    {
        using var fake = new FakeBinance(futures: true);
        long other = fake.AddExternal("SELL", "LIMIT", 1m, 70000m); // a regular order, numbered like the stop will be
        using var broker = Broker(fake);
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1m, 59000m, "Test: Stop loss");
        await Accepted(fake, stop);
        Assert.Equal("algo:" + other.ToString(Inv), stop.ExchangeOrderId); // the same number names two orders

        broker.Cancel(stop);
        await Until(() => stop.Status == OrderStatus.Canceled);
        Assert.Equal(OrderStatus.Canceled, stop.Status);
        Assert.Equal(OrderStates.Done, stop.State);
        Assert.Equal("CANCELED", fake.View(stop.ExchangeOrderId).Status);
        Assert.Contains("DELETE /fapi/v1/algoOrder", fake.Calls);
        Assert.DoesNotContain("DELETE /fapi/v1/order", fake.Calls);
        Assert.Equal("NEW", fake.Order(other).Status); // the regular order with that number still works
        await Polls(fake, 2);
        Assert.Equal(other.ToString(Inv), Assert.Single(broker.ActiveOrders).ExchangeOrderId);
    }

    [Fact]
    public async Task A_triggered_futures_stop_reaches_Filled_through_the_order_it_placed()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 2m, 59000m, "Test: Stop loss");
        await Accepted(fake, stop);
        long algoId = Assert.Single(fake.PlacedAlgo).AlgoId;

        long placed = fake.Trigger(algoId, fill: 2m); // the price reached the trigger: Binance sent a market order, which filled
        await Until(() => stop.State != OrderStates.Active);
        Assert.Equal(OrderStatus.Filled, stop.Status);
        Assert.Equal(OrderStates.Done, stop.State);
        Assert.Equal(2m, stop.Filled);
        Assert.Equal(0m, stop.Unfilled);
        Assert.Equal(placed.ToString(Inv), stop.ExchangeOrderId); // the handle followed the order its stop placed
        Assert.Contains("GET /fapi/v1/algoOrder", fake.Calls); // looked up when it left the open algo orders…
        Assert.Contains("GET /fapi/v1/order", fake.Calls); // …and so was the fill of the order it placed
        Assert.Empty(broker.ActiveOrders);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_triggered_conditional_order_is_followed_through_the_order_it_placed(bool listedWhileTriggered)
    {
        // a stop-limit placed on Binance's website; whether the open algo orders still show it once it triggered is not documented
        using var fake = new FakeBinance(futures: true) { ListTriggeredAlgoOrders = listedWhileTriggered };
        long algoId = fake.AddExternalAlgo("BUY", "STOP", 3m, triggerPrice: 61000m, price: 61100m);
        using var broker = Broker(fake);
        var counts = new ConcurrentQueue<int>();
        broker.Changed += () => counts.Enqueue(broker.ActiveOrders.Count());
        await Until(() => broker.ActiveOrders.Any());
        var order = Assert.Single(broker.ActiveOrders);
        Assert.Equal("algo:" + algoId.ToString(Inv), order.ExchangeOrderId);
        Assert.Equal((OrderDirections.Buy, OrderTypes.Stop, 61000m, 3m), (order.Direction, order.Type, order.TriggerPrice, order.Unfilled));

        fake.FailLookups = 1; // Binance cannot be asked about it at first: the order it placed must still not look like someone else's
        long placed = fake.Trigger(algoId, fill: 1m); // its limit order rests in the book, partly filled
        await Until(() => order.ExchangeOrderId == placed.ToString(Inv) && order.Status == OrderStatus.PartlyFilled);
        await Polls(fake, 2);
        Assert.Same(order, Assert.Single(broker.ActiveOrders)); // the same handle, and none more for the order it placed
        Assert.Equal(placed.ToString(Inv), order.ExchangeOrderId);
        Assert.Equal((OrderTypes.Limit, 61100m, 1m, 2m, OrderStatus.PartlyFilled), (order.Type, order.Price, order.Filled, order.Unfilled, order.Status));

        fake.Fill(placed, 2m);
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(3m, order.Filled);
        Assert.Empty(broker.ActiveOrders);
        Assert.DoesNotContain(counts, n => n > 1); // never listed twice
    }

    [Fact]
    public async Task An_order_listed_before_Binance_says_which_conditional_order_placed_it_ends_up_on_that_handle()
    {
        using var fake = new FakeBinance(futures: true);
        long algoId = fake.AddExternalAlgo("SELL", "TAKE_PROFIT", 1m, triggerPrice: 66000m, price: 65900m);
        using var broker = Broker(fake);
        await Until(() => broker.ActiveOrders.Any());
        var order = Assert.Single(broker.ActiveOrders);
        var others = new ConcurrentBag<Order>();
        broker.Changed += () =>
        {
            foreach (var o in broker.ActiveOrders)
                if (!ReferenceEquals(o, order)) others.Add(o);
        };

        long placed = fake.Trigger(algoId, staleListings: 2); // the open algo orders lag: its limit order looks like someone else's
        await Until(() => order.ExchangeOrderId == placed.ToString(Inv));
        await Polls(fake, 2);
        Assert.Same(order, Assert.Single(broker.ActiveOrders));
        Assert.Equal((OrderStates.Active, OrderStatus.Placed, 65900m), (order.State, order.Status, order.Price));
        var shown = Assert.Single(others.Distinct()); // listed for a moment as an order of unknown origin…
        Assert.Equal(placed.ToString(Inv), shown.ExchangeOrderId);
        Assert.NotEqual(OrderStates.Active, shown.State); // …then dropped, as the conditional order's handle follows it

        fake.Fill(placed, 1m);
        await Until(() => order.Status == OrderStatus.Filled);
        Assert.Equal(1m, order.Filled);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task A_futures_stop_that_Binance_rejects_when_it_triggers_is_reported_as_Rejected()
    {
        using var fake = new FakeBinance(futures: true);
        using var broker = Broker(fake);
        var errors = new ConcurrentQueue<string>();
        broker.Changed += () =>
        {
            if (broker.LastError is { } e) errors.Enqueue(e);
        };
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1m, 59000m, "Test: Stop loss");
        await Accepted(fake, stop);
        string id = stop.ExchangeOrderId;

        fake.RejectAtTrigger(Assert.Single(fake.PlacedAlgo).AlgoId); // e.g. its margin check failed: the position stays open
        // the handle turns Failed during the poll and the error is published (with Changed) at its end: wait for both
        await Until(() => stop.State == OrderStates.Failed && errors.Any(e => e.Contains("rejected conditional order " + id, StringComparison.Ordinal)));
        Assert.Equal(OrderStates.Failed, stop.State);
        Assert.Equal(OrderStatus.Rejected, stop.Status);
        Assert.Contains(errors, e => e.Contains("rejected conditional order " + id, StringComparison.Ordinal));
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task External_conditional_orders_are_listed_next_to_regular_ones_with_stable_handles()
    {
        using var fake = new FakeBinance(futures: true);
        long manual = fake.AddExternal("BUY", "LIMIT", 1m, 50000m);
        long stopId = fake.AddExternalAlgo("SELL", "STOP_MARKET", 0.2m, triggerPrice: 52000m);
        long targetId = fake.AddExternalAlgo("SELL", "TAKE_PROFIT_MARKET", 0.2m, triggerPrice: 75000m);
        Assert.Equal(manual, stopId); // separate numberings: one number, two orders
        using var broker = Broker(fake);
        await Until(() => broker.ActiveOrders.Count() == 3);

        var limit = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == manual.ToString(Inv));
        Assert.Equal((OrderTypes.Limit, 50000m), (limit.Type, limit.Price));
        var stop = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == "algo:" + stopId.ToString(Inv));
        Assert.Equal((OrderDirections.Sell, OrderTypes.Stop, 52000m, 0.2m, OrderStatus.Placed, ""), (stop.Direction, stop.Type, stop.TriggerPrice, stop.Unfilled, stop.Status, stop.Comment));
        var target = Assert.Single(broker.ActiveOrders, o => o.ExchangeOrderId == "algo:" + targetId.ToString(Inv));
        // a take profit shows as a limit, as before the Algo Order API (a stop could trigger at once when modified); one at
        // market has no limit price, so it shows at its trigger price
        Assert.Equal((OrderTypes.Limit, 75000m, 75000m), (target.Type, target.Price, target.TriggerPrice));

        // an order sent through the broker is listed next to them
        var own = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 0.5m, 51000m, "Test: Stop loss");
        await Accepted(fake, own);
        await Polls(fake, 2);
        Assert.Equal(4, broker.ActiveOrders.Count());
        Assert.Same(stop, broker.ActiveOrders.Single(o => o.ExchangeOrderId == "algo:" + stopId.ToString(Inv)));
        Assert.Same(limit, broker.ActiveOrders.Single(o => o.ExchangeOrderId == manual.ToString(Inv)));

        // canceled outside Trading Hub; the take profit is no longer known to Binance at all
        fake.CancelAtExchange("algo:" + stopId.ToString(Inv));
        fake.CancelAtExchange("algo:" + targetId.ToString(Inv), forget: true);
        await Until(() => stop.Status == OrderStatus.Canceled && target.Status == OrderStatus.Canceled);
        Assert.Equal(OrderStatus.Canceled, stop.Status);
        Assert.Equal(OrderStatus.Canceled, target.Status);
        Assert.Equal(2, broker.ActiveOrders.Count());
        Assert.Contains(own, broker.ActiveOrders);
        Assert.Contains(limit, broker.ActiveOrders);
    }

    [Fact]
    public async Task CancelAll_also_cancels_conditional_orders()
    {
        using var fake = new FakeBinance(futures: true);
        long manual = fake.AddExternalAlgo("BUY", "STOP_MARKET", 1m, triggerPrice: 64000m);
        using var broker = Broker(fake);
        var stop = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 1m, 59000m, "Test: Stop loss");
        var target = broker.Exit(OrderTypes.Limit, OrderDirections.Sell, 1m, 61000m, "Test: Take profit");
        await Until(() => target.ExchangeOrderId.Length > 0 && (stop.State != OrderStates.Active || stop.ExchangeOrderId.Length > 0));

        broker.CancelAll();
        await Until(() => fake.Calls.Contains("DELETE /fapi/v1/allOpenOrders") && fake.Open.Count == 0, 5000);
        Assert.Empty(fake.Open); // regular and conditional orders
        Assert.Contains("DELETE /fapi/v1/algoOpenOrders", fake.Calls);
        Assert.Equal("CANCELED", fake.AlgoOrder(manual).Status);
        await Until(() => !broker.ActiveOrders.Any());
        Assert.Equal(OrderStatus.Canceled, stop.Status);
        Assert.Equal(OrderStatus.Canceled, target.Status);
        Assert.Empty(broker.ActiveOrders);
    }

    [Fact]
    public async Task Spot_stops_stay_STOP_LOSS_orders_on_the_api_v3_order_route()
    {
        using var fake = new FakeBinance(futures: false);
        using var broker = Broker(fake);
        var stop = broker.Stop(OrderDirections.Sell, 0.5m, 60000m, "Test: stop");
        var stopLoss = broker.Exit(OrderTypes.Stop, OrderDirections.Sell, 0.25m, 59000m, "Test: Stop loss");
        await Until(() => broker.ActiveOrders.Count() == 2 && stop.ExchangeOrderId.Length > 0 && stopLoss.ExchangeOrderId.Length > 0);

        var placed = fake.Placed;
        Assert.Equal(2, placed.Count);
        var s = Assert.Single(placed, p => p.StopPrice == 60000m);
        var l = Assert.Single(placed, p => p.StopPrice == 59000m);
        Assert.Equal(("STOP_LOSS", 0.5m, false), (s.Type, s.Qty, s.ReduceOnly));
        Assert.Equal(("STOP_LOSS", 0.25m, false), (l.Type, l.Qty, l.ReduceOnly)); // spot has no reduce-only orders
        Assert.Equal(s.Id.ToString(Inv), stop.ExchangeOrderId);
        Assert.Equal(l.Id.ToString(Inv), stopLoss.ExchangeOrderId);

        fake.Fill(s.Id, 0.5m); // triggered and filled
        broker.Cancel(stopLoss);
        await Until(() => stop.Status == OrderStatus.Filled && stopLoss.Status == OrderStatus.Canceled);
        Assert.Equal(OrderStatus.Filled, stop.Status);
        Assert.Equal(0.5m, stop.Filled);
        Assert.Equal(OrderStatus.Canceled, stopLoss.Status);
        Assert.Equal("CANCELED", fake.Order(l.Id).Status);
        Assert.Contains("POST /api/v3/order", fake.Calls);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("/fapi/", StringComparison.Ordinal) || c.Contains("algo", StringComparison.OrdinalIgnoreCase));
    }
}

[Collection("LiveStore")]
public class BinanceSpotRecoveryTests
{
    private sealed class ResumedSpotStrategy : Strategy { }

    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(12);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spot_native_holdings_confirm_a_resumed_strategy_and_close_only_its_ledger_quantity(bool cachedMetadata)
    {
        string previousRoot = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub_spot_" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = root;
        try
        {
            using var fake = new FakeBinance(futures: false)
            {
                SpotSymbol = "ETHBTC", SpotBaseAsset = "ETH", FailSpotBalances = true,
            };
            fake.SetSpotBalance("ETH", 4.75m, 0.75m);
            fake.SetSpotBalance("BTC", 20m, 2m); // quote currency and unrelated wallet holdings are not this position
            var instrument = new Instrument
            {
                Symbol = "ETHBTC", FeedId = BinanceFeed.SpotId, BaseAsset = cachedMetadata ? "ETH" : "",
                QuoteAsset = "BTC", TickSize = 0.00001m, VolumeStep = 0.001m,
            };
            var data = new InstrumentData(instrument, new SimulatedFeed());
            var time = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 5; i++)
                data.Trades.Add(new Trade(i + 1, time.AddSeconds(i * 30), 0.05m, 1m, TradeSide.Buy));
            using var broker = new BinanceBroker(data, fake.Settings, "Spot recovery fixture");
            using var runner = new LiveStrategyRunner(new ResumedSpotStrategy(), data, broker, ChartPeriod.M1);
            LiveStore.AppendFill(runner.Key, new LiveFill(time.AddDays(-1), true, 0.5m, 0.04m));
            runner.Start();

            await Until(() => broker.LastError != null);
            Assert.False(broker.PositionReady);
            Assert.Equal(0.5m, runner.Ledger.Position); // an unavailable wallet snapshot must not be booked flat
            runner.ClosePosition();
            Assert.Empty(fake.Placed);
            Assert.Equal(0.5m, runner.Ledger.Position);

            fake.FailSpotBalances = false;
            await Until(() => runner.Ledger.Position == 0 && broker.PositionReady && broker.Position.Volume == 5m);
            Assert.True(broker.PositionReady, broker.LastError);
            Assert.Equal(0m, runner.Ledger.Position);
            Assert.Equal(0m, LiveStore.LoadLedger(runner.Key, 1m).Position);
            var close = Assert.Single(fake.Placed);
            Assert.Equal(("ETHBTC", "SELL", "MARKET", 0.5m, "FILLED"), (close.Symbol, close.Side, close.Type, close.Qty, close.Status));
            Assert.False(close.ReduceOnly); // spot has no exchange reduceOnly parameter; the runner bounds its own exit
            Assert.Equal(5m, broker.Position.Volume); // free + locked, after closing only the strategy's 0.5
            Assert.Equal((4.25m, 0.75m), fake.SpotBalance("ETH"));
            Assert.Equal((20m, 2m), fake.SpotBalance("BTC"));
            Assert.Equal(0m, broker.Position.AveragePrice); // account balances provide no aggregate cost basis
            Assert.Equal(0m, broker.Position.UnrealizedPnL);

            fake.FailSpotBalances = true;
            await Until(() => !broker.PositionReady && broker.LastError != null);
            Assert.False(broker.PositionReady);
            Assert.NotNull(broker.LastError);
            Assert.Equal(5m, broker.Position.Volume); // retain last known value, but mark it unknown
            fake.SetSpotBalance("ETH", 0m, 0m);
            fake.FailSpotBalances = false;
            await Until(() => broker.PositionReady && broker.Position.Volume == 0m);
            Assert.True(broker.PositionReady, broker.LastError);
            Assert.Equal(0m, broker.Position.Volume); // absent base asset is flat only after a successful signed snapshot
            Assert.Null(broker.LastError);
            Assert.Single(fake.Placed);
            Assert.Equal(cachedMetadata ? 0 : 1, fake.Calls.Count(c => c == "GET /api/v3/exchangeInfo"));
            Assert.DoesNotContain(fake.Calls, c => c.Contains("/fapi/", StringComparison.Ordinal));
        }
        finally
        {
            LiveStore.Root = previousRoot;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

/// <summary>One regular order at the fake exchange.</summary>
internal sealed record FakeBinanceOrder(long Id, string Symbol, string Side, string Type, decimal Qty, decimal Price, decimal StopPrice, bool ReduceOnly, bool External)
{
    public decimal Executed { get; set; }
    public string Status { get; set; } = "NEW";
    public bool IsOpen => Status is "NEW" or "PARTIALLY_FILLED";

    /// <summary>The conditional (algo) order that placed it when it triggered; 0 if it was sent as it is.</summary>
    public long PlacedBy { get; init; }
    public string ClientId { get; init; } = "";
}

/// <summary>
/// One USD-M conditional (algo) order at the fake exchange, numbered apart from regular orders. When it triggers it
/// places a regular order (<see cref="ActualOrderId"/>) and stays TRIGGERED until that one finishes (FINISHED).
/// </summary>
internal sealed record FakeBinanceAlgoOrder(long AlgoId, string Symbol, string Side, string Type, decimal Qty, decimal TriggerPrice, decimal Price, bool ReduceOnly, bool External)
{
    public string Status { get; set; } = "NEW";
    public long ActualOrderId { get; set; }
    public string ClientId { get; init; } = "";
    public bool IsOpen => Status == "NEW";

    /// <summary>How many more open algo-order lists still show it untriggered after it triggered (a lagging copy).</summary>
    public int StaleListings { get; set; }
}

/// <summary>An order of either kind as a test sees it, by the id the broker shows ("algo:" and the number for a conditional order).</summary>
internal sealed record FakeOrderView(string ExchangeId, string Type, string Status, decimal Qty, decimal Level, bool ReduceOnly);

/// <summary>
/// Binance REST on a local fake server (USD-M futures or spot routes). Signed private calls are checked against the
/// secret; the test moves orders (fills, cancels at the exchange, triggers) to see how the broker follows them. As
/// Binance does since 2025-12-09, USD-M futures refuse conditional order types on /fapi/v1/order (-4120) and take them
/// through the Algo Order API, whose routes accept only their documented parameters.
/// </summary>
internal sealed class FakeBinance : IDisposable
{
    public const string Key = "fake-api-key", Secret = "fake-api-secret";
    private const string AlgoPrefix = "algo:";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HashSet<string> ConditionalTypes = new() { "STOP", "STOP_MARKET", "TAKE_PROFIT", "TAKE_PROFIT_MARKET", "TRAILING_STOP_MARKET" };

    // Parameters each Algo Order API route documents, besides recvWindow, timestamp and signature
    // (https://developers.binance.com/en/docs/catalog/core-trading-derivatives-trading-usd-s-m-futures/api/rest-api/trade).
    private static readonly Dictionary<string, string[]> AlgoParameters = new()
    {
        ["POST /fapi/v1/algoOrder"] = new[]
        {
            "algoType", "symbol", "side", "type", "positionSide", "timeInForce", "quantity", "price", "triggerPrice", "workingType", "priceMatch",
            "closePosition", "priceProtect", "reduceOnly", "activatePrice", "callbackRate", "clientAlgoId", "newOrderRespType", "selfTradePreventionMode",
            "goodTillDate",
        },
        ["DELETE /fapi/v1/algoOrder"] = new[] { "algoId", "clientAlgoId" },
        ["GET /fapi/v1/algoOrder"] = new[] { "algoId", "clientAlgoId" },
        ["GET /fapi/v1/openAlgoOrders"] = new[] { "algoType", "symbol", "algoId" },
        ["DELETE /fapi/v1/algoOpenOrders"] = new[] { "symbol" },
    };

    private readonly FakeHttpServer _server = new();
    private readonly object _gate = new();
    private readonly Dictionary<long, FakeBinanceOrder> _orders = new();
    private readonly Dictionary<long, FakeBinanceAlgoOrder> _algo = new();
    private readonly Dictionary<string, (decimal Free, decimal Locked)> _spotBalances = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forgotten = new();
    private readonly HashSet<string> _fillOnCancel = new();
    private readonly List<string> _calls = new();
    private readonly List<string> _refused = new();
    private readonly List<(string ClientId, int Status)> _clientLookups = new();
    private long _nextId = 7000, _nextAlgoId = 7000; // the same numbers on purpose: the two numberings must not be mixed up
    private int _openOrderPolls;

    public FakeBinance(bool futures)
    {
        Futures = futures;
        _server.OnHttpAsync = Handle;
    }

    public bool Futures { get; }

    public string SpotSymbol { get; set; } = "BTCUSDT";
    public string SpotBaseAsset { get; set; } = "BTC";
    public bool FailSpotBalances { get; set; }

    public void SetSpotBalance(string asset, decimal free, decimal locked)
    {
        lock (_gate) _spotBalances[asset] = (free, locked);
    }

    public (decimal Free, decimal Locked) SpotBalance(string asset)
    {
        lock (_gate) return _spotBalances[asset];
    }

    /// <summary>Delay before answering a new order (the order exists at the exchange meanwhile).</summary>
    public int PostDelayMs { get; set; }
    public bool HoldMarketExecution { get; set; }

    /// <summary>Hold a POST reply after exchange acceptance; other requests continue normally.</summary>
    public Func<CancellationToken, Task>? BeforePostReply { get; set; }

    /// <summary>How many of the next new orders Binance refuses.</summary>
    public int RejectPosts { get; set; }
    public int RejectCancels { get; set; }
    public int UnknownPosts { get; set; }
    public int UnknownPostStatus { get; set; } = 503;
    public int UnknownPostCode { get; set; } = -1007;
    public bool HideOwnedOrders { get; set; }

    /// <summary>The next open-order request waits this long before answering (a network stall).</summary>
    public int StallOpenOrdersOnceMs { get; set; }

    /// <summary>The next cancel is carried out, but its answer waits this long (the client gives up first).</summary>
    public int StallCancelOnceMs { get; set; }

    /// <summary>How many of the next order lookups (regular or conditional) fail with a server error.</summary>
    public int FailLookups { get; set; }

    /// <summary>Whether a stalled cancel is carried out before its late answer (otherwise it is not done at all).</summary>
    public bool StalledCancelIsCarriedOut { get; set; } = true;

    /// <summary>
    /// Whether the open algo orders also show a triggered one while the order it placed works. Binance does not document
    /// it, so the broker must cope either way.
    /// </summary>
    public bool ListTriggeredAlgoOrders { get; set; }

    /// <summary>The order fills (a conditional one: triggers and fills) just before a cancel for it arrives, so Binance refuses the cancel.</summary>
    public void FillWhenCanceled(string exchangeId)
    {
        lock (_gate) _fillOnCancel.Add(exchangeId);
    }

    /// <summary>Settings pointing at this server, polling every 250 ms instead of every 2 s to keep the tests short.</summary>
    public BinanceAccountSettings Settings => new()
    {
        Futures = Futures, Testnet = true, ApiKey = Key, ApiSecret = Secret, BaseUrl = _server.Http, PollInterval = TimeSpan.FromMilliseconds(250),
    };

    public int OpenOrderPolls
    {
        get
        {
            lock (_gate) return _openOrderPolls;
        }
    }

    /// <summary>Requests received, as "METHOD /route".</summary>
    public List<string> Calls
    {
        get
        {
            lock (_gate) return _calls.ToList();
        }
    }

    /// <summary>Requests Binance refused, as "METHOD /route -> answer".</summary>
    public List<string> Refused
    {
        get
        {
            lock (_gate) return _refused.ToList();
        }
    }

    /// <summary>Exact-client lookup replies constructed by the fake; never includes signed query strings.</summary>
    public List<(string ClientId, int Status)> ClientLookups
    {
        get
        {
            lock (_gate) return _clientLookups.ToList();
        }
    }

    /// <summary>Regular orders sent by the broker, oldest first (not those a conditional order placed when it triggered).</summary>
    public List<FakeBinanceOrder> Placed
    {
        get
        {
            lock (_gate) return _orders.Values.Where(o => !o.External && o.PlacedBy == 0).OrderBy(o => o.Id).Select(o => o with { }).ToList();
        }
    }

    /// <summary>Conditional orders sent by the broker, oldest first.</summary>
    public List<FakeBinanceAlgoOrder> PlacedAlgo
    {
        get
        {
            lock (_gate) return _algo.Values.Where(a => !a.External).OrderBy(a => a.AlgoId).Select(a => a with { }).ToList();
        }
    }

    /// <summary>Orders of both kinds sent by the broker.</summary>
    public List<FakeOrderView> Sent
    {
        get
        {
            lock (_gate)
                return _orders.Values.Where(o => !o.External && o.PlacedBy == 0).OrderBy(o => o.Id).Select(ViewOf)
                    .Concat(_algo.Values.Where(a => !a.External).OrderBy(a => a.AlgoId).Select(ViewOf)).ToList();
        }
    }

    public List<FakeBinanceOrder> OpenOrders
    {
        get
        {
            lock (_gate) return _orders.Values.Where(o => o.IsOpen).OrderBy(o => o.Id).Select(o => o with { }).ToList();
        }
    }

    /// <summary>Working orders of both kinds: regular ones, and conditional ones that have not triggered.</summary>
    public List<FakeOrderView> Open
    {
        get
        {
            lock (_gate)
                return _orders.Values.Where(o => o.IsOpen).OrderBy(o => o.Id).Select(ViewOf)
                    .Concat(_algo.Values.Where(a => a.IsOpen).OrderBy(a => a.AlgoId).Select(ViewOf)).ToList();
        }
    }

    public FakeBinanceOrder Order(long id)
    {
        lock (_gate) return _orders[id] with { };
    }

    public FakeBinanceAlgoOrder AlgoOrder(long algoId)
    {
        lock (_gate) return _algo[algoId] with { };
    }

    /// <summary>An order of either kind by the id the broker shows.</summary>
    public FakeOrderView View(string exchangeId)
    {
        lock (_gate) return TryAlgo(exchangeId, out var a) ? ViewOf(a) : ViewOf(_orders[long.Parse(exchangeId, Inv)]);
    }

    /// <summary>An order placed outside Trading Hub (website, another app).</summary>
    public long AddExternal(string side, string type, decimal qty, decimal price, decimal stopPrice = 0)
    {
        lock (_gate)
        {
            var o = new FakeBinanceOrder(++_nextId, "BTCUSDT", side, type, qty, price, stopPrice, false, External: true);
            _orders[o.Id] = o;
            return o.Id;
        }
    }

    /// <summary>A conditional order placed outside Trading Hub; <paramref name="price"/> is the limit of STOP and TAKE_PROFIT.</summary>
    public long AddExternalAlgo(string side, string type, decimal qty, decimal triggerPrice, decimal price = 0)
    {
        lock (_gate)
        {
            var a = new FakeBinanceAlgoOrder(++_nextAlgoId, "BTCUSDT", side, type, qty, triggerPrice, price, false, External: true);
            _algo[a.AlgoId] = a;
            return a.AlgoId;
        }
    }

    public void Fill(long id, decimal qty)
    {
        lock (_gate) FillLocked(_orders[id], qty);
    }

    /// <summary>Fills an order of either kind; a conditional one that has not triggered triggers first.</summary>
    public void Fill(string exchangeId, decimal qty)
    {
        lock (_gate)
        {
            if (!TryAlgo(exchangeId, out var a)) FillLocked(_orders[long.Parse(exchangeId, Inv)], qty);
            else if (a.ActualOrderId == 0) TriggerLocked(a, qty);
            else FillLocked(_orders[a.ActualOrderId], qty);
        }
    }

    /// <summary>
    /// The conditional order triggers: Binance places its order and that fills <paramref name="fill"/>. Returns the
    /// placed order's id. <paramref name="staleListings"/> open algo-order lists still show it untriggered.
    /// </summary>
    public long Trigger(long algoId, decimal fill = 0, int staleListings = 0)
    {
        lock (_gate)
        {
            var a = _algo[algoId];
            a.StaleListings = staleListings;
            return TriggerLocked(a, fill);
        }
    }

    /// <summary>The matching engine refuses the conditional order when it triggers (for example a failed margin check).</summary>
    public void RejectAtTrigger(long algoId)
    {
        lock (_gate) _algo[algoId].Status = "REJECTED";
    }

    /// <summary>Canceled at the exchange; <paramref name="forget"/>: Binance no longer returns it when asked.</summary>
    public void CancelAtExchange(long id, bool forget = false) => CancelAtExchange(id.ToString(Inv), forget);

    public void CancelAtExchange(string exchangeId, bool forget = false)
    {
        lock (_gate)
        {
            if (TryAlgo(exchangeId, out var a)) a.Status = "CANCELED";
            else CancelLocked(_orders[long.Parse(exchangeId, Inv)]);
            if (forget) _forgotten.Add(exchangeId);
        }
    }

    private bool TryAlgo(string exchangeId, out FakeBinanceAlgoOrder a)
    {
        a = null!;
        return exchangeId.StartsWith(AlgoPrefix, StringComparison.Ordinal) && _algo.TryGetValue(long.Parse(exchangeId[AlgoPrefix.Length..], Inv), out a!);
    }

    private static FakeOrderView ViewOf(FakeBinanceOrder o) =>
        new(o.Id.ToString(Inv), o.Type, o.Status, o.Qty, o.StopPrice > 0 ? o.StopPrice : o.Price, o.ReduceOnly);

    private static FakeOrderView ViewOf(FakeBinanceAlgoOrder a) =>
        new(AlgoPrefix + a.AlgoId.ToString(Inv), a.Type, a.Status, a.Qty, a.TriggerPrice, a.ReduceOnly);

    private void FillLocked(FakeBinanceOrder o, decimal qty)
    {
        if (!Futures && o.Symbol == SpotSymbol && _spotBalances.TryGetValue(SpotBaseAsset, out var balance))
            _spotBalances[SpotBaseAsset] = (balance.Free + (o.Side == "BUY" ? qty : -qty), balance.Locked);
        o.Executed += qty;
        o.Status = o.Executed >= o.Qty ? "FILLED" : "PARTIALLY_FILLED";
        if (o.Status == "FILLED" && o.PlacedBy != 0) _algo[o.PlacedBy].Status = "FINISHED"; // filled in the matching engine
    }

    private void CancelLocked(FakeBinanceOrder o)
    {
        o.Status = "CANCELED";
        if (o.PlacedBy != 0) _algo[o.PlacedBy].Status = "FINISHED"; // canceled in the matching engine
    }

    /// <summary>Binance places the conditional order's order: a market order, or a limit one for STOP and TAKE_PROFIT.</summary>
    private long TriggerLocked(FakeBinanceAlgoOrder a, decimal fill)
    {
        string type = a.Type.EndsWith("_MARKET", StringComparison.Ordinal) ? "MARKET" : "LIMIT";
        var o = new FakeBinanceOrder(++_nextId, a.Symbol, a.Side, type, a.Qty, type == "LIMIT" ? a.Price : 0, 0, a.ReduceOnly, a.External) { PlacedBy = a.AlgoId };
        _orders[o.Id] = o;
        a.Status = "TRIGGERED";
        a.ActualOrderId = o.Id;
        if (fill > 0) FillLocked(o, fill);
        return o.Id;
    }

    private static decimal Dec(string s) => s.Length == 0 ? 0 : decimal.Parse(s, NumberStyles.Float, Inv);

    private static string Num(decimal v) => v.ToString("0.########", Inv);

    private static string Json(FakeBinanceOrder o) => string.Create(Inv,
        $"{{\"orderId\":{o.Id},\"clientOrderId\":\"{o.ClientId}\",\"symbol\":\"{o.Symbol}\",\"status\":\"{o.Status}\",\"side\":\"{o.Side}\",\"type\":\"{o.Type}\",\"price\":\"{Num(o.Price)}\",\"stopPrice\":\"{Num(o.StopPrice)}\",\"origQty\":\"{Num(o.Qty)}\",\"executedQty\":\"{Num(o.Executed)}\",\"reduceOnly\":{(o.ReduceOnly ? "true" : "false")},\"time\":1790000000000,\"updateTime\":1790000000000}}");

    private enum AlgoAnswer
    {
        New,
        Listed,
        LookedUp,
    }

    /// <summary>
    /// A conditional order with the fields Binance documents for each answer: the new-order answer has no actual* fields;
    /// a lookup adds actualType once triggered and actualQty once its order filled some. Fields that do not apply are
    /// empty strings, as in the documented examples.
    /// </summary>
    private string AlgoJson(FakeBinanceAlgoOrder a, AlgoAnswer answer)
    {
        var placed = a.ActualOrderId != 0 ? _orders[a.ActualOrderId] : null;
        var sb = new StringBuilder();
        sb.Append(Inv, $"{{\"algoId\":{a.AlgoId},\"clientAlgoId\":\"{a.ClientId}\",\"algoType\":\"CONDITIONAL\",\"orderType\":\"{a.Type}\",\"symbol\":\"{a.Symbol}\",\"side\":\"{a.Side}\",\"positionSide\":\"BOTH\",\"timeInForce\":\"GTC\",\"quantity\":\"{Num(a.Qty)}\",\"algoStatus\":\"{a.Status}\"");
        if (answer != AlgoAnswer.New)
        {
            string actualPrice = placed is { Executed: > 0 } ? Num(placed.Price > 0 ? placed.Price : a.TriggerPrice) : "0.00000";
            sb.Append(Inv, $",\"actualOrderId\":\"{(placed == null ? "" : placed.Id.ToString(Inv))}\",\"actualPrice\":\"{actualPrice}\"");
            if (answer == AlgoAnswer.LookedUp && placed != null) sb.Append(Inv, $",\"actualType\":\"{placed.Type}\"");
            if (answer == AlgoAnswer.LookedUp && placed is { Executed: > 0 }) sb.Append(Inv, $",\"actualQty\":\"{Num(placed.Executed)}\"");
        }

        sb.Append(Inv, $",\"triggerPrice\":\"{Num(a.TriggerPrice)}\",\"price\":\"{(a.Price > 0 ? Num(a.Price) : "")}\",\"icebergQuantity\":\"null\",\"selfTradePreventionMode\":\"NONE\",\"workingType\":\"CONTRACT_PRICE\",\"priceMatch\":\"NONE\",\"closePosition\":false,\"priceProtect\":false,\"reduceOnly\":{(a.ReduceOnly ? "true" : "false")}");
        if (answer == AlgoAnswer.New) sb.Append(",\"activatePrice\":\"\",\"callbackRate\":\"\"");
        sb.Append(",\"createTime\":1790000000000,\"updateTime\":1790000000000,\"triggerTime\":0,\"goodTillDate\":0}");
        return sb.ToString();
    }

    private static (int, string) Error(int code, string msg) => (400, $"{{\"code\":{code},\"msg\":\"{msg}\"}}");

    private static bool Signed(FakeRequest r)
    {
        if (r.Headers.GetValueOrDefault("X-MBX-APIKEY") != Key) return false;
        int q = r.Path.IndexOf('?');
        if (q < 0) return false;
        string query = r.Path[(q + 1)..];
        const string marker = "&signature=";
        int s = query.LastIndexOf(marker, StringComparison.Ordinal);
        return s > 0 && query[(s + marker.Length)..] == BinanceAccountClient.Sign(query[..s], Secret);
    }

    /// <summary>Parameters a request sent that its route does not document (null if none).</summary>
    private static string? Undocumented(FakeRequest r, string[] documented)
    {
        int q = r.Path.IndexOf('?');
        if (q < 0) return null;
        var extra = r.Path[(q + 1)..].Split('&').Select(p => Uri.UnescapeDataString(p.Split('=')[0]))
            .Where(n => n is not ("recvWindow" or "timestamp" or "signature") && !documented.Contains(n)).ToList();
        return extra.Count == 0 ? null : string.Join(", ", extra);
    }

    private async ValueTask<(int Status, string Body)> Handle(FakeRequest r, CancellationToken ct)
    {
        var answer = Answer(r);
        if (r.Method == "GET")
        {
            string clientId = r.Route == "/fapi/v1/algoOrder" ? r.Query("clientAlgoId") : r.Query("origClientOrderId");
            if (clientId.Length > 0)
                lock (_gate) _clientLookups.Add((clientId, answer.Status));
        }
        if (r.Method == "POST" && answer.Status == 200 && UnknownPosts > 0)
        {
            UnknownPosts--;
            answer = (UnknownPostStatus, $"{{\"code\":{UnknownPostCode},\"msg\":\"Unknown error, please check your request or try again later.\"}}");
        }
        if (r.Method == "POST" && BeforePostReply is { } beforeReply)
            await beforeReply(ct).ConfigureAwait(false);
        if (answer.Status >= 400)
            lock (_gate) _refused.Add(r.Method + " " + r.Route + " -> " + answer.Body);
        return answer;
    }

    private (int Status, string Body) Answer(FakeRequest r)
    {
        string route = r.Route;
        lock (_gate) _calls.Add(r.Method + " " + route);
        if (!route.StartsWith(Futures ? "/fapi/" : "/api/v3/", StringComparison.Ordinal)) return Error(-1, "Wrong market route.");
        if (route is "/fapi/v1/time" or "/api/v3/time") return (200, $"{{\"serverTime\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
        if (r.Method == "GET" && route == "/api/v3/exchangeInfo")
            return r.Query("symbol") == SpotSymbol
                ? (200, $"{{\"symbols\":[{{\"symbol\":\"{SpotSymbol}\",\"baseAsset\":\"{SpotBaseAsset}\"}}]}}")
                : Error(-1121, "Invalid symbol.");
        if (!Signed(r)) return (401, "{\"code\":-1022,\"msg\":\"Signature for this request is not valid.\"}");
        if (AlgoParameters.TryGetValue(r.Method + " " + route, out var documented) && Undocumented(r, documented) is { } extra)
            return Error(-1104, "Not all sent parameters were read; undocumented: " + extra);
        bool algo = route is "/fapi/v1/algoOrder" or "/fapi/v1/algoOpenOrders";
        if (r.Method == "POST" && PostDelayMs > 0)
        {
            var placed = algo ? PlaceAlgo(r) : Place(r);
            Thread.Sleep(PostDelayMs);
            return placed;
        }

        if (r.Method == "GET" && route is "/fapi/v1/openOrders" or "/api/v3/openOrders")
        {
            int stall;
            lock (_gate)
            {
                stall = StallOpenOrdersOnceMs;
                StallOpenOrdersOnceMs = 0;
            }

            if (stall > 0) Thread.Sleep(stall);
        }

        if (r.Method == "DELETE" && route is "/fapi/v1/order" or "/api/v3/order" or "/fapi/v1/algoOrder")
        {
            int stall;
            lock (_gate)
            {
                stall = StallCancelOnceMs;
                StallCancelOnceMs = 0;
            }

            if (stall > 0)
            {
                // carried out at once (or not at all), answered late
                var answer = !StalledCancelIsCarriedOut ? Error(-1007, "Timeout waiting for response from backend server. Send status unknown; execution status unknown.")
                    : algo ? CancelOneAlgo(r) : CancelOne(r);
                Thread.Sleep(stall);
                return answer;
            }
        }

        lock (_gate)
        {
            switch (r.Method, route)
            {
                case ("GET", "/fapi/v1/openOrders" or "/api/v3/openOrders"):
                    _openOrderPolls++;
                    return (200, "[" + string.Join(",", _orders.Values.Where(o => o.IsOpen && o.Symbol == r.Query("symbol") && (!HideOwnedOrders || o.External)).OrderBy(o => o.Id).Select(Json)) + "]");
                case ("POST", "/fapi/v1/order" or "/api/v3/order"):
                    return Place(r);
                case ("DELETE", "/fapi/v1/order" or "/api/v3/order"):
                    return CancelOne(r);
                case ("GET", "/fapi/v1/order" or "/api/v3/order"):
                {
                    long id = long.TryParse(r.Query("orderId"), Inv, out var requestedId) ? requestedId :
                        _orders.Values.FirstOrDefault(o => o.ClientId.Length > 0 && o.ClientId == r.Query("origClientOrderId"))?.Id ?? 0;
                    if (FailLookups > 0)
                    {
                        FailLookups--;
                        return (500, "{\"code\":-1001,\"msg\":\"Internal error; unable to process your request. Please try again.\"}");
                    }

                    if (!_orders.TryGetValue(id, out var o) || _forgotten.Contains(id.ToString(Inv)) || (HideOwnedOrders && !o.External)) return Error(-2013, "Order does not exist.");
                    return (200, Json(o));
                }
                case ("GET", "/fapi/v2/positionRisk"):
                    return (200, "[]");
                case ("GET", "/api/v3/account"):
                    if (FailSpotBalances) return (503, "{\"code\":-1001,\"msg\":\"Wallet snapshot unavailable.\"}");
                    return (200, "{\"canTrade\":true,\"balances\":[" + string.Join(",", _spotBalances.Where(b => b.Value.Free + b.Value.Locked != 0)
                        .Select(b => $"{{\"asset\":\"{b.Key}\",\"free\":\"{Num(b.Value.Free)}\",\"locked\":\"{Num(b.Value.Locked)}\"}}")) + "]}");
                case ("DELETE", "/fapi/v1/allOpenOrders" or "/api/v3/openOrders"):
                {
                    var canceled = _orders.Values.Where(o => o.IsOpen && o.Symbol == r.Query("symbol")).ToList();
                    foreach (var o in canceled) CancelLocked(o);
                    return Futures ? (200, "{\"code\":200,\"msg\":\"The operation of cancel all open order is done.\"}") : (200, "[" + string.Join(",", canceled.Select(Json)) + "]");
                }
                case ("GET", "/fapi/v1/openAlgoOrders"):
                    return (200, "[" + string.Join(",", ListedAlgo(r.Query("symbol"))) + "]");
                case ("POST", "/fapi/v1/algoOrder"):
                    return PlaceAlgo(r);
                case ("DELETE", "/fapi/v1/algoOrder"):
                    return CancelOneAlgo(r);
                case ("GET", "/fapi/v1/algoOrder"):
                {
                    if (FailLookups > 0)
                    {
                        FailLookups--;
                        return (500, "{\"code\":-1001,\"msg\":\"Internal error; unable to process your request. Please try again.\"}");
                    }

                    long id = long.TryParse(r.Query("algoId"), Inv, out var requestedId) ? requestedId :
                        _algo.Values.FirstOrDefault(a => a.ClientId.Length > 0 && a.ClientId == r.Query("clientAlgoId"))?.AlgoId ?? 0;
                    if (!_algo.TryGetValue(id, out var a) || _forgotten.Contains(AlgoPrefix + id.ToString(Inv)) || (HideOwnedOrders && !a.External)) return Error(-2013, "Order does not exist.");
                    return (200, AlgoJson(a, AlgoAnswer.LookedUp));
                }
                case ("DELETE", "/fapi/v1/algoOpenOrders"):
                {
                    foreach (var a in _algo.Values.Where(a => a.IsOpen && a.Symbol == r.Query("symbol"))) a.Status = "CANCELED";
                    return (200, "{\"code\":200,\"msg\":\"The operation of cancel all open order is done.\"}");
                }
            }
        }

        return Error(-1, "Unknown route.");
    }

    /// <summary>
    /// The open conditional orders of a symbol: those not triggered and, with <see cref="ListTriggeredAlgoOrders"/>,
    /// triggered ones whose order still works. One that triggered moments ago may still show as untriggered.
    /// </summary>
    private List<string> ListedAlgo(string symbol)
    {
        var listed = new List<string>();
        foreach (var a in _algo.Values.Where(a => a.Symbol == symbol).OrderBy(a => a.AlgoId))
        {
            if (HideOwnedOrders && !a.External) continue;
            if (a.StaleListings > 0)
            {
                a.StaleListings--;
                listed.Add(AlgoJson(a with { Status = "NEW", ActualOrderId = 0 }, AlgoAnswer.Listed));
            }
            else if (a.IsOpen || (ListTriggeredAlgoOrders && a.Status == "TRIGGERED" && _orders[a.ActualOrderId].IsOpen))
            {
                listed.Add(AlgoJson(a, AlgoAnswer.Listed));
            }
        }

        return listed;
    }

    private (int, string) CancelOne(FakeRequest r)
    {
        lock (_gate)
        {
            if (RejectCancels > 0)
            {
                RejectCancels--;
                return Error(-2015, "Invalid API-key, IP, or permissions for action.");
            }
            long id = long.Parse(r.Query("orderId"), Inv);
            if (!_orders.TryGetValue(id, out var o) || !o.IsOpen) return Error(-2011, "Unknown order sent.");
            if (_fillOnCancel.Remove(id.ToString(Inv)))
            {
                FillLocked(o, o.Qty - o.Executed);
                return Error(-2011, "Unknown order sent.");
            }

            o.Status = "CANCELED";
            return (200, Json(o));
        }
    }

    /// <summary>Cancels an untriggered conditional order; Binance's answer carries no state, only code 200 and "success".</summary>
    private (int, string) CancelOneAlgo(FakeRequest r)
    {
        lock (_gate)
        {
            if (RejectCancels > 0)
            {
                RejectCancels--;
                return Error(-2015, "Invalid API-key, IP, or permissions for action.");
            }
            long id = long.Parse(r.Query("algoId"), Inv);
            if (!_algo.TryGetValue(id, out var a) || !a.IsOpen) return Error(-2011, "Unknown order sent.");
            if (_fillOnCancel.Remove(AlgoPrefix + id.ToString(Inv)))
            {
                TriggerLocked(a, a.Qty);
                return Error(-2011, "Unknown order sent.");
            }

            a.Status = "CANCELED";
            return (200, $"{{\"algoId\":{id},\"clientAlgoId\":\"fake{id}\",\"code\":\"200\",\"msg\":\"success\"}}");
        }
    }

    private (int, string) Place(FakeRequest r)
    {
        lock (_gate)
        {
            string type = r.Query("type");
            if (Futures && ConditionalTypes.Contains(type))
                return Error(-4120, "Order type not supported for this endpoint. Please use the Algo Order API endpoints instead.");
            if (RejectPosts > 0)
            {
                RejectPosts--;
                return Error(-2021, "Order would immediately trigger.");
            }

            var o = new FakeBinanceOrder(++_nextId, r.Query("symbol"), r.Query("side"), type, Dec(r.Query("quantity")), Dec(r.Query("price")), Dec(r.Query("stopPrice")), r.Query("reduceOnly") == "true", External: false) { ClientId = r.Query("newClientOrderId") };
            _orders[o.Id] = o;
            if (type == "MARKET" && !HoldMarketExecution)
            {
                // the matching engine fills it at once
                FillLocked(o, o.Qty);
            }

            // futures answer ACK by default (accepted, nothing more); spot answers FULL for market and limit orders, ACK otherwise
            if (Futures) return (200, Json(o with { Executed = 0, Status = "NEW" }));
            return (200, type is "MARKET" or "LIMIT" ? Json(o) : $"{{\"symbol\":\"{o.Symbol}\",\"orderId\":{o.Id},\"orderListId\":-1,\"clientOrderId\":\"fake\",\"transactTime\":1790000000000}}");
        }
    }

    private (int, string) PlaceAlgo(FakeRequest r)
    {
        lock (_gate)
        {
            string type = r.Query("type");
            if (r.Query("algoType") != "CONDITIONAL" || !ConditionalTypes.Contains(type)) return Error(-1116, "Invalid orderType.");
            decimal qty = Dec(r.Query("quantity")), trigger = Dec(r.Query("triggerPrice"));
            if (qty <= 0 || trigger <= 0 || r.Query("symbol").Length == 0 || r.Query("side") is not ("BUY" or "SELL"))
                return Error(-1102, "A mandatory parameter was not sent, was empty/null, or malformed.");
            if (RejectPosts > 0)
            {
                RejectPosts--;
                return Error(-2021, "Order would immediately trigger.");
            }

            var a = new FakeBinanceAlgoOrder(++_nextAlgoId, r.Query("symbol"), r.Query("side"), type, qty, trigger, Dec(r.Query("price")), r.Query("reduceOnly") == "true", External: false) { ClientId = r.Query("clientAlgoId") };
            _algo[a.AlgoId] = a;
            return (200, AlgoJson(a, AlgoAnswer.New));
        }
    }

    public void Dispose() => _server.Dispose();
}
