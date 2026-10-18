using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Strategies;

namespace TradingHub.App.Windows;

/// <summary>Crear o editar un portafolio en vivo: nombre, capital de referencia y qué estrategias corren juntas.</summary>
internal sealed class LivePortfolioWindow : Window
{
    private readonly AppServices _svc;
    private readonly LivePortfolioDef _def;
    private readonly TextBox _name = new();
    private readonly NumericUpDown _capital = new()
    {
        Minimum = 100,
        Maximum = 1_000_000_000,
        Increment = 1000,
        FormatString = "#,##0",
        NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly List<(CheckBox Box, string Key)> _members = new();

    public LivePortfolioWindow(AppServices svc, LivePortfolioDef? def)
    {
        _svc = svc;
        _def = def ?? new LivePortfolioDef { Name = "Portfolio " + (LiveStore.LoadPortfolios().Count(d => !d.Auto) + 1) };
        bool isNew = def == null;
        Title = isNew ? "New live portfolio" : $"Portfolio · {_def.Name}";
        Width = 560;
        Height = 660;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;
        _name.Text = _def.Name;
        _name.IsEnabled = !_def.Auto;
        _capital.Value = _def.Capital;

        var list = new StackPanel { Spacing = 6 };
        var entries = svc.Live.Entries();
        var others = LiveStore.LoadPortfolios().Where(d => !d.Auto && d.Id != _def.Id).ToList();
        foreach (var e in entries.OrderByDescending(e => e.Running).ThenBy(e => e.Info.Strategy))
        {
            var other = others.FirstOrDefault(d => d.Members.Contains(e.Key));
            var box = new CheckBox
            {
                IsChecked = _def.Members.Contains(e.Key),
                IsEnabled = !_def.Auto,
                Content = Ui.V(1,
                    new TextBlock { Text = $"{e.Info.Strategy} · {e.Info.Symbol} {e.Info.Period}", FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = $"{TradingHub.Common.EnglishText.Of(e.Info.Account)} · {e.SourceLabel} · {e.StateLabel}{(other != null ? $" · currently in «{other.Name}» (moves here)" : "")}{LockedNote(e)}",
                        Classes = { "faint", "small" },
                        TextWrapping = TextWrapping.Wrap,
                    }),
            };
            _members.Add((box, e.Key));
            list.Children.Add(box);
        }

        if (_members.Count == 0) list.Children.Add(new TextBlock { Text = "No live strategies to add yet. Launch one with New live strategy.", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });

        var body = new StackPanel { Spacing = 16, Margin = new Thickness(22, 18, 22, 18) };
        body.Children.Add(Ui.H2(isNew ? "New portfolio" : _def.Name));
        if (_def.Auto)
            body.Children.Add(new TextBlock { Text = "The automatic portfolio includes all live strategies without a custom portfolio. You can change its reference capital and risk agent.", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Ui.Field("Name", _name));
        body.Children.Add(Ui.Field("Reference capital", _capital, "Used for portfolio percentages, drawdown, Sharpe and the agent's risk budget."));
        body.Children.Add(Ui.V(8, Ui.H3("Strategies"), new TextBlock { Text = "A strategy belongs to one custom portfolio at a time. To add a new strategy, launch it with New live strategy and select this portfolio.", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap }, list));

        var save = Ui.Button(isNew ? "Create portfolio" : "Save", MaterialIconKind.Check, Save, "primary");
        var cancel = Ui.Button("Cancel", MaterialIconKind.Close, () => Close(null));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 10, 22, 16) };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        var bottom = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close(null);
        };
    }

    private void Save()
    {
        if (!_def.Auto)
        {
            _def.Name = string.IsNullOrWhiteSpace(_name.Text) ? _def.Name : _name.Text.Trim();
            _def.Members = _members.Where(m => m.Box.IsChecked == true).Select(m => m.Key).ToList();
        }

        _def.Capital = _capital.Value ?? _def.Capital;
        _svc.Live.SavePortfolio(_def);
        Close(_def.Id);
    }

    private static string LockedNote(LiveEntry e) =>
        "";
}
