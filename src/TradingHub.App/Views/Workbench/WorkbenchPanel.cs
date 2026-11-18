using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using SkiaSharp;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Feeds.External;
using TradingHub.Feeds.Venues;
using TradingHub.Indicators.Native;
using TradingHub.Market;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;

namespace TradingHub.App.Views.Workbench;

/// <summary>The instrument every Workbench panel follows: a connection, its instrument and the shared live data.</summary>
internal sealed record WorkbenchSelection(string ProfileId, Instrument Instrument, InstrumentData Data)
{
    /// <summary>Base asset plus USDT for USD-family crypto (BTCUSDT); the derivatives and liquidation services use this symbol.</summary>
    public string DerivativesSymbol => DerivativesPane.DerivedSymbol(Instrument.Symbol, Instrument.BaseAsset, Instrument.QuoteAsset);

    /// <summary>The options underlying; empty for simulated instruments, which no options venue lists.</summary>
    public string Underlying => IsSimulated ? "" : OptionsData.UnderlyingOf(Instrument.Symbol, Instrument.BaseAsset);

    public string Display => $"{Instrument.Symbol} · {Instrument.Exchange}".TrimEnd(' ', '·');

    /// <summary>
    /// Simulator instruments (feed id <see cref="SimulatedFeed.FeedId"/>) are generated locally: their BTC/ETH-like base
    /// assets are not real markets, so no venue's liquidations, funding, open interest or options belong to them.
    /// </summary>
    public bool IsSimulated => Instrument.FeedId == SimulatedFeed.FeedId;

    /// <summary>Real crypto USD-family instruments, the only ones the public derivatives venues list.</summary>
    public bool IsCryptoUsd => !IsSimulated && Instrument.BaseAsset.Length > 0 && AggregateFeed.IsUsdFamily(Instrument.QuoteAsset);
}

/// <summary>Services the panels read; the defaults are the application's shared instances, swapped only by isolated scenes and tests.</summary>
internal sealed class WorkbenchContext(AppServices services, Action<string> status)
{
    public AppServices Services { get; } = services;
    public Action<string> Status { get; } = status;
    public Func<DerivativesData?> Derivatives { get; set; } = () => DerivativesData.Shared;
    public Func<OptionsData?> Options { get; set; } = () => OptionsData.Shared;
    public Func<LiquidationLevelsService?> Levels { get; set; } = () => LiquidationLevelsService.Shared;
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    public WorkbenchSettings Settings => Services.Settings.Workbench ??= new();
    public void Save() => Services.Settings.Save();
}

/// <summary>
/// One Workbench panel. It follows the page's selection, does network or subscription work only while active
/// (visible in the page or in its own window) and releases it when deactivated or disposed.
/// </summary>
internal abstract class WorkbenchPanel : UserControl, IDisposable
{
    protected WorkbenchPanel(WorkbenchContext context)
    {
        Context = context;
        Name = "Workbench" + GetType().Name;
    }

    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract MaterialIconKind Icon { get; }
    protected WorkbenchContext Context { get; }
    public WorkbenchSelection? Selection { get; private set; }
    public bool IsActive { get; private set; }
    protected bool Disposed { get; private set; }
    private CancellationTokenSource? _activity;

    /// <summary>Cancelled when the panel is hidden or disposed, so requests started while it was visible stop with it.</summary>
    protected CancellationToken ActivityToken => _activity?.Token ?? new CancellationToken(canceled: true);

    public void Follow(WorkbenchSelection? selection)
    {
        if (Selection == selection || Selection != null && selection != null && ReferenceEquals(Selection.Data, selection.Data) && Selection.ProfileId == selection.ProfileId) return;
        Selection = selection;
        if (!Disposed) OnSelectionChanged();
    }

    public void SetActive(bool active)
    {
        if (Disposed || active == IsActive) return;
        IsActive = active;
        if (active)
        {
            _activity = new CancellationTokenSource();
            OnActivated();
        }
        else
        {
            EndActivity();
            OnDeactivated();
        }
    }

    /// <summary>About four times a second while active.</summary>
    public virtual void Tick(DateTime now) { }
    protected virtual void OnSelectionChanged() { }
    protected virtual void OnActivated() { }
    protected virtual void OnDeactivated() { }

    public void Dispose()
    {
        if (Disposed) return;
        EndActivity();
        if (IsActive) OnDeactivated();
        IsActive = false;
        Disposed = true;
        OnDispose();
    }

