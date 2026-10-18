using System.ComponentModel.DataAnnotations;

namespace TradingHub.Indicators
{
    /// <summary>Marca una propiedad como parámetro de cálculo.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class ParameterAttribute : Attribute
    {
    }

    /// <summary>Cómo se dibuja una serie de valores.</summary>
    public enum VisualMode
    {
        [Display(Name = "Line")] Line,
        [Display(Name = "Histogram")] Histogram,
        [Display(Name = "Hash")] Hash,
        [Display(Name = "Cross")] Cross,
        [Display(Name = "Points")] Dots,
        [Display(Name = "Block")] Block,
        [Display(Name = "Square")] Square,
        [Display(Name = "Up arrow")] UpArrow,
        [Display(Name = "Down arrow")] DownArrow,
        [Display(Name = "Axis value only")] OnlyValueOnAxis,
        [Display(Name = "Hidden")] Hide,
        [Display(Name = "Bands")] Bands,
        [Display(Name = "Dots (line)")] Points,
        [Display(Name = "Levels")] Levels,
        [Display(Name = "Step line")] Steps,
    }

    public enum LineDashStyle
    {
        [Display(Name = "Solid")] Solid,
        [Display(Name = "Dashed")] Dash,
        [Display(Name = "Points")] Dot,
        [Display(Name = "Dash-dot")] DashDot,
        [Display(Name = "Dash-dot-dot")] DashDotDot,
    }

    /// <summary>Valor de la vela que recibe OnCalculate como "value".</summary>
    public enum DataSeriesType
    {
        Open,
        High,
        Low,
        Close,
        HL2,
        HLC3,
        OHLC4,
        Volume,
        Delta,
        Ticks,
        Bid,
        Ask,
    }

    /// <summary>Lado de una operación (como lo reportan los indicadores).</summary>
    public enum TradeDirection
    {
        Between = 0,
        Buy = 1,
        Sell = 2,
    }

    public enum MarketDataType
    {
        Trade = 0,
        Bid = 1,
        Ask = 2,
    }

    public enum ChartVisualModes
    {
        Candles,
        Clusters,
        Line,
        Bars,
        Mountain,
        Heatmap,
    }

    public enum CumulativeTradeStates
    {
        Active,
        Finished,
    }
}
