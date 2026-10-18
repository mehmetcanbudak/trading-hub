// Tipos auxiliares de compatibilidad para compilar los indicadores públicos de ATAS sobre Trading Hub.
// Implementaciones propias y mínimas: solo lo que esos indicadores necesitan.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Runtime.CompilerServices;
using TradingHub.Rendering.Context;

namespace TradingHub.Indicators
{
    /// <summary>Filtro decimal (versión no genérica).</summary>
    public class Filter : Filter<decimal>
    {
        public Filter() { }
        public Filter(bool isEnabled) : base(isEnabled) { }
        public Filter(bool isEnabled, decimal value) : base(isEnabled, value) { }
        public Filter(bool isEnabledEditable, bool isEnabled) : base(isEnabledEditable, isEnabled, 0) { }
    }

    /// <summary>Filtro de un valor de enumeración.</summary>
    public class FilterEnum<TEnum> : Filter<TEnum> where TEnum : struct, Enum
    {
        public FilterEnum() { }
        public FilterEnum(bool isEnabled) : base(isEnabled) { }
        public FilterEnum(bool isEnabled, TEnum value) : base(isEnabled, value) { }
        public FilterEnum(bool isEnabledEditable, bool isEnabled) : base(isEnabledEditable, isEnabled, 0) { }
    }

    public enum FixedProfilePeriods
    {
        [Display(Name = "Current day")] CurrentDay,
        [Display(Name = "Previous day")] LastDay,
        [Display(Name = "Current week")] CurrentWeek,
        [Display(Name = "Previous week")] LastWeek,
        [Display(Name = "Current month")] CurrentMonth,
        [Display(Name = "Previous month")] LastMonth,
        [Display(Name = "Contract")] Contract,
    }

    /// <summary>Pedido de perfil fijo de un período (día, semana, mes, contrato).</summary>
    public sealed class FixedProfileRequest
    {
        public FixedProfileRequest(FixedProfilePeriods period)
        {
            Period = period;
        }

        public FixedProfileRequest(FixedProfilePeriods period, TradingSessionDescription session) : this(period)
        {
            Session = session;
        }

        public FixedProfileRequest(FixedProfilePeriods period, long? tradingSessionId) : this(period)
        {
            TradingSessionId = tradingSessionId;
        }

        public long? TradingSessionId { get; }

        public FixedProfilePeriods Period { get; }
        public TradingSessionDescription Session { get; }
    }

    /// <summary>Respuesta a un pedido de perfil fijo.</summary>
    public sealed class FixedProfileResponse
    {
        public FixedProfileResponse(IndicatorCandle scaled, IndicatorCandle original, FixedProfilePeriods period)
        {
            Scaled = scaled;
            Original = original;
            Period = period;
        }

        /// <summary>Perfil agrupado como las filas del gráfico.</summary>
        public IndicatorCandle Scaled { get; }

        /// <summary>Perfil a precio de tick.</summary>
        public IndicatorCandle Original { get; }

        public FixedProfilePeriods Period { get; }
    }

    public enum ContractRolloverType
    {
        [Display(Name = "Expiry")] ExpirationDate,
        [Display(Name = "By volume (end of current contract)")] VolumeBasedCurrentEnd,
        [Display(Name = "By volume (start of next contract)")] VolumeBasedNextStart,
    }

    /// <summary>Cambio de contrato de un futuro.</summary>
    public sealed class ContractRollover
    {
        public DateTime Date { get; init; }
        public string Code { get; init; } = "";
    }

    public sealed class ContractRollovers
    {
        public ContractRollover[] Rollovers { get; init; } = Array.Empty<ContractRollover>();
    }

    /// <summary>Datos que se piden en línea al proveedor (rollovers de contratos, etc.).</summary>
    public interface IOnlineDataProvider
    {
        Task<ContractRollovers> GetContractRolloversAsync(DateTime from, DateTime to, ContractRolloverType type);
    }

    /// <summary>Proveedor sin datos en línea (cripto y simulador no tienen rollovers).</summary>
    public sealed class EmptyOnlineDataProvider : IOnlineDataProvider
    {
        public static readonly EmptyOnlineDataProvider Instance = new();

        public Task<ContractRollovers> GetContractRolloversAsync(DateTime from, DateTime to, ContractRolloverType type) => Task.FromResult(new ContractRollovers());
    }

