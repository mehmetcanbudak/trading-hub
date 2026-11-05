using System.Drawing;
using System.Globalization;
using System.Reflection;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;
using IndicatorCandle = TradingHub.Indicators.IndicatorCandle;

namespace TradingHub.Charting;

/// <summary>Un indicador agregado al gráfico.</summary>
public sealed class IndicatorSlot
{
    internal IndicatorSlot(Indicator indicator)
    {
        Indicator = indicator;
        var t = indicator.GetType();
        WantsTrades = Overrides(t, "OnNewTrade") || Overrides(t, "OnNewTrades");
        WantsDepth = Overrides(t, "MarketDepthChanged") || Overrides(t, "MarketDepthsChanged");
        WantsDepthSnapshots = Overrides(t, "OnDepthSnapshot");
        WantsDepthHistory = Overrides(t, "OnDepthHistoryChanged");
        WantsBestBidAsk = Overrides(t, "OnBestBidAskChanged");
        WantsMyTrades = Overrides(t, "OnNewMyTrade");
    }

    public Indicator Indicator { get; }
    public SubPanel? Panel { get; internal set; }
    public bool Visible { get; set; } = true;
    public string? Error { get; internal set; }
    internal bool NeedsRecalc = true;
    internal bool Initialized;
    internal bool WantsTrades, WantsDepth, WantsBestBidAsk, WantsMyTrades;
    internal bool WantsDepthSnapshots, WantsDepthHistory;
    internal bool WantsCumulative;
    internal List<CumulativeTradesRequest> PendingCumulative { get; } = new();

    private static bool Overrides(Type t, string method)
    {
        for (var c = t; c != null && c != typeof(Indicator); c = c.BaseType)
        {
            if (c.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly).Any(m => m.Name == method))
                return true;
        }

        return false;
    }

    public override string ToString() => Indicator.Name;
}

/// <summary>Panel de indicadores debajo del gráfico de precio.</summary>
public sealed class SubPanel
{
    public List<IndicatorSlot> Slots { get; } = new();
    public double HeightRatio { get; set; } = 0.2;
}

public sealed record AlertInfo(DateTime Time, string Instrument, string Message, string SoundFile, Color Background, Color Foreground, string Source);

/// <summary>Una entrada o salida de una estrategia puesta en el gráfico.</summary>
public readonly record struct StrategySignal(DateTime Time, decimal Price, bool Buy, bool Entry, string Label);

/// <summary>
/// Una estrategia puesta en el gráfico como un indicador: marca dónde entra y dónde sale (lo histórico y lo nuevo).
/// La app la corre (solo visual o ejecutando en una cuenta) y deja acá lo que hay que dibujar.
/// </summary>
public sealed class StrategyOverlay
{
    public string Name { get; set; } = "";

    /// <summary>"visual", "en vivo · cuenta", "calculando…"</summary>
    public string Status { get; set; } = "";

    public bool Visible { get; set; } = true;
    public bool Live { get; set; }
    public string? Error { get; set; }
    public SkiaSharp.SKColor Color { get; set; } = new(180, 120, 255);
    public IReadOnlyList<StrategySignal> Signals { get; set; } = Array.Empty<StrategySignal>();
    public IReadOnlyList<TradeLink> Links { get; set; } = Array.Empty<TradeLink>();

    /// <summary>Lo que dibuja la estrategia: rangos, líneas, niveles, marcas y textos.</summary>
    public IReadOnlyList<StrategyShape> Shapes { get; set; } = Array.Empty<StrategyShape>();

    /// <summary>Líneas continuas de la estrategia (un valor por vela).</summary>
    public IReadOnlyList<StrategyLine> Lines { get; set; } = Array.Empty<StrategyLine>();

    /// <summary>Objeto de la app que la maneja.</summary>
    public object? Tag { get; set; }
}

public enum StrategyShapeKind
{
    Line,
    Box,
    HorizontalLine,
    Marker,
    Text,
}

public enum StrategyMarker
{
    ArrowUp,
    ArrowDown,
    Circle,
    Square,
    Diamond,
    Cross,
}

/// <summary>Un dibujo de una estrategia listo para pintar. Dash: 0 continua, 1 rayada, 2 punteada.</summary>
public sealed record StrategyShape(StrategyShapeKind Kind, DateTime Time1, decimal Price1, DateTime Time2, decimal Price2, SkiaSharp.SKColor Color,
    byte FillAlpha, float Width, int Dash, StrategyMarker Marker, string Text, bool ExtendRight);

/// <summary>Una línea continua de una estrategia (null corta la línea).</summary>
public sealed record StrategyLine(string Name, SkiaSharp.SKColor Color, float Width, int Dash, IReadOnlyList<(DateTime Time, decimal? Value)> Points);

/// <summary>Una ejecución propia para marcar en el gráfico.</summary>
public readonly record struct TradeMark(DateTime Time, decimal Price, bool Buy, decimal Quantity);

/// <summary>Una operación cerrada (entrada → salida) para unirla con una línea en el gráfico.</summary>
public readonly record struct TradeLink(DateTime OpenTime, decimal OpenPrice, DateTime CloseTime, decimal ClosePrice, bool IsLong, decimal Pnl);

/// <summary>
/// Operación resaltada (al elegirla en una lista de operaciones): recuadro de la entrada a la salida, marcas, el peor y
/// el mejor precio por el que pasó (MAE y MFE) y una etiqueta con el detalle.
/// </summary>
public sealed record TradeFocus(DateTime EntryTime, decimal EntryPrice, DateTime ExitTime, decimal ExitPrice, bool IsLong, decimal Quantity, decimal Pnl, string Label);

