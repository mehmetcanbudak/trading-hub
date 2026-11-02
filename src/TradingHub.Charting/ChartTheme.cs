using SkiaSharp;

namespace TradingHub.Charting;

/// <summary>
/// Colores del gráfico. Los valores por defecto salen de la plantilla "Dark footprint"
/// que usabas en ATAS (fondo azul noche, bid rojo 232,69,72, ask verde 73,209,73...).
/// </summary>
public sealed class ChartTheme
{
    public SKColor BackgroundTop { get; set; } = new(0, 0, 0);
    public SKColor BackgroundBottom { get; set; } = new(10, 10, 10);
    public SKColor PanelSeparator { get; set; } = new(48, 48, 48);
    public SKColor Grid { get; set; } = new(255, 255, 255, 14);
    public SKColor AxisBackground { get; set; } = new(0, 0, 0);
    public SKColor AxisText { get; set; } = new(165, 165, 165);
    public SKColor AxisLine { get; set; } = new(48, 48, 48);
    public SKColor Text { get; set; } = new(220, 220, 220);
    public SKColor TextDim { get; set; } = new(130, 130, 130);

    public SKColor UpCandle { get; set; } = new(73, 209, 73);
    public SKColor DownCandle { get; set; } = new(232, 69, 72);
    public SKColor UpCandleBorder { get; set; } = new(73, 209, 73);
    public SKColor DownCandleBorder { get; set; } = new(232, 69, 72);
    public SKColor Wick { get; set; } = new(150, 150, 150);

    public SKColor Bid { get; set; } = new(232, 69, 72);
    public SKColor Ask { get; set; } = new(73, 209, 73);
    public SKColor ClusterBase { get; set; } = new(45, 90, 140);
    public SKColor ClusterText { get; set; } = new(220, 225, 235);
    public SKColor ClusterTextStrong { get; set; } = new(255, 255, 255);
    public SKColor CellBorder { get; set; } = new(64, 61, 61, 150);
    public SKColor Poc { get; set; } = new(200, 216, 255);
    public SKColor ValueArea { get; set; } = new(200, 216, 255, 28);
    public SKColor ImbalanceAsk { get; set; } = new(0, 255, 140);
    public SKColor ImbalanceBid { get; set; } = new(255, 70, 90);

    // modo combinado
    public SKColor LadderPositive { get; set; } = new(18, 92, 150);
    public SKColor LadderNegative { get; set; } = new(150, 38, 38);
    public SKColor LadderText { get; set; } = new(150, 158, 172);
    public SKColor ProfileBar { get; set; } = new(105, 112, 125);
    public SKColor ProfileBarStrong { get; set; } = new(170, 178, 192);
    public SKColor DeltaPositiveText { get; set; } = new(60, 150, 230);
    public SKColor DeltaNegativeText { get; set; } = new(230, 70, 70);

    public SKColor Crosshair { get; set; } = new(128, 128, 128, 175);
    public SKColor CrosshairLabelBack { get; set; } = new(50, 50, 50);
    public SKColor CrosshairLabelText { get; set; } = new(255, 255, 255);
    public SKColor LastPriceUp { get; set; } = new(73, 209, 73);
    public SKColor LastPriceDown { get; set; } = new(232, 69, 72);
    public SKColor LastPriceLine { get; set; } = new(255, 255, 255, 63);
    public SKColor NewSession { get; set; } = new(128, 128, 128, 110);

    public SKColor BuyOrder { get; set; } = new(135, 206, 235);
    public SKColor SellOrder { get; set; } = new(255, 160, 162);
    public SKColor PositionLong { get; set; } = new(73, 209, 73);
    public SKColor PositionShort { get; set; } = new(232, 69, 72);

    /// <summary>Línea de la entrada a la salida de una operación que ganó / perdió.</summary>
    public SKColor TradeWin { get; set; } = new(73, 209, 73, 215);

    public SKColor TradeLoss { get; set; } = new(232, 69, 72, 215);

    public SKColor Selection { get; set; } = new(30, 83, 228);
    public SKColor DrawingDefault { get; set; } = new(30, 144, 255);

