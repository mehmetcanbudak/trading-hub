using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Rendering;

namespace TradingHub.App.Controls;

/// <summary>Control de gráfico: dibuja un <see cref="ChartModel"/> y traduce el mouse/teclado al controlador.</summary>
public sealed class ChartView : SkiaView
{
    private readonly ChartRenderer _renderer = new();
    private readonly ChartRenderState _state = new();
    private ChartModel? _model;
    private ChartController? _controller;
    private readonly TextBlock _recordingWarningTip = new() { FontSize = 13, MaxWidth = 420, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private bool _recordingWarningTipActive, _recordingWarningAttached;
    private int _recordingWarningTipGeneration;

    public ChartModel? Model
    {
        get => _model;
        set
        {
            if (ReferenceEquals(_model, value)) return;
            if (_recordingWarningAttached && _model != null) _model.Invalidated -= RecordingWarningInvalidated;
            _model = value;
            if (_recordingWarningAttached && _model != null) _model.Invalidated += RecordingWarningInvalidated;
            _state.CompactRecordingWarning = null;
            _state.CompactRecordingWarningBounds = null;
            ClearRecordingWarningTip();
            _controller = value != null ? new ChartController(value, _state) : null;
            if (_controller != null) _controller.ToolFinished += () => ToolFinished?.Invoke();
            if (_controller != null) _controller.DrawingsChanged += () => DrawingsChanged?.Invoke();
            if (_controller != null) _controller.OrderDragged += (o, p) => OrderDragged?.Invoke(o, p);
            if (_controller != null) _controller.BracketDropped += (d, p) => BracketDropped?.Invoke(d, p);
            if (_controller != null) _controller.DrawingDoubleClicked += d => DrawingDoubleClicked?.Invoke(d);
            if (_controller != null) _controller.DrawingDefaults = k => DrawingDefaults?.Invoke(k);
            if (_controller != null) _controller.IndicatorsChanged += () => IndicatorsChanged?.Invoke();
            if (_controller != null) _controller.IndicatorSettingsRequested += slot => IndicatorSettingsRequested?.Invoke(slot);
            if (_controller != null) _controller.StrategySettingsRequested += st => StrategySettingsRequested?.Invoke(st);
            if (_controller != null) _controller.StrategyRemoveRequested += st => StrategyRemoveRequested?.Invoke(st);
            InvalidateVisual();
        }
    }

    public ChartController? Controller => _controller;

    public event Action? ToolFinished;
    public event Action? DrawingsChanged;

    /// <summary>Orden propia arrastrada a un nuevo precio.</summary>
    public event Action<TradingHub.Trading.Order, decimal>? OrderDragged;

    /// <summary>Stop/objetivo soltado (desde la posición o una orden pendiente).</summary>
    public event Action<BracketDrag, decimal>? BracketDropped;

    /// <summary>Pedido de menú contextual (clic derecho) con la posición.</summary>
    public event Action<ChartView, Point>? MenuRequested;

    /// <summary>La cruz del cursor se movió (instante y precio bajo el mouse; null al salir del gráfico).</summary>
    public event Action<DateTime?, double?>? CrosshairMoved;

    /// <summary>Doble clic sobre un dibujo (abre su panel de configuración).</summary>
    public event Action<TradingHub.Charting.Drawings.ChartDrawing>? DrawingDoubleClicked;

    /// <summary>Se ocultó, mostró o quitó un indicador desde la leyenda.</summary>
    public event Action? IndicatorsChanged;

    /// <summary>Ajustes de un indicador pedidos desde la leyenda.</summary>
    public event Action<IndicatorSlot>? IndicatorSettingsRequested;

    /// <summary>Configurar o quitar una estrategia del gráfico (desde la leyenda).</summary>
    public event Action<StrategyOverlay>? StrategySettingsRequested;

    public event Action<StrategyOverlay>? StrategyRemoveRequested;

    /// <summary>Estilo por defecto de cada tipo de dibujo.</summary>
    public Func<TradingHub.Charting.Drawings.DrawingKind, TradingHub.Charting.Drawings.ChartDrawing?>? DrawingDefaults { get; set; }

    /// <summary>Muestra la cruz de otro gráfico vinculado (instante y, si es el mismo instrumento, precio).</summary>
    public void SetLinkedCrosshair(DateTime? time, double? price)
    {
        if (_state.LinkedTime == time && _state.LinkedPrice == price) return;
        _state.LinkedTime = time;
        _state.LinkedPrice = price;
        InvalidateVisual();
    }

    private void RaiseCrosshair(Point p)
    {
        if (CrosshairMoved == null || _model?.LastLayout is not { } l) return;
        if (!l.Plot.Contains((float)p.X, (float)p.Y) || _model.Series.Count == 0)
        {
            CrosshairMoved(null, null);
            return;
        }

        var time = TradingHub.Charting.Drawings.ChartDrawing.TimeAt(l, _model.Series, (float)p.X);
        double? price = l.PanelAt((float)p.Y)?.IsPrice == true ? l.Main.ValueAt((float)p.Y) : null;
        CrosshairMoved(time, price);
    }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        if (_model == null)
        {
            ClearRecordingWarningTip();
            canvas.Clear(new SKColor(6, 10, 20));
            return;
        }

        _renderer.Render(canvas, _model, width, height, _state);
        UpdateRecordingWarningTip();
    }

