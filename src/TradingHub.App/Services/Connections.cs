using System.Buffers;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingHub.Common.Logging;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Feeds.Longbridge;
using TradingHub.Networking;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.App.Services;

/// <summary>Connection secrets, one entry per account. An empty result means no saved secret.</summary>
public interface ISecretStore
{
    string Get(string account);

    /// <summary>Save a secret; null or empty deletes it.</summary>
    void Set(string account, string? secret);
}

/// <summary>Optional cancellable access to private credentials without blocking the caller's thread.</summary>
public interface IAsyncSecretStore : ISecretStore
{
    Task<string> GetAsync(string account, CancellationToken cancellationToken = default);
    Task SetAsync(string account, string? secret, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores connection secrets in macOS Keychain or Windows Credential Manager. Test tools select an isolated
/// fake JSON file with TRADINGHUB_KEYCHAIN_FILE and never access the operating system's credential store.
/// </summary>
public static class Keychain
{
    private const string Service = "Trading Hub";

    /// <summary>The platform credential store, or the explicitly selected isolated test file.</summary>
    internal static ISecretStore Store { get; } = Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE") is { Length: > 0 } f
        ? new FileSecretStore(f)
        : OperatingSystem.IsWindows() ? new WindowsCredentialStore(Service) : new MacKeychainStore(Service);

    public static void Set(string account, string? secret) => Store.Set(account, secret);

    public static string Get(string account) => Store.Get(account);

    /// <summary>Close private-store admission and drain owned work after all credential consumers finish shutdown.</summary>
    public static Task ShutdownAsync() => Store is IAsyncDisposable owned ? owned.DisposeAsync().AsTask() : Task.CompletedTask;
}

/// <summary>The owned process boundary, injectable without starting a native credential executable.</summary>
internal interface IKeychainProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync();
    void Kill(bool entireProcessTree);
}

/// <summary>macOS Keychain integration through an owned, bounded /usr/bin/security process.</summary>
internal sealed class MacKeychainStore : IAsyncSecretStore, IAsyncDisposable
{
    private readonly string _service;
    private readonly string _executablePath;
    private readonly Func<ProcessStartInfo, IKeychainProcess> _startProcess;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly SecretStoreOperations _operations = new();

    internal MacKeychainStore(string service, Func<ProcessStartInfo, IKeychainProcess>? startProcess = null,
        TimeSpan? operationTimeout = null, TimeProvider? timeProvider = null, string executablePath = "/usr/bin/security")
    {
        _service = service;
        _executablePath = executablePath;
        _startProcess = startProcess ?? NativeProcess.Start;
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(5);
        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Get(string account) => GetAsync(account).GetAwaiter().GetResult();
    public void Set(string account, string? secret) => SetAsync(account, secret).GetAwaiter().GetResult();

    public async Task<string> GetAsync(string account, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Enter(cancellationToken);
        return await RunAsync(["find-generic-password", "-s", _service, "-a", account, "-w"],
            readSecret: true, allowAbsent: true, operation.Token).ConfigureAwait(false);
    }

    public async Task SetAsync(string account, string? secret, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Enter(cancellationToken);
        bool delete = string.IsNullOrEmpty(secret);
        string[] args = delete
            ? ["delete-generic-password", "-s", _service, "-a", account]
            : ["add-generic-password", "-U", "-s", _service, "-a", account, "-w", secret!];
        await RunAsync(args, readSecret: false, allowAbsent: delete, operation.Token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _operations.DisposeAsync();

    private async Task<string> RunAsync(string[] args, bool readSecret, bool allowAbsent, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_operationTimeout, _timeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        IKeychainProcess? process = null;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(_executablePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            process = _startProcess(info) ?? throw new InvalidOperationException();

            // Start both drains before any await. Reads share the deadline/cancellation even if
            // the root exits while a descendant holds a pipe; all owned tasks must still settle.
            Task<string> output = ReadOutputAsync(process, readSecret, cancellation.Token);
            Task errors = DrainErrorsAsync(process, cancellation.Token);
            Task exit = WaitForExitAsync(process);
            Task completion = Task.WhenAll(output, errors, exit);
            try
            {
                await completion.WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                bool stopFailed = false;
                try { process.Kill(entireProcessTree: true); }
                catch
                {
                    stopFailed = true;
                    // Still try to terminate the owned root if tree enumeration/termination failed.
                    try { process.Kill(entireProcessTree: false); }
                    catch { /* Await the owned exit and drains; never release a still-running child. */ }
                }
                try { await completion.ConfigureAwait(false); }
                catch { /* The sanitized operation failure is reported only after every drain finishes. */ }
                if (stopFailed) throw new IOException();
                cancellation.Token.ThrowIfCancellationRequested();
                throw;
            }

            if (process.ExitCode == 0)
            {
                string result = (await output.ConfigureAwait(false)).TrimEnd('\n');
                if (readSecret && result.Length == 0) throw new IOException();
                return result;
            }
            // Apple's security tool returns errSecItemNotFound (-25300) unchanged; POSIX exposes
            // its low byte, 44. Only find and already-absent delete may accept that status.
            if (allowAbsent && process.ExitCode == 44) return "";
            throw new IOException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The private credential operation was canceled.", cancellationToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The private credential operation exceeded its deadline.");
        }
        catch
        {
            // Native diagnostics, start info, arguments and inner exceptions can all contain secrets.
            throw new IOException("The private credential operation failed.");
        }
        finally
        {
            try { process?.Dispose(); }
            catch { throw new IOException("Could not release the private credential process."); }
        }
    }

    private static async Task<string> ReadOutputAsync(IKeychainProcess process, bool readSecret, CancellationToken cancellationToken)
    {
        if (readSecret) return await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await DrainAsync(process.StandardOutput, cancellationToken).ConfigureAwait(false);
        return "";
    }

    private static async Task DrainErrorsAsync(IKeychainProcess process, CancellationToken cancellationToken) =>
        await DrainAsync(process.StandardError, cancellationToken).ConfigureAwait(false);

    private static async Task WaitForExitAsync(IKeychainProcess process) =>
        await process.WaitForExitAsync().ConfigureAwait(false);

    private static async Task DrainAsync(TextReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) { }
        }
        finally { ArrayPool<char>.Shared.Return(buffer, clearArray: true); }
    }

