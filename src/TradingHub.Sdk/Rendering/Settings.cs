using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using TradingHub.Indicators;
using TradingHub.Rendering.Tools;

namespace TradingHub.Rendering.Settings
{
    /// <summary>Configuración editable de un lápiz (color, grosor, estilo).</summary>
    public class PenSettings : INotifyPropertyChanged
    {
        private Color _color = Color.FromArgb(255, 30, 144, 255);
        private int _width = 1;
        private LineDashStyle _lineDashStyle = LineDashStyle.Solid;
        private RenderPen _renderObject;

        public event PropertyChangedEventHandler PropertyChanged;

        [Display(Name = "Color", Order = 10)]
        public Color Color
        {
            get => _color;
            set => Set(ref _color, value);
        }

        [Display(Name = "Width", Order = 20)]
        [Range(1, 20)]
        public int Width
        {
            get => _width;
            set => Set(ref _width, Math.Max(1, value));
        }

        [Display(Name = "Style", Order = 30)]
        public LineDashStyle LineDashStyle
        {
            get => _lineDashStyle;
            set => Set(ref _lineDashStyle, value);
        }

        [Browsable(false)]
        [JsonIgnore]
        public RenderPen RenderObject => _renderObject ??= new RenderPen(_color, _width, ToDash(_lineDashStyle));

        [Browsable(false)]
        [JsonIgnore]
        public TradingHub.Common.UniversalPen Pen => new(_color, _width, ToDash(_lineDashStyle));

        public static DashStyle ToDash(LineDashStyle s) => s switch
        {
            LineDashStyle.Dash => DashStyle.Dash,
            LineDashStyle.Dot => DashStyle.Dot,
            LineDashStyle.DashDot => DashStyle.DashDot,
            LineDashStyle.DashDotDot => DashStyle.DashDotDot,
            _ => DashStyle.Solid,
        };

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            _renderObject = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public override string ToString() => $"{_color.Name} {_width}px";
    }

    /// <summary>Configuración editable de una fuente.</summary>
    public class FontSetting : INotifyPropertyChanged
    {
        private string _fontFamily = "Arial";
        private int _size = 9;
        private bool _bold;
        private bool _italic;
        private RenderFont _renderObject;

        public FontSetting()
        {
        }

        public FontSetting(string fontFamily, float size)
        {
            _fontFamily = fontFamily;
            _size = (int)Math.Round(size);
        }

        public FontSetting(string fontFamily, float size, bool bold) : this(fontFamily, size)
        {
            _bold = bold;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [Display(Name = "Source", Order = 10)]
        public string FontFamily
        {
            get => _fontFamily;
            set => Set(ref _fontFamily, value);
        }

        [Display(Name = "Size", Order = 20)]
        [Range(4, 72)]
        public int Size
        {
            get => _size;
            set => Set(ref _size, value);
        }

        [Display(Name = "Bold", Order = 30)]
        public bool Bold
        {
            get => _bold;
            set => Set(ref _bold, value);
        }

        [Display(Name = "Italic", Order = 40)]
        public bool Italic
        {
            get => _italic;
            set => Set(ref _italic, value);
        }

        [Browsable(false)]
        [JsonIgnore]
        public RenderFont RenderObject => _renderObject ??= new RenderFont(_fontFamily, _size,
            (_bold ? FontStyle.Bold : FontStyle.Regular) | (_italic ? FontStyle.Italic : FontStyle.Regular));

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            _renderObject = null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public override string ToString() => $"{_fontFamily} {_size}";
    }
}