    private void ClearRecordingWarningTip()
    {
        _recordingWarningTipGeneration++;
        bool ownsTip = ReferenceEquals(ToolTip.GetTip(this), _recordingWarningTip);
        if (!_recordingWarningTipActive && !ownsTip) return;
        _recordingWarningTipActive = false;
        if (ownsTip)
        {
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, null);
        }
        _recordingWarningTip.Text = null;
    }

    private string? CurrentRecordingWarningDisclosure()
    {
        // Render state supplies geometry, not recording authority. Recovery/loading can
        // invalidate the model before the next picture is recorded, including while focused.
        if (!_recordingWarningAttached || _model == null || _model.Data.IsLoadingHistory ||
            _model.Series.Count == 0 || _state.Message != null) return null;
        string? warning = _model.Data.RecordingWarning;
        if (warning == null || warning != _state.CompactRecordingWarning) return null;
        bool hovering = _state.CompactRecordingWarningBounds is { } bounds &&
            _state.MouseX is { } x && _state.MouseY is { } y && bounds.Contains(x, y);
        return IsFocused || hovering ? warning : null;
    }

    private void RecordingWarningInvalidated()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RecordingWarningInvalidated);
            return;
        }
        // Close obsolete disclosure immediately, without reopening from cached geometry
        // or reacting to transient render-state reset while a picture is being recorded.
        if (!_recordingWarningAttached || _model == null || _model.Data.IsLoadingHistory ||
            _model.Series.Count == 0 || _state.Message != null ||
            _model.Data.RecordingWarning is not { } warning ||
            (_recordingWarningTipActive && _recordingWarningTip.Text != UiText.Tr(warning)))
            ClearRecordingWarningTip();
    }

    private void UpdateRecordingWarningTip()
    {
        string? warning = CurrentRecordingWarningDisclosure();
        if (warning == null)
        {
            ClearRecordingWarningTip();
            return;
        }
        string text = UiText.Tr(warning);
        if (_recordingWarningTipActive && _recordingWarningTip.Text == text) return;
        _recordingWarningTip.Text = text;
        _recordingWarningTipActive = true;
        int generation = ++_recordingWarningTipGeneration;
        var model = _model;
        ToolTip.SetTip(this, _recordingWarningTip);
        // Drawing records on the UI thread; create the popup after that recording completes.
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _recordingWarningTipGeneration) return;
            if (!_recordingWarningTipActive || !ReferenceEquals(_model, model) ||
                !ReferenceEquals(ToolTip.GetTip(this), _recordingWarningTip) ||
                CurrentRecordingWarningDisclosure() != warning || UiText.Tr(warning) != text)
            {
                ClearRecordingWarningTip();
                return;
            }
            ToolTip.SetIsOpen(this, true);
        });
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        UpdateRecordingWarningTip();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        UpdateRecordingWarningTip();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _recordingWarningAttached = true;
        if (_model != null) _model.Invalidated += RecordingWarningInvalidated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _recordingWarningAttached = false;
        if (_model != null) _model.Invalidated -= RecordingWarningInvalidated;
        ClearRecordingWarningTip();
        base.OnDetachedFromVisualTree(e);
    }

    private static InputModifiers Mods(KeyModifiers k)
    {
        var m = InputModifiers.None;
        if (k.HasFlag(KeyModifiers.Shift)) m |= InputModifiers.Shift;
        if (k.HasFlag(KeyModifiers.Control)) m |= InputModifiers.Control;
        if (k.HasFlag(KeyModifiers.Alt)) m |= InputModifiers.Alt;
        if (k.HasFlag(KeyModifiers.Meta)) m |= InputModifiers.Command;
        return m;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_controller == null) return;
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var mods = Mods(e.KeyModifiers);
        if (props.IsRightButtonPressed)
        {
            MenuRequested?.Invoke(this, p);
            e.Handled = true;
            return;
        }

        if (_controller.TryCompletePending((float)p.X, (float)p.Y, mods))
        {
            InvalidateVisual();
            return;
        }

        var button = props.IsMiddleButtonPressed ? PointerButton.Middle : PointerButton.Left;
        _controller.MouseDown((float)p.X, (float)p.Y, button, e.ClickCount, mods);
        e.Pointer.Capture(this);
        UpdateCursor();
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_controller == null) return;
        var p = e.GetPosition(this);
        var mods = Mods(e.KeyModifiers);
        if (_controller.HasPending) _controller.HoverPending((float)p.X, (float)p.Y, mods);
        _controller.MouseMove((float)p.X, (float)p.Y, mods);
        UpdateRecordingWarningTip();
        UpdateCursor();
        InvalidateVisual();
        RaiseCrosshair(p);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_controller == null) return;
        var p = e.GetPosition(this);
        _controller.MouseUp((float)p.X, (float)p.Y, e.InitialPressMouseButton == MouseButton.Right ? PointerButton.Right : PointerButton.Left, Mods(e.KeyModifiers));
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_controller == null) return;
        var p = e.GetPosition(this);
        _controller.Wheel((float)p.X, (float)p.Y, (float)e.Delta.X, (float)e.Delta.Y, Mods(e.KeyModifiers));
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _controller?.Leave();
        UpdateRecordingWarningTip();
        InvalidateVisual();
        CrosshairMoved?.Invoke(null, null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_controller == null) return;
        if (_controller.ForwardKey(e) || _controller.KeyDown(e.Key.ToString(), Mods(e.KeyModifiers)))
        {
            e.Handled = true;
            InvalidateVisual();
        }
    }

    private void UpdateCursor()
    {
        if (_controller == null) return;
        Cursor = _controller.Cursor switch
        {
            StdCursor.SizeNS => new Cursor(StandardCursorType.SizeNorthSouth),
            StdCursor.SizeWE => new Cursor(StandardCursorType.SizeWestEast),
            StdCursor.SizeAll => new Cursor(StandardCursorType.SizeAll),
            StdCursor.Hand => new Cursor(StandardCursorType.Hand),
            StdCursor.Arrow => new Cursor(StandardCursorType.Arrow),
            StdCursor.IBeam => new Cursor(StandardCursorType.Ibeam),
            _ => new Cursor(StandardCursorType.Cross),
        };
    }
}
