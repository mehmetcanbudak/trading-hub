using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingHub.Attributes;

namespace TradingHub.Charting;

/// <summary>Tipo de pieza (columna) dentro de cada vela del footprint.</summary>
public enum FootprintColumnKind
{
    [Display(Name = "Thin candle")] Candle,
    [Display(Name = "Colored cells")] Cells,
    [Display(Name = "Ladder (numbers)")] Ladder,
    [Display(Name = "Profile (histogram)")] Profile,
    [Display(Name = "Spacer")] Spacer,
}

/// <summary>Dato que muestra una columna.</summary>
public enum FootprintValue
{
    [Display(Name = "Volume")] Volume,
    [Display(Name = "Delta")] Delta,
    [Display(Name = "Bid (aggressive sells)")] Bid,
    [Display(Name = "Ask (aggressive buys)")] Ask,
    [Display(Name = "Bid x Ask")] BidAsk,
    [Display(Name = "Trades")] Trades,
}

/// <summary>Hacia dónde crecen las barras de un perfil.</summary>
public enum ProfileDirection
{
    [Display(Name = "Left to right")] LeftToRight,
    [Display(Name = "Right to left")] RightToLeft,
    [Display(Name = "From center (delta / bid-ask)")] Centered,
}

/// <summary>Qué niveles se resaltan.</summary>
public enum HighlightRule
{
    [Display(Name = "None")] None,
    [Display(Name = "Strong level delta")] StrongDelta,
    [Display(Name = "Diagonal imbalances")] Imbalance,
    [Display(Name = "Bar POC")] Poc,
    [Display(Name = "High volume (above % of POC)")] HighVolume,
}

public enum ColumnTextAlign
{
    [Display(Name = "Left")] Left,
    [Display(Name = "Center")] Center,
    [Display(Name = "Right")] Right,
}

/// <summary>Una columna (pieza) del footprint.</summary>
public sealed class FootprintColumn
{
    [Display(Name = "Type")]
    public FootprintColumnKind Kind { get; set; } = FootprintColumnKind.Cells;

    [Display(Name = "Value")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public FootprintValue Value { get; set; } = FootprintValue.BidAsk;

    /// <summary>Ancho relativo (las velas finitas tienen ancho fijo en píxeles).</summary>
    [Display(Name = "Width", Description = "Relative to other columns in the bar")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile, FootprintColumnKind.Spacer)]
    public double Width { get; set; } = 1;