    private sealed class NativeProcess(Process process) : IKeychainProcess
    {
        internal static IKeychainProcess Start(ProcessStartInfo info)
        {
            var process = new Process { StartInfo = info };
            try
            {
                if (!process.Start()) throw new IOException();
                return new NativeProcess(process);
            }
            catch { process.Dispose(); throw; }
        }

        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync() => process.WaitForExitAsync();
        public void Kill(bool entireProcessTree)
        {
            if (process.HasExited) return;
            try { process.Kill(entireProcessTree); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        public void Dispose() => process.Dispose();
    }
}

/// <summary>Admission and shutdown ownership shared by the native and isolated-file stores.</summary>
internal sealed class SecretStoreOperations : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _closing;
    private Task? _disposeTask;

    internal Lease Enter(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closing) throw new ObjectDisposedException("Private credential store");
            _active++;
        }
        try { return new Lease(this, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token)); }
        catch { Release(); throw; }
    }

    private void Release()
    {
        lock (_gate)
        {
            _active--;
            if (_closing && _active == 0) _drained.TrySetResult();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _closing = true;
            if (_active == 0) _drained.TrySetResult();
            _disposeTask = StopAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task StopAsync()
    {
        _stop.Cancel();
        await _drained.Task.ConfigureAwait(false);
        _stop.Dispose();
    }

    internal readonly struct Lease(SecretStoreOperations owner, CancellationTokenSource cancellation) : IDisposable
    {
        internal CancellationToken Token => cancellation.Token;
        public void Dispose()
        {
            cancellation.Dispose();
            owner.Release();
        }
    }
}

/// <summary>Fake secrets for tests and tools, isolated by TRADINGHUB_KEYCHAIN_FILE.</summary>
internal sealed class FileSecretStore(string path) : IAsyncSecretStore, IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly SecretStoreOperations _operations = new();

    public string Get(string account) => GetAsync(account).GetAwaiter().GetResult();
    public void Set(string account, string? secret) => SetAsync(account, secret).GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => _operations.DisposeAsync();

    public async Task<string> GetAsync(string account, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Enter(cancellationToken);
        await _serial.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(operation.Token).ConfigureAwait(false)).GetValueOrDefault(account, "");
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            throw new OperationCanceledException("The private credential operation was canceled.", operation.Token);
        }
        catch { throw new IOException("Could not read the isolated private credential file."); }
        finally { _serial.Release(); }
    }

    public async Task SetAsync(string account, string? secret, CancellationToken cancellationToken = default)
    {
        using var operation = _operations.Enter(cancellationToken);
        await _serial.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            var all = await LoadAsync(operation.Token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(secret)) all.Remove(account);
            else all[account] = secret;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(all), operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            throw new OperationCanceledException("The private credential operation was canceled.", operation.Token);
        }
        catch { throw new IOException("Could not save the isolated private credential file."); }
        finally { _serial.Release(); }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        string json;
        try { json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        var all = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new InvalidDataException();
        if (all.Values.Any(value => value is null)) throw new InvalidDataException();
        return all;
    }
}

public enum ConnectionKind
{
    [Display(Name = "Simulator")] Simulator,
    [Display(Name = "Binance Futures")] BinanceFutures,
    [Display(Name = "Binance Spot")] BinanceSpot,
    [Display(Name = "Bybit")] Bybit,
    [Display(Name = "Rithmic")] Rithmic,
    [Display(Name = "Interactive Brokers")] InteractiveBrokers,
    [Display(Name = "AMP Futures")] AmpFutures,
    [Display(Name = "Tradovate")] Tradovate,
    [Display(Name = "cTrader")] CTrader,
    [Display(Name = "OANDA")] Oanda,
    [Display(Name = "Alpaca")] Alpaca,
    [Display(Name = "Databento")] Databento,
    [Display(Name = "Longbridge (data only)")] Longbridge,
    [Display(Name = "Aggregate Binance + Bybit (data only)")] AggregateCrypto,
}

/// <summary>Una conexión configurada (los secretos van al llavero, no a este archivo).</summary>
public sealed class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>Opaque local trading namespace and cooperating configuration generation, not a native account ID.</summary>
    public string TradingNamespace { get; set; } = "";
    public string TradingGeneration { get; set; } = "";
    public ConnectionKind Kind { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Horas de historia a cargar al abrir un instrumento.</summary>
    public double HistoryHours { get; set; } = 1.5;

    // Binance / Bybit
    public bool Testnet { get; set; } = true;
    public string ApiKey { get; set; } = "";

    // Bybit public data uses the selected account environment; both API credentials are in the secret store.
    public BybitEnvironment BybitEnvironment { get; set; } = BybitEnvironment.Demo;

    // Longbridge stores only application/market metadata here, never access or refresh tokens.
    public LongbridgeSettings Longbridge { get; set; } = new();
    public bool AllowRealOrders { get; set; }
    public bool ConfirmOrders { get; set; } = true;
    public decimal MaxOrderQuantity { get; set; }

    // Rithmic
    public string RithmicUri { get; set; } = RithmicGateways.TestUri;
    public string RithmicSystem { get; set; } = "Rithmic Test";
    public bool RithmicAggregatedQuotes { get; set; } = true;
    public int RithmicTimeoutMs { get; set; } = 10000;
    public string RithmicUser { get; set; } = "";
    public string RithmicAppName { get; set; } = "TradingHub";
    public List<string> RithmicProducts { get; set; } = new RithmicSettings().Products;

    // Tradovate
    public string TradovateUser { get; set; } = "";
    public string TradovateCid { get; set; } = "";
    public string TradovateAppId { get; set; } = "TradingHub";
    public List<string> TradovateProducts { get; set; } = new TradingHub.Feeds.Tradovate.TradovateSettings().Products;

    /// <summary>Id fijo de este equipo para la conexión (Tradovate limita los dispositivos).</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();

    // cTrader
    public string CTraderClientId { get; set; } = "";

    /// <summary>Número de cuenta de cTrader (0 = la primera del entorno).</summary>
    public long CTraderLogin { get; set; }

    // OANDA (cuenta 101-001-…; vacía = la primera del token)
    public string OandaAccount { get; set; } = "";

    // Alpaca: datos en vivo iex (gratis) o sip (pago)
    public string AlpacaLiveFeed { get; set; } = "iex";

    // Interactive Brokers
    public string IbHost { get; set; } = "127.0.0.1";
    public int IbPort { get; set; } = 7497;
    public int IbClientId { get; set; } = 17;
    public bool IbOffline { get; set; } = true;

    [JsonIgnore] public string SecretAccount => $"{Id}:secret";

    /// <summary>Otros secretos de la conexión (API secret de Tradovate, tokens de cTrader).</summary>
    public string SecretAccountFor(string name) => $"{Id}:{name}";

    /// <summary>Id de los datos de mercado (comparten archivos de ticks perfiles del mismo mercado).</summary>
    [JsonIgnore]
    public string MarketDataId => Kind switch
    {
        ConnectionKind.Simulator => SimulatedFeed.FeedId,
        ConnectionKind.BinanceFutures => BinanceFeed.FuturesId,
        ConnectionKind.BinanceSpot => BinanceFeed.SpotId,
        ConnectionKind.Bybit => BybitEnvironment == BybitEnvironment.Testnet ? BybitFeed.TestnetId : BybitFeed.LinearId,
        ConnectionKind.Longbridge => "longbridge-" + Id,
        ConnectionKind.AggregateCrypto => AggregateFeed.FeedId,
        ConnectionKind.Rithmic or ConnectionKind.AmpFutures => RithmicFeed.FeedId,
        ConnectionKind.Tradovate => TradingHub.Feeds.Tradovate.TradovateFeed.FeedId,
        // cada broker de cTrader tiene sus propios precios: el primero usa «ctrader» y los demás su propio id
        ConnectionKind.CTrader => Id == TradingHub.Feeds.CTrader.CTraderFeed.FeedId ? Id : $"{TradingHub.Feeds.CTrader.CTraderFeed.FeedId}-{Id}",
        ConnectionKind.Oanda => TradingHub.Feeds.Oanda.OandaFeed.FeedId,
        ConnectionKind.Alpaca => TradingHub.Feeds.Alpaca.AlpacaFeed.FeedId,
        ConnectionKind.Databento => "databento",
        _ => "ib",
    };

    /// <summary>Chart market-data capability; IB remains strictly data-only.</summary>
    [JsonIgnore] public bool HasMarketData => Kind != ConnectionKind.Databento;

    /// <summary>Id por defecto del primer perfil de cada tipo (el mismo de sus datos).</summary>
    public static string DefaultId(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Tradovate => TradingHub.Feeds.Tradovate.TradovateFeed.FeedId,
        ConnectionKind.CTrader => TradingHub.Feeds.CTrader.CTraderFeed.FeedId,
        ConnectionKind.Oanda => TradingHub.Feeds.Oanda.OandaFeed.FeedId,
        ConnectionKind.Alpaca => TradingHub.Feeds.Alpaca.AlpacaFeed.FeedId,
        ConnectionKind.Databento => "databento",
        _ => Guid.NewGuid().ToString("N")[..8],
    };

    /// <summary>Usa la conexión de Rithmic (Rithmic directo o AMP Futures, que da cuentas de Rithmic).</summary>
    [JsonIgnore] public bool IsRithmic => Kind is ConnectionKind.Rithmic or ConnectionKind.AmpFutures;

    [JsonIgnore] public bool SupportsTrading => Kind is ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot or ConnectionKind.Bybit;

    public override string ToString() => Name;
}