/// <summary>
/// Estado completo de un gráfico: datos, velas, indicadores, configuración y navegación.
/// Implementa <see cref="IIndicatorHost"/> para que los indicadores accedan a las velas y al gráfico.
/// Todo se usa desde el hilo de UI.
/// </summary>
public sealed class ChartModel : IIndicatorHost, IIndicatorDataProvider, IDisposable
{
    private readonly List<IndicatorCandle> _wrappers = new();
    private readonly List<(Indicator Ind, TimeSpan Interval, Action Action, DateTime Next)> _timers = new();
    private readonly Queue<Action> _invokes = new();
    private int? _autoStep;
    private CumulativeTrade? _openCumulative;
    private long _lastBestBid = long.MinValue, _lastBestAsk = long.MaxValue;
    private bool _lastBestAvailable;
    private DateTime AvailabilityClock => Data.Feed is TradingHub.Feeds.ReplayFeed replay ? replay.Clock : Data.Feed.Now;
    private string CandleCacheScope => Data.Feed is TradingHub.Feeds.IDataScopeFeed scoped ? scoped.DataScopeKey : Data.Feed.Id;
    private readonly ChartInfo _chartInfo;
    private readonly InstrumentInfo _instrumentInfo = new();
    private readonly MarketDepthInfo _depthInfo;
    private sealed record DepthLoadPublication(long Request, DepthSelectionResult Result, string? Error = null);
    private DepthLoadPublication? _depthPublication;
    private CancellationTokenSource? _depthLoadCancellation;
    private Task? _depthLoadTask;
    private (DateTime From, DateTime To)? _depthRequestedRange;
    private readonly DepthHistorySelection _depthSelection;
    private string? _depthSelectionError;
    private long _depthRequest, _depthObservedVersion = -1;
    private volatile bool _disposed;

    public ChartModel(InstrumentData data, ChartPeriod period, ChartSettings? settings = null)
    {
        Data = data;
        _depthSelection = data.DepthHistory.CreateSelection();
        Period = period;
        Settings = settings ?? new ChartSettings();
        Theme = ChartTheme.With(Settings.Colors);
        Series = new CandleSeries(data.Instrument, period, 1, (data.Feed as TradingHub.Feeds.ISessionSettingsFeed)?.GetSessionSettings(data.Instrument));
        _chartInfo = new ChartInfo(() => (IChartContainer?)LastLayout?.Main ?? EmptyContainer.Instance);
        _depthInfo = new MarketDepthInfo(DepthSnapshotArgs, () => SumDepth(BookSide.Ask), () => SumDepth(BookSide.Bid),
            () => Data.DepthHistory.Current, () => Data.DepthHistory.Status, GetDepthHistory);

        data.TradesAppended += OnTradesAppended;
        data.HistoryLoaded += OnHistoryLoaded;
        data.DepthUpdated += OnDepthUpdated;
        data.DepthHistoryChanged += OnDepthHistoryChanged;
        data.StatusChanged += OnStatusChanged;
        data.OpenInterestChanged += OnOpenInterestChanged;
        Rebuild();
    }

    public InstrumentData Data { get; }
    public Instrument Instrument => Data.Instrument;
    /// <summary>Changes for every rebuild, append and correction, even when candle count is unchanged.</summary>
    public long DataGeneration { get; private set; }
    public string SourceIdentity => $"{Instrument.FeedId}/{Instrument.Symbol}/{Instrument.TickSize.ToString(CultureInfo.InvariantCulture)}";

    public string DepthStatusText
    {
        get
        {
            var status = Data.DepthHistory.Status;
            var current = Data.DepthHistory.Current;
            string source = current == null ? "" : $" · {current.ProviderId}/{current.Venue}";
            string availability = status.IsStale ? "missing or stale" : "received L2 book";
            string text = $"Depth: {status.RecordingState} · {availability}{source}";
            if ((status.Coverage & (DepthCoverage.PartialBook | DepthCoverage.Truncated)) != 0) text += " · partial depth coverage";
            if (status.DroppedRecords > 0) text += $" · {status.DroppedRecords} unrecorded updates";
            if (!string.IsNullOrWhiteSpace(status.Message)) text += " · " + status.Message;
            if (IsLoadingDepthHistory) text += " · loading selected recorded depth";
            else if ((_depthSelectionError ?? _depthSelection.Message) is { } historyMessage) text += " · " + historyMessage;
            var memory = Data.DepthHistory.MemoryUsage;
            double estimated = (memory.LiveBytes + memory.HistoricalBytes + memory.SelectedBytes + memory.QueuedBytes + memory.WorkingReadBytes + Data.QueuedDepthBytes) / (1024d * 1024);
            double limit = (memory.LiveLimit + memory.HistoricalLimit + memory.SelectedLimit + memory.QueueLimit + memory.ReadLimit + Data.DepthQueueByteLimit) / (1024d * 1024);
            text += $" · estimated depth RAM {estimated:0.#}/{limit:0.#} MiB · disk cap {Data.DepthHistory.Options.MaxDiskBytes / (1024 * 1024)} MiB · retention up to {Data.DepthHistory.Options.RetentionDays}d (disk cap and update rate may shorten it)";
            return text;
        }
    }
    public bool IsAggregating => Series.HasPendingTrades(Data.Trades);
    public double AggregationProgress => Data.Trades.Count == 0 ? 1 : Math.Clamp((double)Series.ProcessedTrades / Data.Trades.Count, 0, 1);
    public bool IsLoadingDepthHistory { get; private set; }
    public long DepthDataVersion => Data.DepthHistory.DataVersion + _depthSelection.Version;
    public ChartPeriod Period { get; private set; }
    public ChartSettings Settings { get; }
    public ChartViewport Viewport { get; } = new();
    public ChartTheme Theme { get; set; } = ChartTheme.Base;
    public CandleSeries Series { get; private set; }
    public List<IndicatorSlot> Indicators { get; } = new();
    public List<SubPanel> SubPanels { get; } = new();
    /// <summary>Dibujos del gráfico. Los gráficos del mismo instrumento comparten la misma lista (se sincronizan).</summary>
    public List<Drawings.ChartDrawing> Drawings { get; set; } = new();

    /// <summary>Gestor de trading (broker simulado) — opcional.</summary>
    public TradingManager? Trading { get; set; }

    /// <summary>Cuenta con la que se opera desde el gráfico (órdenes y posición dibujadas).</summary>
    public ITradingAccount? Account { get; set; }

    /// <summary>Estrategias puestas en el gráfico (aparecen en la leyenda y marcan sus operaciones).</summary>
    public List<StrategyOverlay> Strategies { get; } = new();

    /// <summary>Ejecuciones propias en este instrumento (se marcan en el gráfico).</summary>
    public IReadOnlyList<TradeMark> TradeMarks { get; set; } = Array.Empty<TradeMark>();

    /// <summary>Operaciones cerradas: línea de la entrada a la salida.</summary>
    public IReadOnlyList<TradeLink> TradeLinks { get; set; } = Array.Empty<TradeLink>();

    /// <summary>Operación resaltada (null = ninguna).</summary>
    public TradeFocus? FocusTrade { get; set; }

