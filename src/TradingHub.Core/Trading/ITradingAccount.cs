using TradingHub.Market;

namespace TradingHub.Trading;

/// <summary>
/// Una cuenta donde se puede operar un instrumento: la simulada (paper) o una real (Binance...).
/// El DOM, el gráfico y el ticket operan contra esta interfaz sin saber cuál es.
/// </summary>
public interface ITradingAccount
{
    string Name { get; }

    /// <summary>True si las órdenes van al mercado con dinero real.</summary>
    bool IsReal { get; }

    Instrument Instrument { get; }
    Position Position { get; }
    IEnumerable<Order> ActiveOrders { get; }

    /// <summary>Último error del broker (orden rechazada, etc.).</summary>
    string? LastError { get; }

    event Action? Changed;

    Order Market(OrderDirections dir, decimal qty, string comment = "");
    Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "");
    Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "");
    void Cancel(Order order);
    void CancelAt(decimal price);
    void CancelAll();
    void Modify(Order order, decimal newPrice);
    void Flatten();
    void Reverse();

    /// <summary>Último precio operado del instrumento (0 si todavía no hay datos).</summary>
    decimal LastPrice => 0;

    /// <summary>Orden de salida (stop loss / objetivo): solo puede reducir la posición, nunca darla vuelta.</summary>
    Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "")
    {
        var o = type == OrderTypes.Stop ? Stop(dir, qty, price, comment) : Limit(dir, qty, price, comment);
        o.ReduceOnly = true;
        return o;
    }

    /// <summary>
    /// Cambia precio y cantidad de una orden. Devuelve la orden vigente: la misma, o una nueva si el
    /// broker no permite modificar (cancela y vuelve a enviar).
    /// </summary>
    Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        if (newQuantity == order.Unfilled)
        {
            Modify(order, newPrice);
            return order;
        }

        Cancel(order);
        var n = order.Type == OrderTypes.Stop ? Stop(order.Direction, newQuantity, newPrice, order.Comment) : Limit(order.Direction, newQuantity, newPrice, order.Comment);
        n.ReduceOnly = order.ReduceOnly;
        return n;
    }
}

/// <summary>
/// Real-order permission, decided when an order is sent (never only when an account is chosen): on a real account an
/// order that opens, adds to or reverses a position needs its connection enabled with real orders allowed. Reductions
/// and cancels are never blocked, and neither are simulated, testnet, practice or demo accounts.
/// </summary>
public static class RealOrderPermission
{
    /// <summary>
    /// True if an order of <paramref name="signedQuantity"/> (positive buys) opens, adds to or reverses
    /// <paramref name="position"/>; a partial or full reduction toward flat does not.
    /// </summary>
    public static bool IncreasesExposure(decimal position, decimal signedQuantity)
    {
        decimal after = position + signedQuantity;
        bool reduces = Math.Abs(after) < Math.Abs(position) && (after == 0 || Math.Sign(after) == Math.Sign(position));
        return signedQuantity != 0 && !reduces;
    }

    /// <summary>
    /// null if the order may be sent, otherwise why it is blocked. <paramref name="connection"/> is the account's
    /// connection as configured now (null: it no longer exists); <paramref name="signedQuantity"/> is what the order adds
    /// (positive buys, 0 for a cancel) to <paramref name="position"/>.
    /// </summary>
    public static string? Check(bool isReal, (string Name, bool Enabled, bool AllowRealOrders)? connection, decimal position, decimal signedQuantity)
    {
        if (!isReal || !IncreasesExposure(position, signedQuantity)) return null;
        const string Rest = ": new orders are disabled; cancel/close allowed";
        if (connection is not { } c) return "its connection no longer exists" + Rest;
        if (!c.Enabled) return $"connection {c.Name} is disabled" + Rest;
        return c.AllowRealOrders ? null : $"real orders are not allowed for {c.Name}" + Rest;
    }
}
