using System.Drawing;
using TradingHub.Common;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators
{
    /// <summary>Un panel del gráfico (el de precio o uno de indicador) con sus conversiones de coordenadas.</summary>
    public interface IChartContainer
    {
        /// <summary>Rectángulo del panel en píxeles del gráfico.</summary>
        Rectangle Region { get; }

        /// <summary>Rectángulo relativo al propio panel (origen 0,0).</summary>
        Rectangle RelativeRegion { get; }

        int BarsWidth { get; }
        int BarSpacing { get; }

        /// <summary>Paso de precio por fila (tick efectivo).</summary>
        decimal Step { get; }

        /// <summary>Alto en píxeles de una fila de precio.</summary>
        decimal PriceRowHeight { get; }

        int TotalBars { get; }

        /// <summary>Precio en el borde superior / inferior visible (panel de precio).</summary>
        decimal High { get; }
        decimal Low { get; }

        /// <summary>Rango de valores visible (paneles de indicador).</summary>
        decimal Minimum { get; }
        decimal Maximum { get; }

        int FirstVisibleBarNumber { get; }
        int LastVisibleBarNumber { get; }

        int GetXByBar(int bar, bool isStartOfBar = true);
        int GetYByPrice(decimal price, bool isStartOfPrice = true);
        decimal GetPriceByY(int y);
        int GetBarByX(int x);
        int GetYByValue(decimal value);
        decimal GetValueByY(int y);
    }

    /// <summary>Colores del tema del gráfico.</summary>
    public class ColorsStore
    {
        public Color UpCandleColor { get; set; } = Color.FromArgb(255, 73, 209, 73);
        public Color DownCandleColor { get; set; } = Color.FromArgb(255, 232, 69, 72);
        public UniversalPen BarBorderPen { get; set; } = new(Color.FromArgb(255, 130, 130, 140), 1);
        public UniversalPen DojiBarPen { get; set; } = new(Color.FromArgb(255, 160, 160, 170), 1);
        public Color BaseBackgroundColor { get; set; } = Color.FromArgb(255, 8, 12, 22);
        public Color AxisTextColor { get; set; } = Color.FromArgb(255, 144, 197, 212);
        public Color FootprintAskColor { get; set; } = Color.FromArgb(255, 73, 209, 73);
        public Color FootprintBidColor { get; set; } = Color.FromArgb(255, 232, 69, 72);
        public Color FootprintTextColor { get; set; } = Color.FromArgb(255, 220, 225, 235);
        public Color FootprintMaximumVolumeTextColor { get; set; } = Color.FromArgb(255, 255, 255, 255);
        public Color MouseTextColor { get; set; } = Color.FromArgb(255, 240, 240, 240);
        public Color MouseBackground { get; set; } = Color.FromArgb(255, 31, 73, 125);
        public Color DrawingObjectColor { get; set; } = Color.FromArgb(255, 30, 144, 255);
        public UniversalPen Grid { get; set; } = new(Color.FromArgb(28, 200, 210, 230), 1);
        public UniversalPen NewSessionPen { get; set; } = new(Color.FromArgb(255, 110, 110, 120), 1);
        public UniversalPen PaneSeparators { get; set; } = new(Color.FromArgb(255, 67, 70, 81), 1);
    }

    public class MouseLocationInfo
    {
        public Point LastPosition { get; set; }
        public int BarBelowMouse { get; set; } = -1;
        public decimal PriceBelowMouse { get; set; }
        public bool IsMouseLeave { get; set; } = true;
    }

    public class PressedKeyInfo
    {
        public Avalonia.Input.Key Key { get; set; }
        public Avalonia.Input.KeyModifiers Modifiers { get; set; }
    }

    public class KeyboardInfo
    {
        public PressedKeyInfo PressedKey { get; set; }
        public Avalonia.Input.KeyModifiers Modifiers { get; set; }
    }

    public class TradingSessionDescription
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
    }

    /// <summary>Información del gráfico en el que corre un indicador.</summary>
    public class ChartInfo
    {
        private readonly Func<IChartContainer> _price;

        public ChartInfo(Func<IChartContainer> priceContainer)
        {
            _price = priceContainer;
        }

        public IChartContainer PriceChartContainer => _price();
        public IChartContainer ChartContainer => _price();

        /// <summary>Rectángulo de todo el gráfico.</summary>
        public Rectangle Region { get; set; }

        /// <summary>Código del período: M5, H1, Daily, 1000 (ticks)...</summary>
        public string TimeFrame { get; set; } = "M1";

        /// <summary>Duración de una vela si el gráfico es por tiempo (cero si no).</summary>
        public TimeSpan GetTimeFrameTypeChartPeriod() =>
            TradingHub.Data.ChartPeriod.TryParse(TimeFrame, out var p) && p.IsTimeBased ? p.ApproxDuration : TimeSpan.Zero;

        /// <summary>Tipo de gráfico: "TimeFrame", "Seconds", "Tick", "Volume", "Range", "Delta", "Renko".</summary>
        public string ChartType { get; set; } = "TimeFrame";

        public ChartVisualModes ChartVisualMode { get; set; } = ChartVisualModes.Candles;

        public ColorsStore ColorsStore { get; set; } = new();

        public RenderFont PriceAxisFont { get; set; } = new("Arial", 9);

        /// <summary>Formato de precios para string.Format (ej. "{0:0.00}").</summary>
        public string StringFormat { get; set; } = "{0:0.00}";

        public float DpiX { get; set; } = 96f;
        public float DpiY { get; set; } = 96f;

        public MouseLocationInfo MouseLocationInfo { get; } = new();
        public KeyboardInfo KeyboardInfo { get; } = new();

        public IEnumerable<TradingSessionDescription> TradingSessionDescriptions { get; set; } = Array.Empty<TradingSessionDescription>();

        /// <summary>Decimales de volumen a mostrar (cripto usa fracciones).</summary>
        public int VolumeDecimals { get; set; }

        public int GetXByBar(int bar, bool isStartOfBar = true) => PriceChartContainer.GetXByBar(bar, isStartOfBar);
        public int GetYByPrice(decimal price, bool isStartOfPrice = true) => PriceChartContainer.GetYByPrice(price, isStartOfPrice);
        public decimal GetPriceByY(int y) => PriceChartContainer.GetPriceByY(y);

        public string GetPriceString(decimal price) => string.Format(System.Globalization.CultureInfo.InvariantCulture, StringFormat, price);

        /// <summary>Formatea un volumen de forma compacta (1.2K, 3.4M) si es grande.</summary>
        public string TryGetMinimizedVolumeString(decimal volume) => VolumeFormat.Compact(volume, VolumeDecimals);

        public string TryGetMinimizedVolumeString(decimal volume, decimal price) => VolumeFormat.Compact(volume, VolumeDecimals);
    }

    /// <summary>Formato compacto de volúmenes (usado por el footprint y los indicadores).</summary>
    public static class VolumeFormat
    {
        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        public static string Compact(decimal v, int decimals = 0)
        {
            var a = Math.Abs(v);
            string sign = v < 0 ? "-" : "";
            if (a >= 1_000_000_000m) return sign + (a / 1_000_000_000m).ToString(a >= 10_000_000_000m ? "0" : "0.#", Inv) + "B";
            if (a >= 1_000_000m) return sign + (a / 1_000_000m).ToString(a >= 10_000_000m ? "0" : "0.#", Inv) + "M";
            if (a >= 10_000m) return sign + (a / 1_000m).ToString(a >= 100_000m ? "0" : "0.#", Inv) + "K";
            if (decimals <= 0 || a >= 100m) return sign + Math.Round(a, 0).ToString("0", Inv);
            if (a >= 10m) return sign + a.ToString("0.#", Inv);
            if (a >= 1m) return sign + a.ToString(decimals >= 2 ? "0.##" : "0.#", Inv);
            return sign + a.ToString("0." + new string('#', Math.Min(decimals, 4)), Inv);
        }
    }

    /// <summary>Constantes de paneles.</summary>
    public static class IndicatorDataProvider
    {
        public const string CandlesPanel = "Chart";
        public const string NewPanel = "NewPanel";
    }

    /// <summary>Categorías de indicadores (para el listado de la UI).</summary>
    public static class IndicatorCategories
    {
        public const string VolumeOrderFlow = "Volume / Order flow";
        public const string OrderBook = "Order book";
        public const string Trading = "Trading";
        public const string Samples = "Examples";
        public const string Trend = "Trend";
        public const string Oscillators = "Oscillators";
        public const string Levels = "Levels";
        public const string Other = "Other";
        public const string Custom = "Custom";
    }

    /// <summary>Colores por defecto (paleta estándar).</summary>
    public static class DefaultColors
    {
        public static Color Aqua => Color.Aqua;
        public static Color Black => Color.Black;
        public static Color Blue => Color.FromArgb(255, 30, 144, 255);
        public static Color DarkRed => Color.DarkRed;
        public static Color Fuchsia => Color.Fuchsia;
        public static Color Gray => Color.Gray;
        public static Color Green => Color.FromArgb(255, 73, 209, 73);
        public static Color Lime => Color.Lime;
        public static Color Maroon => Color.Maroon;
        public static Color Navy => Color.Navy;
        public static Color Olive => Color.Olive;
        public static Color Orange => Color.Orange;
        public static Color Purple => Color.MediumPurple;
        public static Color Red => Color.FromArgb(255, 232, 69, 72);
        public static Color Silver => Color.Silver;
        public static Color Teal => Color.Teal;
        public static Color White => Color.White;
        public static Color Yellow => Color.Yellow;
    }
}
