using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.Charting.Drawings;

namespace TradingHub.App.Views;

/// <summary>
/// The floating bar of the selected drawing: color, width, line style, lock, quick alert, settings and delete, at the
/// top center of the chart while a drawing is selected.
/// </summary>
internal sealed class SelectedDrawingBar : Border
{
    private static readonly (string Name, uint Argb)[] Colors =
    {
        ("Blue", 0xFF3D8BFD), ("Sky", 0xFF38BDF8), ("Green", 0xFF26C281), ("Amber", 0xFFF2B53A), ("Orange", 0xFFFB8C00),
        ("Red", 0xFFF0525A), ("Pink", 0xFFF472B6), ("Purple", 0xFFB478FF), ("White", 0xFFFFFFFF), ("Gray", 0xFF8B93A1),
    };

    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private string _sig = "";
    private Guid? _suppressed;

    public SelectedDrawingBar(TradingView host, ChartTab chart)
    {
        _host = host;
        _chart = chart;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        // below the first legend lines (prices and indicators)
        Margin = new Thickness(0, 40, 0, 0);
        Padding = new Thickness(4, 3);
        CornerRadius = new CornerRadius(8);
        Background = Ui.Bg2;
        BorderBrush = Ui.Line;
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 4 14 0 #66000000");
        IsVisible = false;
    }

    internal ChartDrawing? Selected =>
        _chart.Model is { Settings.HideDrawings: false } m ? m.Drawings.FirstOrDefault(d => d.Selected && d.IsVisibleOn(m.Period) && d.Id != _suppressed) : null;

    /// <summary>Do not show it for this drawing (just made, with "drawing bar after drawing" off) until it is clicked; null shows it again.</summary>
    public void Suppress(ChartDrawing? d)
    {
        _suppressed = d?.Id;
        _sig = "-";
        Refresh();
    }

    /// <summary>Shows, hides or rebuilds the bar when the selected drawing changes (called from the chart loop).</summary>
    public void Refresh()
    {
        var d = Selected;
        string sig = d == null ? "" : $"{d.Id}|{d.Kind}|{d.ColorArgb}|{d.Width}|{d.EffectiveLineStyle}|{d.Locked}|{d.IsAlert}|{_chart.Model?.Settings.LockDrawings}";
        if (sig == _sig) return;
        _sig = sig;
        IsVisible = d != null;
        Child = d != null ? Build(d) : null;
    }

    private void Changed()
    {
        _host.DrawingsTouched(_chart);
        _sig = "";
        Refresh();
    }

    private static Button Small(Control content, string tip)
    {
        var b = new Button { Content = content, Height = 28, MinHeight = 0, MinWidth = 28, Padding = new Thickness(6, 0) }.Classes("icon");
        b.Width = double.NaN; // the "icon" style fixes 32 px and would clip the width text
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    private static Control Sep() => new Border { Width = 1, Height = 18, Margin = new Thickness(3, 0), Background = Ui.Line, VerticalAlignment = VerticalAlignment.Center };

    private Control Build(ChartDrawing d)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = DrawingToolStrip.ShortName(d.Kind), Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(Sep());

        // color
        var swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(d.ColorArgb)), BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
        var color = Small(swatch, "Color");
        color.Click += (_, _) =>
        {
            var grid = new WrapPanel { Width = 5 * 30, ItemSpacing = 4, LineSpacing = 4, Margin = new Thickness(8) };
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var (name, argb) in Colors)
            {
                var c = argb;
                var b = new Button { Width = 26, Height = 26, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromUInt32(c)), BorderBrush = d.ColorArgb == c ? Ui.Accent : Ui.Line, BorderThickness = new Thickness(d.ColorArgb == c ? 2 : 1) };
                ToolTip.SetTip(b, name);
                Avalonia.Automation.AutomationProperties.SetName(b, name);
                b.Click += (_, _) =>
                {
                    d.ColorArgb = c;
                    flyout.Hide();
                    Changed();
                };
                grid.Children.Add(b);
            }

            flyout.Content = grid;
            flyout.ShowAt(color);
        };
        row.Children.Add(color);

        // width
        var width = Small(new TextBlock { Text = $"{d.Width.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} px", FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, "Line width");
        width.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var w in new[] { 1f, 1.5f, 2f, 3f, 4f })
            {
                var v = w;
                var item = new MenuItem { Header = $"{v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} px", Icon = Math.Abs(d.Width - v) < 0.01 ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null };
                item.Click += (_, _) =>
                {
                    d.Width = v;
                    Changed();
                };
                menu.Items.Add(item);
            }

            menu.ShowAt(width);
        };
        row.Children.Add(width);

        // style
        var styleIcon = d.EffectiveLineStyle == LineStyleKind.Solid ? MaterialIconKind.Minus : MaterialIconKind.DotsHorizontal;
        var style = Small(Ui.Icon(styleIcon, 18), "Line style");
        style.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var (k, text) in new[] { (LineStyleKind.Solid, "Solid"), (LineStyleKind.Dashed, "Dashed"), (LineStyleKind.Dotted, "Dotted") })
            {
                var v = k;
                var item = new MenuItem { Header = text, Icon = d.EffectiveLineStyle == v ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null };
                item.Click += (_, _) =>
                {
                    d.LineStyle = v;
                    d.Dashed = false;
                    Changed();
                };
                menu.Items.Add(item);
            }

            menu.ShowAt(style);
        };
        row.Children.Add(style);
        row.Children.Add(Sep());

        // lock
        bool locked = d.Locked || _chart.Model?.Settings.LockDrawings == true;
        var lockButton = Small(Ui.Icon(locked ? MaterialIconKind.LockOutline : MaterialIconKind.LockOpenVariantOutline, 17, locked ? Ui.Accent : Ui.Text1), locked ? "Unlock (can be moved)" : "Lock (cannot be moved)");
        lockButton.Click += (_, _) =>
        {
            d.Locked = !d.Locked;
            Changed();
        };
        row.Children.Add(lockButton);

        // quick price alert on horizontal levels (the existing touch alert)
        if (d.Kind is DrawingKind.HorizontalLine or DrawingKind.HorizontalRay or DrawingKind.CrossLine)
        {
            var alert = Small(Ui.Icon(d.IsAlert ? MaterialIconKind.Bell : MaterialIconKind.BellPlusOutline, 17, d.IsAlert ? Ui.Accent : Ui.Text1), d.IsAlert ? "Remove alert" : "Alert when price touches");
            alert.Click += (_, _) =>
            {
                if (d.IsAlert)
                {
                    d.Alert = false;
                    // a legacy text marker still keeps the alert armed: clearing it keeps the drawing's own text otherwise
                    if (d.IsAlert) d.Text = "";
                }
                else d.Alert = true;

                Changed();
            };
            row.Children.Add(alert);
        }

        var settings = Small(Ui.Icon(MaterialIconKind.CogOutline, 17), "Drawing settings (also double-click)");
        settings.Click += (_, _) => _host.EditDrawing(_chart, d);
        row.Children.Add(settings);
        var delete = Small(Ui.Icon(MaterialIconKind.DeleteOutline, 17), "Delete drawing (Delete key)");
        delete.Click += (_, _) =>
        {
            _chart.Model?.Drawings.Remove(d);
            _chart.Model?.Invalidate();
            Changed();
        };
        row.Children.Add(delete);
        return row;
    }
}
