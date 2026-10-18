using System.Diagnostics;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>
/// Simula una estrategia operación por operación sobre datos reales: arma las velas, llama a la
/// estrategia y llena las órdenes con reglas realistas (mercado con deslizamiento, límites solo si
/// el precio los atraviesa, stops como mercado al dispararse, comisiones y stops/targets OCO).
/// </summary>
public sealed class BacktestEngine : IStrategyHost
{
    private readonly Strategy _strategy;
    private readonly TickData _data;
    private readonly BacktestSettings _s;
    private readonly Instrument _inst;
    private readonly ChartPeriod _period;
    private readonly SessionSettings _session;
    private readonly TimeZoneInfo _tz;
    private readonly decimal _pointValue;
    private readonly bool _crypto;
    private readonly BarSeries _bars = new();
    private readonly List<StrategyOrder> _orders = new();
    private readonly List<ClosedTrade> _trades = new();
    private readonly List<EquityPoint> _equity = new();
    private readonly List<string> _log = new();
    private BarBuilder _builder = null!;
    private long _nextId = 1;
    private int _index;
    private long _submitIndex;
    private long _timeTicks, _priceTicks;

    // posición
    private decimal _pos, _avg, _realized, _commissionOpen, _commissionTotal;
    private DateTime _entryTime;
    private string _entryName = "";
    private int _entryBar, _tradeNumber;
    private double? _entryTrend, _entryVolatility, _entryVolatilityRank;
    private int? _entryDirection;
    private decimal _mae, _mfe, _highWater, _lowWater, _peak;
    private StrategyOrder? _sl, _tp;
    private long _barsInMarket;
    private DateOnly _sessionDay;
    private long _nextSessionTicks = long.MinValue; // cuándo empieza la próxima sesión (hasta ahí no se recalcula la fecha)

    public BacktestEngine(Strategy strategy, TickData data, BacktestSettings settings)
    {
        _strategy = strategy;
        _data = data;
        _s = settings;
        _inst = data.Instrument;
        _period = ChartPeriod.Parse(settings.Period);
        _tz = settings.TimeZoneId != null && TimeZoneInfo.TryFindSystemTimeZoneById(settings.TimeZoneId, out var z) ? z : TimeZoneInfo.Utc;
        _session = new SessionSettings { TimeZone = _tz, Start = settings.SessionStart };
        _pointValue = _inst.PointValue <= 0 ? 1 : _inst.PointValue;
        _crypto = _inst.FeedId.StartsWith("binance", StringComparison.Ordinal) || _inst.FeedId.StartsWith("bybit", StringComparison.Ordinal);
        _peak = settings.InitialCapital;
    }

    /// <summary>
    /// Cierres por hora del mercado (de antes y durante el rango): el contexto de la estrategia arranca con los anteriores a
    /// su primera vela, como en vivo (si no, las primeras horas no sabe si hay rango o tendencia y el filtro no bloquea, y el
    /// percentil de volatilidad se calcula con pocos días). Nunca usa los de después de su primera vela.
    /// </summary>
    public IReadOnlyList<(DateTime Time, decimal Close)>? ContextHistory { get; init; }

