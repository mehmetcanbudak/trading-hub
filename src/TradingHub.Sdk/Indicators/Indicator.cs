using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using TradingHub.Common;
using TradingHub.Common.Logging;
using TradingHub.Indicators.Drawing;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Trading;

[assembly: InternalsVisibleTo("TradingHub.Charting")]
[assembly: InternalsVisibleTo("TradingHub.App")]
[assembly: InternalsVisibleTo("TradingHub.Tests")]
[assembly: InternalsVisibleTo("TradingHub.Scripting")]

namespace TradingHub.Indicators
{
    /// <summary>Lo que el gráfico le ofrece a un indicador. Lo implementa el motor de gráficos.</summary>
    public interface IIndicatorHost
    {
        int BarCount { get; }
        IndicatorCandle GetCandle(int bar);
        InstrumentInfo InstrumentInfo { get; }
        ChartInfo ChartInfo { get; }
        MarketDepthInfo MarketDepthInfo { get; }
        TradingManager TradingManager { get; }
        TradingHub.Trading.Statistics.TradingStatisticsProvider TradingStatisticsProvider { get; }
        int FirstVisibleBar { get; }
        int LastVisibleBar { get; }
        bool IsNewSession(int bar);
        bool IsNewWeek(int bar);
        bool IsNewMonth(int bar);
        IChartContainer GetContainer(Indicator indicator);
        void RequestRecalculate(Indicator indicator);
        void RequestRedraw();
        void AddAlert(Indicator source, string soundFile, string instrument, string message, Color background, Color foreground);
        void BeginInvoke(Action action);
        void SubscribeTimer(Indicator indicator, TimeSpan interval, Action action);
        void UnsubscribeTimer(Indicator indicator, TimeSpan interval, Action action);
        void RequestCumulativeTrades(Indicator indicator, CumulativeTradesRequest request);

        /// <summary>Pide el perfil de un período (respuesta en OnFixedProfilesResponse).</summary>
        void RequestFixedProfile(Indicator indicator, FixedProfileRequest request)
        {
        }
    }

    /// <summary>Argumento de RedrawChart.</summary>
    public class RedrawArg
    {
        public RedrawArg()
        {
        }

        public RedrawArg(Rectangle region)
        {
            Region = region;
        }

        public RedrawArg(IChartContainer container) : this(container?.Region ?? Rectangle.Empty)
        {
        }

        public Rectangle Region { get; }
    }

    /// <summary>
    /// Clase base de todos los indicadores. API compatible con la de ATAS: OnCalculate(bar, value),
    /// DataSeries, OnRender(RenderContext, DrawingLayouts), GetCandle(bar), etc.
    /// </summary>
    public abstract class Indicator : INotifyPropertyChanged, IDisposable
    {
        private IIndicatorHost _host;
        private Indicator _parent;
        private readonly List<Indicator> _children = new();
        private IDataSeries _sourceSeries;
        private readonly ValueDataSeries _recordedSource = new("Source", "Source");
        private DrawingLayouts _layouts = DrawingLayouts.None;
        private string _name;
        private string _panel = IndicatorDataProvider.CandlesPanel;
        private readonly Dictionary<object, PropertyChangedEventHandler> _tracked = new(ReferenceEqualityComparer.Instance);

        protected Indicator() : this(false)
        {
        }

        protected Indicator(bool useCandles)
        {
            UseCandles = useCandles;
            DataSeries = new IndicatorDataSeriesCollection();
            DataSeries.Add(new ValueDataSeries("Main", "Main"));
        }

