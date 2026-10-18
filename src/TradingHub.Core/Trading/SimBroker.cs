using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading.Statistics;

namespace TradingHub.Trading;

/// <summary>
/// Broker simulado (paper trading) para un instrumento. Llena órdenes contra el tape real:
/// - Mercado: al mejor bid/ask del libro (o al último precio si no hay libro).
/// - Límite: si el precio opera a través del límite, o si en el límite se operó más volumen
///   que el que había adelante en la cola al momento de enviar la orden.
/// - Stop: se dispara cuando opera en el precio de disparo y llena a mercado.
/// Se usa desde el hilo de UI.
/// </summary>
public sealed class SimBroker : TradingManager, ITradingAccount, IDisposable
{
    private readonly InstrumentData _data;
    private readonly List<Order> _orders = new();
    private readonly List<MyTrade> _myTrades = new();
    private readonly Dictionary<long, decimal> _queueAhead = new();
    private readonly TradingStatistics _stats;

    // operación abierta para las estadísticas (entrada → salida)
    private DateTime _openTime;
    private decimal _openPrice;
    private decimal _openVolume;
    private decimal _runUp, _drawDown;

    public SimBroker(InstrumentData data, Portfolio portfolio, TradingStatistics stats)
    {
        _data = data;
        _stats = stats;
        Portfolio = portfolio;
        Security = new Security
        {
            Code = data.Instrument.Symbol,
            SecurityId = data.Instrument.Key,
            Exchange = data.Instrument.Exchange,
            TickSize = data.Instrument.TickSize,
            TickCost = data.Instrument.TickSize * data.Instrument.PointValue,
            LotSize = data.Instrument.VolumeStep,
        };
        Position = new Position { Security = Security, Portfolio = portfolio };
        data.TradesAppended += OnTrades;
    }

    public Instrument Instrument => _data.Instrument;

    /// <summary>Nombre de la cuenta (cada estrategia puede usar una propia).</summary>
    // Persisted live-ledger keys include this legacy name; localize it only for display.
    public string Name { get; init; } = "Cuenta simulada";
    public bool IsReal => false;
    public decimal LastPrice => _data.LastPrice;
    public string? LastError { get; private set; }
    Position ITradingAccount.Position => Position!;

    /// <summary>Comisión por unidad de volumen (en moneda).</summary>
    public decimal CommissionPerUnit { get; set; }

    /// <summary>Comisión como fracción del nominal (0.0004 = 0.04%).</summary>
    public decimal CommissionRate { get; set; }

    public override IReadOnlyList<Order> Orders => _orders;
    public override IReadOnlyList<MyTrade> MyTrades => _myTrades;
    public IEnumerable<Order> ActiveOrders => _orders.Where(o => o.State == OrderStates.Active);

    public event Action? Changed;

    // ===================================================================== API de órdenes

    public Order Market(OrderDirections dir, decimal qty, string comment = "") =>
        Submit(new Order { Direction = dir, Type = OrderTypes.Market, QuantityToFill = qty, Comment = comment });

    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") =>
        Submit(new Order { Direction = dir, Type = OrderTypes.Limit, QuantityToFill = qty, Price = Instrument.RoundPrice(price), Comment = comment });

    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") =>
        Submit(new Order { Direction = dir, Type = OrderTypes.Stop, QuantityToFill = qty, TriggerPrice = Instrument.RoundPrice(trigger), Comment = comment });

    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") =>
        Submit(type == OrderTypes.Stop
            ? new Order { Direction = dir, Type = OrderTypes.Stop, QuantityToFill = qty, TriggerPrice = Instrument.RoundPrice(price), Comment = comment, ReduceOnly = true }
            : new Order { Direction = dir, Type = OrderTypes.Limit, QuantityToFill = qty, Price = Instrument.RoundPrice(price), Comment = comment, ReduceOnly = true });

    /// <summary>Cambia precio y cantidad en el lugar (la orden conserva su identidad).</summary>
    public Order Replace(Order o, decimal newPrice, decimal newQuantity)
    {
        if (o.State != OrderStates.Active) return o;
        if (newQuantity <= 0)
        {
            Cancel(o);
            return o;
        }

        o.QuantityToFill = o.Filled + newQuantity;
        o.Unfilled = newQuantity;
        Modify(o, newPrice);
        return o;
    }

    public override Task OpenOrderAsync(Order order)
    {
        Submit(order);
        return Task.CompletedTask;
    }

    public override Task CancelOrderAsync(Order order)
    {
        Cancel(order);
        return Task.CompletedTask;
    }

    public override Task ModifyOrderAsync(Order order, decimal newPrice)
    {
        Modify(order, newPrice);
        return Task.CompletedTask;
    }

