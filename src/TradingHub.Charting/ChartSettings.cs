using System.ComponentModel.DataAnnotations;

namespace TradingHub.Charting;

public enum ChartStyle
{
    [Display(Name = "Candles")] Candles,
    [Display(Name = "OHLC bars")] Bars,
    [Display(Name = "Line")] Line,
    [Display(Name = "Footprint (clusters)")] Footprint,
    [Display(Name = "Heikin Ashi")] HeikinAshi,
}

/// <summary>Cómo se colorean las celdas.</summary>
public enum ClusterColorScheme
{
    [Display(Name = "Proportional to volume")] VolumeProportion,
    [Display(Name = "By delta")] Delta,
    [Display(Name = "Bid / Ask")] BidAsk,
    [Display(Name = "Heatmap")] Heatmap,
    [Display(Name = "Solid")] Solid,
    [Display(Name = "No background")] None,
}

/// <summary>Configuración visual de un gráfico.</summary>
/// <summary>Trazo de la línea entre la entrada y la salida de una operación.</summary>
public enum TradeLineStyle
{
    [Display(Name = "Rayas")] Dashed,
    [Display(Name = "Continuous")] Solid,
    [Display(Name = "Points")] Dotted,
}

public sealed class ChartSettings
{
    public ChartStyle Style { get; set; } = ChartStyle.Footprint;

    /// <summary>Cómo se arma cada vela del footprint (columnas elegidas por el usuario).</summary>
    public FootprintLayout Layout { get; set; } = FootprintLayout.Presets[0].Clone();

    /// <summary>Sombrear el área de valor (70%) de cada vela.</summary>
    public bool ShowValueArea { get; set; }

    public bool ShowGrid { get; set; } = true;
    public bool ShowSessionLines { get; set; } = true;
    public bool ShowInfoPanel { get; set; } = true;

    /// <summary>Nombre del instrumento arriba a la izquierda del gráfico (ya está en la barra; por defecto no).</summary>
    public bool ShowLegendTitle { get; set; }

    /// <summary>Valores de la vela (Ap, Máx, Mín, Cie, Vol, Δ) en la leyenda.</summary>
    public bool ShowLegendValues { get; set; } = true;

    /// <summary>Nombres de los indicadores en el gráfico (la flechita de la leyenda los pliega, como en TradingView).</summary>
    public bool ShowIndicatorNames { get; set; } = true;
    public bool ShowLastPriceLine { get; set; } = true;

    /// <summary>Marcar las operaciones propias (ejecuciones y operaciones cerradas).</summary>
    public bool ShowTradeMarks { get; set; } = true;

    /// <summary>Grosor de la línea de la entrada a la salida de cada operación (las propias y las de las estrategias).</summary>
    public double TradeLineWidth { get; set; } = 1.5;

    public TradeLineStyle TradeLineStyle { get; set; } = TradeLineStyle.Dashed;

    /// <summary>Ticks por fila del footprint. 0 = automático.</summary>
    public int PriceStep { get; set; }

    /// <summary>Huso horario para mostrar las horas (null = el de la computadora).</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Colores propios de este gráfico (fondo, velas, ejes…), por nombre del color del tema. Null = los de siempre.</summary>
    public Dictionary<string, uint>? Colors { get; set; }

    public TimeZoneInfo TimeZone
    {
        get
        {
            if (string.IsNullOrEmpty(TimeZoneId)) return TimeZoneInfo.Local;
            try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
            catch { return TimeZoneInfo.Local; }
        }
    }

    public ChartSettings Clone()
    {
        var c = (ChartSettings)MemberwiseClone();
        c.Layout = Layout.Clone();
        c.Colors = Colors == null ? null : new Dictionary<string, uint>(Colors);
        return c;
    }
}
