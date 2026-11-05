using TradingHub.Feeds.Bybit;
using TradingHub.Trading;

namespace TradingHub.App.Services;

public sealed partial class AppServices
{
    private readonly object _bybitGate = new();
    private readonly Dictionary<string, BybitTradingSession> _bybitSessions = new(StringComparer.Ordinal);
    private readonly List<BybitTradingSession> _retiredBybitSessions = new();
    private readonly Dictionary<BybitTradingSession, ConnectionProfile> _bybitProfiles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BybitTradingSession, Task> _bybitRetirementTasks = new(ReferenceEqualityComparer.Instance);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BybitTradingSession, string> BybitJournalScopes = new();
    private bool _bybitClosing;
    public sealed record BybitRecoveryIssue(string ProfileId, string ContextIdentity, string Message);
    private readonly List<BybitRecoveryIssue> _bybitRecoveryIssues = new();
    public IReadOnlyList<BybitRecoveryIssue> BybitRecoveryIssues { get { lock (_bybitGate) return _bybitRecoveryIssues.ToArray(); } }
    public IReadOnlyList<(ConnectionProfile Profile, BybitTradingSession Session)> BybitContexts
    {
        get { lock (_bybitGate) return _bybitProfiles.Select(pair => (pair.Value, pair.Key)).ToArray(); }
    }