    protected virtual void OnDispose() { }

    // Not disposed: running requests may still observe the cancelled token after the panel moved on.
    private void EndActivity()
    {
        _activity?.Cancel();
        _activity = null;
    }

    protected static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "faint", "small" } };

    protected static Border Banner(string name, out TextBlock text)
    {
        text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Name = name, Child = Ui.H(8, Ui.Icon(MaterialIconKind.InformationOutline, 16, Ui.Warn), text), Padding = new Avalonia.Thickness(10, 6),
            CornerRadius = new Avalonia.CornerRadius(6), BorderBrush = Ui.Warn, BorderThickness = new Avalonia.Thickness(1), IsVisible = false,
        };
    }

    protected static NumericUpDown Number(string name, decimal value, decimal min, decimal max, decimal step, string format, Action<decimal> changed, double width = 120)
    {
        var box = new NumericUpDown
        {
            Name = name, Minimum = min, Maximum = max, Increment = step, FormatString = format, MinWidth = width,
            Value = Math.Clamp(value, min, max), HorizontalAlignment = HorizontalAlignment.Left, ShowButtonSpinner = false,
        };
        box.ValueChanged += (_, _) => changed(box.Value ?? min);
        return box;
    }
}

/// <summary>Number formatting and venue names shared by the panels.</summary>
internal static class WorkbenchFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string Missing = "—";

    public static string Compact(decimal? value) => value is { } v ? Compact((double)v) : Missing;

    public static string Compact(double? value)
    {
        if (value is not { } v || !double.IsFinite(v)) return Missing;
        double a = Math.Abs(v);
        return a >= 1e9 ? (v / 1e9).ToString("0.##", Inv) + "B"
            : a >= 1e6 ? (v / 1e6).ToString("0.##", Inv) + "M"
            : a >= 1e3 ? (v / 1e3).ToString("0.#", Inv) + "K"
            : v.ToString(a >= 100 ? "0" : a >= 1 ? "0.##" : "0.####", Inv);
    }

    public static string Percent(decimal? value, int decimals = 2) =>
        value is { } v ? (v > 0 ? "+" : "") + v.ToString("F" + decimals, Inv) + "%" : Missing;

    public static string Percent(double? value, int decimals = 2) =>
        value is { } v && double.IsFinite(v) ? Percent((decimal)v, decimals) : Missing;

    public static string Price(decimal? value) => value is { } v ? Price((double)v) : Missing;

    public static string Price(double? value)
    {
        if (value is not { } v || !double.IsFinite(v)) return Missing;
        double a = Math.Abs(v);
        return v.ToString(a >= 1000 ? "#,0.##" : a >= 1 ? "0.####" : "0.########", Inv);
    }

    public static string Number(decimal value, int maxDecimals = 6) =>
        value.ToString("#,0." + new string('#', Math.Clamp(maxDecimals, 0, 12)), Inv);

    public static string Countdown(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : $"{span.Minutes}m {span.Seconds:00}s";
    }

    /// <summary>Market name of a feed or derivatives venue id.</summary>
    public static string Venue(string id) => id switch
    {
        DerivativesData.Binance => "Binance Futures",
        DerivativesData.Bybit => "Bybit",
        _ => CryptoVenues.Find(id)?.Name ?? id,
    };
}

/// <summary>Skia surface with the app theme's colors and fonts.</summary>
internal abstract class WorkbenchCanvas : SkiaView
{
    protected SKColor Bg, Grid, Text, Strong, Up, Down, Warn, Accent;

    protected WorkbenchCanvas()
    {
        CornerRadius = 6;
        AttachedToVisualTree += (_, _) => AppTheme.Changed += InvalidateVisual;
        DetachedFromVisualTree += (_, _) => AppTheme.Changed -= InvalidateVisual;
    }

    private static SKColor Sk(string key)
    {
        var c = Ui.Color(key);
        return new SKColor(c.R, c.G, c.B, c.A);
    }

    protected void LoadColors()
    {
        bool light = AppTheme.IsLight;
        Bg = light ? Sk("Bg2") : new SKColor(0x0F, 0x14, 0x1D);
        Grid = light ? Sk("Line") : new SKColor(0x1C, 0x24, 0x32);
        Text = light ? Sk("Text2") : new SKColor(0x8A, 0x94, 0xA6);
        Strong = Sk("Text0");
        Up = Sk("Up");
        Down = Sk("Down");
        Warn = Sk("Warn");
        Accent = Sk("Accent");
    }

