using System.Net;
using TradingHub.App.Services;
using TradingHub.Feeds;
using TradingHub.Feeds.Longbridge;

namespace TradingHub.UiSnapshot;

// Constructor dependencies only: production AppServices/ConnectionManager/LongbridgeFeed and OAuth flow
// own these controlled transports. No alternate terminal implementation or real authorization is used.
internal sealed class KvpShutdownLongbridgeFixture
{
    internal readonly HeldTransport Transport = new();
    internal readonly PrivateHttpProbe Http = new();
    internal readonly TaskCompletionSource BrowserEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource BrowserRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ConnectionProfile Profile { get; } = new()
    {
        Id = "shutdown-longbridge-" + Guid.NewGuid().ToString("N"), Name = "Owned Longbridge shutdown", Kind = ConnectionKind.Longbridge, Enabled = true,
        Longbridge = new LongbridgeSettings
        {
            ClientId = "isolated-existing-public-client", BaseUrl = "https://auth.invalid", Symbols = ["AAPL.US"],
            Instruments = new(StringComparer.OrdinalIgnoreCase)
            {
                ["AAPL.US"] = new LongbridgeInstrumentMetadata
                {
                    TickSize = .01m, VolumeStep = 1, Exchange = "NASDAQ", Currency = "USD", TimeZoneId = "America/New_York",
                    RegularSessions = [new(new TimeOnly(9, 30), new TimeOnly(16, 0))],
                },
            },
        },
    };
    internal Task InstrumentRequest { get; private set; } = null!;
    internal Task Authorization { get; private set; } = null!;

    internal AppServices CreateServices() => new(networkFactory => new ConnectionManager(networkFactory,
        longbridgeHttpFactory: (_, _) => new HttpClient(Http), longbridgeTransportFactory: (_, _) => Transport));

    internal void Begin(AppServices services)
    {
        services.Connections.Profiles.Add(Profile);
        services.Connections.Save();
        var feed = (LongbridgeFeed)services.Connections.GetFeed(Profile.Id);
        InstrumentRequest = feed.GetInstrumentsAsync(CancellationToken.None);
        Authorization = LongbridgeAuthorizationFlow.AuthorizeAsync(services.Connections, Profile, async (_, _) =>
        {
            BrowserEntered.TrySetResult();
            await BrowserRelease.Task; // Deliberately ignore cancellation to prove owned completion, never open a browser.
        });
    }
    internal void ReleaseAll() { Transport.Release.TrySetResult(); BrowserRelease.TrySetResult(); }

    internal sealed class PrivateHttpProbe : HttpMessageHandler
    {
        internal bool Disposed { get; private set; }
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("This isolated known-client Longbridge shutdown flow must not issue OAuth HTTP requests.");
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }
    internal sealed class HeldTransport : ILongbridgeTransport, IAsyncDisposable
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _closing;
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<byte, ReadOnlyMemory<byte>>? Push { add { } remove { } }
        public event Action<FeedStatus, string?>? Status { add { } remove { } }
        public async Task<byte[]> RequestAsync(byte command, ReadOnlyMemory<byte> body, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (command != 10) throw new InvalidOperationException("Expected the actual Longbridge static-instrument request.");
            Entered.TrySetResult();
            try
            {
                await Release.Task.ConfigureAwait(false); // A late provider response stays owned until explicitly released.
                ct.ThrowIfCancellationRequested();
                return [];
            }
            finally { _completed.TrySetResult(); }
        }
        public void Dispose() => _ = DisposeAsync().AsTask();
        public async ValueTask DisposeAsync()
        {
            _closing = true;
            if (Entered.Task.IsCompleted) await _completed.Task.ConfigureAwait(false);
            Disposed = true;
        }
    }
}
