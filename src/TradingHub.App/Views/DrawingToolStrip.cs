using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;

namespace TradingHub.App.Views;

/// <summary>
/// Each chart's drawing bar, on the left: tools in groups with the group arrow to the right of each button (the button
/// keeps the last tool used; a double-click keeps it active), favorite tools together and, at the bottom, magnet, keep
/// drawing, gesture drawing, the drawing bar after drawing, lock, hide, delete and choosing favorites.
/// </summary>
internal sealed class DrawingToolStrip : UserControl
{
    private sealed class Group
    {
        public required ToggleButton Button;
        public required DrawingKind?[] Kinds;
        public DrawingKind? Current;
    }

    internal static readonly Dictionary<DrawingKind, (MaterialIconKind Icon, string Name)> Info = new()
    {
        [DrawingKind.TrendLine] = (MaterialIconKind.ChartLineVariant, "Trend line"),
        [DrawingKind.Ray] = (MaterialIconKind.ArrowTopRightThin, "Ray"),
        [DrawingKind.ExtendedLine] = (MaterialIconKind.ArrowExpandHorizontal, "Extended line"),
        [DrawingKind.HorizontalLine] = (MaterialIconKind.Minus, "Horizontal line"),
        [DrawingKind.HorizontalRay] = (MaterialIconKind.ArrowRightThin, "Horizontal ray"),
        [DrawingKind.VerticalLine] = (MaterialIconKind.DragVertical, "Vertical line"),
        [DrawingKind.CrossLine] = (MaterialIconKind.Plus, "Cross line"),
        [DrawingKind.Rectangle] = (MaterialIconKind.RectangleOutline, "Rectangle / zone"),
        [DrawingKind.Ellipse] = (MaterialIconKind.EllipseOutline, "Ellipse"),
        [DrawingKind.ParallelChannel] = (MaterialIconKind.VectorLine, "Parallel channel (two clicks for the base line; the third handle sets the width)"),
        [DrawingKind.FibRetracement] = (MaterialIconKind.FormatLineSpacing, "Fibonacci retracement"),
        [DrawingKind.Brush] = (MaterialIconKind.Brush, "Brush (freehand)"),
        [DrawingKind.Highlighter] = (MaterialIconKind.FormatColorHighlight, "Highlighter"),
        [DrawingKind.Path] = (MaterialIconKind.VectorPolyline, "Path (one click per point; double-click or Enter finishes)"),
        [DrawingKind.Arrow] = (MaterialIconKind.ArrowTopRight, "Arrow"),
        [DrawingKind.Ruler] = (MaterialIconKind.RulerSquare, "Ruler (measure; also Shift + drag)"),
        [DrawingKind.LongPosition] = (MaterialIconKind.TrendingUp, "Long position (entry, stop, target)"),
        [DrawingKind.ShortPosition] = (MaterialIconKind.TrendingDown, "Short position (entry, stop, target)"),
        [DrawingKind.Text] = (MaterialIconKind.FormatText, "Text"),
        [DrawingKind.AnchoredVwap] = (MaterialIconKind.Anchor, "Anchored VWAP with volume-weighted deviation"),
        [DrawingKind.FixedRangeProfile] = (MaterialIconKind.ChartBar, "Fixed-range volume / delta profile"),
    };

    /// <summary>Short name of a tool (the part before the explanation in parentheses).</summary>
    internal static string ShortName(DrawingKind k) => Info.TryGetValue(k, out var info) ? info.Name.Split(" (")[0] : ChartDrawing.KindName(k);

    /// <summary>Each tool's keyboard shortcut ("Alt+H"); the view sets it from the shortcut settings.</summary>
    internal static Func<DrawingKind, string?>? ShortcutOf { get; set; }

