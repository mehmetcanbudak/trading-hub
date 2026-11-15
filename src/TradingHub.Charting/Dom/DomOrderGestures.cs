using TradingHub.Trading;

namespace TradingHub.Charting.Dom;

/// <summary>Outcome of a pointer press on the DOM.</summary>
public enum DomPressResult
{
    /// <summary>Nothing under the pointer, or a single click while double-click trading is required.</summary>
    None,
    /// <summary>DOM trading is switched off: the press was not routed anywhere.</summary>
    Blocked,
    /// <summary>An own order was picked up for dragging (the view should capture the pointer).</summary>
    DragStarted,
    /// <summary>The press was routed to the order listener or, without one, directly to the account.</summary>
    Routed,
}

/// <summary>
/// Routes DOM pointer gestures (click to place or cancel, drag to reprice) to orders. <see cref="TradingEnabled"/> is
/// the DOM trading switch: when off, no gesture reaches <see cref="OrderRequested"/>, <see cref="OrderMoved"/> or the
/// account. When on, routing is exactly the existing click/drag path; confirmations, real-account permission and
/// risk limits stay in the order path behind the listeners.
/// </summary>
public sealed class DomOrderGestures
{
    public const string TradingOffMessage = "DOM trading is off: the DOM is read-only. Use the lock in the DOM header to enable it.";

    private readonly DomRenderer _renderer;

    public DomOrderGestures(DomRenderer renderer) => _renderer = renderer;

    /// <summary>Whether DOM gestures may place, cancel or move orders.</summary>
    public bool TradingEnabled { get; set; } = true;

    public bool IsDragging { get; private set; }

    /// <summary>Order request: column, price and whether it is a cancel (right click / Alt). Without a listener the DOM trades directly.</summary>
    public event Action<DomColumn, decimal, bool>? OrderRequested;

    /// <summary>An own order dragged to another price. Without a listener the DOM reprices directly.</summary>
    public event Action<Order, decimal>? OrderMoved;

    /// <summary>Result text of the last action (for the status bar).</summary>
    public event Action<string>? Message;

    public DomPressResult Press(DomModel dom, float x, float y, bool rightButton, bool alt, int clickCount)
    {
        if (!TradingEnabled)
        {
            Message?.Invoke(TradingOffMessage);
            return DomPressResult.Blocked;
        }

        bool cancel = rightButton || alt;
        // dragging an own order
        if (!cancel && _renderer.OrderAt(dom, x, y) is { } order)
        {
            dom.DragOrder = order;
            dom.DragPrice = order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price;
            IsDragging = true;
            return DomPressResult.DragStarted;
        }

        // with double-click trading a single click never sends an order (cancelling still works)
        if (!cancel && dom.Settings.DoubleClickToTrade && clickCount < 2) return DomPressResult.None;

        if (OrderRequested != null)
        {
            if (_renderer.HitTest(dom, x, y) is not { } hit) return DomPressResult.None;
            OrderRequested(hit.Column, hit.Price, cancel);
            return DomPressResult.Routed;
        }

        var message = _renderer.Click(dom, x, y, rightButton, alt);
        if (message == null) return DomPressResult.None;
        Message?.Invoke(message);
        return DomPressResult.Routed;
    }

    public void Move(DomModel dom, float y)
    {
        if (IsDragging && _renderer.HitTest(dom, 1, y) is { } hit) dom.DragPrice = hit.Price;
    }

    /// <summary>Ends a drag; true when a drag was in progress (the view should release the pointer).</summary>
    public bool Release(DomModel dom, float y)
    {
        if (!IsDragging) return false;
        IsDragging = false;
        var order = dom.DragOrder;
        dom.DragOrder = null;
        // switched off mid-drag: the order stays where it was
        if (order == null || !TradingEnabled) return true;
        if (_renderer.HitTest(dom, 1, y) is { } drop) dom.DragPrice = drop.Price;
        decimal from = order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price;
        decimal to = dom.DragPrice;
        if (to > 0 && to != from)
        {
            if (OrderMoved != null) OrderMoved(order, to);
            else if (dom.Broker is { } broker) OrderActions.Reprice(broker, order, to);
        }
        return true;
    }

    /// <summary>Abandons a drag without moving the order.</summary>
    public void CancelDrag(DomModel? dom)
    {
        IsDragging = false;
        if (dom != null) dom.DragOrder = null;
    }

    /// <summary>Whether the pointer should show the reprice cursor.</summary>
    public bool ShowsMoveCursor(DomModel dom, float x, float y) => IsDragging || (TradingEnabled && _renderer.OrderAt(dom, x, y) != null);
}