        protected Indicator(DataSeriesType sourceType) : this(true)
        {
            SourceType = sourceType;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        // ================================================================ identidad y configuración

        [Browsable(false)]
        [JsonIgnore]
        public string Name
        {
            get => TradingHub.Common.EnglishText.Of(_name ??= DefaultName(GetType()));
            set => _name = value;
        }

        public static string DefaultName(Type t)
        {
            var dn = t.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
            return TradingHub.Common.EnglishText.Of(string.IsNullOrWhiteSpace(dn) ? t.Name : dn);
        }

        [Browsable(false)]
        [JsonIgnore]
        public bool UseCandles { get; }

        [Browsable(false)]
        public DataSeriesType SourceType { get; set; } = DataSeriesType.Close;

        [Browsable(false)]
        [JsonIgnore]
        public IndicatorDataSeriesCollection DataSeries { get; }

        /// <summary>Serie de entrada (por defecto, el cierre de cada vela).</summary>
        [Browsable(false)]
        [JsonIgnore]
        public IDataSeries SourceDataSeries
        {
            get
            {
                if (_sourceSeries != null) return _sourceSeries;
                if (_parent == null && _host != null) return _hostSource ??= new CandleSourceSeries(this);
                return _recordedSource;
            }
            set => _sourceSeries = value;
        }

        private CandleSourceSeries _hostSource;

        /// <summary>Panel: IndicatorDataProvider.CandlesPanel (sobre el precio) o NewPanel.</summary>
        [Browsable(false)]
        public string Panel
        {
            get => _panel;
            set
            {
                if (_panel == value) return;
                _panel = value;
                RaisePropertyChanged();
            }
        }

        [Browsable(false)]
        [JsonIgnore]
        public bool DenyToChangePanel { get; set; }

        [Browsable(false)]
        [JsonIgnore]
        public bool EnableCustomDrawing { get; set; }

        [Browsable(false)]
        [JsonIgnore]
        public bool DrawAbovePrice { get; set; }

        [Browsable(false)]
        public bool IsVisible { get; set; } = true;

        [Browsable(false)]
        [JsonIgnore]
        public bool SupportsCalculationTimeFrame { get; set; }

        [Browsable(false)]
        [JsonIgnore]
        public bool IsVerticalLinesEnabled { get; set; }

        [Browsable(false)]
        [JsonIgnore]
        public DrawingLayouts SubscribedLayouts => _layouts;

        // ================================================================ objetos de dibujo

        [Browsable(false)]
        [JsonIgnore]
        public List<TrendLine> TrendLines { get; } = new();

        [Browsable(false)]
        [JsonIgnore]
        public List<LineTillTouch> HorizontalLinesTillTouch { get; } = new();

        [Browsable(false)]
        [JsonIgnore]
        public List<LineSeries> LineSeries { get; } = new();

        [Browsable(false)]
        [JsonIgnore]
        public List<DrawingRectangle> Rectangles { get; } = new();

        [Browsable(false)]
        [JsonIgnore]
        public Dictionary<string, DrawingText> Labels { get; } = new();

        // ================================================================ contexto del gráfico

        internal IIndicatorHost Host => _host ?? _parent?.Host;

        [Browsable(false)]
        [JsonIgnore]
        public int CurrentBar => Host?.BarCount ?? 0;

        [Browsable(false)]
        [JsonIgnore]
        public ChartInfo ChartInfo => Host?.ChartInfo;

        [Browsable(false)]
        [JsonIgnore]
        public InstrumentInfo InstrumentInfo => Host?.InstrumentInfo;

        [Browsable(false)]
        [JsonIgnore]
        public MarketDepthInfo MarketDepthInfo => Host?.MarketDepthInfo ?? MarketDepthInfo.Empty;

        [Browsable(false)]
        [JsonIgnore]
        public TradingManager TradingManager => Host?.TradingManager;

        [Browsable(false)]
        [JsonIgnore]
        public TradingHub.Trading.Statistics.TradingStatisticsProvider TradingStatisticsProvider =>
            Host?.TradingStatisticsProvider ?? TradingHub.Trading.Statistics.TradingStatisticsProvider.Empty;

        [Browsable(false)]
        [JsonIgnore]
        public IIndicatorDataProvider DataProvider => Host as IIndicatorDataProvider;

        [Browsable(false)]
        [JsonIgnore]
        public IChartContainer Container => Host?.GetContainer(_parent ?? this);

        [Browsable(false)]
        [JsonIgnore]
        public MouseLocationInfo MouseLocationInfo => ChartInfo?.MouseLocationInfo ?? new MouseLocationInfo();

        /// <summary>Área completa del gráfico en píxeles (como en ATAS).</summary>
        [Browsable(false)]
        public Rectangle ChartArea
        {
            get
            {
                var r = ChartInfo?.Region ?? Rectangle.Empty;
                return r.Width > 0 ? r : Container?.Region ?? Rectangle.Empty;
            }
        }

        /// <summary>Tipo de gráfico ("TimeFrame", "Tick", "Volume"…). En ATAS está marcado como obsoleto.</summary>
        [Browsable(false)]
        [Obsolete("Use ChartInfo.ChartType")]
        public string? ChartType => ChartInfo?.ChartType;

        /// <summary>Período del gráfico ("M5", "H1", "Daily"…). En ATAS está marcado como obsoleto.</summary>
        [Browsable(false)]
        [Obsolete("Use ChartInfo.TimeFrame")]
        public string? TimeFrame => ChartInfo?.TimeFrame;

        [Browsable(false)]
        [JsonIgnore]
        public int FirstVisibleBarNumber => Host?.FirstVisibleBar ?? 0;

        [Browsable(false)]
        [JsonIgnore]
        public int LastVisibleBarNumber => Host?.LastVisibleBar ?? 0;

        /// <summary>Valor de la serie principal en una barra.</summary>
        public decimal this[int bar]
        {
            get => DataSeries.Count > 0 && DataSeries[0] is ValueDataSeries v ? v[bar] : 0m;
            set
            {
                if (DataSeries.Count > 0 && DataSeries[0] is ValueDataSeries v) v[bar] = value;
            }
        }

        // ================================================================ ciclo de vida (lo que sobrescriben los indicadores)

        protected abstract void OnCalculate(int bar, decimal value);

        protected virtual void OnRecalculate()
        {
        }

        protected virtual void OnInitialize()
        {
        }

        protected virtual void OnFinishRecalculate()
        {
        }

        protected virtual void OnApplyDefaultColors()
        {
        }

        protected virtual void OnValuationChanged()
        {
        }

        protected virtual void OnDispose()
        {
        }

        protected virtual void OnRender(RenderContext context, DrawingLayouts layout)
        {
        }

        protected virtual void OnNewTrade(MarketDataArg trade)
        {
        }

        protected virtual void OnNewTrades(IEnumerable<MarketDataArg> trades)
        {
            foreach (var t in trades) OnNewTrade(t);
        }

        protected virtual void OnBestBidAskChanged(MarketDataArg depth)
        {
        }

        protected virtual void MarketDepthChanged(MarketDataArg depth)
        {
        }

        protected virtual void MarketDepthsChanged(IEnumerable<MarketDataArg> depths)
        {
            foreach (var d in depths) MarketDepthChanged(d);
        }

        /// <summary>
        /// Perfil del período pedido: <paramref name="fixedProfileScaled"/> agrupado como el gráfico y
        /// <paramref name="fixedProfileOriginScale"/> a precio de tick.
        /// </summary>
        protected virtual void OnFixedProfilesResponse(IndicatorCandle fixedProfileScaled, IndicatorCandle fixedProfileOriginScale, FixedProfilePeriods period)
        {
        }

        internal void HostFixedProfileResponse(IndicatorCandle scaled, IndicatorCandle origin, FixedProfilePeriods period)
        {
            TaskCompletionSource<FixedProfileResponse> tcs = null;
            lock (_pendingProfiles)
            {
                if (_pendingProfiles.Remove(period, out var t)) tcs = t;
            }

            tcs?.TrySetResult(new FixedProfileResponse(scaled, origin, period));
            OnFixedProfilesResponse(scaled, origin, period);
        }

        protected virtual void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> cumulativeTrades)
        {
        }

