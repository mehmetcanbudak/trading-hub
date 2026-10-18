using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;

namespace TradingHub.Feeds.Tradovate;

/// <summary>
/// Datos de Tradovate (futuros de CME): cinta (gráfico de ticks en vivo, con bid/ask para saber quién agredió), libro
/// (DOM), historia tick a tick y velas. Ojo: los datos por API solo andan si la cuenta tiene habilitados los datos de
/// CME para API (licencia aparte); sin eso Tradovate responde «symbol is inaccessible» y se puede operar igual con los
/// gráficos de otra conexión (por ejemplo Rithmic).
/// </summary>
public sealed class TradovateFeed : IMarketDataFeed, IKlineHistory, IDayDownloader
{
    public const string FeedId = "tradovate";
    private readonly TradovateConnection _conn;
    private readonly ConcurrentDictionary<long, ChartRoute> _charts = new();

    /// <summary>
    /// Paquetes de gráfico que llegaron antes de saber de quién son: la respuesta de md/getChart (con los ids) y los
    /// primeros paquetes vienen pegados, y el que pidió registra la ruta un instante después. Se guardan y se entregan
    /// al registrar la ruta.
    /// </summary>
    private readonly ConcurrentDictionary<long, (DateTime At, List<JsonElement> Packets)> _orphans = new();
    private readonly ConcurrentDictionary<long, List<LiveTarget>> _doms = new();
    private readonly List<LiveTarget> _live = new();
    private long _liveId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public TradovateFeed(Func<TradovateSettings> settings)
    {
        _conn = new TradovateConnection(settings);
        _conn.Md.Event += OnMdEvent;
        _conn.Md.StateChanged += (up, why) =>
        {
            LiveTarget[] targets;
            lock (_live) targets = _live.ToArray();
            foreach (var t in targets)
                if (!up)
                {
                    t.Sink.OnStatus(t.Instrument, FeedStatus.Reconnecting, "Tradovate: " + why);
                    _ = ResubscribeAsync(t);
                }
        };
    }

    public TradovateConnection Connection => _conn;
    public string Id => FeedId;
    public string DisplayName => "Tradovate";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public string HistoryNote => "Tradovate ticks (requires CME API data entitlement)";

