using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Material.Icons;
using SkiaSharp;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Indicators;

namespace TradingHub.App.Views;

// Drawing bar, selected-drawing bar, gesture chip and the toolbar actions (favorites, screenshot, full screen), plus
// the one-chart-menu-at-a-time rule.
public sealed partial class TradingView
{
    /// <summary>Wires a chart's drawing bar (left), selected-drawing bar and gesture chip (over the chart).</summary>
    private void InitializeDrawingTools(ChartTab chart, DockPanel body, Panel viewHost)
    {
        var view = chart.View;
        DrawingToolStrip.ShortcutOf = k => Hotkeys.DrawingTools.TryGetValue(k, out var id) ? Hotkeys.GestureOf(_svc.Settings, id) : null;
        var tools = new DrawingToolStrip();
        chart.Tools = tools;
        DockPanel.SetDock(tools, Dock.Left);
        body.Children.Insert(body.Children.IndexOf(viewHost), tools);
        tools.ToolSelected += kind =>
        {
            Activate(chart);
            if (view.Controller is not { } ctl) return;
            ctl.ActiveTool = kind;
            if (ctl.KeepTool != kind) ctl.KeepTool = null;
            view.Focus();
        };
        tools.ToolKept += kind =>
        {
            Activate(chart);
            if (view.Controller is not { } ctl) return;
            ctl.ActiveTool = kind;
            ctl.KeepTool = kind;
            tools.MarkKept(kind);
            view.Focus();
            _status($"{ChartDrawing.KindName(kind)} stays active after each drawing (Esc releases it)");
        };
        tools.ClearRequested += () =>
        {
            if (chart.Model == null) return;
            chart.Model.Drawings.Clear();
            chart.Model.Invalidate();
            DrawingsTouched(chart);
        };
        tools.DeleteSelectedRequested += () =>
        {
            if (chart.Model == null) return;
            var selected = chart.Model.Drawings.Where(d => d.Selected).ToList();
            if (selected.Count == 0)
            {
                _status("Select a drawing to delete it (or use the arrow to delete all)");
                return;
            }

            foreach (var d in selected) chart.Model.Drawings.Remove(d);
            chart.Model.Invalidate();
            DrawingsTouched(chart);
        };
        tools.MagnetChanged += m =>
        {
            _svc.Settings.DrawingMagnet = m;
            _svc.Settings.Save();
            foreach (var c in AllCharts)
            {
                c.View.Magnet = m;
                SyncDrawingTools(c);
            }

            _status(m switch { MagnetMode.Weak => "Weak magnet on", MagnetMode.Strong => "Strong magnet on", _ => "Magnet off" });
        };
        tools.StickyChanged += on =>
        {
            _svc.Settings.DrawingSticky = on;
            _svc.Settings.Save();
            foreach (var c in AllCharts)
            {
                c.View.StickyTool = on;
                SyncDrawingTools(c);
            }
        };
        tools.InkChanged += on =>
        {
            _svc.Settings.InkGestures = on;
            _svc.Settings.Save();
            foreach (var c in AllCharts)
            {
                c.View.InkGestures = on;
                SyncDrawingTools(c);
            }

            _status(on ? "Draw with a gesture: hold still on empty chart space, then trace the shape" : "Gesture drawing off");
        };
        tools.AfterBarChanged += on =>
        {
            _svc.Settings.DrawingBarAfterDrawing = on;
            _svc.Settings.Save();
            foreach (var c in AllCharts) SyncDrawingTools(c);
        };
        tools.LockChanged += on =>
        {
            if (chart.Model == null) return;
            chart.Model.Settings.LockDrawings = on;
            chart.Model.Invalidate();
            Workspace.Save(this);
            _status(on ? "Drawings locked: they can be selected but not moved" : "Drawings unlocked");
        };
        tools.HideChanged += on =>
        {
            if (chart.Model == null) return;
            chart.Model.Settings.HideDrawings = on;
            if (on)
                foreach (var d in chart.Model.Drawings) d.Selected = false;
            chart.Model.Invalidate();
            Workspace.Save(this);
            _status(on ? "Drawings hidden (still saved)" : "Drawings shown");
        };
        tools.HideIndicatorsChanged += on => SetIndicatorsHidden(chart, on);
        tools.FavoritesChanged += list =>
        {
            _svc.Settings.FavoriteDrawingTools = list.Select(k => k.ToString()).ToList();
            _svc.Settings.Save();
            foreach (var c in AllCharts) SyncDrawingTools(c);
        };

        view.Magnet = _svc.Settings.DrawingMagnet;
        view.StickyTool = _svc.Settings.DrawingSticky;
        view.InkGestures = _svc.Settings.InkGestures;

        var drawingBar = new SelectedDrawingBar(this, chart);
        chart.DrawingBar = drawingBar;
        viewHost.Children.Add(drawingBar);
        var inkChip = new InkChip(this, chart);
        chart.InkChip = inkChip;
        viewHost.Children.Add(inkChip);

        // keep drawing keeps the tool checked; without "drawing bar after drawing" the new drawing's bar waits for a click
        view.ToolFinished += () =>
        {
            tools.Check(view.Controller?.ActiveTool);
            if (!_svc.Settings.DrawingBarAfterDrawing) drawingBar.Suppress(chart.Model?.Drawings.FirstOrDefault(d => d.Selected));
        };
        // drawing with a tool shows hidden drawings again, as a gesture does: the strip follows and the chart keeps it
        view.DrawingsShown += () =>
        {
            SyncDrawingTools(chart);
            Workspace.Save(this);
        };
        view.DrawingSelected += _ => drawingBar.Suppress(null);
        view.InkFinished += r => OnInkFinished(chart, r);
        view.ModelChanged += () => SyncDrawingTools(chart);
        SyncDrawingTools(chart);
    }

