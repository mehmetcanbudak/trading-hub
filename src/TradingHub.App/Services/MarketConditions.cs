using System.Text.Json;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>Una hora del contexto de un mercado (para la tira de las últimas horas del panel).</summary>
internal readonly record struct MarketHour(DateTime Hour, MarketRegime Regime, int? Direction, double? VolatilityRank);

/// <summary>Cómo está un mercado ahora: rango o tendencia (alcista o bajista), volatilidad y las últimas horas.</summary>
internal sealed class MarketCondition
{
    public required string FeedId { get; init; }
    public required string Symbol { get; init; }
    public string Market => $"{FeedId}:{Symbol}";

    /// <summary>Hasta cuándo llegan los datos (si el mercado está cerrado o no se graba, puede ser de antes).</summary>
    public DateTime DataUntilUtc { get; init; }

    public decimal LastPrice { get; init; }
    public double? Trend { get; init; }
    public int? Direction { get; init; }
    public double? Change { get; init; }
    public double? Volatility { get; init; }
    public double? VolatilityRank { get; init; }
    public MarketRegime Regime { get; init; }

    /// <summary>Días de historia por hora con que se calculó.</summary>
    public int Days { get; init; }

    /// <summary>Horas seguidas en el régimen de ahora (dentro de las últimas 48).</summary>
    public int RegimeHours { get; init; }

    public List<MarketHour> Recent { get; init; } = new();
    public string RegimeLabel => MarketContextTracker.Label(Regime, Direction);
    public string VolatilityLabel => MarketContextTracker.VolatilityLabel(VolatilityRank);

    /// <summary>El contexto de ahora, para preguntarle a un filtro si deja entrar.</summary>
    public MarketContextTracker? Tracker { get; init; }
}

/// <summary>
/// Condición del mercado con la misma medida que usan las estrategias y sus filtros de contexto (tendencia de las últimas
/// 24 horas y percentil de volatilidad del último mes), armada con los cierres por hora de los últimos 32 días: de la
/// historia guardada (con caché por día), de las velas del exchange para los días que faltan (si el mercado ya está
/// abierto) y de los datos en vivo de hoy. La usan el panel «Mercado» de En vivo y las estrategias en vivo al arrancar.
/// </summary>
internal sealed class MarketConditions
{
    public const int HistoryDays = 32;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly AppServices _svc;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MarketConditions(AppServices svc) => _svc = svc;

    private sealed class DayCloses
    {
        /// <summary>Largo del archivo del día cuando se calculó (-1 = salió de las velas del exchange).</summary>
        public long Length { get; set; }

        public decimal?[] Closes { get; set; } = new decimal?[24];
    }

    private static string CacheDir => Path.Combine(AppPaths.Root, "Context");