    public Order Submit(Order o)
    {
        o.Security = Security!;
        o.Portfolio = Portfolio!;
        o.Unfilled = o.QuantityToFill;
        o.Time = DateTime.UtcNow;
        if (o.QuantityToFill <= 0)
        {
            o.State = OrderStates.Failed;
            o.Status = OrderStatus.Rejected;
            return o;
        }

        o.State = OrderStates.Active;
        o.Status = OrderStatus.Placed;
        _orders.Add(o);

        switch (o.Type)
        {
            case OrderTypes.Market:
                Fill(o, MarketPrice(o.Direction), o.Unfilled);
                break;
            case OrderTypes.Limit:
            {
                // si el límite ya cruza el mercado, llena como mercado al mejor precio
                var best = MarketPrice(o.Direction);
                bool marketable = o.Direction == OrderDirections.Buy ? o.Price >= best : o.Price <= best;
                if (marketable && best > 0) Fill(o, best, o.Unfilled);
                else _queueAhead[o.Id] = BookSizeAt(o.Direction, o.Price);
                break;
            }
        }

        RaiseOrderChanged(o);
        Changed?.Invoke();
        return o;
    }

    public void Cancel(Order o)
    {
        if (o.State != OrderStates.Active) return;
        o.State = OrderStates.Done;
        o.Status = OrderStatus.Canceled;
        _queueAhead.Remove(o.Id);
        RaiseOrderChanged(o);
        Changed?.Invoke();
    }

    public void CancelAll()
    {
        foreach (var o in ActiveOrders.ToList()) Cancel(o);
    }

    public void CancelAt(decimal price)
    {
        foreach (var o in ActiveOrders.Where(o => (o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price) == price).ToList()) Cancel(o);
    }

    public void Modify(Order o, decimal newPrice)
    {
        if (o.State != OrderStates.Active) return;
        newPrice = Instrument.RoundPrice(newPrice);
        if (o.Type == OrderTypes.Stop) o.TriggerPrice = newPrice;
        else
        {
            o.Price = newPrice;
            _queueAhead[o.Id] = BookSizeAt(o.Direction, newPrice);
        }

        RaiseOrderChanged(o);
        Changed?.Invoke();
    }

    /// <summary>Cierra la posición a mercado y cancela todas las órdenes.</summary>
    public void Flatten()
    {
        CancelAll();
        var pos = Position!.Volume;
        if (pos > 0) Market(OrderDirections.Sell, pos, "Close");
        else if (pos < 0) Market(OrderDirections.Buy, -pos, "Close");
    }

    /// <summary>Da vuelta la posición (de largo a corto o viceversa).</summary>
    public void Reverse()
    {
        var pos = Position!.Volume;
        if (pos > 0) Market(OrderDirections.Sell, pos * 2, "Reverse");
        else if (pos < 0) Market(OrderDirections.Buy, -pos * 2, "Reverse");
    }

    // ===================================================================== motor de llenado

    private decimal MarketPrice(OrderDirections dir)
    {
        var d = _data.Depth;
        if (dir == OrderDirections.Buy && d.HasAsk) return Instrument.FromTicks(d.BestAskTicks);
        if (dir == OrderDirections.Sell && d.HasBid) return Instrument.FromTicks(d.BestBidTicks);
        var last = _data.LastPrice;
        return dir == OrderDirections.Buy ? last + Instrument.TickSize : last - Instrument.TickSize;
    }

    private decimal BookSizeAt(OrderDirections dir, decimal price)
    {
        long ticks = Instrument.ToTicks(price);
        return _data.Depth.GetSize(dir == OrderDirections.Buy ? BookSide.Bid : BookSide.Ask, ticks);
    }

    private void OnTrades(int start, int count)
    {
        if (!_orders.Any(o => o.State == OrderStates.Active))
        {
            UpdateUnrealized();
            return;
        }

        var store = _data.Trades;
        for (int i = start; i < start + count; i++)
        {
            decimal price = store.GetPrice(i);
            decimal vol = store.GetVolume(i);
            foreach (var o in _orders.Where(o => o.State == OrderStates.Active).ToList())
            {
                // una ejecución anterior pudo cancelar esta orden (OCO)
                if (o.State != OrderStates.Active) continue;
                switch (o.Type)
                {
                    case OrderTypes.Limit:
                    {
                        bool through = o.Direction == OrderDirections.Buy ? price < o.Price : price > o.Price;
                        if (through)
                        {
                            Fill(o, o.Price, o.Unfilled);
                        }
                        else if (price == o.Price)
                        {
                            var ahead = _queueAhead.GetValueOrDefault(o.Id) - vol;
                            _queueAhead[o.Id] = ahead;
                            if (ahead < 0) Fill(o, o.Price, Math.Min(o.Unfilled, -ahead));
                        }

                        break;
                    }
                    case OrderTypes.Stop:
                    {
                        bool trig = o.Direction == OrderDirections.Buy ? price >= o.TriggerPrice : price <= o.TriggerPrice;
                        if (trig) Fill(o, price, o.Unfilled);
                        break;
                    }
                }
            }
        }

        UpdateUnrealized();
    }

