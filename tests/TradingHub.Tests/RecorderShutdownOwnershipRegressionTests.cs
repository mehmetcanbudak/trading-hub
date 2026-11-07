using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[CollectionDefinition("RecorderShutdownOwnership", DisableParallelization = true)]
public sealed class RecorderShutdownOwnershipCollection { }

[Collection("RecorderShutdownOwnership")]
public sealed class RecorderShutdownOwnershipRegressionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriterHold = TimeSpan.FromSeconds(5);
    private static readonly DateTime Start = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [MacRecorderFact]
    public async Task True_service_shutdown_does_not_complete_with_a_live_recorder_lease_and_unsaved_accepted_tail()
    {
        await using var fixture = new RecorderFixture();
        await fixture.AcceptLiveRowsAsync();
        var shutdown = Task.Run(async () =>
        {
            await fixture.Services.ShutdownAsync().ConfigureAwait(false);
            return fixture.ObserveOwnership();
        });
        try
        {
            await fixture.Feed.SubscriptionStopped.Task.WaitAsync(Watchdog);
            // The deadline only releases the FIFO. Assertions use ownership captured at actual terminal completion.
            await Task.WhenAny(shutdown, Task.Delay(WriterHold));
        }
        finally
        {
            fixture.ReleaseWriter();
        }

        var returned = await shutdown.WaitAsync(Watchdog);
        await fixture.WaitForWriterAsync();
        fixture.AssertAcceptedTail();
        Assert.True(fixture.Services.IsShuttingDown);
        Assert.True(fixture.Services.PineHandoffs.IsStopping);
        Assert.True(fixture.Services.Work.IsStopping);
        Assert.True(fixture.Feed.IsDisposed);
        Assert.Equal(0, fixture.NetworkRequests);
        Assert.False(returned.RecorderActive,
            "AppServices.ShutdownAsync completed while its real recorder still held append ownership.");
        Assert.False(returned.InstrumentLeaseHeld,
            "The terminal service barrier returned before the recorder released the instrument lease.");
        Assert.False(returned.TailDayLeaseHeld,
            "The terminal service barrier returned while the accepted normal-day tail was still reserved by its writer.");
        Assert.True(returned.TailFileExists,
            "The accepted normal-day tail must exist before the terminal service barrier completes.");
    }

    [MacRecorderFact]
    public async Task Ordinary_hub_dispose_can_return_before_the_real_recorder_finishes_without_surrendering_its_tail()
    {
        await using var fixture = new RecorderFixture();
        await fixture.AcceptLiveRowsAsync();
        OwnershipObservation returned;
        try
        {
            await Task.Run(fixture.Services.Hub.Dispose).WaitAsync(Watchdog);
            returned = fixture.ObserveOwnership();
        }
        finally
        {
            fixture.ReleaseWriter();
        }

        await fixture.WaitForWriterAsync();
        fixture.AssertAcceptedTail();
        Assert.True(returned.RecorderActive);
        Assert.True(returned.InstrumentLeaseHeld);
        Assert.True(returned.TailDayLeaseHeld);
        Assert.False(returned.TailFileExists);
        Assert.False(fixture.Feed.IsDisposed);
        Assert.Equal(0, fixture.NetworkRequests);
    }

    [MacRecorderFact]
    public Task True_service_shutdown_retains_a_recorder_detached_by_an_earlier_instrument_stop() =>
        AssertDetachedRecorderAsync(hub => hub.Items.Single().Stop());

    [MacRecorderFact]
    public Task True_service_shutdown_retains_the_old_recorder_after_a_new_generation_starts() =>
        AssertDetachedRecorderAsync(hub =>
        {
            var data = hub.Items.Single();
            data.Start(data.Feed.Now);
        });

    [MacRecorderFact]
    public Task True_service_shutdown_retains_a_recorder_after_ordinary_hub_disposal_clears_its_items() =>
        AssertDetachedRecorderAsync(hub =>
        {
            hub.Dispose();
            Assert.Empty(hub.Items);
        });

    private static async Task AssertDetachedRecorderAsync(Action<MarketDataHub> detach)
    {
        await using var fixture = new RecorderFixture();
        await fixture.AcceptLiveRowsAsync();
        await Task.Run(() => detach(fixture.Services.Hub)).WaitAsync(Watchdog);
        var detached = fixture.ObserveOwnership();
        Assert.True(detached.RecorderActive);
        Assert.True(detached.InstrumentLeaseHeld);
        Assert.True(detached.TailDayLeaseHeld);
        Assert.False(detached.TailFileExists);
        var shutdown = Task.Run(async () =>
        {
            await fixture.Services.ShutdownAsync().ConfigureAwait(false);
            return fixture.ObserveOwnership();
        });
        try { await Task.WhenAny(shutdown, Task.Delay(WriterHold)); }
        finally { fixture.ReleaseWriter(); }
        var returned = await shutdown.WaitAsync(Watchdog);
        await fixture.WaitForWriterAsync();
        fixture.AssertAcceptedTail();
        Assert.True(fixture.Services.IsShuttingDown);
        Assert.True(fixture.Services.Work.IsStopping);
        Assert.True(fixture.Services.PineHandoffs.IsStopping);
        Assert.True(fixture.Feed.IsDisposed);
        Assert.Equal(0, fixture.NetworkRequests);
        Assert.False(returned.RecorderActive,
            "The terminal service barrier lost ownership of a recorder detached before shutdown.");
        Assert.False(returned.InstrumentLeaseHeld);
        Assert.False(returned.TailDayLeaseHeld);
        Assert.True(returned.TailFileExists);
    }

    private sealed record OwnershipObservation(bool RecorderActive, bool InstrumentLeaseHeld,
        bool TailDayLeaseHeld, bool TailFileExists);

    private sealed class RecorderFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-recorder-shutdown-" + Guid.NewGuid().ToString("N"));
        private readonly Instrument _instrument = new()
        {
            FeedId = "recorder-shutdown-fixture", Symbol = "TAIL", TickSize = 1, VolumeStep = 1, PointValue = 1,
        };
        private readonly Trade _accepted = new(1, Start, 100, 2, TradeSide.Buy);
        private readonly Trade _tail = new(2, Start.AddDays(1), 101, 3, TradeSide.Sell);
        private readonly CancellationTokenSource _archiveStop = new();
        private readonly RejectNetwork _network = new();
        private readonly HttpClient _http;
        private readonly string _previousLiveRoot;
        private readonly string _ticks;
        private readonly string _fifoPath;
        private readonly string _tailPath;
        private SafeFileHandle? _fifoEndpoint;
        private bool _fifoCreated;

        public AppServices Services { get; }
        public LiveFeed Feed { get; }
        public int NetworkRequests => _network.Requests;

        public RecorderFixture()
        {
            string? startupRoot = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR");
            Assert.False(string.IsNullOrWhiteSpace(startupRoot),
                "Start the test process with an isolated data directory before resolving application paths.");
            _previousLiveRoot = LiveStore.Root;
            Directory.CreateDirectory(_root);
            _ticks = Path.Combine(_root, "Ticks");
            _fifoPath = TickFile.DayPath(_ticks, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(_accepted.Time));
            _tailPath = TickFile.DayPath(_ticks, _instrument.FeedId, _instrument.Symbol, DateOnly.FromDateTime(_tail.Time));
            _http = new HttpClient(_network);
            var secrets = new MemorySecrets();
            var profile = new ConnectionProfile
            {
                Id = _instrument.FeedId, Kind = ConnectionKind.Simulator, Name = "Recorder shutdown fixture", Enabled = true,
            };
            string connectionsPath = Path.Combine(_root, "connections.json");
            File.WriteAllText(connectionsPath, System.Text.Json.JsonSerializer.Serialize(new[] { profile }));
            var connections = new ConnectionManager(
                networkFactory: () => throw new InvalidOperationException("The recorder fixture cannot create a network transport."),
                secretStore: secrets, configurationPath: connectionsPath);
            Feed = new LiveFeed(_instrument);
            Field<Dictionary<string, IMarketDataFeed>>(connections, "_feeds").Add(profile.Id, Feed);

            // The startup constructor reads process-wide stores and starts maintenance. Compose only its real shutdown owners here.
            Services = (AppServices)RuntimeHelpers.GetUninitializedObject(typeof(AppServices));
            foreach (string name in new[]
            {
                "_shutdownGate", "_simBrokers", "_realBrokers", "_replacedBrokers", "_rithmicSessions", "_tradovateSessions",
                "_retiredTradovateSessions", "_retiredRithmicSessions", "_historyArchiveGate", "_historyArchiveTasks",
                "_bybitGate", "_bybitSessions", "_retiredBybitSessions", "_bybitProfiles", "_bybitRetirementTasks", "_bybitRecoveryIssues",
            })
            {
                var field = RequiredField(Services, name);
                field.SetValue(Services, Activator.CreateInstance(field.FieldType));
            }
            SetField(Services, "_historyArchiveStop", _archiveStop);
            SetField(Services, "_executionRoot", Path.Combine(_root, "Live"));
            SetField(Services, "<Settings>k__BackingField", new AppSettings { KeepAwake = false, RecordLiveData = true });
            SetField(Services, "<Connections>k__BackingField", connections);
            SetField(Services, "<Hub>k__BackingField", new MarketDataHub { RecordingRoot = _ticks, RecordLive = true });
            SetField(Services, "<Runners>k__BackingField", new List<LiveStrategyRunner>());
            Services.RunningAtLastClose = new List<LiveRunnerInfo>();
            SetField(Services, "<Journal>k__BackingField", new AccountJournal(Path.Combine(_root, "Accounts")));
            SetField(Services, "<PineHandoffs>k__BackingField", new OwnedOperationRegistry());
            SetField(Services, "<Work>k__BackingField", new OwnedOperationRegistry());
            SetField(Services, "<Sleep>k__BackingField", new SleepGuard { FlagDirectory = _root });
            var alerts = new AlertCenter(Path.Combine(_root, "notifications.json"), secrets, _http);
            SetField(Services, "<Alerts>k__BackingField", alerts);
            SetField(Services, "<ReplaySessions>k__BackingField", new ReplaySessions(Services.Hub, _ticks, Path.Combine(_root, "Practice"), alerts));
            LiveStore.Root = Path.Combine(_root, "Live");
        }

        public async Task AcceptLiveRowsAsync()
        {
            var data = Services.Hub.GetOrStart(Services.Connections.GetFeed(_instrument.FeedId), _instrument, TimeSpan.FromMinutes(1));
            using (var ready = new CancellationTokenSource(Watchdog))
            {
                while (data.IsLoadingHistory)
                {
                    Services.Hub.PumpAll();
                    if (data.IsLoadingHistory) await Task.Delay(5, ready.Token);
                }
            }
            Assert.Null(data.HistoryError);
            Assert.Null(data.RecordingWarning);
            Assert.Empty(Enumerable.Range(0, data.Trades.Count));

            // Create the FIFO after history completes so the loader never opens the deliberately blocked recording day.
            Directory.CreateDirectory(Path.GetDirectoryName(_fifoPath)!);
            Assert.Equal(0, MakeFifo(_fifoPath, 0x180));
            _fifoCreated = true;
            Feed.Emit(_accepted);
            Feed.Emit(_tail);
            Services.Hub.PumpAll();
            Assert.Equal(new[] { _accepted.Id, _tail.Id }, Enumerable.Range(0, data.Trades.Count).Select(data.Trades.GetId));
            Assert.Null(data.RecordingWarning);
            var accepted = ObserveOwnership();
            Assert.True(accepted.RecorderActive);
            Assert.True(accepted.InstrumentLeaseHeld);
            Assert.True(accepted.TailDayLeaseHeld);
            Assert.False(accepted.TailFileExists);
        }

        public OwnershipObservation ObserveOwnership()
        {
            using var instrumentLease = TickFile.TryLock(_ticks, _instrument.FeedId, _instrument.Symbol, failOnNonSharingError: true);
            bool tailOwned;
            try
            {
                using var tailLease = new FileStream(_tailPath + ".write.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                tailOwned = false;
            }
            catch (IOException error) when (TickFile.IsSharingViolation(error))
            {
                tailOwned = true;
            }
            return new OwnershipObservation(TickFile.HasActiveRecordingWriter(_ticks, _instrument.FeedId, _instrument.Symbol),
                instrumentLease == null, tailOwned, File.Exists(_tailPath));
        }

        public void ReleaseWriter()
        {
            if (_fifoCreated)
                _fifoEndpoint ??= File.OpenHandle(_fifoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }

        public async Task WaitForWriterAsync()
        {
            using var drain = new CancellationTokenSource(Watchdog);
            while (TickFile.HasActiveRecordingWriter(_ticks, _instrument.FeedId, _instrument.Symbol))
                await Task.Delay(5, drain.Token);
        }

        public void AssertAcceptedTail()
        {
            using var releasedLease = TickFile.TryLock(_ticks, _instrument.FeedId, _instrument.Symbol, failOnNonSharingError: true);
            Assert.NotNull(releasedLease);
            Assert.Equal(_tail, Assert.Single(TickFile.Read(_tailPath, out int dropped, requireComplete: true)));
            Assert.Equal(0, dropped);
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseWriter();
            try
            {
                await Services.ShutdownAsync().WaitAsync(Watchdog);
                await WaitForWriterAsync();
            }
            finally
            {
                LiveStore.Root = _previousLiveRoot;
                _archiveStop.Dispose();
                _http.Dispose();
                _fifoEndpoint?.Dispose();
                if (!TickFile.HasActiveRecordingWriter(_ticks, _instrument.FeedId, _instrument.Symbol))
                    Directory.Delete(_root, recursive: true);
            }
        }

        private static FieldInfo RequiredField(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing fixture composition field " + name);
        private static T Field<T>(object target, string name) => (T)RequiredField(target, name).GetValue(target)!;
        private static void SetField(object target, string name, object value) => RequiredField(target, name).SetValue(target, value);
    }

    private sealed class LiveFeed(Instrument instrument) : IMarketDataFeed
    {
        private IMarketDataSink? _sink;
        private int _disposed;
        public string Id => instrument.FeedId;
        public string DisplayName => "Recorder shutdown live fixture";
        public bool SupportsHistory => false;
        public bool IsLive => true;
        public DateTime Now => Start.AddDays(1).AddMinutes(1);
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public TaskCompletionSource SubscriptionStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Instrument>>(new[] { instrument });
        public IDisposable Subscribe(Instrument selected, IMarketDataSink sink)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _sink = sink;
            sink.OnStatus(selected, FeedStatus.Connected, null);
            return new Subscription(() =>
            {
                _sink = null;
                SubscriptionStopped.TrySetResult();
            });
        }
        public void Emit(Trade trade) =>
            (_sink ?? throw new InvalidOperationException("The live fixture is not subscribed.")).OnTrades(instrument, new[] { trade });
        public Task LoadTradesAsync(Instrument selected, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) =>
            throw new NotSupportedException("The recorder fixture has no historical transport.");
        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _sink = null;
        }
        private sealed class Subscription(Action stop) : IDisposable
        {
            private Action? _stop = stop;
            public void Dispose() => Interlocked.Exchange(ref _stop, null)?.Invoke();
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string Get(string account) => _values.GetValueOrDefault(account, "");
        public void Set(string account, string? secret)
        {
            if (string.IsNullOrEmpty(secret)) _values.Remove(account);
            else _values[account] = secret;
        }
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("The recorder shutdown fixture cannot send network requests.");
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