    /// <summary>Primero los contratos que elegiste en la conexión y después todos los de CME Group (al mes activo).</summary>
    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = new List<Instrument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _conn.Settings.Products.Select(r => r.Trim().ToUpperInvariant()).Concat(FuturesCatalog.All.Select(p => p.Root)))
        {
            if (root.Length == 0 || !seen.Add(root)) continue;
            var product = FuturesCatalog.Find(root);
            list.Add(new Instrument
            {
                Symbol = root,
                FeedId = FeedId,
                Exchange = product?.Exchange ?? "CME",
                TickSize = product?.Tick ?? 0.25m,
                VolumeStep = 1,
                PointValue = product?.PointValue ?? 1,
                Description = $"{product?.Name ?? root} · Tradovate (active contract)",
            });
        }

        return Task.FromResult<IReadOnlyList<Instrument>>(list);
    }

    // ===================================================================== historia

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv);

    /// <summary>Una ruta de gráfico: a dónde van los paquetes de una suscripción de md/getChart.</summary>
    private sealed class ChartRoute
    {
        public Instrument Instrument = null!;
        public bool Bars;
        public Action<JsonElement>? Packet;
        public TaskCompletionSource<bool>? EndOfHistory;
        public LiveTarget? Live;
    }

    /// <summary>Pide un gráfico (ticks o velas) y junta la historia hasta el fin de historia («eoh»); después lo cancela.</summary>
    private async Task ChartHistoryAsync(string contract, JsonObject chartDescription, DateTime fromUtc, DateTime toUtc, int elements, Instrument instrument,
        bool bars, Action<JsonElement> packet, CancellationToken ct)
    {
        var route = new ChartRoute { Instrument = instrument, Bars = bars, Packet = packet, EndOfHistory = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
        var req = new JsonObject
        {
            ["symbol"] = contract,
            ["chartDescription"] = chartDescription,
            ["timeRange"] = new JsonObject { ["closestTimestamp"] = Iso(toUtc), ["asFarAsTimestamp"] = Iso(fromUtc), ["asMuchAsElements"] = elements },
        };
        var res = await _conn.Md.RequestAsync("md/getChart", req, ct).ConfigureAwait(false);
        long hist = res.TryGetProperty("historicalId", out var h) ? h.GetInt64() : 0;
        long real = res.TryGetProperty("realtimeId", out var r) ? r.GetInt64() : 0;
        Register(route, hist, real);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await using (timeout.Token.Register(() => route.EndOfHistory.TrySetCanceled()))
                await route.EndOfHistory.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Tradovate history request timed out.");
        }
        finally
        {
            if (hist != 0) _charts.TryRemove(hist, out _);
            if (real != 0) _charts.TryRemove(real, out _);
            foreach (var id in new[] { real, hist }.Where(x => x != 0).Distinct())
            {
                try
                {
                    await _conn.Md.RequestAsync("md/cancelChart", new JsonObject { ["subscriptionId"] = id }, CancellationToken.None).ConfigureAwait(false);
                    break;
                }
                catch
                {
                    // ya terminado
                }
            }
        }
    }

    private static JsonObject TickChart() => new() { ["underlyingType"] = "Tick", ["elementSize"] = 1, ["elementSizeUnit"] = "UnderlyingUnits", ["withHistogram"] = false };

    /// <summary>Ticks de un paquete: precio, bid y ask relativos al base (en ticks) y hora relativa a la base.</summary>
    private static IEnumerable<(long Id, DateTime Time, decimal Price, decimal Size, decimal? Bid, decimal? Ask)> Ticks(JsonElement packet)
    {
        if (!packet.TryGetProperty("tks", out var tks) || tks.ValueKind != JsonValueKind.Array) yield break;
        decimal bp = Num(packet, "bp"), ts = Num(packet, "ts");
        long bt = (long)Num(packet, "bt");
        if (ts <= 0) ts = 1;
        foreach (var t in tks.EnumerateArray())
        {
            long time = bt + (long)Num(t, "t");
            decimal price = (bp + Num(t, "p")) * ts;
            decimal? bid = t.TryGetProperty("bs", out _) && t.TryGetProperty("b", out _) ? (bp + Num(t, "b")) * ts : null;
            decimal? ask = t.TryGetProperty("as", out _) && t.TryGetProperty("a", out _) ? (bp + Num(t, "a")) * ts : null;
            yield return ((long)Num(t, "id"), DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime, price, Num(t, "s"), bid, ask);
        }
    }

    private static decimal Num(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0;

    /// <summary>Lado agresor: al ask o más arriba compró, al bid o más abajo vendió; si no, la regla del tick.</summary>
    private static TradeSide Aggressor(decimal price, decimal? bid, decimal? ask, ref decimal last, ref TradeSide lastSide)
    {
        if (ask is { } a && price >= a)
        {
            last = price;
            return lastSide = TradeSide.Buy;
        }

        if (bid is { } b && price <= b)
        {
            last = price;
            return lastSide = TradeSide.Sell;
        }

        return HistoryBuilder.TickRule(price, ref last, ref lastSide);
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var segments = FuturesRolls.Supports(instrument.Symbol)
            ? FuturesRolls.Segments(instrument.Symbol, fromUtc, toUtc).ToList()
            : new List<(string Symbol, DateTime From, DateTime To)> { ((await _conn.ContractAsync(instrument.Symbol, toUtc, ct).ConfigureAwait(false)).Name, fromUtc, toUtc) };
        double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        long lastId = 0;
        foreach (var seg in segments)
        {
            // de atrás para adelante: cada pedido trae hasta 50 mil ticks hacia el pasado desde el cursor
            var ticks = new Dictionary<long, (DateTime Time, decimal Price, decimal Size, decimal? Bid, decimal? Ask)>();
            var cursor = seg.To;
            for (int page = 0; page < 400 && cursor > seg.From && !ct.IsCancellationRequested; page++)
            {
                int before = ticks.Count;
                DateTime oldest = DateTime.MaxValue;
                await ChartHistoryAsync(seg.Symbol, TickChart(), seg.From, cursor, 50_000, instrument, false, packet =>
                {
                    foreach (var t in Ticks(packet))
                    {
                        if (t.Time < seg.From || t.Time > cursor) continue;
                        ticks.TryAdd(t.Id != 0 ? t.Id : t.Time.Ticks ^ (long)(t.Price * 1000), (t.Time, t.Price, t.Size, t.Bid, t.Ask));
                        if (t.Time < oldest) oldest = t.Time;
                    }
                }, ct).ConfigureAwait(false);
                if (ticks.Count == before || oldest == DateTime.MaxValue) break;
                cursor = oldest.AddMilliseconds(-1);
                progress?.Report(Math.Min(1, (toUtc - cursor).TotalSeconds / total));
            }

            decimal last = 0;
            var side = TradeSide.Buy;
            var trades = ticks.Values.OrderBy(t => t.Time).Select(t =>
            {
                decimal price = instrument.RoundPrice(t.Price);
                return new Trade(HistoryBuilder.TimeId(t.Time, ref lastId), t.Time, price, Math.Max(1, t.Size), Aggressor(price, t.Bid, t.Ask, ref last, ref side));
            }).ToList();
            for (int i = 0; i < trades.Count; i += 20000)
                if (!onBatch(trades.GetRange(i, Math.Min(20000, trades.Count - i)))) return;
        }

        progress?.Report(1);
    }

    public async Task<List<Trade>?> DownloadDayAsync(Instrument instrument, DateOnly day, IProgress<double>? progress, CancellationToken ct)
    {
        var list = new List<Trade>();
        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await LoadTradesAsync(instrument, from, from.AddDays(1), null, b =>
        {
            list.AddRange(b);
            return true;
        }, progress, ct).ConfigureAwait(false);
        return list.Count > 0 ? list : null;
    }

    // ===================================================================== velas

    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var segments = FuturesRolls.Supports(instrument.Symbol)
            ? FuturesRolls.Segments(instrument.Symbol, fromUtc, toUtc).ToList()
            : new List<(string Symbol, DateTime From, DateTime To)> { ((await _conn.ContractAsync(instrument.Symbol, toUtc, ct).ConfigureAwait(false)).Name, fromUtc, toUtc) };
        foreach (var seg in segments)
        {
            var bars = new SortedDictionary<long, Kline>();
            await ChartHistoryAsync(seg.Symbol, new JsonObject { ["underlyingType"] = "MinuteBar", ["elementSize"] = minutes, ["elementSizeUnit"] = "UnderlyingUnits", ["withHistogram"] = false },
                seg.From, seg.To, 20_000, instrument, true, packet =>
                {
                    if (!packet.TryGetProperty("bars", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
                    foreach (var b in arr.EnumerateArray())
                    {
                        if (!b.TryGetProperty("timestamp", out var ts) || !DateTime.TryParse(ts.GetString(), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) continue;
                        if (t < seg.From || t >= seg.To) continue;
                        decimal up = Num(b, "upVolume"), down = Num(b, "downVolume"), offer = Num(b, "offerVolume"), bid = Num(b, "bidVolume");
                        decimal vol = Math.Max(up + down, offer + bid);
                        long ms = new DateTimeOffset(t).ToUnixTimeMilliseconds();
                        bars[ms] = new Kline(ms, Num(b, "open"), Num(b, "high"), Num(b, "low"), Num(b, "close"), Math.Max(1, vol), offer, offer + bid > 0);
                    }
                }, ct).ConfigureAwait(false);
            if (bars.Count > 0 && !onBatch(bars.Values.ToList())) return;
        }
    }

    public string CacheTag => "tradovate";

    // ===================================================================== en vivo

    private sealed class LiveTarget
    {
        public required Instrument Instrument;
        public required IMarketDataSink Sink;
        public string Contract = "";
        public long ContractId;
        public long ChartId;
        public bool Disposed;
        public decimal LastPrice;
        public TradeSide LastSide = TradeSide.Buy;
        public bool HistoryDone;
        public int Attempts;
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var target = new LiveTarget { Instrument = instrument, Sink = sink };
        lock (_live) _live.Add(target);
        _ = ResubscribeAsync(target);
        return new Subscription(this, target);
    }

    private async Task ResubscribeAsync(LiveTarget t)
    {
        while (!t.Disposed)
        {
            try
            {
                t.Sink.OnStatus(t.Instrument, FeedStatus.Connecting, "Connecting to Tradovate…");
                var (id, name) = await _conn.ContractAsync(t.Instrument.Symbol, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                t.Contract = name;
                t.ContractId = id;
                t.HistoryDone = false;
                // cinta: gráfico de ticks en vivo (cada tick trae el bid y el ask del momento)
                var res = await _conn.Md.RequestAsync("md/getChart", new JsonObject
                {
                    ["symbol"] = name,
                    ["chartDescription"] = TickChart(),
                    ["timeRange"] = new JsonObject { ["asMuchAsElements"] = 1 },
                }, CancellationToken.None).ConfigureAwait(false);
                if (res.ValueKind == JsonValueKind.String) throw new TradovateException(res.GetString() ?? "");
                long real = res.TryGetProperty("realtimeId", out var r) ? r.GetInt64() : 0;
                long hist = res.TryGetProperty("historicalId", out var h) ? h.GetInt64() : 0;
                t.ChartId = real != 0 ? real : hist;
                Register(new ChartRoute { Instrument = t.Instrument, Live = t }, hist, real);
                // libro
                _doms.AddOrUpdate(id, _ => new List<LiveTarget> { t }, (_, l) =>
                {
                    lock (l)
                        if (!l.Contains(t)) l.Add(t);
                    return l;
                });
                var dom = await _conn.Md.RequestAsync("md/subscribeDOM", new JsonObject { ["symbol"] = name }, CancellationToken.None).ConfigureAwait(false);
                if (dom.ValueKind == JsonValueKind.String) throw new TradovateException(dom.GetString() ?? "");
                t.Attempts = 0;
                t.Sink.OnStatus(t.Instrument, FeedStatus.Connected, $"Tradovate connected ({name})");
                return;
            }
            catch (Exception ex)
            {
                t.Attempts++;
                string msg = ex.Message.Contains("inaccessible", StringComparison.OrdinalIgnoreCase)
                    ? "Tradovate has no API data for this contract (CME API entitlement required). Trade Tradovate accounts using a Rithmic chart instead."
                    : ex.Message;
                t.Sink.OnStatus(t.Instrument, t.Attempts > 3 && msg.Contains("API") ? FeedStatus.Error : FeedStatus.Reconnecting, msg);
                await Task.Delay(LiveSubscription.Backoff(t.Attempts)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Registra la ruta de un gráfico y le entrega lo que ya había llegado para sus ids.</summary>
    private void Register(ChartRoute route, long hist, long real)
    {
        foreach (var id in new[] { hist, real }.Where(x => x != 0).Distinct())
        {
            lock (route)
            {
                _charts[id] = route;
                if (_orphans.TryRemove(id, out var pending))
                    foreach (var packet in pending.Packets) Deliver(route, packet);
            }
        }
    }

    private void OnMdEvent(string kind, JsonElement d)
    {
        if (kind == "chart" && d.TryGetProperty("charts", out var charts) && charts.ValueKind == JsonValueKind.Array)
        {
            foreach (var packet in charts.EnumerateArray())
            {
                long id = packet.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var v) ? v : 0;
                if (!_charts.TryGetValue(id, out var route))
                {
                    // todavía sin ruta: se guarda un rato (los viejos se descartan)
                    var entry = _orphans.GetOrAdd(id, _ => (DateTime.UtcNow, new List<JsonElement>()));
                    lock (entry.Packets)
                        if (entry.Packets.Count < 5000) entry.Packets.Add(packet.Clone());
                    foreach (var old in _orphans.Where(kv => (DateTime.UtcNow - kv.Value.At).TotalSeconds > 60).Select(kv => kv.Key).ToList()) _orphans.TryRemove(old, out _);
                    // pudo registrarse justo ahora
                    if (_charts.TryGetValue(id, out var late) && _orphans.TryRemove(id, out var again))
                        lock (late)
                            foreach (var pk in again.Packets) Deliver(late, pk);
                    continue;
                }

                lock (route) Deliver(route, packet);
            }
        }
        else if (kind == "md" && d.TryGetProperty("doms", out var doms) && doms.ValueKind == JsonValueKind.Array)
            OnDoms(doms);
    }

    private void Deliver(ChartRoute route, JsonElement packet)
    {
        {
            {
                bool eoh = packet.TryGetProperty("eoh", out var e) && e.ValueKind == JsonValueKind.True;
                if (route.Live is { } live)
                {
                    if (eoh)
                    {
                        live.HistoryDone = true;
                        return;
                    }

                    if (!live.HistoryDone) return; // el tick de historia que trae al suscribirse
                    var trades = new List<Trade>();
                    foreach (var t in Ticks(packet).OrderBy(x => x.Time))
                    {
                        decimal price = live.Instrument.RoundPrice(t.Price);
                        trades.Add(new Trade(Interlocked.Increment(ref _liveId), t.Time, price, Math.Max(1, t.Size), Aggressor(price, t.Bid, t.Ask, ref live.LastPrice, ref live.LastSide)));
                    }

                    if (trades.Count > 0) live.Sink.OnTrades(live.Instrument, trades);
                }
                else
                {
                    if (!eoh) route.Packet?.Invoke(packet);
                    else route.EndOfHistory?.TrySetResult(true);
                }
            }
        }
    }

    private void OnDoms(JsonElement doms)
    {
        {
            foreach (var dom in doms.EnumerateArray())
            {
                long cid = dom.TryGetProperty("contractId", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
                if (!_doms.TryGetValue(cid, out var list)) continue;
                LiveTarget[] targets;
                lock (list) targets = list.ToArray();
                foreach (var t in targets)
                {
                    var book = new OrderBookBuilder();
                    foreach (var lvl in dom.TryGetProperty("bids", out var bids) ? bids.EnumerateArray() : Enumerable.Empty<JsonElement>())
                        book.Set(BookSide.Bid, t.Instrument.ToTicks(Num(lvl, "price")), Num(lvl, "size"));
                    foreach (var lvl in dom.TryGetProperty("offers", out var offers) ? offers.EnumerateArray() : Enumerable.Empty<JsonElement>())
                        book.Set(BookSide.Ask, t.Instrument.ToTicks(Num(lvl, "price")), Num(lvl, "size"));
                    t.Sink.OnDepth(t.Instrument, book.BuildUpdate(DateTime.UtcNow, 200, true));
                }
            }
        }
    }

    private sealed class Subscription(TradovateFeed feed, LiveTarget target) : IDisposable, IReconnectable
    {
        public void Reconnect() => _ = feed.ResubscribeAsync(target);

        public void Dispose()
        {
            if (target.Disposed) return;
            target.Disposed = true;
            lock (feed._live) feed._live.Remove(target);
            foreach (var kv in feed._charts.Where(kv => kv.Value.Live == target).ToList()) feed._charts.TryRemove(kv.Key, out _);
            if (feed._doms.TryGetValue(target.ContractId, out var list))
                lock (list)
                    list.Remove(target);
            if (target.ChartId != 0) _ = Quiet(() => feed._conn.Md.RequestAsync("md/cancelChart", new JsonObject { ["subscriptionId"] = target.ChartId }, CancellationToken.None));
            if (target.Contract.Length > 0) _ = Quiet(() => feed._conn.Md.RequestAsync("md/unsubscribeDOM", new JsonObject { ["symbol"] = target.Contract }, CancellationToken.None));
        }

        private static async Task Quiet(Func<Task> a)
        {
            try
            {
                await a().ConfigureAwait(false);
            }
            catch
            {
                // ya no importa
            }
        }
    }

    /// <summary>Prueba el login y si hay datos por API.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        await _conn.AccessTokenAsync(ct).ConfigureAwait(false);
        var accounts = await _conn.GetAsync("/account/list", ct).ConfigureAwait(false);
        int n = accounts.ValueKind == JsonValueKind.Array ? accounts.GetArrayLength() : 0;
        string data;
        try
        {
            var (_, name) = await _conn.ContractAsync("ES", DateTime.UtcNow, ct).ConfigureAwait(false);
            var res = await _conn.Md.RequestAsync("md/subscribeQuote", new JsonObject { ["symbol"] = name }, ct).ConfigureAwait(false);
            data = res.ValueKind == JsonValueKind.String && (res.GetString() ?? "").Contains("inaccessible", StringComparison.OrdinalIgnoreCase)
                ? "No API market data (CME API entitlement required): trading is available from compatible Rithmic charts."
                : "API market data available.";
            _ = _conn.Md.RequestAsync("md/unsubscribeQuote", new JsonObject { ["symbol"] = name }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            data = "Market data: " + ex.Message;
        }

        return (true, $"Signed in as {_conn.UserName} ({(_conn.IsDemo ? "demo" : "real")}): {n} account(s). {data}");
    }

    public void Dispose() => _conn.Dispose();
}