    private void Fill(Order o, decimal price, decimal qty)
    {
        if (qty <= 0) return;
        qty = Math.Min(qty, o.Unfilled);

        // solo-reducir: nunca abre ni da vuelta la posición
        if (o.ReduceOnly)
        {
            decimal open = Position!.Volume;
            bool reduces = o.Direction == OrderDirections.Buy ? open < 0 : open > 0;
            if (!reduces)
            {
                Cancel(o);
                return;
            }

            qty = Math.Min(qty, Math.Abs(open));
        }

        o.Unfilled -= qty;
        if (o.Unfilled <= 0)
        {
            o.State = OrderStates.Done;
            o.Status = OrderStatus.Filled;
            _queueAhead.Remove(o.Id);
        }
        else o.Status = OrderStatus.PartlyFilled;

        var pos = Position!;
        decimal signed = o.Direction == OrderDirections.Buy ? qty : -qty;
        decimal commission = qty * CommissionPerUnit + qty * price * Instrument.PointValue * CommissionRate;
        decimal realized = 0;

        decimal before = pos.Volume;
        if (before == 0 || Math.Sign(before) == Math.Sign(signed))
        {
            // abre o agranda
            decimal newVol = before + signed;
            pos.AveragePrice = before == 0 ? price : (pos.AveragePrice * Math.Abs(before) + price * qty) / Math.Abs(newVol);
            pos.Volume = newVol;
            if (before == 0)
            {
                _openTime = DateTime.UtcNow;
                _openPrice = price;
                _openVolume = signed;
                _runUp = _drawDown = 0;
            }
            else
            {
                _openPrice = pos.AveragePrice;
                _openVolume = newVol;
            }
        }
        else
        {
            // reduce, cierra o revierte
            decimal closing = Math.Min(Math.Abs(before), qty);
            realized = (price - pos.AveragePrice) * closing * Math.Sign(before) * Instrument.PointValue;
            decimal newVol = before + signed;
            RecordRoundTrip(price, closing * Math.Sign(before), realized - commission);
            if (newVol == 0)
            {
                pos.Volume = 0;
                pos.AveragePrice = 0;
            }
            else if (Math.Sign(newVol) == Math.Sign(before))
            {
                pos.Volume = newVol;
            }
            else
            {
                // revirtió: el resto abre en sentido contrario
                pos.Volume = newVol;
                pos.AveragePrice = price;
                _openTime = DateTime.UtcNow;
                _openPrice = price;
                _openVolume = newVol;
                _runUp = _drawDown = 0;
            }
        }

        pos.RealizedPnL += realized;
        pos.Commission += commission;
        Portfolio!.RealizedPnL += realized - commission;
        Portfolio.Commission += commission;
        Portfolio.Balance = Portfolio.BeginBalance + Portfolio.RealizedPnL;

        var mt = new MyTrade
        {
            Order = o,
            Security = Security!,
            Portfolio = Portfolio,
            OrderDirection = o.Direction,
            Price = price,
            Volume = qty,
            Time = DateTime.UtcNow,
            Commission = commission,
            RealizedPnL = realized,
        };
        _myTrades.Add(mt);
        RaiseNewMyTrade(mt);
        RaiseOrderChanged(o);
        RaisePositionChanged(pos);
        UpdateUnrealized();
        Changed?.Invoke();

        // solo-reducir: lo que sobra de la orden no puede abrir una posición nueva
        if (o.ReduceOnly && o.State == OrderStates.Active && pos.Volume == 0) Cancel(o);
    }

    private void RecordRoundTrip(decimal exitPrice, decimal signedVolume, decimal pnl)
    {
        decimal tick = Instrument.TickSize;
        _stats.HistoryMyTrades.Add(new HistoryMyTrade
        {
            Security = Security!,
            Portfolio = Portfolio!,
            OpenTime = _openTime,
            CloseTime = DateTime.UtcNow,
            OpenPrice = Position!.AveragePrice,
            ClosePrice = exitPrice,
            OpenVolume = signedVolume,
            PnL = pnl,
            TicksPnL = tick > 0 ? (exitPrice - Position.AveragePrice) / tick * Math.Sign(signedVolume) : 0,
            MaxRunUp = _runUp,
            MaxDrawDown = _drawDown,
        });
    }

    private void UpdateUnrealized()
    {
        var pos = Position!;
        var last = _data.LastPrice;
        pos.UnrealizedPnL = pos.Volume == 0 || last == 0 ? 0 : (last - pos.AveragePrice) * pos.Volume * Instrument.PointValue;
        if (pos.Volume != 0)
        {
            _runUp = Math.Max(_runUp, pos.UnrealizedPnL);
            _drawDown = Math.Min(_drawDown, pos.UnrealizedPnL);
        }

        Portfolio!.UnrealizedPnL = pos.UnrealizedPnL;
    }

    public void Dispose() => _data.TradesAppended -= OnTrades;
}
