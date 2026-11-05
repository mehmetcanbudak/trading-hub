using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using TradingHub.App.Windows;
using TradingHub.Networking;

namespace TradingHub.App.Services;

/// <summary>A reviewed local authorization grant, not a claimed TradingView account identity.</summary>
public sealed record TradingViewAccess(string GrantId, string ApprovalId, string CatalogFingerprint,
    ImmutableArray<string> ApprovedToolNames)
{
    public string CreateMcpConfigJson()
    {
        var launch = StrategyMcpProcess.AppCommand("--research-mcp", "--approval", ApprovalId);
        return JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> {
            ["tradingview_research"] = new { command = launch.Command, args = launch.Arguments, env = StrategyMcpProcess.ChildEnvironment() } } });
    }
}

public sealed record TradingViewResearchTool(string Name, string Description, JsonElement Definition, bool Candidate);
public sealed record TradingViewResearchReview(string GrantId, string Fingerprint, ImmutableArray<TradingViewResearchTool> Tools, string Revision = "");
public sealed record TradingViewToolPage(ImmutableArray<JsonElement> Tools, string? NextCursor);

/// <summary>Metadata and research calls only; no sampling, prompts, roots, browser or order capability.</summary>
public interface ITradingViewResearchTransport : IAsyncDisposable
{
    event Action? CatalogChanged;
    event Action? Disconnected;
    Task<TradingViewToolPage> ListToolsAsync(string? cursor, CancellationToken cancellationToken);
    Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}

public sealed record TradingViewResearchConnection(Uri Endpoint, string GrantId, bool Interactive);

public sealed class TradingViewResearchException(string message) : Exception(message);

/// <summary>Fail-closed, persisted grant/approval boundary shared by the GUI and isolated stdio process.</summary>
public sealed class TradingViewResearchService : IAsyncDisposable
{
    public static readonly Uri OfficialEndpoint = new("https://mcp.tradingview.com/mcp");
    private static readonly Lazy<TradingViewResearchService> DefaultService = new(() =>
        new(Path.Combine(AppPaths.Root, "tradingview-research.json"), Keychain.Store));
    public static TradingViewResearchService Default => DefaultService.Value;
    internal static ValueTask DisposeDefaultAsync() => DefaultService.IsValueCreated
        ? DefaultService.Value.DisposeAsync() : ValueTask.CompletedTask;

    private static readonly ImmutableHashSet<string> Candidates = ImmutableHashSet.Create(StringComparer.Ordinal,
        "get_ohlcv", "get_economic_data", "get_economic_symbols", "search_symbols", "get_symbol_data", "run_screener",
        "get_symbol_data_batch", "get_screener_columns", "get_technicals_rating", "get_news", "get_news_story",
        "get_forecasts", "get_financials", "get_financial_history", "get_documents", "get_document_view",
        "get_earnings_calendar", "get_economic_calendar", "get_dividends_calendar");
    public static bool IsResearchCandidate(string name) => Candidates.Contains(name);

    private readonly string _statePath;
    private readonly ISecretStore _secrets;
    private readonly Uri _endpoint;
    private readonly Func<TradingViewResearchConnection, CancellationToken, Task<ITradingViewResearchTransport>> _factory;
    private readonly Func<string, CancellationToken, Task<string>> _revokeTokens;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _monitorStop = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _activeOperations;
    private TaskCompletionSource? _operationsDrained;
    private Task? _disposal;
    private Task? _stateMonitor;
    private ITradingViewResearchTransport? _transport;
    private string? _transportGrant;
    private TradingViewResearchReview? _review;
    private CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private string? _observedState;
    public event Action? AccessChanged;
    public string Status { get; private set; } = "Off — no TradingView network access.";
    public string Endpoint => _endpoint.AbsoluteUri;

