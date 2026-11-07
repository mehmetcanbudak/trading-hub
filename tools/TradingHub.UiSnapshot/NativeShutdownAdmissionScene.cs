using System.Reflection;
using System.Text.Json;
using Avalonia.Threading;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Feeds.Rithmic;
using TradingHub.Tests;
using TradingHub.Trading;

internal static class NativeShutdownAdmissionScene
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string OriginalBasket = "shutdown-admission-original";
    private const string ReplacementBasket = "shutdown-admission-replacement";
    private sealed record CheckResult(bool Passed, string Description);
    private sealed record TerminalObservation(bool OriginalFillPersisted, bool OriginalReaderCompleted,
        bool ReplacementCreated, bool ReplacementFillPersisted, bool ReplacementReaderCompleted, bool FillFileExists);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        var services = App.Services;
        string root = Path.Combine(AppPaths.Root, "native-shutdown-admission-" + Guid.NewGuid().ToString("N"));
        var evidence = new List<CheckResult>();
        int failures = 0, timedOut = 0, persisted = 0, ticks = 0, terminalEntered = 0, exits = 0, callbacksAfterTerminal = 0;
        int orderLoginsAtSnapshot = 0, orderLoginsAfterTick = 0;
        string? appendPath = null;
        TerminalObservation? terminal = null;
        Task? originalFill = null, replacementFill = null, shutdown = null, terminalWork = null;
        Task? originalReader = null, replacementReader = null;
        RithmicTradingSession? original = null, replacement = null;
        Action<RithmicFill>? originalConsumer = null, replacementConsumer = null;
        DispatcherTimer? accountsTimer = null;
        using var originalEntered = new ManualResetEventSlim();
        using var originalRelease = new ManualResetEventSlim();
        using var replacementEntered = new ManualResetEventSlim();
        using var replacementRelease = new ManualResetEventSlim();
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 1, 100);
        var profile = new ConnectionProfile
        {
            Id = "native-shutdown-admission-" + Guid.NewGuid().ToString("N"), Kind = ConnectionKind.Rithmic,
            Name = "Native shutdown admission fixture", Enabled = true, AllowRealOrders = false, MaxOrderQuantity = 1,
            RithmicUri = server.Uri, RithmicSystem = "Rithmic 01", RithmicUser = "demo",
        };
        string accountId = AppServices.RithmicAccountId(profile, "ACC1");
        var fillEvent = typeof(RithmicTradingSession).GetField("FillReceived", Private)!;
        var shutdownGate = Field<object>(services, "_shutdownGate");
        var sessions = Field<Dictionary<string, RithmicTradingSession>>(services, "_rithmicSessions");
        var retired = Field<List<RithmicTradingSession>>(services, "_retiredRithmicSessions");
        AccountJournal? journal = null;
        long CrashLength()
        {
            string path = Path.Combine(AppPaths.Root, "crash.log");
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        int OrderLogins()
        {
            lock (Field<object>(server, "_gate")) return server.Logins.GetValueOrDefault(2);
        }
        void Check(bool passed, string description)
        {
            evidence.Add(new(passed, description));
            if (!passed) failures++;
            Console.WriteLine($"{(passed ? "OK" : "FAIL")} native shutdown admission: {description}");
        }
        void Require(bool passed, string description)
        {
            Check(passed, description);
            if (!passed) throw new InvalidOperationException(description);
        }
        void Until(Func<bool> condition, string description)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < deadline) pump(0.02);
            Require(condition(), description);
        }
        void Await(Task task, string description)
        {
            Until(() => task.IsCompleted, description);
            task.GetAwaiter().GetResult();
        }
        void OnAccountTick(object? sender, EventArgs args) => Interlocked.Increment(ref ticks);
        Action<RithmicFill> Hold(Action<RithmicFill> registered, ManualResetEventSlim entered, ManualResetEventSlim release) => native =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) Interlocked.Increment(ref timedOut);
            registered(native);
            if (Volatile.Read(ref terminalEntered) != 0) Interlocked.Increment(ref callbacksAfterTerminal);
        };
        try
        {
            foreach (string variable in new[] { "TRADINGHUB_DATA_DIR", "TRADINGHUB_STRATEGIES_DIR", "TRADINGHUB_INDICATORS_DIR",
                "TRADINGHUB_BACKTESTS_DIR", "TRADINGHUB_LIVE_DIR", "TRADINGHUB_KEYCHAIN_FILE" })
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                    throw new InvalidOperationException("Missing isolated native admission store " + variable);
            if (Environment.GetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD") != "off")
                throw new InvalidOperationException("The native admission fixture requires sleep guard off.");
            Directory.CreateDirectory(root);
            lock (shutdownGate) Require(sessions.Count == 0, "the isolated application has no native order session before Accounts discovery");
            foreach (var existing in services.Connections.Profiles) existing.Enabled = false;
            journal = new AccountJournal(Path.Combine(root, "Accounts"), path =>
            {
                appendPath = path;
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            });
            journal.Changed += account => { if (account == accountId) Interlocked.Increment(ref persisted); };
            typeof(AppServices).GetField("<Journal>k__BackingField", Private)!.SetValue(services, journal);
            services.Connections.Profiles.Add(profile);
            Keychain.Set(profile.SecretAccount, "demo");
            Await(server.PushOrder(OriginalBasket, "external-shutdown-original", false, 1, 95, type: 4),
                "the fake seeds original native protection without a local order create");
            owner.Navigate("accounts");
            var accounts = owner.Page("accounts") as AccountsView ?? throw new InvalidOperationException("The actual Accounts page is unavailable.");
            accountsTimer = Field<DispatcherTimer>(accounts, "_timer");
            accountsTimer.Tick += OnAccountTick;
            Until(() =>
            {
                lock (shutdownGate) return sessions.TryGetValue(profile.Id, out original) && original.OrdersReadyFor("ACC1") &&
                    original.PositionReadyFor("ACC1", "ESZ6", "CME") && original.OrdersOf("ACC1").Any(order => order.BasketId == OriginalBasket);
            }, "visible Accounts discovery creates the actual AppServices session with complete native replay");
            Until(() => Volatile.Read(ref ticks) > 0 && Field<IReadOnlyList<AccountEntry>>(accounts, "_accounts").Any(account => account.Id == accountId),
                "the actual visible Accounts timer enumerates the enabled native profile");
            Require(profile.Enabled && accounts.IsEffectivelyVisible && accountsTimer.IsEnabled,
                "the consumer is a visible enabled-profile Accounts page, not the disabled-profile retirement fixture");
            accountsTimer.Stop();
            originalReader = Field<Task>(original!, "_loop");
            originalConsumer = (Action<RithmicFill>?)fillEvent.GetValue(original) ??
                throw new InvalidOperationException("AppServices did not register the original native fill consumer.");
            fillEvent.SetValue(original, Hold(originalConsumer, originalEntered, originalRelease));
            profile.RithmicSystem = "Rithmic Test";
            services.Connections.Invalidate(profile);
            lock (shutdownGate) Require(!sessions.ContainsKey(profile.Id) && retired.Contains(original!),
                "actual profile replacement retains the exposed original outside the current-session dictionary");
            var originalCancellation = Field<CancellationTokenSource>(original!, "_cts");
            Require(!originalCancellation.IsCancellationRequested, "the retained original has not entered native stop before the snapshot");
            originalFill = server.FillOrder(OriginalBasket, 1, 95);
            Until(() => originalEntered.IsSet, "the original registered native consumer is held before its real AccountJournal.Add");
            Await(originalFill, "the original native execution and independent PnL reports are sent");
            Until(() => original!.PositionFor("ACC1", "ESZ6", "CME")?.Net == 0,
                "independent native PnL becomes flat while the original journal consumer stays held");
            Require(original!.OrdersOf("ACC1").All(order => order.Done) && original.OrdersReadyFor("ACC1") &&
                !original.HasUnresolvedPositionClose && !originalReader.IsCompleted && Volatile.Read(ref persisted) == 0,
                "the original is snapshot-idle but its actual reader and durable fill remain unfinished");
            var terminalLifetime = new ApplicationQuitTerminal(services, () => Interlocked.Increment(ref exits));
            terminalWork = Task.Run(() => terminalLifetime.CompleteAsync(() =>
            {
                Volatile.Write(ref terminalEntered, 1);
                var fills = journal.FillsOf(accountId);
                terminal = new(fills.Any(fill => fill.OrderId == OriginalBasket), originalReader.IsCompleted,
                    replacement != null, replacement == null || fills.Any(fill => fill.OrderId == ReplacementBasket),
                    replacementReader == null || replacementReader.IsCompleted,
                    appendPath != null && File.Exists(appendPath) && new FileInfo(appendPath).Length > 0);
            }));
            Until(() =>
            {
                lock (shutdownGate) return originalCancellation.IsCancellationRequested && sessions.Count == 0 &&
                    Field<List<Task>>(services, "_rithmicRetirementTasks").Any(task => !task.IsCompleted);
            }, "the actual native drain finishes its copied-taskset critical section with the original held and current dictionary empty");
            shutdown = services.ShutdownAsync();
            orderLoginsAtSnapshot = OrderLogins();
            Require(!shutdown.IsCompleted && !terminalWork.IsCompleted && !originalReader.IsCompleted,
                "the real service and terminal barriers are waiting for the original native reader");
            typeof(AppServices).GetMethod("DisposeIdleReplacedBrokers", Private)!.Invoke(services, null);
            lock (shutdownGate) Require(sessions.Count == 0 && !retired.Contains(original),
                "actual idle retirement removes the original before the next visible Accounts timer tick");
            long crashBeforeTick = CrashLength();
            int ticksBefore = Volatile.Read(ref ticks);
            accountsTimer.Start();
            Until(() => Volatile.Read(ref ticks) > ticksBefore, "the actual visible Accounts timer runs after the native drain snapshot");
            accountsTimer.Stop();
            lock (shutdownGate) sessions.TryGetValue(profile.Id, out replacement);
            Check(replacement == null, "the visible Accounts timer cannot admit a new native reader after the drain snapshot");
            Check(CrashLength() == crashBeforeTick, "closing account enumeration is graceful and does not write an unexpected UI exception");
            if (replacement != null)
            {
                replacementReader = Field<Task>(replacement, "_loop");
                replacementConsumer = (Action<RithmicFill>?)fillEvent.GetValue(replacement) ??
                    throw new InvalidOperationException("AppServices did not register the replacement native fill consumer.");
                fillEvent.SetValue(replacement, Hold(replacementConsumer, replacementEntered, replacementRelease));
                Until(() => replacement.OrdersReadyFor("ACC1") && replacement.PositionReadyFor("ACC1", "ESZ6", "CME"),
                    "the illegally admitted replacement starts actual native order and PnL readers");
                Await(server.PushOrder(ReplacementBasket, "external-shutdown-replacement", true, 1, 96),
                    "the fake sends a distinct external native order to the replacement reader");
                Until(() => replacement.OrdersOf("ACC1").Any(order => order.BasketId == ReplacementBasket),
                    "the replacement consumes the actual working native report before its fill");
                replacementFill = server.FillOrder(ReplacementBasket, 1, 96);
                Until(() => replacementEntered.IsSet, "the replacement's independently held callback reaches its actual registered journal consumer");
                Await(replacementFill, "the distinct replacement native execution and independent PnL reports are sent");
                Require(!originalReader.IsCompleted && !replacementReader.IsCompleted && Volatile.Read(ref persisted) == 0,
                    "both actual journal consumers are independently held outside the journal lock");
            }
            orderLoginsAfterTick = OrderLogins();
            Check(orderLoginsAfterTick == orderLoginsAtSnapshot, "no native order-plant login begins after terminal drain ownership is captured");
            originalRelease.Set();
            Until(() => originalReader.IsCompleted, "the original reader joins after its actual journal consumer is released");
            if (replacement != null)
            {
                DateTime observation = DateTime.UtcNow.AddMilliseconds(250);
                while (!terminalWork.IsCompleted && DateTime.UtcNow < observation) pump(0.02);
                Check(!shutdown.IsCompleted && !terminalWork.IsCompleted && !replacementReader!.IsCompleted,
                    "releasing the original cannot permit true terminal completion while another admitted native consumer is held");
            }
            replacementRelease.Set();
            if (replacement != null) Await(replacement.StopAsync(), "the independently admitted replacement reader is joined after its release");
            Await(terminalWork, "the actual terminal composition finishes after the owned native consumers are released");
            Require(terminal != null, "the actual terminal handoff supplies its native ownership observation");
            Check(terminal!.OriginalFillPersisted && terminal.OriginalReaderCompleted && terminal.ReplacementFillPersisted &&
                terminal.ReplacementReaderCompleted && terminal.FillFileExists,
                "actual terminal handoff observes every admitted native reader joined and every actual execution persisted");
            Check(Volatile.Read(ref callbacksAfterTerminal) == 0, "no actual registered native journal consumer completes after the terminal handoff");
            Check(Volatile.Read(ref exits) == 1 && shutdown.IsCompleted,
                "the real terminal composition exits exactly once after its actual shared service completion");
            var finalFills = journal.FillsOf(accountId);
            Check(finalFills.Count == (replacement == null ? 1 : 2) &&
                finalFills.Single(fill => fill.OrderId == OriginalBasket) is { Quantity: 1, Buy: false, Price: 95 } &&
                (replacement == null || finalFills.Single(fill => fill.OrderId == ReplacementBasket) is { Quantity: 1, Buy: true, Price: 96 }),
                "finite cleanup preserves each real execution exactly once, including any replacement admitted by the defect");
            Check(Volatile.Read(ref timedOut) == 0, "neither independent native consumer is released by its finite watchdog");
            lock (server.Tags) Check(server.Tags.Count == 0, "the enabled-profile discovery and terminal scenario submit zero native orders");
        }
        catch (Exception error)
        {
            Check(false, "the actual native shutdown admission scenario failed: " + error);
        }
        finally
        {
            accountsTimer?.Stop();
            if (accountsTimer != null) accountsTimer.Tick -= OnAccountTick;
            originalRelease.Set();
            replacementRelease.Set();
            try
            {
                if (originalFill != null) Await(originalFill, "the owned original fake sender finishes during cleanup");
                if (replacementFill != null) Await(replacementFill, "the owned replacement fake sender finishes during cleanup");
                if (original != null) Await(original.StopAsync(), "cleanup joins the original actual native reader");
                if (replacement != null) Await(replacement.StopAsync(), "cleanup joins the replacement actual native reader");
                RithmicTradingSession[] remaining;
                lock (shutdownGate) remaining = sessions.Values.Concat(retired).Distinct().ToArray();
                foreach (var session in remaining) Await(session.StopAsync(), "cleanup joins every remaining application-owned native reader");
                if (original != null && originalConsumer != null) fillEvent.SetValue(original, originalConsumer);
                if (replacement != null && replacementConsumer != null) fillEvent.SetValue(replacement, replacementConsumer);
                Await(services.ShutdownAsync(), "cleanup joins the actual shared service shutdown");
                if (terminalWork != null) Await(terminalWork, "cleanup joins the actual terminal composition");
            }
            catch (Exception error) { Check(false, "native shutdown admission cleanup failed: " + error); }
            File.WriteAllText(Path.Combine(outDir, "native-shutdown-admission-results.json"),
                JsonSerializer.Serialize(new { checks = evidence.Count, failures, root, profileEnabled = profile.Enabled,
                    schedule = "active-retired native snapshot, then actual idle retirement, then visible Accounts timer",
                    terminal, persisted, timedOut, ticks, orderLoginsAtSnapshot, orderLoginsAfterTick, callbacksAfterTerminal, exits, evidence },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures;
    }
}