    /// <summary>
    /// Centra la vista en un intervalo de tiempo (con aire a los costados) y, si no entra, achica las velas. Usa el
    /// ancho del último cuadro dibujado (o uno típico si todavía no se dibujó).
    /// </summary>
    public void CenterOn(DateTime fromUtc, DateTime toUtc)
    {
        var series = Series;
        if (series.Count == 0) return;
        int b0 = Math.Max(0, series.FindBar(fromUtc)), b1 = Math.Max(b0, series.FindBar(toUtc));
        double plot = LastLayout is { } l ? l.Plot.Width : 900;
        var vp = Viewport;
        double span = b1 - b0 + 1;
        double needed = Math.Max(30, span * 1.8 + 10);
        if (needed * vp.BarWidth > plot) vp.BarWidth = Math.Clamp(plot / needed, ChartViewport.MinBarWidth, ChartViewport.MaxBarWidth);
        double mid = (b0 + b1) / 2.0;
        double scroll = series.Count - 1 - mid + vp.RightMarginBars + 0.5 - plot / 2 / vp.BarWidth;
        vp.ScrollBars = Math.Max(0, scroll);
        vp.AutoScale = true;
        Invalidate();
    }

    /// <summary>Stop loss / objetivos de la cuenta (para dibujarlos y arrastrarlos desde la posición).</summary>
    public BracketManager? Brackets { get; set; }

    public TradingStatisticsProvider Statistics { get; set; } = new();

    /// <summary>Distribución del último cuadro dibujado.</summary>
    public ChartLayout? LastLayout { get; internal set; }

    public bool IsDirty { get; private set; } = true;

    public event Action? Invalidated;
    public event Action<AlertInfo>? AlertRaised;

    /// <summary>Vuelve a armar los colores con los de este gráfico (después de cambiarlos en su configuración).</summary>
    public void ApplyColors()
    {
        Theme = ChartTheme.With(Settings.Colors);
        Invalidate();
    }

    public void Invalidate()
    {
        IsDirty = true;
        Invalidated?.Invoke();
    }

    internal void ClearDirty() => IsDirty = false;

    public string Title => $"{Instrument.Symbol} · {Period.DisplayName}";

    // =================================================================== construcción

    public void SetPeriod(ChartPeriod period)
    {
        if (period == Period) return;
        Period = period;
        _autoStep = null;
        Rebuild();
    }

    /// <summary>Ticks por fila que se muestran en el footprint (0 = automático según el zoom).</summary>
    public void SetPriceStep(int step)
    {
        Settings.PriceStep = Math.Max(0, step);
        int baseStep = BaseStepFor(Settings.PriceStep);
        if (baseStep != Series.PriceStep) Rebuild();
        else Invalidate();
    }

    /// <summary>Ticks por fila con que se construyen las velas (resolución base del footprint).</summary>
    public int EffectivePriceStep => Series.PriceStep;

    /// <summary>
    /// Paso base: fino (≈150 filas por vela típica) para poder acercar el zoom sin reconstruir.
    /// Si el usuario fija un paso, el base es el mayor divisor de ese paso que no supere al automático.
    /// </summary>
    private int BaseStepFor(int userStep)
    {
        if (_autoStep == null && Data.Trades.Count > 100) _autoStep = CandleSeries.SuggestPriceStep(Data.Trades, Instrument, Period, targetRows: 150);
        int auto = _autoStep ?? 1;
        if (userStep <= 0) return auto;
        if (userStep <= auto) return userStep;
        for (int d = auto; d >= 1; d--)
            if (userStep % d == 0) return d;
        return 1;
    }

    /// <summary>Reconstruye las velas desde las operaciones y recalcula los indicadores.</summary>
    public void Rebuild()
    {
        DataGeneration++;
        int step = BaseStepFor(Settings.PriceStep);
        Series = new CandleSeries(Instrument, Period, step, (Data.Feed as TradingHub.Feeds.ISessionSettingsFeed)?.GetSessionSettings(Instrument));
        if (!CandleSeriesCache.Shared.TryRestore(Series, Data.Trades, CandleCacheScope))
            Series.BeginRebuild(Data.Trades);
        Series.Append(Data.Trades, CandleSeries.InitialBatchSize);
        Series.AdvanceAvailability(AvailabilityClock);
        if (!Series.HasPendingTrades(Data.Trades)) CandleSeriesCache.Shared.Store(Series, Data.Trades, CandleCacheScope);
        Data.OpenInterest.Apply(Series, Data.Feed.Now);
        _wrappers.Clear();
        for (int i = 0; i < Series.Count; i++) _wrappers.Add(new IndicatorCandle(Series[i], Series));
        UpdateInfos();
        foreach (var s in Indicators) s.NeedsRecalc = true;
        _openCumulative = null;
        if (Indicators.Any(s => s.WantsDepthHistory) && Series.Count > 0)
            EnsureDepthHistory(GetDepthInterval(0).From, GetDepthInterval(Series.Count - 1).To);
        ProcessPending();
        Invalidate();
    }

    private void UpdateInfos()
    {
        _instrumentInfo.Instrument = Instrument.Symbol;
        _instrumentInfo.Exchange = Instrument.Exchange;
        _instrumentInfo.TickSize = Series.RowSize;
        _instrumentInfo.TickCost = Instrument.PointValue * Series.RowSize;
        _instrumentInfo.LotSize = Instrument.VolumeStep;
        _instrumentInfo.Description = Instrument.Description;
        _instrumentInfo.TimeZoneOffset = Settings.TimeZone.GetUtcOffset(DateTime.UtcNow);

        _chartInfo.TimeFrame = Period.Code;
        _chartInfo.ChartType = Period.Type switch
        {
            PeriodType.Seconds => "Seconds",
            PeriodType.Tick => "Tick",
            PeriodType.Volume => "Volume",
            PeriodType.Range => "Range",
            PeriodType.Delta => "Delta",
            PeriodType.Renko => "Renko",
            _ => "TimeFrame",
        };
        _chartInfo.ChartVisualMode = Settings.Style switch
        {
            ChartStyle.Footprint => ChartVisualModes.Clusters,
            ChartStyle.Line => ChartVisualModes.Line,
            ChartStyle.Bars => ChartVisualModes.Bars,
            _ => ChartVisualModes.Candles,
        };
        int dec = Instrument.DecimalPlaces(Series.RowSize);
        _chartInfo.StringFormat = "{0:F" + dec + "}";
        _chartInfo.VolumeDecimals = Instrument.VolumeDecimals;
        var c = _chartInfo.ColorsStore;
        c.UpCandleColor = ToColor(Theme.UpCandle);
        c.DownCandleColor = ToColor(Theme.DownCandle);
        c.FootprintAskColor = ToColor(Theme.Ask);
        c.FootprintBidColor = ToColor(Theme.Bid);
        c.BaseBackgroundColor = ToColor(Theme.BackgroundBottom);
        c.AxisTextColor = ToColor(Theme.AxisText);
    }