    private static string CachePath(string feed, string symbol)
    {
        static string Clean(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
        return Path.Combine(CacheDir, $"{Clean(feed)}_{Clean(symbol)}.json");
    }

    /// <summary>Cierres por hora (UTC, inicio de cada hora) de los últimos 32 días hasta ahora, en orden.</summary>
    public async Task<List<(DateTime Time, decimal Close)>> HourlyAsync(string feed, string symbol, CancellationToken ct = default)
    {
        SortedDictionary<DateTime, decimal> hours;
        await _gate.WaitAsync(ct);
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            hours = await StoredAsync(feed, symbol, today.AddDays(-(HistoryDays - 1)), today, ct);
        }
        finally
        {
            _gate.Release();
        }

        // hoy, con lo que trae el mercado abierto (en el hilo de la interfaz, donde se le suman operaciones)
        var live = _svc.Hub.Items.FirstOrDefault(x => Same(x.Instrument, feed, symbol));
        if (live is { Trades.Count: > 0 })
        {
            var store = live.Trades;
            long since = DateTime.UtcNow.AddHours(-30).Ticks;
            var seen = new HashSet<long>();
            for (int i = store.Count - 1; i >= 0; i--)
            {
                long t = store.GetTimeTicks(i);
                if (t < since) break;
                long hour = t - t % TimeSpan.TicksPerHour;
                if (seen.Add(hour)) hours[new DateTime(hour, DateTimeKind.Utc)] = store.GetPrice(i);
            }
        }

        return hours.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Cómo está el mercado ahora (null si no hay ningún dato).</summary>
    public async Task<MarketCondition?> ConditionAsync(string feed, string symbol, CancellationToken ct = default)
    {
        var hourly = await HourlyAsync(feed, symbol, ct);
        if (hourly.Count == 0) return null;
        var tracker = new MarketContextTracker();
        var recent = new List<MarketHour>();
        var cut = hourly[^1].Time.AddHours(-47);
        foreach (var (t, c) in hourly)
        {
            tracker.Add(t, c);
            if (t >= cut) recent.Add(new MarketHour(t, tracker.Regime, tracker.Direction, tracker.VolatilityRank));
        }

        int same = 0;
        for (int i = recent.Count - 1; i >= 0 && recent[i].Regime == tracker.Regime && (tracker.Regime != MarketRegime.Trend || recent[i].Direction == tracker.Direction); i--) same++;
        var live = _svc.Hub.Items.FirstOrDefault(x => Same(x.Instrument, feed, symbol));
        return new MarketCondition
        {
            FeedId = feed,
            Symbol = symbol,
            DataUntilUtc = live is { Trades.Count: > 0 } ? live.Trades.LastTime : hourly[^1].Time,
            LastPrice = live is { Trades.Count: > 0 } ? live.LastPrice : hourly[^1].Close,
            Trend = tracker.Trend,
            Direction = tracker.Direction,
            Change = tracker.Change,
            Volatility = tracker.Volatility,
            VolatilityRank = tracker.VolatilityRank,
            Regime = tracker.Regime,
            Days = (int)Math.Round((hourly[^1].Time - hourly[0].Time).TotalDays),
            RegimeHours = same,
            Recent = recent,
            Tracker = tracker,
        };
    }

    /// <summary>A una estrategia que arrancó en vivo le arma el contexto con el mes completo (en segundo plano).</summary>
    public async Task SeedAsync(LiveStrategyRunner runner)
    {
        try
        {
            var inst = runner.Data.Instrument;
            var hourly = await HourlyAsync(inst.FeedId, inst.Symbol);
            if (runner.IsRunning && hourly.Count > 0) runner.SeedContext(hourly);
        }
        catch (Exception ex)
        {
            this.LogWarn($"Could not build market context for {runner.StrategyName}: {ex.Message}");
        }
    }

    private static bool Same(Instrument i, string feed, string symbol) =>
        i.FeedId.Equals(feed, StringComparison.OrdinalIgnoreCase) && i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lo guardado: los archivos de cada día (con caché) y, para los días sin archivo, velas del exchange.</summary>
    /// <summary>
    /// Cierres por hora de los 32 días anteriores a un instante, de la historia guardada (con la misma caché por día): para
    /// que un backtest, una optimización o un walk-forward arranquen su contexto de mercado como en vivo. Solo horas ya
    /// cerradas antes de <paramref name="untilUtc"/>.
    /// </summary>
    public static List<(DateTime Time, decimal Close)> HourlyBefore(string feed, string symbol, DateTime untilUtc)
    {
        try
        {
            var last = DateOnly.FromDateTime(untilUtc);
            var (hours, _, cache, changed) = ReadStored(feed, symbol, last.AddDays(-HistoryDays), last, CancellationToken.None);
            if (changed) Save(CachePath(feed, symbol), Prune(cache));
            return hours.Where(kv => kv.Key.AddHours(1) <= untilUtc).Select(kv => (kv.Key, kv.Value)).ToList();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return new List<(DateTime, decimal)>();
        }
    }

    /// <summary>Se guardan los días del último año y pico (alcanza para los backtests de siempre).</summary>
    private static Dictionary<DateOnly, DayCloses> Prune(Dictionary<DateOnly, DayCloses> cache)
    {
        var oldest = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-400);
        foreach (var d in cache.Keys.Where(d => d < oldest).ToList()) cache.Remove(d);
        return cache;
    }

    /// <summary>Los días guardados entre dos fechas: de la caché o de los archivos de cada día (sin red).</summary>
    private static (SortedDictionary<DateTime, decimal> Hours, List<DateOnly> Missing, Dictionary<DateOnly, DayCloses> Cache, bool Changed) ReadStored(
        string feed, string symbol, DateOnly first, DateOnly last, CancellationToken ct)
    {
        var stored = Load(CachePath(feed, symbol));
        var found = new SortedDictionary<DateTime, decimal>();
        var absent = new List<DateOnly>();
        bool dirty = false;
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            // de cada fuente del día (la combinada de Rithmic y Databento lee las dos): la primera manda
            var files = HistorySources.DayFiles(AppPaths.Ticks, feed, symbol, d);
            long len = files.Count == 0 ? -1 : files.Sum(f => new FileInfo(f).Length);
            // un día ya no cambia media hora después de terminar (antes, puede seguir grabándose)
            bool final = DateTime.UtcNow - d.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) > TimeSpan.FromMinutes(30);
            decimal?[]? closes = null;
            if (stored.TryGetValue(d, out var dc) && dc.Length == len) closes = dc.Closes;
            else if (len > 0)
            {
                try
                {
                    closes = FromTicks(files);
                }
                catch (IOException)
                {
                    closes = null;
                }

                if (closes != null && final)
                {
                    stored[d] = new DayCloses { Length = len, Closes = closes };
                    dirty = true;
                }
            }

            if (closes == null)
            {
                absent.Add(d);
                continue;
            }