    protected static SKFont Font(float size = 11, bool bold = false) => RenderContext.GetFont("Inter", size, bold, false);

    /// <summary>When set (by scenes), collects the translated text every Workbench canvas label draws.</summary>
    internal static System.Collections.Concurrent.ConcurrentQueue<string>? DrawnText { get; set; }

    protected static void Label(SKCanvas canvas, string text, float x, float y, SKColor color, float size = 11, bool bold = false, bool right = false)
    {
        DrawnText?.Enqueue(UiText.Tr(text));
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        var font = Font(size, bold);
        if (right) x -= font.MeasureUiText(text);
        canvas.DrawUiText(text, x, y, font, paint);
    }

    /// <summary>A diagonal hatch: intervals that were not observed are drawn as gaps, never as zero.</summary>
    protected void Hatch(SKCanvas canvas, SKRect rect)
    {
        using var paint = new SKPaint { Color = Warn.WithAlpha(40), IsAntialias = false, StrokeWidth = 1 };
        canvas.Save();
        canvas.ClipRect(rect);
        for (float x = rect.Left - rect.Height; x < rect.Right; x += 6)
            canvas.DrawLine(x, rect.Bottom, x + rect.Height, rect.Top, paint);
        canvas.Restore();
    }

    /// <summary>
    /// A small gap-aware line chart: null values break the line; the title and latest value are drawn on top.
    /// </summary>
    protected void Sparkline(SKCanvas canvas, SKRect rect, string title, IReadOnlyList<double?> values, SKColor color, Func<double, string> format, bool zeroLine = false)
    {
        using var frame = new SKPaint { Color = Grid, IsAntialias = true, Style = SKPaintStyle.Stroke };
        canvas.DrawRoundRect(rect, 5, 5, frame);
        Label(canvas, title, rect.Left + 8, rect.Top + 15, Text, 10.5f);
        var known = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (known.Length == 0)
        {
            Label(canvas, "No data for this range", rect.Left + 8, rect.MidY + 6, Text, 10.5f);
            return;
        }
        Label(canvas, format(known[^1]), rect.Right - 8, rect.Top + 15, Strong, 11, true, right: true);
        double lo = known.Min(), hi = known.Max();
        if (zeroLine) { lo = Math.Min(lo, 0); hi = Math.Max(hi, 0); }
        if (hi - lo < 1e-12) { hi += 1; lo -= 1; }
        var plot = new SKRect(rect.Left + 6, rect.Top + 22, rect.Right - 6, rect.Bottom - 6);
        float X(int i) => plot.Left + (values.Count <= 1 ? plot.Width : plot.Width * i / (values.Count - 1));
        float Y(double v) => plot.Bottom - (float)((v - lo) / (hi - lo)) * plot.Height;
        if (zeroLine)
        {
            using var axis = new SKPaint { Color = Grid, StrokeWidth = 1 };
            canvas.DrawLine(plot.Left, Y(0), plot.Right, Y(0), axis);
        }
        using var line = new SKPaint { Color = color, IsAntialias = true, StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
        using var path = new SKPath();
        bool pen = false;
        int gapStart = -1;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } v)
            {
                pen = false;
                if (gapStart < 0) gapStart = i;
                continue;
            }
            if (gapStart >= 0)
            {
                Hatch(canvas, new SKRect(X(Math.Max(0, gapStart - 1)), plot.Top, X(i), plot.Bottom));
                gapStart = -1;
            }
            if (pen) path.LineTo(X(i), Y(v));
            else
            {
                path.MoveTo(X(i), Y(v));
                // an isolated value (gaps on both sides) is a dot, not an invisible zero-length line
                if (i + 1 >= values.Count || values[i + 1] == null)
                {
                    using var dot = new SKPaint { Color = color, IsAntialias = true };
                    canvas.DrawCircle(X(i), Y(v), 2.5f, dot);
                }
            }
            pen = true;
        }
        if (gapStart >= 0) Hatch(canvas, new SKRect(X(Math.Max(0, gapStart - 1)), plot.Top, plot.Right, plot.Bottom));
        canvas.DrawPath(path, line);
    }
}
