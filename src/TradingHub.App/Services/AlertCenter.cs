using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using TradingHub.Common;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>
/// Local alerts and an opt-in bounded durable external outbox. Raise never performs I/O, reads secrets,
/// awaits network or calls subscribers. Delivery is best-effort with bounded retries; an ambiguous acknowledgment may duplicate.
/// A successful HTTP response is endpoint acceptance, not proof of human receipt.
/// Workflow inspired by KVP commit 50220ba91613161685f84384825d855cb4dbeb76; persistence/consent/lifecycle redesigned.
/// </summary>
public sealed class AlertCenter : IAsyncDisposable
{
    public const int MaximumPending = 500;
    private const int MaximumRecent = 300, MaximumDeliveries = 1000, MaximumSeenIds = 4096, MaximumAttempts = 5;
    private readonly string _path;
    private readonly ISecretStore _secrets;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _deliveryWake = new(0, 1);
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<AlertEvent> _incoming = Channel.CreateBounded<AlertEvent>(new BoundedChannelOptions(512)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Dictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeIds = new(StringComparer.Ordinal);
    private readonly NotificationRedactor _redactor = new();
    private readonly SemaphoreSlim _privateGate = new(1, 1);
    private HashSet<Task>? _legacyPrivateOperations;
    private readonly Dictionary<string, NotificationCredential> _credentials = new(StringComparer.Ordinal);
    private readonly NotificationState _diskState = new();
    private readonly Dictionary<string, (AlertEvent Source, AlertEvent Metadata)> _metadataEvents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (NotificationDelivery Source, NotificationDelivery Stored)> _diskDeliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AlertEvent> _heldContent = new(StringComparer.Ordinal);
    private readonly List<DeferredExternalAlert> _deferredExternal = new();
    private CancellationTokenSource? _refreshCancellation;
    private volatile bool _redactionReady = true;
    private bool _redactionBlockedForSession;
    private const string PrivateWebhookLabel = "Private webhook";
    private readonly Task _worker;
    private readonly Task _deliveryWorker;
    private NotificationState _state = new();
    private string _status = "Local alerts ready. External delivery is off until explicitly enabled.";
    private bool _unreadable, _dirty, _stopped;
    private int _dropped;
    private long _pendingIntake;
    private readonly FileStream? _lease;
    private Task? _disposeTask;

    public AlertCenter(string statePath, ISecretStore secrets, HttpClient? http = null, Func<DateTime>? utcNow = null)
    {
        _path = Path.GetFullPath(statePath);
        _secrets = secrets;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _ownsHttp = http == null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _lease = new FileStream(_path + ".lease", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Load();
            _redactionReady = !_state.Destinations.Any(d => d.Enabled);
            if (!_redactionReady)
            {
                for (int i = 0; i < _state.Recent.Count; i++)
                {
                    var alert = _state.Recent[i];
                    _heldContent.TryAdd(alert.Id, alert);
                    _state.Recent[i] = Withheld(alert);
                }
                for (int i = 0; i < _state.Deliveries.Count; i++)
                {
                    var delivery = _state.Deliveries[i];
                    _heldContent.TryAdd(delivery.Alert.Id, delivery.Alert);
                    _state.Deliveries[i] = delivery with { Alert = Withheld(delivery.Alert) };
                }
                _dirty = true;
            }
        }
        catch
        {
            _unreadable = true;
            _status = "Alert storage is unavailable or already owned by another process. Local alerts remain available; external sends are blocked.";
        }
        _worker = Task.Run(WorkAsync);
        _deliveryWorker = Task.Run(DeliveryWorkAsync);
    }

    public event Action<AlertEvent>? Raised;
    public event Action? Changed;
    public static string SecretAccount(string destinationId, string revision) => "notification:" + destinationId + ":" + revision;
    public NotificationSnapshot Snapshot()
    {
        lock (_gate)
        {
            int notDurable = _deferredExternal.Count + _state.Deliveries.Count(d => d.State == NotificationDeliveryState.AwaitingPersistence);
            return new(_state.Recent.ToArray(), _state.Destinations.Select(d => d with { Kinds = d.Kinds.ToArray() }).ToArray(),
                _state.Deliveries.ToArray(), _status + (notDurable > 0 ? $" · {notDurable} intake items are NOT YET DURABLE." : ""),
                Volatile.Read(ref _dropped), notDurable);
        }
    }

    /// <summary>Accepts bounded local intake. Flush persists committed state; withheld intake is explicitly not yet durable.</summary>
    public bool Raise(AlertKind kind, string instrument, string source, string message, string? id = null, DateTime? timeUtc = null, bool allowExternal = true)
    {
        if (Volatile.Read(ref _stopped)) return false;
        id ??= Guid.NewGuid().ToString("N");
        if (!ValidId(id) || !Enum.IsDefined(kind)) { Interlocked.Increment(ref _dropped); return false; }
        bool oversizedUnready = !_redactionReady && ((instrument?.Length ?? 0) > 128 || (source?.Length ?? 0) > 128 || (message?.Length ?? 0) > 2048);
        var alert = oversizedUnready
            ? new AlertEvent(id, timeUtc ?? _utcNow(), kind, "", "Alert center", "Oversized content was withheld before private redaction was available; it will not be forwarded.", false)
            : new AlertEvent(id, timeUtc ?? _utcNow(), kind, _redactor.Bounded(instrument, 128), _redactor.Bounded(source, 128), _redactor.Bounded(message, 2048), allowExternal);
        // Count before publication so a concurrent FlushAsync cannot miss an intake operation.
        Interlocked.Increment(ref _pendingIntake);
        if (!_incoming.Writer.TryWrite(alert))
        {
            Interlocked.Decrement(ref _pendingIntake);
            Interlocked.Increment(ref _dropped);
            return false;
        }
        Wake();
        return true;
    }

    public IDisposable Attach(Strategy strategy)
    {
        void Receive(StrategyAlert alert) => Raise(AlertKind.Strategy, alert.Instrument, alert.Source, alert.Message, alert.Id, alert.TimeUtc);
        strategy.AlertRaised += Receive;
        return new Subscription(() => strategy.AlertRaised -= Receive);
    }

    private static bool ValidId(string id) => id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
    private NotificationCredential? ReadCredential(NotificationDestination destination)
    {
        lock (_gate) return _credentials.GetValueOrDefault(SecretAccount(destination.Id, destination.Revision));
    }
    private async Task<string> GetSecretAsync(string account, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(3));
        bounded.Token.ThrowIfCancellationRequested();
        return _secrets is IAsyncSecretStore asynchronous
            ? await asynchronous.GetAsync(account, bounded.Token).ConfigureAwait(false)
            : await TrackLegacy(Task.Run(() => _secrets.Get(account), CancellationToken.None)).WaitAsync(bounded.Token).ConfigureAwait(false);
    }
    private async Task SetSecretAsync(string account, string? value, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(3));
        bounded.Token.ThrowIfCancellationRequested();
        if (_secrets is IAsyncSecretStore asynchronous) await asynchronous.SetAsync(account, value, bounded.Token).ConfigureAwait(false);
        else await TrackLegacy(Task.Run(() => _secrets.Set(account, value), CancellationToken.None)).WaitAsync(bounded.Token).ConfigureAwait(false);
    }
    private Task TrackLegacy(Task operation)
    {
        lock (_gate) (_legacyPrivateOperations ??= new()).Add(operation);
        _ = operation.ContinueWith(completed => { lock (_gate) _legacyPrivateOperations?.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return operation;
    }
    private Task<T> TrackLegacy<T>(Task<T> operation) { TrackLegacy((Task)operation); return operation; }
    private async Task<NotificationCredential?> LoadCredentialAsync(NotificationDestination destination, CancellationToken cancellationToken)
    {
        try
        {
            string stored = await GetSecretAsync(SecretAccount(destination.Id, destination.Revision), cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(stored) ? null : JsonSerializer.Deserialize<NotificationCredential>(stored);
        }
        catch (OperationCanceledException) { throw; }
        catch { throw new InvalidOperationException("Private notification material is unavailable."); }
    }
    private static AlertEvent Withheld(AlertEvent alert) => alert with {
        Instrument = "", Source = "Alert center", Message = "Alert content is withheld while private notification redaction is unavailable.", AllowExternal = false };
    private void DropWithheldLocked()
    {
        _heldContent.Clear();
        _deferredExternal.Clear();
    }
    private void RedactHeldLocked()
    {
        // Once a private value is known, retirement or bounded preview history must never reveal it again.
        foreach (var held in _heldContent.ToArray())
        {
            var safe = _redactor.Redact(held.Value);
            if (!ReferenceEquals(safe, held.Value)) _heldContent[held.Key] = safe;
        }
        for (int i = 0; i < _deferredExternal.Count; i++)
        {
            var deferred = _deferredExternal[i];
            var safe = _redactor.Redact(deferred.Alert);
            if (!ReferenceEquals(safe, deferred.Alert)) _deferredExternal[i] = deferred with { Alert = safe };
        }
    }
    private NotificationDelivery UnlockLocked(NotificationDelivery delivery)
    {
        if (delivery.PlaintextReady) return delivery with { Alert = _redactor.Redact(delivery.Alert) };
        var credential = _credentials.GetValueOrDefault(SecretAccount(delivery.DestinationId, delivery.Revision));
        if (credential is not { Enabled: true }) return delivery;
        try { return delivery with { Alert = _redactor.Redact(NotificationPayload.Open(delivery, credential)), PlaintextReady = true }; }
        catch { return delivery with { State = NotificationDeliveryState.Failed, Status = "Protected notification content could not be authenticated. Nothing was sent." }; }
    }
    private void ReleaseWithheldLocked()
    {
        var recovered = new Dictionary<string, AlertEvent>(StringComparer.Ordinal);
        for (int i = 0; i < _state.Deliveries.Count; i++)
        {
            var delivery = UnlockLocked(_state.Deliveries[i]);
            _state.Deliveries[i] = delivery;
            if (delivery.PlaintextReady && delivery.Alert.OutboxEventId is { } id) recovered[id] = delivery.Alert;
        }
        for (int i = 0; i < _state.Recent.Count; i++)
        {
            var recent = _state.Recent[i];
            if (recent.OutboxEventId is { } id && recovered.TryGetValue(id, out var restored)) recent = restored;
            else if (_heldContent.TryGetValue(recent.Id, out var held)) recent = _redactor.Redact(held);
            else recent = _redactor.Redact(recent);
            _state.Recent[i] = recent;
        }
        foreach (var deferred in _deferredExternal) QueueExternalLocked(_redactor.Redact(deferred.Alert), deferred.Grants);
        _deferredExternal.Clear();
        _heldContent.Clear();
        TrimLocked();
        _dirty = true;
    }
    /// <summary>Refreshes missing private material on a bounded cancellable background path. No private I/O holds the local alert lock.</summary>
    public async Task<bool> RefreshPrivateMaterialAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) if (_redactionBlockedForSession || _redactionReady || _stopped) return _redactionReady && !_stopped && !_redactionBlockedForSession;
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _privateGate.WaitAsync(refresh.Token).ConfigureAwait(false);
        try
        {
            NotificationDestination[] destinations;
            lock (_gate)
            {
                if (_redactionBlockedForSession || _redactionReady || _stopped) return _redactionReady && !_stopped && !_redactionBlockedForSession;
                _refreshCancellation = refresh;
                destinations = _state.Destinations.Where(d => d.Enabled).ToArray();
            }
            bool ready = true;
            foreach (var destination in destinations)
            {
                lock (_gate)
                {
                    if (!_state.Destinations.Any(d => ReferenceEquals(d, destination) && d.Enabled)) continue;
                    if (_credentials.ContainsKey(SecretAccount(destination.Id, destination.Revision))) continue;
                }
                NotificationCredential? credential;
                try { credential = await LoadCredentialAsync(destination, refresh.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (refresh.IsCancellationRequested) { return false; }
                catch { ready = false; continue; }
                if (credential is not { Enabled: true } || !NotificationPayload.HasKey(credential) || !NotificationPayload.ConsentsTo(credential, destination) ||
                    (destination.Kind == NotificationDestinationKind.Webhook &&
                        (!Uri.TryCreate(credential.FullWebhookUri, UriKind.Absolute, out var privateUri) || privateUri.Scheme != Uri.UriSchemeHttps ||
                         privateUri.UserInfo.Length != 0 || privateUri.Fragment.Length != 0)) ||
                    (destination.Kind == NotificationDestinationKind.Telegram && string.IsNullOrWhiteSpace(credential.Authorization)))
                {
                    ready = false;
                    lock (_gate)
                    {
                        if (!_state.Destinations.Any(d => ReferenceEquals(d, destination) && d.Enabled)) continue;
                        // A completed lookup without valid consent is distinct from unavailable private I/O.
                        // Retain the protected content, but do not leave a known unauthorized send due.
                        bool changed = false;
                        for (int i = 0; i < _state.Deliveries.Count; i++)
                        {
                            var delivery = _state.Deliveries[i];
                            if (delivery.DestinationId != destination.Id || delivery.Revision != destination.Revision ||
                                delivery.State is not (NotificationDeliveryState.Pending or NotificationDeliveryState.AwaitingPersistence)) continue;
                            _state.Deliveries[i] = delivery with { State = NotificationDeliveryState.Failed,
                                Status = "Destination consent is missing or revoked in the secret store; no HTTP request was sent." };
                            changed = true;
                        }
                        if (changed) { _dirty = true; PersistLocked(); }
                    }
                    continue;
                }
                lock (_gate)
                    if (_state.Destinations.Any(d => ReferenceEquals(d, destination) && d.Enabled))
                    {
                        _credentials[SecretAccount(destination.Id, destination.Revision)] = credential;
                        _redactor.SetActive(_credentials.Values);
                        RedactHeldLocked();
                    }
            }
            lock (_gate)
            {
                if (_redactionBlockedForSession || refresh.IsCancellationRequested || _stopped) return false;
                ready &= _state.Destinations.Where(d => d.Enabled).All(d => _credentials.ContainsKey(SecretAccount(d.Id, d.Revision)));
                if (ready)
                {
                    _redactionReady = true;
                    ReleaseWithheldLocked();
                    _status = "Private redaction material is ready. External sends still require durable explicit permission.";
                    PersistLocked();
                }
                else _status = "Private notification material is unavailable. Local alerts continue with withheld content; unsafe content is neither persisted nor sent.";
            }
            NotifyChanged();
            return ready;
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_refreshCancellation, refresh)) _refreshCancellation = null;
            _privateGate.Release();
        }
    }
    private void Wake()
    {
        Signal(_wake);
        Signal(_deliveryWake);
    }
    private static void Signal(SemaphoreSlim signal)
    {
        try { signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length > 32 * 1024 * 1024) throw new InvalidDataException();
            _state = JsonSerializer.Deserialize<NotificationState>(File.ReadAllText(_path)) ?? throw new InvalidDataException();
            if (_state.Version != 3 || _state.Recent == null || _state.Destinations == null || _state.Deliveries == null || _state.SeenIds == null
                || _state.Recent.Count > MaximumRecent || _state.Destinations.Count > 8 || _state.Deliveries.Count > MaximumDeliveries
                || _state.SeenIds.Count > MaximumSeenIds
                || _state.Deliveries.Count(d => d.State is NotificationDeliveryState.Pending or NotificationDeliveryState.Delivering or NotificationDeliveryState.AwaitingPersistence) > MaximumPending)
                throw new InvalidDataException();
            if (_state.Deliveries.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != _state.Deliveries.Count
                || _state.Destinations.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != _state.Destinations.Count)
                throw new InvalidDataException();
            foreach (var destination in _state.Destinations)
            {
                Validate(destination.Kind, destination.Kind == NotificationDestinationKind.Webhook ? "https://private.invalid" : "",
                    destination.Target, destination.Kinds);
                if (!ValidId(destination.Id) || !ValidId(destination.Revision)) throw new InvalidDataException();
                if (destination.Kind == NotificationDestinationKind.Webhook && destination.Endpoint != PrivateWebhookLabel)
                    throw new InvalidDataException();
            }
            for (int i = 0; i < _state.Deliveries.Count; i++)
            {
                var delivery = _state.Deliveries[i];
                if (!ValidId(delivery.Alert.Id) || delivery.Attempts is < 0 or > MaximumAttempts || !Enum.IsDefined(delivery.State))
                    throw new InvalidDataException();
                delivery = delivery with { Alert = NotificationPayload.Metadata(delivery.Alert) };
                _state.Deliveries[i] = delivery;
                if (!NotificationPayload.IsBounded(delivery.EncryptedAlert))
                {
                    _state.Deliveries[i] = delivery with { State = NotificationDeliveryState.Failed, Status = "Protected notification content is missing or invalid. Nothing was sent." };
                    continue;
                }
                if (delivery.State is NotificationDeliveryState.Delivering or NotificationDeliveryState.AwaitingPersistence)
                    _state.Deliveries[i] = delivery with { State = delivery.Attempts >= MaximumAttempts ? NotificationDeliveryState.Failed : NotificationDeliveryState.Pending,
                        Status = "Restarted after an unconfirmed send; a retry can duplicate.", NextAttemptUtc = _utcNow() };
            }
            _dirty = true;
            _status = "Restored local history and pending delivery. Endpoint acceptance does not prove human receipt.";
        }
        catch
        {
            _state = new();
            _unreadable = true;
            _status = "Alert storage could not be read. The original file is preserved; external delivery is blocked.";
        }
    }

    private static void Validate(NotificationDestinationKind kind, string endpoint, string target, AlertKind[] kinds)
    {
        if (!Enum.IsDefined(kind) || kinds == null || kinds.Length == 0 || kinds.Any(k => !Enum.IsDefined(k)))
            throw new ArgumentException("Select at least one supported alert kind.");
        if (kind == NotificationDestinationKind.Webhook && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0))
            throw new ArgumentException("Webhook needs an HTTPS URI without user information or a fragment. Its full target is stored privately.");
        if (kind == NotificationDestinationKind.Telegram && (string.IsNullOrWhiteSpace(target) || target.Length > 128
            || target.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '@'))))
            throw new ArgumentException("Telegram needs a valid chat ID or channel name.");
        if (endpoint.Length > 2048) throw new ArgumentException("Notification endpoint is too long.");
    }

    /// <summary>Only a confirmed private write and durable configuration publish a new external permission.</summary>
    public async Task<NotificationDestination> ConfigureAsync(string? id, NotificationDestinationKind kind, string endpoint, string target,
        string? secret, bool enabled, AlertKind[] kinds, CancellationToken cancellationToken = default)
    {
        NotificationDestination? old;
        bool withdrawing, saved = false;
        lock (_gate)
        {
            ThrowIfStopped();
            old = _state.Destinations.FirstOrDefault(d => d.Id == id);
            withdrawing = !enabled && old != null;
            if (withdrawing) saved = DisableLocalLocked(old!);
        }
        if (withdrawing)
        {
            await FinishWithdrawalAsync(old!, saved).ConfigureAwait(false);
            lock (_gate) return _state.Destinations.Single(d => d.Id == old!.Id);
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _privateGate.WaitAsync(operation.Token).ConfigureAwait(false);
        string? freshAccount = null;
        bool published = false;
        try
        {
            NotificationCredential? previous;
            lock (_gate)
            {
                ThrowIfStopped();
                if (_unreadable) throw new InvalidOperationException(_status);
                if (!ReferenceEquals(old, _state.Destinations.FirstOrDefault(d => d.Id == id)))
                    throw new InvalidOperationException("Destination changed while permission was being configured; review it again.");
                if (old == null && _state.Destinations.Count >= 8) throw new InvalidOperationException("At most eight notification destinations are supported.");
                previous = old == null ? null : ReadCredential(old);
            }
            if (old != null && previous == null && (secret == null || string.IsNullOrWhiteSpace(endpoint)))
                previous = await LoadCredentialAsync(old, operation.Token).ConfigureAwait(false);
            string fullTarget = kind == NotificationDestinationKind.Webhook
                ? string.IsNullOrWhiteSpace(endpoint) ? previous?.FullWebhookUri ?? "" : endpoint : "";
            Validate(kind, fullTarget, target, kinds);
            string value = secret ?? previous?.Authorization ?? "";
            if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("Authorization must be one line.");
            if (kind == NotificationDestinationKind.Telegram && enabled && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Telegram needs a token in the private store before enabling.");
            if (value.Length > 4096 || (kind == NotificationDestinationKind.Telegram &&
                (value.Length > 256 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not (':' or '_' or '-')))))
                throw new ArgumentException("Notification credential has an unsupported length or format.");
            var destination = new NotificationDestination(old?.Id ?? Guid.NewGuid().ToString("N"), kind,
                kind == NotificationDestinationKind.Webhook ? PrivateWebhookLabel : "Telegram", kind == NotificationDestinationKind.Telegram ? target : "",
                enabled, Guid.NewGuid().ToString("N"), kinds.Distinct().ToArray());
            var credential = new NotificationCredential(value, enabled, kind == NotificationDestinationKind.Webhook ? fullTarget : null,
                NotificationPayload.NewKey(), NotificationPayload.ConfigurationOf(destination));
            _redactor.Remember(credential);
            freshAccount = SecretAccount(destination.Id, destination.Revision);
            string encoded = JsonSerializer.Serialize(credential);
            try
            {
                await SetSecretAsync(freshAccount, encoded, operation.Token).ConfigureAwait(false);
                if (!string.Equals(await GetSecretAsync(freshAccount, operation.Token).ConfigureAwait(false), encoded, StringComparison.Ordinal))
                    throw new InvalidOperationException();
            }
            catch
            {
                lock (_gate) _status = "The private store did not confirm notification permission. Destination configuration was not changed.";
                throw new InvalidOperationException("The private store did not confirm notification permission. Destination configuration was not changed.");
            }
            lock (_gate)
            {
                ThrowIfStopped();
                operation.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(old, _state.Destinations.FirstOrDefault(d => d.Id == id)))
                    throw new InvalidOperationException("Permission was withdrawn or changed while configuring; no new permission was published.");
                _state.Destinations.RemoveAll(d => d.Id == destination.Id);
                _state.Destinations.Add(destination);
                _dirty = true;
                if (!PersistLocked())
                {
                    _state.Destinations.Remove(destination);
                    if (old != null) _state.Destinations.Add(old);
                    _dirty = true;
                    throw new InvalidOperationException(_status);
                }
                if (old != null)
                {
                    if (old.Enabled && !_redactionReady && !_credentials.ContainsKey(SecretAccount(old.Id, old.Revision)))
                    { DropWithheldLocked(); _redactionBlockedForSession = true; }
                    RetireLocked(old.Id, "Destination changed; old-permission pending deliveries were cancelled.");
                    if (_credentials.Remove(SecretAccount(old.Id, old.Revision), out var retired))
                    {
                        _redactor.Remember(retired);
                        RedactHeldLocked();
                    }
                }
                _credentials[freshAccount] = credential;
                _redactor.SetActive(_credentials.Values);
                RedactHeldLocked();
                _redactionReady = !_redactionBlockedForSession && _state.Destinations.Where(d => d.Enabled).All(d => _credentials.ContainsKey(SecretAccount(d.Id, d.Revision)));
                if (_redactionReady) ReleaseWithheldLocked();
                _dirty = true;
                PersistLocked();
                published = true;
                _status = !_redactionReady ? "Permission saved; content and delivery are withheld until private redaction is ready. If unavailable permission was revoked, restart after confirming withdrawal."
                    : enabled ? "External destination saved with explicit permission. Pending sends are durable and may duplicate." : "Destination saved disabled; alerts stay local.";
            }
            if (old != null)
                try
                {
                    string retired = SecretAccount(old.Id, old.Revision);
                    await SetSecretAsync(retired, null, operation.Token).ConfigureAwait(false);
                    if ((await GetSecretAsync(retired, operation.Token).ConfigureAwait(false)).Length != 0)
                        throw new InvalidOperationException();
                }
                catch { lock (_gate) _status = "Destination saved, but retired private credential deletion was not confirmed."; }
            Wake();
            NotifyChanged();
            return destination;
        }
        finally
        {
            if (!published && freshAccount != null)
                try { await SetSecretAsync(freshAccount, null, CancellationToken.None).ConfigureAwait(false); } catch { }
            _privateGate.Release();
            NotifyChanged();
        }
    }

    /// <summary>Local withdrawal happens before any await and cannot be cancelled into continued consent.</summary>
    public async Task RevokeAsync(string destinationId, CancellationToken cancellationToken = default)
    {
        NotificationDestination? destination;
        bool saved;
        lock (_gate)
        {
            ThrowIfStopped();
            destination = _state.Destinations.FirstOrDefault(d => d.Id == destinationId);
            if (destination == null) return;
            saved = DisableLocalLocked(destination);
        }
        await FinishWithdrawalAsync(destination, saved).ConfigureAwait(false);
    }
    private bool DisableLocalLocked(NotificationDestination destination)
    {
        bool wasReady = _redactionReady;
        bool missingRetiredMaterial = destination.Enabled && !_credentials.ContainsKey(SecretAccount(destination.Id, destination.Revision));
        if (!_redactionReady && missingRetiredMaterial) _redactionBlockedForSession = true;
        _refreshCancellation?.Cancel();
        RetireLocked(destination.Id, "Permission withdrawn locally. A remote acceptance cannot be recalled.");
        _state.Destinations.Remove(destination);
        _state.Destinations.Add(destination with { Enabled = false });
        if (_credentials.Remove(SecretAccount(destination.Id, destination.Revision), out var retired))
        {
            _redactor.Remember(retired);
            RedactHeldLocked();
            _redactor.SetActive(_credentials.Values);
        }
        if (_redactionBlockedForSession) DropWithheldLocked();
        _redactionReady = !_redactionBlockedForSession && _state.Destinations.Where(d => d.Enabled).All(d => _credentials.ContainsKey(SecretAccount(d.Id, d.Revision)));
        if (!wasReady && _redactionReady) ReleaseWithheldLocked();
        _dirty = true;
        _status = "Destination is OFF locally; confirming durable withdrawal. Private target edits were not applied.";
        bool saved = PersistLocked();
        NotifyChanged();
        return saved;
    }
    private async Task FinishWithdrawalAsync(NotificationDestination destination, bool saved)
    {
        bool deleted = false;
        try
        {
            await _privateGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                string account = SecretAccount(destination.Id, destination.Revision);
                await SetSecretAsync(account, null, _shutdown.Token).ConfigureAwait(false);
                deleted = (await GetSecretAsync(account, _shutdown.Token).ConfigureAwait(false)).Length == 0;
            }
            finally { _privateGate.Release(); }
        }
        catch { }
        lock (_gate)
            _status = saved && deleted
                ? _redactionBlockedForSession ? "Destination is off and its private material was deleted. Content that could not be safely redacted remains withheld until restart."
                    : "Destination is off. Private target and credentials were revoked; re-enter them before enabling again."
                : "Destination is OFF in this process, but durable revocation was not confirmed. Restore storage and revoke again before restarting; a remote acceptance cannot be recalled.";
        NotifyChanged();
        if (!saved || !deleted) throw new InvalidOperationException("Destination is OFF in this process, but durable revocation was not confirmed. Restore storage and revoke again before restarting.");
    }

    /// <summary>Explicit in-memory review only; never used by Snapshot, Preview, ordinary settings or logs.</summary>
    public string ReviewPrivateTarget(string destinationId)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            var destination = _state.Destinations.Single(d => d.Id == destinationId);
            var credential = ReadCredential(destination);
            if (destination.Kind != NotificationDestinationKind.Webhook || string.IsNullOrWhiteSpace(credential?.FullWebhookUri))
                throw new InvalidOperationException("No private webhook target is stored. Re-enter it before enabling.");
            return credential.FullWebhookUri;
        }
    }

    private void RetireLocked(string destinationId, string status)
    {
        if (_active.TryGetValue(destinationId, out var active)) active.Cancel();
        foreach (var deferred in _deferredExternal) deferred.Grants.RemoveAll(grant => grant.DestinationId == destinationId);
        for (int i = 0; i < _state.Deliveries.Count; i++)
            if (_state.Deliveries[i].DestinationId == destinationId && _state.Deliveries[i].State is NotificationDeliveryState.Pending or NotificationDeliveryState.Delivering or NotificationDeliveryState.AwaitingPersistence)
                _state.Deliveries[i] = _state.Deliveries[i] with { State = NotificationDeliveryState.Cancelled, Status = status };
    }

    public void CancelDelivery(string deliveryId)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            int index = _state.Deliveries.FindIndex(d => d.Id == deliveryId);
            if (index < 0) return;
            var delivery = _state.Deliveries[index];
            if (delivery.State is NotificationDeliveryState.Delivered or NotificationDeliveryState.Cancelled) return;
            if (_activeIds.GetValueOrDefault(delivery.DestinationId) == delivery.Id && _active.TryGetValue(delivery.DestinationId, out var active)) active.Cancel();
            _state.Deliveries[index] = delivery with { State = NotificationDeliveryState.Cancelled,
                Status = "Cancelled locally. Remote acceptance, if already in flight, cannot be recalled." };
            _dirty = true;
            if (!PersistLocked())
                _status += " Cancellation is currently only in memory; an older pending delivery may resume after restart unless storage recovers.";
        }
        NotifyChanged();
    }

    public bool Retry(string deliveryId)
    {
        lock (_gate)
        {
            ThrowIfStopped();
            int index = _state.Deliveries.FindIndex(d => d.Id == deliveryId);
            if (index < 0) return false;
            var delivery = _state.Deliveries[index];
            if (!NotificationPayload.IsBounded(delivery.EncryptedAlert) || delivery.State is not (NotificationDeliveryState.Failed or NotificationDeliveryState.Pending)
                || !_state.Destinations.Any(d => d.Id == delivery.DestinationId && d.Revision == delivery.Revision && d.Enabled)) return false;
            _state.Deliveries[index] = delivery with { State = NotificationDeliveryState.Pending, Attempts = 0, NextAttemptUtc = _utcNow(),
                Status = "Explicit retry queued with the original delivery ID; duplicates remain possible." };
            _dirty = true;
            if (!PersistLocked()) { ReplaceLocked(delivery); return false; }
        }
        Wake();
        NotifyChanged();
        return true;
    }

    public NotificationPreview Preview(NotificationDestination destination, AlertEvent alert)
    {
        lock (_gate)
        {
            if (destination.Kind == NotificationDestinationKind.Webhook && Uri.TryCreate(destination.Endpoint, UriKind.Absolute, out _))
                _redactor.Remember(new("", false, destination.Endpoint));
            var safe = _redactionReady ? _redactor.Redact(alert) : Withheld(alert);
            string target = destination.Kind == NotificationDestinationKind.Telegram ? "Telegram · " + destination.Target + " · token [redacted]" : PrivateWebhookLabel;
            return new(target, "[private authorization and tokens are omitted from this preview]",
                destination.Kind == NotificationDestinationKind.Telegram ? TelegramPayload(destination, safe) : Payload(safe),
                alert.AllowExternal ? "Opt-in sends this content externally. Retries may duplicate; HTTP acceptance is not human receipt."
                    : "This practice event is local-only and will never be queued externally. Its historical timestamp is omitted from this preview.");
        }
    }

    public static string Text(AlertEvent alert) => $"[{Product.Name}] {alert.Instrument} · {alert.Source}: {alert.Message}";
    private static string Payload(AlertEvent alert) => JsonSerializer.Serialize(new { app = Product.Name, id = alert.Id,
        time = alert.AllowExternal ? (DateTime?)alert.TimeUtc : null, kind = alert.Kind.ToString().ToLowerInvariant(), instrument = alert.Instrument, source = alert.Source, message = alert.Message, text = Text(alert) });
    private static string TelegramPayload(NotificationDestination destination, AlertEvent alert) =>
        JsonSerializer.Serialize(new { chat_id = destination.Target, text = Text(alert) + "\nAlert ID: " + alert.Id });

    private void Accept(AlertEvent value)
    {
        AlertEvent? accepted = null;
        lock (_gate)
        {
            string eventId = NotificationPayload.OpaqueId(value.Id);
            value = value with { OutboxEventId = eventId };
            var alert = _redactionReady ? _redactor.Redact(value) : Withheld(value);
            if (_state.SeenIds.Contains(eventId, StringComparer.Ordinal)) return;
            _state.SeenIds.Add(eventId);
            if (_state.SeenIds.Count > MaximumSeenIds) _state.SeenIds.RemoveAt(0);
            if (!_redactionReady)
            {
                if (_deferredExternal.Count < MaximumRecent)
                {
                    _heldContent[value.Id] = value;
                    // Eligibility belongs to this acceptance, never to a later configuration or consent revision.
                    var grants = value.AllowExternal
                        ? _state.Destinations.Where(d => d.Enabled && d.Kinds.Contains(value.Kind)).Select(d => new ExternalGrant(d.Id, d.Revision)).ToList()
                        : new List<ExternalGrant>();
                    _deferredExternal.Add(new(value, grants));
                    _status = "Alert content is held in bounded memory and is NOT YET DURABLE until private redaction and encryption material are ready.";
                }
                else Interlocked.Increment(ref _dropped);
            }
            _state.Recent.Add(alert);
            if (_state.Recent.Count > MaximumRecent) _state.Recent.RemoveAt(0);
            if (_redactionReady) QueueExternalLocked(alert);
            TrimLocked();
            _dirty = true;
            accepted = alert;
        }
        if (accepted != null && Raised != null)
            foreach (Action<AlertEvent> handler in Raised.GetInvocationList()) { try { handler(accepted); } catch { } }
        NotifyChanged();
    }
    private void QueueExternalLocked(AlertEvent alert, List<ExternalGrant>? capturedGrants = null)
    {
        if (!alert.AllowExternal) return;
        foreach (var destination in _state.Destinations)
        {
            if (!destination.Enabled || !destination.Kinds.Contains(alert.Kind) ||
                (capturedGrants != null && !capturedGrants.Contains(new(destination.Id, destination.Revision)))) continue;
            string deliveryId = NotificationPayload.OpaqueId((alert.OutboxEventId ?? NotificationPayload.OpaqueId(alert.Id)) + ":" + destination.Id);
            if (_state.Deliveries.Any(d => d.Id == deliveryId)) continue;
            if (_state.Deliveries.Count(d => d.State is NotificationDeliveryState.Pending or NotificationDeliveryState.Delivering or NotificationDeliveryState.AwaitingPersistence) >= MaximumPending)
            { _status = "External pending queue is full. This alert remains local; no external delivery was queued."; break; }
            try
            {
                var credential = ReadCredential(destination) ?? throw new InvalidOperationException();
                alert = _redactor.Redact(alert);
                var encrypted = NotificationPayload.Seal(alert, destination.Id, destination.Revision, deliveryId, credential);
                _state.Deliveries.Add(new(deliveryId, destination.Id, destination.Revision, alert,
                    NotificationDeliveryState.AwaitingPersistence, 0, _utcNow(), "Encrypted content awaits storage and is NOT YET DURABLE.", encrypted) { PlaintextReady = true });
            }
            catch
            {
                _state.Deliveries.Add(new(deliveryId, destination.Id, destination.Revision, alert,
                    NotificationDeliveryState.Failed, 0, _utcNow(), "Protected pending content could not be created. Nothing was queued or sent."));
                _status = "The local alert was recorded, but its external content is not durable and was not queued.";
            }
        }
    }

    private void TrimLocked()
    {
        while (_state.Deliveries.Count > MaximumDeliveries)
        {
            int index = _state.Deliveries.FindIndex(d => d.State is NotificationDeliveryState.Delivered or NotificationDeliveryState.Failed or NotificationDeliveryState.Cancelled);
            if (index < 0) break;
            _diskDeliveries.Remove(_state.Deliveries[index].Id);
            _state.Deliveries.RemoveAt(index);
        }
        if (_metadataEvents.Count > MaximumRecent + MaximumDeliveries + 64)
        {
            var retained = _state.Recent.Select(a => a.OutboxEventId ?? NotificationPayload.OpaqueId(a.Id))
                .Concat(_state.Deliveries.Select(d => d.Alert.OutboxEventId ?? NotificationPayload.OpaqueId(d.Alert.Id))).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _metadataEvents.Keys.Where(id => !retained.Contains(id)).ToArray()) _metadataEvents.Remove(id);
        }
    }
    private AlertEvent StoredMetadataLocked(AlertEvent alert)
    {
        string id = alert.OutboxEventId ?? NotificationPayload.OpaqueId(alert.Id);
        if (_metadataEvents.TryGetValue(id, out var cached) && ReferenceEquals(cached.Source, alert)) return cached.Metadata;
        var metadata = NotificationPayload.Metadata(alert);
        _metadataEvents[id] = (alert, metadata);
        return metadata;
    }
    private NotificationState StoredStateLocked()
    {
        _diskState.Destinations = _state.Destinations;
        _diskState.SeenIds = _state.SeenIds;
        _diskState.Recent.Clear();
        foreach (var alert in _state.Recent) _diskState.Recent.Add(StoredMetadataLocked(alert));
        _diskState.Deliveries.Clear();
        foreach (var delivery in _state.Deliveries)
        {
            if (!_diskDeliveries.TryGetValue(delivery.Id, out var cached) || !ReferenceEquals(cached.Source, delivery))
            {
                var stored = delivery with { Alert = StoredMetadataLocked(delivery.Alert),
                    State = delivery.State == NotificationDeliveryState.AwaitingPersistence ? NotificationDeliveryState.Pending : delivery.State,
                    Status = delivery.State == NotificationDeliveryState.AwaitingPersistence ? "Durable encrypted pending delivery." : delivery.Status };
                cached = (delivery, stored);
                _diskDeliveries[delivery.Id] = cached;
            }
            _diskState.Deliveries.Add(cached.Stored);
        }
        return _diskState;
    }

    private bool PersistLocked()
    {
        if (_unreadable) return false;
        if (!_dirty) return true;
        string temporary = _path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, StoredStateLocked());
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            for (int i = 0; i < _state.Deliveries.Count; i++)
                if (_state.Deliveries[i].State == NotificationDeliveryState.AwaitingPersistence)
                    _state.Deliveries[i] = _state.Deliveries[i] with { State = NotificationDeliveryState.Pending, Status = "Durable encrypted pending delivery." };
            _dirty = false;
            return true;
        }
        catch
        {
            _status = "Alert persistence failed. Local history remains in memory; external sends are blocked until storage recovers.";
            try { File.Delete(temporary); } catch { }
            return false;
        }
    }

    private async Task WorkAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                while (_incoming.Reader.TryRead(out var alert))
                {
                    try { Accept(alert); }
                    catch { lock (_gate) _status = "An alert could not be recorded. Trading and protection remain independent."; }
                    finally { Interlocked.Decrement(ref _pendingIntake); }
                }
                bool changed;
                lock (_gate) { changed = _dirty; PersistLocked(); }
                if (changed) NotifyChanged();
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch { lock (_gate) _status = "Notification worker stopped unexpectedly. Pending deliveries remain available for restart."; NotifyChanged(); }
    }

    private async Task DeliveryWorkAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await DeliverPendingAsync(_shutdown.Token).ConfigureAwait(false);
                await _deliveryWake.WaitAsync(TimeSpan.FromSeconds(1), _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch { lock (_gate) _status = "External delivery stopped unexpectedly. Local alerts continue; pending deliveries remain available for restart."; NotifyChanged(); }
    }

    /// <summary>Also permits deterministic fake-clock consumers to run currently due deliveries without sleeping.</summary>
    public async Task DeliverPendingAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        if (!await RefreshPrivateMaterialAsync(linked.Token).ConfigureAwait(false)) return;
        await _deliveryGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                NotificationDelivery delivery;
                NotificationDestination destination;
                CancellationTokenSource active;
                lock (_gate)
                {
                    if (_stopped || !_redactionReady || !PersistLocked()) return;
                    var next = _state.Deliveries.FirstOrDefault(d => d.State == NotificationDeliveryState.Pending && d.NextAttemptUtc <= _utcNow());
                    if (next == null) return;
                    var configured = _state.Destinations.FirstOrDefault(d => d.Id == next.DestinationId && d.Revision == next.Revision && d.Enabled);
                    if (configured == null)
                    {
                        ReplaceLocked(next with { State = NotificationDeliveryState.Cancelled, Status = "Destination is no longer authorized." });
                        continue;
                    }
                    next = UnlockLocked(next);
                    if (next.State == NotificationDeliveryState.Failed)
                    { ReplaceLocked(next); PersistLocked(); continue; }
                    if (!next.PlaintextReady)
                    { _status = "Private content is blocked until its confirmed original consent and encryption key are available."; return; }
                    destination = configured;
                    delivery = next with { State = NotificationDeliveryState.Delivering, Attempts = next.Attempts + 1, Status = "Sending; remote acceptance is not yet known." };
                    ReplaceLocked(delivery);
                    if (!PersistLocked())
                    {
                        ReplaceLocked(next);
                        return;
                    }
                    active = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    active.CancelAfter(TimeSpan.FromSeconds(15));
                    _active[destination.Id] = active;
                    _activeIds[destination.Id] = delivery.Id;
                }
                string status;
                bool success = false, permanent = false;
                try
                {
                    var credential = ReadCredential(destination);
                    if (credential is not { Enabled: true } || !NotificationPayload.ConsentsTo(credential, destination) || (destination.Kind == NotificationDestinationKind.Webhook &&
                        (!Uri.TryCreate(credential.FullWebhookUri, UriKind.Absolute, out var privateUri) || privateUri.Scheme != Uri.UriSchemeHttps || privateUri.UserInfo.Length != 0 || privateUri.Fragment.Length != 0)))
                        throw new NotificationConsentException();
                    active.Token.ThrowIfCancellationRequested();
                    using var request = BuildRequest(destination, delivery, credential);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, active.Token).ConfigureAwait(false);
                    success = response.IsSuccessStatusCode;
                    permanent = (int)response.StatusCode is >= 400 and < 500 && response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
                    status = success ? "Endpoint accepted the request. Human receipt is not confirmed." : "Endpoint returned HTTP " + (int)response.StatusCode + ".";
                    if (success && destination.Kind == NotificationDestinationKind.Telegram)
                    {
                        var acknowledgment = await ReadTelegramAcknowledgmentAsync(response.Content, active.Token).ConfigureAwait(false);
                        success = acknowledgment.Success;
                        permanent = acknowledgment.Permanent;
                        status = success ? "Telegram accepted the message. Human receipt is not confirmed."
                            : "Telegram rejected or did not acknowledge the message (code " + acknowledgment.ErrorCode + ").";
                    }
                }
                catch (NotificationConsentException) { permanent = true; status = "Destination consent is missing or revoked in the secret store; no HTTP request was sent."; }
                catch (OperationCanceledException) { status = "Send cancelled or timed out. Remote acceptance is unknown; retry can duplicate."; }
                catch { status = "Delivery failed. Credential, network and response details are intentionally not logged."; }
                finally
                {
                    lock (_gate) { _active.Remove(destination.Id); _activeIds.Remove(destination.Id); }
                    active.Dispose();
                }
                lock (_gate)
                {
                    var current = _state.Deliveries.FirstOrDefault(d => d.Id == delivery.Id);
                    if (current?.State == NotificationDeliveryState.Delivering)
                    {
                        var state = success ? NotificationDeliveryState.Delivered : permanent || delivery.Attempts >= MaximumAttempts
                            ? NotificationDeliveryState.Failed : NotificationDeliveryState.Pending;
                        ReplaceLocked(delivery with { State = state, Status = status,
                            NextAttemptUtc = _utcNow().AddSeconds(Math.Min(3600, 5 * Math.Pow(2, delivery.Attempts - 1))) });
                        PersistLocked();
                    }
                }
                NotifyChanged();
            }
        }
        finally { _deliveryGate.Release(); }
    }

    private HttpRequestMessage BuildRequest(NotificationDestination destination, NotificationDelivery delivery, NotificationCredential credential)
    {
        var alert = _redactor.Redact(delivery.Alert);
        HttpRequestMessage request;
        string secret = credential.Authorization;
        if (destination.Kind == NotificationDestinationKind.Telegram)
        {
            if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException();
            request = new(HttpMethod.Post, "https://api.telegram.org/bot" + secret + "/sendMessage")
                { Content = new StringContent(TelegramPayload(destination, alert), System.Text.Encoding.UTF8, "application/json") };
        }
        else
        {
            request = new(HttpMethod.Post, credential.FullWebhookUri) { Content = new StringContent(Payload(alert), System.Text.Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(secret)) request.Headers.Add("Authorization", secret);
        }
        request.Headers.Add("Idempotency-Key", delivery.Id);
        request.Headers.Add("X-TradingHub-Alert-Id", alert.Id);
        return request;
    }

    private static async Task<(bool Success, bool Permanent, int ErrorCode)> ReadTelegramAcknowledgmentAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximum = 16 * 1024;
        byte[] bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(maximum + 1);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            int length = 0;
            while (length <= maximum)
            {
                int count = await stream.ReadAsync(bytes.AsMemory(length, maximum + 1 - length), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                length += count;
            }
            if (length > maximum) throw new InvalidDataException();
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length));
            bool success = json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
            int code = json.RootElement.TryGetProperty("error_code", out var error) && error.TryGetInt32(out int value) ? value : 0;
            return (success, code is >= 400 and < 500 && code is not (408 or 429), code);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
    }

    private sealed class NotificationConsentException : Exception { }
    private void ReplaceLocked(NotificationDelivery delivery)
    {
        int index = _state.Deliveries.FindIndex(d => d.Id == delivery.Id);
        if (index < 0) return;
        _state.Deliveries[index] = delivery;
        _dirty = true;
    }
    private void ThrowIfStopped() { if (_stopped) throw new ObjectDisposedException(nameof(AlertCenter)); }
    private void NotifyChanged()
    {
        if (Changed == null) return;
        foreach (Action handler in Changed.GetInvocationList()) { try { handler(); } catch { } }
    }

    /// <summary>Drains local intake and persists committed ciphertext. Withheld content remains explicitly not yet durable; remote receipt is not confirmed.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        Wake();
        while (Interlocked.Read(ref _pendingIntake) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_worker.IsCompleted) throw new InvalidOperationException("Notification worker is not running.");
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
        lock (_gate) if (!PersistLocked()) throw new InvalidOperationException(_status);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _incoming.Writer.TryComplete();
            _shutdown.Cancel();
            foreach (var active in _active.Values) active.Cancel();
        }
        await Task.WhenAll(_worker, _deliveryWorker).ConfigureAwait(false);
        await _privateGate.WaitAsync().ConfigureAwait(false);
        _privateGate.Release();
        Task[] legacy;
        lock (_gate) legacy = _legacyPrivateOperations?.ToArray() ?? Array.Empty<Task>();
        if (legacy.Length > 0)
            try { await Task.WhenAll(legacy).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { lock (_gate) _status = "A legacy credential operation did not confirm drain. Its injected owner remains responsible for completing it; no permission was published after shutdown."; }
        await _deliveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Shutdown cancels transport, not consent: unconfirmed sends remain retryable after restart.
            while (_incoming.Reader.TryRead(out var alert)) { Accept(alert); Interlocked.Decrement(ref _pendingIntake); }
            lock (_gate) { _dirty = true; PersistLocked(); }
        }
        finally { _deliveryGate.Release(); }
        if (_ownsHttp) _http.Dispose();
        _shutdown.Dispose();
        _lease?.Dispose();
    }

    private readonly record struct ExternalGrant(string DestinationId, string Revision);
    private sealed record DeferredExternalAlert(AlertEvent Alert, List<ExternalGrant> Grants);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
