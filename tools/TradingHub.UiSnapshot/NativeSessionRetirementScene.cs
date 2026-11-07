using System.Reflection;
using System.Text.Json;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Feeds.Rithmic;
using TradingHub.Tests;
using TradingHub.Trading;

internal static class NativeSessionRetirementScene
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private sealed record CheckResult(bool Passed, string Description);
    private sealed record TerminalObservation(bool FillPersisted, bool ReaderCompleted, bool FillFileExists);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    public static int Run(MainWindow owner, Action<double> pump, string outDir, bool idleRetired = false)
    {
        string root = Path.Combine(AppPaths.Root, "native-retirement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var services = App.Services;
        var evidence = new List<CheckResult>();
        int failures = 0, timedOut = 0, persisted = 0;
        string? appendPath = null;
        TerminalObservation? terminal = null;
        Task? fill = null, shutdown = null;
        Task<TerminalObservation>? terminalWork = null;
        RithmicTradingSession? session = null;
        Action<RithmicFill>? registeredFill = null;
        using var appendEntered = new ManualResetEventSlim();
        using var appendRelease = new ManualResetEventSlim();
        using var server = new FakeRithmicServer { MarketPrice = 100 };
        server.SeedPosition(true, 1, 100);
        var profile = new ConnectionProfile
        {
            Id = "native-retirement-" + Guid.NewGuid().ToString("N"), Kind = ConnectionKind.Rithmic,
            Name = "Native retirement fixture", Enabled = false, AllowRealOrders = false, MaxOrderQuantity = 1,
            RithmicUri = server.Uri, RithmicSystem = "Rithmic 01", RithmicUser = "demo",
        };
        void Check(bool passed, string description)
        {
            evidence.Add(new(passed, description));
            if (!passed) failures++;
            Console.WriteLine($"{(passed ? "OK" : "FAIL")} native retirement: {description}");
        }
        void Until(Func<bool> condition, string description)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < deadline) pump(0.02);
            Check(condition(), description);
            if (!condition()) throw new TimeoutException(description);
        }
        void Await(Task task, string description)
        {
            Until(() => task.IsCompleted, description);
            task.GetAwaiter().GetResult();
        }
        try
        {
            foreach (string variable in new[] { "TRADINGHUB_DATA_DIR", "TRADINGHUB_STRATEGIES_DIR", "TRADINGHUB_INDICATORS_DIR",
                "TRADINGHUB_BACKTESTS_DIR", "TRADINGHUB_LIVE_DIR", "TRADINGHUB_KEYCHAIN_FILE" })
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                    throw new InvalidOperationException("Missing isolated native retirement store " + variable);
            if (Environment.GetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD") != "off")
                throw new InvalidOperationException("The native retirement fixture requires sleep guard off.");
            var journal = new AccountJournal(Path.Combine(root, "Accounts"), path =>
            {
                appendPath = path;
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            });
            journal.Changed += account => { if (account == AppServices.RithmicAccountId(profile, "ACC1")) Interlocked.Increment(ref persisted); };
            typeof(AppServices).GetField("<Journal>k__BackingField", Private)!.SetValue(services, journal);
            services.Connections.Profiles.Add(profile);
            Keychain.Set(profile.SecretAccount, "demo");
            Await(server.PushOrder("retirement-final-fill", "external-retirement-stop", false, 1, 95, type: 4),
                "the fake seeds a real reducing native order without a local create");
            session = services.RithmicSessionFor(profile);
            Until(() => session.OrdersReadyFor("ACC1") && session.PositionReadyFor("ACC1", "ESZ6", "CME") &&
                session.OrdersOf("ACC1").Any(order => order.BasketId == "retirement-final-fill"),
                "the actual AppServices registered session has full replay and original exposure");
            Task reader = Field<Task>(session, "_loop");
            var fillEvent = typeof(RithmicTradingSession).GetField("FillReceived", Private)!;
            registeredFill = (Action<RithmicFill>?)fillEvent.GetValue(session) ??
                throw new InvalidOperationException("The actual AppServices native fill consumer was not registered.");
            Action<RithmicFill> originalConsumer = registeredFill;
            fillEvent.SetValue(session, (Action<RithmicFill>)(native =>
            {
                appendEntered.Set();
                if (!appendRelease.Wait(TimeSpan.FromSeconds(20))) Interlocked.Exchange(ref timedOut, 1);
                originalConsumer(native);
            }));
            if (idleRetired)
            {
                profile.RithmicSystem = "Rithmic Test";
                services.Connections.Invalidate(profile);
                Check(Field<List<RithmicTradingSession>>(services, "_retiredRithmicSessions").Contains(session),
                    "reconfiguration initially retains the still-exposed original native session");
            }
            fill = server.FillOrder("retirement-final-fill", 1, 95);
            Until(() => appendEntered.IsSet, "the actual registered FillReceived consumer is held before entering its original AccountJournal.Add");
            Await(fill, "the fake completes its actual order and independent PnL sends before retirement");
            Until(() => session.PositionFor("ACC1", "ESZ6", "CME")?.Net == 0,
                "independent native PnL is flat while the admitted journal append is still held");
            Check(session.OrdersOf("ACC1").All(order => order.Done) && session.OrdersReadyFor("ACC1") &&
                !session.HasUnresolvedPositionClose && !reader.IsCompleted && Volatile.Read(ref persisted) == 0,
                "snapshot-idle is distinguished from the active reader and undurable real fill");
            if (idleRetired)
                typeof(AppServices).GetMethod("DisposeIdleReplacedBrokers", Private)!.Invoke(services, null);
            else
            {
                profile.RithmicSystem = "Rithmic Test";
                services.Connections.Invalidate(profile);
            }
            Check(!Field<Dictionary<string, RithmicTradingSession>>(services, "_rithmicSessions").Values.Contains(session) &&
                !Field<List<RithmicTradingSession>>(services, "_retiredRithmicSessions").Contains(session),
                "the actual reconfiguration or idle-retirement path removes the old session from both active collections");
            Check(Field<CancellationTokenSource>(session, "_cts").IsCancellationRequested && !reader.IsCompleted,
                "retirement cancels admission without impersonating completion of the held reader");
            shutdown = Task.Run(() => services.ShutdownAsync());
            terminalWork = Task.Run(async () =>
            {
                await shutdown.ConfigureAwait(false);
                return new TerminalObservation(Volatile.Read(ref persisted) == 1, reader.IsCompleted,
                    appendPath != null && File.Exists(appendPath) && new FileInfo(appendPath).Length > 0);
            });
            DateTime observation = DateTime.UtcNow.AddMilliseconds(250);
            while (!terminalWork.IsCompleted && DateTime.UtcNow < observation) pump(0.02);
            Check(!terminalWork.IsCompleted && !shutdown.IsCompleted && Volatile.Read(ref persisted) == 0,
                "true application shutdown cannot complete while its removed session still owns the native fill append");
            appendRelease.Set();
            Await(terminalWork, "the actual service terminal barrier finishes after the append is released");
            terminal = terminalWork.GetAwaiter().GetResult();
            Check(terminal.FillPersisted && terminal.ReaderCompleted && terminal.FillFileExists,
                "terminal completion observes the actual fill file and joined original native reader");
            Check(journal.FillsOf(AppServices.RithmicAccountId(profile, "ACC1")) is [{ Quantity: 1, Buy: false }],
                "the original registered account journal contains exactly its real reducing execution");
            Check(Volatile.Read(ref timedOut) == 0, "the finite fixture never releases a held append through its watchdog");
            lock (server.Tags) Check(server.Tags.Count == 0, "retirement and shutdown do not submit a native order");
        }
        catch (Exception error)
        {
            Check(false, "the actual native retirement fixture failed: " + error);
        }
        finally
        {
            appendRelease.Set();
            try
            {
                if (fill != null) Await(fill, "the owned fake fill sender finishes during cleanup");
                if (session != null) Await(session.StopAsync(), "the original native reader is joined before fixture disposal");
                if (session != null && registeredFill != null)
                    typeof(RithmicTradingSession).GetField("FillReceived", Private)!.SetValue(session, registeredFill);
                Await(services.ShutdownAsync(), "the actual application shutdown finishes during cleanup");
                if (terminalWork != null) Await(terminalWork, "the terminal ownership observer finishes during cleanup");
            }
            catch (Exception error) { Check(false, "native retirement cleanup failed: " + error); }
            File.WriteAllText(Path.Combine(outDir, idleRetired ? "native-idle-retirement-results.json" : "native-reconfigure-retirement-results.json"),
                JsonSerializer.Serialize(new { checks = evidence.Count, failures, idleRetired, root, terminal, persisted, timedOut, evidence },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        return failures;
    }
}
