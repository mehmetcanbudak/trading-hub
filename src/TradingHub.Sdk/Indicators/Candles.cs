using TradingHub.Data;

namespace TradingHub.Indicators
{
    /// <summary>Volumen en un nivel de precio de una vela (una fila del footprint).</summary>
    public class PriceVolumeInfo
    {
        public decimal Price { get; set; }
        public decimal Volume { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal Between { get; set; }
        public decimal Ticks { get; set; }

        /// <summary>"Tiempo" en el nivel (en Trading Hub, cantidad de operaciones como aproximación).</summary>
        public int Time { get; set; }

        public decimal Delta => Ask - Bid;

        public PriceVolumeInfo()
        {
        }

        public PriceVolumeInfo(decimal price)
        {
            Price = price;
        }

        public PriceVolumeInfo Clone() => (PriceVolumeInfo)MemberwiseClone();

        public override string ToString() => $"{Price}: {Bid} x {Ask}";
    }

    /// <summary>Área de valor de una vela o perfil.</summary>
    public sealed class ValueArea
    {
        public decimal ValueAreaHigh { get; set; }
        public decimal ValueAreaLow { get; set; }
        public decimal PointOfControl { get; set; }
    }

    /// <summary>
    /// Vela tal como la ven los indicadores (API compatible con la de ATAS).
    /// Envuelve una vela del núcleo y convierte filas/ticks a precios.
    /// </summary>
    public class IndicatorCandle
    {
        private readonly TradingHub.Data.Candle _c;
        private readonly CandleSeries _series;

        public IndicatorCandle(TradingHub.Data.Candle candle, CandleSeries series)
        {
            _c = candle;
            _series = series;
        }

        internal TradingHub.Data.Candle Core => _c;

        public DateTime Time => _c.Time;
        public DateTime LastTime => _c.LastTime;
        public decimal Open => _series.TicksToPrice(_c.OpenTicks);
        public decimal High => _series.TicksToPrice(_c.HighTicks);
        public decimal Low => _series.TicksToPrice(_c.LowTicks);
        public decimal Close => _series.TicksToPrice(_c.CloseTicks);
        public decimal Volume => _c.Volume;
        public decimal Bid => _c.Bid;
        public decimal Ask => _c.Ask;
        public decimal Betweenvolume => _c.Between;
        public decimal Between => _c.Between;
        public decimal Delta => _c.Ask - _c.Bid;
        public decimal MaxDelta => _c.MaxDelta;
        public decimal MinDelta => _c.MinDelta;
        public decimal Ticks => _c.Trades;
        public decimal OI => _c.OI;
        public decimal MaxOI => _c.MaxOI;
        public decimal MinOI => _c.MinOI;
        public bool HasOpenInterest => _c.HasOpenInterest;
        public bool HasOpenInterestRange => _c.HasOpenInterestRange;
        public DateTime? OpenInterestTime => _c.OpenInterestTime;
        public bool HasOpenInterestChange => _c.HasOpenInterestChange;
        public decimal OpenInterestChange => _c.OpenInterestChange;
        public string? OpenInterestStatus => _c.OpenInterestStatus;
        /// <summary>At least one observed footprint row; mixed aggregate history may also be present.</summary>
        public bool HasObservedFootprint => !_c.Levels.IsEmpty;
        /// <summary>All contributions in this candle came from observed trades, not aggregate bars.</summary>
        public bool IsFootprintComplete => _c.HasRealTrades && !_c.HasSyntheticTrades;
        public bool IsCandleFinished => _c.IsClosed;
        public decimal MaxTradeVolume => _c.MaxTradeVolume;

        public decimal Body => Math.Abs(Close - Open);
        public bool IsBullish => Close > Open;
        public bool IsBearish => Close < Open;

        private PriceVolumeInfo Make(long row, in LevelData d) => new()
        {
            Price = _series.RowPrice(row),
            Volume = d.Volume,
            Bid = d.Bid,
            Ask = d.Ask,
            Between = d.Between,
            Ticks = d.Trades,
            Time = d.Trades,
        };

        /// <summary>Nivel de precio exacto (o null si no hubo operaciones ahí).</summary>
        public PriceVolumeInfo GetPriceVolumeInfo(decimal price)
        {
            long row = _series.PriceToRow(price);
            return _c.Levels.TryGet(row, out var d) ? Make(row, d) : null;
        }

