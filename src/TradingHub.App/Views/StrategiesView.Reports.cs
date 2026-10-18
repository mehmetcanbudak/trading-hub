using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Lab;
using TradingHub.App.Services;

namespace TradingHub.App.Views;

/// <summary>Reportes del laboratorio de Claude: lista con el veredicto y visor del Markdown.</summary>
internal sealed partial class StrategiesView
{
    private readonly ListBox _reportList = new() { Background = Brushes.Transparent };
    private readonly MarkdownView _reportView = new();
    private readonly TextBlock _reportEmpty = new()
    {
        Text = "No reports yet. Enable Laboratory in the Code tab's Claude panel and ask it to research and optimize a strategy; the completed report appears here.",
        Classes = { "muted" },
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 460,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
    };
    private LabReport? _report;
    private Control? _reportBar;
    private Control? _reportColumn;

    private void ExportReport()
    {
        if (_report == null) return;
        try
        {
            var html = ReportExport.Export(_report);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", $"\"{html}\"") { UseShellExecute = false });
            _status($"Report exported: {System.IO.Path.GetFileName(html)} (in your browser: File → Export as PDF)");
        }
        catch (Exception ex)
        {
            _status("Could not export: " + ex.Message);
        }
    }

    internal static IBrush VerdictBrush(string verdict) => verdict switch
    {
        "ganadora" => Ui.Up,
        "prometedora" => Ui.Accent,
        "perdedora" => Ui.Down,
        "descartada" => Ui.Text2,
        _ => Ui.Text2,
    };

    internal static Control VerdictPill(string verdict)
    {
        if (string.IsNullOrEmpty(verdict)) return new Panel();
        var brush = VerdictBrush(verdict);
        return new Border
        {
            Child = new TextBlock { Text = TradingHub.Common.EnglishText.Of(verdict).ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.Bold, Foreground = brush },
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private Control BuildReportsPage()
    {
        _reportList.SelectionChanged += (_, _) =>
        {
            if (_reportList.SelectedItem is ListBoxItem { Tag: LabReport r }) ShowReport(r);
        };

        _reportView.Paper = true;
        var refresh = Ui.IconButton(MaterialIconKind.Refresh, "Refresh", RefreshReports, 16);
        var folder = Ui.IconButton(MaterialIconKind.FolderOpenOutline, "Open reports folder", () =>
        {
            System.IO.Directory.CreateDirectory(AppPaths.Reports);
            Windows.Shell.OpenFolder(AppPaths.Reports);
        }, 16);
        var head = new DockPanel { Margin = new Thickness(16, 14, 10, 10) };
        var tools = Ui.H(4, refresh, folder);
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        head.Children.Add(Ui.H3("Laboratory reports"));
        var left = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        left.Children.Add(new ScrollViewer { Content = _reportList, Margin = new Thickness(8, 0, 8, 8) });

        // el reporte se muestra como una hoja (fondo blanco), igual que al imprimirlo
        var page = new Border
        {
            Child = _reportView,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(44, 38),
            Margin = new Thickness(28, 18, 28, 28),
            MaxWidth = 1060,
            HorizontalAlignment = HorizontalAlignment.Center,
            BoxShadow = BoxShadows.Parse("0 6 28 0 #66000000"),
        };
        var export = Ui.Button("Export PDF / HTML", MaterialIconKind.FileExportOutline, ExportReport, "primary");
        ToolTip.SetTip(export, "Save an HTML report with embedded charts and open it in your browser (then use File → Export as PDF or Print)");
        var finder = Ui.Button("Show in Finder", MaterialIconKind.FolderOutline, () =>
        {
            if (_report != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", $"-R \"{_report.Path}\"") { UseShellExecute = false });
        });
        var bar = Ui.H(8, finder, export);
        bar.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Margin = new Thickness(28, 14, 28, 0);
        _reportBar = bar;
        var column = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        column.Children.Add(bar);
        column.Children.Add(new ScrollViewer { Content = page });
        _reportColumn = column;
        var right = new Panel();
        right.Children.Add(column);
        right.Children.Add(_reportEmpty);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,1,*") };
        grid.Children.Add(new Border { Child = left, Background = Ui.Bg1 });
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Ui.Line };
        Grid.SetColumn(split, 1);
        grid.Children.Add(split);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private void RefreshReports()
    {
        var keep = _report?.Path;
        _reportList.Items.Clear();
        foreach (var r in ReportStore.List())
        {
            var title = new TextBlock { Text = r.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            var sub = new TextBlock { Text = $"{r.Created:dd/MM/yyyy HH:mm}{(r.Strategy != null ? " · " + r.Strategy : "")}", Classes = { "faint", "small" } };
            var row = Ui.V(4, title, Ui.H(8, VerdictPill(r.Verdict), sub));
            row.Margin = new Thickness(4, 6);
            _reportList.Items.Add(new ListBoxItem { Content = row, Tag = r });
        }

        var select = _reportList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is LabReport lr && lr.Path == keep)
                     ?? _reportList.Items.OfType<ListBoxItem>().FirstOrDefault();
        _reportList.SelectedItem = select;
        if (select == null)
        {
            _report = null;
            _reportView.Markdown = "";
        }

        _reportEmpty.IsVisible = select == null;
        if (_reportBar != null) _reportBar.IsVisible = select != null;
        // sin reportes la hoja blanca quedaba como un rectángulo deforme en el medio
        if (_reportColumn != null) _reportColumn.IsVisible = select != null;
    }

    private void ShowReport(LabReport r)
    {
        _report = r;
        try
        {
            _reportView.BasePath = System.IO.Path.GetDirectoryName(r.Path);
            _reportView.Markdown = System.IO.File.ReadAllText(r.Path);
        }
        catch (Exception ex)
        {
            _reportView.Markdown = "Could not read report: " + ex.Message;
        }

        _reportEmpty.IsVisible = false;
        if (_reportColumn != null) _reportColumn.IsVisible = true;
    }

    /// <summary>Abre la pestaña Reportes en un reporte.</summary>
    internal void OpenReport(string path)
    {
        _tabs.Select("reports");
        ShowTab("reports");
        var item = _reportList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is LabReport lr && lr.Path == path);
        if (item != null) _reportList.SelectedItem = item;
    }
}