        protected virtual void OnCumulativeTrade(CumulativeTrade trade)
        {
        }

        protected virtual void OnUpdateCumulativeTrade(CumulativeTrade trade)
        {
        }

        protected virtual void OnNewMyTrade(MyTrade myTrade)
        {
        }

        protected virtual void OnPositionChanged(Position position)
        {
        }

        protected virtual void OnOrderChanged(Order order)
        {
        }

        protected virtual void OnPortfolioSelected(Portfolio portfolio)
        {
        }

        protected virtual void OnDataProviderChanged(IIndicatorDataProvider oldDataProvider, IIndicatorDataProvider newDataProvider)
        {
        }

        protected virtual void OnPropertiesEditorChanged(IPropertiesEditor oldValue, IPropertiesEditor newValue)
        {
        }

        public virtual bool ProcessMouseClick(RenderControlMouseEventArgs e) => false;
        public virtual bool ProcessMouseDown(RenderControlMouseEventArgs e) => false;
        public virtual bool ProcessMouseUp(RenderControlMouseEventArgs e) => false;
        public virtual bool ProcessMouseMove(RenderControlMouseEventArgs e) => false;
        public virtual bool ProcessMouseDoubleClick(RenderControlMouseEventArgs e) => false;
        public virtual bool ProcessMouseWheel(RenderControlMouseEventArgs e) => false;
        public virtual bool ProcessKeyDown(Avalonia.Input.KeyEventArgs e) => false;
        public virtual bool ProcessKeyUp(Avalonia.Input.KeyEventArgs e) => false;
        public virtual StdCursor GetCursor(RenderControlMouseEventArgs e) => StdCursor.None;

