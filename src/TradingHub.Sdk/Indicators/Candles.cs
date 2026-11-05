using TradingHub.Data;
using TradingHub.Market;

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
        public bool IsAggregated { get; set; }
        public DataQuality Quality { get; set; }
        public int ResolutionSeconds { get; set; }
        /// <summary>Partial aggregate coverage cannot establish a complete constituent-event count.</summary>
        public bool HasTradeCount => Ticks > 0 && (!IsAggregated || (Quality & DataQuality.Partial) == 0);

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
        public bool IsFootprintComplete => _c.IsFootprintComplete;
        public bool HasAggregatedFootprint => !_c.AggregatedLevels.IsEmpty;
        public bool HasRawTrades => _c.HasRealTrades;
        public bool HasCompactTrades => _c.HasCompactTrades;
        public bool IsSynthetic => _c.IsSynthetic;
        public bool HasInferredSide => _c.HasInferredSide;
        public DataQuality Quality => _c.Quality;
        public int ResolutionSeconds => _c.ResolutionSeconds;
        public decimal ProfileRowSize => _series.RowSize;
        public decimal MarketTickSize => _series.Instrument.TickSize;
        public DateTime AvailableAt => _c.AvailableAt;
        public bool HasKnownAvailability => _c.HasKnownAvailability;
        public bool IsTimeResolutionCompatible => !_c.HasIncompatibleResolution;
        public bool CanCalculateCausally => _c.Trades > 0 && HasKnownAvailability && IsTimeResolutionCompatible &&
            (_c.IsClosed || _c.AvailableAt <= _series.AvailableThrough);
        public bool IsTimeResolutionContained(DateTime startUtc, DateTime endUtc)
        {
            if (_c.Trades == 0 || !HasKnownAvailability || !IsTimeResolutionCompatible ||
                startUtc > _c.EarliestSourceTime || _c.EarliestSourceTime >= endUtc || _c.AvailableAt > endUtc) return false;
            if (_series.Period.IsTimeBased)
            {
                var bucket = _series.TimeBucket(_c.Time.Ticks);
                return startUtc.Ticks <= bucket.Start && bucket.End <= endUtc.Ticks;
            }
            return startUtc <= _c.Time && _c.LastTime < endUtc;
        }
        public string SourceIdentity => _c.SourceIdentity ?? _series.SourceIdentity;
        public string SeriesSourceIdentity => _series.SourceIdentity;
        public long Revision => _c.Revision;
        public long CorrectionRevision => _c.CorrectionRevision;
        public Guid SeriesIdentity => _series.Identity;
        public int ObservedTrades => _c.ObservedTrades;
        public long AggregatedTrades => _c.AggregatedTrades;
        public DateOnly SessionDate => _series.Session.SessionDate(_c.Time);
        public TimeZoneInfo SessionTimeZone => _series.Session.TimeZone;
        public DateTime SessionStartUtc(DateOnly date) => _series.Session.SessionStartUtc(date);
        public bool IsCandleFinished => _c.IsClosed;
        public decimal MaxTradeVolume => _c.MaxTradeVolume;

        public decimal Body => Math.Abs(Close - Open);
        public bool IsBullish => Close > Open;
        public bool IsBearish => Close < Open;

        private PriceVolumeInfo Make(long row, in LevelData d, bool aggregated = false) => new()
        {
            Price = _series.RowPrice(row),
            Volume = d.Volume,
            Bid = d.Bid,
            Ask = d.Ask,
            Between = d.Between,
            Ticks = d.Trades,
            Time = d.Trades,
            IsAggregated = aggregated,
            Quality = (aggregated ? DataQuality.Compact : DataQuality.Raw) | (_c.Quality & (DataQuality.InferredSide | DataQuality.Partial)),
            ResolutionSeconds = aggregated ? _c.ResolutionSeconds : 0,
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
            reuse.IsAggregated = false;
            reuse.Quality = DataQuality.Raw | (_c.Quality & (DataQuality.InferredSide | DataQuality.Partial));
            reuse.ResolutionSeconds = 0;
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

        /// <summary>Measured compact bucket totals at their declared resolution, not observed individual prints.</summary>
        public IEnumerable<PriceVolumeInfo> GetAggregatedPriceLevels()
        {
            foreach (var (row, data) in _c.AggregatedLevels.Enumerate()) yield return Make(row, data, aggregated: true);
        }

        public List<PriceVolumeInfo> GetAggregatedPriceLevelsList() => GetAggregatedPriceLevels().ToList();

        /// <summary>Observed plus measured aggregate rows; excludes all candle-derived estimates.</summary>
        public IEnumerable<PriceVolumeInfo> GetProfilePriceLevels()
        {
            if (!HasAggregatedFootprint)
            {
                foreach (var level in GetAllPriceLevels()) yield return level;
                yield break;
            }
            var rows = new SortedDictionary<long, LevelData>();
            foreach (var (row, data) in _c.Levels.Enumerate()) rows[row] = data;
            foreach (var (row, data) in _c.AggregatedLevels.Enumerate())
            {
                rows.TryGetValue(row, out var previous);
                previous.Ask += data.Ask;
                previous.Bid += data.Bid;
                previous.Between += data.Between;
                previous.Trades += data.Trades;
                rows[row] = previous;
            }
            foreach (var (row, data) in rows) yield return Make(row, data, aggregated: true);
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
            var levels = _c.Levels.Enumerate().OrderBy(x => x.Row).ToList();
            if (levels.Count == 0) return (High, Low);
            decimal total = levels.Sum(x => x.Data.Volume);
            decimal target = total * Math.Clamp(percent, 0, 100) / 100m;
            int poc = levels.FindIndex(x => x.Row == _c.PocRow);
            if (poc < 0) return (High, Low);
            int up = poc, down = poc;
            decimal accumulated = levels[poc].Data.Volume;
            while (accumulated < target && (up + 1 < levels.Count || down > 0))
            {
                decimal above = up + 1 < levels.Count ? levels[up + 1].Data.Volume : -1;
                decimal below = down > 0 ? levels[down - 1].Data.Volume : -1;
                accumulated += above >= below ? levels[++up].Data.Volume : levels[--down].Data.Volume;
            }
            return (_series.RowPrice(levels[up].Row), _series.RowPrice(levels[down].Row));
        }

        public override string ToString() => $"{Time:yyyy-MM-dd HH:mm} O:{Open} H:{High} L:{Low} C:{Close} V:{Volume}";
    }
}
