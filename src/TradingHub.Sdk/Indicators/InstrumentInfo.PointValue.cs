namespace TradingHub.Indicators
{
    public partial class InstrumentInfo
    {
        /// <summary>
        /// Quote-currency value of one price point per unit of volume (the contract multiplier; 1 for spot and linear crypto).
        /// Notional of a print is price × size × <see cref="PointValue"/>.
        /// </summary>
        public decimal PointValue { get; set; } = 1m;
    }
}
