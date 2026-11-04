// Adapted from PANASV/trading-hub cdb84ea16eee7ac8d6c1b74136d2eeacedadf834.
// Copyright (c) 2026 ralphtradesz and Trading Hub contributors; MIT with Commons Clause (repository LICENSE).
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Feeds.InteractiveBrokers;
public sealed record IbSettings(string Host, int Port, int ClientId, bool Offline = false, string ProfileId = "ib");
public sealed class IbFeed : IMarketDataFeed, IBarHistoryFeed, IDataScopeFeed, ISessionSettingsFeed, IHistoryRefreshFeed, IKlineHistory, ISessionAwareFeed
{
    public const string FeedId = IbBarImport.FeedId;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private volatile IbSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _root;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Action<string[]>> _routes = new();
    private volatile IbClient? _client;
    private readonly object _configurationGate = new();
    private int _configurationGeneration, _clientGeneration;
    private CancellationTokenSource _configurationLifetime = new();
    private bool _disposed;
    public IbFeed(IbSettings settings, string? root = null)
    {
        _settings = settings;
        _root = root == null ? null : Path.GetFullPath(root);
        if (_root != null) IbBarStore.HistoryChanged += StoredHistoryChanged;
    }
    public event Action<string?>? HistoryInvalidated;
    private void StoredHistoryChanged(string root, string symbol)
    {
        if (!_disposed && _root != null && Path.GetFullPath(root).Equals(_root, StringComparison.Ordinal))
            HistoryInvalidated?.Invoke(symbol);
    }

