using TradingHub.Market;

namespace TradingHub.Feeds;

/// <summary>
/// Mercado simulado offline: genera un tape realista (ráfagas, barridas, prints grandes,
/// cambios de régimen de volatilidad) y un libro de órdenes coherente con el precio.
/// Sirve para usar la plataforma sin conexión y para practicar.
/// </summary>
public sealed class SimulatedFeed : IMarketDataFeed, ITickerStatsProvider, IKlineHistory
{
    public const string FeedId = "sim";

    private readonly Dictionary<string, Model> _models = new();
    private readonly object _lock = new();

    public string Id => FeedId;
    public string DisplayName => "Simulator (offline)";
    public bool SupportsHistory => true;
    public bool IsLive => false;

    private static readonly (Instrument Instrument, double StartPrice, double TradesPerSec, double BaseVolume, double VolTicksPerSqrtSec)[] Specs =
    {
        (new Instrument { Symbol = "SIM-BTC", FeedId = FeedId, Exchange = "Simulator", TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT", Description = "Simulated Bitcoin" }, 86800, 9, 0.06, 6.0),
        (new Instrument { Symbol = "SIM-ES", FeedId = FeedId, Exchange = "Simulator", TickSize = 0.25m, VolumeStep = 1m, PointValue = 50m, Description = "Simulated E-mini S&P 500" }, 6650, 14, 2.2, 0.55),
        (new Instrument { Symbol = "SIM-NQ", FeedId = FeedId, Exchange = "Simulator", TickSize = 0.25m, VolumeStep = 1m, PointValue = 20m, Description = "Simulated E-mini Nasdaq" }, 24300, 11, 1.6, 2.4),
        (new Instrument { Symbol = "SIM-ETH", FeedId = FeedId, Exchange = "Simulator", TickSize = 0.01m, VolumeStep = 0.001m, BaseAsset = "ETH", QuoteAsset = "USDT", Description = "Simulated Ethereum" }, 3210, 8, 0.9, 12.0),
    };

    public Task<IReadOnlyDictionary<string, TickerStats>> GetTickerStatsAsync(CancellationToken ct)
    {
        var res = new Dictionary<string, TickerStats>();
        foreach (var s in Specs)
        {
            decimal p = (decimal)s.StartPrice;
            res[s.Instrument.Symbol] = new TickerStats(s.Instrument.Symbol, p, 0, (decimal)(s.TradesPerSec * s.BaseVolume * 86400) * p, p * 1.01m, p * 0.99m);
        }

        return Task.FromResult<IReadOnlyDictionary<string, TickerStats>>(res);
    }

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Instrument>>(Specs.Select(s => s.Instrument).ToList());

    // ======================================================================= velas (historia larga)

    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(DateTime.UtcNow.Date.AddYears(-3));

    /// <summary>
    /// Historia larga simulada: caminata aleatoria determinística generada hacia atrás desde
    /// <paramref name="endPrice"/> (el primer precio que ya hay), así empalma con lo cargado.
    /// </summary>
    public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var spec = Specs.FirstOrDefault(x => x.Instrument.Symbol == instrument.Symbol);
        if (spec.Instrument == null) spec = Specs[0];
        return Task.Run(() =>
        {
            long step = minutes * 60_000L;
            long start = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / step * step;
            long end = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / step * step;
            int n = (int)Math.Max(0, (end - start) / step);
            if (n == 0) return;
            var bars = new Kline[n];
            double price = endPrice > 0 ? (double)endPrice : spec.StartPrice;
            double tick = (double)instrument.TickSize;
            double sigma = spec.VolTicksPerSqrtSec * Math.Sqrt(minutes * 60.0) * tick;
            ulong seed = 1469598103934665603UL;
            foreach (char ch in instrument.Symbol) seed = (seed ^ ch) * 1099511628211UL;
            decimal stepVol = instrument.VolumeStep > 0 ? instrument.VolumeStep : 0.001m;

            static ulong Mix(ulong z)
            {
                z += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }

            double U(ulong h) => ((h >> 11) + 0.5) * (1.0 / 9007199254740992.0);
            decimal Vol(double v) => Math.Max(stepVol, Math.Round((decimal)v / stepVol) * stepVol);

            for (int i = n - 1; i >= 0; i--)
            {
                long t = start + i * step;
                ulong h = Mix(seed ^ (ulong)t);
                double u1 = U(Mix(h + 1)), u2 = U(Mix(h + 2));
                double g = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
                double close = price;
                double move = g * sigma;
                double open = Math.Max(tick, close - move);
                double hi = Math.Max(open, close) + Math.Abs(U(Mix(h + 3)) - 0.5) * sigma;
                double lo = Math.Max(tick, Math.Min(open, close) - Math.Abs(U(Mix(h + 4)) - 0.5) * sigma);
                double vol = spec.TradesPerSec * spec.BaseVolume * minutes * 60 * (0.4 + 1.2 * U(Mix(h + 5)));
                double buyShare = Math.Clamp(0.5 + move / (sigma * 4), 0.08, 0.92);
                bars[i] = new Kline(t, instrument.RoundPrice((decimal)open), instrument.RoundPrice((decimal)hi), instrument.RoundPrice((decimal)lo),
                    instrument.RoundPrice((decimal)close), Vol(vol), Vol(vol * buyShare), true);
                price = open;
            }

            for (int i = 0; i < n && !ct.IsCancellationRequested; i += 5000)
            {
                if (!onBatch(bars.Skip(i).Take(5000).ToList())) return;
                progress?.Report(Math.Min(1, (i + 5000) / (double)n));
            }
        }, ct);
    }

    private Model GetModel(Instrument instrument)
    {
        lock (_lock)
        {
            if (!_models.TryGetValue(instrument.Symbol, out var m))
            {
                var spec = Specs.FirstOrDefault(s => s.Instrument.Symbol == instrument.Symbol);
                if (spec.Instrument == null) spec = Specs[0];
                m = new Model(spec.Instrument, spec.StartPrice, spec.TradesPerSec, spec.BaseVolume, spec.VolTicksPerSqrtSec);
                _models[instrument.Symbol] = m;
            }

            return m;
        }
    }

    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var model = GetModel(instrument);
        model.HistoryActive = true;
        return Task.Run(() =>
        {
            try
            {
            lock (model)
            {
                // La historia se genera de forma determinística desde el inicio del rango.
                model.Reset(fromUtc);
                var batch = new List<Trade>(4096);
                double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
                while (!ct.IsCancellationRequested)
                {
                    var t = model.Next();
                    if (t.Time >= toUtc)
                    {
                        model.PushBack(t);
                        break;
                    }

                    batch.Add(t);
                    if (batch.Count >= 4096)
                    {
                        if (!onBatch(batch)) return;
                        batch = new List<Trade>(4096);
                        progress?.Report((t.Time - fromUtc).TotalSeconds / total);
                    }
                }

                if (batch.Count > 0) onBatch(batch);
                progress?.Report(1);
            }
            }
            finally
            {
                model.HistoryActive = false;
                model.HistoryDone = true;
            }
        }, ct);
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var model = GetModel(instrument);
        var cts = new CancellationTokenSource();
        var thread = new Thread(() => LiveLoop(model, sink, cts.Token)) { IsBackground = true, Name = "sim-" + instrument.Symbol };
        thread.Start();
        return new Subscription(cts);
    }

