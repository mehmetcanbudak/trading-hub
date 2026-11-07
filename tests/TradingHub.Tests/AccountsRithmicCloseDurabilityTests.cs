using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using TradingHub.App.Services;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.Tests;

public sealed partial class AccountsRithmicCloseRegressionTests
{
    [Fact]
    public async Task Restarted_close_charges_a_known_terminal_reduction_from_a_different_observation_scope()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100, ReplayTerminalOrders = true, ReportedPositionOverride = 2 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        var original = target.Confirmation.Context.BeginPositionClose(target.Broker.Instrument.Key,
            target.Confirmation.State with { ConfirmationRevision = 10000, ObservationScope = "previous-process" });
        Assert.Null(original.Error);
        await fixture.Session.StopAsync().WaitAsync(Deadline);
        server.SeedCompletedOrder("restart-terminal-reduction", "external-terminal", false, 1, 100);
        await fixture.RestartAsync();
        var recovered = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        Assert.Null(await recovered.Broker.ClosePositionAsync(recovered.Confirmation).WaitAsync(Deadline));
        string tag = Assert.Single(Tags(server));
        var sent = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.UserTag == tag);
        Assert.Equal(1m, sent.Quantity);
        Assert.False(sent.IsBuy);
        Assert.Equal(0, server.Position);
        var ticket = Assert.Single(recovered.Confirmation.Context.PositionCloses());
        Assert.Equal(original.Id, ticket.Id);
        Assert.Equal(1m, recovered.Confirmation.Context.PositionCloseState(ticket).KnownReductions);
    }

    [Fact]
    public async Task A_failed_prior_journal_registration_is_retried_before_a_cached_chart_route_can_dispatch()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var current = fixture.Services.RithmicContextFor(fixture.Profile, fixture.Session, "ACC1");
        var descriptor = current.Descriptor with { Id = Guid.NewGuid().ToString("N"), Generation = Guid.NewGuid().ToString("N") };
        var prior = new OrderContext(descriptor, fixture.ExecutionRoot);
        var saved = prior.BeginPositionClose("rithmic:ES", NativeClose());
        Assert.Null(saved.Error);
        string path = Path.Combine(fixture.ExecutionRoot, "execution-state", OrderContext.Hash(descriptor.Id), "intent", OrderContext.Hash(saved.Id) + ".json");
        byte[] complete = await File.ReadAllBytesAsync(path);
        await File.WriteAllTextAsync(path, "corrupt-prior-close");
        var chart = fixture.Chart("ES");
        try
        {
            await Until(() => chart.LastError?.Contains("Could not determine active contract", StringComparison.Ordinal) == true);
        }
        finally { await File.WriteAllBytesAsync(path, complete); }
        var order = OrderActions.Manual(chart, OrderTypes.Market, OrderDirections.Sell, 2, reduceOnly: true);
        await Until(() => order.Status == OrderStatus.Rejected || order.ExchangeOrderId.Length > 0);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Empty(Tags(server));
        Assert.True(fixture.Session.HasUnresolvedPositionClose);
        Assert.Contains("original Rithmic close session is unavailable", fixture.Session.PositionCloseRecoveryStatus);
    }

    [Fact]
    public void Cached_contexts_choose_one_durable_native_contract_close_identity()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-close-cached-" + Guid.NewGuid().ToString("N"));
        try
        {
            var descriptor = ClaimDescriptor();
            var first = new OrderContext(descriptor, root);
            var second = new OrderContext(descriptor, root);
            Assert.Empty(first.PositionCloses());
            Assert.Empty(second.PositionCloses());
            var one = first.BeginPositionClose("rithmic:ESZ6", NativeClose());
            var two = second.BeginPositionClose("rithmic:ESZ6", NativeClose());
            Assert.Null(one.Error);
            Assert.Null(two.Error);
            Assert.Equal(one.Id, two.Id);
            Assert.Equal(one.ClientOrderId, two.ClientOrderId);
            Assert.Equal(one.Id, Assert.Single(new OrderContext(descriptor, root).PositionCloses()).Id);
            Assert.Null(first.StartDispatch(one, "ESZ6", "CME", 2));
            Assert.NotNull(second.StartDispatch(two, "ESZ6", "CME", 2));
            Assert.True(new OrderContext(descriptor, root).Find(one.Id)!.DispatchUncertain);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Separate_cached_processes_choose_one_close_identity_with_a_shared_execution_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-close-processes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".ledger-fixture-root"), "native close fixture");
        try
        {
            using var first = StartCloseClaim(root, "first");
            using var second = StartCloseClaim(root, "second");
            var firstError = first.StandardError.ReadToEndAsync();
            var secondError = second.StandardError.ReadToEndAsync();
            string? one = null, two = null;
            try
            {
                Assert.Equal("checkpoint", await first.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
                Assert.Equal("checkpoint", await second.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
            }
            finally
            {
                await first.StandardInput.WriteLineAsync("continue");
                await first.StandardInput.FlushAsync();
                await second.StandardInput.WriteLineAsync("continue");
                await second.StandardInput.FlushAsync();
                one = await first.StandardOutput.ReadLineAsync().WaitAsync(Deadline);
                two = await second.StandardOutput.ReadLineAsync().WaitAsync(Deadline);
                await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(Deadline);
            }
            Assert.True(first.ExitCode == 0, await firstError);
            Assert.True(second.ExitCode == 0, await secondError);
            using var firstClaim = JsonDocument.Parse(one!);
            using var secondClaim = JsonDocument.Parse(two!);
            Assert.Equal(firstClaim.RootElement.GetProperty("Id").GetString(), secondClaim.RootElement.GetProperty("Id").GetString());
            Assert.Equal(firstClaim.RootElement.GetProperty("ClientOrderId").GetString(), secondClaim.RootElement.GetProperty("ClientOrderId").GetString());
            Assert.Equal(1, (firstClaim.RootElement.GetProperty("Dispatched").GetBoolean() ? 1 : 0) +
                (secondClaim.RootElement.GetProperty("Dispatched").GetBoolean() ? 1 : 0));
            Assert.Single(new OrderContext(ClaimDescriptor(), Path.Combine(root, "shared-execution")).PositionCloses());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Replay_recovers_the_accepted_quantity_amendment_but_not_unsent_or_rejected_successors()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        fixture.Profile.AllowRealOrders = true;
        fixture.Profile.MaxOrderQuantity = 3;
        var chart = fixture.Chart("ES");
        await Until(() => chart.PositionReady && chart.OrdersReady);
        var order = OrderActions.Manual(chart, OrderTypes.Limit, OrderDirections.Buy, 1, 90);
        await Until(() => order.ExchangeOrderId.Length > 0 && order.QuantityToFill == 1);
        string originalTag = Assert.IsType<string>(order.ClientOrderId);
        string logical = order.LogicalOrderId!;
        string basket = order.ExchangeOrderId;
        OrderActions.Replace(chart, order, 91, 2);
        await Until(() => order.QuantityToFill == 2 && !((ITrackedOrderReplacement)chart).IsReplacementPending(order));
        string accepted = order.IntentId!;
        Assert.Equal(originalTag, fixture.Session.OrdersOf("ACC1").Single(native => native.BasketId == basket).UserTag);
        var unsent = chart.OrderContext.PrepareReplacement(order, 92, 3);
        Assert.Null(unsent.Error);
        server.ModifyRejection = "fixture rejected quantity three";
        OrderActions.Replace(chart, order, 93, 3);
        await Until(() => !((ITrackedOrderReplacement)chart).IsReplacementPending(order) && chart.LastError?.Contains("fixture rejected", StringComparison.Ordinal) == true);
        await fixture.RestartAsync();
        var restored = fixture.Chart("ES");
        await Until(() => restored.OrdersReady && restored.ActiveOrders.Any());
        var recovered = Assert.Single(restored.ActiveOrders);
        Assert.Equal(accepted, recovered.IntentId);
        Assert.Equal(logical, recovered.LogicalOrderId);
        Assert.Equal(basket, recovered.ExchangeOrderId);
        Assert.Equal(2m, recovered.QuantityToFill);
        Assert.Equal(chart.OrderContext.ManualOwner.Id, recovered.OwnerId);
        Assert.DoesNotContain("recovery is unresolved", restored.LastError ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Single(Tags(server));
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task Final_native_permission_uses_the_dispatching_snapshot_not_a_stale_exact_contract_sibling(bool allowRealOrders, int cap)
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        fixture.Profile.AllowRealOrders = allowRealOrders;
        fixture.Profile.MaxOrderQuantity = cap;
        var chart = fixture.Chart("ESZ6");
        await Until(() => chart.PositionReady && chart.OrdersReady && chart.Position.Volume == 2);
        var sibling = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref).Broker;
        Assert.NotSame(chart, sibling);
        Assert.Same(chart.OrderContext, sibling.OrderContext);
        Assert.Equal(2m, sibling.Position.Volume);
        var slot = await fixture.HoldSendSlotAsync();
        GateHold? held = null;
        Order? order = null;
        bool slotOwned = true;
        try
        {
            order = OrderActions.Manual(chart, OrderTypes.Market, OrderDirections.Sell, 2);
            await Until(() => fixture.PendingRequests > 0);
            held = new GateHold(NativeField<object>(chart, "_gate"));
            slot.Release();
            slotOwned = false;
            object admission = typeof(RithmicTradingSession).GetField("NativeAdmissionGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            await Until(() => IsHeld(admission));
            server.SeedPosition(false, 2, 100);
            await server.PublishPosition();
            await Until(() => fixture.Net == 0);
            Assert.Equal(2m, chart.Position.Volume);
            Assert.Equal(2m, sibling.Position.Volume);
        }
        finally
        {
            held?.Dispose();
            if (slotOwned) slot.Release();
        }
        Assert.NotNull(order);
        await Until(() => order.Status == OrderStatus.Rejected || order.ExchangeOrderId.Length > 0);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Empty(Tags(server));
        Assert.Equal(0, server.Position);
        Assert.False(chart.OrderContext.Find(order.IntentId)!.DispatchUncertain);
    }

    [Fact]
    public async Task Proven_equivalent_sessions_share_outstanding_reductions_before_cross_session_broadcasts()
    {
        var fillRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true, MarketFillRelease = fillRelease };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        server.SeedPosition(true, 3, 100);
        await server.PublishPosition();
        await Until(() => fixture.Net == 5);
        await using var peer = await NativePeer.CreateAsync(fixture, server, target);
        try
        {
            Assert.Null(await target.Broker.ClosePositionAsync(target.Confirmation).WaitAsync(Deadline));
            await Until(() => fixture.Session.OrdersOf("ACC1").Any(order => order.PriceType == 2 && !order.Done));
            var first = OrderActions.Manual(target.Broker, OrderTypes.Stop, OrderDirections.Sell, 1, 95, reduceOnly: true);
            await Until(() => first.ExchangeOrderId.Length > 0);
            Assert.DoesNotContain(peer.Session.OrdersOf("ACC1"), order => order.BasketId == first.ExchangeOrderId);
            var overspend = OrderActions.Manual(peer.Broker, OrderTypes.Stop, OrderDirections.Sell, 3, 94, reduceOnly: true);
            await Until(() => overspend.Status == OrderStatus.Rejected || overspend.ExchangeOrderId.Length > 0);
            Assert.Equal(OrderStatus.Rejected, overspend.Status);
            Assert.Equal(2, Tags(server).Length);
            var available = OrderActions.Manual(peer.Broker, OrderTypes.Stop, OrderDirections.Sell, 2, 93, reduceOnly: true);
            await Until(() => available.ExchangeOrderId.Length > 0 || available.Status == OrderStatus.Rejected);
            Assert.NotEqual(OrderStatus.Rejected, available.Status);
            Assert.Equal(3, Tags(server).Length);
            Assert.Equal(0, server.Kicks);
        }
        finally { fillRelease.TrySetResult(); }
        await Until(() => fixture.Net == 3);
    }

    [Fact]
    public async Task Close_reconciles_a_pending_send_in_an_equivalent_session_before_cancellation_and_create()
    {
        var responseRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, AllowConcurrentTradingSessions = true };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var target = Assert.IsType<RithmicPositionTarget>(fixture.Position().Ref);
        await using var peer = await NativePeer.CreateAsync(fixture, server, target);
        server.NewOrderResponseRelease = responseRelease;
        var protection = OrderActions.Manual(peer.Broker, OrderTypes.Stop, OrderDirections.Sell, 1, 95, reduceOnly: true);
        string basket = await server.NewOrderResponseRequested.Task.WaitAsync(Deadline);
        Assert.DoesNotContain(fixture.Session.OrdersOf("ACC1"), order => order.BasketId == basket);
        var close = target.Broker.ClosePositionAsync(target.Confirmation);
        try
        {
            await fixture.Session.CancelAsync("ACC1", "missing-pending-send-barrier", CancellationToken.None)
                .ContinueWith(_ => { }).WaitAsync(Deadline);
            await Task.Delay(250);
            Assert.Single(Tags(server));
            Assert.False(close.IsCompleted);
            Assert.Equal("", protection.ExchangeOrderId);
        }
        finally { responseRelease.TrySetResult(); }
        string? closeResult = await close.WaitAsync(Deadline);
        Assert.True(closeResult == null, closeResult == null ? null : CloseDiagnostics(fixture, server, target.Confirmation, closeResult, peer));
        await Until(() => fixture.Net == 0, () => CloseDiagnostics(fixture, server, target.Confirmation, closeResult, peer));
        Assert.Equal(2, Tags(server).Length);
        Assert.True(peer.Session.OrdersOf("ACC1").Single(order => order.BasketId == basket).Cancelled);
        Assert.Equal(0, server.Kicks);
    }

    [Fact]
    public async Task Residual_protection_created_after_close_dispatch_keeps_accepted_price_maintenance()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var target = Assert.IsType<RithmicPositionTarget>(row.Ref);
        server.SeedPosition(true, 3, 100);
        await server.PublishPosition();
        await Until(() => fixture.Net == 5);
        Assert.Null(await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline));
        await Until(() => fixture.Net == 3);
        var ticket = Assert.Single(target.Confirmation.Context.PositionCloses());
        await Until(() => target.Confirmation.Context.PositionCloseState(ticket).Phase == PositionClosePhase.Unresolved);
        var protection = OrderActions.Manual(target.Broker, OrderTypes.Stop, OrderDirections.Sell, 1, 95, reduceOnly: true);
        await Until(() => protection.ExchangeOrderId.Length > 0);
        string basket = protection.ExchangeOrderId;
        Assert.Equal(0, Requests(server, "316"));
        OrderActions.Reprice(target.Broker, protection, 96);
        await Until(() => server.ModifyRequests == 1 || target.Broker.LastError?.Contains("superseded this amendment", StringComparison.Ordinal) == true);
        Assert.Equal(1, server.ModifyRequests);
        await Until(() => protection.TriggerPrice == 96 && !target.Broker.IsReplacementPending(protection));
        Assert.Equal(basket, protection.ExchangeOrderId);
        Assert.Equal(1m, protection.QuantityToFill);
        Assert.Equal(0, Requests(server, "316"));
        Assert.Equal(2, Tags(server).Length);
    }

    private static string CloseDiagnostics(AccountsCloseFixture fixture, FakeRithmicServer server,
        RithmicCloseConfirmation confirmation, string? closeResult, NativePeer? peer = null)
    {
        object JournalState(OrderContext context) => new
        {
            context.Descriptor, context.LastError,
            Closes = context.PositionCloses().Select(intent => new
            {
                intent.Id, intent.ClientOrderId, intent.Error,
                State = context.PositionCloseState(intent), Intent = context.Find(intent.Id),
            }).ToArray(),
        };
        object SessionState(RithmicTradingSession session)
        {
            lock (NativeField<object>(session, "_orderGate"))
                return new
                {
                    Scope = NativeField<string>(session, "_closeSessionNamespace"),
                    Environment = NativeField<string>(session, "_nativeObservationEnvironment"),
                    Stopping = NativeField<bool>(session, "_closeSessionStopping"),
                    Disposed = NativeField<bool>(session, "_closeSessionDisposed"),
                    session.IsConnected, session.StatusText, session.Accounts,
                    OrdersReady = session.OrdersReadyFor("ACC1"),
                    PositionReady = session.PositionReadyFor("ACC1", "ESZ6", "CME"),
                    Position = session.PositionFor("ACC1", "ESZ6", "CME")?.Net,
                    PendingRequests = NativeField<ConcurrentDictionary<string, TaskCompletionSource<TradingHub.Feeds.Rithmic.ProtoMessage>>>(session, "_pending").Count,
                    Orders = session.OrdersOf("ACC1").Select(order => new
                    {
                        order.BasketId, order.UserTag, order.Symbol, order.Exchange, order.IsBuy, order.PriceType,
                        order.Quantity, order.Filled, order.Unfilled, order.Done, order.Cancelled, order.Rejected, order.Reason,
                        order.CumulativeQuantityKnown, order.NativeTerminal, order.ReplayAbsent, order.LocalSubmission,
                        order.NativeObservationEnvironment, order.NativeAccountIdentity, order.FillRevision, order.PendingModifyQuantity,
                    }).ToArray(),
                    Fills = session.FillsOf("ACC1"),
                    RetainedOrders = ((System.Collections.IDictionary)NativeField<object>(session, "_retiredNativeOrders"))
                        .Values.Cast<object>().Select(order => order.ToString()).ToArray(),
                };
        }
        string KeyPart(object key, string property) => (string)key.GetType().GetProperty(property)!.GetValue(key)!;
        bool SameNativeAccount(object key) => KeyPart(key, "Environment") == confirmation.Environment &&
            KeyPart(key, "Account") == confirmation.State.NativeAccount;
        object StaticField(string name) => typeof(RithmicTradingSession)
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        IEnumerable<System.Collections.DictionaryEntry> Entries(string name)
        {
            foreach (System.Collections.DictionaryEntry entry in (System.Collections.IDictionary)StaticField(name))
                yield return entry;
        }
        lock (StaticField("NativeAdmissionGate"))
        {
            var nativeSessions = Entries("NativeSessions").Where(entry => SameNativeAccount(entry.Key))
                .SelectMany(entry => (IEnumerable<RithmicTradingSession>)entry.Value!).Select(SessionState).ToArray();
            var nativeCloses = Entries("NativeCloses").Where(entry => SameNativeAccount(entry.Key) &&
                    KeyPart(entry.Key, "Symbol") == confirmation.Symbol && KeyPart(entry.Key, "Exchange") == confirmation.Exchange)
                .Select(entry =>
                {
                    object operation = entry.Value!;
                    var type = operation.GetType();
                    var context = (OrderContext)type.GetProperty("Context")!.GetValue(operation)!;
                    var result = (TaskCompletionSource<string?>)type.GetProperty("Result")!.GetValue(operation)!;
                    return new
                    {
                        Contract = entry.Key.ToString(),
                        Session = SessionState((RithmicTradingSession)type.GetProperty("Session")!.GetValue(operation)!),
                        Fault = type.GetField("Fault")!.GetValue(operation),
                        Running = type.GetField("Running")!.GetValue(operation),
                        ResultStatus = result.Task.Status.ToString(), Journal = JournalState(context),
                    };
                }).ToArray();
            object serverState;
            lock (NativeField<object>(server, "_gate"))
                serverState = new
                {
                    server.Uri, server.Position, server.Kicks,
                    Open = server.Open.Select(entry => new
                    {
                        Basket = entry.Key, entry.Value.Buy, entry.Value.Qty, entry.Value.Type, entry.Value.Tag,
                    }).ToArray(),
                    ResponseRequested = server.NewOrderResponseRequested.Task.Status.ToString(),
                    ResponseRelease = server.NewOrderResponseRelease?.Task.Status.ToString(),
                };
            string[] received;
            lock (server.Received) received = server.Received.ToArray();
            return JsonSerializer.Serialize(new
            {
                CloseResult = closeResult, Captured = confirmation.State,
                Main = SessionState(fixture.Session), Journal = JournalState(confirmation.Context),
                Peer = peer == null ? null : new { Session = SessionState(peer.Session), Journal = JournalState(peer.Broker.OrderContext) },
                NativeSessions = nativeSessions, NativeCloses = nativeCloses,
                Server = serverState, Tags = Tags(server), Requests = received,
            });
        }
    }

    private static NativePositionClose NativeClose() => new("ACC1", JsonSerializer.Serialize(new[] { "FCM", "IB", "ACC1" }),
        "ESZ6", "CME", 2, Guid.Parse("00000000-0000-0000-0000-000000000001"), 10000,
        PositionClosePhase.Reconciling, Array.Empty<PositionCloseCancellation>(), ObservationScope: "claim-fixture");

    private static AccountContextDescriptor ClaimDescriptor() => new("native-close-claim", "Rithmic", "futures", "loopback-native-claim",
        "native-close-claim", "native-close-claim-generation");

    private static Process StartCloseClaim(string root, string processName)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LedgerCrashFixture", "TradingHub.LedgerCrashFixture.dll"));
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("native-close-claim");
        foreach (string store in new[] { "DATA", "STRATEGIES", "INDICATORS", "BACKTESTS", "LIVE" })
            start.Environment["TRADINGHUB_" + store + "_DIR"] = Path.Combine(root, processName, store.ToLowerInvariant());
        start.Environment["TRADINGHUB_KEYCHAIN_FILE"] = Path.Combine(root, processName, "fake-keychain.json");
        start.Environment["TRADINGHUB_SLEEP_GUARD"] = "off";
        return Process.Start(start)!;
    }

    private static T NativeField<T>(object value, string name) =>
        (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    private static bool IsHeld(object gate)
    {
        if (!Monitor.TryEnter(gate)) return true;
        Monitor.Exit(gate);
        return false;
    }

    private sealed class GateHold : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private bool _timedOut;

        public GateHold(object gate)
        {
            using var entered = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                lock (gate)
                {
                    entered.Set();
                    _timedOut = !_release.Wait(TimeSpan.FromSeconds(20));
                }
            });
            _thread.Start();
            Assert.True(entered.Wait(Deadline));
        }

        public void Dispose()
        {
            _release.Set();
            Assert.True(_thread.Join(Deadline));
            _release.Dispose();
            Assert.False(_timedOut);
        }
    }

    private sealed class NativePeer(RithmicTradingSession session, RithmicBroker broker) : IAsyncDisposable
    {
        public RithmicTradingSession Session { get; } = session;
        public RithmicBroker Broker { get; } = broker;

        public static async Task<NativePeer> CreateAsync(AccountsCloseFixture fixture, FakeRithmicServer server, RithmicPositionTarget target)
        {
            var session = new RithmicTradingSession(new RithmicSettings { Uri = server.Uri, System = "Rithmic 01",
                User = "demo", Password = "demo", TimeoutMs = 5000 });
            session.Start();
            try
            {
                await Until(() => session.OrdersReadyFor("ACC1") && session.PositionReadyFor("ACC1", "ESZ6", "CME"));
                var descriptor = target.Confirmation.Context.Descriptor with { Id = Guid.NewGuid().ToString("N"),
                    LocalNamespace = Guid.NewGuid().ToString("N"), Generation = Guid.NewGuid().ToString("N") };
                var context = new OrderContext(descriptor, fixture.ExecutionRoot);
                return new(session, new RithmicBroker(target.Broker.Instrument, session, "ACC1", "Equivalent native session", "ESZ6", "CME", context));
            }
            catch { await session.StopAsync().WaitAsync(Deadline); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await Session.StopAsync().WaitAsync(Deadline);
            Broker.Dispose();
        }
    }
}