    /// <summary>Corre la estrategia completa.</summary>
    public BacktestResult Run(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new BacktestResult { Strategy = Strategy.DisplayName(_strategy.GetType()), Settings = _s.Clone() };
        try
        {
            _strategy.InternalInit(this);
            if (ContextHistory is { Count: > 0 } history && _data.End > _data.Start)
            {
                long first = _data.TimeTicks(_data.Start);
                long firstHour = first - first % TimeSpan.TicksPerHour;
                _strategy.Context.AddHistory(history.Where(h => h.Time.Ticks < firstHour), _period.IsTimeBased ? _period.ApproxDuration : TimeSpan.FromHours(1));
            }

            result.Parameters = _strategy.GetParameters();
            _builder = new BarBuilder(_inst, _period, _session, _strategy.UseFootprint);
            bool ticks = _strategy.WantsTicks;
            for (int i = _data.Start; i < _data.End; i++)
            {
                if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                _index = i;
                long t = _data.TimeTicks(i), p = _data.PriceTicks(i);
                _timeTicks = t;

                // 1) cierre de vela (la estrategia decide con lo que había hasta el cierre)
                var closed = _builder.Roll(t, p);
                if (closed != null) OnBarClosed(closed);
                // la fecha de sesión solo cambia al empezar la sesión siguiente: convertir de zona horaria en cada tick
                // costaba casi tanto como el resto del motor
                if (t >= _nextSessionTicks)
                {
                    var day = _session.SessionDate(new DateTime(t, DateTimeKind.Utc));
                    _nextSessionTicks = NextSessionTicks(day, t);
                    if (day != _sessionDay)
                    {
                        bool first = _sessionDay == default;
                        _sessionDay = day;
                        if (!first)
                        {
                            _submitIndex = i - 1;
                            _strategy.InternalSession(day);
                        }
                    }
                }

                // 2) ejecuciones con esta operación
                _priceTicks = p;
                if (_orders.Count > 0) ProcessFills(i, p, t);

                // 3) la operación entra en la vela en formación
                _builder.Append(t, p, _data.Volume(i), _data.Side(i));
                if (_pos != 0) TrackExcursion(p);
                if (ticks)
                {
                    _submitIndex = i;
                    _strategy.InternalTick(_data.Get(i));
                }
            }

            var last = _builder.Flush();
            if (last != null) OnBarClosed(last);
            if (_s.CloseAtEnd && _pos != 0)
            {
                foreach (var o in _orders.ToList()) Cancel(o);
                var exit = new StrategyOrder { Id = _nextId++, Side = _pos > 0 ? OrderSide.Sell : OrderSide.Buy, Type = StrategyOrderType.Market, Quantity = Math.Abs(_pos), Name = "End of backtest", Time = Time };
                ApplyFill(exit, _priceTicks, Time, notify: false);
            }

            if (_pos != 0) result.OpenPosition = new OpenPosition(_pos > 0, Math.Abs(_pos), _avg, _entryTime);
            _strategy.InternalStop();
            AddEquity(Time);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            _log.Add("ERROR " + ex);
        }

        result.Trades = _trades;
        result.Equity = _equity;
        result.Log = _log;
        result.Ticks = _data.Count;
        result.Bars = _bars.Count;
        result.Metrics = PerformanceMetrics.Compute(_trades, _equity, _s.InitialCapital, _crypto, _barsInMarket, _bars.Count);
        result.Seconds = sw.Elapsed.TotalSeconds;
        return result;
    }

    /// <summary>Cuándo empieza la sesión siguiente (si la hora cae en un cambio de horario raro, se vuelve a mirar en un minuto).</summary>
    private long NextSessionTicks(DateOnly day, long now)
    {
        try
        {
            long next = _session.SessionStartUtc(day.AddDays(1)).Ticks;
            return next > now ? next : now + TimeSpan.TicksPerMinute;
        }
        catch (ArgumentException)
        {
            return now + TimeSpan.TicksPerMinute;
        }
    }

    private void OnBarClosed(Bar bar)
    {
        _bars.Add(bar);
        _priceTicks = _inst.ToTicks(bar.Close);
        if (_pos != 0) _barsInMarket++;
        AddEquity(bar.CloseTime);
        _submitIndex = _index - 1;
        _strategy.InternalBar(bar);
    }

    private void AddEquity(DateTime time)
    {
        decimal eq = _s.InitialCapital + _realized + OpenPnL - _commissionOpen;
        _peak = Math.Max(_peak, eq);
        _equity.Add(new EquityPoint(time, eq, _peak - eq));
    }

    // ----------------------------------------------------------------- ejecuciones

    private void ProcessFills(int i, long p, long t)
    {
        long slip = _s.SlippageTicks;
        bool touch = _s.LimitFill == LimitFillMode.Touch;
        var snapshot = _orders.ToArray();
        foreach (var o in snapshot)
        {
            if (!o.IsActive || o.SubmittedAt >= i) continue;
            long fill;
            bool buy = o.Side == OrderSide.Buy;
            long level = _inst.ToTicks(o.Price);
            switch (o.Type)
            {
                case StrategyOrderType.Market:
                    fill = buy ? p + slip : p - slip;
                    break;
                case StrategyOrderType.Limit:
                    if (o.Marketable)
                    {
                        // límite que ya cruzaba el precio: se llena como mercado, sin pasar el límite
                        fill = buy ? Math.Min(level, p + slip) : Math.Max(level, p - slip);
                        break;
                    }

                    if (buy ? (p < level || (touch && p <= level)) : (p > level || (touch && p >= level))) fill = level;
                    else continue;
                    break;
                default:
                    if (buy ? p >= level : p <= level) fill = buy ? p + slip : p - slip;
                    else continue;
                    break;
            }

            ApplyFill(o, fill, new DateTime(t, DateTimeKind.Utc), notify: true);
        }
    }

