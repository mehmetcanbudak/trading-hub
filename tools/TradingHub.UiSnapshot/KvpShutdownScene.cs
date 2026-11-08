using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.Longbridge;
using TradingHub.Market;
using TradingHub.Scripting;

// Unlike SafetyScene's reusable finite protection-state-machine fixture, this scene deliberately
// uses App.UseQuit's real, nonoptional ApplicationQuitTerminal and actually disposes App.Services.
internal static class KvpShutdownScene
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static TradingHub.UiSnapshot.KvpShutdownLongbridgeFixture? _longbridge;
    public static AppServices CreateServices()
    {
        if (_longbridge != null) throw new InvalidOperationException("Shutdown fixture application must be constructed only once.");
        _longbridge = new();
        return _longbridge.CreateServices();
    }

    public static int Run(MainWindow owner, Action<double> pump, string outDir, bool platformRequest = false)
    {
        outDir = Path.GetFullPath(outDir);
        string runtimeRoot = Path.Combine(AppPaths.Root, "kvp-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeRoot);
        int checks = 0, failures = 0;
        var evidence = new List<object>();
        var ordering = new List<string>();
        void Check(bool condition, string description)
        {
            checks++;
            if (!condition) failures++;
            evidence.Add(new { condition, description });
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} KVP shutdown: {description}");
        }
        void Until(Func<bool> condition, string description)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(45);
            while (!condition() && DateTime.UtcNow < until) pump(0.02);
            Check(condition(), description);
            if (!condition()) throw new TimeoutException(description);
        }
        void Await(Task task, string description)
        {
            Until(() => task.IsCompleted, description);
            task.GetAwaiter().GetResult();
        }
        void ObserveCancellation(Task task, string description)
        {
            Until(() => task.IsCompleted, description);
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static Button Action(Window window, string text) => window.GetVisualDescendants().OfType<Button>()
            .Single(button => EnglishScene.Labels(button).Contains(Lang.T(text)));

        var services = App.Services;
        var longbridge = _longbridge ?? throw new InvalidOperationException("The real application must use the shutdown fixture constructor dependencies.");
        var application = Application.Current!;
        var lifetimeField = typeof(Application).GetField("_applicationLifetime", Private)!;
        object? previousLifetime = lifetimeField.GetValue(application);
        QuitFlow? previousQuit = App.Quit;
        var desktop = KvpTerminalDesktopLifetime.For(owner);
        using var providerEntered = new ManualResetEventSlim();
        var providerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var compilerEntered = new ManualResetEventSlim();
        var compilerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new HeldPineBridge();
        using var exchange = new KvpProvidersExchange();
        const string key = "isolated-shutdown-key", secret = "isolated-shutdown-secret";
        exchange.Add(key, secret, 900099);
        var probe = new DisposalProbe(exchange.CreateHandler(async (_, _) =>
        {
            providerEntered.Set();
            await providerRelease.Task.ConfigureAwait(false); // Deliberately late transport completion, never a real provider.
        }));
        var settings = new BybitAccountSettings
        {
            Environment = BybitEnvironment.Demo, ApiKey = key, ApiSecret = secret,
            BaseUrl = exchange.Server.Http, RequestTimeout = TimeSpan.FromMinutes(2),
        };
        var profile = new ConnectionProfile { Id = "owned-shutdown-" + Guid.NewGuid().ToString("N"), Kind = ConnectionKind.Bybit, Name = "Owned shutdown context", BybitEnvironment = BybitEnvironment.Demo };
        var session = new BybitTradingSession(settings, Path.Combine(runtimeRoot, "owned-shutdown-state.json"),
            new HttpClient(probe) { Timeout = TimeSpan.FromMinutes(2) }, ownsHttpClient: true);
        session.Retire();
        services.Connections.Profiles.Add(profile);
        services.Connections.PreserveBybitContext(profile, settings);
        string credentialKey = profile.SecretAccountFor("bybit-context-" + settings.ContextIdentity);
        string savedCredentials = Keychain.Get(credentialKey);
        Field<Dictionary<BybitTradingSession, ConnectionProfile>>(services, "_bybitProfiles").Add(session, profile);
        Field<List<BybitTradingSession>>(services, "_retiredBybitSessions").Add(session);
        Task providerRefresh = session.RefreshAsync();
        Task? activeReview = null, pendingReview = null, activeCompile = null, quitTask = null;
        Task? pendingChartLaunch = null;
        Task<(TradingHub.Strategies.LiveStrategyRunner? Runner, string Message)>? pendingPanelLaunch = null;
        UserScripts? monitor = null;
        HeldHistoryFeed? historyFeed = null;
        SemaphoreSlim? privateStoreSerial = null;
        bool privateStoreSerialHeld = false;
        Task<string>? privateRead = null;
        Task? privateWrite = null;
        var privateAdmissionProbes = new List<Task<string>>();
        int compilerCalls = 0, compilePublications = 0, pendingFactories = 0, replacements = 0;
        string strategyPath = Path.Combine(AppPaths.Strategies, "KvpShutdownLaunch.cs");
        var keptData = owner.Trading.ActiveTab?.Model?.Data;
        try
        {
            longbridge.Begin(services);
            Until(() => longbridge.Transport.Entered.Task.IsCompleted && longbridge.BrowserEntered.Task.IsCompleted,
                "actual cached Longbridge feed request and owned PKCE loopback/browser flow are both held without real OAuth");
            Check(longbridge.Http.Requests == 0, "known-client authorization preview and injected static-data transport send no real OAuth HTTP");
            Until(() => providerEntered.IsSet, "actual owned retired provider is held inside an in-flight HTTP refresh");
            if (services.Scripts != null) Await(services.Scripts.DisposeAsync().AsTask(), "initial ordinary monitor drains before fixture replacement");
            string monitorRoot = Path.Combine(runtimeRoot, "owned-monitor");
            Directory.CreateDirectory(monitorRoot);
            File.WriteAllText(Path.Combine(monitorRoot, "HeldIndicator.cs"), "using TradingHub.Indicators; public class KvpHeldShutdownIndicator : Indicator { public KvpHeldShutdownIndicator() : base(true) {} protected override void OnCalculate(int bar, decimal value) { ((ValueDataSeries)DataSeries[0])[bar] = 7m; } }");
            monitor = new UserScripts(services.Catalog, monitorRoot, dispatch: action => { _ = Task.Run(action); },
                compile: (sources, _) =>
                {
                    Interlocked.Increment(ref compilerCalls);
                    compilerEntered.Set();
                    compilerRelease.Task.GetAwaiter().GetResult();
                    // The real compiler completes after cancellation; ownership, not a fake result, must prevent publication.
                    return new ScriptCompiler().CompileSources(sources, "KvpShutdownHeldSource", CancellationToken.None);
                });
            services.Scripts = monitor;
            monitor.Compiled += _ => Interlocked.Increment(ref compilePublications);
            activeCompile = Task.Run(() => monitor.CompileAll());
            Until(() => compilerEntered.IsSet, "actual captured-source compilation is active before shutdown");

            string launchId = "pending-history-" + Guid.NewGuid().ToString("N");
            var launchInstrument = new Instrument { FeedId = launchId, Symbol = "PENDING", TickSize = 1, VolumeStep = 1, PointValue = 1 };
            historyFeed = new HeldHistoryFeed(launchId, launchInstrument);
            var launchProfile = new ConnectionProfile { Id = launchId, Kind = ConnectionKind.Simulator, Name = "Pending history fixture", HistoryHours = 1 };
            KvpSceneFeed.Register(services, launchProfile, historyFeed);
            var opening = owner.Trading.NewTabAsync(launchId, launchInstrument.Symbol, ChartPeriod.M1);
            Await(opening, "actual chart is created while its history request remains pending");
            var chart = opening.Result;
            Until(() => historyFeed.Entered.Task.IsCompleted && chart.Model!.Data.IsLoadingHistory, "actual chart history has a controlled asynchronous boundary");
            File.WriteAllText(strategyPath, "using TradingHub.Strategies; [Strategy(\"Owned shutdown launch\")] public class KvpShutdownLaunch : Strategy { }");
            services.Strategies.Refresh();
            var file = services.Strategies.Files.Single(item => item.FileName == "KvpShutdownLaunch");
            var generation = services.Strategies.Compile(file).Generation;
            Check(generation != null, "real strategy compiler provides pending chart/panel launch consumer");
            var cs = new ChartStrategy { StrategyId = file.Id, AccountId = "sim", Live = true, Generation = generation };
            chart.Strategies.Add(cs);
            chart.Model!.Strategies.Add(cs.Overlay);
            pendingChartLaunch = owner.Trading.RunStrategyAsync(chart, cs, generation: generation);
            pendingPanelLaunch = services.Live.LaunchAsync(new LiveLaunch { File = file, Generation = generation, ProfileId = launchId, Instrument = launchInstrument, AccountId = "sim", Period = ChartPeriod.M1 });
            Check(!pendingChartLaunch.IsCompleted && !pendingPanelLaunch.IsCompleted, "both actual launch paths wait before any live runner publication");

            byte[] source = Encoding.UTF8.GetBytes("//@version=5\nindicator(\"Owned shutdown fixture\")\nplot(close)\n");
            using var input = new MemoryStream(source);
            var preserved = PineImports.PreserveAsync(input, "shutdown.pine", AppPaths.UserIndicators);
            Await(preserved, "Pine original is preserved before any egress approval");
            var preview = PineImports.Preview(preserved.Result, "codex", "fixture-model", "high");
            activeReview = PineReviewWindow.ShowOwnedAsync(services, owner, preview, () => bridge);
            pump(0.1);
            var activeWindow = owner.OwnedWindows.OfType<PineReviewWindow>().Single();
            Click(Action(activeWindow, "Send source to Codex")); pump(0.1);
            var approved = activeWindow.OwnedWindows.OfType<ConfirmDialog>().Single();
            Click(Action(approved, "Send reviewed source"));
            Until(() => bridge.Entered.Task.IsCompleted, "actual owned Pine preview and explicit confirmation start the held fake CLI bridge");
            pendingReview = PineReviewWindow.ShowOwnedAsync(services, owner, preview, () => { pendingFactories++; return new HeldPineBridge(); });
            pump(0.1);
            var pendingWindow = owner.OwnedWindows.OfType<PineReviewWindow>().Single(window => !ReferenceEquals(window, activeWindow));
            Click(Action(pendingWindow, "Send source to Codex")); pump(0.1);
            Check(pendingWindow.OwnedWindows.OfType<ConfirmDialog>().Any() && pendingFactories == 0,
                "a second owned request is awaiting safe consent and has not created a CLI");
            using (var frame = owner.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "kvp_shutdown_owned_pending.png"));

            // Block the actual default isolated file store at its serialized I/O boundary, not a
            // replacement drain callback. Public async operations are admitted and remain owned.
            var privateStore = (IAsyncSecretStore)Keychain.Store;
            privateStoreSerial = Field<SemaphoreSlim>(privateStore, "_serial");
            Until(() => privateStoreSerialHeld || (privateStoreSerialHeld = privateStoreSerial.Wait(0)), "controlled isolated private-store I/O boundary is acquired");
            privateRead = privateStore.GetAsync("terminal-owned-read");
            privateWrite = privateStore.SetAsync("terminal-owned-write", "fixture-pending-only");
            Check(!privateRead.IsCompleted && !privateWrite.IsCompleted, "actual default private-store read/write requests are pending before terminal shutdown");
            void CheckPrivateAdmissionClosed(string phase)
            {
                Task<string> request;
                try { request = privateStore.GetAsync("terminal-rejected-read"); }
                catch (ObjectDisposedException) { Check(true, "private-store admission is already closed at " + phase); return; }
                privateAdmissionProbes.Add(request);
                bool rejected = false;
                if (request.IsCompleted)
                {
                    try { request.GetAwaiter().GetResult(); }
                    catch (ObjectDisposedException) { rejected = true; }
                    catch (OperationCanceledException) { rejected = true; }
                }
                Check(rejected, "private-store admission is already closed at " + phase);
            }

            lifetimeField.SetValue(application, desktop.Lifetime);
            var quit = App.UseQuit(desktop.Lifetime);
            void Replacement()
            {
                Check(probe.Disposed && activeCompile.IsCompleted && activeReview.IsCompleted && pendingReview.IsCompleted,
                    "replacement callback runs only after every owned provider/monitor/Pine drain");
                CheckPrivateAdmissionClosed("replacement handoff");
                replacements++;
                ordering.Add("replacement");
            }
            desktop.Exiting = () =>
            {
                CheckPrivateAdmissionClosed("desktop exit");
                ordering.Add("desktop-exit");
            };
            if (platformRequest)
            {
                Check(!desktop.RequestShutdown(), "first no-holdings platform request is canceled rather than bypassing asynchronous drain");
                Until(() => services.IsShuttingDown, "posted platform request enters the real terminal lifetime");
            }
            else quitTask = quit.QuitAsync(Replacement);
            Until(() => services.IsShuttingDown && services.PineHandoffs.IsStopping, "real root shutdown closes admission while dispatcher remains active");
            Task sharedShutdown = services.ShutdownAsync();
            Check(ReferenceEquals(sharedShutdown, services.ShutdownAsync()), "concurrent root shutdown callers share one owned completion");
            var concurrentQuit = quit.QuitAsync(Replacement);
            Await(concurrentQuit, "concurrent quit returns without a duplicate terminal operation");
            Check(!desktop.RequestShutdown() && desktop.Shutdowns == 0 && replacements == 0 && !sharedShutdown.IsCompleted,
                "repeat platform/menu quit cannot exit or launch a replacement while operations are held");
            Check(bridge.CancellationObserved && bridge.IsRunning && bridge.Last != null && Directory.Exists(bridge.Last.WorkDirectory),
                "active Pine cancellation retains its running child boundary and owned workspace until drain");
            // The fixture owns the store's I/O lock; do not synchronously re-enter it to inspect retention.
            var heldCredentialsSnapshot = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE")!))!;
            Check(!probe.Disposed && savedCredentials.Length > 0 && heldCredentialsSnapshot.GetValueOrDefault(credentialKey) == savedCredentials,
                "held provider transport and captured credentials remain owned, not disposed/forgotten early");
            ObserveCancellation(pendingReview, "unanswered consent is canceled and owned review closes without deadlock");
            Check(pendingFactories == 0, "shutdown of pending consent makes zero CLI/model calls");
            bool registrationRejected = false;
            try { services.PineHandoffs.RunAsync(_ => Task.CompletedTask); }
            catch (ObjectDisposedException) { registrationRejected = true; }
            Check(registrationRejected, "new authoring registration is rejected after shutdown admission closes");
            bool providerRejected = false, reactivationRejected = false;
            try { services.BybitSessionFor(new ConnectionProfile { Id = "new-after-stop", Kind = ConnectionKind.Bybit }); }
            catch (InvalidOperationException) { providerRejected = true; }
            try { services.BybitSessionFor(profile); }
            catch (InvalidOperationException) { reactivationRejected = true; }
            Check(providerRejected && reactivationRejected, "new provider creation and captured-context reactivation are both rejected");
            bool longbridgeFeedRejected = false, longbridgeSessionRejected = false, longbridgeAuthRejected = false;
            try { services.Connections.GetFeed(longbridge.Profile.Id); } catch (ObjectDisposedException) { longbridgeFeedRejected = true; }
            try { services.Connections.LongbridgeSessionFor(longbridge.Profile); } catch (ObjectDisposedException) { longbridgeSessionRejected = true; }
            try { _ = LongbridgeAuthorizationFlow.AuthorizeAsync(services.Connections, longbridge.Profile, (_, _) => Task.CompletedTask); }
            catch (ObjectDisposedException) { longbridgeAuthRejected = true; }
            Check(longbridgeFeedRejected && longbridgeSessionRejected && longbridgeAuthRejected,
                "Longbridge cached-feed/session and new authorization admission all reject after terminal closure");
            int beforeCompile = Volatile.Read(ref compilerCalls);
            monitor.CompileAll();
            Check(Volatile.Read(ref compilerCalls) == beforeCompile, "new monitor compilation is rejected without waiting for the held compiler");
            ObserveCancellation(pendingChartLaunch, "pending chart launch aborts while shutdown history is still unavailable");
            Await(pendingPanelLaunch, "pending panel launch aborts while shutdown history is still unavailable");
            Check(cs.Runner == null && pendingPanelLaunch.Result.Runner == null && services.Runners.All(runner => !runner.IsRunning),
                "late history/compile continuations cannot publish a new runner after protected stop");

            compilerRelease.TrySetResult();
            Await(activeCompile, "held real compiler completes after cancellation");
            Check(compilePublications == 0 && services.Catalog.Items.All(item => item.Type.Name != "KvpHeldShutdownIndicator"),
                "late real compiler result cannot publish a callback or replace the catalog");
            Check(desktop.Shutdowns == 0 && replacements == 0 && !sharedShutdown.IsCompleted,
                "monitor drain alone cannot release the held Pine/provider terminal lifetime");
            bridge.Release.TrySetResult();
            ObserveCancellation(activeReview, "held Pine bridge, streams/workspace and actual review complete before exit");
            Check(bridge.Last != null && !Directory.Exists(bridge.Last.WorkDirectory) && !bridge.IsRunning,
                "Pine workspace is removed only after owned bridge completion");
            historyFeed.Release.TrySetResult();
            pump(0.2);
            Check(!probe.Disposed && !sharedShutdown.IsCompleted && desktop.Shutdowns == 0 && replacements == 0,
                "provider refresh still holds root drain after monitor/Pine/history producers finish");
            Check(keptData == null || services.Hub.Items.Contains(keptData), "shared market data remains alive until held provider operations drain");
            owner.Close(); pump(0.1);
            Check(owner.IsVisible && quit.IsQuitting && !quit.IsTerminalComplete && desktop.Shutdowns == 0 && !sharedShutdown.IsCompleted,
                "a second actual main-window red-close is canceled while the accepted terminal still awaits provider drain");
            Check(!privateRead.IsCompleted && !privateWrite.IsCompleted,
                "global private store stays available for provider/alert final persistence until those owners drain");
            providerRelease.TrySetResult();
            ObserveCancellation(providerRefresh, "actual canceled provider refresh releases its owned refresh gate");
            pump(.1);
            Check(!sharedShutdown.IsCompleted && desktop.Shutdowns == 0 && replacements == 0 && !longbridge.Http.Disposed,
                "actual Longbridge feed/browser ownership independently prevents shared/private disposal and replacement after Bybit drains");
            longbridge.Transport.Release.TrySetResult();
            ObserveCancellation(longbridge.InstrumentRequest, "late actual Longbridge static-data request settles under cancellation");
            Check(!longbridge.Authorization.IsCompleted && !sharedShutdown.IsCompleted && desktop.Shutdowns == 0 && !longbridge.Http.Disposed,
                "held owned browser callback/loopback lifetime still prevents exit after Longbridge data request drains");
            longbridge.BrowserRelease.TrySetResult();
            ObserveCancellation(longbridge.Authorization, "owned Longbridge authorization callback and loopback listener drain before exit");
            Await(sharedShutdown, "real composed AppServices shutdown drains all resources");
            if (quitTask != null) Await(quitTask, "restart-style quit observes terminal completion");
            Until(() => desktop.Shutdowns == 1, "desktop shutdown occurs exactly once after asynchronous ownership drains");
            ObserveCancellation(privateRead, "real default private-store pending read is canceled/drained before completed terminal return");
            ObserveCancellation(privateWrite, "real default private-store pending write is canceled/drained before completed terminal return");
            Check(probe.Disposed && replacements == (platformRequest ? 0 : 1), "owned transport is disposed and only the accepted replacement callback runs");
            Check(longbridge.Transport.Disposed && longbridge.Http.Disposed && longbridge.Http.Requests == 0,
                "actual default root awaits Longbridge request/authorization owners before disposing their factory resources");
            Check(platformRequest ? ordering.SequenceEqual(["desktop-exit"]) : ordering.SequenceEqual(["replacement", "desktop-exit"]),
                "terminal ordering is drain, accepted replacement handoff, then desktop exit");
            var persistedSecrets = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE")!))!;
            Check(persistedSecrets.GetValueOrDefault(credentialKey) == savedCredentials,
                "unreconciled retired provider credentials remain available for durable recovery after drain");
            Check(!persistedSecrets.ContainsKey("terminal-owned-write"), "canceled queued private write never publishes after shutdown");
            desktop.RaiseExit();
            Check(services.ShutdownAsync().IsCompleted && desktop.Shutdowns == 1 && desktop.ExitEvents == 2,
                "native Exit fallback completes idempotently without a second drain/desktop exit");
        }
        finally
        {
            compilerRelease.TrySetResult();
            bridge.Release.TrySetResult();
            providerRelease.TrySetResult();
            historyFeed?.Release.TrySetResult();
            longbridge.ReleaseAll();
            if (privateStoreSerialHeld)
            {
                privateStoreSerial!.Release();
                privateStoreSerialHeld = false;
            }
            var cleanup = services.ShutdownAsync();
            DateTime until = DateTime.UtcNow.AddSeconds(45);
            while (!cleanup.IsCompleted && DateTime.UtcNow < until) pump(0.02);
            if (!cleanup.IsCompleted) Check(false, "owned shutdown cleanup completes");
            else cleanup.GetAwaiter().GetResult();
            var privateCleanup = Keychain.ShutdownAsync();
            while (!privateCleanup.IsCompleted && DateTime.UtcNow < until) pump(0.02);
            if (!privateCleanup.IsCompleted) Check(false, "global private-store cleanup completes");
            else privateCleanup.GetAwaiter().GetResult();
            foreach (var request in privateAdmissionProbes) if (request.IsFaulted) _ = request.Exception;
            File.Delete(strategyPath);
            lifetimeField.SetValue(application, previousLifetime);
            typeof(App).GetProperty("Quit", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, previousQuit);
            File.WriteAllText(Path.Combine(outDir, platformRequest ? "kvp-shutdown-platform-results.json" : "kvp-shutdown-results.json"),
                JsonSerializer.Serialize(new { checks, failures, platformRequest, ordering, evidence }));
        }
        return failures;
    }

    private sealed class DisposalProbe(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
    private sealed class HeldPineBridge : IStrategyChatBridge
    {
        public string Engine => "codex";
        public string? SessionId => null;
        public bool IsRunning { get; private set; }
        public StrategyChatRequest? Last { get; private set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<string>? Text;
        public event Action<StrategyChatResult>? Finished;
        public event Action<string, string?>? ToolUsed { add { } remove { } }
        public event Action? ToolFinished { add { } remove { } }
        public void NewConversation() { }
        public void Resume(string? sessionId) { if (sessionId != null) throw new InvalidOperationException("Fixture has no resumable session"); }
        public void Cancel() => CancellationObserved = true;
        public async Task<StrategyChatResult> SendAsync(StrategyChatRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            IsRunning = true;
            using var registration = cancellationToken.Register(() => CancellationObserved = true);
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            IsRunning = false;
            var result = new StrategyChatResult(StrategyChatStatus.Cancelled, "", null, 0, false);
            Text?.Invoke("");
            Finished?.Invoke(result);
            return result;
        }
    }
    private sealed class HeldHistoryFeed(string id, Instrument instrument) : IMarketDataFeed
    {
        private readonly KvpSceneFeed _inner = new(id, instrument) { Now = DateTime.UtcNow };
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => id;
        public string DisplayName => "Owned pending history fixture";
        public bool SupportsHistory => true;
        public bool IsLive => false;
        public DateTime Now => _inner.Now;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => _inner.GetInstrumentsAsync(ct);
        public IDisposable Subscribe(Instrument item, IMarketDataSink sink) => _inner.Subscribe(item, sink);
        public async Task LoadTradesAsync(Instrument item, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            onBatch([new Trade(1, Now.AddMinutes(-1), 100, 1, TradeSide.Buy)]);
            progress?.Report(1);
        }
        public void Dispose() { Release.TrySetResult(); _inner.Dispose(); }
    }
}

// The only substituted boundary is the OS desktop lifetime. Exit invokes the actual production
// fallback subscription; no drain callback is optional and AppServices is not reused after this scene.
public class KvpTerminalDesktopLifetime : DispatchProxy
{
    private EventHandler<ShutdownRequestedEventArgs>? _shutdownRequested;
    private Delegate? _exit;
    private Window? _main;
    public IClassicDesktopStyleApplicationLifetime Lifetime { get; private set; } = null!;
    public int Shutdowns { get; private set; }
    public int ExitEvents { get; private set; }
    public Action? Exiting { get; set; }
    public static KvpTerminalDesktopLifetime For(Window main)
    {
        var lifetime = Create<IClassicDesktopStyleApplicationLifetime, KvpTerminalDesktopLifetime>();
        var fixture = (KvpTerminalDesktopLifetime)(object)lifetime;
        fixture.Lifetime = lifetime;
        fixture._main = main;
        return fixture;
    }
    public bool RequestShutdown()
    {
        var e = new ShutdownRequestedEventArgs();
        _shutdownRequested?.Invoke(Lifetime, e);
        return !e.Cancel;
    }
    public void RaiseExit() { ExitEvents++; _exit?.DynamicInvoke(Lifetime, null); }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "Shutdown": Exiting?.Invoke(); Shutdowns++; RaiseExit(); return null;
            case "TryShutdown": return RequestShutdown();
            case "add_ShutdownRequested": _shutdownRequested += (EventHandler<ShutdownRequestedEventArgs>?)args![0]; return null;
            case "remove_ShutdownRequested": _shutdownRequested -= (EventHandler<ShutdownRequestedEventArgs>?)args![0]; return null;
            case "add_Exit": _exit = Delegate.Combine(_exit, (Delegate?)args![0]); return null;
            case "remove_Exit": _exit = Delegate.Remove(_exit, (Delegate?)args![0]); return null;
            case "get_MainWindow": return _main;
            case "set_MainWindow": _main = (Window?)args![0]; return null;
            case "get_Windows": return _main is { } main ? new[] { main } : Array.Empty<Window>();
            case "get_ShutdownMode": return ShutdownMode.OnExplicitShutdown;
            case "get_Args": return Array.Empty<string>();
            default: return null;
        }
    }
}
