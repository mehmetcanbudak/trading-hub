using System.Text;
using TradingHub.App.Services;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>Order-of-operations and power-cut models for every safety-critical state writer on <see cref="DurableFile"/>.
/// A "power cut" undoes renames whose directory was never synchronized (the bytes were flushed, the name was not).</summary>
[Collection("LiveStore")]
public sealed class DurableStateWriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tradinghub-durable-writers-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string PathFor(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    [Fact]
    public void Replacement_flushes_the_temporary_before_rename_and_synchronizes_the_directory_after_it()
    {
        string target = PathFor("state.json");
        File.WriteAllText(target, "previous");
        var recorder = new DurabilityRecorder();
        using (recorder.Install()) DurableFile.ReplaceAtomically(target, "next");
        recorder.AssertAtomicReplace(target);
        Assert.Equal("next", File.ReadAllText(target));
        Assert.Equal(new[] { target }, Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("flush:")]
    [InlineData("rename:")]
    public void A_failure_before_publication_is_visible_and_keeps_the_previous_file_without_a_leftover(string failing)
    {
        string target = PathFor("state.json");
        File.WriteAllText(target, "previous");
        var recorder = new DurabilityRecorder { FailWhen = step => step.StartsWith(failing, StringComparison.Ordinal) };
        using (recorder.Install())
            Assert.Throws<IOException>(() => DurableFile.ReplaceAtomically(target, "next"));
        Assert.Equal("previous", File.ReadAllText(target));
        Assert.Equal(new[] { target }, Directory.GetFiles(_root));
    }

    [Fact]
    public void An_unsynchronized_rename_is_reported_and_a_power_cut_restores_the_previous_complete_file()
    {
        string target = PathFor("state.json");
        File.WriteAllText(target, "previous");
        var recorder = new DurabilityRecorder { FailWhen = step => step.StartsWith("sync:", StringComparison.Ordinal) };
        using (recorder.Install())
            Assert.Throws<IOException>(() => DurableFile.ReplaceAtomically(target, "next"));
        recorder.LoseUnsyncedRenames();
        Assert.Equal("previous", File.ReadAllText(target));
        string leftover = Assert.Single(Directory.GetFiles(_root, "state.json.*.tmp"));
        Assert.Equal("next", File.ReadAllText(leftover)); // flushed bytes under a name the reader never opens
        DurableFile.ReplaceAtomically(target, "retried");
        Assert.Equal("retried", File.ReadAllText(target));
    }

    [Fact]
    public async Task Asynchronous_replacement_honors_cancellation_before_publication()
    {
        string target = PathFor("control.json");
        File.WriteAllText(target, "previous");
        using var cancel = new CancellationTokenSource();
        var recorder = new DurabilityRecorder();
        using (recorder.Install())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DurableFile.ReplaceAtomicallyAsync(target,
                async (stream, token) => { await stream.WriteAsync("next"u8.ToArray(), token); cancel.Cancel(); }, cancel.Token));
            Assert.DoesNotContain(recorder.Steps, step => step.StartsWith("rename:", StringComparison.Ordinal));
            await DurableFile.ReplaceAtomicallyAsync(target, (stream, token) => stream.WriteAsync("next"u8.ToArray(), token).AsTask(), CancellationToken.None);
        }
        recorder.AssertAtomicReplace(target);
        Assert.Equal("next", File.ReadAllText(target));
        Assert.Equal(new[] { target }, Directory.GetFiles(_root));
    }

    [Fact]
    public void Account_journal_syncs_a_new_name_once_and_never_acknowledges_an_unsynchronized_one()
    {
        string directory = PathFor("journal");
        var time = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
        var first = new AccountFill("first", time, "TEST", true, 1m, 100m, 1m);
        var second = new AccountFill("second", time.AddMinutes(1), "TEST", false, 1m, 101m, 1m);
        var journal = new AccountJournal(directory);
        string file = Path.Combine(directory, "account.jsonl");

        var failing = new DurabilityRecorder { FailWhen = step => step.StartsWith("sync:", StringComparison.Ordinal) };
        using (failing.Install())
            Assert.Throws<IOException>(() => journal.Add("account", first));
        Assert.Empty(journal.FillsOf("account")); // flushed bytes under an unconfirmed name are not acknowledged
        File.Delete(file); // a power cut may lose that never-synchronized name
        Assert.Empty(new AccountJournal(directory).FillsOf("account"));

        var recorder = new DurabilityRecorder();
        using (recorder.Install())
        {
            Assert.True(journal.Add("account", first));
            Assert.True(journal.Add("account", second));
        }
        string flush = "flush:" + DurabilityRecorder.Full(file), sync = "sync:" + DurabilityRecorder.Full(directory);
        Assert.Equal(new[] { flush, sync, flush }, recorder.Steps);
        Assert.Equal(new[] { first, second }, new AccountJournal(directory).FillsOf("account"));

        var restart = new DurabilityRecorder();
        using (restart.Install()) journal.Restart("account");
        Assert.StartsWith("rename:" + DurabilityRecorder.Full(file) + "->", restart.Steps[0]);
        Assert.Equal(sync, restart.Steps[1]);
        using (restart.Install()) Assert.True(journal.Add("account", first));
        Assert.Equal(new[] { flush, sync }, restart.Steps.Skip(2)); // a restarted name is synchronized again
    }

    [Fact]
    public void Order_intent_is_durable_before_prepare_returns_and_a_power_cut_leaves_no_phantom_intent()
    {
        var descriptor = new AccountContextDescriptor("durable-writer-context", "simulator", "paper", "fixture", "durable", "generation");
        string root = PathFor("execution");
        var recorder = new DurabilityRecorder();
        PreparedOrderIntent committed;
        OrderContext context;
        using (recorder.Install())
        {
            context = new OrderContext(descriptor, root);
            committed = context.Prepare(context.ManualOwner, new("test:TEST", OrderDirections.Buy, OrderTypes.Limit, 1m, 100m));
        }
        Assert.Null(committed.Error);
        recorder.AssertEveryTemporaryRenameIsDurable();
        Assert.Contains(recorder.Steps, step => step.StartsWith("rename:", StringComparison.Ordinal) && step.EndsWith(
            Path.DirectorySeparatorChar + "context.json", StringComparison.Ordinal));

        var cut = new DurabilityRecorder
        {
            FailWhen = step => step.StartsWith("sync:", StringComparison.Ordinal) &&
                step.EndsWith(Path.DirectorySeparatorChar + "intent", StringComparison.Ordinal),
        };
        PreparedOrderIntent unconfirmed;
        using (cut.Install())
            unconfirmed = context.Prepare(context.ManualOwner, new("test:TEST", OrderDirections.Sell, OrderTypes.Limit, 1m, 110m));
        Assert.Contains("Simulated storage failure", unconfirmed.Error); // never routable without its durable intent
        cut.LoseUnsyncedRenames();

        var reopened = new OrderContext(descriptor, root);
        Assert.Equal(committed.Request, reopened.Find(committed.Id)?.Request);
        Assert.Null(reopened.Find(unconfirmed.Id));
    }

    [Fact]
    public void Live_ledger_append_publishes_its_intent_durably_before_the_record_and_runner_state_is_replaced_atomically()
    {
        string previous = LiveStore.Root;
        LiveStore.Root = PathFor("live");
        try
        {
            const string key = "durable-writer|test:TEST|sim";
            var time = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
            Assert.Null(LiveStore.AppendFill(key, new LiveFill(time, true, 1m, 100m)));
            string ledger = DurabilityRecorder.Full(LiveStore.LedgerFile(key));
            string directory = Path.GetDirectoryName(ledger)!;
            var recorder = new DurabilityRecorder();
            using (recorder.Install())
                Assert.Null(LiveStore.AppendFill(key, new LiveFill(time.AddMinutes(1), false, 1m, 101m)));
            recorder.AssertOrder("flush:" + ledger + ".wal.tmp", $"rename:{ledger}.wal.tmp->{ledger}.wal", "sync:" + directory,
                "flush:" + ledger, "sync:" + directory, "sync:" + directory);
            recorder.AssertEveryTemporaryRenameIsDurable();
            Assert.False(File.Exists(ledger + ".wal"));
            Assert.Equal(0m, LiveStore.LoadLedger(key, 1m).Position);

            var info = new LiveRunnerInfo { Key = key, ProtectionOrderIds = new() { "stop-1" } };
            Assert.Null(LiveStore.SaveProtectionOrderIds(info));
            string runners = Path.Combine(LiveStore.Root, "estrategias.json");
            var saves = new DurabilityRecorder();
            using (saves.Install()) Assert.Null(LiveStore.SaveProtectionOrderIds(new LiveRunnerInfo { Key = key, ProtectionOrderIds = new() { "stop-2" } }));
            saves.AssertAtomicReplace(runners);

            var cut = new DurabilityRecorder { FailWhen = step => step.StartsWith("sync:", StringComparison.Ordinal) };
            using (cut.Install())
                Assert.Contains("Simulated storage failure", LiveStore.SaveProtectionOrderIds(new LiveRunnerInfo { Key = key, ProtectionOrderIds = new() { "stop-3" } }));
            cut.LoseUnsyncedRenames();
            Assert.Equal(new[] { "stop-2" }, Assert.Single(LiveStore.LoadRunners()).ProtectionOrderIds!);
        }
        finally { LiveStore.Root = previous; }
    }

    [Fact]
    public async Task Bybit_order_state_is_durable_before_transport_and_an_unsynchronized_save_blocks_the_create()
    {
        string statePath = PathFor("bybit", "orders.json");
        var btc = new Instrument { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        var recorder = new DurabilityRecorder();
        using var scope = recorder.Install();
        using var protocol = new FakeBybitProtocol();
        using var http = new HttpClient(protocol);
        await using (var session = new BybitTradingSession(protocol.Settings, statePath: statePath, httpClient: http,
            clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now)))
        {
            await session.RefreshAsync();
            recorder.AssertAtomicReplace(statePath);
            recorder.AssertEveryTemporaryRenameIsDurable();
            string directory = "sync:" + DurabilityRecorder.Full(Path.GetDirectoryName(statePath)!);
            recorder.FailWhen = step => step == directory;
            var order = session.Place(btc, 100, OrderDirections.Buy, OrderTypes.Limit, 1, 99);
            Assert.NotEqual(OrderStates.Active, order.State);
            Assert.Contains("new exposure is blocked", session.LastError);
            Assert.DoesNotContain(protocol.Requests, request => request.Path == "/v5/order/create");
        }
        recorder.FailWhen = null;
        recorder.LoseUnsyncedRenames();
        await using var reopened = new BybitTradingSession(protocol.Settings, statePath: statePath, httpClient: http,
            clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
        Assert.DoesNotContain("unavailable", reopened.LastError ?? "");
        Assert.Empty(reopened.Orders);
    }

    [Fact]
    public async Task Connection_profiles_and_isolated_secrets_are_replaced_durably_and_failures_stay_visible()
    {
        string path = PathFor("app", "connections.json");
        var secrets = new FileSecretStore(PathFor("secrets.json"));
        var connections = new ConnectionManager(secretStore: secrets, configurationPath: path);
        try
        {
            connections.Profiles.Add(new ConnectionProfile { Kind = ConnectionKind.Simulator, Id = "durable-sim", Name = "Durable" });
            var recorder = new DurabilityRecorder();
            using (recorder.Install())
            {
                connections.Save();
                await secrets.SetAsync("account", "secret-value");
            }
            recorder.AssertAtomicReplace(path);
            recorder.AssertAtomicReplace(PathFor("secrets.json"));
            string saved = File.ReadAllText(path);

            // A truncated temporary from an interrupted earlier write is never read as configuration.
            File.WriteAllText(path + "." + Guid.NewGuid().ToString("N") + ".tmp", "[{\"Kind\":");
            var cut = new DurabilityRecorder { FailWhen = step => step.StartsWith("sync:", StringComparison.Ordinal) };
            connections.Profiles[0].Name = "Unconfirmed";
            using (cut.Install()) Assert.Throws<IOException>(connections.Save);
            cut.LoseUnsyncedRenames();
            Assert.Equal(saved, File.ReadAllText(path));
            var reopened = new ConnectionManager(secretStore: secrets, configurationPath: path);
            try { Assert.Equal("Durable", Assert.Single(reopened.Profiles, p => p.Id == "durable-sim").Name); }
            finally { reopened.DisposeAll(); }
            Assert.Equal("secret-value", await secrets.GetAsync("account"));
        }
        finally { connections.DisposeAll(); await secrets.DisposeAsync(); }
    }

    [Fact]
    public async Task Alert_state_is_replaced_durably()
    {
        string path = PathFor("alerts", "alerts.json");
        var recorder = new DurabilityRecorder();
        using var scope = recorder.Install();
        using var http = new HttpClient(new NoNetwork());
        await using (var center = new AlertCenter(path, new MemorySecrets(), http))
        {
            Assert.True(center.Raise(AlertKind.System, "TEST", "Durability", "Local alert", "durable-alert", allowExternal: false));
            await center.FlushAsync();
        }
        recorder.AssertAtomicReplace(path);
        recorder.AssertEveryTemporaryRenameIsDurable();
        await using var reopened = new AlertCenter(path, new MemorySecrets(), http);
        Assert.Single(reopened.Snapshot().Recent); // redacted metadata of the committed alert survives
    }

    [Fact]
    public void App_settings_are_replaced_durably_inside_the_isolated_data_directory()
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR")),
            "Start the test process with an isolated data directory before saving application settings.");
        string path = Path.Combine(AppPaths.Root, "settings.json");
        byte[]? original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            var recorder = new DurabilityRecorder();
            using (recorder.Install()) new AppSettings().Save();
            recorder.AssertAtomicReplace(path);
        }
        finally
        {
            if (original != null) File.WriteAllBytes(path, original);
            else File.Delete(path);
        }
    }

    [Fact]
    public async Task Storage_relocation_makes_every_copied_name_durable_before_the_authority_switch()
    {
        using var fixture = new OperationsStorageFixture();
        var store = fixture.Store();
        var selection = fixture.NewSelection();
        var recorder = new DurabilityRecorder();
        using (recorder.Install())
        {
            await store.StageAsync(await store.PreviewAsync(selection, CancellationToken.None), CancellationToken.None);
            Assert.True((await store.ApplyPendingAsync(CancellationToken.None)).Success);
        }
        string bootstrap = DurabilityRecorder.Full(fixture.AppRoot);
        recorder.AssertEveryTemporaryRenameIsDurable();
        var steps = recorder.Steps;
        int selectionPublished = steps.ToList().FindIndex(step =>
            step.StartsWith("rename:", StringComparison.Ordinal) && step.EndsWith("->" + Path.Combine(bootstrap, "storage-locations.json"), StringComparison.Ordinal));
        Assert.True(selectionPublished > 0);
        int copiedFlush = steps.ToList().FindIndex(step => step.StartsWith("flush:", StringComparison.Ordinal) &&
            step.Contains(".tradinghub-copy-", StringComparison.Ordinal) && step.EndsWith("raw.fixture", StringComparison.Ordinal));
        int stageSync = steps.ToList().FindLastIndex(step => step.StartsWith("sync:", StringComparison.Ordinal) &&
            step.Contains(".tradinghub-copy-", StringComparison.Ordinal));
        Assert.InRange(copiedFlush, 0, stageSync - 1);
        Assert.InRange(stageSync, 0, selectionPublished - 1);
        Assert.InRange(steps.ToList().FindLastIndex(step => step == "sync:" + DurabilityRecorder.Full(Path.GetDirectoryName(selection.AppRoot!)!)), stageSync + 1, selectionPublished - 1);
        Assert.False(File.Exists(Path.Combine(fixture.AppRoot, "storage-relocation.json")));
        Assert.Contains("sync:" + bootstrap, steps.Skip(selectionPublished + 1)); // pending-request removal is durable too
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string Get(string account) => _values.GetValueOrDefault(account, "");
        public void Set(string account, string? secret) { if (secret == null) _values.Remove(account); else _values[account] = secret; }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Durability fixtures never reach the network.");
    }
}
