using System.Collections.Concurrent;
using System.Reflection;
using TradingHub.Data;
using TradingHub.App.Services;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Networking;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed partial class AccountsRithmicCloseRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Close_waits_for_complete_order_replay_before_any_new_order_reaches_the_wire()
    {
        var replayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new FakeRithmicServer { MarketPrice = 100, OrderReplayRelease = replayRelease };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("replayed-stop", "external-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync(ordersReady: false);
        var row = fixture.Position();
        Assert.Equal(2m, row.Net);
        Assert.True(fixture.Session.PositionReadyFor("ACC1", "ESZ6", "CME"));
        Assert.False(fixture.Session.OrdersReadyFor("ACC1"));
        Assert.True(fixture.Account.IsReal);
        Assert.False(fixture.Account.CanTrade);

        using var readerRelease = new ManualResetEventSlim();
        var readerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0, readerTimedOut = 0;
        void HoldReplayedOrderNotification()
        {
            if (fixture.Session.OrdersReadyFor("ACC1") ||
                !fixture.Session.OrdersOf("ACC1").Any(order => order.BasketId == "replayed-stop") ||
                Interlocked.CompareExchange(ref held, 1, 0) != 0) return;
            readerEntered.TrySetResult();
            if (!readerRelease.Wait(TimeSpan.FromSeconds(20))) Interlocked.Exchange(ref readerTimedOut, 1);
        }
        fixture.Session.Changed += HoldReplayedOrderNotification;
        var close = fixture.Center.CloseAsync(fixture.Account, row);
        // The fake cannot read 312 while its 320 handler is held. A later cancel proves that
        // earlier outbound frames have reached the fake while the client still has not consumed 321.
        var wireBarrier = Record.ExceptionAsync(() => fixture.Session.CancelAsync("ACC1", "missing-wire-barrier", CancellationToken.None));
        try
        {
            replayRelease.TrySetResult();
            await readerEntered.Task.WaitAsync(Deadline);
            await Until(() => Requests(server, "316") == 1);
            Assert.False(fixture.Session.OrdersReadyFor("ACC1"));
            Assert.Empty(Tags(server));
            Assert.Equal(0, Requests(server, "312"));
        }
        finally
        {
            readerRelease.Set();
            fixture.Session.Changed -= HoldReplayedOrderNotification;
            replayRelease.TrySetResult();
            await Task.WhenAll(close, wireBarrier).WaitAsync(Deadline);
        }

        Assert.Equal(0, Volatile.Read(ref readerTimedOut));
        Assert.NotNull(await wireBarrier);
        Assert.Null(await close);
        await Until(() => fixture.Session.OrdersReadyFor("ACC1") && fixture.Net == 0 &&
            fixture.Session.FillsOf("ACC1").Sum(fill => fill.Quantity) == 2m);
        Assert.True(fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "replayed-stop").Cancelled);
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(2, Requests(server, "316"));
    }

    [Fact]
    public async Task Overlapping_closes_share_one_reduction_until_its_position_update_arrives()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PositionUpdateRelease = positionRelease;
        var first = fixture.Center.CloseAsync(fixture.Account, row);
        Task<string?>? second = null;
        try
        {
            await Task.WhenAny(first, server.PositionUpdateRequested.Task).WaitAsync(Deadline);
            if (first.IsCompleted) Assert.Null(await first);
            await server.PositionUpdateRequested.Task.WaitAsync(Deadline);
            await Until(() => fixture.Session.OrdersOf("ACC1").Any(order => order.PriceType == 2 && order.Done));
            Assert.Equal(0, server.Position);
            Assert.Equal(2m, fixture.Net);
            second = fixture.Center.CloseAsync(fixture.Account, row);
        }
        finally
        {
            positionRelease.TrySetResult();
            await first.WaitAsync(Deadline);
            if (second != null) await second.WaitAsync(Deadline);
        }

        Assert.Null(await first);
        Assert.NotNull(second);
        string? secondResult = await second;
        await Until(() => fixture.Net == 0 || Tags(server).Length > 1,
            () => CloseDiagnostics(fixture, server, Assert.IsType<RithmicPositionTarget>(row.Ref).Confirmation, secondResult));
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(0, Requests(server, "316"));
    }

    [Fact]
    public async Task A_fill_while_cancel_is_pending_cannot_turn_the_close_into_a_reversal()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await server.PushOrder("partially-filled-stop", "external-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var stop = fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "partially-filled-stop");
        var modifyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var positionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ModifyResponseRelease = modifyRelease;
        server.PositionUpdateRelease = positionRelease;
        server.PositionSnapshotRelease = positionRelease;
        var modification = Record.ExceptionAsync(() => fixture.Session.ModifyAsync("ACC1", stop, 2, 96, CancellationToken.None));
        Task<string?>? close = null;
        Task<Exception?>? wireBarrier = null;
        Task? fill = null;
        try
        {
            await Until(() => server.ModifyRequests == 1);
            close = fixture.Center.CloseAsync(fixture.Account, row);
            fill = server.FillOrder(stop.BasketId, 1, 96);
            await server.PositionUpdateRequested.Task.WaitAsync(Deadline);
            await Until(() => fixture.Session.FillsOf("ACC1").Any(execution => execution.BasketId == stop.BasketId && execution.Quantity == 1));
            Assert.Equal(1, server.Position);
            Assert.Equal(2m, fixture.Net);
            modifyRelease.TrySetResult();
            await Until(() => stop.Done && stop.Cancelled);
            wireBarrier = Record.ExceptionAsync(() => fixture.Session.CancelAsync("ACC1", "missing-wire-barrier", CancellationToken.None));
            await close.WaitAsync(Deadline);
            await Until(() => Tags(server).Length > 0 || Requests(server, "316") >= 2);
            if (Tags(server) is [var sentTag])
            {
                var sent = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.UserTag == sentTag);
                Assert.False(sent.IsBuy);
                Assert.Equal(1m, sent.Quantity);
            }
            else Assert.Empty(Tags(server));
        }
        finally
        {
            modifyRelease.TrySetResult();
            positionRelease.TrySetResult();
            if (fill != null) await fill.WaitAsync(Deadline);
            await modification.WaitAsync(Deadline);
            if (wireBarrier != null) await wireBarrier.WaitAsync(Deadline);
            if (close != null) await close.WaitAsync(Deadline);
        }

        Assert.Null(await modification);
        Assert.NotNull(close);
        Assert.Null(await close);
        await Until(() => fixture.Net == 0 || Tags(server).Length > 1);
        Assert.Equal(1m, stop.Filled);
        Assert.True(stop.Cancelled);
        await AssertReductionAsync(fixture, server, 1);
        Assert.Equal(2m, fixture.Session.FillsOf("ACC1").Sum(execution => execution.Quantity));
        Assert.Equal(2, Requests(server, "316"));
    }

    [Fact]
    public async Task A_lost_close_reply_replays_and_fills_the_original_order_without_a_duplicate()
    {
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 2, 100);
        await using var fixture = new AccountsCloseFixture(server);
        await fixture.StartAsync();
        var row = fixture.Position();
        var replayRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OrderReplayRelease = replayRelease;
        server.LoseNewOrderResponse = true;
        var first = fixture.Center.CloseAsync(fixture.Account, row);
        Task<string?>? second = null;
        try
        {
            string basket = await server.UnacknowledgedOrderAccepted.Task.WaitAsync(Deadline);
            string tag = Assert.Single(Tags(server));
            server.LoseNewOrderResponse = false;
            await Until(() => fixture.Session.IsConnected && !fixture.Session.OrdersReadyFor("ACC1") && Requests(server, "320") >= 2);
            second = fixture.Center.CloseAsync(fixture.Account, row);
            replayRelease.TrySetResult();
            await Until(() => fixture.Session.OrdersReadyFor("ACC1") && fixture.Session.OrderByTag(tag)?.BasketId == basket);
            var accepted = Assert.Single(fixture.Session.OrdersOf("ACC1"), order => order.UserTag == tag);
            Assert.Equal(basket, accepted.BasketId);
            Assert.Equal(2m, accepted.Quantity);
            Assert.False(accepted.IsBuy);
            Assert.False(accepted.Done);
            Assert.Single(Tags(server));
            await server.FillOrder(basket, 2, 100).WaitAsync(Deadline);
        }
        finally
        {
            replayRelease.TrySetResult();
            await first.WaitAsync(Deadline);
            if (second != null) await second.WaitAsync(Deadline);
        }

        string? firstResult = await first;
        Assert.NotNull(firstResult);
        Assert.NotNull(second);
        string? secondResult = await second;
        Assert.NotNull(secondResult);
        await Until(() => fixture.Net == 0,
            () => CloseDiagnostics(fixture, server, Assert.IsType<RithmicPositionTarget>(row.Ref).Confirmation, secondResult));
        await AssertReductionAsync(fixture, server, 2);
        Assert.Equal(0, Requests(server, "316"));
    }

    [Fact]
    public async Task Close_after_profile_change_uses_the_retained_original_session()
    {
        using var original = new FakeRithmicServer { MarketPrice = 100 };
        using var replacement = new FakeRithmicServer { MarketPrice = 200 };
        original.SeedPosition(true, 2, 100);
        await original.PushOrder("original-stop", "original-external-stop", false, 2, 95, type: 4);
        await using var fixture = new AccountsCloseFixture(original);
        await fixture.StartAsync();
        var row = fixture.Position();
        fixture.RetireOriginalSession();
        fixture.Profile.RithmicUri = replacement.Uri;
        fixture.Profile.RithmicSystem = "Rithmic Test";
        fixture.Services.Connections.EnsureTradingIdentity(fixture.Profile);
        Assert.Same(fixture.Session, fixture.Services.RithmicSessionFor(fixture.Profile));
        Assert.Equal("Rithmic 01", fixture.Session.System);

        Assert.Null(await fixture.Center.CloseAsync(fixture.Account, row).WaitAsync(Deadline));
        await Until(() => fixture.Net == 0 && fixture.Session.FillsOf("ACC1").Sum(fill => fill.Quantity) == 2m);
        await AssertReductionAsync(fixture, original, 2);
        Assert.True(fixture.Session.OrdersOf("ACC1").Single(order => order.BasketId == "original-stop").Cancelled);
        Assert.Equal(1, Requests(original, "316"));
        Assert.Empty(Tags(replacement));
        Assert.Equal(0, Requests(replacement, "312"));
        Assert.Equal(0, Requests(replacement, "10"));
    }

    private static async Task AssertReductionAsync(AccountsCloseFixture fixture, FakeRithmicServer server, decimal quantity)
    {
        string tag = Assert.Single(Tags(server));
        await Until(() => fixture.Net == 0 && fixture.Session.OrdersOf("ACC1").Any(item => item.UserTag == tag && item.Done));
        Assert.Equal(1, Requests(server, "312"));
        var order = Assert.Single(fixture.Session.OrdersOf("ACC1"), item => item.UserTag == tag);
        Assert.Equal("ESZ6", order.Symbol);
        Assert.Equal("CME", order.Exchange);
        Assert.Equal(2, order.PriceType);
        Assert.False(order.IsBuy);
        Assert.Equal(quantity, order.Quantity);
        Assert.Equal(quantity, order.Filled);
        Assert.True(order.Done);
        Assert.False(order.Rejected);
        Assert.Equal(0, server.Position);
        Assert.Equal(0m, fixture.Net);
    }

    private static string[] Tags(FakeRithmicServer server)
    {
        lock (server.Tags) return server.Tags.ToArray();
    }

    private static int Requests(FakeRithmicServer server, string template)
    {
        lock (server.Received) return server.Received.Count(item => item == template);
    }

    private static async Task Until(Func<bool> condition, Func<string>? diagnostics = null)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        bool complete = condition();
        string? state = complete ? null : diagnostics?.Invoke();
        Assert.True(complete, "The Accounts close fixture did not publish the expected native order, fill, position, or replay state." +
            (state == null ? "" : Environment.NewLine + state));
    }

    private sealed class AccountsCloseFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-accounts-close-" + Guid.NewGuid().ToString("N"));
        private readonly FileSecretStore _secrets;
        private readonly RithmicFeed _feed;
        private readonly List<InstrumentData> _chartData = new();

        public AppServices Services { get; }
        public ConnectionProfile Profile { get; }
        public RithmicTradingSession Session { get; private set; }
        public AccountCenter Center => Services.AccountCenter;
        public AccountEntry Account { get; }
        public decimal Net => Session.PositionFor("ACC1", "ESZ6", "CME")?.Net ?? 0;
        public string ExecutionRoot => _root;
        public int PendingRequests => Field<ConcurrentDictionary<string, TaskCompletionSource<ProtoMessage>>>(Session, "_pending").Count;

        public ITradingAccount Chart(string symbol, string nativeSymbol = "ESZ6")
        {
            var data = new InstrumentData(new Instrument { Symbol = symbol, FeedId = RithmicFeed.FeedId, Exchange = "CME",
                TickSize = 0.25m, PointValue = 50, VolumeStep = 1 }, new SimulatedFeed());
            Field<ConcurrentDictionary<string, RithmicContract>>(_feed, "_contracts")[data.Instrument.Key] =
                new(nativeSymbol, "CME", null, DateTime.UtcNow);
            _chartData.Add(data);
            return Services.AccountFor(data, Account.Id);
        }

        public async Task<SemaphoreSlim> HoldSendSlotAsync()
        {
            var slot = Field<SemaphoreSlim>(Field<RithmicClient>(Session, "_orderClient"), "_send");
            await slot.WaitAsync().WaitAsync(Deadline);
            return slot;
        }

        public async Task RestartAsync(Func<Task>? whileStopped = null)
        {
            foreach (var broker in Field<Dictionary<string, ITradingAccount>>(Services, "_realBrokers").Values)
                (broker as IDisposable)?.Dispose();
            Field<Dictionary<string, ITradingAccount>>(Services, "_realBrokers").Clear();
            Field<Dictionary<string, OrderContext>>(Services, "_orderContexts").Clear();
            Field<ConcurrentDictionary<(string Context, string Instrument), WeakReference<ITradingAccount>>>(Services, "_wireAccounts").Clear();
            await Session.StopAsync().WaitAsync(Deadline);
            if (whileStopped != null) await whileStopped().WaitAsync(Deadline);
            Session = new RithmicTradingSession(new RithmicSettings { Uri = Profile.RithmicUri, System = Profile.RithmicSystem,
                User = "demo", Password = "demo", TimeoutMs = 5000 });
            Field<Dictionary<string, RithmicTradingSession>>(Services, "_rithmicSessions")[Profile.Id] = Session;
            await StartAsync();
        }

        public AccountsCloseFixture(FakeRithmicServer server)
        {
            Services = SearchZoomFixture.Services();
            Profile = new ConnectionProfile
            {
                Id = "accounts-close-" + Guid.NewGuid().ToString("N"), Kind = ConnectionKind.Rithmic,
                Name = "Accounts close REAL fixture", Enabled = true, AllowRealOrders = false, MaxOrderQuantity = 1,
                TradingNamespace = Guid.NewGuid().ToString("N"), TradingGeneration = Guid.NewGuid().ToString("N"),
                RithmicUri = server.Uri, RithmicSystem = "Rithmic 01", RithmicUser = "demo",
            };
            Services.Connections.Profiles.Add(Profile);
            InitializeField(Services.Connections, "_tradingRoutes");
            InitializeField(Services.Connections, "_retiredFeeds");
            InitializeField(Services.Connections, "_ibScopes");
            SearchZoomFixture.Set(Services.Connections, "_filePath", Path.Combine(_root, "connections.json"));
            Directory.CreateDirectory(_root);
            _secrets = new FileSecretStore(Path.Combine(_root, "fixture-secrets.json"));
            _secrets.Set(Profile.SecretAccount, "demo");
            SearchZoomFixture.Set(Services.Connections, "_secretStore", _secrets);
            Func<ProxyHandlerFactory> network = () => new(new NetworkProxyPolicy());
            SearchZoomFixture.Set(Services.Connections, "_networkFactory", network);
            Services.Connections.EnsureTradingIdentity(Profile);
            foreach (string field in new[]
            {
                "_shutdownGate",
                "_simBrokers", "_realBrokers", "_orderContexts", "_wireAccounts", "_brokerOrigins", "_replacedBrokers",
                "_rithmicSessions", "_rithmicOrigins", "_retiredRithmicSessions", "_tradovateSessions", "_retiredTradovateSessions",
                "_bybitGate", "_bybitSessions", "_retiredBybitSessions", "_bybitProfiles", "_bybitRetirementTasks", "_bybitRecoveryIssues",
            }) InitializeField(Services, field);
            SearchZoomFixture.Set(Services, "_executionRoot", _root);
            SearchZoomFixture.Set(Services, "<Journal>k__BackingField", new AccountJournal(Path.Combine(_root, "Accounts")));
            SearchZoomFixture.Set(Services, "<Runners>k__BackingField", new List<LiveStrategyRunner>());
            SearchZoomFixture.Set(Services, "_networkProxy", new AppNetworkProxy(Services.Settings, _secrets));
            var settings = new RithmicSettings
            {
                Uri = server.Uri, System = Profile.RithmicSystem, User = "demo", Password = "demo", TimeoutMs = 5000,
            };
            Session = new RithmicTradingSession(settings);
            Field<Dictionary<string, RithmicTradingSession>>(Services, "_rithmicSessions").Add(Profile.Id, Session);
            _feed = new RithmicFeed(settings);
            var contracts = Field<ConcurrentDictionary<string, RithmicContract>>(_feed, "_contracts");
            foreach (string symbol in new[] { "ES", "ESZ6" })
                contracts[new Instrument { Symbol = symbol, FeedId = RithmicFeed.FeedId, TickSize = 0.25m }.Key] = new("ESZ6", "CME", null, DateTime.UtcNow);
            Field<Dictionary<string, IMarketDataFeed>>(Services.Connections, "_feeds").Add(Profile.Id, _feed);
            Account = new(AppServices.RithmicAccountId(Profile, "ACC1"), "ACC1 REAL fixture", AccountKind.Rithmic,
                true, false, Profile, "ACC1");
        }

        public async Task StartAsync(bool ordersReady = true)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe()
            {
                if (Session.PositionReadyFor("ACC1", "ESZ6", "CME") && Session.SummaryFor("ACC1")?.Balance != null &&
                    (!ordersReady || Session.OrdersReadyFor("ACC1"))) ready.TrySetResult();
            }
            Session.Changed += Observe;
            try
            {
                Session.Start();
                Observe();
                await ready.Task.WaitAsync(Deadline);
            }
            finally { Session.Changed -= Observe; }
        }

        public PositionLine Position() => Assert.Single(Center.Snapshot(Account).Positions, row => row.Symbol == "ESZ6" && row.Net != 0);

        public void RetireOriginalSession()
        {
            var originType = typeof(AppServices).GetNestedType("RithmicContextOrigin", BindingFlags.NonPublic)!;
            var origin = Activator.CreateInstance(originType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: new object[] { Profile.Id, Profile.TradingNamespace, Profile.TradingGeneration,
                    new UriBuilder(Profile.RithmicUri).Uri.GetLeftPart(UriPartial.Path) + "|" + Profile.RithmicSystem }, culture: null)!;
            object origins = Field<object>(Services, "_rithmicOrigins");
            origins.GetType().GetMethod("AddOrUpdate")!.Invoke(origins, new[] { (object)Session, origin });
            Field<Dictionary<string, RithmicTradingSession>>(Services, "_rithmicSessions").Remove(Profile.Id);
            Field<List<RithmicTradingSession>>(Services, "_retiredRithmicSessions").Add(Session);
        }

        public async ValueTask DisposeAsync()
        {
            var brokers = Field<Dictionary<string, ITradingAccount>>(Services, "_realBrokers").Values
                .Concat(Field<List<ITradingAccount>>(Services, "_replacedBrokers"))
                .Concat(Field<ConcurrentDictionary<(string Context, string Instrument), WeakReference<ITradingAccount>>>(Services, "_wireAccounts")
                    .Values.Select(weak => weak.TryGetTarget(out var account) ? account : null).OfType<ITradingAccount>())
                .Distinct().ToArray();
            foreach (var broker in brokers) (broker as IDisposable)?.Dispose();
            foreach (var data in _chartData) data.Dispose();
            await Session.StopAsync().WaitAsync(Deadline);
            _feed.Dispose();
            await _secrets.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static T Field<T>(object target, string name) =>
            (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

        private static void InitializeField(object target, string name)
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            SearchZoomFixture.Set(target, name, Activator.CreateInstance(field.FieldType)!);
        }
    }
}
