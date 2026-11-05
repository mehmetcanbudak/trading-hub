using System.Runtime.CompilerServices;
using TradingHub.Market;
using TradingHub.Replay;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    public AlertCenter Alerts { get; private set; } = null!;
    public ReplaySessions ReplaySessions { get; private set; } = null!;
    private readonly ConditionalWeakTable<LiveStrategyRunner, RunnerAlertSubscription> _runnerAlerts = new();
    private readonly ConditionalWeakTable<TradingHub.Trading.ITradingAccount, object> _accountAlerts = new();
    private HttpClient? _notificationHttp;

    private void InitializeReplayAlerts()
    {
        _notificationHttp = new HttpClient(new NotificationProxyHandler(() =>
        {
            var handler = NetworkProxy.CreateFactory().CreateHandler();
            handler.AllowAutoRedirect = false;
            return handler;
        }), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        Alerts = new AlertCenter(Path.Combine(AppPaths.Root, "notifications.json"), Keychain.Store, _notificationHttp);
        ReplaySessions = new ReplaySessions(Hub, AppPaths.Ticks, Path.Combine(AppPaths.Root, "Practice"), Alerts);
        BracketsCreated += OnAlertBracketsCreated;
    }
    private void OnAlertBracketsCreated(TradingHub.Trading.BracketManager brackets) =>
        brackets.Message += message =>
        {
            var run = ReplaySessions.All.FirstOrDefault(r => ReferenceEquals(r.Account, brackets.Account));
            Alerts.Raise(AlertKind.Risk, run?.IsBlind == true ? "Practice" : brackets.Account.Instrument.Symbol,
                "Order protection", run?.IsBlind == true ? "Practice protection notification. Reveal the run to inspect details." : message, allowExternal: run == null);
        };

    public ReplaySession OpenPracticeReplay(Instrument source, DateTime startUtc, DateTime endUtc, TimeSpan preload, int seed = 0, bool blind = false)
    {
        if (IsShuttingDown) throw new InvalidOperationException("Trading Hub is shutting down; new practice sessions are disabled.");
        return ReplaySessions.Open(ReplaySelection.Selected(source, startUtc, endUtc, seed, blind), Settings.ReplayStartBalance,
            new ReplayFees(Settings.ReplayFeePerUnit, Settings.ReplayNotionalFeeRate), preload);
    }

    private void AttachTradingAlerts(TradingHub.Trading.ITradingAccount account)
    {
        if (account is not TradingHub.Trading.TradingManager manager || account is ReplayAccount || _accountAlerts.TryGetValue(account, out _)) return;
        _accountAlerts.Add(account, new object());
        manager.NewMyTrade += fill => Alerts.Raise(AlertKind.Execution, account.Instrument.Symbol, account.Name,
            $"{fill.OrderDirection} {fill.Volume} @ {fill.Price} · fee {fill.Commission}", timeUtc: fill.Time);
    }

    private void AttachRunnerAlerts(LiveStrategyRunner runner)
    {
        if (Alerts == null || _runnerAlerts.TryGetValue(runner, out _)) return;
        _runnerAlerts.Add(runner, new RunnerAlertSubscription(this, runner));
    }

    private async ValueTask DisposeReplayAlertsAsync()
    {
        BracketsCreated -= OnAlertBracketsCreated;
        ReplaySessions?.Dispose();
        if (Alerts != null) await Alerts.DisposeAsync().ConfigureAwait(false);
        _notificationHttp?.Dispose();
    }

    // Proxy/credential construction happens only on the notification worker after prior destination consent.
    // A bad proxy cannot prevent application startup or fall back to an unapproved direct connection.
    private sealed class NotificationProxyHandler(Func<HttpMessageHandler> factory) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private HttpMessageInvoker? _transport;
        private bool _disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _transport ??= new HttpMessageInvoker(factory(), disposeHandler: true);
                return _transport.SendAsync(request, cancellationToken);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) lock (_gate) { _disposed = true; _transport?.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class RunnerAlertSubscription
    {
        public RunnerAlertSubscription(AppServices services, LiveStrategyRunner runner)
        {
            void Publish(AlertKind kind, string message, string? id = null, DateTime? time = null)
            {
                var run = services.ReplaySessions.TryGet(runner.Data);
                bool blind = run?.IsBlind == true;
                services.Alerts.Raise(kind, blind ? "Practice" : runner.ExecInstrument.Symbol, runner.StrategyName,
                    blind ? "Practice strategy notification. Reveal the run to inspect its strategy log." : message,
                    id, time, allowExternal: run == null);
            }
            runner.StrategyInstance.AlertRaised += alert => Publish(AlertKind.Strategy, alert.Message, alert.Id, alert.TimeUtc);
            runner.LimitHit += (_, message) => Publish(AlertKind.Risk, message);
            runner.GuardBlocked += (_, message) => Publish(AlertKind.Risk, message);
            runner.Executed += (_, message) => Publish(AlertKind.Execution, message);
        }
    }
}