    /// <summary>Base para objetos de configuración que avisan cuando cambia una propiedad.</summary>
    public abstract class NotifyPropertyChangedBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) => OnPropertyChanged(propertyName);

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public enum SelectionType
    {
        Full,
        Bid,
        Ask,
    }

    public enum CandleVisualMode
    {
        [Display(Name = "Candles")] Candles,
        [Display(Name = "Bars")] Bars,
    }

    /// <summary>Lo que un indicador puede pedirle al proveedor de datos del gráfico.</summary>
    public interface IIndicatorDataProvider
    {
        ChartInfo ChartInfo { get; }
        InstrumentInfo InstrumentInfo { get; }
        int CandlesCount { get; }
        IndicatorCandle GetCandle(int bar);

        /// <summary>Datos en línea del proveedor (rollovers…).</summary>
        IOnlineDataProvider OnlineDataProvider => EmptyOnlineDataProvider.Instance;

        bool IsNewSession(int bar);
        bool IsNewWeek(int bar);
        bool IsNewMonth(int bar);

        /// <summary>Si entre dos instantes empezó una sesión, semana o mes nuevos.</summary>
        bool IsNewSession(DateTime previous, DateTime current);
        bool IsNewWeek(DateTime previous, DateTime current);
        bool IsNewMonth(DateTime previous, DateTime current);

        /// <summary>Inicio del período de largo <paramref name="period"/> que contiene a <paramref name="time"/>.</summary>
        DateTime GetCustomStartTime(DateTime time, TimeSpan period) =>
            period <= TimeSpan.Zero ? time : new DateTime(time.Ticks - time.Ticks % period.Ticks, time.Kind);
    }

    /// <summary>Editor de propiedades (la ventana de configuración).</summary>
    public interface IPropertiesEditor
    {
        object EditedObject { get; }

        void BeginInit()
        {
        }

        void EndInit()
        {
        }

        void SetIsExpandedCategory(string category, bool isExpanded)
        {
        }

        void SetIsExpandedProperty(string property, bool isExpanded)
        {
        }
    }


    /// <summary>Validación: el valor debe ser mayor que otra propiedad.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class GreaterThanAttribute<T> : ValidationAttribute where T : IComparable<T>
    {
        public GreaterThanAttribute(string otherProperty)
        {
            OtherProperty = otherProperty;
        }

        public string OtherProperty { get; }

        protected override ValidationResult IsValid(object value, ValidationContext ctx)
        {
            var other = ctx.ObjectType.GetProperty(OtherProperty)?.GetValue(ctx.ObjectInstance);
            if (value is T v && other is T o && v.CompareTo(o) <= 0) return new ValidationResult(ErrorMessageString);
            return ValidationResult.Success;
        }
    }

    /// <summary>Validación: el valor debe ser menor que otra propiedad.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class LessThanAttribute<T> : ValidationAttribute where T : IComparable<T>
    {
        public LessThanAttribute(string otherProperty)
        {
            OtherProperty = otherProperty;
        }

        public string OtherProperty { get; }

        protected override ValidationResult IsValid(object value, ValidationContext ctx)
        {
            var other = ctx.ObjectType.GetProperty(OtherProperty)?.GetValue(ctx.ObjectInstance);
            if (value is T v && other is T o && v.CompareTo(o) >= 0) return new ValidationResult(ErrorMessageString);
            return ValidationResult.Success;
        }
    }
}

namespace TradingHub.Common
{
    /// <summary>Base con INotifyPropertyChanged.</summary>
    public abstract class NotifyPropertyChangedBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void RaisePropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null) => RaisePropertyChanged(propertyName);

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null) => SetProperty(ref field, value, propertyName);
    }
}

namespace TradingHub.Common.Collections.Synchronized
{
    /// <summary>Lista con bloqueo (en Trading Hub todo corre en el hilo de UI, así que es una List común).</summary>
    public class SyncList<T> : List<T>
    {
        public SyncList()
        {
        }

        public SyncList(int capacity) : base(capacity)
        {
        }

        public SyncList(IEnumerable<T> items) : base(items)
        {
        }

        public object SyncRoot { get; } = new();
    }
}

namespace TradingHub.Common.Localization
{
    public static class LocalizationExtensions
    {
        public static string Localize(this string key) => key;
    }
}

namespace TradingHub.Common.Attributes
{
    [AttributeUsage(AttributeTargets.All)]
    public sealed class LocalizedDescriptionAttribute : DescriptionAttribute
    {
        public LocalizedDescriptionAttribute(Type resourceType, string key) : base(key)
        {
        }
    }
}

namespace TradingHub.Rendering.Heatmap
{
    public enum HeatmapTypes
    {
        [Display(Name = "Red → dark → green")] RedToDarkToGreen,
        [Display(Name = "Red → green")] RedToGreen,
        [Display(Name = "Blue → red")] BlueToRed,
        [Display(Name = "Grayscale")] Grayscale,
        [Display(Name = "Custom")] Custom,
    }