    private static void LiveLoop(Model model, IMarketDataSink sink, CancellationToken ct)
    {
        // espera a que termine la historia (si se pidió) para que el vivo continúe donde terminó
        var waitStart = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && !model.HistoryDone && (model.HistoryActive || (DateTime.UtcNow - waitStart).TotalMilliseconds < 1500))
            Thread.Sleep(50);
        sink.OnStatus(model.Instrument, FeedStatus.Connected, "Simulator active");
        var lastDepth = DateTime.MinValue;
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var trades = new List<Trade>();
            lock (model)
            {
                if (first)
                {
                    model.EnsureLiveStart(now);
                    first = false;
                }

                while (true)
                {
                    var t = model.Next();
                    if (t.Time > now)
                    {
                        model.PushBack(t);
                        break;
                    }

                    trades.Add(t);
                    model.ConsumeBook(t);
                    if (trades.Count > 20000) break;
                }
            }

            if (trades.Count > 0) sink.OnTrades(model.Instrument, trades);

            if ((now - lastDepth).TotalMilliseconds >= 100)
            {
                DepthUpdate upd;
                lock (model) upd = model.BuildDepth(now);
                sink.OnDepth(model.Instrument, upd);
                lastDepth = now;
            }

            Thread.Sleep(25);
        }

        sink.OnStatus(model.Instrument, FeedStatus.Disconnected, null);
    }

    public void Dispose()
    {
    }

    private sealed class Subscription : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        public Subscription(CancellationTokenSource cts) => _cts = cts;
        public void Dispose() => _cts.Cancel();
    }

    /// <summary>Modelo estocástico del mercado.</summary>
    private sealed class Model
    {
        public readonly Instrument Instrument;
        public volatile bool HistoryActive;
        public volatile bool HistoryDone;
        private readonly double _startPriceTicks;
        private readonly double _baseRate;
        private readonly double _baseVolume;
        private readonly double _baseVol;
        private Random _rng = new(1);

        private double _fair;        // valor "justo" latente, en ticks
        private double _drift;       // ticks/seg
        private double _volMult = 1;
        private double _regimeLeft;  // segundos hasta el próximo cambio de régimen
        private DateTime _time;
        private long _nextId;
        private Trade? _pushed;

        // ráfaga (barrida agresiva)
        private int _burstLeft;
        private TradeSide _burstSide;

        // libro
        private readonly OrderBookBuilder _book = new();
        private readonly Dictionary<long, double> _bidSizes = new();
        private readonly Dictionary<long, double> _askSizes = new();
        private long _lastTradeTicks;
        private bool _liveStarted;

        public Model(Instrument instrument, double startPrice, double rate, double baseVolume, double volTicks)
        {
            Instrument = instrument;
            _startPriceTicks = startPrice / (double)instrument.TickSize;
            _baseRate = rate;
            _baseVolume = baseVolume;
            _baseVol = volTicks;
            Reset(DateTime.UtcNow.AddHours(-1));
        }

        public void Reset(DateTime from)
        {
            // semilla estable por instrumento y día: la misma fecha genera el mismo mercado
            int seed = HashCode.Combine(Instrument.Symbol.GetHashCode(StringComparison.Ordinal) & 0x7fffffff, from.Date.Ticks.GetHashCode());
            seed = StableSeed(Instrument.Symbol, from.Date);
            _rng = new Random(seed);
            _fair = _startPriceTicks * (1 + (_rng.NextDouble() - 0.5) * 0.04);
            _drift = 0;
            _volMult = 1;
            _regimeLeft = 300;
            _time = from;
            _nextId = new DateTimeOffset(from).ToUnixTimeMilliseconds() * 10;
            _pushed = null;
            _burstLeft = 0;
            _liveStarted = false;
            _lastTradeTicks = (long)_fair;
        }

        private static int StableSeed(string s, DateTime day)
        {
            unchecked
            {
                int h = 17;
                foreach (var c in s) h = h * 31 + c;
                h = h * 31 + day.Year;
                h = h * 31 + day.DayOfYear;
                return h & 0x7fffffff;
            }
        }

        public void EnsureLiveStart(DateTime now)
        {
            if (_liveStarted) return;
            _liveStarted = true;
            // si el modelo quedó muy atrás (sin historia), salta al presente
            if (_pushed is { } p && (now - p.Time).TotalMinutes > 5)
            {
                _pushed = null;
                _time = now;
            }
            else if (_pushed == null && (now - _time).TotalMinutes > 5)
            {
                _time = now;
            }
        }

        public void PushBack(Trade t) => _pushed = t;

        private double Gaussian()
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        /// <summary>Actividad según la hora UTC (más movimiento en la sesión de EE.UU.).</summary>
        private static double ActivityFactor(DateTime t)
        {
            double h = t.Hour + t.Minute / 60.0;
            double us = Math.Exp(-Math.Pow((h - 15.5) / 2.6, 2)) * 1.4;   // 13:30-18:00 UTC
            double eu = Math.Exp(-Math.Pow((h - 8.5) / 2.0, 2)) * 0.6;
            double asia = Math.Exp(-Math.Pow((h - 1.5) / 2.0, 2)) * 0.35;
            return 0.45 + us + eu + asia;
        }

        public Trade Next()
        {
            if (_pushed is { } p)
            {
                _pushed = null;
                return p;
            }

            double act = ActivityFactor(_time) * (0.6 + 0.4 * _volMult);
            double rate = _baseRate * act;

            double dt;
            if (_burstLeft > 0)
            {
                dt = _rng.NextDouble() * 0.004; // prácticamente simultáneas
            }
            else
            {
                dt = -Math.Log(1.0 - _rng.NextDouble()) / rate;
            }

            dt = Math.Min(dt, 30);
            _time = _time.AddTicks((long)(dt * TimeSpan.TicksPerSecond) + 1);

            // régimen
            _regimeLeft -= dt;
            if (_regimeLeft <= 0)
            {
                double r = _rng.NextDouble();
                _volMult = r < 0.25 ? 0.5 : r < 0.7 ? 1.0 : r < 0.92 ? 1.8 : 3.2;
                _drift = Gaussian() * _baseVol * 0.08 * _volMult;
                _regimeLeft = 120 + _rng.NextDouble() * 1500;
            }

            // reversión suave hacia el precio inicial para que no se vaya a cualquier lado
            double meanRev = (_startPriceTicks - _fair) * 0.00002;
            _fair += (_drift + meanRev) * dt + _baseVol * _volMult * Math.Sqrt(dt) * Gaussian();

            long bid = (long)Math.Floor(_fair);
            long ask = bid + 1;

            TradeSide side;
            long price;
            if (_burstLeft > 0)
            {
                side = _burstSide;
                _burstLeft--;
                // la barrida camina el libro
                if (_rng.NextDouble() < 0.35) _fair += side == TradeSide.Buy ? 1 : -1;
                bid = (long)Math.Floor(_fair);
                ask = bid + 1;
                price = side == TradeSide.Buy ? ask : bid;
            }
            else
            {
                double frac = _fair - bid; // 0..1
                double pBuy = 0.5 + 0.38 * Math.Tanh((frac - 0.5) * 3) + Math.Clamp(_drift / (_baseVol + 1e-9), -0.1, 0.1);
                side = _rng.NextDouble() < pBuy ? TradeSide.Buy : TradeSide.Sell;
                price = side == TradeSide.Buy ? ask : bid;

                if (_rng.NextDouble() < 0.012 * _volMult)
                {
                    _burstLeft = 3 + _rng.Next(25);
                    _burstSide = side;
                }
            }

            double vol = _baseVolume * Math.Exp(Gaussian() * 0.95 - 0.45);
            double big = _rng.NextDouble();
            if (big < 0.004) vol *= 15 + _rng.NextDouble() * 45;      // print grande
            else if (big < 0.03) vol *= 3 + _rng.NextDouble() * 5;
            if (_burstLeft > 0) vol *= 1.5;

            decimal step = Instrument.VolumeStep;
            decimal volume = Math.Max(step, Math.Round((decimal)vol / step) * step);

            _lastTradeTicks = price;
            return new Trade(++_nextId, _time, Instrument.FromTicks(price), volume, side);
        }

        public void ConsumeBook(Trade t)
        {
            long p = Instrument.ToTicks(t.Price);
            var dict = t.Side == TradeSide.Buy ? _askSizes : _bidSizes;
            if (dict.TryGetValue(p, out var s))
            {
                s -= (double)t.Volume;
                dict[p] = Math.Max(0, s);
            }
        }

        public DepthUpdate BuildDepth(DateTime now)
        {
            long bestBid = _lastTradeTicks - (_rng.NextDouble() < 0.5 ? 0 : 1);
            if (_fair < _lastTradeTicks) bestBid = _lastTradeTicks - 1;
            long bestAsk = bestBid + 1;
            const int levels = 120;
            double baseDepth = _baseVolume * 6;

            // limpia niveles cruzados
            foreach (var k in _bidSizes.Keys.Where(k => k > bestBid || k < bestBid - levels * 2).ToList()) _bidSizes.Remove(k);
            foreach (var k in _askSizes.Keys.Where(k => k < bestAsk || k > bestAsk + levels * 2).ToList()) _askSizes.Remove(k);

            for (int i = 0; i < levels; i++)
            {
                Touch(_bidSizes, bestBid - i, i, baseDepth);
                Touch(_askSizes, bestAsk + i, i, baseDepth);
            }

            _book.Clear();
            foreach (var kv in _bidSizes)
                if (kv.Value > 0) _book.Set(BookSide.Bid, kv.Key, RoundVol(kv.Value));
            foreach (var kv in _askSizes)
                if (kv.Value > 0) _book.Set(BookSide.Ask, kv.Key, RoundVol(kv.Value));
            return _book.BuildUpdate(now, 500, fullRefresh: true);
        }

        private decimal RoundVol(double v)
        {
            decimal step = Instrument.VolumeStep;
            return Math.Max(step, Math.Round((decimal)v / step) * step);
        }

        private void Touch(Dictionary<long, double> dict, long price, int distance, double baseDepth)
        {
            if (!dict.TryGetValue(price, out var size) || size <= 0)
            {
                size = baseDepth * (0.4 + distance * 0.05) * Math.Exp(Gaussian() * 0.7);
                // paredes ocasionales en números redondos
                if (price % 50 == 0 && _rng.NextDouble() < 0.6) size *= 6 + _rng.NextDouble() * 10;
                dict[price] = size;
            }
            else if (_rng.NextDouble() < 0.15)
            {
                size *= Math.Exp(Gaussian() * 0.25);
                dict[price] = size;
            }
        }
    }
}