    private static Color ToColor(SkiaSharp.SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

    // =================================================================== indicadores

    public IndicatorSlot AddIndicator(Indicator indicator, bool applyDefaultColors = true)
    {
        var slot = new IndicatorSlot(indicator);
        indicator.HostAttach(this);
        if (indicator.Panel == IndicatorDataProvider.NewPanel)
        {
            var panel = new SubPanel();
            panel.Slots.Add(slot);
            slot.Panel = panel;
            SubPanels.Add(panel);
        }

        Indicators.Add(slot);
        try
        {
            indicator.HostInitialize(applyDefaultColors);
            slot.Initialized = true;
        }
        catch (Exception ex)
        {
            slot.Error = ex.Message;
            this.LogError($"Initialization error in {indicator.Name}", ex);
        }

        indicator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Indicator.Panel)) MoveToPanelByName(slot, indicator.Panel);
        };
        slot.NeedsRecalc = true;
        if (slot.WantsDepthHistory && Series.Count > 0)
            EnsureDepthHistory(GetDepthInterval(0).From, GetDepthInterval(Series.Count - 1).To);
        ProcessPending();
        Invalidate();
        return slot;
    }

    /// <summary>Agrega el indicador a un panel existente (para superponer indicadores).</summary>
    public void MoveToPanel(IndicatorSlot slot, SubPanel? target)
    {
        if (slot.Panel == target) return;
        if (slot.Panel != null)
        {
            slot.Panel.Slots.Remove(slot);
            if (slot.Panel.Slots.Count == 0) SubPanels.Remove(slot.Panel);
        }

        slot.Panel = target;
        if (target != null)
        {
            target.Slots.Add(slot);
            if (!SubPanels.Contains(target)) SubPanels.Add(target);
        }

        Invalidate();
    }

    private void MoveToPanelByName(IndicatorSlot slot, string panel)
    {
        if (panel == IndicatorDataProvider.NewPanel && slot.Panel == null) MoveToPanel(slot, new SubPanel());
        else if (panel == IndicatorDataProvider.CandlesPanel && slot.Panel != null) MoveToPanel(slot, (SubPanel?)null);
    }

    public void RemoveIndicator(IndicatorSlot slot)
    {
        Indicators.Remove(slot);
        if (slot.Panel != null)
        {
            slot.Panel.Slots.Remove(slot);
            if (slot.Panel.Slots.Count == 0) SubPanels.Remove(slot.Panel);
        }

        _timers.RemoveAll(t => t.Ind == slot.Indicator);
        try { slot.Indicator.HostDetach(); }
        catch (Exception ex) { this.LogError("Error removing indicator", ex); }
        Invalidate();
    }

    /// <summary>Procesa recálculos, timers y pedidos pendientes. Llamar en cada tick de UI.</summary>
    public void Update()
    {
        if (_disposed) return;
        var selection = Interlocked.Exchange(ref _depthPublication, null);
        if (selection != null && selection.Request == _depthRequest)
        {
            _depthSelectionError = selection.Error;
            IsLoadingDepthHistory = false;
        }
        long depthVersion = DepthDataVersion;
        if (_depthObservedVersion != depthVersion)
        {
            _depthObservedVersion = depthVersion;
            OnDepthHistoryChanged();
        }
        ContinueAggregation();
        if (Series.AdvanceAvailability(AvailabilityClock))
            foreach (var slot in Indicators) slot.NeedsRecalc = true;
        RefreshFixedProfiles();
        while (_invokes.Count > 0)
        {
            var a = _invokes.Dequeue();
            try { a(); }
            catch (Exception ex) { this.LogError("Deferred action error", ex); }
        }

        ProcessPending();

        if (_timers.Count > 0)
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < _timers.Count; i++)
            {
                var t = _timers[i];
                if (now < t.Next) continue;
                _timers[i] = (t.Ind, t.Interval, t.Action, now + t.Interval);
                try { t.Action(); }
                catch (Exception ex) { this.LogError("Indicator timer error", ex); }
            }
        }
    }

    private void ContinueAggregation()
    {
        if (!Series.HasPendingTrades(Data.Trades)) return;
        long correction = Series.CorrectionRevision;
        var result = Series.Append(Data.Trades, CandleSeries.InitialBatchSize);
        Series.AdvanceAvailability(AvailabilityClock);
        bool rebuilt = Series.CorrectionRevision != correction;
        if (rebuilt) { _wrappers.Clear(); _openCumulative = null; }
        for (int i = _wrappers.Count; i < Series.Count; i++) _wrappers.Add(new IndicatorCandle(Series[i], Series));
        if (!result.Changed && !rebuilt) return;
        DataGeneration++;
        int first = Math.Max(0, result.FirstChangedBar);
        Data.OpenInterest.Apply(Series, Data.Feed.Now, first);
        foreach (var slot in Indicators)
        {
            if (rebuilt) slot.NeedsRecalc = true;
            else if (!slot.NeedsRecalc) Safe(slot, () => slot.Indicator.HostCalculate(first));
        }
        if (!Series.HasPendingTrades(Data.Trades)) CandleSeriesCache.Shared.Store(Series, Data.Trades, CandleCacheScope);
        if (!Series.HasPendingTrades(Data.Trades) && Series.Count > 0 && Indicators.Any(s => s.WantsDepthHistory))
            EnsureDepthHistory(GetDepthInterval(0).From, GetDepthInterval(Series.Count - 1).To);
        Invalidate();
    }

    private void ProcessPending()
    {
        bool cumulativeReady = !Series.HasPendingTrades(Data.Trades);
        foreach (var slot in Indicators.ToArray())
        {
            if (slot.NeedsRecalc)
            {
                slot.NeedsRecalc = false;
                Safe(slot, () => slot.Indicator.HostRecalculate());
                UpdateLinesTillTouch(slot.Indicator, 0);
                Invalidate();
            }

            if (slot.PendingCumulative.Count > 0 && cumulativeReady)
            {
                var reqs = slot.PendingCumulative.ToArray();
                slot.PendingCumulative.Clear();
                foreach (var r in reqs)
                {
                    var list = BuildCumulativeTrades(r.BeginTime, r.EndTime);
                    Safe(slot, () => slot.Indicator.HostCumulativeResponse(r, list));
                }

                slot.WantsCumulative = true;
                Invalidate();
            }
        }
    }

    private void Safe(IndicatorSlot slot, Action a)
    {
        try
        {
            a();
            slot.Error = null;
        }
        catch (Exception ex)
        {
            if (slot.Error == null) this.LogError($"Indicator error in {slot.Indicator.Name}: {ex.Message}", ex);
            slot.Error = ex.Message;
        }
    }

    // =================================================================== datos en vivo

    private void OnHistoryLoaded()
    {
        _autoStep = null;
        Rebuild();
    }
    private void OnStatusChanged() => Invalidate();

    private void OnOpenInterestChanged()
    {
        int from = Math.Max(0, Series.Count - 1);
        if (Data.OpenInterest.ChangedFrom is { } time)
        {
            from = Math.Max(0, Series.FindBar(time));
            // Volume/tick bars can share a trade timestamp; all such intervals must see the correction.
            while (from > 0 && Series[from - 1].LastTime >= time) from--;
        }
        int first = Data.OpenInterest.Apply(Series, Data.Feed.Now, from);
        if (first >= 0) DataGeneration++;
        foreach (var slot in Indicators)
        {
            if (slot.NeedsRecalc) continue;
            Safe(slot, () => slot.Indicator.HostCalculate(first >= 0 ? first : Math.Max(0, Series.Count - 1)));
        }
        Invalidate();
    }

    private void OnTradesAppended(int start, int count)
    {
        long correction = Series.CorrectionRevision;
        var res = Series.Append(Data.Trades, CandleSeries.InitialBatchSize);
        Series.AdvanceAvailability(AvailabilityClock);
        bool rebuilt = correction != Series.CorrectionRevision;
        if (rebuilt)
        {
            _openCumulative = null;
            _wrappers.Clear();
            foreach (var slot in Indicators) slot.NeedsRecalc = true;
        }
        for (int i = _wrappers.Count; i < Series.Count; i++) _wrappers.Add(new IndicatorCandle(Series[i], Series));
        if (!res.Changed) return;
        DataGeneration++;
        Data.OpenInterest.Apply(Series, Data.Feed.Now, res.FirstChangedBar);

        // ticks para los indicadores que los piden
        List<MarketDataArg>? args = null;
        bool anyCum = Indicators.Any(s => s.WantsCumulative);
        if (Indicators.Any(s => s.WantsTrades) || anyCum)
        {
            args = new List<MarketDataArg>(count);
            for (int i = start; i < start + count; i++)
            {
                var side = Data.Trades.GetSide(i);
                args.Add(new MarketDataArg
                {
                    Id = Data.Trades.GetId(i),
                    Time = Data.Trades.GetTime(i),
                    Price = Data.Trades.GetPrice(i),
                    Volume = Data.Trades.GetVolume(i),
                    Direction = side == TradeSide.Buy ? TradeDirection.Buy : side == TradeSide.Sell ? TradeDirection.Sell : TradeDirection.Between,
                    DataType = MarketDataType.Trade,
                    Security = Instrument.Symbol,
                    Provenance = Data.Trades.GetProvenance(i),
                });
            }
        }

        foreach (var slot in Indicators)
        {
            if (slot.NeedsRecalc) continue;
            Safe(slot, () => slot.Indicator.HostCalculate(res.FirstChangedBar));
            if (slot.WantsTrades && args != null) Safe(slot, () => slot.Indicator.HostNewTrades(args));
            UpdateLinesTillTouch(slot.Indicator, res.FirstChangedBar);
        }

        if (anyCum && args != null) FeedCumulative(args);
        Invalidate();
    }

    private void OnDepthUpdated(DepthUpdate update)
    {
        var snap = update.Snapshot;
        bool available = update.IsUsable && AvailabilityClock - update.ReceivedAt <= Data.DepthHistory.Options.MaxAge;
        bool bestChanged = snap.BestBidTicks != _lastBestBid || snap.BestAskTicks != _lastBestAsk || available != _lastBestAvailable;
        _lastBestBid = snap.BestBidTicks;
        _lastBestAsk = snap.BestAskTicks;
        _lastBestAvailable = available;

        foreach (var slot in Indicators)
            if (slot.WantsDepthSnapshots && slot.Initialized)
                Safe(slot, () => slot.Indicator.HostDepthSnapshot(update));

        if (Indicators.Any(s => s.WantsDepth) && (update.Changes.Count > 0 || update.IsFullRefresh))
        {
            var args = update.IsFullRefresh ? DepthSnapshotArgs(update).ToList() : update.Changes.Select(c => new MarketDataArg
            {
                Time = update.ReceivedAt,
                Price = Instrument.FromTicks(c.PriceTicks),
                Volume = c.Size,
                DataType = c.Side == BookSide.Bid ? MarketDataType.Bid : MarketDataType.Ask,
                Security = Instrument.Symbol, IsAvailable = available,
                DepthCoverage = update.Coverage, ProviderId = update.ProviderId, Venue = update.Venue,
            }).ToList();
            foreach (var slot in Indicators)
                if (slot.WantsDepth && !slot.NeedsRecalc) Safe(slot, () => slot.Indicator.HostMarketDepths(args));
        }

        if (bestChanged) NotifyBestQuotes(update, available);
        Invalidate();
    }

    private void NotifyBestQuotes(DepthUpdate update, bool available)
    {
        var snapshot = update.Snapshot;
        var bid = new MarketDataArg { Time = update.ReceivedAt, Price = snapshot.HasBid ? Instrument.FromTicks(snapshot.BestBidTicks) : 0,
            Volume = available ? snapshot.BestBidSize : 0, DataType = MarketDataType.Bid, IsAvailable = available && snapshot.HasBid,
            DepthCoverage = update.Coverage, ProviderId = update.ProviderId, Venue = update.Venue };
        var ask = new MarketDataArg { Time = update.ReceivedAt, Price = snapshot.HasAsk ? Instrument.FromTicks(snapshot.BestAskTicks) : 0,
            Volume = available ? snapshot.BestAskSize : 0, DataType = MarketDataType.Ask, IsAvailable = available && snapshot.HasAsk,
            DepthCoverage = update.Coverage, ProviderId = update.ProviderId, Venue = update.Venue };
        foreach (var slot in Indicators)
            if (slot.WantsBestBidAsk && slot.Initialized)
            {
                Safe(slot, () => slot.Indicator.HostBestBidAsk(bid));
                Safe(slot, () => slot.Indicator.HostBestBidAsk(ask));
            }
    }

    private void OnDepthHistoryChanged()
    {
        if (_lastBestAvailable && Data.DepthHistory.Current == null)
        {
            _lastBestAvailable = false;
            NotifyBestQuotes(new DepthUpdate { Snapshot = DepthSnapshot.Empty, ReceivedAt = AvailabilityClock,
                Coverage = Data.DepthHistory.Status.Coverage | DepthCoverage.Missing, ProviderId = Instrument.FeedId }, false);
        }
        foreach (var slot in Indicators)
            if (slot.WantsDepthHistory && !slot.NeedsRecalc)
                Safe(slot, () => slot.Indicator.HostDepthHistoryChanged());
        Invalidate();
    }

    private IEnumerable<MarketDataArg> DepthSnapshotArgs()
    {
        var current = Data.DepthHistory.Current;
        if (current == null) yield break;
        foreach (var arg in DepthSnapshotArgs(current.Update)) yield return arg;
    }

    private IEnumerable<MarketDataArg> DepthSnapshotArgs(DepthUpdate update)
    {
        foreach (var b in update.Snapshot.Bids) yield return DepthArg(update, b, BookSide.Bid);
        foreach (var a in update.Snapshot.Asks) yield return DepthArg(update, a, BookSide.Ask);
    }

    private MarketDataArg DepthArg(DepthUpdate update, BookLevel level, BookSide side) => new()
    {
        Price = Instrument.FromTicks(level.PriceTicks), Volume = level.Size,
        DataType = side == BookSide.Bid ? MarketDataType.Bid : MarketDataType.Ask,
        Time = update.ReceivedAt, Security = Instrument.Symbol, DepthCoverage = update.Coverage,
        ProviderId = update.ProviderId, Venue = update.Venue, IsAvailable = update.IsUsable,
    };

    private decimal SumDepth(BookSide side)
    {
        var current = Data.DepthHistory.Current;
        if (current == null) return 0;
        decimal sum = 0;
        foreach (var level in side == BookSide.Bid ? current.Snapshot.Bids : current.Snapshot.Asks) sum += level.Size;
        return sum;
    }

    public DepthHistoryQuery GetDepthHistory(DateTime fromUtc, DateTime toUtc) => _depthSelection.Query(fromUtc, toUtc);

    /// <summary>
    /// Requests the selected visible interval from disk without network access or replacing
    /// another chart's selection. One owned cancellable reader runs at a time; newer requests
    /// supersede older publication. Minute rounding avoids issuing one file read per frame.
    /// </summary>
    public void EnsureDepthHistory(DateTime fromUtc, DateTime toUtc)
    {
        if (_disposed || Data.RecordingRoot == null || toUtc <= fromUtc) return;
        var from = new DateTime(fromUtc.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        long endTicks = toUtc.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute;
        if (endTicks < toUtc.Ticks && endTicks <= DateTime.MaxValue.Ticks - TimeSpan.TicksPerMinute) endTicks += TimeSpan.TicksPerMinute;
        var to = new DateTime(endTicks, DateTimeKind.Utc);
        if (_depthRequestedRange == (from, to)) return;
        _depthRequestedRange = (from, to);
        _depthLoadCancellation?.Cancel();
        _depthLoadCancellation?.Dispose();
        _depthLoadCancellation = new CancellationTokenSource();
        long request = ++_depthRequest;
        IsLoadingDepthHistory = true;
        _depthLoadTask = ReadDepthSelectionAsync(_depthLoadTask ?? Task.CompletedTask, request, from, to, _depthLoadCancellation.Token);
    }

    private async Task ReadDepthSelectionAsync(Task previous, long request, DateTime from, DateTime to, CancellationToken ct)
    {
        try
        {
            await previous.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var result = await _depthSelection.LoadAsync(from, to, ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested && !_disposed && request == Volatile.Read(ref _depthRequest))
                Interlocked.Exchange(ref _depthPublication, new(request, result));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && !_disposed && request == Volatile.Read(ref _depthRequest))
                Interlocked.Exchange(ref _depthPublication, new(request, new(_depthSelection.Version, null, true), "Selected depth history is unavailable: " + ex.Message));
        }
    }


    public DepthHistoryQuery GetDepthHistory(int bar)
    {
        var (from, to) = GetDepthInterval(bar);
        return GetDepthHistory(from, to);
    }

    /// <summary>
    /// Time bars use the exchange-session calendar bucket, not approximate months. Non-time
    /// bars use consecutive availability timestamps; equal-start bars have no unique interval.
    /// </summary>
    public (DateTime From, DateTime To) GetDepthInterval(int bar)
    {
        if (bar < 0 || bar >= Series.Count) return (DateTime.MinValue, DateTime.MinValue);
        var from = Series[bar].Time;
        DateTime to;
        if (Period.IsTimeBased)
            to = new DateTime(Series.TimeBucket(from.Ticks).End, DateTimeKind.Utc);
        else if (bar + 1 < Series.Count)
            to = Series[bar + 1].Time;
        else
            to = Series[bar].LastTime < AvailabilityClock ? AvailabilityClock : Series[bar].LastTime;
        if (!Series[bar].IsClosed && AvailabilityClock < to) to = AvailabilityClock;
        return (from, to < from ? from : to);
    }

    // =================================================================== operaciones acumuladas

    private List<CumulativeTrade> BuildCumulativeTrades(DateTime from, DateTime to)
    {
        var res = new List<CumulativeTrade>();
        var store = Data.Trades;
        int processed = Math.Min(Series.ProcessedTrades, store.Count);
        int i = store.LowerBound(from);
        CumulativeTrade? cur = null;
        long curTime = 0;
        for (; i < processed; i++)
        {
            long t = store.GetTimeTicks(i);
            if (to != DateTime.MaxValue && t > to.Ticks) break;
            if (!store.GetProvenance(i).IsObservedTrade) continue;
            var side = store.GetSide(i);
            var dir = side == TradeSide.Buy ? TradeDirection.Buy : side == TradeSide.Sell ? TradeDirection.Sell : TradeDirection.Between;
            var price = store.GetPrice(i);
            var vol = store.GetVolume(i);
            if (cur == null || t / TimeSpan.TicksPerMillisecond != curTime || cur.Direction != dir)
            {
                if (cur != null) cur.State = CumulativeTradeStates.Finished;
                cur = new CumulativeTrade { Time = new DateTime(t, DateTimeKind.Utc), FirstPrice = price, Lastprice = price, Direction = dir };
                curTime = t / TimeSpan.TicksPerMillisecond;
                res.Add(cur);
            }

            cur.Lastprice = price;
            cur.Volume += vol;
            cur.Ticks.Add(new MarketDataArg { Id = store.GetId(i), Time = new DateTime(t, DateTimeKind.Utc), Price = price, Volume = vol,
                Direction = dir, DataType = MarketDataType.Trade, Provenance = store.GetProvenance(i), Security = Instrument.Symbol });
        }

        if (cur != null && processed == store.Count && (to == DateTime.MaxValue || to.Ticks >= store.GetTimeTicks(processed - 1)))
        {
            var existing = _openCumulative;
            if (existing != null && existing.Time == cur.Time && existing.Direction == cur.Direction &&
                existing.FirstPrice == cur.FirstPrice && existing.Lastprice == cur.Lastprice &&
                existing.Volume == cur.Volume && existing.Ticks.Count == cur.Ticks.Count &&
                existing.Ticks[0].Id == cur.Ticks[0].Id && existing.Ticks[^1].Id == cur.Ticks[^1].Id)
                res[^1] = existing;
            else if (existing == null || existing.Time.Ticks / TimeSpan.TicksPerMillisecond != curTime ||
                existing.Direction != cur.Direction || cur.Time <= existing.Time)
                _openCumulative = cur;
        }
        return res;
    }

    private void FeedCumulative(List<MarketDataArg> ticks)
    {
        var slots = Indicators.Where(s => s.WantsCumulative && !s.NeedsRecalc && s.PendingCumulative.Count == 0).ToList();
        foreach (var tk in ticks)
        {
            if (!tk.IsObservedTrade) continue;
            var cur = _openCumulative;
            bool same = cur != null && cur.Direction == tk.Direction &&
                        cur.Time.Ticks / TimeSpan.TicksPerMillisecond == tk.Time.Ticks / TimeSpan.TicksPerMillisecond;
            if (same)
            {
                cur!.Lastprice = tk.Price;
                cur.Volume += tk.Volume;
                cur.Ticks.Add(tk);
                foreach (var s in slots) Safe(s, () => s.Indicator.HostUpdateCumulativeTrade(cur));
            }
            else
            {
                if (cur != null) cur.State = CumulativeTradeStates.Finished;
                var n = new CumulativeTrade { Time = tk.Time, FirstPrice = tk.Price, Lastprice = tk.Price, Volume = tk.Volume, Direction = tk.Direction };
                n.Ticks.Add(tk);
                _openCumulative = n;
                foreach (var s in slots) Safe(s, () => s.Indicator.HostCumulativeTrade(n));
            }
        }
    }

    // =================================================================== líneas hasta toque

    private void UpdateLinesTillTouch(Indicator ind, int fromBar)
    {
        if (ind.HorizontalLinesTillTouch.Count == 0) return;
        int last = Series.Count - 1;
        foreach (var line in ind.HorizontalLinesTillTouch)
        {
            if (line.Finished) continue;
            long rowTicks = Instrument.ToTicks(line.FirstPrice);
            int start = Math.Max(line.FirstBar + 1, Math.Min(fromBar, line.SecondBar));
            line.SecondBar = Math.Max(line.SecondBar, line.FirstBar);
            for (int b = Math.Max(start, line.FirstBar + 1); b <= last; b++)
            {
                var c = Series[b];
                line.SecondBar = b;
                if (c.LowTicks <= rowTicks && c.HighTicks >= rowTicks)
                {
                    line.Finished = true;
                    break;
                }

                if (line.Length > 0 && b - line.FirstBar >= line.Length)
                {
                    line.Finished = true;
                    break;
                }
            }
        }
    }

    // =================================================================== IIndicatorHost

    public int BarCount => Series.Count;

    public IndicatorCandle GetCandle(int bar) => _wrappers[Math.Clamp(bar, 0, _wrappers.Count - 1)];

    public InstrumentInfo InstrumentInfo => _instrumentInfo;
    public ChartInfo ChartInfo => _chartInfo;
    public MarketDepthInfo MarketDepthInfo => _depthInfo;
    public TradingManager TradingManager => Trading!;
    public TradingStatisticsProvider TradingStatisticsProvider => Statistics;
    public int FirstVisibleBar => LastLayout?.FirstVisible ?? Math.Max(0, Series.Count - 50);
    public int LastVisibleBar => LastLayout?.LastVisible ?? Math.Max(0, Series.Count - 1);
    public int CandlesCount => Series.Count;

    public bool IsNewSession(DateTime previous, DateTime current) => Series.Session.IsNewSession(previous, current);
    public bool IsNewWeek(DateTime previous, DateTime current) => Series.Session.IsNewWeek(previous, current);
    public bool IsNewMonth(DateTime previous, DateTime current) => Series.Session.IsNewMonth(previous, current);

    public bool IsNewSession(int bar) => bar > 0 && bar < Series.Count && Series.Session.IsNewSession(Series[bar - 1].Time, Series[bar].Time);
    public bool IsNewWeek(int bar) => bar > 0 && bar < Series.Count && Series.Session.IsNewWeek(Series[bar - 1].Time, Series[bar].Time);
    public bool IsNewMonth(int bar) => bar > 0 && bar < Series.Count && Series.Session.IsNewMonth(Series[bar - 1].Time, Series[bar].Time);

    public IChartContainer GetContainer(Indicator indicator)
    {
        var layout = LastLayout;
        if (layout == null) return EmptyContainer.Instance;
        var slot = Indicators.FirstOrDefault(s => s.Indicator == indicator);
        if (slot?.Panel == null) return layout.Main;
        return layout.Panels.FirstOrDefault(p => p.SubPanel == slot.Panel) ?? (IChartContainer)layout.Main;
    }

    public void RequestRecalculate(Indicator indicator)
    {
        var slot = Indicators.FirstOrDefault(s => s.Indicator == indicator);
        if (slot != null) slot.NeedsRecalc = true;
        Invalidate();
    }

    public void RequestRedraw() => Invalidate();

    public void AddAlert(Indicator source, string soundFile, string instrument, string message, Color background, Color foreground) =>
        AlertRaised?.Invoke(new AlertInfo(DateTime.Now, instrument, message, soundFile, background, foreground, source?.Name ?? ""));

    public void BeginInvoke(Action action) => _invokes.Enqueue(action);

    public void SubscribeTimer(Indicator indicator, TimeSpan interval, Action action)
    {
        if (interval < TimeSpan.FromMilliseconds(10)) interval = TimeSpan.FromMilliseconds(10);
        _timers.Add((indicator, interval, action, DateTime.UtcNow + interval));
    }

    public void UnsubscribeTimer(Indicator indicator, TimeSpan interval, Action action) =>
        _timers.RemoveAll(t => t.Ind == indicator && t.Action == action);

    public void RequestCumulativeTrades(Indicator indicator, CumulativeTradesRequest request)
    {
        var slot = Indicators.FirstOrDefault(s => s.Indicator == indicator || ReferenceEquals(s.Indicator, indicator));
        slot?.PendingCumulative.Add(request);
    }

    // ----------------------------------------------------------------- perfiles fijos (día, semana, mes…)

    private readonly List<(Indicator Indicator, FixedProfilePeriods Period)> _fixedProfileSubs = new();
    private int _fixedProfileBars = -1;

    public void RequestFixedProfile(Indicator indicator, FixedProfileRequest request)
    {
        if (!_fixedProfileSubs.Contains((indicator, request.Period))) _fixedProfileSubs.Add((indicator, request.Period));
        var p = request.Period;
        BeginInvoke(() => RespondFixedProfile(indicator, p));
    }

    /// <summary>Vuelve a mandar los perfiles de los períodos en curso cuando aparece una vela nueva.</summary>
    private void RefreshFixedProfiles()
    {
        if (_fixedProfileSubs.Count == 0 || Series.Count == _fixedProfileBars) return;
        _fixedProfileBars = Series.Count;
        foreach (var (ind, period) in _fixedProfileSubs.ToList())
        {
            if (Indicators.All(s => !ReferenceEquals(s.Indicator, ind)))
            {
                _fixedProfileSubs.Remove((ind, period));
                continue;
            }

            RespondFixedProfile(ind, period);
        }
    }

    private void RespondFixedProfile(Indicator indicator, FixedProfilePeriods period)
    {
        var range = FixedProfileRange(period);
        if (range is not { } r) return;
        var store = Data.Trades;
        int first = Series[r.First].FirstTradeIndex, last = Series[r.Last].LastTradeIndex;
        if (first < 0 || last < first || last >= store.Count) return;
        var origin = new CandleSeries(Instrument, Period, 1, Series.Session);
        var scaled = new TradingHub.Data.Candle { Time = Series[r.First].Time };
        var exact = new TradingHub.Data.Candle { Time = Series[r.First].Time };
        for (int i = first; i <= last; i++)
        {
            long price = store.GetPriceTicks(i);
            decimal vol = store.GetVolume(i);
            var side = store.GetSide(i);
            long t = store.GetTimeTicks(i);
            var provenance = store.GetProvenance(i);
            bool synthetic = provenance.Quality.HasFlag(DataQuality.Synthetic);
            scaled.AddTrade(price, Series.RowOf(price), vol, side, t, i, synthetic, provenance);
            exact.AddTrade(price, price, vol, side, t, i, synthetic, provenance);
        }

        try
        {
            indicator.HostFixedProfileResponse(new IndicatorCandle(scaled, Series), new IndicatorCandle(exact, origin), period);
        }
        catch (Exception ex)
        {
            indicator.LogWarn($"Fixed-profile error in {indicator.Name}: {ex.Message}");
        }
    }

    /// <summary>Velas (primera y última) que forman el período pedido.</summary>
    private (int First, int Last)? FixedProfileRange(FixedProfilePeriods period)
    {
        int n = Series.Count;
        if (n == 0) return null;
        var session = Series.Session;
        Func<DateTime, DateTime, bool> isNew = period switch
        {
            FixedProfilePeriods.CurrentWeek or FixedProfilePeriods.LastWeek => session.IsNewWeek,
            FixedProfilePeriods.CurrentMonth or FixedProfilePeriods.LastMonth => session.IsNewMonth,
            FixedProfilePeriods.Contract => (_, _) => false,
            _ => session.IsNewSession,
        };

        // inicio del período en curso
        int start = n - 1;
        while (start > 0 && !isNew(Series[start - 1].Time, Series[start].Time)) start--;
        bool previous = period is FixedProfilePeriods.LastDay or FixedProfilePeriods.LastWeek or FixedProfilePeriods.LastMonth;
        if (!previous) return (start, n - 1);
        if (start == 0) return null;
        int end = start - 1, pstart = end;
        while (pstart > 0 && !isNew(Series[pstart - 1].Time, Series[pstart].Time)) pstart--;
        return (pstart, end);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _depthLoadCancellation?.Cancel();
        _depthLoadTask?.GetAwaiter().GetResult();
        _depthLoadCancellation?.Dispose();
        Interlocked.Exchange(ref _depthPublication, null);
        _depthSelection.Dispose();
        Data.TradesAppended -= OnTradesAppended;
        Data.HistoryLoaded -= OnHistoryLoaded;
        Data.DepthUpdated -= OnDepthUpdated;
        Data.DepthHistoryChanged -= OnDepthHistoryChanged;
        Data.StatusChanged -= OnStatusChanged;
        Data.OpenInterestChanged -= OnOpenInterestChanged;
        foreach (var s in Indicators.ToArray())
        {
            try { s.Indicator.HostDetach(); }
            catch { /* ignorar */ }
        }

        Indicators.Clear();
    }

    /// <summary>Contenedor vacío hasta el primer dibujado.</summary>
    private sealed class EmptyContainer : IChartContainer
    {
        public static readonly EmptyContainer Instance = new();
        public Rectangle Region => Rectangle.Empty;
        public Rectangle RelativeRegion => Rectangle.Empty;
        public int BarsWidth => 1;
        public int BarSpacing => 0;
        public decimal Step => 1;
        public decimal PriceRowHeight => 1;
        public int TotalBars => 0;
        public decimal High => 1;
        public decimal Low => 0;
        public decimal Minimum => 0;
        public decimal Maximum => 1;
        public int FirstVisibleBarNumber => 0;
        public int LastVisibleBarNumber => 0;
        public int GetXByBar(int bar, bool isStartOfBar = true) => 0;
        public int GetYByPrice(decimal price, bool isStartOfPrice = true) => 0;
        public decimal GetPriceByY(int y) => 0;
        public int GetBarByX(int x) => 0;
        public int GetYByValue(decimal value) => 0;
        public decimal GetValueByY(int y) => 0;
    }
}