    /// <summary>A chart's drawing bar state: magnet, keep drawing and gestures are global; lock and hide belong to the chart.</summary>
    internal void SyncDrawingTools(ChartTab chart)
    {
        if (chart.Tools == null) return;
        chart.Tools.SetStates(_svc.Settings.DrawingMagnet, _svc.Settings.DrawingSticky, chart.Model?.Settings.LockDrawings ?? false, chart.Model?.Settings.HideDrawings ?? false,
            _svc.Settings.InkGestures, _svc.Settings.DrawingBarAfterDrawing, chart.HiddenIndicators != null);
        chart.Tools.SetFavorites(_svc.Settings.FavoriteDrawingTools.Select(n => Enum.TryParse<DrawingKind>(n, out var k) && Enum.IsDefined(k) ? (DrawingKind?)k : null).OfType<DrawingKind>().ToList());
    }

    /// <summary>A gesture finished: the chip to switch the type or undo the erase.</summary>
    private void OnInkFinished(ChartTab chart, InkResult r)
    {
        if (r.Unhid)
        {
            SyncDrawingTools(chart);
            Workspace.Save(this);
        }

        if (r.Drawing != null && !_svc.Settings.DrawingBarAfterDrawing) chart.DrawingBar?.Suppress(r.Drawing);
        chart.InkChip?.Show(r);
        if (r.Erase) _status(InkChip.ErasedText(r.Removed.Count));
    }

    /// <summary>Hides or shows every indicator of the chart (the ones that were visible come back).</summary>
    internal void SetIndicatorsHidden(ChartTab chart, bool hidden)
    {
        if (chart.Model is not { } m) return;
        if (hidden && chart.HiddenIndicators == null)
        {
            chart.HiddenIndicators = m.Indicators.Where(s => s.Visible).ToList();
            foreach (var s in chart.HiddenIndicators) s.Visible = false;
        }
        else if (!hidden && chart.HiddenIndicators is { } list)
        {
            foreach (var s in list.Where(m.Indicators.Contains)) s.Visible = true;
            chart.HiddenIndicators = null;
        }

        m.Invalidate();
        SyncDrawingTools(chart);
        Workspace.Save(this);
        _status(hidden ? "Indicators hidden (the drawing bar eye shows them again)" : "Indicators shown");
    }

    // ===================================================================== toolbar actions

    /// <summary>Favorite indicators that exist in the catalog (in the order they were starred).</summary>
    internal IReadOnlyList<IndicatorInfo> FavoriteIndicators() =>
        _svc.Settings.FavoriteIndicators.Select(t => _svc.Catalog.Items.FirstOrDefault(i => i.Type.FullName == t)).OfType<IndicatorInfo>().ToList();

    /// <summary>Adds a catalog indicator to the chart (with its default template, when it has one).</summary>
    internal void AddIndicator(ChartTab? chart, IndicatorInfo info)
    {
        if (chart?.Model == null) return;
        try
        {
            var ind = (Indicator)Activator.CreateInstance(info.Type)!;
            bool templated = Workspace.ApplyDefaultIndicatorTemplate(_svc, ind);
            chart.Model.AddIndicator(ind, applyDefaultColors: !templated);
            chart.Model.Invalidate();
            Workspace.Save(this);
            _toolbar.Sync();
            _status($"{info.Name} added to the chart");
        }
        catch (Exception ex)
        {
            _status($"Could not create {info.Name}: {ex.InnerException?.Message ?? ex.Message}");
        }
    }

