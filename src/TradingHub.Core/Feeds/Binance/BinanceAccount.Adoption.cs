using System.Globalization;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Feeds.Binance;

/// <summary>
/// Dragging an unowned order that has already executed in part (placed on Binance's website or by another app) adopts
/// it only after Binance itself establishes what executed before the drag. The order's executions and its executed
/// quantity are read and must agree exactly: that quantity becomes the adopted baseline and those executions the
/// excluded prefix, so only later fills are owned and priced. Anything else refuses the drag fail-closed and leaves the
/// order unowned and unchanged, so its fills can never leave owned quantity without priced executions.
/// </summary>
public sealed partial class BinanceBroker : IAuthoritativeAdoption
{
    internal const string AdoptionStillReading = "Not modified: this order's earlier executions are still being read.";
    internal const string AdoptionHistoryNotReady =
        "Not modified: Binance execution history is still synchronizing. The order was left unowned and unchanged; try again.";
    internal const string AdoptionOrderFinished = "Not modified: the order already finished at Binance.";
    internal const string AdoptionExecutedWhileReading =
        "Not modified: the order executed while its earlier executions were being read. It was left unowned and unchanged; try again.";
    internal const string AdoptionHistoryIncomplete =
        "Not modified: Binance's trade history does not account for this order's executed quantity. It was left unowned and unchanged.";
    internal const string AdoptionOrderChanged =
        "Not modified: the order changed while its earlier executions were being read. It was left unowned and unchanged.";
    private static string AdoptionReadFailed(string reason) =>
        $"Not modified: Binance could not confirm this order's earlier executions ({reason}). It was left unowned and unchanged.";

    // Handles whose earlier executions are being read before adoption; no other amendment may adopt them meanwhile.
    private readonly HashSet<Tracked> _adopting = new(ReferenceEqualityComparer.Instance);

    public bool TryAdoptAndReplace(Order order, decimal price, decimal? quantity)
    {
        lock (_gate)
        {
            if (_disposed || !string.IsNullOrEmpty(order.OwnerId) || order.Filled <= 0 ||
                !_byOrder.TryGetValue(order, out var t) || t.Own) return false;
            if (_adopting.Contains(t)) LastError = AdoptionStillReading;
            else if (order.State != OrderStates.Active || t.CancelRequested || t.Id == 0 || t.AlgoId != 0 || t.Busy > 0)
                LastError = "Cannot replace an order while its submission or previous replacement outcome is unresolved.";
            else
            {
                _adopting.Add(t);
                Enqueue(t, () => AdoptAndReplaceAsync(t, price, quantity));
            }
        }
        Publish();
        return true;
    }

    private async Task AdoptAndReplaceAsync(Tracked t, decimal price, decimal? quantity)
    {
        var order = t.Order;
        long id;
        ExecutionScope? scope;
        lock (_gate)
        {
            id = t.Id;
            scope = _executionScope;
        }
        string? refusal = null;
        decimal baseline = 0;
        var prior = Array.Empty<ExecutionIdentity>();
        try
        {
            if (scope == null) refusal = AdoptionHistoryNotReady;
            else
            {
                // The order, its executions, then the order again: an unchanged executed quantity around the trade read
                // proves no execution happened meanwhile (it only grows), so the trades must then account for it exactly.
                string side = order.Direction == OrderDirections.Buy ? "BUY" : "SELL";
                var before = await _client.GetOrderAsync(Instrument.Symbol, id, _cts.Token).ConfigureAwait(false);
                var trades = before == null || before.Status is not ("NEW" or "PARTIALLY_FILLED") ? null
                    : await _client.GetOrderTradesAsync(Instrument.Symbol, id, _cts.Token).ConfigureAwait(false);
                var after = trades == null ? null : await _client.GetOrderAsync(Instrument.Symbol, id, _cts.Token).ConfigureAwait(false);
                if (trades == null || after == null || after.Status is not ("NEW" or "PARTIALLY_FILLED")) refusal = AdoptionOrderFinished;
                else if (before!.OrderId != id || after.OrderId != id || before.Symbol != Instrument.Symbol || after.Symbol != Instrument.Symbol ||
                    before.Side != side || after.Side != side) refusal = AdoptionOrderChanged;
                else if (after.Executed != before.Executed) refusal = AdoptionExecutedWhileReading;
                else if (trades.Count >= BinanceAccountClient.TradePageLimit ||
                    trades.Any(trade => trade.OrderId != id || trade.Symbol != Instrument.Symbol || trade.Buy != (side == "BUY")) ||
                    trades.Select(trade => trade.Id).Distinct().Count() != trades.Count ||
                    trades.Sum(trade => trade.Quantity) != after.Executed)
                    refusal = AdoptionHistoryIncomplete;
                else
                {
                    baseline = after.Executed;
                    prior = trades.Select(trade => new ExecutionIdentity(scope, trade.Id.ToString(CultureInfo.InvariantCulture))).ToArray();
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            lock (_gate) _adopting.Remove(t);
            return;
        }
        catch (Exception ex)
        {
            refusal = AdoptionReadFailed(ex.Message);
        }

        lock (_gate)
        {
            _adopting.Remove(t);
            if (_disposed) return;
            // An execution after the order read is a later fill: it stays owned. Anything else that moved the handle
            // (a cancel, a finish, a binding) means the drag no longer amends the order that was read.
            if (refusal == null && (!_byOrder.TryGetValue(order, out var current) || !ReferenceEquals(current, t) || t.Id != id ||
                t.AlgoId != 0 || order.State != OrderStates.Active || t.CancelRequested || !string.IsNullOrEmpty(order.OwnerId)))
                refusal = AdoptionOrderChanged;
            if (refusal == null)
            {
                try
                {
                    OrderContext.Adopt(order, OrderContext.ManualOwner, baseline, prior, baselineKnown: true, protective: order.ReduceOnly);
                }
                catch (Exception ex)
                {
                    refusal = AdoptionReadFailed(ex.Message);
                }
            }
            if (refusal != null) LastError = refusal;
        }
        if (refusal == null)
        {
            // The order is now bound: the ordinary amendment path prepares from the adopted claim, never adopting again.
            var intent = quantity is { } remaining
                ? OrderContext.PrepareReplacement(order, price, remaining)
                : OrderContext.PreparePriceUpdate(order, price);
            Replace(order, intent);
        }
        Publish();
        PublishAccountChanged();
    }
}