            Add(found, d, closes);
        }

        return (found, absent, stored, dirty);
    }

    private async Task<SortedDictionary<DateTime, decimal>> StoredAsync(string feed, string symbol, DateOnly first, DateOnly today, CancellationToken ct)
    {
        var path = CachePath(feed, symbol);
        var (hours, missing, cache, changed) = await Task.Run(() => ReadStored(feed, symbol, first, today, ct), ct);

        // días sin archivo: velas de 15 minutos del exchange, si ese mercado ya está abierto en la app o es cripto (velas
        // públicas); de a tramos seguidos, y un día ya cerrado queda en la caché aunque no haya tenido velas (un sábado de futuros)
        if (missing.Count > 0 && await KlineSourceAsync(feed, symbol, ct) is var (klines, instrument))
        {
            foreach (var run in Runs(missing))
            {
                var from = run[0].ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var to = run[^1].AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                if (to > DateTime.UtcNow) to = DateTime.UtcNow;
                try
                {
                    var list = await KlineHistory.LoadAsync(klines, instrument, from, to, 15, 0, null, ct);
                    var byDay = new Dictionary<DateOnly, decimal?[]>();
                    foreach (var k in list)
                    {
                        var t = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime;
                        var d = DateOnly.FromDateTime(t);
                        if (!byDay.TryGetValue(d, out var arr)) byDay[d] = arr = new decimal?[24];
                        arr[t.Hour] = k.Close;
                    }

                    foreach (var d in run)
                    {
                        byDay.TryGetValue(d, out var arr);
                        if (arr != null) Add(hours, d, arr);
                        if (DateTime.UtcNow - d.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) <= TimeSpan.FromMinutes(30)) continue;
                        cache[d] = new DayCloses { Length = -1, Closes = arr ?? new decimal?[24] };
                        changed = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.LogInfo($"No exchange bars for market context of {symbol}: {ex.Message}");
                }
            }
        }

        if (changed) Save(path, Prune(cache));

        return hours;
    }

    private readonly Dictionary<string, Instrument> _instruments = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// De dónde sacar velas: del mercado abierto en la app o, en cripto, de la conexión configurada de ese exchange (las
    /// velas son públicas: no abre ninguna sesión). null si no hay.
    /// </summary>
    private async Task<(IKlineHistory Source, Instrument Instrument)?> KlineSourceAsync(string feed, string symbol, CancellationToken ct)
    {
        if (_svc.Hub.Items.FirstOrDefault(x => Same(x.Instrument, feed, symbol)) is { Feed: IKlineHistory open } data) return (open, data.Instrument);
        if (!PublicArchives.Supports(feed)) return null;
        var profile = _svc.Connections.Enabled.FirstOrDefault(p => p.HasMarketData && p.MarketDataId == feed);
        if (profile == null) return null;
        try
        {
            if (_svc.Connections.GetFeed(profile.Id) is not (IKlineHistory source and IMarketDataFeed market)) return null;
            string key = $"{feed}:{symbol}";
            if (!_instruments.TryGetValue(key, out var inst))
            {
                inst = (await market.GetInstrumentsAsync(ct)).FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                if (inst == null) return null;
                _instruments[key] = inst;
            }

            return (source, inst);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogInfo($"No public bars for market context of {symbol}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Días seguidos, en tramos.</summary>
    private static IEnumerable<List<DateOnly>> Runs(IEnumerable<DateOnly> days)
    {
        var run = new List<DateOnly>();
        foreach (var d in days.OrderBy(x => x))
        {
            if (run.Count > 0 && d != run[^1].AddDays(1))
            {
                yield return run;
                run = new List<DateOnly>();
            }

            run.Add(d);
        }

        if (run.Count > 0) yield return run;
    }

    private static void Add(SortedDictionary<DateTime, decimal> hours, DateOnly day, decimal?[] closes)
    {
        for (int h = 0; h < 24; h++)
            if (closes[h] is { } c)
                hours[day.ToDateTime(new TimeOnly(h, 0), DateTimeKind.Utc)] = c;
    }

    /// <summary>El último precio de cada hora de un día guardado (las horas que no tiene el primer archivo, de los siguientes).</summary>
    private static decimal?[] FromTicks(IReadOnlyList<string> files)
    {
        var closes = new decimal?[24];
        foreach (var file in files)
        {
            var own = new decimal?[24];
            var last = new long[24];
            foreach (var t in TickFile.Read(file))
            {
                int h = t.Time.Hour;
                if (t.Time.Ticks < last[h]) continue;
                last[h] = t.Time.Ticks;
                own[h] = t.Price;
            }

            for (int h = 0; h < 24; h++) closes[h] ??= own[h];
        }

        return closes;
    }

    private static Dictionary<DateOnly, DayCloses> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var raw = JsonSerializer.Deserialize<Dictionary<string, DayCloses>>(File.ReadAllText(path), Json) ?? new();
            return raw.Where(kv => DateOnly.TryParse(kv.Key, System.Globalization.CultureInfo.InvariantCulture, out _) && kv.Value.Closes.Length == 24)
                .ToDictionary(kv => DateOnly.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
        }
        catch
        {
            return new(); // caché dañada: se vuelve a calcular
        }
    }

    private static void Save(string path, Dictionary<DateOnly, DayCloses> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var raw = cache.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(raw, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}