    // Helvetica Neue y Menlo en la Mac; Segoe UI y Consolas en Windows (las de la Mac no vienen ahí)
    public string FontFamily { get; set; } = TradingHub.Common.PlatformFonts.SansSkia;
    public string MonoFontFamily { get; set; } = TradingHub.Common.PlatformFonts.MonoSkia;

    public static ChartTheme Dark { get; } = new();

    /// <summary>Modo claro: fondo blanco, textos oscuros, velas y footprint con colores que se leen sobre blanco.</summary>
    public static ChartTheme Light { get; } = new()
    {
        BackgroundTop = new(255, 255, 255),
        BackgroundBottom = new(255, 255, 255),
        PanelSeparator = new(214, 214, 218),
        Grid = new(0, 0, 0, 14),
        AxisBackground = new(255, 255, 255),
        AxisText = new(85, 85, 90),
        AxisLine = new(214, 214, 218),
        Text = new(25, 25, 28),
        TextDim = new(110, 110, 116),
        UpCandle = new(22, 160, 100),
        DownCandle = new(220, 58, 66),
        UpCandleBorder = new(22, 160, 100),
        DownCandleBorder = new(220, 58, 66),
        Wick = new(95, 95, 100),
        Bid = new(220, 58, 66),
        Ask = new(22, 160, 100),
        ClusterBase = new(60, 110, 170),
        ClusterText = new(30, 30, 34),
        ClusterTextStrong = new(0, 0, 0),
        CellBorder = new(200, 200, 205, 160),
        Poc = new(40, 70, 140),
        ValueArea = new(40, 70, 140, 26),
        ImbalanceAsk = new(0, 150, 85),
        ImbalanceBid = new(210, 40, 60),
        LadderPositive = new(120, 170, 225),
        LadderNegative = new(235, 140, 140),
        LadderText = new(70, 70, 76),
        ProfileBar = new(170, 172, 178),
        ProfileBarStrong = new(110, 112, 120),
        DeltaPositiveText = new(25, 110, 200),
        DeltaNegativeText = new(200, 45, 45),
        Crosshair = new(90, 90, 96, 175),
        CrosshairLabelBack = new(40, 40, 44),
        CrosshairLabelText = new(255, 255, 255),
        LastPriceUp = new(22, 160, 100),
        LastPriceDown = new(220, 58, 66),
        LastPriceLine = new(0, 0, 0, 60),
        NewSession = new(120, 120, 126, 110),
        BuyOrder = new(30, 120, 200),
        SellOrder = new(205, 60, 75),
        PositionLong = new(22, 160, 100),
        PositionShort = new(220, 58, 66),
        TradeWin = new(22, 160, 100),
        TradeLoss = new(220, 58, 66),
    };

    /// <summary>Base de todos los gráficos (oscura o clara según el modo de la app); los colores propios van encima.</summary>
    public static ChartTheme Base { get; set; } = Dark;

    /// <summary>Colores que se pueden cambiar en cada gráfico (por el nombre de la propiedad).</summary>
    public static IReadOnlyList<System.Reflection.PropertyInfo> ColorProperties { get; } =
        typeof(ChartTheme).GetProperties().Where(p => p.PropertyType == typeof(SKColor) && p.CanWrite).ToList();

    /// <summary>El tema de siempre con los colores propios de un gráfico encima.</summary>
    public static ChartTheme With(IReadOnlyDictionary<string, uint>? colors)
    {
        if (colors == null || colors.Count == 0) return Base;
        var t = (ChartTheme)Base.MemberwiseClone();
        foreach (var p in ColorProperties)
            if (colors.TryGetValue(p.Name, out var argb)) p.SetValue(t, new SKColor(argb));
        return t;
    }

    /// <summary>Color por defecto de una propiedad (ARGB).</summary>
    public static uint DefaultOf(string property) =>
        ColorProperties.FirstOrDefault(p => p.Name == property)?.GetValue(Base) is SKColor c ? (uint)c : 0;
}