        // ================================================================ servicios para los indicadores

        public IndicatorCandle GetCandle(int bar)
        {
            var h = Host;
            if (h == null || h.BarCount == 0) throw new InvalidOperationException("Indicator is not attached to a chart.");
            bar = Math.Clamp(bar, 0, h.BarCount - 1);
            return h.GetCandle(bar);
        }

        public bool IsNewSession(int bar) => bar > 0 && (Host?.IsNewSession(bar) ?? false);
        public bool IsNewWeek(int bar) => bar > 0 && (Host?.IsNewWeek(bar) ?? false);
        public bool IsNewMonth(int bar) => bar > 0 && (Host?.IsNewMonth(bar) ?? false);

        /// <summary>Pide recalcular el indicador completo (se hace en el próximo ciclo).</summary>
        public void RecalculateValues()
        {
            var root = this;
            while (root._parent != null) root = root._parent;
            root._host?.RequestRecalculate(root);
        }

        public void RedrawChart() => Host?.RequestRedraw();
        public void RedrawChart(RedrawArg arg) => Host?.RequestRedraw();

        protected void SubscribeToDrawingEvents(DrawingLayouts layouts) => _layouts = layouts;

        protected void AddAlert(string soundFile, string instrument, string message, Color backgroundColor, Color foregroundColor) =>
            Host?.AddAlert(this, soundFile, instrument, message, backgroundColor, foregroundColor);

        protected void AddAlert(string soundFile, string message) =>
            Host?.AddAlert(this, soundFile, InstrumentInfo?.Instrument ?? "", message, Color.Black, Color.White);

        protected void DoActionInGuiThread(Action action)
        {
            var h = Host;
            if (h == null) action();
            else h.BeginInvoke(action);
        }

        protected void RaiseBarValueChanged(int bar) => Host?.RequestRedraw();

        protected void RequestForCumulativeTrades(CumulativeTradesRequest request) => Host?.RequestCumulativeTrades(this, request);

        /// <summary>Pide el perfil fijo de un período; llega en <see cref="OnFixedProfilesResponse"/>.</summary>
        protected void GetFixedProfile(FixedProfileRequest request) => Host?.RequestFixedProfile(this, request);

        protected void SubscribeToTimer(TimeSpan interval, Action action) => Host?.SubscribeTimer(this, interval, action);

        protected void UnsubscribeFromTimer(TimeSpan interval, Action action) => Host?.UnsubscribeTimer(this, interval, action);

        /// <summary>Registra un indicador hijo que comparte los datos del gráfico (se calcula con child.Calculate).</summary>
        protected void Add(Indicator child)
        {
            if (child == null || _children.Contains(child)) return;
            child._parent = this;
            _children.Add(child);
        }

        /// <summary>Calcula una barra "a mano" (indicadores usados como calculadoras dentro de otros).</summary>
        public decimal Calculate(int bar, decimal value)
        {
            if (_sourceSeries is ValueDataSeries own && _sourceSeries != _hostSource) own[bar] = value;
            else if (_sourceSeries == null) _recordedSource[bar] = value;
            OnCalculate(bar, value);
            return this[bar];
        }

        public DrawingText AddText(string tag, string text, bool isAbovePrice, int bar, decimal price,
            Color textColor, Color borderColor, Color fillColor, float fontSize, DrawingText.TextAlign align, bool autoSize = false) =>
            AddText(tag, text, isAbovePrice, bar, price, 0, 0, textColor, borderColor, fillColor, fontSize, align, autoSize);

        public DrawingText AddText(string tag, string text, bool isAbovePrice, int bar, decimal price, int yOffset, int xOffset,
            Color textColor, Color borderColor, Color fillColor, float fontSize, DrawingText.TextAlign align, bool autoSize = false)
        {
            var t = new DrawingText
            {
                Tag = tag,
                Text = text,
                IsAbovePrice = isAbovePrice,
                Bar = bar,
                TextPrice = price,
                YOffset = yOffset,
                XOffset = xOffset,
                Textcolor = textColor,
                BorderColor = borderColor,
                FillColor = fillColor,
                FontSize = fontSize <= 0 ? 10 : fontSize,
                Align = align,
                AutoSize = autoSize,
            };
            Labels[tag] = t;
            return t;
        }