    private static KeyGesture? GestureOf(DrawingKind k)
    {
        try
        {
            return ShortcutOf?.Invoke(k) is { Length: > 0 } g ? KeyGesture.Parse(g) : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private readonly List<Group> _groups = new();
    private readonly List<(DrawingKind Kind, ToggleButton Button)> _favButtons = new();
    private readonly StackPanel _favPanel = new() { Orientation = Orientation.Vertical, Spacing = 2 };
    private readonly Control _favDivider = Divider();
    private readonly ToggleButton _magnet, _sticky, _ink, _afterBar, _lock, _hide;
    private readonly Button _star;
    private MagnetMode _magnetMode, _lastMagnet = MagnetMode.Weak;
    private bool _syncing, _indicatorsHidden;
    private object? _lastClicked;
    private long _lastClickAt;
    private DrawingKind? _kept, _checked;
    private List<DrawingKind> _favorites = new();

    public DrawingToolStrip()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(4, 6, 2, 6) };
        DrawingKind?[][][] sections =
        {
            new[] { new DrawingKind?[] { null } },
            new[]
            {
                new DrawingKind?[] { DrawingKind.TrendLine, DrawingKind.Ray, DrawingKind.ExtendedLine, DrawingKind.HorizontalLine, DrawingKind.HorizontalRay, DrawingKind.VerticalLine, DrawingKind.CrossLine },
                new DrawingKind?[] { DrawingKind.Rectangle, DrawingKind.ParallelChannel, DrawingKind.Ellipse },
                new DrawingKind?[] { DrawingKind.FibRetracement },
            },
            new[] { new DrawingKind?[] { DrawingKind.Brush, DrawingKind.Highlighter, DrawingKind.Path }, new DrawingKind?[] { DrawingKind.Arrow } },
            new[] { new DrawingKind?[] { DrawingKind.AnchoredVwap, DrawingKind.FixedRangeProfile } },
            new[] { new DrawingKind?[] { DrawingKind.LongPosition }, new DrawingKind?[] { DrawingKind.ShortPosition }, new DrawingKind?[] { DrawingKind.Ruler } },
            new[] { new DrawingKind?[] { DrawingKind.Text } },
        };
        foreach (var section in sections)
        {
            if (panel.Children.Count > 0) panel.Children.Add(Divider());
            foreach (var kinds in section)
            {
                var g = new Group { Button = new ToggleButton().Classes("dtool"), Kinds = kinds, Current = kinds[0] };
                UpdateButton(g);
                g.Button.Click += (_, _) => Clicked(g, g.Current);
                _groups.Add(g);
                panel.Children.Add(Row(g.Button, kinds.Length > 1 ? more => ShowGroup(g, more) : null, "More tools in this group"));
            }
        }

        // favorite tools, together (chosen with the star below)
        panel.Children.Add(_favDivider);
        panel.Children.Add(_favPanel);
        _favDivider.IsVisible = _favPanel.IsVisible = false;

        // ---- bottom: how drawings are made and what happens to them
        panel.Children.Add(Divider());
        _magnet = new ToggleButton().Classes("dtool");
        _magnet.Click += (_, _) =>
        {
            if (_syncing) return;
            SetMagnet(_magnetMode == MagnetMode.Off ? _lastMagnet : MagnetMode.Off, raise: true);
        };
        panel.Children.Add(Row(_magnet, ShowMagnetMenu, "Weak or strong magnet"));
        _sticky = Toggle(MaterialIconKind.PencilLockOutline, "Keep drawing: the tool stays active after each drawing (Esc releases it; double-click a tool to keep only that one)", v => StickyChanged?.Invoke(v));
        panel.Children.Add(Row(_sticky, null, null));
        _ink = Toggle(MaterialIconKind.GestureTapHold, "Draw with a gesture: hold still on the chart until the ring fills, then trace the shape · hold this button to see the gestures", v => InkChanged?.Invoke(v));
        AddHold(_ink, () => ShowGuide(_ink));
        panel.Children.Add(Row(_ink, ShowInkMenu, "Gestures"));
        _afterBar = Toggle(MaterialIconKind.TuneVariant, "Show the drawing bar (color, width…) as soon as a drawing is finished", v => AfterBarChanged?.Invoke(v));
        panel.Children.Add(Row(_afterBar, null, null));
        _lock = Toggle(MaterialIconKind.LockOpenVariantOutline, "Lock all drawings (they can be selected but not moved)", v =>
        {
            UpdateLockIcon();
            LockChanged?.Invoke(v);
        });
        panel.Children.Add(Row(_lock, null, null));
        _hide = Toggle(MaterialIconKind.EyeOutline, "Hide all drawings (they stay saved and their alerts keep running); the arrow: hide indicators", v =>
        {
            UpdateHideIcon();
            HideChanged?.Invoke(v);
        });
        panel.Children.Add(Row(_hide, ShowHideMenu, "Hide drawings or indicators"));
        panel.Children.Add(Divider());
        var delete = new Button { Content = Ui.Icon(MaterialIconKind.DeleteOutline, 19) }.Classes("dtool");
        ToolTip.SetTip(delete, "Delete the selected drawing (Delete key); the arrow: delete all");
        Avalonia.Automation.AutomationProperties.SetName(delete, "Delete the selected drawing");
        delete.Click += (_, _) => DeleteSelectedRequested?.Invoke();
        panel.Children.Add(Row(delete, more =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
            var one = new MenuItem { Header = "Delete the selected drawing", Icon = Ui.Icon(MaterialIconKind.DeleteOutline, 16) };
            one.Click += (_, _) => DeleteSelectedRequested?.Invoke();
            var all = new MenuItem { Header = "Delete all drawings for this instrument", Icon = Ui.Icon(MaterialIconKind.DeleteSweepOutline, 16) };
            all.Click += (_, _) => ClearRequested?.Invoke();
            menu.Items.Add(one);
            menu.Items.Add(all);
            menu.ShowAt(more);
        }, "Delete all"));
        _star = new Button { Content = Ui.Icon(MaterialIconKind.StarOutline, 19) }.Classes("dtool");
        ToolTip.SetTip(_star, "Favorite drawing tools");
        Avalonia.Automation.AutomationProperties.SetName(_star, "Favorite drawing tools");
        _star.Click += (_, _) => ShowFavoritesMenu(_star);
        panel.Children.Add(Row(_star, null, null));