/// <summary>Administra las conexiones: las guarda, crea sus feeds y las prueba.</summary>
public sealed class ConnectionManager
{
    private readonly string _filePath;
    private readonly ISecretStore _secretStore;
    private readonly Func<ProxyHandlerFactory> _networkFactory;
    private readonly object _longbridgeGate = new();
    private readonly CancellationTokenSource _longbridgeLifetime = new();
    private readonly HashSet<Task> _longbridgeAuthorizations = new();
    private readonly HashSet<LongbridgeFeed> _longbridgeOwnedFeeds = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ILongbridgeTransport> _longbridgeOwnedTransports = new(ReferenceEqualityComparer.Instance);
    private readonly Func<ConnectionProfile, ProxyHandlerFactory, HttpClient>? _longbridgeHttpFactory;
    private readonly Func<ConnectionProfile, LongbridgeSession, ILongbridgeTransport>? _longbridgeTransportFactory;
    private readonly TimeProvider? _longbridgeClock;
    private bool _longbridgeClosing;
    private Task? _longbridgeStop;
    private readonly Dictionary<string, long> _longbridgeGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Key, string Secret)> _bybitCredentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (LongbridgeSession Session, HttpClient Http)> _longbridgeSessions = new(StringComparer.Ordinal);
    private readonly List<(LongbridgeSession Session, HttpClient Http)> _retiredLongbridge = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly Dictionary<string, IMarketDataFeed> _feeds = new();
    private readonly List<IMarketDataFeed> _retiredFeeds = new();
    // Hub retains consumers; IB scopes stay alive until application shutdown.
    private readonly Dictionary<string, IbFeed> _ibScopes = new(StringComparer.Ordinal);
    // Compared only in memory. Never serialize this snapshot into order identity records.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TradingRouteState> _tradingRoutes = new(StringComparer.Ordinal);
    private string? _tradingPersistenceError;

    // Tradovate shares its feed transport with account management; do not sever a live or unresolved consumer.
    internal Func<IMarketDataFeed, bool>? FeedInUse { get; set; }

    public List<ConnectionProfile> Profiles { get; } = new();
    public event Action? Changed;

    public ConnectionManager(Func<ProxyHandlerFactory>? networkFactory = null, ISecretStore? secretStore = null, string? configurationPath = null,
        Func<ConnectionProfile, ProxyHandlerFactory, HttpClient>? longbridgeHttpFactory = null,
        Func<ConnectionProfile, LongbridgeSession, ILongbridgeTransport>? longbridgeTransportFactory = null, TimeProvider? longbridgeClock = null)
    {
        _filePath = configurationPath ?? Path.Combine(AppPaths.Root, "connections.json");
        _secretStore = secretStore ?? Keychain.Store;
        _networkFactory = networkFactory ?? (() => new ProxyHandlerFactory(new NetworkProxyPolicy()));
        _longbridgeHttpFactory = longbridgeHttpFactory;
        _longbridgeTransportFactory = longbridgeTransportFactory;
        _longbridgeClock = longbridgeClock;
        Load();
        if (Profiles.Count == 0)
        {
            // ids iguales a los datos de mercado para que los espacios de trabajo viejos sigan andando
            Profiles.Add(new ConnectionProfile { Id = SimulatedFeed.FeedId, Kind = ConnectionKind.Simulator, Name = "Simulator", HistoryHours = 6 });
            Profiles.Add(new ConnectionProfile { Id = BinanceFeed.FuturesId, Kind = ConnectionKind.BinanceFutures, Name = "Binance Futures" });
            Profiles.Add(new ConnectionProfile { Id = BinanceFeed.SpotId, Kind = ConnectionKind.BinanceSpot, Name = "Binance Spot" });
            Profiles.Add(new ConnectionProfile { Id = BybitFeed.LinearId, Kind = ConnectionKind.Bybit, Name = "Bybit" });
            Profiles.Add(new ConnectionProfile { Id = RithmicFeed.FeedId, Kind = ConnectionKind.Rithmic, Name = "Rithmic", Enabled = false });
            Save();
        }
    }

    public IEnumerable<ConnectionProfile> Enabled => Profiles.Where(p => p.Enabled);

    /// <summary>Conexión de una cuenta de operar ("sim", la de Binance o rithmic|conexión|cuenta).</summary>
    public ConnectionProfile? FindForAccount(string accountId) =>
        (accountId.StartsWith("rithmic|", StringComparison.Ordinal) || accountId.StartsWith("tradovate|", StringComparison.Ordinal)) && accountId.Split('|') is { Length: >= 2 } parts
            ? Find(parts[1])
            : Find(accountId);

    public ConnectionProfile? Find(string id) => Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault(p => p.MarketDataId == id);

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                Profiles.AddRange(JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(_filePath), Json) ?? new());
            foreach (var profile in Profiles.Where(p => p.Kind == ConnectionKind.Simulator && p.Name == "Simulador"))
                profile.Name = "Simulator";
            bool missing = Profiles.Any(p => TradesNatively(p) && (p.TradingNamespace.Length == 0 || p.TradingGeneration.Length == 0));
            foreach (var profile in Profiles) RefreshTradingIdentity(profile);
            if (missing || Profiles.Any(p => p.Kind == ConnectionKind.Bybit && p.ApiKey.Length > 0)) Save();
        }
        catch (Exception ex)
        {
            this.LogWarn("Could not read connections: " + ex.Message);
        }
    }

    public void Save()
    {
        // Older Bybit profiles could expose the public API identifier. Move it before any profile serialization.
        foreach (var p in Profiles.Where(p => p.Kind == ConnectionKind.Bybit && p.ApiKey.Length > 0))
        {
            SetSecret(p, "apikey", p.ApiKey);
            p.ApiKey = "";
        }
        string directory = Path.GetDirectoryName(Path.GetFullPath(_filePath))!;
        Directory.CreateDirectory(directory);
        foreach (var profile in Profiles) RefreshTradingIdentity(profile);
        string temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, Profiles, Json);
                StorageSync.Flush(file);
            }
            File.Move(temporary, _filePath, overwrite: true);
            StorageSync.SyncDirectory(directory);
            _tradingPersistenceError = null;
        }
        catch (Exception ex)
        {
            _tradingPersistenceError = "Trading configuration namespace is not committed: " + ex.Message;
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Changed?.Invoke();
    }

    public string GetSecret(ConnectionProfile p) => _secretStore.Get(p.SecretAccount);

    public void SetSecret(ConnectionProfile p, string? secret)
    {
        AdvanceTradingCredentials(p);
        _secretStore.Set(p.SecretAccount, secret);
        if (p.Kind is ConnectionKind.Bybit or ConnectionKind.Longbridge && _secretStore.Get(p.SecretAccount) != (secret ?? ""))
            throw new IOException("The private credential store did not confirm the connection secret; existing settings were not replaced.");
        lock (_bybitCredentials) _bybitCredentials.Remove(p.Id);
    }

    public string GetSecret(ConnectionProfile p, string name) => _secretStore.Get(p.SecretAccountFor(name));

    public void SetSecret(ConnectionProfile p, string name, string? secret)
    {
        AdvanceTradingCredentials(p);
        _secretStore.Set(p.SecretAccountFor(name), secret);
        if (p.Kind is ConnectionKind.Bybit or ConnectionKind.Longbridge && _secretStore.Get(p.SecretAccountFor(name)) != (secret ?? ""))
            throw new IOException("The private credential store did not confirm the connection secret; existing settings were not replaced.");
        lock (_bybitCredentials) _bybitCredentials.Remove(p.Id);
    }

    /// <summary>Borra todos los secretos de una conexión (al eliminarla).</summary>
    public void ClearSecrets(ConnectionProfile p)
    {
        RetireLongbridgeContext(p.Id);
        SetSecret(p, null);
        foreach (var name in new[] { "apisecret", "token", "refresh", "apikey", "longbridge-oauth" }) SetSecret(p, name, null);
    }

    private static bool TradesNatively(ConnectionProfile p) => p.IsRithmic ||
        p.Kind is ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot or ConnectionKind.Tradovate;

    private sealed class TradingRouteState(ConnectionProfile profile)
    {
        private readonly ConnectionKind _kind = profile.Kind;
        private readonly bool _testnet = profile.Testnet;
        private readonly string _apiKey = profile.ApiKey, _rithmicUri = profile.RithmicUri,
            _rithmicSystem = profile.RithmicSystem, _rithmicUser = profile.RithmicUser,
            _tradovateUser = profile.TradovateUser, _tradovateCid = profile.TradovateCid, _tradovateAppId = profile.TradovateAppId;
        private readonly string? _binance = Environment.GetEnvironmentVariable("TRADINGHUB_BINANCE_REST"),
            _tradovateRest = Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_REST"),
            _tradovateWs = Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_WS");
        public bool Matches(ConnectionProfile p, bool endpoints = false) =>
            _kind == p.Kind && _testnet == p.Testnet && _apiKey == p.ApiKey && _rithmicUri == p.RithmicUri &&
            _rithmicSystem == p.RithmicSystem && _rithmicUser == p.RithmicUser && _tradovateUser == p.TradovateUser &&
            _tradovateCid == p.TradovateCid && _tradovateAppId == p.TradovateAppId &&
            (!endpoints || (_binance == Environment.GetEnvironmentVariable("TRADINGHUB_BINANCE_REST") &&
                _tradovateRest == Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_REST") &&
                _tradovateWs == Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_WS")));
    }

    private void RefreshTradingIdentity(ConnectionProfile profile)
    {
        if (!TradesNatively(profile)) return;
        if (profile.TradingNamespace.Length == 0) profile.TradingNamespace = Guid.NewGuid().ToString("N");
        bool exists = _tradingRoutes.TryGetValue(profile.Id, out var route);
        bool matches = route?.Matches(profile, endpoints: true) == true;
        if (profile.TradingGeneration.Length == 0 || (exists && !matches))
            profile.TradingGeneration = Guid.NewGuid().ToString("N");
        if (!matches) _tradingRoutes[profile.Id] = new(profile);
    }

    public void EnsureTradingIdentity(ConnectionProfile profile)
    {
        string oldNamespace = profile.TradingNamespace, oldGeneration = profile.TradingGeneration;
        RefreshTradingIdentity(profile);
        if (oldNamespace != profile.TradingNamespace || oldGeneration != profile.TradingGeneration || _tradingPersistenceError != null) Save();
    }

    public bool IsTradingGenerationCurrent(ConnectionProfile profile, string generation) =>
        _tradingPersistenceError == null && profile.TradingGeneration == generation &&
        _tradingRoutes.TryGetValue(profile.Id, out var route) && route.Matches(profile);

    private void AdvanceTradingCredentials(ConnectionProfile profile)
    {
        if (!TradesNatively(profile)) return;
        RefreshTradingIdentity(profile);
        profile.TradingGeneration = Guid.NewGuid().ToString("N");
        // Publish the new opaque namespace generation before changing credentials. An interrupted credential write
        // can disable additions, but must never silently reuse the previous account's ownership identity.
        if (Profiles.Contains(profile)) Save();
    }

    /// <summary>Cambió la configuración de una conexión (las de Rithmic avisan para que su sesión de órdenes también se actualice).</summary>
    public event Action<ConnectionProfile>? Reconfigured;

    /// <summary>Descarta el feed creado (por ejemplo, tras cambiar la configuración).</summary>
    public void Invalidate(ConnectionProfile p)
    {
        lock (_bybitCredentials) _bybitCredentials.Remove(p.Id);
        RetireLongbridgeContext(p.Id);
        if (p.Kind == ConnectionKind.InteractiveBrokers)
        {
            lock (_ibScopes)
                if (_ibScopes.TryGetValue(p.Id, out var ib))
                    ib.Reconfigure(new IbSettings(p.IbHost, p.IbPort, p.IbClientId, p.IbOffline || !p.Enabled || !Profiles.Contains(p), p.Id));
            Reconfigured?.Invoke(p);
            return;
        }
        // Rithmic: el mismo feed vuelve a entrar con los datos nuevos. Descartarlo dejaba la sesión vieja andando
        // en los gráficos abiertos y la nueva le cortaba la conexión (Rithmic deja una sola sesión por usuario).
        if (p.IsRithmic && _feeds.TryGetValue(p.Id, out var existing) && existing is RithmicFeed rf)
        {
            rf.Reconfigure(RithmicSettingsOf(p));
            Reconfigured?.Invoke(p);
            return;
        }

        if (_feeds.Remove(p.Id, out var f))
        {
            try
            {
                if (FeedInUse?.Invoke(f) == true) _retiredFeeds.Add(f);
                else f.Dispose();
            }
            catch { /* ignorar */ }
        }

        Reconfigured?.Invoke(p);
    }

    public IMarketDataFeed GetFeed(string profileId)
    {
        var p = Find(profileId) ?? throw new KeyNotFoundException($"Connection not found: {profileId}");
        if (p.Kind == ConnectionKind.Longbridge)
        {
            lock (_longbridgeGate)
            {
                if (_longbridgeClosing) throw new ObjectDisposedException(nameof(ConnectionManager), "Longbridge connections are closing; new work is not admitted.");
                if (_feeds.TryGetValue(p.Id, out var existing)) return existing;
                var feed = LongbridgeFeedOf(p);
                _longbridgeOwnedFeeds.Add(feed);
                _feeds[p.Id] = feed;
                return feed;
            }
        }
        if (p.Kind == ConnectionKind.InteractiveBrokers)
        {
            var settings = new IbSettings(p.IbHost, p.IbPort, p.IbClientId, p.IbOffline || !p.Enabled, p.Id);
            lock (_ibScopes)
            {
                if (!_ibScopes.TryGetValue(p.Id, out var ib)) _ibScopes[p.Id] = ib = new IbFeed(settings, AppPaths.Ticks);
                else ib.Reconfigure(settings);
                return ib;
            }
        }
        if (_feeds.TryGetValue(p.Id, out var f)) return f;
        f = p.Kind switch
        {
            ConnectionKind.Simulator => new SimulatedFeed(),
            ConnectionKind.BinanceFutures => BinanceFeedOf(futures: true),
            ConnectionKind.BinanceSpot => BinanceFeedOf(futures: false),
            ConnectionKind.Bybit => BybitFeedOf(p),
            ConnectionKind.AggregateCrypto => new AggregateFeed(new IMarketDataFeed[]
            {
                BinanceFeedOf(futures: true),
                new BybitFeed(_networkFactory().CreateHttpClient(TimeSpan.FromSeconds(20)), configureSocket: _networkFactory().Configure, ownsHttpClient: true),
            }, ownsSources: true),
            ConnectionKind.Rithmic or ConnectionKind.AmpFutures => new RithmicFeed(RithmicSettingsOf(p), network: _networkFactory()),
            ConnectionKind.Tradovate => TradovateFeedOf(p),
            ConnectionKind.CTrader => CTraderFeedOf(p),
            ConnectionKind.Oanda => new TradingHub.Feeds.Oanda.OandaFeed(OandaSettingsOf(p), network: _networkFactory()),
            ConnectionKind.Alpaca => new TradingHub.Feeds.Alpaca.AlpacaFeed(AlpacaSettingsOf(p), network: _networkFactory()),
            ConnectionKind.Databento => throw new NotSupportedException("Databento does not provide live data here: use Data to download futures history."),
            _ => throw new NotSupportedException("This connection has no integrated market-data feed."),
        };
        _feeds[p.Id] = f;
        return f;
    }

    public RithmicSettings RithmicSettingsOf(ConnectionProfile p) => new()
    {
        Uri = p.RithmicUri,
        System = p.RithmicSystem,
        User = p.RithmicUser,
        Password = GetSecret(p),
        AppName = p.RithmicAppName,
        Products = p.RithmicProducts,
        AggregatedQuotes = p.RithmicAggregatedQuotes,
        TimeoutMs = p.RithmicTimeoutMs,
    };

    public TradingHub.Feeds.Tradovate.TradovateSettings TradovateSettingsOf(ConnectionProfile p) => new()
    {
        Live = !p.Testnet,
        User = p.TradovateUser,
        Password = GetSecret(p),
        Cid = p.TradovateCid,
        Sec = GetSecret(p, "apisecret"),
        AppId = string.IsNullOrWhiteSpace(p.TradovateAppId) ? "TradingHub" : p.TradovateAppId,
        AppVersion = "1.0",
        DeviceId = p.DeviceId,
        Products = p.TradovateProducts,
        RestUrl = Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_REST") is { Length: > 0 } r ? r : null,
        UserSocketUrl = Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_WS") is { Length: > 0 } w ? w : null,
        MdSocketUrl = Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_MD") is { Length: > 0 } m ? m : null,
    };

    private TradingHub.Feeds.Tradovate.TradovateFeed TradovateFeedOf(ConnectionProfile p)
    {
        // los secretos se leen una vez (cada lectura del Llavero es un proceso)
        var settings = TradovateSettingsOf(p);
        return new TradingHub.Feeds.Tradovate.TradovateFeed(() => settings, network: _networkFactory());
    }

    public TradingHub.Feeds.CTrader.CTraderSettings CTraderSettingsOf(ConnectionProfile p) => new()
    {
        Live = !p.Testnet,
        ClientId = p.CTraderClientId,
        ClientSecret = GetSecret(p),
        AccessToken = GetSecret(p, "token"),
        RefreshToken = GetSecret(p, "refresh"),
        TraderLogin = p.CTraderLogin,
        Url = Environment.GetEnvironmentVariable("TRADINGHUB_CTRADER_URL") is { Length: > 0 } u ? u : null,
    };

    private TradingHub.Feeds.CTrader.CTraderFeed CTraderFeedOf(ConnectionProfile p)
    {
        var settings = CTraderSettingsOf(p);
        var feed = new TradingHub.Feeds.CTrader.CTraderFeed(() => settings, p.MarketDataId, network: _networkFactory());
        // la Open API renueva el token: se guarda el nuevo en el Llavero
        feed.Session.TokensRefreshed += (access, refresh) =>
        {
            settings.AccessToken = access;
            settings.RefreshToken = refresh;
            SetSecret(p, "token", access);
            SetSecret(p, "refresh", refresh);
        };
        return feed;
    }

    public TradingHub.Feeds.Oanda.OandaSettings OandaSettingsOf(ConnectionProfile p) => new()
    {
        Practice = p.Testnet,
        Token = GetSecret(p),
        AccountId = p.OandaAccount,
        RestUrl = Environment.GetEnvironmentVariable("TRADINGHUB_OANDA_REST") is { Length: > 0 } r ? r : null,
        StreamUrl = Environment.GetEnvironmentVariable("TRADINGHUB_OANDA_STREAM") is { Length: > 0 } s ? s : null,
    };

    public TradingHub.Feeds.Alpaca.AlpacaSettings AlpacaSettingsOf(ConnectionProfile p) => new()
    {
        KeyId = p.ApiKey,
        Secret = GetSecret(p),
        LiveFeed = p.AlpacaLiveFeed is "sip" ? "sip" : "iex",
        DataUrl = Environment.GetEnvironmentVariable("TRADINGHUB_ALPACA_DATA") is { Length: > 0 } d ? d : null,
        TradingUrl = Environment.GetEnvironmentVariable("TRADINGHUB_ALPACA_TRADING") is { Length: > 0 } t ? t : null,
        StreamUrl = Environment.GetEnvironmentVariable("TRADINGHUB_ALPACA_STREAM") is { Length: > 0 } st ? st : null,
    };

    /// <summary>Cliente de Databento con la API key de la conexión (TRADINGHUB_DATABENTO_URL lo apunta a otro servidor).</summary>
    public TradingHub.Feeds.Databento.DatabentoClient DatabentoOf(ConnectionProfile p) =>
        new(GetSecret(p), Environment.GetEnvironmentVariable("TRADINGHUB_DATABENTO_URL") is { Length: > 0 } u ? u : null, network: _networkFactory());

    public BinanceAccountSettings BinanceSettingsOf(ConnectionProfile p) => new()
    {
        Futures = p.Kind == ConnectionKind.BinanceFutures,
        Testnet = p.Testnet,
        ApiKey = p.ApiKey,
        ApiSecret = GetSecret(p),
        // tests point the account (orders, positions) at a local fake server, as with the other brokers' variables
        BaseUrl = Environment.GetEnvironmentVariable("TRADINGHUB_BINANCE_REST") is { Length: > 0 } u ? u : null,
    };

    public ProxyHandlerFactory NetworkFactory() => _networkFactory();

    public HttpClient CreatePrivateHttpClient(TimeSpan timeout)
    {
        var handler = _networkFactory().CreateHandler();
        handler.AllowAutoRedirect = false; // Never forward OAuth or signed custom authentication headers to a redirect target.
        return new HttpClient(handler) { Timeout = timeout };
    }

    private BinanceFeed BinanceFeedOf(bool futures)
    {
        var proxy = _networkFactory();
        return new BinanceFeed(futures: futures, httpClient: proxy.CreateHttpClient(TimeSpan.FromMinutes(5)),
            configureSocket: proxy.Configure, ownsHttpClient: true);
    }

    public BybitAccountSettings BybitSettingsOf(ConnectionProfile p)
    {
        lock (_bybitCredentials)
        {
            if (!_bybitCredentials.TryGetValue(p.Id, out var credentials))
            {
                credentials = (GetSecret(p, "apikey"), GetSecret(p));
                if (credentials.Key.Length == 0 && p.ApiKey.Length > 0)
                {
                    string legacyKey = p.ApiKey;
                    SetSecret(p, "apikey", legacyKey);
                    credentials.Key = GetSecret(p, "apikey");
                    if (credentials.Key != legacyKey)
                        throw new IOException("The private credential store did not confirm the legacy Bybit API key; the existing identifier was retained.");
                    p.ApiKey = "";
                }
                _bybitCredentials[p.Id] = credentials;
            }
            return new BybitAccountSettings
            {
                Environment = p.BybitEnvironment, ApiKey = credentials.Key, ApiSecret = credentials.Secret,
                BaseUrl = Environment.GetEnvironmentVariable("TRADINGHUB_BYBIT_REST") is { Length: > 0 } rest ? rest : null,
            };
        }
    }

    public void PreserveBybitContext(ConnectionProfile profile, BybitAccountSettings settings)
    {
        if (!settings.HasKeys) throw new InvalidOperationException("Bybit API credentials are required before creating an account context.");
        string identity = settings.ContextIdentity;
        // A nonempty entry is not proof: stale/corrupt private data must never admit unrecoverable opening exposure.
        try { if (SavedBybitContext(profile, identity) != null) return; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        SetSecret(profile, "bybit-context-" + identity, JsonSerializer.Serialize(settings));
        try
        {
            if (SavedBybitContext(profile, identity) != null) return;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new IOException("The private credential store did not confirm the durable Bybit account context; new account creation was blocked.", ex);
        }
        throw new IOException("The private credential store did not confirm the durable Bybit account context; new account creation was blocked.");
    }

    public BybitAccountSettings? SavedBybitContext(ConnectionProfile profile, string identity)
    {
        if (identity.Length != 64 || identity.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid Bybit context identity.");
        string secret = GetSecret(profile, "bybit-context-" + identity);
        if (secret.Length == 0) return null;
        var settings = JsonSerializer.Deserialize<BybitAccountSettings>(secret) ?? throw new InvalidDataException("Invalid saved Bybit credential context.");
        if (!settings.HasKeys || settings.ContextIdentity != identity) throw new InvalidDataException("Saved Bybit credentials do not match their durable context.");
        return settings;
    }

    public void ForgetIdleBybitContext(ConnectionProfile profile, string identity) => SetSecret(profile, "bybit-context-" + identity, null);

    public string BybitContextOf(ConnectionProfile p) => BybitSettingsOf(p).ContextIdentity;
    public bool HasTradingCredentials(ConnectionProfile p) => p.Kind == ConnectionKind.Bybit
        ? p.ApiKey.Length > 0 ? GetSecret(p).Length > 0 : BybitSettingsOf(p).HasKeys
        : !string.IsNullOrWhiteSpace(p.ApiKey);

    private BybitFeed BybitFeedOf(ConnectionProfile p)
    {
        bool testnet = p.BybitEnvironment == BybitEnvironment.Testnet;
        var proxy = _networkFactory();
        string rest = Environment.GetEnvironmentVariable("TRADINGHUB_BYBIT_MARKET_REST") ??
            (testnet ? "https://api-testnet.bybit.com" : "https://api.bybit.com");
        string ws = Environment.GetEnvironmentVariable("TRADINGHUB_BYBIT_MARKET_WS") ??
            (testnet ? "wss://stream-testnet.bybit.com/v5/public/linear" : "wss://stream.bybit.com/v5/public/linear");
        return new BybitFeed(proxy.CreateHttpClient(TimeSpan.FromSeconds(20)), new Uri(rest), new Uri(ws), proxy.Configure,
            ownsHttpClient: true, feedId: p.MarketDataId);
    }

    public LongbridgeSession LongbridgeSessionFor(ConnectionProfile p)
    {
        lock (_longbridgeGate)
        {
            if (_longbridgeClosing) throw new ObjectDisposedException(nameof(ConnectionManager), "Longbridge connections are closing; new work is not admitted.");
            if (_longbridgeSessions.TryGetValue(p.Id, out var existing)) return existing.Session;
            long generation = _longbridgeGenerations.GetValueOrDefault(p.Id) + 1;
            _longbridgeGenerations[p.Id] = generation;
            var http = _longbridgeHttpFactory?.Invoke(p, _networkFactory()) ?? CreatePrivateHttpClient(TimeSpan.FromSeconds(25));
            try
            {
                var snapshot = JsonSerializer.Deserialize<LongbridgeSettings>(JsonSerializer.Serialize(p.Longbridge))!;
                bool Current() => _longbridgeGenerations.GetValueOrDefault(p.Id) == generation &&
                    p.Kind == ConnectionKind.Longbridge && p.Longbridge.ClientId == snapshot.ClientId &&
                    p.Longbridge.Region == snapshot.Region && p.Longbridge.BaseUrl == snapshot.BaseUrl && p.Longbridge.QuoteWsUrl == snapshot.QuoteWsUrl;
                var session = new LongbridgeSession(snapshot, http, () =>
                {
                    lock (_longbridgeGate)
                    {
                        if (!Current()) throw new InvalidOperationException("Longbridge credential context was replaced.");
                        return GetSecret(p, "longbridge-oauth");
                    }
                }, value =>
                {
                    lock (_longbridgeGate)
                    {
                        if (!Current()) throw new InvalidOperationException("Longbridge credential context was replaced; no old tokens were saved.");
                        SetSecret(p, "longbridge-oauth", value);
                    }
                }, clock: _longbridgeClock);
                _longbridgeSessions[p.Id] = (session, http);
                return session;
            }
            catch { http.Dispose(); throw; }
        }
    }

    private void RetireLongbridgeContext(string profileId)
    {
        (LongbridgeSession Session, HttpClient Http)? context = null;
        lock (_longbridgeGate)
        {
            _longbridgeGenerations[profileId] = _longbridgeGenerations.GetValueOrDefault(profileId) + 1;
            if (_longbridgeSessions.Remove(profileId, out var retired))
            {
                context = retired;
                _retiredLongbridge.Add(retired);
            }
        }
        // Do not hold the manager gate while taking the session's credential gate.
        context?.Session.Retire();
    }

    private LongbridgeFeed LongbridgeFeedOf(ConnectionProfile p)
    {
        var proxy = _networkFactory();
        var session = LongbridgeSessionFor(p);
        var transport = _longbridgeTransportFactory?.Invoke(p, session);
        if (transport != null) _longbridgeOwnedTransports.Add(transport);
        var feed = new LongbridgeFeed(p.Longbridge, session, transport: transport, socketFactory: () =>
        {
            var socket = new System.Net.WebSockets.ClientWebSocket();
            proxy.Configure(socket.Options);
            return socket;
        }, feedId: p.MarketDataId, network: proxy);
        if (transport != null) _longbridgeOwnedTransports.Remove(transport); // The fully constructed feed now owns terminal transport disposal.
        return feed;
    }

    /// <summary>Prueba una conexión y devuelve un mensaje para mostrar.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(ConnectionProfile p, CancellationToken ct)
    {
        try
        {
            switch (p.Kind)
            {
                case ConnectionKind.Simulator:
                    return (true, "The simulator does not require a connection.");
                case ConnectionKind.BinanceFutures:
                case ConnectionKind.BinanceSpot:
                {
                    using var feed = BinanceFeedOf(p.Kind == ConnectionKind.BinanceFutures);
                    var inst = await feed.GetInstrumentsAsync(ct);
                    using var acc = new BinanceAccountClient(BinanceSettingsOf(p), network: _networkFactory());
                    var (ok, msg) = await acc.TestAsync(ct);
                    return (ok, $"{inst.Count} instruments available. {msg}");
                }
                case ConnectionKind.Bybit:
                {
                    using var feed = BybitFeedOf(p);
                    var instruments = await feed.GetInstrumentsAsync(ct);
                    using var http = CreatePrivateHttpClient(TimeSpan.FromSeconds(20));
                    using var client = new BybitAccountClient(BybitSettingsOf(p), http);
                    var (ok, message) = await client.TestAsync(ct);
                    return (ok, $"{instruments.Count} perpetuals available. {message}");
                }
                case ConnectionKind.Longbridge:
                {
                    var instruments = await GetFeed(p.Id).GetInstrumentsAsync(ct);
                    return (true, $"{instruments.Count} configured Longbridge instruments. Data only; recent tape is not full historical order flow.");
                }
                case ConnectionKind.AggregateCrypto:
                {
                    var instruments = await GetFeed(p.Id).GetInstrumentsAsync(ct);
                    return (true, $"{instruments.Count} composite instruments. Non-executable prices; DOM is Binance Futures only. Constituent failures remain visible in coverage.");
                }
                case ConnectionKind.Rithmic:
                case ConnectionKind.AmpFutures:
                {
                    var systems = await RithmicClient.ListSystemsAsync(p.RithmicUri, ct, network: _networkFactory());
                    if (string.IsNullOrWhiteSpace(p.RithmicUser))
                        return (true, $"Server OK: {systems.Count} systems available. Enter username and password to sign in.");
                    // con la misma sesión de los gráficos: un login aparte cortaría la que está andando
                    var (ok, message) = await ((RithmicFeed)GetFeed(p.Id)).TestAsync(ct);
                    return (ok, ok ? $"Connected to {p.RithmicSystem}. {message}" : message);
                }
                case ConnectionKind.InteractiveBrokers:
                {
                    var feed = (IbFeed)GetFeed(p.Id);
                    if (!feed.IsLive) return (true, "Stored/offline or disabled mode: no Gateway connection. Synthetic OHLCV history is not order-flow data.");
                    var client = await feed.ClientAsync(ct);
                    return (true, $"IB protocol {client.ServerVersion} START_API ready; exchange historical/live/depth entitlements are checked only when data is requested. Data only; no orders.");
                }
                case ConnectionKind.Tradovate:
                    return await ((TradingHub.Feeds.Tradovate.TradovateFeed)GetFeed(p.Id)).TestAsync(ct);
                case ConnectionKind.CTrader:
                    return await ((TradingHub.Feeds.CTrader.CTraderFeed)GetFeed(p.Id)).TestAsync(ct);
                case ConnectionKind.Oanda:
                    return await ((TradingHub.Feeds.Oanda.OandaFeed)GetFeed(p.Id)).TestAsync(ct);
                case ConnectionKind.Alpaca:
                    return await ((TradingHub.Feeds.Alpaca.AlpacaFeed)GetFeed(p.Id)).TestAsync(ct);
                case ConnectionKind.Databento:
                {
                    if (string.IsNullOrWhiteSpace(GetSecret(p))) return (false, "Databento API key is missing.");
                    using var db = DatabentoOf(p);
                    var (start, end) = await db.RangeAsync(ct);
                    // costo de un día de NQ, para que se vea cuánto rinden los créditos (no baja nada)
                    var day = DateOnly.FromDateTime(end.AddDays(-3));
                    while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);
                    decimal cost = await db.CostAsync("NQ", day, day, ct);
                    return (true, $"API key OK. Globex from {start:dd/MM/yyyy} to {end:dd/MM/yyyy}. One day of NQ tick data costs approximately US$ {cost:0.00} from your credits.");
                }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        return (false, "Unknown connection type");
    }

    internal void ReleaseRetiredFeeds()
    {
        for (int i = _retiredFeeds.Count - 1; i >= 0; i--)
        {
            var feed = _retiredFeeds[i];
            if (FeedInUse?.Invoke(feed) == true) continue;
            _retiredFeeds.RemoveAt(i);
            try { feed.Dispose(); }
            catch { /* Feed disposal must not prevent releasing other idle transports. */ }
        }
    }

    internal Task RunLongbridgeAuthorizationAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        CancellationTokenSource lifetime;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_longbridgeGate)
        {
            if (_longbridgeClosing) throw new ObjectDisposedException(nameof(ConnectionManager), "Longbridge authorization is closing; new work is not admitted.");
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _longbridgeLifetime.Token);
            _longbridgeAuthorizations.Add(completion.Task);
        }
        return RunAsync();

        async Task RunAsync()
        {
            try { await action(lifetime.Token).ConfigureAwait(false); }
            finally
            {
                lifetime.Dispose();
                lock (_longbridgeGate) _longbridgeAuthorizations.Remove(completion.Task);
                completion.TrySetResult();
            }
        }
    }

    /// <summary>Closes Longbridge admission and drains every cached/retired feed, OAuth request and loopback authorization before private HTTP disposal.</summary>
    public Task StopLongbridgeAsync()
    {
        TaskCompletionSource completion;
        (LongbridgeSession Session, HttpClient Http)[] contexts;
        LongbridgeFeed[] feeds;
        ILongbridgeTransport[] transports;
        Task[] authorizations;
        lock (_longbridgeGate)
        {
            if (_longbridgeStop != null) return _longbridgeStop;
            _longbridgeClosing = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _longbridgeStop = completion.Task;
            contexts = _longbridgeSessions.Values.Concat(_retiredLongbridge).ToArray();
            feeds = _longbridgeOwnedFeeds.ToArray();
            transports = _longbridgeOwnedTransports.ToArray();
            authorizations = _longbridgeAuthorizations.ToArray();
        }
        _ = CompleteLongbridgeStopAsync(completion, contexts, feeds, transports, authorizations);
        return completion.Task;
    }

    private async Task CompleteLongbridgeStopAsync(TaskCompletionSource completion,
        (LongbridgeSession Session, HttpClient Http)[] contexts, LongbridgeFeed[] feeds,
        ILongbridgeTransport[] transports, Task[] authorizations)
    {
        var errors = new List<Exception>();
        var drains = new List<Task>(authorizations);
        try { _longbridgeLifetime.Cancel(); } catch (Exception error) { errors.Add(error); }
        foreach (var feed in feeds)
            try { drains.Add(feed.DisposeAsync().AsTask()); } catch (Exception error) { errors.Add(error); }
        foreach (var transport in transports)
        {
            try
            {
                if (transport is IAsyncDisposable asynchronous) drains.Add(asynchronous.DisposeAsync().AsTask());
                else transport.Dispose();
            }
            catch (Exception error) { errors.Add(error); }
        }
        foreach (var context in contexts)
            try { drains.Add(context.Session.DisposeAsync().AsTask()); } catch (Exception error) { errors.Add(error); }
        try { await Task.WhenAll(drains).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        foreach (var context in contexts)
            try { context.Http.Dispose(); } catch (Exception error) { errors.Add(error); }
        lock (_longbridgeGate)
        {
            _longbridgeSessions.Clear();
            _retiredLongbridge.Clear();
            _longbridgeOwnedFeeds.Clear();
            _longbridgeOwnedTransports.Clear();
        }
        _longbridgeLifetime.Dispose();
        if (errors.Count > 0) completion.TrySetException(new AggregateException("Longbridge shutdown encountered errors after draining owned work.", errors));
        else completion.TrySetResult();
    }


    public void DisposeAll()
    {
        _ = StopLongbridgeAsync(); // The application awaits this owned barrier before disposing shared/private resources.
        foreach (var ib in _ibScopes.Values) ib.Dispose();
        _ibScopes.Clear();
        foreach (var f in _feeds.Values)
        {
            try { f.Dispose(); }
            catch { /* ignorar */ }
        }

        _feeds.Clear();
        foreach (var feed in _retiredFeeds)
        {
            try { feed.Dispose(); }
            catch { /* Continue closing the remaining transports. */ }
        }
        _retiredFeeds.Clear();
    }
}