    private void ApplyFill(StrategyOrder o, long fillTicks, DateTime time, bool notify)
    {
        decimal price = _inst.FromTicks(fillTicks);
        decimal qty = o.Quantity;
        o.State = StrategyOrderState.Filled;
        o.FillPrice = price;
        o.FillTime = time;
        _orders.Remove(o);

        // OCO: se cancela el par del stop/target
        if (o.OcoGroup != null)
        {
            foreach (var other in _orders.Where(x => x.OcoGroup == o.OcoGroup).ToList()) Cancel(other);
        }

        decimal commission = qty * _s.CommissionPerUnit + Math.Abs(qty * price * _pointValue) * _s.CommissionPercent / 100m;
        _commissionTotal += commission;
        decimal signed = o.Side == OrderSide.Buy ? qty : -qty;
        if (_pos == 0 || Math.Sign(_pos) == Math.Sign(signed))
        {
            if (_pos == 0) OpenTrade(o.Name, time, price);
            _avg = (_avg * Math.Abs(_pos) + price * qty) / (Math.Abs(_pos) + qty);
            _pos += signed;
            _commissionOpen += commission;
        }
        else
        {
            decimal closeQty = Math.Min(qty, Math.Abs(_pos));
            int dir = Math.Sign(_pos);
            decimal pnl = (price - _avg) * closeQty * dir * _pointValue;
            decimal entryComm = _commissionOpen * closeQty / Math.Abs(_pos);
            decimal exitComm = commission * closeQty / qty;
            _commissionOpen -= entryComm;
            _realized += pnl - entryComm - exitComm;
            _trades.Add(new ClosedTrade(++_tradeNumber, dir > 0, _entryTime, _avg, time, price, closeQty, pnl, entryComm + exitComm,
                _mae * closeQty * _pointValue, _mfe * closeQty * _pointValue, _entryName, o.Name, _bars.Count - _entryBar)
            {
                EntryTrend = _entryTrend,
                EntryVolatility = _entryVolatility,
                EntryVolatilityRank = _entryVolatilityRank,
                EntryMarketDirection = _entryDirection,
            });
            _pos -= dir * closeQty;
            decimal rest = qty - closeQty;
            if (_pos == 0)
            {
                _avg = 0;
                _commissionOpen = 0;
            }

            if (rest > 0)
            {
                // da vuelta la posición
                OpenTrade(o.Name, time, price);
                _avg = price;
                _pos = o.Side == OrderSide.Buy ? rest : -rest;
                _commissionOpen = commission * rest / qty;
            }
        }

        _submitIndex = _index;
        UpdateProtection();
        if (notify)
        {
            _submitIndex = _index;
            _strategy.InternalFilled(o);
        }
    }

    private void OpenTrade(string name, DateTime time, decimal price)
    {
        _entryTime = time;
        _entryName = name;
        _entryBar = _bars.Count;
        _mae = _mfe = 0;
        _highWater = _lowWater = price;
        // el contexto que vio la estrategia al entrar (el mismo que usa su filtro de contexto)
        var context = _strategy.Context;
        (_entryTrend, _entryVolatility, _entryVolatilityRank, _entryDirection) = (context.Trend, context.Volatility, context.VolatilityRank, context.Direction);
    }