    /// <summary>The toolbar star: favorite indicators to add with one click and the chart templates.</summary>
    internal void ShowFavoritesMenu(ChartTab? chart, Control anchor)
    {
        if (chart?.Model == null) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var favorites = FavoriteIndicators();
        menu.Items.Add(new MenuItem { Header = "Favorite indicators", IsEnabled = false, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        if (favorites.Count == 0)
            menu.Items.Add(new MenuItem { Header = "None yet: in Indicators, right-click a library item → Add to favorites", IsEnabled = false });
        foreach (var f in favorites)
        {
            var info = f;
            var item = new MenuItem { Header = info.Name, Icon = Ui.Icon(MaterialIconKind.Star, 15, Ui.Accent) };
            item.Click += (_, _) => AddIndicator(chart, info);
            menu.Items.Add(item);
        }

        var open = new MenuItem { Header = "Indicator library…", Icon = Ui.Icon(MaterialIconKind.FunctionVariant, 16) };
        open.Click += (_, _) => OpenIndicators(chart);
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Chart templates", IsEnabled = false, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        foreach (var i in ChartTemplateItems(chart)) menu.Items.Add(i);
        menu.ShowAt(anchor);
    }

    /// <summary>Folder for chart screenshots: the user's Pictures/Trading Hub.</summary>
    internal static string ScreenshotFolder()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pictures)) pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(pictures, TradingHub.Common.Product.Name);
    }

    /// <summary>Renders the chart at double resolution to PNG bytes (no file I/O).</summary>
    internal static byte[]? RenderChartPng(ChartTab chart)
    {
        if (chart.Model == null) return null;
        var size = chart.View.Bounds.Size;
        int w = Math.Max(200, (int)size.Width), h = Math.Max(150, (int)size.Height);
        const float scale = 2;
        using var surface = SKSurface.Create(new SKImageInfo((int)(w * scale), (int)(h * scale)));
        surface.Canvas.Scale(scale);
        new ChartRenderer().Render(surface.Canvas, chart.Model, w, h, new ChartRenderState { Crosshair = false });
        chart.Model.Invalidate();
        using var img = surface.Snapshot();
        using var png = img.Encode(SKEncodedImageFormat.Png, 95);
        return png.ToArray();
    }

    /// <summary>Saves a PNG of the chart in Pictures/Trading Hub and says where.</summary>
    internal void SaveChartImage(ChartTab? chart)
    {
        if (chart?.Model == null) return;
        try
        {
            var bytes = RenderChartPng(chart);
            if (bytes == null) return;
            string dir = ScreenshotFolder();
            Directory.CreateDirectory(dir);
            string symbol = chart.Model.Settings.BlindReplay ? "Practice" : chart.Model.Instrument.Symbol;
            string safe = string.Concat($"{symbol}_{chart.Model.Period.DisplayName}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string path = Path.Combine(dir, string.Concat(safe, "_", DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture), ".png"));
            File.WriteAllBytes(path, bytes);
            _status($"Screenshot saved: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status($"Could not save the screenshot: {ex.Message}");
        }
    }

    /// <summary>Full screen for the chart's window (the same button or the system shortcut leaves it).</summary>
    internal void ToggleFullScreen(ChartTab? chart)
    {
        var window = chart?.Window ?? TopLevel.GetTopLevel(this) as Window;
        if (window == null) return;
        window.WindowState = window.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    // ===================================================================== one chart menu at a time

    private readonly ExclusiveMenuSlot<ContextMenu> _chartMenu = new();

    /// <summary>The chart context menu currently open (null when none).</summary>
    internal ContextMenu? OpenChartContextMenu => _chartMenu.Current;

    /// <summary>
    /// Opens a chart context menu after closing the previous one: repeated right-clicks used to stack menus, each with
    /// order prices from a different pointer position.
    /// </summary>
    private void OpenChartMenu(ContextMenu menu, Control target)
    {
        menu.Closed += (_, _) => _chartMenu.Closed(menu);
        _chartMenu.Opening(menu, previous =>
        {
            if (previous.IsOpen) previous.Close();
        });
        menu.Open(target);
    }
}

/// <summary>Holds at most one open menu: opening another closes the previous one first.</summary>
internal sealed class ExclusiveMenuSlot<TMenu> where TMenu : class
{
    public TMenu? Current { get; private set; }

    /// <summary>Records <paramref name="menu"/> as the open one and closes the previous one (if different).</summary>
    public void Opening(TMenu menu, Action<TMenu> close)
    {
        var previous = Current;
        Current = menu;
        if (previous != null && !ReferenceEquals(previous, menu)) close(previous);
    }

    /// <summary>A menu closed; only the current one clears the slot (a closing previous menu never clears the new one).</summary>
    public void Closed(TMenu menu)
    {
        if (ReferenceEquals(Current, menu)) Current = null;
    }
}