        protected void AddHorizontalLineTillTouch(LineTillTouch line) => HorizontalLinesTillTouch.Add(line);

        protected void RemoveHorizontalLineTillTouchAt(int index)
        {
            if (index >= 0 && index < HorizontalLinesTillTouch.Count) HorizontalLinesTillTouch.RemoveAt(index);
        }

        protected void ClearHorizontalLinesTillTouch() => HorizontalLinesTillTouch.Clear();

        // ================================================================ propiedades / notificaciones

        public void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            OnPropertyChanged(propertyName);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected virtual void OnPropertyChanged(string propertyName)
        {
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(propertyName);
            RecalculateValues();
            return true;
        }

        protected bool SetProperty<T>(ref T field, T value, Action onChanged, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(propertyName);
            onChanged?.Invoke();
            return true;
        }

        /// <summary>Avisa que cambió una propiedad (nombre de ATAS).</summary>
        protected void OnChangeProperty([CallerMemberName] string propertyName = null) => RaisePropertyChanged(propertyName);

        protected void RaisePanelPropertyChanged(string propertyName) => RaisePropertyChanged(propertyName);

        /// <summary>Borra los valores de todas las series.</summary>
        protected void Clear()
        {
            foreach (var s in DataSeries) s.Clear();
        }

        protected void ReplaceHorizontalLineTillTouch(int index, LineTillTouch line)
        {
            if (index >= 0 && index < HorizontalLinesTillTouch.Count) HorizontalLinesTillTouch[index] = line;
        }

        /// <summary>Ventana de configuración abierta (Trading Hub no la expone a los indicadores).</summary>
        [Browsable(false)]
        [JsonIgnore]
        public IPropertiesEditor PropertiesEditor { get; set; }

        /// <summary>Hora actual del mercado (UTC).</summary>
        [Browsable(false)]
        [JsonIgnore]
        public DateTime MarketTime => DateTime.UtcNow;

        [Browsable(false)]
        [JsonIgnore]
        public int VisibleBarsCount => Math.Max(0, LastVisibleBarNumber - FirstVisibleBarNumber + 1);

        [Browsable(false)]
        [JsonIgnore]
        public bool IgnoreHistoryScale { get; set; }

        [Browsable(false)]
        [JsonIgnore]
        public bool ShowDescription { get; set; } = true;

        [Browsable(false)]
        [JsonIgnore]
        public bool SupportsExtendedSeries { get; set; }

        [Browsable(false)]
        [JsonIgnore]
        public bool SupportsCalculationTimeFrameSmoothing { get; set; }

        private readonly Dictionary<FixedProfilePeriods, TaskCompletionSource<FixedProfileResponse>> _pendingProfiles = new();

        /// <summary>Pide un perfil fijo y espera la respuesta.</summary>
        protected Task<FixedProfileResponse> RequestFixedProfileAsync(FixedProfileRequest request)
        {
            if (Host == null) return Task.FromResult<FixedProfileResponse>(null);
            var tcs = new TaskCompletionSource<FixedProfileResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingProfiles) _pendingProfiles[request.Period] = tcs;
            Host.RequestFixedProfile(this, request);
            return tcs.Task;
        }

        protected bool SetProperty<T>(ref T field, T value, Action<string> onChanged, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(propertyName);
            onChanged?.Invoke(propertyName);
            return true;
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }

        /// <summary>
        /// Asigna una propiedad y, si el valor notifica cambios (filtros, lápices...), se suscribe para
        /// llamar a <paramref name="onChanged"/> cuando cambie algo adentro.
        /// </summary>
        protected bool SetTrackedProperty<T>(ref T field, T value, Action<string> onChanged = null, [CallerMemberName] string propertyName = null)
        {
            if (ReferenceEquals(field, value) || EqualityComparer<T>.Default.Equals(field, value)) return false;
            if (field is INotifyPropertyChanged oldN && _tracked.Remove(oldN, out var oldH)) oldN.PropertyChanged -= oldH;
            field = value;
            if (value is INotifyPropertyChanged newN)
            {
                PropertyChangedEventHandler h = (_, e) =>
                {
                    if (onChanged != null) onChanged(e.PropertyName);
                    else RecalculateValues();
                };
                newN.PropertyChanged += h;
                _tracked[newN] = h;
            }

            RaisePropertyChanged(propertyName);
            onChanged?.Invoke(propertyName);
            return true;
        }

        public override string ToString() => Name;