    /// <summary>Recover current and retired contexts from provider-owned state and private credential entries, never ordinary settings.</summary>
    private void InitializeKvpProviders()
    {
        string root = Path.Combine(AppPaths.Accounts, "Bybit");
        if (!Directory.Exists(root)) return;
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string profileId = Path.GetFileName(directory);
            var profile = Connections.Profiles.FirstOrDefault(p => p.Id == profileId) ??
                new ConnectionProfile { Id = profileId, Name = "Retired Bybit " + profileId, Kind = ConnectionKind.Bybit, Enabled = false };
            foreach (string state in Directory.EnumerateFiles(directory, "*.json"))
            {
                string identity = Path.GetFileNameWithoutExtension(state);
                try
                {
                    if (!BybitTradingSession.RequiresRecovery(state)) { Connections.ForgetIdleBybitContext(profile, identity); continue; }
                    bool current = false;
                    try
                    {
                        current = profile.Kind == ConnectionKind.Bybit && profile.Enabled && Connections.HasTradingCredentials(profile) &&
                            Connections.BybitContextOf(profile) == identity;
                    }
                    catch (Exception ex) { RecordBybitRegistrationFailure(profile, "current-credentials", ex); }
                    if (current) { BybitSessionFor(profile); continue; }
                    var settings = Connections.SavedBybitContext(profile, identity);
                    if (settings == null) throw new InvalidDataException("The previous credential context is missing from the secret store; its exposure remains unknown.");
                    var session = CreateBybitSession(profile, settings);
                    session.Retire();
                    _retiredBybitSessions.Add(session);
                    session.Start();
                }
                catch (Exception ex) { _bybitRecoveryIssues.Add(new(profileId, identity, "Bybit recovery unavailable: " + ex.Message)); }
            }
        }
    }
    private static string BybitStatePath(ConnectionProfile profile, BybitAccountSettings settings) =>
        Path.Combine(AppPaths.Accounts, "Bybit", profile.Id, settings.ContextIdentity + ".json");

    public static string BybitName(ConnectionProfile profile) => profile.BybitEnvironment == BybitEnvironment.Real
        ? $"{profile.Name} — REAL" : $"{profile.Name} ({(profile.BybitEnvironment == BybitEnvironment.Demo ? "demo" : "testnet")})";

    public static string BybitJournalId(string profileId, BybitTradingSession session)
    {
        if (session.PersistentAccountIdentity == null)
            throw new InvalidOperationException("Bybit account identity is not verified; persistent account history is deferred.");
        string scope = BybitJournalScopes.GetValue(session, static source =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                source.PersistentAccountIdentity ?? throw new InvalidOperationException("Bybit account identity is not verified.")))));
        return "bybit|" + profileId + "|" + scope;
    }

    /// <summary>One UTA account per connection. Never mutate old credentials out from under owned requests/protection.</summary>
    public BybitTradingSession BybitSessionFor(ConnectionProfile profile)
    {
        lock (_bybitGate)
        {
            if (_bybitClosing || IsShuttingDown) throw new InvalidOperationException("Trading Hub is shutting down; new Bybit account contexts are not admitted.");
            if (_bybitSessions.TryGetValue(profile.Id, out var existing)) return existing;
            string recoveryIdentity = "current-credentials";
            try
            {
                var settings = Connections.BybitSettingsOf(profile);
                recoveryIdentity = settings.ContextIdentity;
                Connections.PreserveBybitContext(profile, settings);
                var retired = _retiredBybitSessions.FirstOrDefault(s => s.ContextIdentity == settings.ContextIdentity && _bybitProfiles[s].Id == profile.Id);
                if (retired != null)
                {
                    _retiredBybitSessions.Remove(retired);
                    retired.Reactivate();
                    _bybitSessions.Add(profile.Id, retired);
                    _bybitRecoveryIssues.RemoveAll(issue => issue.ProfileId == profile.Id &&
                        (issue.ContextIdentity == settings.ContextIdentity || issue.ContextIdentity == "current-credentials"));
                    return retired;
                }
                var session = CreateBybitSession(profile, settings);
                _bybitSessions.Add(profile.Id, session);
                _bybitRecoveryIssues.RemoveAll(issue => issue.ProfileId == profile.Id &&
                    (issue.ContextIdentity == settings.ContextIdentity || issue.ContextIdentity == "current-credentials"));
                session.Start();
                return session;
            }
            catch (Exception ex)
            {
                RecordBybitRegistrationFailure(profile, recoveryIdentity, ex);
                throw;
            }
        }
    }

    /// <summary>UI discovery retains an explicit unavailable account instead of losing all account rows on a private-store failure.</summary>
    public void EnsureBybitAccountRegistered(ConnectionProfile profile)
    {
        bool configured;
        try { configured = Connections.HasTradingCredentials(profile); }
        catch (Exception ex) { RecordBybitRegistrationFailure(profile, "current-credentials", ex); return; }
        if (!configured) return;
        try { BybitSessionFor(profile); }
        catch (Exception) { /* BybitSessionFor records the concrete failure; shutdown admission is deliberately closed. */ }
    }

    private void RecordBybitRegistrationFailure(ConnectionProfile profile, string identity, Exception error)
    {
        lock (_bybitGate)
        {
            if (_bybitClosing || IsShuttingDown) return;
            _bybitRecoveryIssues.RemoveAll(issue => issue.ProfileId == profile.Id && issue.ContextIdentity == identity);
            _bybitRecoveryIssues.Add(new(profile.Id, identity,
                "Bybit account credentials or recovery could not be confirmed. New account creation is blocked; retained contexts remain available for cancellation and native reducing exits. " + error.Message));
        }
    }

    public bool IsCurrentBybitContext(ConnectionProfile profile, BybitTradingSession session)
    {
        if (!Connections.Profiles.Contains(profile) || profile.Kind != ConnectionKind.Bybit || session.IsRetired) return false;
        try { return Connections.BybitContextOf(profile) == session.ContextIdentity; }
        catch (Exception ex) { RecordBybitRegistrationFailure(profile, "current-credentials", ex); return false; }
    }

    private BybitTradingSession CreateBybitSession(ConnectionProfile profile, BybitAccountSettings settings)
    {
        string identity = settings.ContextIdentity;
        var http = Connections.CreatePrivateHttpClient(settings.RequestTimeout);
        BybitTradingSession session;
        try
        {
            session = new BybitTradingSession(settings, BybitStatePath(profile, settings), http,
                (symbol, position, signedQuantity, reduceOnly) =>
                {
                    if (reduceOnly) return null;
                    if (IsShuttingDown)
                        return "Trading Hub is shutting down; ordinary Bybit orders were not sent. Native reducing exits and cancellation remain available.";
                    var current = Connections.Profiles.FirstOrDefault(p => p.Id == profile.Id);
                    if (current == null || current.Kind != ConnectionKind.Bybit || !current.Enabled)
                        return "The Bybit connection or its credential context changed; cancel and native reducing exits remain available.";
                    if (settings.IsReal && !current.AllowRealOrders)
                        return "Real Bybit order permission is disabled. Use a native reducing exit; an ordinary opposite-side order can open exposure after another fill.";
                    try
                    {
                        if (Connections.BybitContextOf(current) != identity)
                            return "The Bybit credential context changed; cancel and native reducing exits remain available.";
                    }
                    catch (Exception ex) { return "The current Bybit credential context could not be confirmed; new exposure was not sent. " + ex.Message; }
                    if (current.MaxOrderQuantity > 0 && Math.Abs(signedQuantity) > current.MaxOrderQuantity && RealOrderPermission.IncreasesExposure(position, signedQuantity))
                        return "Bybit order quantity exceeds this connection's configured maximum.";
                    return RealOrderPermission.Check(settings.IsReal, (current.Name, current.Enabled, current.AllowRealOrders), position, signedQuantity);
                }, ownsHttpClient: true);
        }
        catch { http.Dispose(); throw; }
        session.FillReceived += fill => Journal.Add(BybitJournalId(profile.Id, session),
            new AccountFill("bybit-" + fill.Symbol + "-" + fill.OrderId + "-" + fill.Id, fill.Time, fill.Symbol, fill.Buy, fill.Quantity, fill.Price, 1,
                fill.HasQuoteFee ? fill.Fee!.Value : 0, fill.OrderId));
        _bybitProfiles.Add(session, profile);
        return session;
    }

    public BybitTradingSession? FindBybitContext(string profileId, string contextIdentity)
    {
        lock (_bybitGate)
            return _bybitProfiles.FirstOrDefault(pair => pair.Value.Id == profileId && pair.Key.ContextIdentity == contextIdentity).Key;
    }

    /// <summary>Called before shared broker replacement, so newly created views cannot inherit the retired session.</summary>
    private void ReconfigureKvpProviders(ConnectionProfile profile)
    {
        lock (_bybitGate)
        {
            if (_bybitSessions.TryGetValue(profile.Id, out var session) &&
                (profile.Kind != ConnectionKind.Bybit || !Connections.Profiles.Contains(profile) || session.ContextIdentity != Connections.BybitContextOf(profile)))
            {
                _bybitSessions.Remove(profile.Id);
                session.Retire();
                _retiredBybitSessions.Add(session);
            }
            ReleaseRetiredBybitSessions();
        }
    }
    private void ReleaseRetiredBybitSessions()
    {
        lock (_bybitGate)
        {
            if (_bybitClosing) return;
            for (int i = _retiredBybitSessions.Count - 1; i >= 0; i--)
            {
                var session = _retiredBybitSessions[i];
                if (_realBrokers.Values.Concat(_replacedBrokers).OfType<BybitBroker>().Any(b => ReferenceEquals(b.Session, session) && BrokerInUse(b)) ||
                    !session.TryBeginIdleShutdown(out var drain)) continue;
                _retiredBybitSessions.RemoveAt(i);
                var profile = _bybitProfiles[session];
                _bybitRetirementTasks[session] = Task.Run(async () =>
                {
                    await drain.ConfigureAwait(false);
                    lock (_bybitGate)
                    {
                        _bybitProfiles.Remove(session);
                        bool stillSelected = _bybitProfiles.Any(pair => pair.Value.Id == profile.Id && pair.Key.ContextIdentity == session.ContextIdentity);
                        if (!stillSelected) Connections.ForgetIdleBybitContext(profile, session.ContextIdentity);
                        _bybitRetirementTasks.Remove(session);
                    }
                });
            }
        }
    }
    private async Task ShutdownKvpProvidersAsync()
    {
        (ConnectionProfile Profile, BybitTradingSession Session, bool Idle)[] contexts;
        Task[] retiring;
        lock (_bybitGate)
        {
            _bybitClosing = true;
            contexts = _bybitProfiles.Select(pair => (pair.Value, pair.Key, !pair.Key.HasOpenPositionsOrOrders)).ToArray();
            retiring = _bybitRetirementTasks.Values.ToArray();
        }
        // Fill callbacks, private requests, manual refreshes and leases drain before maps or private credential entries leave.
        await Task.WhenAll(contexts.Select(async context => await context.Session.DisposeAsync().ConfigureAwait(false))).ConfigureAwait(false);
        await Task.WhenAll(retiring).ConfigureAwait(false);
        lock (_bybitGate)
        {
            foreach (var group in contexts.GroupBy(context => (context.Profile.Id, context.Session.ContextIdentity)))
                if (group.All(context => context.Idle))
                    Connections.ForgetIdleBybitContext(group.First().Profile, group.Key.ContextIdentity);
            _bybitSessions.Clear();
            _retiredBybitSessions.Clear();
            _bybitProfiles.Clear();
            _bybitRetirementTasks.Clear();
        }
    }
}
