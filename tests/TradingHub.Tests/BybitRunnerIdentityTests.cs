using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class BybitRunnerIdentityTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, PointValue = 1 };
    private sealed class PassiveProtection : Strategy { protected override void OnBar(Bar bar) { } }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_pre_ACK_client_identity_reconnects_actual_runner_protection_without_duplicate_or_orphan(bool cancelBeforeReconciliation)
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-runner-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "provider", "context.json");
        LiveStore.Root = Path.Combine(root, "live");
        using var protocol = new FakeBybitProtocol { NetPosition = 1, HoldCreate = true };
        using var http = new HttpClient(protocol);
        using var data = new InstrumentData(Btc, new SimulatedFeed());
        data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 100, 1, TradeSide.Buy));
        string key;
        string[] savedIdentities;
        long oldLocalId;
        try
        {
            await using (var first = new BybitTradingSession(protocol.Settings, state, http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now)))
            {
                using var broker = new BybitBroker(data, first, "Bybit fake account");
                await first.RefreshAsync();
                using var runner = new LiveStrategyRunner(new PassiveProtection { StopLossTicks = 50 }, data, broker, ChartPeriod.M1)
                { ProtectOnStart = true, AccountId = "bybit-owned", ProfileId = "bybit-owned" };
                runner.SeedPosition(new LiveFill(FakeBybitProtocol.Now.UtcDateTime.AddDays(-30), true, 1, 100));
                runner.Start();
                await protocol.CreateReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var pending = Assert.Single(broker.ActiveOrders);
                Assert.Empty(pending.ExchangeOrderId);
                oldLocalId = pending.Id;
                key = runner.Key;
                runner.Stop(flatten: false, keepProtection: true);
                savedIdentities = LiveStore.InfoFor(key)!.ProtectionOrderIds!.ToArray();
                Assert.StartsWith("client:bybit:demo-1234567:th-", Assert.Single(savedIdentities));
                Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
                // Disposing the transport before the held ACK models a process losing its in-memory exchange ID.
                // The provider intent and runner's protection selection have already been written through their real stores.
            }

            protocol.HoldCreate = false;
            await using var recovered = new BybitTradingSession(protocol.Settings, state, http, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var recoveredBroker = new BybitBroker(data, recovered, "Bybit fake account");
            var loaded = Assert.Single(recovered.Orders);
            Assert.NotEqual(oldLocalId, loaded.Id);
            Assert.False(recovered.OrdersReady);
            var lookupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            protocol.ResponseForAsync = async (request, cancellationToken) =>
            {
                if (request.Path == "/v5/order/realtime")
                {
                    lookupEntered.TrySetResult();
                    await releaseLookup.Task.WaitAsync(cancellationToken);
                }
                return null;
            };
            using var resumed = new LiveStrategyRunner(new PassiveProtection { StopLossTicks = 50 }, data, recoveredBroker, ChartPeriod.M1)
            {
                ProtectOnStart = true, AccountId = "bybit-owned", ProfileId = "bybit-owned",
                AdoptProtectionIds = LiveStore.InfoFor(key)!.ProtectionOrderIds!,
            };
            Assert.False(resumed.TryPrepareIdentity(out _)); // The saved UID alone does not authorize reading another account's ledger.
            var reconciliation = recovered.RefreshAsync();
            await lookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); // Fresh UID verified, but order lookup/position readiness still held.
            Assert.True(resumed.TryPrepareIdentity(out _));
            resumed.Start();
            Assert.Equal(savedIdentities, resumed.ProtectionOrderIds);
            if (cancelBeforeReconciliation) resumed.Stop(flatten: false, keepProtection: false);
            releaseLookup.TrySetResult();
            await reconciliation;
            await recovered.RefreshAsync();
            if (cancelBeforeReconciliation)
            {
                await protocol.CancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await recovered.RefreshAsync();
                Assert.Equal(OrderStatus.Canceled, loaded.Status);
                Assert.Empty(recoveredBroker.ActiveOrders);
            }
            else
            {
                Assert.Same(loaded, Assert.Single(recoveredBroker.ActiveOrders));
                Assert.Equal("exchange-1", loaded.ExchangeOrderId);
                Assert.Equal(savedIdentities, resumed.ProtectionOrderIds);
                Assert.Single(resumed.ActiveOrders, order => order.IsProtective);
                Assert.Contains(resumed.LogLines, line => line.Contains("Adopted", StringComparison.Ordinal));
                resumed.Stop(flatten: false, keepProtection: true);
            }
            Assert.Single(protocol.Requests, request => request.Path == "/v5/order/create");
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