        _groups[0].Button.IsChecked = true;
        SetMagnet(MagnetMode.Off, raise: false);
        UpdateLockIcon();
        UpdateHideIcon();
        Content = new Border
        {
            Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden },
            Background = Ui.Bg1,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
    }

    public event Action<DrawingKind?>? ToolSelected;

    /// <summary>Double-click on a tool: it stays active until Esc or another tool is chosen.</summary>
    public event Action<DrawingKind>? ToolKept;

    public event Action? ClearRequested;
    public event Action? DeleteSelectedRequested;
    public event Action<MagnetMode>? MagnetChanged;
    public event Action<bool>? StickyChanged;
    public event Action<bool>? InkChanged;
    public event Action<bool>? AfterBarChanged;
    public event Action<bool>? LockChanged;
    public event Action<bool>? HideChanged;
    public event Action<bool>? HideIndicatorsChanged;
    public event Action<List<DrawingKind>>? FavoritesChanged;

    /// <summary>A tool click selects it; a second click right after (a double-click) keeps it active.</summary>
    private void Clicked(object source, DrawingKind? kind)
    {
        long now = Environment.TickCount64;
        bool second = ReferenceEquals(_lastClicked, source) && now - _lastClickAt < 450;
        _lastClicked = second ? null : source;
        _lastClickAt = now;
        Check(kind);
        if (second && kind is { } k) ToolKept?.Invoke(k);
        else ToolSelected?.Invoke(kind);
    }

    private static Control Divider() => new Border { Height = 1, Margin = new Thickness(6, 4, 8, 4) }.Classes("divider");

    /// <summary>A button with its group arrow on the right (right-clicking the button opens the group too).</summary>
    private static Control Row(Control button, Action<Control>? more, string? moreTip)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(10, GridUnitType.Pixel) } };
        grid.Children.Add(button);
        if (more != null)
        {
            var arrow = new Button { Content = Ui.Icon(MaterialIconKind.MenuRight, 12, Ui.Text2) }.Classes("dmore");
            if (moreTip != null)
            {
                ToolTip.SetTip(arrow, moreTip);
                Avalonia.Automation.AutomationProperties.SetName(arrow, moreTip);
            }

            arrow.Click += (_, _) => more(arrow);
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);
            button.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Right) return;
                e.Handled = true;
                more(arrow);
            }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        }

        return grid;
    }

    /// <summary>Holding the button for half a second does something else; that press does not toggle the button.</summary>
    private void AddHold(ToggleButton button, Action onHold)
    {
        DispatcherTimer? timer = null;
        bool held = false;
        button.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) return;
            held = false;
            timer?.Stop();
            timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Input, (_, _) =>
            {
                timer?.Stop();
                held = true;
                onHold();
            });
            timer.Start();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(PointerReleasedEvent, (_, _) => timer?.Stop(), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        button.AddHandler(PointerCaptureLostEvent, (_, _) => timer?.Stop(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        button.IsCheckedChanged += (_, _) =>
        {
            if (!held || _syncing) return;
            // it was a long press: the button returns to its previous state
            held = false;
            _syncing = true;
            button.IsChecked = !button.IsChecked;
            _syncing = false;
        };
    }

    private ToggleButton Toggle(MaterialIconKind icon, string tip, Action<bool> changed)
    {
        var t = new ToggleButton { Content = Ui.Icon(icon, 19) }.Classes("dtool");
        ToolTip.SetTip(t, tip);
        Avalonia.Automation.AutomationProperties.SetName(t, tip.Split(':')[0]);
        t.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing) changed(t.IsChecked == true);
        };
        return t;
    }

    private void UpdateLockIcon() => _lock.Content = Ui.Icon(_lock.IsChecked == true ? MaterialIconKind.LockOutline : MaterialIconKind.LockOpenVariantOutline, 19);

    private void UpdateHideIcon() => _hide.Content = Ui.Icon(_hide.IsChecked == true || _indicatorsHidden ? MaterialIconKind.EyeOffOutline : MaterialIconKind.EyeOutline, 19);

    private void SetMagnet(MagnetMode mode, bool raise)
    {
        _magnetMode = mode;
        if (mode != MagnetMode.Off) _lastMagnet = mode;
        _syncing = true;
        _magnet.IsChecked = mode != MagnetMode.Off;
        _syncing = false;
        _magnet.Content = Ui.Icon(mode == MagnetMode.Strong ? MaterialIconKind.MagnetOn : MaterialIconKind.Magnet, 19);
        string tip = mode switch
        {
            MagnetMode.Weak => "Weak magnet: points snap to the bar's high, low, open or close when the pointer is close",
            MagnetMode.Strong => "Strong magnet: points always snap to the bar's high, low, open or close",
            _ => "Magnet off (Shift turns it on while held)",
        };
        ToolTip.SetTip(_magnet, tip);
        Avalonia.Automation.AutomationProperties.SetName(_magnet, "Magnet");
        if (raise) MagnetChanged?.Invoke(mode);
    }

    private void ShowMagnetMenu(Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        foreach (var (mode, text, icon) in new[] { (MagnetMode.Weak, "Weak magnet (when the pointer is close)", MaterialIconKind.Magnet), (MagnetMode.Strong, "Strong magnet (always)", MaterialIconKind.MagnetOn), (MagnetMode.Off, "No magnet", MaterialIconKind.Close) })
        {
            var m = mode;
            var item = new MenuItem { Header = text, Icon = Ui.Icon(_magnetMode == m ? MaterialIconKind.Check : icon, 16) };
            item.Click += (_, _) => SetMagnet(m, raise: true);
            menu.Items.Add(item);
        }

        menu.ShowAt(anchor);
    }

    private void ShowInkMenu(Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        bool on = _ink.IsChecked == true;
        var toggle = new MenuItem { Header = "Draw with a gesture", Icon = Ui.Icon(on ? MaterialIconKind.Check : MaterialIconKind.GestureTapHold, 16) };
        toggle.Click += (_, _) => _ink.IsChecked = !on;
        var guide = new MenuItem { Header = "Show the gestures…", Icon = Ui.Icon(MaterialIconKind.HelpCircleOutline, 16) };
        guide.Click += (_, _) => ShowGuide(_ink);
        menu.Items.Add(toggle);
        menu.Items.Add(guide);
        menu.ShowAt(anchor);
    }

    /// <summary>The gesture help: which stroke makes each drawing.</summary>
    internal static void ShowGuide(Control anchor) =>
        new Flyout { Placement = PlacementMode.RightEdgeAlignedTop, Content = new Border { Padding = new Thickness(8, 6), Child = BuildGuide() } }.ShowAt(anchor);

    internal static Control BuildGuide()
    {
        var rows = new StackPanel { Spacing = 7, Width = 380 };
        rows.Children.Add(new TextBlock { Text = "Draw with a gesture", FontWeight = FontWeight.SemiBold, FontSize = 14 });
        rows.Children.Add(new TextBlock { Text = "Hold the mouse button still on empty chart space until the ring fills. Without releasing, trace the shape:", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        foreach (var (glyph, text) in new[]
                 {
                     ("M2,12 L30,12", "Horizontal stroke → horizontal line"),
                     ("M16,2 L16,22", "Vertical stroke → vertical line"),
                     ("M2,20 L30,4", "Diagonal → trend line (to the right edge: ray)"),
                     ("M4,12 A12,8 0 1 1 28,12 A12,8 0 1 1 4,12", "A loop → ellipse"),
                     ("M4,4 L28,4 L28,20 L4,20 Z", "A box → rectangle"),
                     ("M2,20 L28,4 L19,5", "A stroke with a small hook at the end → arrow"),
                     ("M2,5 L28,5 L4,19 L30,19", "A Z → Fibonacci retracement (between the first and last strokes)"),
                     ("M2,12 L7,4 L11,20 L15,4 L19,20 L23,4 L28,16", "A scribble over drawings → erases the drawings it touches"),
                     ("M2,18 L10,6 L18,14 L30,4", "Any other shape → path"),
                 })
        {
            var shape = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(glyph),
                Stroke = Ui.Accent,
                StrokeThickness = 1.8,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Width = 32,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            row.Children.Add(shape);
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
            rows.Children.Add(row);
        }

        rows.Children.Add(new TextBlock { Text = "Order lines, stop/target handles and the Chart Trader always take the click first; a gesture only starts on empty chart space. When you release, a chip lets you switch the drawing type (line to ray, rectangle to ellipse…) or delete it. With the magnet, points snap to bars. Esc cancels the stroke.", TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } });
        return rows;
    }

    private void ShowHideMenu(Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        bool drawings = _hide.IsChecked == true;
        var d = new MenuItem { Header = "Hide drawings", Icon = Ui.Icon(drawings ? MaterialIconKind.Check : MaterialIconKind.PencilOutline, 16) };
        d.Click += (_, _) => _hide.IsChecked = !drawings;
        var i = new MenuItem { Header = "Hide indicators", Icon = Ui.Icon(_indicatorsHidden ? MaterialIconKind.Check : MaterialIconKind.FunctionVariant, 16) };
        i.Click += (_, _) => SetIndicatorsHidden(!_indicatorsHidden, raise: true);
        bool all = drawings && _indicatorsHidden;
        var both = new MenuItem { Header = all ? "Show all" : "Hide all", Icon = Ui.Icon(all ? MaterialIconKind.EyeOutline : MaterialIconKind.EyeOffOutline, 16) };
        both.Click += (_, _) =>
        {
            if (_hide.IsChecked == all) _hide.IsChecked = !all;
            if (_indicatorsHidden == all) SetIndicatorsHidden(!all, raise: true);
        };
        menu.Items.Add(d);
        menu.Items.Add(i);
        menu.Items.Add(new Separator());
        menu.Items.Add(both);
        menu.ShowAt(anchor);
    }

    private void SetIndicatorsHidden(bool hidden, bool raise)
    {
        _indicatorsHidden = hidden;
        UpdateHideIcon();
        if (raise) HideIndicatorsChanged?.Invoke(hidden);
    }

    private void ShowFavoritesMenu(Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedBottom };
        menu.Items.Add(new MenuItem { Header = "Favorite tools", IsEnabled = false, FontWeight = FontWeight.SemiBold });
        if (_favorites.Count == 0) menu.Items.Add(new MenuItem { Header = "None yet: choose them below and they appear together on the bar", IsEnabled = false });
        foreach (var k in _favorites)
        {
            var kind = k;
            var item = new MenuItem { Header = Info[kind].Name, Icon = Ui.Icon(Info[kind].Icon, 16) };
            item.Click += (_, _) =>
            {
                Check(kind);
                ToolSelected?.Invoke(kind);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Choose favorites", Icon = Ui.Icon(MaterialIconKind.StarOutline, 16) };
        foreach (var k in Info.Keys)
        {
            var kind = k;
            bool fav = _favorites.Contains(kind);
            var item = new MenuItem { Header = Info[kind].Name, Icon = Ui.Icon(fav ? MaterialIconKind.Star : Info[kind].Icon, 16, fav ? Ui.Accent : null) };
            item.Click += (_, _) =>
            {
                var list = _favorites.ToList();
                if (!list.Remove(kind)) list.Add(kind);
                SetFavorites(list);
                FavoritesChanged?.Invoke(list);
            };
            edit.Items.Add(item);
        }

        menu.Items.Add(edit);
        menu.ShowAt(anchor);
    }

    /// <summary>Favorite tools appear together on the bar.</summary>
    public void SetFavorites(IReadOnlyList<DrawingKind> favorites)
    {
        var list = favorites.Where(Info.ContainsKey).Distinct().ToList();
        if (list.SequenceEqual(_favorites) && _favButtons.Count == list.Count) return;
        _favorites = list;
        _favPanel.Children.Clear();
        _favButtons.Clear();
        foreach (var k in _favorites)
        {
            var kind = k;
            var b = new ToggleButton { Content = Ui.Icon(Info[kind].Icon, 19) }.Classes("dtool");
            ToolTip.SetTip(b, Tip(kind));
            Avalonia.Automation.AutomationProperties.SetName(b, Info[kind].Name);
            b.Click += (_, _) => Clicked(b, kind);
            _favButtons.Add((kind, b));
            _favPanel.Children.Add(Row(b, null, null));
        }

        _favDivider.IsVisible = _favPanel.IsVisible = _favorites.Count > 0;
        _star.Content = Ui.Icon(_favorites.Count > 0 ? MaterialIconKind.Star : MaterialIconKind.StarOutline, 19, _favorites.Count > 0 ? Ui.Accent : null);
        Check(_checked);
    }

    /// <summary>Shows the chart's state without raising events.</summary>
    public void SetStates(MagnetMode magnet, bool sticky, bool locked, bool hidden, bool ink, bool afterBar, bool indicatorsHidden)
    {
        SetMagnet(magnet, raise: false);
        _syncing = true;
        _sticky.IsChecked = sticky;
        _ink.IsChecked = ink;
        _afterBar.IsChecked = afterBar;
        _lock.IsChecked = locked;
        _hide.IsChecked = hidden;
        _syncing = false;
        _indicatorsHidden = indicatorsHidden;
        UpdateLockIcon();
        UpdateHideIcon();
    }

    internal bool InkChecked => _ink.IsChecked == true;

    private static string Tip(DrawingKind k) => ShortcutOf?.Invoke(k) is { Length: > 0 } s ? Info[k].Name + " · " + Services.Hotkeys.Pretty(s) : Info[k].Name;

    private void UpdateButton(Group g)
    {
        if (g.Current is { } k)
        {
            var icon = Ui.Icon(Info[k].Icon, 19);
            if (_kept == k)
            {
                // kept with a double-click: a dot at the bottom right
                var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 5, Height = 5, Fill = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -6, -6) };
                g.Button.Content = new Panel { Children = { icon, dot } };
            }
            else g.Button.Content = icon;

            ToolTip.SetTip(g.Button, Tip(k) + "\n" + (_kept == k ? "Kept: stays active after each drawing (Esc releases it)" : "Double-click: keep it active"));
            Avalonia.Automation.AutomationProperties.SetName(g.Button, Info[k].Name);
        }
        else
        {
            g.Button.Content = Ui.Icon(MaterialIconKind.CursorDefaultOutline, 19);
            ToolTip.SetTip(g.Button, "Cursor (Esc)");
            Avalonia.Automation.AutomationProperties.SetName(g.Button, "Cursor (Esc)");
        }
    }

    private void ShowGroup(Group g, Control anchor)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.RightEdgeAlignedTop };
        foreach (var k in g.Kinds.OfType<DrawingKind>())
        {
            var kind = k;
            var item = new MenuItem { Header = Info[kind].Name, Icon = Ui.Icon(Info[kind].Icon, 16), InputGesture = GestureOf(kind) };
            if (Equals(g.Current, kind)) item.FontWeight = FontWeight.SemiBold;
            item.Click += (_, _) =>
            {
                g.Current = kind;
                UpdateButton(g);
                Check(kind);
                ToolSelected?.Invoke(kind);
            };
            menu.Items.Add(item);
        }

        menu.ShowAt(anchor);
    }

    /// <summary>The tool kept with a double-click (null = none): its button shows a dot.</summary>
    public void MarkKept(DrawingKind? kind)
    {
        if (_kept == kind) return;
        _kept = kind;
        foreach (var g in _groups) UpdateButton(g);
    }

    /// <summary>Marks the active tool (null = cursor); its group button switches to it.</summary>
    public void Check(DrawingKind? kind)
    {
        _checked = kind;
        foreach (var g in _groups)
        {
            bool has = g.Kinds.Contains(kind);
            if (has && kind != null && !Equals(g.Current, kind))
            {
                g.Current = kind;
                UpdateButton(g);
            }

            g.Button.IsChecked = has;
        }

        foreach (var (k, b) in _favButtons) b.IsChecked = kind == k;
        if (kind == null || kind != _kept) MarkKept(null);
    }
}
