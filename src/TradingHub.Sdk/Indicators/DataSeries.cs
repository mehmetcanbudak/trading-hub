using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using TradingHub.Common.Collections.Synchronized;

namespace TradingHub.Indicators
{
    /// <summary>Serie de datos de un indicador (una línea, un histograma, velas, colores...).</summary>
    public interface IDataSeries : INotifyPropertyChanged
    {
        string Id { get; }
        string Name { get; set; }
        int Count { get; }
        object this[int index] { get; set; }
        bool IsHidden { get; set; }
        bool Visible { get; set; }
        bool ScaleIt { get; set; }
        bool DrawAbovePrice { get; set; }
        Type ValueType { get; }

        /// <summary>Clave de la descripción (localización) que muestra el editor.</summary>
        string DescriptionKey { get; set; }

        /// <summary>Usa el modo minimizado del panel si está activado.</summary>
        bool UseMinimizedModeIfEnabled { get; set; }

        /// <summary>No se ofrece para alertas.</summary>
        bool IgnoredByAlerts { get; set; }
        void Clear();
        void Resize(int count);
    }

    /// <summary>Base de las series: un arreglo que crece solo al asignar y devuelve default fuera de rango.</summary>
    public abstract class BaseDataSeries<T> : IDataSeries, IEnumerable<T>
    {
        protected T[] _values = Array.Empty<T>();
        private int _count;
        private string _name;
        private bool _isHidden;
        private bool _visible = true;

        protected BaseDataSeries(string id, string name)
        {
            Id = id ?? name ?? GetType().Name;
            _name = name ?? id;
        }

