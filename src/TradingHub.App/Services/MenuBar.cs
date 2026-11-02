using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using TradingHub.App.Windows;

namespace TradingHub.App.Services;

/// <summary>
/// Shows live strategies, account positions and agent activity in the macOS menu bar or Windows system tray.
/// Closing the main window can keep Trading Hub running here; explicit Quit always uses the protection-aware flow.
/// </summary>
internal sealed class MenuBar
{
    private readonly AppServices _svc;
    private readonly MainWindow _main;
    private readonly TrayIcon _tray;
    private MenuBarPanel? _panel;
    private DateTime _panelHiddenAt;
    private readonly NativeMenu _menu = new();
    private readonly DispatcherTimer _menuTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(string Key, NativeMenuItemBase Item)> _menuItems = new();

    private MenuBar(Application app, AppServices svc, MainWindow main)
    {
        _svc = svc;
        _main = main;
        // Use the colored app icon on Windows so it remains visible on a dark taskbar.
        var icon = OperatingSystem.IsWindows() && AppIcon.Window is { } appIcon ? appIcon : new WindowIcon(Glyph());
        _tray = new TrayIcon { Icon = icon, ToolTipText = TradingHub.Common.Product.Name, IsVisible = svc.Settings.KeepInMenuBar };
        if (OperatingSystem.IsMacOS()) MacOSProperties.SetIsTemplateIcon(_tray, true);
        // en macOS el clic en el ícono abre su menú (sin menú no llega ningún aviso): el menú trae lo que corre, en vivo
        _tray.Menu = _menu;
        _menu.NeedsUpdate += (_, _) => RefreshMenu();
        _menu.Opening += (_, _) =>
        {
            RefreshMenu();
            _menuTimer.Start(); // abierto: los números se actualizan solos
        };
        _menu.Closed += (_, _) =>
        {
            if (!OperatingSystem.IsWindows()) _menuTimer.Stop();
        };
        _tray.Clicked += (_, _) => TogglePanel();
        RefreshMenu();
        _menuTimer.Tick += (_, _) => RefreshMenu();
        if (OperatingSystem.IsWindows())
        {
            // Windows builds its context menu without NeedsUpdate, so keep its contents current.
            _menuTimer.Interval = TimeSpan.FromSeconds(3);
            _menuTimer.Start();
        }
        TrayIcon.SetIcons(app, new TrayIcons { _tray });

        // tocar el ícono del Dock con la ventana cerrada la vuelve a abrir
        if (app.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) Dispatcher.UIThread.Post(ShowMain);
            };
    }

    public static MenuBar? Current { get; private set; }

    /// <summary>Isolated tools and tests never install a menu-bar or tray icon.</summary>
    public static MenuBar? Install(Application app, AppServices svc, MainWindow main)
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) || Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 }) return null;
        try
        {
            Current = new MenuBar(app, svc, main);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        return Current;
    }

    /// <summary>Se activó o desactivó «seguir en la barra de menú» en Ajustes.</summary>
    public void Refresh() => _tray.IsVisible = _svc.Settings.KeepInMenuBar;

    public void ShowMain()
    {
        _panel?.Hide();
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    /// <summary>
    /// «Quit Trading Hub»: the same quit as ⌘Q (<see cref="QuitFlow"/>). While live strategies hold positions it first asks
    /// what to do with their orders; the ones that were running resume at the next start if enabled in Settings.
    /// </summary>
    public static void Quit() => _ = App.Quit?.QuitAsync();

    /// <summary>Un renglón del menú: clave estable, texto, si se puede tocar y qué hace (null = separador).</summary>
    private sealed record MenuLine(string Key, string? Text, bool Enabled = false, Action? Click = null);

    private static System.Globalization.CultureInfo Es => TradingHub.Common.UiCulture.Current;

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Es);

    private List<MenuLine> MenuLines()
    {
        var lines = new List<MenuLine> { new("hdr", $"Trading Hub · {_main.ConnectionSummary}") };
        int sep = 0;
        void Sep() => lines.Add(new MenuLine($"sep{++sep}", null));

        Sep();
        var running = _svc.Live.Entries(includeStopped: false).Where(e => e.Running).OrderBy(e => e.Locked).ThenBy(e => e.Info.Strategy).ToList();
        lines.Add(new MenuLine("live", $"Live strategies ({running.Count})"));
        foreach (var e in running)
        {
            var r = e.Runner!;
            decimal pos = e.Ledger.Position;
            var plan = _svc.Live.PlanOf(e);
            string side = pos == 0 ? "flat" : $"{(pos > 0 ? "long" : "short")} {Math.Abs(pos).ToString("0.####", Es)} {e.Ledger.OpenSymbol ?? plan.Symbol}";
            string state = e.Paused ? " · paused" : r.DailyLimitActive ? " · stopped for today" : "";
            string each = plan.Units <= 0 ? "no new entries" : $"{plan.Text} per order{(plan.Micro ? " (micro)" : "")}";
            lines.Add(new MenuLine($"s:{e.Key}", $"   {e.Info.Strategy} · {r.Data.Instrument.Symbol} · {each} · {side} · open {Money(e.OpenPnL)} · today {Money(r.TodayPnL)}{state}", true, ShowPanel));
        }

        if (running.Count == 0) lines.Add(new MenuLine("live-none", "   None running"));

        Sep();
        var positions = new List<MenuLine>();
        foreach (var a in _svc.AccountCenter.Accounts())
        {
            try
            {
                foreach (var p in _svc.AccountCenter.Snapshot(a).Positions.Where(p => p.Net != 0))
                    positions.Add(new MenuLine($"p:{a.Id}:{p.Symbol}", $"   {p.Symbol} · {a.Name} · {(p.Net > 0 ? "long" : "short")} {Math.Abs(p.Net).ToString("0.####", Es)} · {Money(p.OpenPnl)}", true, ShowPanel));
            }
            catch
            {
                // una cuenta que no contesta no frena el menú
            }
        }

        lines.Add(new MenuLine("pos", $"Open positions ({positions.Count})"));
        lines.AddRange(positions);
        if (positions.Count == 0) lines.Add(new MenuLine("pos-none", "   No positions"));


        Sep();
        lines.Add(new MenuLine("panel", "View live panel…", true, ShowPanel));
        lines.Add(new MenuLine("open", "Open Trading Hub", true, ShowMain));
        lines.Add(new MenuLine("quit", "Quit Trading Hub", true, Quit));
        return lines;
    }

    /// <summary>Pone el menú al día: si cambiaron los renglones lo rehace; si no, solo cambia los textos.</summary>
    private void RefreshMenu()
    {
        List<MenuLine> lines;
        try
        {
            lines = MenuLines();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return;
        }

        if (lines.Select(l => l.Key).SequenceEqual(_menuItems.Select(m => m.Key)))
        {
            for (int i = 0; i < lines.Count; i++)
                if (_menuItems[i].Item is NativeMenuItem mi && mi.Header != lines[i].Text) mi.Header = lines[i].Text;
            return;
        }

        _menu.Items.Clear();
        _menuItems.Clear();
        foreach (var line in lines)
        {
            NativeMenuItemBase item;
            if (line.Text == null) item = new NativeMenuItemSeparator();
            else
            {
                var mi = new NativeMenuItem { Header = line.Text, IsEnabled = line.Enabled };
                if (line.Click is { } click) mi.Click += (_, _) => Dispatcher.UIThread.Post(click);
                item = mi;
            }

            _menu.Items.Add(item);
            _menuItems.Add((line.Key, item));
        }
    }

    /// <summary>Shows the detailed panel below the menu bar or above the Windows taskbar.</summary>
    public void ShowPanel()
    {
        _panel ??= CreatePanel();
        _panel.Refresh();
        Place(_panel);
        _panel.Show();
        _panel.Activate();
    }

    private void TogglePanel()
    {
        // el clic en el ícono también le saca el foco al panel (y lo cierra): no lo vuelve a abrir enseguida
        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
            return;
        }

        if ((DateTime.UtcNow - _panelHiddenAt).TotalMilliseconds < 350) return;
        _panel ??= CreatePanel();
        _panel.Refresh();
        Place(_panel);
        _panel.Show();
        _panel.Activate();
    }

    private MenuBarPanel CreatePanel()
    {
        var p = new MenuBarPanel(_svc, _main, ShowMain);
        p.Deactivated += (_, _) =>
        {
            if (!p.IsVisible) return;
            p.Hide();
            _panelHiddenAt = DateTime.UtcNow;
        };
        if (OperatingSystem.IsWindows())
        {
            p.SizeChanged += (_, _) =>
            {
                if (p.IsVisible) PlaceOnWindows(p);
            };
            p.Closed += (_, _) =>
            {
                if (ReferenceEquals(_panel, p)) _panel = null;
            };
        }

        return p;
    }

    /// <summary>Debajo del ícono (donde está el mouse al tocarlo), sin salirse de la pantalla.</summary>
    private void Place(Window panel)
    {
        if (OperatingSystem.IsWindows())
        {
            _trayClick = CursorPosition();
            PlaceOnWindows(panel);
            return;
        }

        var screens = _main.Screens;
        var primary = screens.Primary ?? screens.All.FirstOrDefault();
        if (primary == null) return;
        double scale = primary.Scaling;
        PixelPoint? mouse = null;
        if (MouseLocation() is { } m)
        {
            // macOS mide desde abajo a la izquierda de la pantalla principal, en puntos
            double heightPoints = primary.Bounds.Height / scale;
            mouse = new PixelPoint((int)(m.X * scale), (int)((heightPoints - m.Y) * scale));
        }

        var screen = mouse is { } mp ? screens.All.FirstOrDefault(s => s.Bounds.Contains(mp)) ?? primary : primary;
        var area = screen.WorkingArea;
        int width = (int)(panel.Width * screen.Scaling);
        int x = mouse is { } p0 ? p0.X - width / 2 : area.Right - width - (int)(12 * screen.Scaling);
        x = Math.Clamp(x, area.X + (int)(8 * screen.Scaling), Math.Max(area.X, area.Right - width - (int)(8 * screen.Scaling)));
        panel.Position = new PixelPoint(x, area.Y + (int)(6 * screen.Scaling));
    }

    // ===================================================================== Windows

    private PixelPoint? _trayClick;

    /// <summary>Positions the panel beside the tray click, within that monitor's working area.</summary>
    private void PlaceOnWindows(Window panel)
    {
        var screens = _main.Screens;
        var screen = (_trayClick is { } click ? screens.ScreenFromPoint(click) : null) ?? screens.Primary ?? screens.All.FirstOrDefault();
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        double height = panel.Bounds.Height > 0 ? panel.Bounds.Height : 420;
        var size = new PixelSize((int)Math.Ceiling(panel.Width * scale), (int)Math.Ceiling(height * scale));
        var anchor = _trayClick ?? new PixelPoint(area.Right, area.Bottom);
        panel.Position = TrayPanelPosition(area, anchor, size, (int)Math.Round(8 * scale));
    }

    /// <summary>Clamps a click-centered panel above the taskbar, including top and side taskbars.</summary>
    internal static PixelPoint TrayPanelPosition(PixelRect area, PixelPoint click, PixelSize panel, int gap)
    {
        int x = Math.Clamp(click.X - panel.Width / 2, area.X + gap, Math.Max(area.X + gap, area.Right - panel.Width - gap));
        int y = Math.Clamp(Math.Min(click.Y, area.Bottom) - panel.Height - gap, area.Y + gap, Math.Max(area.Y + gap, area.Bottom - panel.Height - gap));
        return new PixelPoint(x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32Point point);

    /// <summary>Returns the cursor in per-monitor DPI-aware screen pixels, or null if unavailable.</summary>
    internal static PixelPoint? CursorPosition()
    {
        try
        {
            return GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;
        }
        catch
        {
            return null;
        }
    }

    // ===================================================================== macOS

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGPoint SendPoint(IntPtr receiver, IntPtr selector);

    /// <summary>Dónde está el mouse ([NSEvent mouseLocation]: en puntos, desde abajo a la izquierda).</summary>
    private static CGPoint? MouseLocation()
    {
        try
        {
            var cls = objc_getClass("NSEvent");
            return cls == IntPtr.Zero ? null : SendPoint(cls, sel_registerName("mouseLocation"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>El ícono de la barra: la serpiente de Trading Hub en negro sobre transparente (macOS lo adapta al tema).</summary>
    internal static Bitmap Glyph()
    {
        const int size = 44; // 22 puntos en pantallas Retina
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        // el cuerpo: una S que sube desde la cola (abajo a la izquierda) hasta la cabeza (arriba a la derecha)
        using var body = new SKPath();
        body.MoveTo(7, 38);
        body.CubicTo(17, 43, 31, 39, 29, 30);
        body.CubicTo(27.5f, 23, 14, 26, 15, 18.5f);
        body.CubicTo(16, 12, 23, 9.5f, 29, 9.5f);
        using var stroke = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5.4f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(body, stroke);
        using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
        c.DrawOval(new SKRect(27, 4.5f, 38.5f, 13.5f), fill); // la cabeza
        using var clear = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true };
        c.DrawCircle(34.2f, 7.8f, 1.5f, clear); // el ojo
        // la lengua bífida
        using var tongue = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round };
        c.DrawLine(38, 9.5f, 41.5f, 10.5f, tongue);
        c.DrawLine(41.5f, 10.5f, 43.2f, 9, tongue);
        c.DrawLine(41.5f, 10.5f, 43, 12.4f, tongue);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        return new Bitmap(stream);
    }
}