        public virtual void Dispose()
        {
            try { OnDispose(); }
            catch (Exception ex) { this.LogError("Error disposing indicator", ex); }
        }

        // ================================================================ motor (lo llama el gráfico)

        internal void HostAttach(IIndicatorHost host)
        {
            _host = host;
            _hostSource = null;
        }

        internal void HostDetach()
        {
            Dispose();
            _host = null;
        }

        internal void HostInitialize(bool applyDefaultColors)
        {
            OnInitialize();
            foreach (var c in _children) c.HostInitialize(false);
            if (applyDefaultColors) OnApplyDefaultColors();
        }

        internal decimal SourceValue(int bar)
        {
            var h = Host;
            if (h == null || bar < 0 || bar >= h.BarCount) return 0;
            var c = h.GetCandle(bar);
            return SourceType switch
            {
                DataSeriesType.Open => c.Open,
                DataSeriesType.High => c.High,
                DataSeriesType.Low => c.Low,
                DataSeriesType.HL2 => (c.High + c.Low) / 2,
                DataSeriesType.HLC3 => (c.High + c.Low + c.Close) / 3,
                DataSeriesType.OHLC4 => (c.Open + c.High + c.Low + c.Close) / 4,
                DataSeriesType.Volume => c.Volume,
                DataSeriesType.Delta => c.Delta,
                DataSeriesType.Ticks => c.Ticks,
                DataSeriesType.Bid => c.Bid,
                DataSeriesType.Ask => c.Ask,
                _ => c.Close,
            };
        }

        private void ClearForRecalc()
        {
            foreach (var s in DataSeries) s.Clear();
            _recordedSource.Clear();
            foreach (var c in _children)
            {
                c.ClearForRecalc();
                c.OnRecalculate();
            }
        }

        /// <summary>Recalcula todas las barras.</summary>
        internal void HostRecalculate()
        {
            ClearForRecalc();
            OnRecalculate();
            int n = CurrentBar;
            for (int bar = 0; bar < n; bar++) OnCalculate(bar, SourceValue(bar));
            OnFinishRecalculate();
        }

        /// <summary>Calcula desde una barra hasta la última (tiempo real).</summary>
        internal void HostCalculate(int fromBar)
        {
            int n = CurrentBar;
            for (int bar = Math.Max(0, fromBar); bar < n; bar++) OnCalculate(bar, SourceValue(bar));
        }

        internal void HostRender(RenderContext context, DrawingLayouts layout) => OnRender(context, layout);
        internal void HostNewTrades(IEnumerable<MarketDataArg> trades) => OnNewTrades(trades);
        internal void HostBestBidAsk(MarketDataArg arg) => OnBestBidAskChanged(arg);
        internal void HostMarketDepths(IEnumerable<MarketDataArg> depths) => MarketDepthsChanged(depths);
        internal void HostCumulativeResponse(CumulativeTradesRequest req, IEnumerable<CumulativeTrade> trades) => OnCumulativeTradesResponse(req, trades);
        internal void HostCumulativeTrade(CumulativeTrade t) => OnCumulativeTrade(t);
        internal void HostUpdateCumulativeTrade(CumulativeTrade t) => OnUpdateCumulativeTrade(t);
        internal void HostNewMyTrade(MyTrade t) => OnNewMyTrade(t);
        internal void HostPositionChanged(Position p) => OnPositionChanged(p);
        internal void HostOrderChanged(Order o) => OnOrderChanged(o);

        /// <summary>Serie de entrada respaldada por las velas del gráfico.</summary>
        private sealed class CandleSourceSeries : IDataSeries
        {
            private readonly Indicator _owner;

            public CandleSourceSeries(Indicator owner) => _owner = owner;

            public event PropertyChangedEventHandler PropertyChanged
            {
                add { }
                remove { }
            }

            public string Id => "Source";
            public string Name { get; set; } = "Source";
            public int Count => _owner.CurrentBar;

            public object this[int index]
            {
                get => _owner.SourceValue(index);
                set { }
            }

            public bool IsHidden { get; set; } = true;
            public bool Visible { get; set; }
            public bool ScaleIt { get; set; }
            public bool DrawAbovePrice { get; set; }
            public string DescriptionKey { get; set; }
            public bool UseMinimizedModeIfEnabled { get; set; }
            public bool IgnoredByAlerts { get; set; }
            public Type ValueType => typeof(decimal);

            public void Clear()
            {
            }

            public void Resize(int count)
            {
            }
        }
    }
}