        protected BaseDataSeries(string name) : this(name, name)
        {
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [Browsable(false)]
        public string Id { get; set; }

        [Browsable(false)]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        [Browsable(false)]
        public string DescriptionKey { get; set; }

        [Browsable(false)]
        public bool IsHidden
        {
            get => _isHidden;
            set => SetProperty(ref _isHidden, value);
        }

        [Display(Name = "Visible", Order = 0)]
        public bool Visible
        {
            get => _visible;
            set => SetProperty(ref _visible, value);
        }

        [Browsable(false)]
        public bool ScaleIt { get; set; } = true;

        [Browsable(false)]
        public bool DrawAbovePrice { get; set; }

        [Browsable(false)]
        public bool IgnoredByAlerts { get; set; }

        [Browsable(false)]
        public bool UseMinimizedModeIfEnabled { get; set; }

        [Browsable(false)]
        public bool ResetAlertsOnNewBar { get; set; }

        [Browsable(false)]
        public bool ShowTooltip { get; set; } = true;

        [Browsable(false)]
        public bool IsDynamicColor { get; set; }

        [Browsable(false)]
        public int Count => _count;

        [Browsable(false)]
        public Type ValueType => typeof(T);

        protected virtual T CreateDefault(int index) => default;

        protected virtual bool CreatesOnRead => false;

        public virtual T this[int index]
        {
            get
            {
                if (index < 0) return default;
                if (index >= _count)
                {
                    if (!CreatesOnRead) return default;
                    EnsureCount(index + 1);
                }

                if (CreatesOnRead && _values[index] == null) _values[index] = CreateDefault(index);
                return _values[index];
            }
            set
            {
                if (index < 0) return;
                EnsureCount(index + 1);
                _values[index] = value;
            }
        }

        object IDataSeries.this[int index]
        {
            get => this[index];
            set => this[index] = (T)Convert(value);
        }

        protected virtual object Convert(object value) => value;

        protected void EnsureCount(int count)
        {
            if (count > _values.Length)
            {
                int cap = Math.Max(count, Math.Max(16, _values.Length * 2));
                Array.Resize(ref _values, cap);
            }

            if (count > _count) _count = count;
        }

        public virtual void Resize(int count)
        {
            if (count < 0) count = 0;
            if (count > _count) EnsureCount(count);
            else if (count < _count)
            {
                Array.Clear(_values, count, _count - count);
                _count = count;
            }
        }

        public virtual void Clear()
        {
            Array.Clear(_values, 0, _count);
            _count = 0;
        }

        /// <summary>Valor en la última barra (o default).</summary>
        [Browsable(false)]
        public T LastValue => _count > 0 ? this[_count - 1] : default;

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        protected bool SetProperty<TV>(ref TV field, TV value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<TV>.Default.Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public override string ToString() => Name;
    }

    /// <summary>Colores por barra de una serie (Color.Empty = usar el color de la serie).</summary>
    public sealed class SeriesColors
    {
        private Color[] _colors = Array.Empty<Color>();

        public Color this[int bar]
        {
            get => bar >= 0 && bar < _colors.Length ? _colors[bar] : Color.Empty;
            set
            {
                if (bar < 0) return;
                if (bar >= _colors.Length) Array.Resize(ref _colors, Math.Max(bar + 1, Math.Max(16, _colors.Length * 2)));
                _colors[bar] = value;
            }
        }

        public bool Has(int bar) => bar >= 0 && bar < _colors.Length && !_colors[bar].IsEmpty;

        public void Clear() => Array.Clear(_colors);
    }

    /// <summary>Serie numérica: líneas, histogramas, puntos, flechas...</summary>
    public class ValueDataSeries : BaseDataSeries<decimal>
    {
        private Color _color = Color.FromArgb(255, 70, 130, 230);
        private VisualMode _visualType = VisualMode.Line;
        private int _width = 1;
        private LineDashStyle _lineDashStyle = LineDashStyle.Solid;
        private readonly HashSet<int> _endLines = new();

        public ValueDataSeries(string id, string name) : base(id, name)
        {
        }

        public ValueDataSeries(string name) : base(name)
        {
        }

        public ValueDataSeries() : base("Value")
        {
        }

        [Display(Name = "Color", Order = 10)]
        public Color Color
        {
            get => _color;
            set => SetProperty(ref _color, value);
        }

        [Browsable(false)]
        [JsonIgnore]
        public SeriesColors Colors { get; } = new();

        /// <summary>Formato del valor en el eje (ej. "{0:F2}").</summary>
        [Browsable(false)]
        [JsonIgnore]
        public string StringFormat { get; set; }

        /// <summary>Color de los valores (etiquetas).</summary>
        [Browsable(false)]
        public Color ValuesColor { get; set; } = Color.White;

        /// <summary>Moneda si la serie muestra dinero.</summary>
        [Browsable(false)]
        [JsonIgnore]
        public TradingHub.Trading.MoneyCurrency? ValueCurrency { get; set; }

        [Display(Name = "Type", Order = 20)]
        public VisualMode VisualType
        {
            get => _visualType;
            set => SetProperty(ref _visualType, value);
        }

        [Display(Name = "Width", Order = 30)]
        [Range(1, 20)]
        public int Width
        {
            get => _width;
            set => SetProperty(ref _width, Math.Max(1, value));
        }

        [Display(Name = "Line style", Order = 40)]
        public LineDashStyle LineDashStyle
        {
            get => _lineDashStyle;
            set => SetProperty(ref _lineDashStyle, value);
        }

        [Display(Name = "Show zeros", Order = 50)]
        public bool ShowZeroValue { get; set; } = true;

        [Display(Name = "Current value on axis", Order = 60)]
        public bool ShowCurrentValue { get; set; } = true;

        [Browsable(false)]
        public bool ShowNullValue { get; set; }

        [Browsable(false)]
        public decimal ZeroValue { get; set; }

        [Browsable(false)]
        public int Digits { get; set; } = -1;

        [Browsable(false)]
        public Color ValueAxisBackground { get; set; } = Color.Empty;

        /// <summary>Corta la línea después de esta barra (no la une con la siguiente).</summary>
        public void SetPointOfEndLine(int bar) => _endLines.Add(bar);

        public void RemovePointOfEndLine(int bar) => _endLines.Remove(bar);

        public bool IsThisPointOfEndLine(int bar) => _endLines.Contains(bar);

        public bool IsThisPointOfStartBar(int bar) => _endLines.Contains(bar - 1);

        public override void Clear()
        {
            base.Clear();
            Colors.Clear();
            _endLines.Clear();
        }

        protected override object Convert(object value) => value switch
        {
            null => 0m,
            decimal d => d,
            _ => System.Convert.ToDecimal(value),
        };

        public decimal Max(int count, int bar)
        {
            decimal m = decimal.MinValue;
            for (int i = Math.Max(0, bar - count + 1); i <= bar; i++) m = Math.Max(m, this[i]);
            return m == decimal.MinValue ? 0 : m;
        }

        public decimal Min(int count, int bar)
        {
            decimal m = decimal.MaxValue;
            for (int i = Math.Max(0, bar - count + 1); i <= bar; i++) m = Math.Min(m, this[i]);
            return m == decimal.MaxValue ? 0 : m;
        }
    }

    /// <summary>Valor de una banda (superior e inferior).</summary>
    public class RangeValue
    {
        public decimal Upper { get; set; }
        public decimal Lower { get; set; }

        public RangeValue()
        {
        }

        public RangeValue(decimal upper, decimal lower)
        {
            Upper = upper;
            Lower = lower;
        }

        public override string ToString() => $"{Lower} - {Upper}";
    }

    /// <summary>Serie de bandas: rellena entre Upper y Lower.</summary>
    public class RangeDataSeries : BaseDataSeries<RangeValue>
    {
        public RangeDataSeries(string id, string name) : base(id, name)
        {
        }

        public RangeDataSeries(string name) : base(name)
        {
        }

        [Display(Name = "Fill color", Order = 10)]
        public Color RangeColor { get; set; } = Color.FromArgb(40, 70, 130, 230);

        protected override bool CreatesOnRead => true;
        protected override RangeValue CreateDefault(int index) => new();
    }

    /// <summary>Vela de una CandleDataSeries (por ejemplo, el delta acumulado dibujado como velas).</summary>
    public class Candle
    {
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public DateTime Time { get; set; }
        public Color? Color { get; set; }

        public Candle()
        {
        }

        public Candle(decimal open, decimal high, decimal low, decimal close)
        {
            Open = open;
            High = high;
            Low = low;
            Close = close;
        }

        public bool IsEmpty => Open == 0 && High == 0 && Low == 0 && Close == 0;

        public Candle Clone() => (Candle)base.MemberwiseClone();

        public new Candle MemberwiseClone() => (Candle)base.MemberwiseClone();
    }

    /// <summary>Dónde se ancla el tooltip de una vela.</summary>
    public enum CandleTooltipAnchor
    {
        Close,
        Top,
        Bottom,
    }

    /// <summary>Serie de velas en un panel.</summary>
    public class CandleDataSeries : BaseDataSeries<Candle>
    {
        public CandleDataSeries(string id, string name) : base(id, name)
        {
        }

        public CandleDataSeries(string name) : base(name)
        {
        }

        [Display(Name = "Bullish candle", Order = 10)]
        public Color UpCandleColor { get; set; } = Color.FromArgb(255, 73, 209, 73);

        [Display(Name = "Bearish candle", Order = 20)]
        public Color DownCandleColor { get; set; } = Color.FromArgb(255, 232, 69, 72);

        [Display(Name = "Border", Order = 30)]
        public Color BorderColor { get; set; } = Color.FromArgb(255, 120, 120, 120);

        [Display(Name = "Draw border", Order = 40)]
        public bool DrawCandleBorder { get; set; } = true;

        [Browsable(false)]
        public bool ShowCurrentValue { get; set; } = true;

        [Browsable(false)]
        public SeriesColors Colors { get; } = new();

        /// <summary>Velas o barras.</summary>
        [Display(Name = "Mode", Order = 50)]
        public CandleVisualMode Mode { get; set; } = CandleVisualMode.Candles;

        [Browsable(false)]
        public Color ValuesColor { get; set; } = Color.White;

        [Browsable(false)]
        [JsonIgnore]
        public TradingHub.Trading.MoneyCurrency? ValueCurrency { get; set; }

        /// <summary>No dibuja las velas vacías (todo en cero).</summary>
        [Browsable(false)]
        public bool HideZeroCandles { get; set; }

        [Browsable(false)]
        public bool HideOpenCloseLabels { get; set; }

        [Browsable(false)]
        public CandleTooltipAnchor TooltipAnchor { get; set; }

        protected override bool CreatesOnRead => true;
        protected override Candle CreateDefault(int index) => new();

        public override void Clear()
        {
            base.Clear();
            Colors.Clear();
        }
    }

    /// <summary>Colorea las velas del gráfico principal barra por barra (null = color normal).</summary>
    public class PaintbarsDataSeries : BaseDataSeries<Color?>
    {
        public PaintbarsDataSeries(string id, string name) : base(id, name)
        {
        }

        public PaintbarsDataSeries(string name) : base(name)
        {
        }

        /// <summary>Oculta las velas del gráfico (se dibujan solo las de la serie).</summary>
        [Browsable(false)]
        public bool HideChart { get; set; }

        protected override object Convert(object value) => value switch
        {
            null => null,
            Color c => c,
            _ => (Color?)value,
        };
    }

    public enum ObjectType
    {
        Rectangle,
        Ellipse,
        Triangle,
        Diamond,
        Cross,
        Square,
        OnlyCluster,
        Line,
        DownArrow,
        UpArrow,
    }

    public enum PriceSelectionValueTypes
    {
        Default,
        Ask,
        Bid,
    }

    /// <summary>Marca un rango de precios en una barra (círculos de big trades, celdas resaltadas...).</summary>
    public class PriceSelectionValue
    {
        public PriceSelectionValue(decimal price)
        {
            MinimumPrice = MaximumPrice = price;
        }

        public PriceSelectionValue(decimal minimumPrice, decimal maximumPrice)
        {
            MinimumPrice = minimumPrice;
            MaximumPrice = maximumPrice;
        }

        public decimal MinimumPrice { get; set; }
        public decimal MaximumPrice { get; set; }
        public ObjectType VisualObject { get; set; } = ObjectType.Rectangle;
        public int Size { get; set; } = 10;
        public int MinimumSize { get; set; } = 1;
        public int MaximumSize { get; set; } = 100;
        public Color PriceSelectionColor { get; set; } = Color.FromArgb(80, 70, 130, 230);
        public Color ObjectColor { get; set; } = Color.FromArgb(160, 70, 130, 230);
        public Color ObjectBorderColor { get; set; } = Color.Empty;
        public int ObjectsTransparency { get; set; }
        public string Tooltip { get; set; }
        public object Context { get; set; }
        public bool DrawAbovePrice { get; set; } = true;
        public SelectionType SelectionSide { get; set; }
        public decimal Volume { get; set; }
        public int ClusterTransparency { get; set; }
        public bool IsBorderEnabled { get; set; }
    }

    public class PriceSelectionDataSeries : BaseDataSeries<SyncList<PriceSelectionValue>>
    {
        public PriceSelectionDataSeries(string id, string name) : base(id, name)
        {
        }

        public PriceSelectionDataSeries(string name) : base(name)
        {
        }

        protected override bool CreatesOnRead => true;
        protected override SyncList<PriceSelectionValue> CreateDefault(int index) => new();

        /// <summary>Cambió el contenido de la serie.</summary>
        public event Action<int> Changed;

        public void RaiseChanged(int bar) => Changed?.Invoke(bar);
    }

    /// <summary>Serie de objetos arbitrarios (no se dibuja).</summary>
    public class ObjectDataSeries : BaseDataSeries<object>
    {
        public ObjectDataSeries(string id, string name) : base(id, name)
        {
        }

        public ObjectDataSeries(string name) : base(name)
        {
        }
    }

    /// <summary>Lista de series de un indicador (DataSeries[0] es la principal).</summary>
    public sealed class IndicatorDataSeriesCollection : Collection<IDataSeries>
    {
        internal IndicatorDataSeriesCollection()
        {
        }

        public IDataSeries FindById(string id) => this.FirstOrDefault(s => s.Id == id);

        public void ForEach(Action<IDataSeries> action)
        {
            foreach (var s in this.ToList()) action(s);
        }

        public void AddRange(IEnumerable<IDataSeries> series)
        {
            foreach (var s in series) Add(s);
        }
    }
}
