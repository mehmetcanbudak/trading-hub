using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using TradingHub.Charting.Dom;
using TradingHub.Rendering;
using TradingHub.Trading;

namespace TradingHub.App.Controls;

/// <summary>
/// DOM (price ladder). Order gestures go through <see cref="DomOrderGestures"/>; with DOM trading switched off the
/// ladder is read-only (wheel scroll and double-click recenter still work).
/// </summary>
public sealed class DomView : SkiaView
{
    private readonly DomRenderer _renderer = new();
    private readonly DomOrderGestures _gestures;
    private readonly SKPaint _badgeFill = new() { Color = new SKColor(40, 40, 46, 220), IsAntialias = true };
    private readonly SKPaint _badgeText = new() { Color = new SKColor(230, 180, 90), IsAntialias = true };
    private float? _mouseY;

    public DomView() => _gestures = new DomOrderGestures(_renderer);

    public DomModel? Dom { get; set; }

    /// <summary>The DOM trading switch. Off: no click, cancel or drag on the DOM reaches an order listener or the account.</summary>
    public bool TradingEnabled
    {
        get => _gestures.TradingEnabled;
        set
        {
            _gestures.TradingEnabled = value;
            if (!value) _gestures.CancelDrag(Dom);
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Pedido de orden: columna, precio y si es cancelación (clic derecho / Alt).
    /// Si nadie escucha, el DOM opera directo en la cuenta.
    /// </summary>
    public event Action<DomColumn, decimal, bool>? OrderRequested
    {
        add => _gestures.OrderRequested += value;
        remove => _gestures.OrderRequested -= value;
    }

    /// <summary>Una orden propia arrastrada a otro precio.</summary>
    public event Action<Order, decimal>? OrderMoved
    {
        add => _gestures.OrderMoved += value;
        remove => _gestures.OrderMoved -= value;
    }

    /// <summary>Mensaje de la última acción (para la barra de estado).</summary>
    public event Action<string>? Message
    {
        add => _gestures.Message += value;
        remove => _gestures.Message -= value;
    }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        if (Dom == null)
        {
            canvas.Clear(TradingHub.Charting.ChartTheme.Base.BackgroundTop);
            return;
        }

        _renderer.Render(canvas, Dom, width, height, _mouseY);
        if (TradingEnabled) return;
        // a discreet read-only badge in the bottom-right corner
        var font = TradingHub.Rendering.Context.RenderContext.GetFont(TradingHub.Charting.ChartTheme.Base.FontFamily, 10.5f, false, false);
        string text = TradingHub.Rendering.UiText.Tr("DOM trading off");
        float w = font.MeasureUiText(text) + 12;
        var r = new SKRect(width - w - 6, height - 22, width - 6, height - 5);
        canvas.DrawRoundRect(r, 4, 4, _badgeFill);
        canvas.DrawUiText(text, r.Left + 6, r.Bottom - 5, font, _badgeText);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Dom == null) return;
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var result = _gestures.Press(Dom, (float)p.X, (float)p.Y, props.IsRightButtonPressed, e.KeyModifiers.HasFlag(KeyModifiers.Alt), e.ClickCount);
        if (result == DomPressResult.DragStarted) e.Pointer.Capture(this);
        else if (result == DomPressResult.Blocked && e.ClickCount >= 2) Recenter();
        if (result != DomPressResult.None) InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _mouseY = (float)p.Y;
        if (Dom != null) _gestures.Move(Dom, (float)p.Y);
        Cursor = Dom != null && _gestures.ShowsMoveCursor(Dom, (float)p.X, (float)p.Y) ? new Cursor(StandardCursorType.SizeNorthSouth) : Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Dom == null || !_gestures.Release(Dom, (float)e.GetPosition(this).Y)) return;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _mouseY = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Dom == null) return;
        _renderer.Scroll(Dom, (int)Math.Round(e.Delta.Y * 3));
        e.Handled = true;
        InvalidateVisual();
    }

    public void Recenter()
    {
        if (Dom == null) return;
        Dom.AutoCenter = Dom.Settings.AutoCenter;
        Dom.CenterTicks = 0;
        InvalidateVisual();
    }

    /// <summary>Aplica una configuración nueva (columnas, tamaños, colores).</summary>
    public void ApplySettings(DomSettings settings)
    {
        if (Dom == null) return;
        Dom.Settings = settings;
        Dom.AutoCenter = settings.AutoCenter;
        InvalidateVisual();
    }
}

/// <summary>Cinta de operaciones (Time &amp; Sales).</summary>
public sealed class TapeView : SkiaView
{
    private readonly TapeRenderer _renderer = new();

    public TapeModel? Tape { get; set; }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        if (Tape == null)
        {
            canvas.Clear(TradingHub.Charting.ChartTheme.Base.BackgroundTop);
            return;
        }

        _renderer.Render(canvas, Tape, width, height);
    }

    // rueda o trackpad: hacia abajo muestra operaciones más viejas (y se queda quieta); arriba vuelve a lo último
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Tape == null) return;
        _wheel += e.Delta.Y * 3;
        int rows = (int)Math.Truncate(_wheel);
        if (rows == 0) return;
        _wheel -= rows;
        TapeRenderer.Scroll(Tape, -rows);
        e.Handled = true;
        InvalidateVisual();
    }

    private double _wheel;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Tape?.Anchor == null || e.GetPosition(this).Y > TapeRenderer.HeaderHeight) return;
        Tape.Anchor = null;
        InvalidateVisual();
    }

    /// <summary>Vuelve a lo último.</summary>
    public void Live()
    {
        if (Tape == null) return;
        Tape.Anchor = null;
        InvalidateVisual();
    }
}