    /// <summary>Invalidates old subscriptions/history; retained InstrumentData rebinds through HistoryInvalidated without changing market identity.</summary>
    public void Reconfigure(IbSettings settings)
    {
        lock (_configurationGate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IbFeed));
            if (settings.ProfileId != _settings.ProfileId) throw new InvalidOperationException("An IB feed cannot change its owning profile.");
            if (settings == _settings) return;
            _settings = settings;
            _configurationGeneration++;
            _client?.Dispose();
            var previous = _configurationLifetime;
            _configurationLifetime = new();
            previous.Cancel();
        }
        HistoryInvalidated?.Invoke(null);
    }

    private int ConfigurationGeneration => Volatile.Read(ref _configurationGeneration);
    internal void EnsureCurrent(int generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (generation != ConfigurationGeneration) throw new IOException("IB configuration changed; the pending historical operation was canceled. Request history again.");
    }

    private CancellationToken ConfigurationToken
    {
        get { lock (_configurationGate) return _configurationLifetime.Token; }
    }

    public string Id => FeedId;
    public string DisplayName => $"IB {_settings.ProfileId} · {_settings.Host}:{_settings.Port} · client {_settings.ClientId}";
    public bool SupportsHistory => true;
    public bool IsLive => !_settings.Offline;
    public string DataScopeKey => $"ib:{_settings.ProfileId}:calendar-roll:NY18";
    public SessionSettings GetSessionSettings(Instrument instrument) => Session;
    public static SessionSettings Session { get; } = new() { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), Start = TimeSpan.FromHours(18) };
    public string CacheTag => "ib";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>IB's same-instrument tick request pacing. Zero is reserved for isolated fake transports.</summary>
    public TimeSpan TickRequestInterval { get; set; } = TimeSpan.FromSeconds(15);
    private static readonly object TickPacingGate = new();
    private static readonly Dictionary<string, DateTime> NextTickRequest = new(StringComparer.OrdinalIgnoreCase);

    private async Task WaitForTickRequestSlotAsync(IbClient client, Contract contract, CancellationToken ct)
    {
        var interval = TickRequestInterval;
        if (interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(TickRequestInterval));
        if (interval > TimeSpan.Zero)
        {
            TimeSpan delay;
            string key = $"{_settings.Host}:{_settings.Port}:{contract.Symbol}:{contract.MonthCode}";
            lock (TickPacingGate)
            {
                var now = DateTime.UtcNow;
                var ready = NextTickRequest.TryGetValue(key, out var next) && next > now ? next : now;
                delay = ready - now;
                NextTickRequest[key] = ready + interval;
            }
            if (delay > TimeSpan.Zero)
            {
                var waiting = Task.Delay(delay, ct);
                if (await Task.WhenAny(waiting, client.Closed).ConfigureAwait(false) != waiting)
                    throw new IOException(await client.Closed.ConfigureAwait(false) ?? "IB closed while pacing a subscription.");
                await waiting.ConfigureAwait(false);
            }
        }
        ct.ThrowIfCancellationRequested();
    }


    public async Task<IbClient> ClientAsync(CancellationToken ct)
    {
        var c = _client;
        if (c is { IsConnected: true }) return c;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IbFeed));
            c = _client;
            if (c is { IsConnected: true }) return c;
            if (c != null)
            {
                c.Dispose();
                await c.ReaderStopped.WaitAsync(ct).ConfigureAwait(false);
                c.Message -= Route;
            }
            IbSettings s;
            int generation;
            CancellationToken configurationToken;
            lock (_configurationGate) { s = _settings; generation = _configurationGeneration; configurationToken = _configurationLifetime.Token; }
            using var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, configurationToken);
            if (s.Offline) throw new InvalidOperationException("IB stored/offline mode never connects to Gateway.");
            try
            {
                c = await IbClient.ConnectAsync(s.Host, s.Port, s.ClientId, connectCancellation.Token).ConfigureAwait(false);
            }
            catch (IbException ex) when (ex.Code == 326)
            {
                throw new IbException(326, string.Create(UiCulture.Current, $"IB client ID {s.ClientId} is already in use; choose a unique ID in Connections → Interactive Brokers."));
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new IOException(string.Create(UiCulture.Current, $"IB Gateway is unavailable at {s.Host}:{s.Port}. Open TWS/Gateway, sign in and enable the socket API."), ex);
            }

            lock (_configurationGate)
            {
                if (_disposed || generation != _configurationGeneration || _settings.Offline)
                {
                    c.Dispose();
                    throw new IOException("IB transport configuration changed during START_API; the old connection was closed.");
                }
                c.Message += Route;
                _clientGeneration = generation;
                _client = c;
                return c;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void Route(string[] f)
    {
        lock (_configurationGate)
        {
            if (_disposed || _client?.IsConnected != true || _clientGeneration != _configurationGeneration || f.Length < 2) return;
            int reqId;
            switch (f[0])
            {
                case "4":
                    reqId = IbClient.Error(f).Id;
                    if (reqId < 0 && IbClient.Error(f).Code is 1100 or 1101 or 1102) _client?.Dispose();
                    break;
                case "17":
                case "99":
                    reqId = int.Parse(f[1], NumberStyles.Integer, Inv);
                    break;
                case "12":
                case "13":
                    if (f.Length < 3) throw new InvalidDataException("Truncated IB depth route.");
                    reqId = int.Parse(f[2], NumberStyles.Integer, Inv);
                    break;
                default:
                    return;
            }
            if (_routes.TryGetValue(reqId, out var handler)) handler(f);
        }
    }


    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = new List<Instrument>(IbBarImport.SupportedRoots.Count);
        var now = DateTime.UtcNow;
        ct.ThrowIfCancellationRequested();
        foreach (var root in IbBarImport.SupportedRoots)
        {
            var spec = FuturesCatalog.Find(root) ?? throw new InvalidDataException($"IB catalog metadata is missing for {root}.");
            list.Add(new Instrument
            {
                Symbol = spec.Root,
                FeedId = FeedId,
                Exchange = spec.Exchange,
                TickSize = spec.Tick,
                PointValue = spec.PointValue,
                VolumeStep = 1,
                Description = $"{spec.Name} · IB ({FuturesRolls.On(root, now).Symbol})",
            });
        }

        return Task.FromResult<IReadOnlyList<Instrument>>(list);
    }
    public sealed record Contract(string Symbol, string Exchange, int Year, int Month, bool IncludeExpired)
    {
        public string MonthCode => $"{Year:0000}{Month:00}";
        public object?[] Fields(bool withPrimaryExchange = true) => withPrimaryExchange
            ? new object?[] { 0, Symbol, "FUT", MonthCode, 0d, "", "", Exchange, "", "USD", "", "" }
            : new object?[] { 0, Symbol, "FUT", MonthCode, 0d, "", "", Exchange, "USD", "", "" };
    }
    public static Contract ContractOn(Instrument inst, DateTime utc)
    {
        if (!IbBarImport.SupportedRoots.Contains(inst.Symbol))
            throw new NotSupportedException($"{inst.Symbol}: IB data supports only MNQ, MES, M2K, MYM, NQ, ES, RTY and YM.");
        var (y, m) = FuturesRolls.ContractMonth(utc);
        bool expired = FuturesRolls.Expiry(y, m).AddDays(1) < DateTime.UtcNow;
        return new Contract(inst.Symbol, string.IsNullOrEmpty(inst.Exchange) ? "CME" : inst.Exchange, y, m, expired);
    }


    public readonly record struct Bar(DateTime OpenUtc, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);
    public static object?[] HistoricalRequest(int reqId, Contract c, DateTime endUtc, string duration, string barSize)
    {
        var f = new List<object?> { 20, reqId };
        f.AddRange(c.Fields());
        f.Add(c.IncludeExpired);
        f.Add(endUtc.ToString("yyyyMMdd-HH:mm:ss", Inv));
        f.Add(barSize);
        f.Add(duration);
        f.Add(0);
        f.Add("TRADES");
        f.Add(2);
        f.Add(false);
        f.Add("");
        return f.ToArray();
    }
    public static List<Bar> ParseHistorical(string[] f)
    {
        var r = new IbReader(f, 2);
        r.ReadString();
        r.ReadString();
        int n = r.ReadInt();
        if (n < 0 || n > 100000 || f.Length != 5 + n * 8) throw new InvalidDataException("Malformed IB historical bar count/layout.");
        var bars = new List<Bar>(Math.Max(0, n));
        for (int i = 0; i < n; i++)
        {
            string date = r.ReadString();
            double o = r.ReadDouble(), h = r.ReadDouble(), l = r.ReadDouble(), c = r.ReadDouble();
            decimal vol = r.ReadDecimal();
            r.ReadDecimal();
            r.ReadInt();
            long secs = long.Parse(date, NumberStyles.Integer, Inv);
            if (o <= 0 || l <= 0 || h < l || o < l || o > h || c < l || c > h || vol < 0)
                throw new InvalidDataException("IB historical response contains invalid OHLCV.");
            if (vol == 0) continue;
            bars.Add(new Bar(DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime, (decimal)o, (decimal)h, (decimal)l, (decimal)c, vol));
        }

        return bars;
    }
    public async Task<List<Bar>> BarsAsync(Contract c, DateTime endUtc, string duration, string barSize, CancellationToken ct)
    {
        int generation = ConfigurationGeneration;
        var client = await ClientAsync(ct).ConfigureAwait(false);
        EnsureCurrent(generation, ct);
        int reqId = client.NextRequestId();
        var tcs = new TaskCompletionSource<List<Bar>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _routes[reqId] = f =>
        {
            if (f[0] == "17") tcs.TrySetResult(ParseHistorical(f));
            else if (f[0] == "4")
            {
                var (_, code, text) = IbClient.Error(f);

                if (code == 162 && text.Contains("returned no data", StringComparison.OrdinalIgnoreCase)) tcs.TrySetResult(new List<Bar>());
                else if (code is not (2100 or 2104 or 2106 or 2107 or 2108 or 2158)) tcs.TrySetException(new IbException(code, $"IB {code}: {text}"));
            }
        };
        try
        {
            client.Send(HistoricalRequest(reqId, c, endUtc, duration, barSize));
            var done = await Task.WhenAny(tcs.Task, client.Closed, Task.Delay(RequestTimeout, ct)).ConfigureAwait(false);
            if (done == tcs.Task)
            {
                var bars = await tcs.Task.ConfigureAwait(false);
                EnsureCurrent(generation, ct);
                return bars;
            }
            ct.ThrowIfCancellationRequested();
            if (done == client.Closed) throw new IOException(await client.Closed.ConfigureAwait(false) ?? "IB Gateway closed the connection");
            throw new TimeoutException("IB historical data request timed out.");
        }
        finally
        {
            _routes.TryRemove(reqId, out _);
            if (client.IsConnected) client.Send(25, 1, reqId);
        }
    }

    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_settings.Offline) return Task.FromResult<DateTime?>(null); // IB does not promise a complete earliest date.
        using var snapshot = IbBarStore.OpenSnapshot(_root ?? throw new InvalidOperationException("IB storage root is required."), instrument.Symbol, ct);
        var days = snapshot.AvailableDays();
        return Task.FromResult<DateTime?>(days.Count == 0 ? null : days[0].ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }
    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        int generation = ConfigurationGeneration;
        ct.ThrowIfCancellationRequested();
        if (minutes is not (1 or 5 or 15)) throw new ArgumentOutOfRangeException(nameof(minutes), "IB bar history supports 1, 5 and 15 minute requests.");
        if (_settings.Offline)
        {
            var rows = IbBarStore.LoadRange(_root ?? throw new InvalidOperationException("IB storage root is required."), instrument.Symbol, fromUtc, toUtc, ct, instrument.TickSize);
            var stored = rows.GroupBy(t => t.Time.Ticks / (TimeSpan.TicksPerMinute * minutes))
                .Select(g => new Kline(new DateTimeOffset(new DateTime(g.Key * TimeSpan.TicksPerMinute * minutes, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    g.First().Price, g.Max(t => t.Price), g.Min(t => t.Price), g.Last().Price, g.Sum(t => t.Volume), 0, false)).ToList();
            lock (_configurationGate)
            {
                EnsureCurrent(generation, ct);
                if (stored.Count > 0 && !onBatch(stored)) return;
            }
            progress?.Report(1);
            return;
        }
        await foreach (var bars in BarChunksAsync(instrument, fromUtc, toUtc, minutes, progress, ct).ConfigureAwait(false))
        {
            var klines = bars.Select(b => new Kline(new DateTimeOffset(b.OpenUtc).ToUnixTimeMilliseconds(), b.Open, b.High, b.Low, b.Close, b.Volume, 0, false)).ToList();
            lock (_configurationGate)
            {
                EnsureCurrent(generation, ct);
                if (klines.Count > 0 && !onBatch(klines)) return;
            }
        }
        progress?.Report(1);
    }

    private async Task<List<Bar>> RangeAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, IProgress<double>? progress, CancellationToken ct)
    {
        int generation = ConfigurationGeneration;
        var result = new List<Bar>();
        await foreach (var batch in BarChunksAsync(instrument, fromUtc, toUtc, minutes, progress, ct).ConfigureAwait(false))
        {
            EnsureCurrent(generation, ct);
            result.AddRange(batch);
        }
        result.Sort((a, b) => a.OpenUtc.CompareTo(b.OpenUtc));
        EnsureCurrent(generation, ct);
        return result;
    }

    private async IAsyncEnumerable<List<Bar>> BarChunksAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes,
        IProgress<double>? progress, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        int generation = ConfigurationGeneration;
        if (!IbBarImport.SupportedRoots.Contains(instrument.Symbol)) throw new NotSupportedException("Unsupported IB data root.");
        var now = DateTime.UtcNow;
        if (toUtc > now) toUtc = now;
        if (toUtc <= fromUtc) yield break;
        string barSize = minutes switch { 1 => "1 min", 5 => "5 mins", 15 => "15 mins", _ => throw new ArgumentOutOfRangeException(nameof(minutes)) };
        var chunk = minutes == 1 ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
        double total = (toUtc - fromUtc).Ticks;
        foreach (var (_, segFrom, segTo) in FuturesRolls.Segments(instrument.Symbol, fromUtc, toUtc))
        {
            var contract = ContractOn(instrument, segFrom);
            for (var start = segFrom; start < segTo; start += chunk)
            {
                ct.ThrowIfCancellationRequested();
                var end = start + chunk < segTo ? start + chunk : segTo;
                var span = end - start;
                EnsureCurrent(generation, ct);
                string duration = span <= TimeSpan.FromDays(1) ? $"{Math.Max(60, (int)Math.Ceiling(span.TotalSeconds))} S" : $"{(int)Math.Ceiling(span.TotalDays)} D";
                var bars = await BarsAsync(contract, end, duration, barSize, ct).ConfigureAwait(false);
                yield return bars.Where(b => b.OpenUtc >= start && b.OpenUtc < end && b.OpenUtc.AddMinutes(minutes) <= now).OrderBy(b => b.OpenUtc).ToList();
                progress?.Report((end - fromUtc).Ticks / total);
            }
        }
    }
    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        int generation = ConfigurationGeneration;
        if (_settings.Offline)
        {
            var stored = await LoadBarHistoryAsync(instrument, fromUtc, toUtc, ct).ConfigureAwait(false);
            lock (_configurationGate)
            {
                EnsureCurrent(generation, ct);
                if (stored.Count > 0 && !onBatch(stored)) return;
            }
            progress?.Report(1);
            return;
        }
        await LoadKlinesAsync(instrument, fromUtc, toUtc, 1, 0, klines =>
        {
            var synthetic = new List<Trade>(klines.Count * 4);
            KlineHistory.Synthesize(klines, instrument, 1, new[] { (fromUtc, toUtc) }, synthetic);
            return synthetic.Count == 0 || onBatch(synthetic);
        }, progress, ct).ConfigureAwait(false);
    }
    public async Task<DateTime?> LastTradeTimeAsync(Instrument instrument, CancellationToken ct)
    {
        if (_settings.Offline)
        {
            using var snapshot = IbBarStore.OpenSnapshot(_root ?? throw new InvalidOperationException("IB storage root is required."), instrument.Symbol, ct);
            var days = snapshot.AvailableDays();
            if (days.Count == 0) return null;
            var rows = snapshot.ReadDay(days[^1], instrument.TickSize);
            return rows.Count == 0 ? null : rows[^1].Time;
        }
        var now = DateTime.UtcNow;
        var bars = await BarsAsync(ContractOn(instrument, now), now, "3 D", "1 min", ct).ConfigureAwait(false);
        return bars.Count == 0 ? null : bars[^1].OpenUtc.AddMinutes(1);
    }

    public async Task<IReadOnlyList<Trade>> LoadBarHistoryAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
    {
        int generation = await EnsureBarHistoryAsync(instrument, from, to, ct).ConfigureAwait(false);
        var stored = IbBarStore.LoadRange(HistoryRoot, instrument.Symbol, from, to, ct, instrument.TickSize).Where(t => t.Id < 0).ToList();
        EnsureCurrent(generation, ct);
        return stored;
    }

    internal string HistoryRoot => _root ?? throw new InvalidOperationException("IB storage root is required.");

    internal async Task<int> EnsureBarHistoryAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
    {
        int generation = ConfigurationGeneration;
        ct.ThrowIfCancellationRequested();
        if (_disposed) throw new ObjectDisposedException(nameof(IbFeed));
        if (_root == null) throw new InvalidOperationException("IB storage root is required.");
        if (!_settings.Offline)
        {
            var bars = await RangeAsync(instrument, from, to, 1, null, ct).ConfigureAwait(false);
            var klines = bars.Select(b => new Kline(new DateTimeOffset(b.OpenUtc).ToUnixTimeMilliseconds(), b.Open, b.High, b.Low, b.Close, b.Volume, 0, false)).ToList();
            lock (_configurationGate)
            {
                EnsureCurrent(generation, ct);
                IbBarStore.MergeBars(_root, instrument, klines, ct);
            }
        }
        EnsureCurrent(generation, ct);
        return generation;
    }


    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(IbFeed));
        var sub = new LiveSub(this, instrument, sink);
        sub.Start();
        return sub;
    }
    public static object?[] TickByTickRequest(int reqId, Contract c, string tickType)
    {
        var f = new List<object?> { 97, reqId };
        f.AddRange(c.Fields());
        f.Add(tickType);
        f.Add(0);
        f.Add(false);
        return f.ToArray();
    }
    public static object?[] DepthRequest(int reqId, Contract c, int rows)
    {
        var f = new List<object?> { 10, 5, reqId };
        f.AddRange(c.Fields());
        f.Add(rows);
        f.Add(false);
        f.Add("");
        return f.ToArray();
    }
    public static TradeSide Aggressor(decimal price, decimal bid, decimal ask, decimal lastPrice, TradeSide lastSide)
    {
        if (ask > 0 && price >= ask) return TradeSide.Buy;
        if (bid > 0 && price <= bid) return TradeSide.Sell;
        if (lastPrice > 0 && price > lastPrice) return TradeSide.Buy;
        if (lastPrice > 0 && price < lastPrice) return TradeSide.Sell;
        return lastSide;
    }

    private sealed class LiveSub(IbFeed feed, Instrument instrument, IMarketDataSink sink) : IDisposable, IReconnectable
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(feed._lifetime.Token, feed.ConfigurationToken);
        private readonly OrderBookBuilder _book = new();
        private readonly List<(decimal Price, decimal Size)>[] _depth = { new(), new() };
        private readonly object _lock = new();
        private IbClient? _client;
        private int _activeTradesId, _activeQuotesId, _activeDepthId;
        private decimal _bid, _ask, _last, _bidSize, _askSize;
        private TradeSide _lastSide;
        private DateTime _lastTime;
        private long _id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        private bool _depthLive;
        private string? _tradeError;

        public void Start() => _ = Task.Run(RunAsync);

        public void Reconnect()
        {
            _client?.Dispose();
        }
        private void CancelRequests(IbClient? expected = null)
        {
            lock (_lock)
            {
                var client = _client;
                if (expected != null && !ReferenceEquals(expected, client)) return;
                int trades = _activeTradesId, quotes = _activeQuotesId, depth = _activeDepthId;
                _activeTradesId = _activeQuotesId = _activeDepthId = 0;
                feed._routes.TryRemove(trades, out _);
                feed._routes.TryRemove(quotes, out _);
                feed._routes.TryRemove(depth, out _);
                if (client is not { IsConnected: true }) return;
                try
                {
                    if (trades != 0) client.Send(98, trades);
                    if (quotes != 0) client.Send(98, quotes);
                    if (depth != 0) client.Send(11, 1, depth, false);
                }
                catch (IOException error) { sink.OnStatus(instrument, FeedStatus.Disconnected, "IB cancellation transport closed: " + error.Message); }
                catch (ObjectDisposedException error) { sink.OnStatus(instrument, FeedStatus.Disconnected, "IB cancellation transport closed: " + error.Message); }
            }
        }


        private async Task RunAsync()
        {
            var ct = _cts.Token;
            int attempt = 0;
            while (!ct.IsCancellationRequested)
            {
                int tradesId = 0, quotesId = 0, depthId = 0;
                IbClient? client = null;
                try
                {
                    if (feed._settings.Offline)
                    {
                        lock (_lock)
                        {
                            _bid = _ask = _last = _bidSize = _askSize = 0;
                            _depthLive = false; _tradeError = null; _lastSide = default;
                            _depth[0].Clear(); _depth[1].Clear(); _book.Clear();
                        }
                        sink.OnDepth(instrument, _book.BuildUpdate(DateTime.UtcNow, fullRefresh: true));
                        sink.OnStatus(instrument, FeedStatus.Disconnected, $"{feed.DisplayName} · stored/offline mode; no Gateway connection");
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                        continue;
                    }
                    sink.OnStatus(instrument, attempt == 0 ? FeedStatus.Connecting : FeedStatus.Reconnecting, "Connecting to " + feed.DisplayName);
                    client = await feed.ClientAsync(ct).ConfigureAwait(false);
                    _client = client;
                    var contract = ContractOn(instrument, DateTime.UtcNow);
                    tradesId = client.NextRequestId();
                    quotesId = client.NextRequestId();
                    depthId = client.NextRequestId();
                    _depthLive = false;
                    lock (_lock)
                    {
                        _bid = _ask = _last = _bidSize = _askSize = 0;
                        _lastSide = default;
                        _tradeError = null;
                        _book.Clear();
                        _depth[0].Clear();
                        _depth[1].Clear();
                    }
                    sink.OnDepth(instrument, _book.BuildUpdate(DateTime.UtcNow, fullRefresh: true));

                    feed._routes[tradesId] = f => OnMessage(f, Channel.Trades);
                    feed._routes[quotesId] = f => OnMessage(f, Channel.Quotes);
                    feed._routes[depthId] = f => OnMessage(f, Channel.Depth);
                    string name = FuturesRolls.Symbol(instrument.Symbol, contract.Year, contract.Month);
                    sink.OnStatus(instrument, FeedStatus.Connected, $"IB API ready · {name} · awaiting paced subscriptions and market-data entitlements; live aggressor side is inferred");
                    lock (_lock)
                    {
                        ct.ThrowIfCancellationRequested();
                        client.Send(DepthRequest(depthId, contract, 10));
                        _activeDepthId = depthId;
                    }
                    await feed.WaitForTickRequestSlotAsync(client, contract, ct).ConfigureAwait(false);
                    lock (_lock)
                    {
                        ct.ThrowIfCancellationRequested();
                        client.Send(TickByTickRequest(tradesId, contract, "AllLast"));
                        _activeTradesId = tradesId;
                    }
                    await feed.WaitForTickRequestSlotAsync(client, contract, ct).ConfigureAwait(false);
                    lock (_lock)
                    {
                        ct.ThrowIfCancellationRequested();
                        client.Send(TickByTickRequest(quotesId, contract, "BidAsk"));
                        _activeQuotesId = quotesId;
                    }
                    attempt = 0;
                    while (!client.Closed.IsCompleted && !ct.IsCancellationRequested)
                    {
                        await Task.WhenAny(client.Closed, Task.Delay(TimeSpan.FromMinutes(1), ct)).ConfigureAwait(false);
                        var current = ContractOn(instrument, DateTime.UtcNow);
                        if (current.Year != contract.Year || current.Month != contract.Month)
                        {
                            sink.OnStatus(instrument, FeedStatus.Reconnecting, "IB calendar contract rolled; restoring data-only subscriptions.");
                            client.Dispose();
                        }
                    }
                    if (ct.IsCancellationRequested) break;
                    sink.OnStatus(instrument, FeedStatus.Reconnecting, $"IB Gateway disconnected ({await client.Closed.ConfigureAwait(false) ?? "closed"}); reconnecting…");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    sink.OnStatus(instrument, ex is NotSupportedException ? FeedStatus.Error : FeedStatus.Reconnecting, ex.Message);
                    if (ex is NotSupportedException) return;
                }
                finally
                {
                    feed._routes.TryRemove(tradesId, out _);
                    feed._routes.TryRemove(quotesId, out _);
                    feed._routes.TryRemove(depthId, out _);
                    CancelRequests(client);
                }

                if (feed._settings.Offline) continue;
                attempt++;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * attempt)), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private enum Channel
        {
            Trades,
            Quotes,
            Depth,
        }

        private void OnMessage(string[] f, Channel channel)
        {
            if (_cts.IsCancellationRequested) return;
            switch (f[0])
            {
                case "99":
                    OnTick(f);
                    break;
                case "12":
                case "13":
                    OnDepth(f);
                    break;
                case "4":
                {
                    var (_, code, text) = IbClient.Error(f);
                    if (code is 2100 or 2104 or 2106 or 2107 or 2108 or 2158) return;
                    if (channel != Channel.Depth || code != 317)
                        feed._routes.TryRemove(IbClient.Error(f).Id, out _); // Denied routes suppress late data; 317 keeps its reset subscription.
                    if (channel == Channel.Depth)
                    {

                        DepthUpdate fallback;
                        lock (_lock)
                        {
                            _depthLive = false;
                            _depth[0].Clear(); _depth[1].Clear(); _book.Clear();
                            if (_bid > 0) _book.Set(BookSide.Bid, instrument.ToTicks(_bid), _bidSize);
                            if (_ask > 0) _book.Set(BookSide.Ask, instrument.ToTicks(_ask), _askSize);
                            fallback = _book.BuildUpdate(DateTime.UtcNow, fullRefresh: true);
                        }
                        sink.OnDepth(instrument, fallback);
                        sink.OnStatus(instrument, _tradeError == null ? FeedStatus.Connected : FeedStatus.Error,
                            (code == 317 ? "IB depth reset; waiting for fresh rows with current best bid/ask fallback" : $"IB depth unavailable ({code}); fresh best bid/ask fallback · inferred aggressor side") + (_tradeError == null ? "" : " · " + _tradeError));
                        return;
                    }


                    if (channel == Channel.Quotes)
                    {
                        DepthUpdate? cleared = null;
                        lock (_lock)
                        {
                            _bid = _ask = _bidSize = _askSize = 0;
                            if (!_depthLive)
                            {
                                _book.Clear();
                                cleared = _book.BuildUpdate(DateTime.UtcNow, fullRefresh: true);
                            }
                        }
                        if (cleared != null) sink.OnDepth(instrument, cleared);
                        sink.OnStatus(instrument, _tradeError == null ? FeedStatus.Connected : FeedStatus.Error,
                            $"IB quotes unavailable ({code}); trade side uses an inferred tick rule, not exchange aggressor data." + (_tradeError == null ? "" : " · " + _tradeError));
                        return;
                    }
                    _tradeError = code switch
                    {
                        354 or 10089 or 10090 or 10167 or 10168 or 10189 =>
                            $"IB live data unavailable for {instrument.Symbol}; check real-time exchange entitlements ({text}).",
                        10190 => $"IB tick-by-tick subscription limit reached; close another IB chart ({text}).",
                        200 => $"IB contract unavailable for {instrument.Symbol} ({text}).",
                        _ => $"IB {code}: {text}",
                    };
                    sink.OnStatus(instrument, FeedStatus.Error, _tradeError);
                    break;
                }
            }
        }

        private void OnTick(string[] f)
        {
            var r = new IbReader(f, 2);
            int type = r.ReadInt();
            long secs = r.ReadLong();
            var time = DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
            if (type is 1 or 2)
            {
                decimal price = instrument.RoundPrice((decimal)r.ReadDouble());
                decimal size = r.ReadDecimal();
                if (size <= 0 || price <= 0) return;
                Trade trade;
                lock (_lock)
                {

                    var now = DateTime.UtcNow;
                    var t = Math.Abs((now - time).TotalSeconds) < 2 ? now : time;
                    if (t < _lastTime) t = _lastTime;
                    _lastTime = t;
                    _lastSide = Aggressor(price, _bid, _ask, _last, _lastSide);
                    _last = price;
                    trade = new Trade(++_id, t, price, size, _lastSide);
                }

                sink.OnTrades(instrument, new[] { trade });
            }
            else if (type == 3)
            {
                decimal bid = (decimal)r.ReadDouble(), ask = (decimal)r.ReadDouble();
                decimal bidSize = r.ReadDecimal(), askSize = r.ReadDecimal();
                DepthUpdate? update = null;
                lock (_lock)
                {
                    _bid = bid; _bidSize = bidSize; _askSize = askSize;
                    _ask = ask;
                    if (!_depthLive)
                    {
                        _book.Clear();
                        if (bid > 0) _book.Set(BookSide.Bid, instrument.ToTicks(bid), bidSize);
                        if (ask > 0) _book.Set(BookSide.Ask, instrument.ToTicks(ask), askSize);
                        update = _book.BuildUpdate(DateTime.UtcNow, fullRefresh: true);
                    }
                }

                if (update != null) sink.OnDepth(instrument, update);
            }
        }
        private void OnDepth(string[] f)
        {
            var r = new IbReader(f, 3);
            int position = r.ReadInt();
            if (f[0] == "13") r.ReadString();
            int op = r.ReadInt();
            int side = r.ReadInt();
            decimal price = (decimal)r.ReadDouble();
            decimal size = r.ReadDecimal();
            if (side is not (0 or 1) || position < 0 || op is not (0 or 1 or 2) || size < 0 || (op != 2 && price <= 0))
                throw new InvalidDataException("Malformed IB depth mutation.");
            DepthUpdate update;
            lock (_lock)
            {
                _depthLive = true;
                var rows = _depth[side];
                if (position > rows.Count || (op != 0 && position == rows.Count))
                    throw new InvalidDataException("IB depth row position is outside the current book.");
                switch (op)
                {
                    case 0:
                        rows.Insert(position, (price, size));
                        break;
                    case 1:
                        rows[position] = (price, size);
                        break;
                    case 2:
                        rows.RemoveAt(position);
                        break;
                }

                _book.Clear();
                foreach (var (p, s) in _depth[1]) _book.Set(BookSide.Bid, instrument.ToTicks(p), s);
                foreach (var (p, s) in _depth[0]) _book.Set(BookSide.Ask, instrument.ToTicks(p), s);
                update = _book.BuildUpdate(DateTime.UtcNow, fullRefresh: true);
            }

            sink.OnDepth(instrument, update);
        }

        public void Dispose()
        {
            _cts.Cancel();
            CancelRequests();
        }
    }

    public void Dispose()
    {
        lock (_configurationGate)
        {
            if (_disposed) return;
            _disposed = true;
            _client?.Dispose();
            _client = null;
            _configurationLifetime.Cancel();
            _lifetime.Cancel();
        }
        IbBarStore.HistoryChanged -= StoredHistoryChanged;
    }
}