    public static class HeatmapColors
    {
        /// <summary>Color en la escala (t de 0 a 1).</summary>
        public static Color GetColor(HeatmapTypes type, double t)
        {
            t = Math.Clamp(t, 0, 1);
            static Color Lerp(Color a, Color b, double k) => Color.FromArgb(
                (int)(a.A + (b.A - a.A) * k), (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
            return type switch
            {
                HeatmapTypes.RedToGreen => Lerp(Color.FromArgb(232, 69, 72), Color.FromArgb(73, 209, 73), t),
                HeatmapTypes.BlueToRed => Lerp(Color.FromArgb(40, 90, 220), Color.FromArgb(232, 69, 72), t),
                HeatmapTypes.Grayscale => Lerp(Color.FromArgb(20, 20, 20), Color.FromArgb(235, 235, 235), t),
                _ => t < 0.5 ? Lerp(Color.FromArgb(232, 69, 72), Color.FromArgb(25, 25, 30), t * 2) : Lerp(Color.FromArgb(25, 25, 30), Color.FromArgb(73, 209, 73), (t - 0.5) * 2),
            };
        }
    }
}

namespace TradingHub.Rendering.Helpers
{
    /// <summary>Acumula barras horizontales de un histograma y las dibuja juntas.</summary>
    public class HistogramRender
    {
        private readonly bool _leftToRight;
        private readonly List<Rectangle> _rects = new();

        public HistogramRender(bool leftToRight)
        {
            _leftToRight = leftToRight;
        }

        public void AddPrice(int x1, int x2, int y1, int y2)
        {
            int l = Math.Min(x1, x2), r = Math.Max(x1, x2);
            int t = Math.Min(y1, y2), b = Math.Max(y1, y2);
            _rects.Add(new Rectangle(l, t, Math.Max(1, r - l), Math.Max(1, b - t + 1)));
        }

        public void Draw(RenderContext context, Color color, bool fill)
        {
            foreach (var r in _rects)
            {
                if (fill) context.FillRectangle(color, r);
                else context.DrawRectangle(new TradingHub.Rendering.Tools.RenderPen(color), r);
            }
        }

        public void Clear() => _rects.Clear();
    }

    /// <summary>Histograma donde cada barra puede tener su propio color.</summary>
    public class MultiColorsHistogramRender
    {
        private readonly Color _defaultColor;
        private readonly bool _leftToRight;
        private readonly List<(Rectangle Rect, Color Color)> _rects = new();

        public MultiColorsHistogramRender(Color defaultColor, bool leftToRight)
        {
            _defaultColor = defaultColor;
            _leftToRight = leftToRight;
        }

        public void AddPrice(int x1, int x2, int y1, int y2) => AddPrice(x1, x2, y1, y2, _defaultColor);

        public void AddPrice(int x1, int x2, int y1, int y2, Color color)
        {
            int l = Math.Min(x1, x2), r = Math.Max(x1, x2);
            int t = Math.Min(y1, y2), b = Math.Max(y1, y2);
            _rects.Add((new Rectangle(l, t, Math.Max(1, r - l), Math.Max(1, b - t + 1)), color));
        }

        public void Draw(RenderContext context, bool fill)
        {
            foreach (var (r, c) in _rects)
            {
                if (fill) context.FillRectangle(c, r);
                else context.DrawRectangle(new TradingHub.Rendering.Tools.RenderPen(c), r);
            }
        }

        public void Clear() => _rects.Clear();
    }
}

namespace TradingHub.Rendering.Settings
{
    /// <summary>Relleno listo para dibujar (color inicial y final si es degradé).</summary>
    public sealed class RenderBrush
    {
        public RenderBrush(Color startColor, Color endColor)
        {
            StartColor = startColor;
            EndColor = endColor;
        }

        public Color StartColor { get; }
        public Color EndColor { get; }

        public static implicit operator Color(RenderBrush b) => b.StartColor;
    }

    /// <summary>Configuración editable de un relleno.</summary>
    public class BrushSettings : INotifyPropertyChanged
    {
        private Color _startColor = Color.FromArgb(60, 30, 144, 255);
        private Color _endColor = Color.Empty;

        public event PropertyChangedEventHandler PropertyChanged;

        [Display(Name = "Color", Order = 10)]
        public Color StartColor
        {
            get => _startColor;
            set
            {
                _startColor = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartColor)));
            }
        }

        [Display(Name = "End color", Order = 20)]
        public Color EndColor
        {
            get => _endColor;
            set
            {
                _endColor = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndColor)));
            }
        }

        [Browsable(false)]
        public Color Color
        {
            get => _startColor;
            set => StartColor = value;
        }

        /// <summary>Usa degradé desde StartColor hasta EndColor.</summary>
        [Browsable(false)]
        public bool UseEndColor { get; set; }

        [Browsable(false)]
        public RenderBrush RenderObject => new(_startColor, UseEndColor ? _endColor : _startColor);
    }
}

namespace TradingHub.Attributes.Editors
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SoundComboBoxEditorAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SelectDirectoryEditorAttribute : Attribute
    {
        public bool IsTextEditable { get; set; } = true;
        public bool ShowNewFolderButton { get; set; } = true;
    }
}
