using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.Charting.Drawings;

namespace TradingHub.App.Views;

/// <summary>
/// The chip shown where a gesture was released: which drawing it made, the other kinds the same stroke can become and
/// delete; after a scribble, how many drawings it erased and undo. It leaves after a few seconds or with the next click on
/// the chart.
/// </summary>
internal sealed class InkChip : Border
{
    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private readonly DispatcherTimer _timer;

    public InkChip(TradingView host, ChartTab chart)
    {
        _host = host;
        _chart = chart;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Padding = new Thickness(4, 3);
        CornerRadius = new CornerRadius(8);
        Background = Ui.Bg2;
        BorderBrush = Ui.Line;
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 4 14 0 #66000000");
        IsVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, _) => Hide());
        // it stays while the mouse is over it
        PointerEntered += (_, _) => _timer.Stop();
        PointerExited += (_, _) =>
        {
            if (IsVisible) _timer.Start();
        };
        chart.View.AddHandler(PointerPressedEvent, (_, _) => Hide(), RoutingStrategies.Tunnel, handledEventsToo: true);
        // undo and retype act on the chart's current drawings: another instrument or period closes the chip
        chart.View.ModelChanged += Hide;
    }

    public void Hide()
    {
        _timer.Stop();
        IsVisible = false;
    }

    public void Show(InkResult r)
    {
        Child = r.Erase ? BuildErase(r) : r.Drawing != null ? BuildCreated(r) : null;
        if (Child == null)
        {
            Hide();
            return;
        }

        // next to the release point, inside the chart and clear of the price axis (wrapping on narrow charts)
        var size = _chart.View.Bounds.Size;
        double right = size.Width - TradingHub.Charting.ChartLayout.PriceAxisWidth - 4;
        MaxWidth = Math.Max(160, right - 4);
        Measure(new Size(MaxWidth, double.PositiveInfinity));
        double w = DesiredSize.Width, h = DesiredSize.Height;
        double x = r.X + 14 + w <= right ? r.X + 14 : Math.Max(4, r.X - 14 - w);
        if (x + w > right) x = Math.Max(4, right - w);
        double y = r.Y + 14 + h <= size.Height - 30 ? r.Y + 14 : Math.Max(4, r.Y - 14 - h);
        Margin = new Thickness(x, y, 0, 0);
        IsVisible = true;
        _timer.Stop();
        _timer.Start();
    }

    private static Button Small(Control content, string tip)
    {
        var b = new Button { Content = content, Height = 28, MinHeight = 0, MinWidth = 28, Padding = new Thickness(6, 0) }.Classes("icon");
        b.Width = double.NaN;
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    private static Control Sep() => new Border { Width = 1, Height = 18, Margin = new Thickness(3, 0), Background = Ui.Line, VerticalAlignment = VerticalAlignment.Center };

    private static Control Label(MaterialIconKind icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        row.Children.Add(Ui.Icon(icon, 15, Ui.Accent));
        row.Children.Add(new TextBlock { Text = text, Classes = { "small" }, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static MaterialIconKind IconOf(DrawingKind k) => DrawingToolStrip.Info.TryGetValue(k, out var i) ? i.Icon : MaterialIconKind.PencilOutline;

    private Control BuildCreated(InkResult r)
    {
        var d = r.Drawing!;
        var row = new WrapPanel { ItemSpacing = 2, LineSpacing = 2, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Label(IconOf(d.Kind), DrawingToolStrip.ShortName(d.Kind)));
        if (r.Alternatives.Count > 0 && r.Geometry != null)
        {
            row.Children.Add(Sep());
            row.Children.Add(new TextBlock { Text = "Switch to:", Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) });
            foreach (var k in r.Alternatives)
            {
                var kind = k;
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                content.Children.Add(Ui.Icon(IconOf(kind), 15));
                content.Children.Add(new TextBlock { Text = DrawingToolStrip.ShortName(kind), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var b = Small(content, "Same stroke, another drawing type");
                Avalonia.Automation.AutomationProperties.SetName(b, DrawingToolStrip.ShortName(kind));
                b.Click += (_, _) =>
                {
                    if (_chart.View.Controller is not { } c || _chart.Model?.Drawings.Contains(d) != true) return;
                    c.RetypeInk(d, r.Geometry, kind);
                    _host.DrawingsTouched(_chart);
                    Show(new InkResult { Drawing = d, Geometry = r.Geometry, Alternatives = InkDrawings.Alternatives(kind), X = r.X, Y = r.Y });
                };
                row.Children.Add(b);
            }
        }

        row.Children.Add(Sep());
        var delete = Small(Ui.Icon(MaterialIconKind.DeleteOutline, 17), "Delete it");
        delete.Click += (_, _) =>
        {
            if (_chart.Model == null) return;
            _chart.Model.Drawings.Remove(d);
            _chart.Model.Invalidate();
            _host.DrawingsTouched(_chart);
            Hide();
        };
        row.Children.Add(delete);
        var help = Small(Ui.Icon(MaterialIconKind.HelpCircleOutline, 16), "Gestures");
        help.Click += (_, _) => DrawingToolStrip.ShowGuide(help);
        row.Children.Add(help);
        return row;
    }

    internal static string ErasedText(int count) => count switch
    {
        0 => "The scribble touched no drawing",
        1 => "Erased one drawing",
        _ => $"Erased {count} drawings",
    };

    private Control BuildErase(InkResult r)
    {
        var row = new WrapPanel { ItemSpacing = 2, LineSpacing = 2, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Label(MaterialIconKind.Eraser, ErasedText(r.Removed.Count)));
        if (r.Removed.Count > 0)
        {
            row.Children.Add(Sep());
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(Ui.Icon(MaterialIconKind.UndoVariant, 15));
            content.Children.Add(new TextBlock { Text = "Undo", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var undo = Small(content, "Restore what the scribble erased");
            undo.Click += (_, _) =>
            {
                _chart.View.Controller?.UndoErase(r.Removed);
                _host.DrawingsTouched(_chart);
                Hide();
            };
            row.Children.Add(undo);
        }

        return row;
    }
}