    /// <summary>The factory is injectable for isolated fake-server tests. Construction performs no network or secret reads.</summary>
    public TradingViewResearchService(string statePath, ISecretStore secrets,
        Func<TradingViewResearchConnection, CancellationToken, Task<ITradingViewResearchTransport>>? factory = null,
        Uri? endpoint = null, Func<string, CancellationToken, Task<string>>? revokeTokens = null,
        Func<ProxyHandlerFactory>? networkFactory = null)
    {
        _statePath = Path.GetFullPath(statePath);
        _secrets = secrets;
        _endpoint = endpoint ?? OfficialEndpoint;
        var createNetwork = networkFactory ?? (() => new AppNetworkProxy(AppSettings.Load(), _secrets).CreateFactory());
        _revokeTokens = revokeTokens ?? ((saved, ct) => TradingViewResearchSdk.RevokeTokensAsync(saved, ct, network: createNetwork()));
        _factory = factory ?? (async (connection, ct) => await TradingViewResearchSdk.ConnectAsync(connection,
            new TradingViewSecretTokenCache(_secrets, connection.GrantId, () => ReadState().GrantId == connection.GrantId), ct,
            network: createNetwork()).ConfigureAwait(false));
    }

    internal sealed record State(string Endpoint, string? GrantId, string? ApprovalId, string? Fingerprint,
        ImmutableArray<string> ApprovedNames, bool Enabled, string Revision = "", string? Diagnostic = null);
    private State OffState => new(_endpoint.AbsoluteUri, null, null, null, [], false);
    internal State ReadState()
    {
        lock (_stateGate)
        {
            if (!File.Exists(_statePath)) return OffState;
            try { return JsonSerializer.Deserialize<State>(File.ReadAllText(_statePath)) ?? throw new JsonException(); }
            catch (JsonException) { throw new TradingViewResearchException("Research authorization state is invalid. Turn it off and reconnect."); }
        }
    }
    private void SaveState(State state, string? expectedRevision = null)
    {
        state = state with { Revision = Guid.NewGuid().ToString("N") };
        lock (_stateGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            using var lease = AcquireStateLease();
            if (expectedRevision != null && ReadState().Revision != expectedRevision)
                throw new TradingViewResearchException("Authorization changed before the state update. Review again.");
            string temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(state));
                File.Move(temp, _statePath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            _observedState = File.ReadAllText(_statePath);
        }
        EnsureStateMonitor();
    }
    private FileStream AcquireStateLease()
    {
        // Cross-process compare-and-swap: an in-flight approval must never overwrite a completed revoke.
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(_statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (deadline.Elapsed < TimeSpan.FromSeconds(2)) { Thread.Sleep(10); }
        }
    }
    private void EnsureStateMonitor()
    {
        lock (_stateGate)
        {
            if (_disposed || _stateMonitor != null || !Directory.Exists(Path.GetDirectoryName(_statePath))) return;
            _observedState ??= File.Exists(_statePath) ? File.ReadAllText(_statePath) : null;
            var stop = _monitorStop.Token;
            _stateMonitor = Task.Run(() => MonitorStateAsync(stop));
        }
    }
    private async Task MonitorStateAsync(CancellationToken stop)
    {
        // macOS FileSystemWatcher startup performs a system-wide POSIX sync. Exact-content polling
        // avoids that unrelated global flush; one awaited loop keeps polls nonoverlapping.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false)) ObserveStateChange();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    private void ObserveStateChange()
    {
        string? current;
        bool unavailable = false;
        lock (_stateGate)
        {
            try { current = File.Exists(_statePath) ? File.ReadAllText(_statePath) : null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { current = null; unavailable = true; }
            if (current == _observedState) return;
            _observedState = current;
        }
        CancelActive();
        _review = null;
        try
        {
            var state = current == null ? null : JsonSerializer.Deserialize<State>(current);
            Status = unavailable
                ? "Research authorization state is unavailable. Requests require readable authorization state."
                : state?.Diagnostic ?? "Research access changed in another process. Reopen settings to review.";
        }
        catch (JsonException)
        { Status = "Research authorization state is unavailable. Requests require readable authorization state."; }
        AccessChanged?.Invoke();
    }
    private readonly struct OperationScope(TradingViewResearchService owner, CancellationToken token,
        CancellationTokenSource? linked) : IDisposable
    {
        public CancellationToken Token => token;
        public void Dispose()
        {
            try { linked?.Dispose(); }
            finally { owner.EndOperation(); }
        }
    }
    private OperationScope BeginOperation(CancellationToken caller)
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            caller.ThrowIfCancellationRequested();
            var shutdown = _shutdown.Token;
            var linked = caller.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(caller, shutdown) : null;
            _activeOperations++;
            return new(this, linked?.Token ?? shutdown, linked);
        }
    }
    private bool TryBeginNotification(out OperationScope operation)
    {
        lock (_stateGate)
        {
            if (_disposed) { operation = default; return false; }
            _activeOperations++;
            operation = new(this, _shutdown.Token, null);
            return true;
        }
    }
    private void EndOperation()
    {
        lock (_stateGate)
            if (--_activeOperations == 0) _operationsDrained?.TrySetResult();
    }
    private void ThrowIfClosing(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
            if (_disposed) throw new OperationCanceledException(cancellationToken);
    }
    private void CancelActive()
    {
        lock (_stateGate) _lifetime.Cancel();
    }
    private CancellationToken FreshLifetime()
    {
        lock (_stateGate)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            _lifetime = new();
            return _lifetime.Token;
        }
    }
    public TradingViewAccess? CaptureAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stateMonitor?.IsFaulted == true)
            throw new TradingViewResearchException("Research authorization state is unavailable. Requests require readable authorization state.");
        var state = ReadState();
        if (!string.IsNullOrEmpty(state.Diagnostic)) Status = state.Diagnostic;
        EnsureStateMonitor();
        return state.Enabled && state.Endpoint == Endpoint && state.GrantId is { Length: > 0 } grant
            && state.ApprovalId is { Length: > 0 } approval && state.Fingerprint is { Length: > 0 } fingerprint
            && !state.ApprovedNames.IsDefaultOrEmpty && state.ApprovedNames.All(IsResearchCandidate)
            ? new(grant, approval, fingerprint, state.ApprovedNames) : null;
    }
    public Task ConfigureAsync(Window owner) => new TradingViewResearchWindow(this).ShowDialog(owner);

    public async Task<TradingViewResearchReview> ConnectAndReviewAsync(CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing(cancellationToken);
            await RevokeCoreAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfClosing(cancellationToken);
            string grant = Guid.NewGuid().ToString("N");
            SaveState(new(Endpoint, grant, null, null, [], false));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, FreshLifetime());
            Status = "Authorizing in your browser, then discovering all tool metadata. No tools are called.";
            await ConnectTransportAsync(grant, interactive: true, linked.Token).ConfigureAwait(false);
            _review = await DiscoverAsync(grant, linked.Token).ConfigureAwait(false);
            if (ReadState().GrantId != grant || linked.IsCancellationRequested) throw new OperationCanceledException();
            Status = "Review required — local grant " + grant[..8] + ". Account identity is not asserted.";
            return _review;
        }
        catch (Exception ex)
        {
            Invalidate("Connect failed. " + SafeError(ex));
            throw new TradingViewResearchException(Status);
        }
        finally { _operations.Release(); }
    }

    public async Task<TradingViewResearchReview> ReviewAsync(CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing(cancellationToken);
            var state = ReadState();
            if (state.GrantId == null) throw new TradingViewResearchException("Connect before reviewing metadata.");
            SaveState(state with { Enabled = false, ApprovalId = null, Fingerprint = null, ApprovedNames = [], Diagnostic = null }, state.Revision);
            AccessChanged?.Invoke();
            await EnsureTransportAsync(state.GrantId, cancellationToken).ConfigureAwait(false);
            _review = await DiscoverAsync(state.GrantId, cancellationToken).ConfigureAwait(false);
            Status = "Review required — local grant " + state.GrantId[..8] + ". Account identity is not asserted.";
            return _review;
        }
        catch (Exception ex) { Invalidate("Review failed. " + SafeError(ex)); throw new TradingViewResearchException(Status); }
        finally { _operations.Release(); }
    }

    public async Task<TradingViewAccess> ApproveAsync(TradingViewResearchReview review, IEnumerable<string> names,
        CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing(cancellationToken);
            var state = ReadState();
            if (!ReferenceEquals(review, _review) || state.GrantId != review.GrantId || state.Revision != review.Revision)
                throw new TradingViewResearchException("This review is no longer current. Discover metadata again.");
            var selected = names.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            if (selected.IsEmpty || selected.Any(n => !IsResearchCandidate(n) || !review.Tools.Any(t => t.Name == n && t.Candidate)))
                throw new TradingViewResearchException("Select only offered research candidates. Watchlists, alerts and unknown tools are denied.");
            var current = await DiscoverAsync(review.GrantId, cancellationToken).ConfigureAwait(false);
            if (current.Fingerprint != review.Fingerprint || current.Revision != review.Revision
                || ReadState().Revision != review.Revision || !ReferenceEquals(_review, review))
                throw new TradingViewResearchException("Catalog changed during approval. Review again.");
            string approval = Guid.NewGuid().ToString("N");
            lock (_stateGate)
            {
                ThrowIfClosing(cancellationToken);
                SaveState(new(Endpoint, review.GrantId, approval, review.Fingerprint, selected, true), review.Revision);
                FreshLifetime();
            }
            Status = $"Enabled for {selected.Length} explicitly approved research tools; local grant {review.GrantId[..8]}.";
            AccessChanged?.Invoke();
            return RequireAccess(approval);
        }
        catch (Exception ex) { Invalidate("Approval failed. " + SafeError(ex)); throw new TradingViewResearchException(Status); }
        finally { _operations.Release(); }
    }
    public void CancelReview()
    {
        using var operation = BeginOperation(CancellationToken.None);
        _review = null;
        Invalidate("Review canceled — research tools remain off.");
    }

    public async Task<ImmutableArray<TradingViewResearchTool>> GetApprovedToolsAsync(string approvalId, CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing(cancellationToken);
            var access = RequireAccess(approvalId);
            await EnsureTransportAsync(access.GrantId, cancellationToken).ConfigureAwait(false);
            var catalog = await DiscoverAsync(access.GrantId, cancellationToken).ConfigureAwait(false);
            RequireCatalog(access, catalog);
            return catalog.Tools.Where(t => access.ApprovedToolNames.Contains(t.Name, StringComparer.Ordinal)).ToImmutableArray();
        }
        catch (Exception ex) { Invalidate("Research metadata unavailable. " + SafeError(ex)); throw new TradingViewResearchException(Status); }
        finally { _operations.Release(); }
    }
    public async Task<JsonElement> ForwardAsync(string approvalId, string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing(cancellationToken);
            var access = RequireAccess(approvalId);
            // Reject the name before connecting or requesting metadata, regardless of readOnlyHint.
            if (!IsResearchCandidate(name) || !access.ApprovedToolNames.Contains(name, StringComparer.Ordinal))
                throw new TradingViewResearchException("Tool denied by the approved research policy.");
            if (arguments.ValueKind != JsonValueKind.Object) throw new TradingViewResearchException("Tool arguments must be an object.");
            await EnsureTransportAsync(access.GrantId, cancellationToken).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var catalog = await DiscoverAsync(access.GrantId, linked.Token).ConfigureAwait(false);
            RequireCatalog(access, catalog);
            RequireAccess(approvalId);
            linked.Token.ThrowIfCancellationRequested();
            return await _transport!.CallToolAsync(name, arguments, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not TradingViewResearchException)
        {
            Invalidate("Research request failed. " + SafeError(ex));
            throw new TradingViewResearchException(Status);
        }
        finally { _operations.Release(); }
    }
    private TradingViewAccess RequireAccess(string approval)
    {
        lock (_stateGate)
        {
            ThrowIfClosing(_shutdown.Token);
            var access = CaptureAccess();
            if (access == null || access.ApprovalId != approval) throw new TradingViewResearchException("Research access is off, revoked or requires a new review.");
            return access;
        }
    }
    private void RequireCatalog(TradingViewAccess access, TradingViewResearchReview catalog)
    {
        if (catalog.Fingerprint != access.CatalogFingerprint)
        {
            Invalidate("TradingView catalog/schema changed. Explicit review is required again.");
            throw new TradingViewResearchException(Status);
        }
        var current = RequireAccess(access.ApprovalId);
        if (current.GrantId != access.GrantId) throw new TradingViewResearchException("Authorization grant changed.");
    }
    private async Task EnsureTransportAsync(string grant, CancellationToken cancellationToken)
    {
        ThrowIfClosing(cancellationToken);
        if (ReadState().GrantId != grant) throw new TradingViewResearchException("Authorization was revoked before connection.");
        lock (_stateGate)
            if (_transport != null && _transportGrant == grant) return;
        await DisposeTransportAsync().ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, FreshLifetime());
        await ConnectTransportAsync(grant, interactive: false, linked.Token).ConfigureAwait(false);
    }
    private async Task ConnectTransportAsync(string grant, bool interactive, CancellationToken cancellationToken)
    {
        ThrowIfClosing(cancellationToken);
        var transport = await _factory(new(_endpoint, grant, interactive), cancellationToken).ConfigureAwait(false);
        bool published = false;
        try
        {
            lock (_stateGate)
            {
                ThrowIfClosing(cancellationToken);
                if (ReadState().GrantId != grant)
                    throw new TradingViewResearchException("Authorization was revoked before connection.");
                _transport = transport;
                _transportGrant = grant;
                published = true;
            }
            // Event accessors can synchronously call back; never invoke them while holding _stateGate.
            Attach(transport);
            ThrowIfClosing(cancellationToken);
        }
        finally
        {
            if (!published) await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
    private async Task DisposeTransportAsync()
    {
        ITradingViewResearchTransport? transport;
        lock (_stateGate)
        {
            transport = _transport;
            _transport = null;
            _transportGrant = null;
        }
        if (transport == null) return;
        transport.CatalogChanged -= OnCatalogChanged;
        transport.Disconnected -= OnDisconnected;
        await transport.DisposeAsync().ConfigureAwait(false);
    }
    private void Attach(ITradingViewResearchTransport transport)
    {
        transport.CatalogChanged += OnCatalogChanged;
        transport.Disconnected += OnDisconnected;
    }
    private void OnCatalogChanged()
    {
        if (!TryBeginNotification(out var operation)) return;
        using (operation) Invalidate("TradingView reported a catalog change. Review is required again.");
    }
    private void OnDisconnected()
    {
        if (!TryBeginNotification(out var operation)) return;
        using (operation) Invalidate("TradingView disconnected. Reconnect and review before continuing.");
    }
    private void Invalidate(string status)
    {
        CancelActive();
        _review = null;
        var state = ReadState();
        SaveState(state with { Enabled = false, ApprovalId = null, Fingerprint = null, ApprovedNames = [], Diagnostic = status });
        Status = status;
        AccessChanged?.Invoke();
    }
    private async Task<TradingViewResearchReview> DiscoverAsync(string grant, CancellationToken cancellationToken)
    {
        string revision = ReadState().Revision;
        var tools = ImmutableArray.CreateBuilder<TradingViewResearchTool>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await _transport!.ListToolsAsync(cursor, cancellationToken).ConfigureAwait(false);
            foreach (var definition in page.Tools)
            {
                string name = definition.GetProperty("name").GetString() ?? throw new TradingViewResearchException("Tool name missing.");
                if (!names.Add(name)) throw new TradingViewResearchException("Duplicate tool names cannot be reviewed safely.");
                if (!definition.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
                    throw new TradingViewResearchException("Tool schema missing. Discovery is incomplete.");
                string description = definition.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                tools.Add(new(name, description, definition.Clone(), IsResearchCandidate(name)));
            }
            cursor = page.NextCursor;
            if (cursor != null && !cursors.Add(cursor)) throw new TradingViewResearchException("Repeated tools/list cursor. Discovery is incomplete.");
        } while (cursor != null);
        var state = ReadState();
        if (state.GrantId != grant || state.Revision != revision)
            throw new TradingViewResearchException("Authorization or catalog changed during discovery.");
        var ordered = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToImmutableArray();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var tool in ordered) WriteCanonical(writer, tool.Definition);
            writer.WriteEndArray();
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
        return new(grant, fingerprint, ordered, revision);
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }

    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        using var operation = BeginOperation(cancellationToken);
        await RevokeCoreAsync(operation.Token).ConfigureAwait(false);
    }
    private async Task RevokeCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfClosing(cancellationToken);
        var old = ReadState();
        SaveState(OffState); // Persist denial before awaiting anything, including a remote revocation attempt.
        CancelActive();
        _review = null;
        AccessChanged?.Invoke();
        Status = "Off — dedicated local authorization removed.";
        if (old.GrantId != null)
        {
            string account = TradingViewSecretTokenCache.Account(old.GrantId);
            string saved = _secrets.Get(account);
            _secrets.Set(account, null);
            if (!string.IsNullOrEmpty(saved))
            {
                try { Status += " " + await _revokeTokens(saved, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { Status += " Server revocation was not confirmed. " + SafeError(ex); }
            }
        }
        await DisposeTransportAsync().ConfigureAwait(false);
    }
    public static string SafeError(Exception error)
    {
        // Never surface SDK/HTTP exception text: OAuth errors can contain codes, URLs or tokens.
        if (error is TradingViewResearchException safe) return safe.Message;
        if (error is HttpRequestException http)
            return http.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Authorization expired. Reconnect outside chat.",
                System.Net.HttpStatusCode.Forbidden => "TradingView denied access. Essential or above is required; trials are excluded.",
                System.Net.HttpStatusCode.TooManyRequests => "TradingView rate limit reached (HTTP 429). No fallback data was used.",
                _ => "TradingView HTTP request failed" + (http.StatusCode is { } code ? $" (HTTP {(int)code})." : ".")
            };
        if (error is OperationCanceledException) return "Operation canceled; research access was not enabled.";
        if (error.Message.Contains("does not support dynamic client registration", StringComparison.OrdinalIgnoreCase))
            return "The authorization server does not offer supported application registration. Trading Hub needs an approved client registration or public client metadata document; CLI credentials cannot be reused.";
        if (error.Message.Contains("PKCE", StringComparison.OrdinalIgnoreCase))
            return "Authorization refused: the server must advertise S256 PKCE support.";
        if (error.Message.Contains("issuer", StringComparison.OrdinalIgnoreCase) || error.Message.Contains("state", StringComparison.OrdinalIgnoreCase))
            return "Authorization refused: OAuth issuer/state validation failed. Reconnect outside chat.";
        if (error.InnerException != null) return SafeError(error.InnerException);
        return "TradingView authorization or transport failed. Reconnect or check the service; no fallback data was used.";
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task operations, monitor;
        lock (_stateGate)
        {
            if (_disposal != null) return new ValueTask(_disposal);
            _disposed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
            operations = _activeOperations == 0 ? Task.CompletedTask
                : (_operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            monitor = _stateMonitor ?? Task.CompletedTask;
        }
        // Start outside _stateGate: cancellation callbacks and asynchronous unwinding may need it.
        _ = CompleteDisposalAsync(operations, monitor, completion);
        return new ValueTask(completion.Task);
    }
    private async Task CompleteDisposalAsync(Task operations, Task monitor, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { _shutdown.Cancel(); }
        catch (Exception error) { failure = error; }
        try { CancelActive(); }
        catch (Exception error) { failure = ShutdownFailure(failure, error); }
        try { _monitorStop.Cancel(); }
        catch (Exception error) { failure = ShutdownFailure(failure, error); }
        try { await Task.WhenAll(operations, monitor).ConfigureAwait(false); }
        catch (Exception error) { failure = ShutdownFailure(failure, error); }
        try { await DisposeTransportAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = ShutdownFailure(failure, error); }
        try
        {
            _monitorStop.Dispose();
            _lifetime.Dispose();
            _shutdown.Dispose();
            _operations.Dispose();
        }
        catch (Exception error) { failure = ShutdownFailure(failure, error); }
        // Every concurrent disposer observes the same completed drain, including cleanup failures.
        if (failure == null) completion.TrySetResult();
        else completion.TrySetException(failure);
    }
    private static Exception ShutdownFailure(Exception? previous, Exception next) =>
        previous == null ? next : new AggregateException(previous, next);
}