    private void TrackExcursion(long p)
    {
        decimal price = _inst.FromTicks(p);
        decimal exc = (price - _avg) * Math.Sign(_pos);
        if (exc > _mfe) _mfe = exc;
        if (exc < _mae) _mae = exc;
        if (_strategy.TrailingStopTicks <= 0) return;

        // trailing stop: acompaña al precio a favor
        decimal dist = _strategy.TrailingStopTicks * _inst.TickSize;
        if (_pos > 0)
        {
            if (price <= _highWater) return;
            _highWater = price;
            decimal stop = _inst.RoundPrice(price - dist);
            if (_sl is { IsActive: true } && stop > _sl.Price) _sl.Price = stop;
        }
        else
        {
            if (price >= _lowWater) return;
            _lowWater = price;
            decimal stop = _inst.RoundPrice(price + dist);
            if (_sl is { IsActive: true } && stop < _sl.Price) _sl.Price = stop;
        }
    }

    /// <summary>Rehace el stop loss y el take profit automáticos según la posición.</summary>
    private void UpdateProtection()
    {
        if (_sl is { IsActive: true }) Cancel(_sl);
        if (_tp is { IsActive: true }) Cancel(_tp);
        _sl = _tp = null;
        if (_pos == 0) return;
        decimal qty = Math.Abs(_pos);
        var exitSide = _pos > 0 ? OrderSide.Sell : OrderSide.Buy;
        string group = "oco" + _nextId;
        int stopTicks = _strategy.StopLossTicks > 0 ? _strategy.StopLossTicks : _strategy.TrailingStopTicks;
        if (stopTicks > 0)
        {
            decimal d = stopTicks * _inst.TickSize;
            _sl = NewOrder(exitSide, StrategyOrderType.Stop, qty, _inst.RoundPrice(_pos > 0 ? _avg - d : _avg + d), "Stop loss", group, protective: true);
        }

        if (_strategy.TakeProfitTicks > 0)
        {
            decimal d = _strategy.TakeProfitTicks * _inst.TickSize;
            _tp = NewOrder(exitSide, StrategyOrderType.Limit, qty, _inst.RoundPrice(_pos > 0 ? _avg + d : _avg - d), "Take profit", group, protective: true);
        }
    }

    private StrategyOrder NewOrder(OrderSide side, StrategyOrderType type, decimal qty, decimal price, string name, string? oco, bool protective)
    {
        decimal last = LastPrice;
        bool marketable = type == StrategyOrderType.Limit && (side == OrderSide.Buy ? price >= last : price <= last);
        var o = new StrategyOrder
        {
            Id = _nextId++,
            Side = side,
            Type = type,
            Quantity = qty,
            Price = price,
            Name = name,
            Time = Time,
            SubmittedAt = _submitIndex,
            OcoGroup = oco,
            IsProtective = protective,
            Marketable = marketable && !protective,
        };
        _orders.Add(o);
        return o;
    }

    // ----------------------------------------------------------------- IStrategyHost

    public Instrument Instrument => _inst;
    public ChartPeriod Period => _period;
    public BarSeries Bars => _bars;
    public Bar? FormingBar => _builder?.Current;
    public DateTime Time => new(_timeTicks, DateTimeKind.Utc);
    public decimal LastPrice => _inst.FromTicks(_priceTicks);
    public TimeZoneInfo TimeZone => _tz;
    public SessionSettings Session => _session;
    public decimal Position => _pos;
    public decimal AveragePrice => _avg;
    public decimal OpenPnL => _pos == 0 ? 0 : (LastPrice - _avg) * _pos * _pointValue;
    public decimal RealizedPnL => _realized;
    public IReadOnlyList<StrategyOrder> ActiveOrders => _orders.ToList();
    public bool IsBacktest => true;

    public StrategyOrder Submit(OrderSide side, StrategyOrderType type, decimal quantity, decimal price, string name)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be greater than zero.");
        if (type != StrategyOrderType.Market && price <= 0) throw new ArgumentException("Order price must be greater than zero.");
        return NewOrder(side, type, quantity, price, name, null, protective: false);
    }

    public void Cancel(StrategyOrder order)
    {
        if (!order.IsActive) return;
        order.State = StrategyOrderState.Canceled;
        _orders.Remove(order);
    }

    public void Modify(StrategyOrder order, decimal price)
    {
        if (order.IsActive && order.Type != StrategyOrderType.Market && price > 0) order.Price = price;
    }

    public void Log(string message)
    {
        if (_log.Count < 5000) _log.Add($"{Time:yyyy-MM-dd HH:mm:ss}  {message}");
    }
}