    [Display(Name = "Bar direction")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Profile)]
    public ProfileDirection Direction { get; set; } = ProfileDirection.LeftToRight;

    [Display(Name = "Highlight")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public HighlightRule Highlight { get; set; } = HighlightRule.None;

    [Display(Name = "Cell colors")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells)]
    public ClusterColorScheme ColorScheme { get; set; } = ClusterColorScheme.VolumeProportion;

    [Display(Name = "Text alignment")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Ladder)]
    public ColumnTextAlign Align { get; set; } = ColumnTextAlign.Right;

    /// <summary>En los perfiles, escribir el valor sobre la barra.</summary>
    [Display(Name = "Numbers over bars")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Profile)]
    public bool ShowNumbers { get; set; }

    /// <summary>Colores propios (ARGB). Null = los del tema.</summary>
    [Display(Name = "Positive color", GroupName = "Background colors", Description = "Ask, positive delta and positive highlight background/bar. Automatic = theme color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? PositiveColor { get; set; }

    [Display(Name = "Negative color", GroupName = "Background colors", Description = "Bid, negative delta and negative highlight background/bar. Automatic = theme color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? NegativeColor { get; set; }

    [Display(Name = "Neutral color", GroupName = "Background colors", Description = "Volume and nondirectional highlight background/bar. Automatic = theme color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? NeutralColor { get; set; }

    /// <summary>Color de los números (null = el del tema). Los de abajo, si están, mandan sobre este.</summary>
    [Display(Name = "Text color", GroupName = "Text colors", Description = "All column numbers except those with their own color (delta, bid, ask). Automatic = theme color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? TextColor { get; set; }

    [Display(Name = "Positive delta text", GroupName = "Text colors")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Delta)]
    public uint? PositiveTextColor { get; set; }

    [Display(Name = "Negative delta text", GroupName = "Text colors")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Delta)]
    public uint? NegativeTextColor { get; set; }

    [Display(Name = "Bid text", GroupName = "Text colors", Description = "Highlighted imbalances retain their color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Bid, FootprintValue.BidAsk)]
    public uint? BidTextColor { get; set; }

    [Display(Name = "Ask text", GroupName = "Text colors", Description = "Highlighted imbalances retain their color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Ask, FootprintValue.BidAsk)]
    public uint? AskTextColor { get; set; }

    [Display(Name = "Highlighted level text", GroupName = "Text colors", Description = "Automatic = white.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Ladder)]
    [VisibleWhen(nameof(Highlight), HighlightRule.StrongDelta, HighlightRule.Imbalance, HighlightRule.Poc, HighlightRule.HighVolume)]
    public uint? HighlightTextColor { get; set; }

    public FootprintColumn Clone() => (FootprintColumn)MemberwiseClone();

    public override string ToString() => Kind switch
    {
        FootprintColumnKind.Candle => "Bar",
        FootprintColumnKind.Spacer => "Spacer",
        _ => $"{Kind} · {Value}",
    };
}

/// <summary>
/// Cómo se arma cada vela del footprint: una lista de columnas de izquierda a derecha.
/// Se guarda como plantilla JSON.
/// </summary>
public sealed class FootprintLayout
{
    public string Name { get; set; } = "Custom";
    [JsonIgnore] public string DisplayName => TradingHub.Common.EnglishText.Of(Name);
    public List<FootprintColumn> Columns { get; set; } = new();

    /// <summary>Delta de la vela arriba y volumen total abajo.</summary>
    [Display(Name = "Bar totals", Description = "Bar delta above and total volume below")]
    public bool ShowTotals { get; set; }

    /// <summary>Recuadro en el POC de cada vela (en las columnas de celdas).</summary>
    [Display(Name = "Outline POC", Description = "In cell columns")]
    public bool ShowPoc { get; set; } = true;

    /// <summary>Resaltar desequilibrios en el texto de bid x ask.</summary>
    [Display(Name = "Highlight imbalances", Description = "In bid x ask text (diagonal)")]
    public bool ShowImbalances { get; set; } = true;

    [Display(Name = "Imbalance ratio", Description = "How many times larger one side must be than the other (diagonally)")]
    public decimal ImbalanceRatio { get; set; } = 3m;

    [Display(Name = "Imbalance minimum volume")]
    public decimal ImbalanceMinVolume { get; set; }

    /// <summary>Para "delta fuerte": |delta| / volumen del nivel (0.45 ≈ 72/28).</summary>
    [Display(Name = "Strong delta threshold", Description = "|delta| / level volume (0.45 ≈ 72% on one side)")]
    public decimal StrongDeltaRatio { get; set; } = 0.45m;

    /// <summary>Volumen mínimo del nivel (fracción del POC) para resaltar.</summary>
    [Display(Name = "Minimum highlight volume", Description = "Fraction of POC volume (0.3 = 30%)")]
    public decimal HighlightMinVolumeFraction { get; set; } = 0.3m;

    /// <summary>Colores proporcionales al máximo de la vela (true) o de todo lo visible.</summary>
    [Display(Name = "Colors proportional to each bar", Description = "Off: use the maximum over the entire visible range")]
    public bool ProportionPerCandle { get; set; } = true;

    public FootprintLayout Clone()
    {
        var c = (FootprintLayout)MemberwiseClone();
        c.Columns = Columns.Select(x => x.Clone()).ToList();
        return c;
    }

    // ------------------------------------------------------------------ plantillas

    private static FootprintColumn Candle() => new() { Kind = FootprintColumnKind.Candle };

    public static IReadOnlyList<FootprintLayout> Presets { get; } = new[]
    {
        new FootprintLayout
        {
            Name = "Bid x Ask (clásico)",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.BidAsk, ColorScheme = ClusterColorScheme.VolumeProportion } },
        },
        new FootprintLayout
        {
            Name = "Escalera de volumen + perfil",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Volume, Highlight = HighlightRule.StrongDelta, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, Highlight = HighlightRule.StrongDelta, Direction = ProfileDirection.LeftToRight, Width = 1.3 },
            },
        },
        new FootprintLayout
        {
            Name = "Escalera de delta + perfil de volumen",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Delta, Highlight = HighlightRule.StrongDelta, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, Direction = ProfileDirection.LeftToRight, Width = 1.3 },
            },
        },
        new FootprintLayout
        {
            Name = "Doble escalera (bid | ask)",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Bid, Highlight = HighlightRule.Imbalance, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Ask, Highlight = HighlightRule.Imbalance, Align = ColumnTextAlign.Left, Width = 1 },
            },
        },
        new FootprintLayout
        {
            Name = "Doble escalera de delta",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Delta, Highlight = HighlightRule.StrongDelta, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Volume, Highlight = HighlightRule.HighVolume, Align = ColumnTextAlign.Left, Width = 1 },
            },
        },
        new FootprintLayout
        {
            Name = "Volumen (celdas)",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.Volume } },
        },
        new FootprintLayout
        {
            Name = "Delta (celdas)",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.Delta, ColorScheme = ClusterColorScheme.Delta } },
        },
        new FootprintLayout
        {
            Name = "Histograma bid / ask",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.BidAsk, Direction = ProfileDirection.Centered, ShowNumbers = true } },
        },
        new FootprintLayout
        {
            Name = "Perfil de volumen por vela",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, ShowNumbers = true } },
        },
        new FootprintLayout
        {
            Name = "Bid x Ask + perfil",
            Columns =
            {
                Candle(),
                new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.BidAsk, Width = 1.4 },
                new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, Width = 0.8 },
            },
        },
    };

    // ------------------------------------------------------------------ persistencia

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static FootprintLayout? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<FootprintLayout>(json, Json); }
        catch { return null; }
    }

    /// <summary>Carpeta de plantillas del usuario (con TRADINGHUB_DATA_DIR, la de las pruebas).</summary>
    public static string TemplatesFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } dir ? dir : TradingHub.Common.Product.AppData, "FootprintTemplates");

    public static List<FootprintLayout> LoadUserTemplates()
    {
        var list = new List<FootprintLayout>();
        if (!Directory.Exists(TemplatesFolder)) return list;
        foreach (var f in Directory.EnumerateFiles(TemplatesFolder, "*.json"))
        {
            var l = FromJson(File.ReadAllText(f));
            if (l != null) list.Add(l);
        }

        return list;
    }

    public void SaveAsTemplate()
    {
        Directory.CreateDirectory(TemplatesFolder);
        var safe = string.Concat(Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        File.WriteAllText(Path.Combine(TemplatesFolder, safe + ".json"), ToJson());
    }
}