        /// <summary>Igual que <see cref="GetPriceVolumeInfo(decimal)"/> pero reutilizando un objeto (sin asignar memoria).</summary>
        public PriceVolumeInfo GetPriceVolumeInfo(decimal price, PriceVolumeInfo reuse)
        {
            long row = _series.PriceToRow(price);
            if (!_c.Levels.TryGet(row, out var d)) return null;
            reuse ??= new PriceVolumeInfo();
            reuse.Price = _series.RowPrice(row);
            reuse.Volume = d.Volume;
            reuse.Bid = d.Bid;
            reuse.Ask = d.Ask;
            reuse.Between = d.Between;
            reuse.Ticks = d.Trades;
            reuse.Time = d.Trades;
            return reuse;
        }

        /// <summary>VWAP de la vela (precio promedio ponderado por volumen).</summary>
        public decimal VWAP
        {
            get
            {
                decimal pv = 0, v = 0;
                foreach (var l in GetAllPriceLevels())
                {
                    pv += l.Price * l.Volume;
                    v += l.Volume;
                }

                return v > 0 ? pv / v : 0;
            }
        }

        /// <summary>Área de valor (70%) y POC.</summary>
        public ValueArea ValueArea
        {
            get
            {
                if (_c.Levels.IsEmpty) return null;
                var (hi, lo) = GetValueArea();
                return new ValueArea { ValueAreaHigh = hi, ValueAreaLow = lo, PointOfControl = _series.RowPrice(_c.PocRow) };
            }
        }

        /// <summary>Nivel donde más tiempo estuvo el precio (aproximado por cantidad de operaciones).</summary>
        public PriceVolumeInfo MaxTimePriceInfo => MaxTickPriceInfo;

        /// <summary>Todos los niveles con operaciones, de menor a mayor precio.</summary>
        public IEnumerable<PriceVolumeInfo> GetAllPriceLevels()
        {
            foreach (var (row, d) in _c.Levels.Enumerate()) yield return Make(row, d);
        }

        /// <summary>Niveles ordenados por precio (lista materializada).</summary>
        public List<PriceVolumeInfo> GetAllPriceLevelsList() => GetAllPriceLevels().ToList();

        /// <summary>POC de la vela (nivel con más volumen).</summary>
        public PriceVolumeInfo MaxVolumePriceInfo
        {
            get
            {
                if (_c.Levels.IsEmpty) return null;
                return _c.Levels.TryGet(_c.PocRow, out var d) ? Make(_c.PocRow, d) : null;
            }
        }

        public PriceVolumeInfo MaxAskPriceInfo => MaxBy(d => d.Ask);
        public PriceVolumeInfo MaxBidPriceInfo => MaxBy(d => d.Bid);
        public PriceVolumeInfo MaxTickPriceInfo => MaxBy(d => d.Trades);
        public PriceVolumeInfo MaxPositiveDeltaPriceInfo => MaxBy(d => d.Delta);
        public PriceVolumeInfo MaxNegativeDeltaPriceInfo => MaxBy(d => -d.Delta);

        private PriceVolumeInfo MaxBy(Func<LevelData, decimal> selector)
        {
            long best = 0;
            decimal bestV = decimal.MinValue;
            LevelData bestD = default;
            foreach (var (row, d) in _c.Levels.Enumerate())
            {
                var v = selector(d);
                if (v > bestV)
                {
                    bestV = v;
                    best = row;
                    bestD = d;
                }
            }

            return bestV == decimal.MinValue ? null : Make(best, bestD);
        }

        /// <summary>Área de valor (70% del volumen por defecto) alrededor del POC.</summary>
        public (decimal High, decimal Low) GetValueArea(decimal percent = 70m)
        {
            var levels = _c.Levels;
            if (levels.IsEmpty) return (High, Low);
            decimal total = 0;
            foreach (var (_, d) in levels.Enumerate()) total += d.Volume;
            decimal target = total * percent / 100m;
            long up = _c.PocRow, dn = _c.PocRow;
            decimal acc = levels.Get(_c.PocRow).Volume;
            while (acc < target && (up < levels.MaxRow || dn > levels.MinRow))
            {
                decimal vu = up < levels.MaxRow ? levels.Get(up + 1).Volume : -1;
                decimal vd = dn > levels.MinRow ? levels.Get(dn - 1).Volume : -1;
                if (vu >= vd)
                {
                    up++;
                    acc += Math.Max(0, vu);
                }
                else
                {
                    dn--;
                    acc += Math.Max(0, vd);
                }
            }

            return (_series.RowPrice(up), _series.RowPrice(dn));
        }

        public override string ToString() => $"{Time:yyyy-MM-dd HH:mm} O:{Open} H:{High} L:{Low} C:{Close} V:{Volume}";
    }
}
