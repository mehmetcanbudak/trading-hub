namespace TradingHub.App.Services;

public sealed partial class AppSettings
{
    /// <summary>
    /// The DOM trading switch, separate from chart trading. On (default, unchanged behaviour): DOM clicks, drags and the
    /// DOM order buttons trade through the normal confirmed order path. Off: the DOM is read-only.
    /// </summary>
    public bool DomTrading { get; set; } = true;

    /// <summary>Trade tape: only prints of at least this notional (quantity × price × point value; 0 = all).</summary>
    public decimal TapeMinNotional { get; set; }
}
