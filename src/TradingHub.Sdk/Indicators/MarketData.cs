using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Indicators
{
    /// <summary>Un evento de mercado: operación, o cambio de un nivel del libro.</summary>
    public class MarketDataArg
    {
        public DateTime Time { get; set; }
        public decimal Price { get; set; }
        public decimal Volume { get; set; }
        public TradeDirection Direction { get; set; }
        public MarketDataType DataType { get; set; }
        public string Security { get; set; }
        public long? AggressorExchangeOrderId { get; set; }
        public long? ExchangeOrderId { get; set; }
        public long Id { get; set; }
        public decimal OpenInterest { get; set; }
        /// <summary>False for a missing/stale quote; zero price is otherwise a valid price.</summary>
        public bool IsAvailable { get; set; } = true;
        public DepthCoverage DepthCoverage { get; set; }
        public string? ProviderId { get; set; }
        public string? Venue { get; set; }
        public TradeProvenance Provenance { get; set; } = new(DataQuality.Raw);
        public DataQuality Quality => Provenance.Quality;
        public int ResolutionSeconds => Provenance.ResolutionSeconds;
        public string? SourceIdentity => Provenance.SourceIdentity;
        public int ObservedTradeCount => Provenance.ObservedTradeCount;
        public bool IsObservedTrade => IsTrade && Provenance.IsObservedTrade;

        /// <summary>Precio original (antes de agrupar por filas).</summary>
        public decimal OriginPrice { get; set; }

        public bool IsBid => DataType == MarketDataType.Bid;
        public bool IsAsk => DataType == MarketDataType.Ask;
        public bool IsTrade => DataType == MarketDataType.Trade;

        public MarketDataArg Clone() => (MarketDataArg)MemberwiseClone();

        public override string ToString() => $"{DataType} {Direction} {Volume}@{Price} {Time:HH:mm:ss.fff}";
    }

    /// <summary>
    /// Operación acumulada: todas las ejecuciones de una misma orden agresiva
    /// (mismo instante y mismo lado), aunque haya barrido varios niveles.
    /// </summary>
    public class CumulativeTrade
    {
        public DateTime Time { get; set; }
        public decimal FirstPrice { get; set; }
        public decimal Lastprice { get; set; }
        public decimal Volume { get; set; }
        public TradeDirection Direction { get; set; }
        public List<MarketDataArg> Ticks { get; set; } = new();
        public MarketDataArg PreviousBid { get; set; }
        public MarketDataArg PreviousAsk { get; set; }
        public MarketDataArg NewBid { get; set; }
        public MarketDataArg NewAsk { get; set; }
        public CumulativeTradeStates State { get; set; }

        public decimal LastPrice => Lastprice;
        public decimal MaxPrice => Ticks.Count > 0 ? Ticks.Max(t => t.Price) : Math.Max(FirstPrice, Lastprice);
        public decimal MinPrice => Ticks.Count > 0 ? Ticks.Min(t => t.Price) : Math.Min(FirstPrice, Lastprice);

        /// <summary>Si es la misma operación acumulada (se actualiza mientras la orden sigue barriendo).</summary>
        public bool IsEqual(CumulativeTrade other) =>
            other != null && other.Time == Time && other.Direction == Direction && other.FirstPrice == FirstPrice;

        public new CumulativeTrade MemberwiseClone() => (CumulativeTrade)base.MemberwiseClone();

        public override string ToString() => $"{Direction} {Volume} {FirstPrice}->{Lastprice} {Time:HH:mm:ss.fff}";
    }

    /// <summary>Pedido de operaciones acumuladas históricas.</summary>
    public class CumulativeTradesRequest
    {
        public CumulativeTradesRequest(DateTime beginTime)
        {
            BeginTime = beginTime;
            EndTime = DateTime.MaxValue;
        }

        public CumulativeTradesRequest(DateTime beginTime, DateTime endTime, decimal minVolume, decimal maxVolume)
        {
            BeginTime = beginTime;
            EndTime = endTime;
            MinVolume = minVolume;
            MaxVolume = maxVolume;
        }

        private static int _nextId;

        public int RequestId { get; } = Interlocked.Increment(ref _nextId);
        public DateTime BeginTime { get; }
        public DateTime EndTime { get; }
        public decimal MinVolume { get; }
        public decimal MaxVolume { get; }
    }

    /// <summary>Información del libro de órdenes para los indicadores.</summary>
    public class MarketDepthInfo
    {
        private readonly Func<IEnumerable<MarketDataArg>> _snapshot;
        private readonly Func<decimal> _cumAsks;
        private readonly Func<decimal> _cumBids;
        private readonly Func<DepthSample?>? _current;
        private readonly Func<DepthHistoryStatus>? _status;
        private readonly Func<DateTime, DateTime, DepthHistoryQuery>? _history;

        public DepthSample? Current => _current?.Invoke();
        public DepthHistoryStatus HistoryStatus => _status?.Invoke() ??
            new(DepthRecordingState.Disabled, "No depth provider is attached.", null, null, 0, DepthCoverage.Missing, true);

        public DepthHistoryQuery GetHistory(DateTime fromUtc, DateTime toUtc) =>
            _history?.Invoke(fromUtc, toUtc) ?? new(fromUtc, toUtc, Array.Empty<DepthSample>(), null, DepthCoverage.Missing, "Recorded depth is unavailable.");
        public MarketDepthInfo(Func<IEnumerable<MarketDataArg>> snapshot, Func<decimal> cumulativeAsks, Func<decimal> cumulativeBids)
        {
            _snapshot = snapshot;
            _cumAsks = cumulativeAsks;
            _cumBids = cumulativeBids;
        }

        public MarketDepthInfo(Func<IEnumerable<MarketDataArg>> snapshot, Func<decimal> cumulativeAsks,
            Func<decimal> cumulativeBids, Func<DepthSample?> current, Func<DepthHistoryStatus> status,
            Func<DateTime, DateTime, DepthHistoryQuery> history) : this(snapshot, cumulativeAsks, cumulativeBids)
        {
            _current = current;
            _status = status;
            _history = history;
        }

        public static MarketDepthInfo Empty { get; } = new(() => Array.Empty<MarketDataArg>(), () => 0, () => 0);

        public decimal CumulativeDomAsks => _cumAsks();
        public decimal CumulativeDomBids => _cumBids();

        public IEnumerable<MarketDataArg> GetMarketDepthSnapshot() => _snapshot();
    }

    /// <summary>Información del instrumento para los indicadores.</summary>
    public class InstrumentInfo
    {
        public string Instrument { get; set; } = "";
        public string Exchange { get; set; } = "";
        public decimal TickSize { get; set; } = 1m;
        public decimal TickCost { get; set; } = 1m;
        public TimeSpan TimeZoneOffset { get; set; } = TimeSpan.Zero;
        public Valuation Valuation { get; set; } = new();
        public decimal LotSize { get; set; } = 1m;
        public string Description { get; set; } = "";
        public int Digits => TradingHub.Market.Instrument.DecimalPlaces(TickSize);

        public override string ToString() => Instrument;
    }
}
