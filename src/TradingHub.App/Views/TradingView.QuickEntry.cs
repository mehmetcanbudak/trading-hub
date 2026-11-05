using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Views;

// Inspired by KVP 50220ba91613161685f84384825d855cb4dbeb76; selection and lifetime handling redesigned.
public sealed partial class TradingView
{
    private sealed record ChartEntryOption(string Label, Instrument? Instrument = null, ChartPeriod? Period = null);
    private sealed class ChartEntrySession(ChartTab tab, ChartModel model, Window owner)
    {
        public readonly ChartTab Tab = tab;
        public readonly ChartModel Model = model;
        public readonly Window Owner = owner;
        public readonly TextBox Input = new() { Watermark = "Symbol or period: BTC, 700.HK, 15, 1h…", MinWidth = 340, FontSize = 16, MaxLength = 128 };
        public readonly ListBox Results = new() { MaxHeight = 280, MinHeight = 36 };
        public readonly ComboBox Connection = new() { MinWidth = 300 };
        public readonly TextBlock Hint = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 470, Classes = { "faint", "small" } };
        public readonly Popup Popup = new() { Placement = PlacementMode.TopEdgeAlignedLeft, IsLightDismissEnabled = true, HorizontalOffset = 24, VerticalOffset = 24 };
        public CancellationTokenSource? Search;
        public int Generation;
        public string ProfileId = tab.FeedId;
        public bool Closing;
        public EventHandler? Deactivated;
        public Action? ConnectionsChanged;
    }

    private ChartEntrySession? _chartEntry;
    private Window? _chartHelp;

    internal void InitializeChartQuickEntry(ChartTab chart)
    {
        chart.View.CommittedText += text => OnChartTyped(chart, text);
        chart.View.ModelChanged += () => CancelChartQuickEntry(chart);
        chart.View.DetachedFromVisualTree += (_, _) => CancelChartQuickEntry(chart);
        chart.View.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_chartEntry is not { } session || !ReferenceEquals(session.Tab, chart)) return;
            if (!SessionHasFocus(session)) CloseChartEntry(session, restoreFocus: false);
        }, DispatcherPriority.Input);
    }

    internal void CancelChartQuickEntry(ChartTab? chart = null)
    {
        if (_chartEntry is { } session && (chart == null || ReferenceEquals(session.Tab, chart)))
            CloseChartEntry(session, restoreFocus: false);
        if (chart == null) { _chartHelp?.Close(); _chartHelp = null; }
    }

    internal void OnChartTyped(ChartTab chart, string text)
    {
        if (chart.Model == null || !chart.View.IsFocused || !chart.View.IsEffectivelyVisible || TopLevel.GetTopLevel(chart.View) is not Window owner) return;
        if (text == "?") { ShowChartShortcuts(chart); return; }
        if (chart.Model.Settings.BlindReplay) { _status("Reveal practice before changing its symbol or period."); return; }
        if (_chartEntry is { } session && ReferenceEquals(session.Tab, chart) && IsCurrentChartEntry(session))
        {
            session.Input.Text += text;
            session.Input.CaretIndex = session.Input.Text?.Length ?? 0;
            return;
        }
        OpenChartEntry(chart, owner, text);
    }

    private void OpenChartEntry(ChartTab chart, Window owner, string initialText)
    {
        CancelChartQuickEntry();
        if (chart.Model == null) return;
        if (chart.Model.Settings.BlindReplay) { _status("Reveal practice before changing its symbol or period."); return; }
        var session = new ChartEntrySession(chart, chart.Model, owner);
        _chartEntry = session;
        AutomationProperties.SetName(session.Input, "Chart symbol or period");
        AutomationProperties.SetName(session.Results, "Visible chart navigation results");
        AutomationProperties.SetName(session.Connection, "Quick entry data connection");
        session.Results.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ChartEntryOption>((item, _) =>
            new TextBlock { Text = item?.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = 450 });
        foreach (var profile in _svc.Connections.Enabled.Where(p => p.HasMarketData || p.Id == chart.FeedId))
            session.Connection.Items.Add(new ComboBoxItem { Content = profile.Name, DataContext = profile.Id, Tag = Hotkeys.ChartQuickEntryScope });
        session.Connection.SelectedItem = session.Connection.Items.OfType<ComboBoxItem>().FirstOrDefault(p => (string)p.DataContext! == chart.FeedId);
        session.Connection.Tag = Hotkeys.ChartQuickEntryScope;
        session.Connection.SelectionChanged += (_, _) =>
        {
            if (session.Connection.SelectedItem is not ComboBoxItem { DataContext: string id }) return;
            session.ProfileId = id;
            UpdateChartEntry(session);
        };
        session.Input.TextChanged += (_, _) => UpdateChartEntry(session);
        session.Input.KeyDown += (_, e) =>
        {
            if (HasPreedit(session.Input)) return;
            switch (e.Key)
            {
                case Key.Escape: CloseChartEntry(session, restoreFocus: true); e.Handled = true; break;
                case Key.Down:
                    if (session.Results.ItemCount > 0) session.Results.SelectedIndex = Math.Min(session.Results.ItemCount - 1, session.Results.SelectedIndex + 1);
                    session.Results.ScrollIntoView(session.Results.SelectedIndex);
                    e.Handled = true;
                    break;
                case Key.Up:
                    if (session.Results.ItemCount > 0) session.Results.SelectedIndex = Math.Max(0, session.Results.SelectedIndex - 1);
                    session.Results.ScrollIntoView(session.Results.SelectedIndex);
                    e.Handled = true;
                    break;
                case Key.Enter: _ = ApplyChartEntryAsync(session); e.Handled = true; break;
            }
        };
        session.Results.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = ApplyChartEntryAsync(session); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseChartEntry(session, restoreFocus: true); e.Handled = true; }
        };
        session.Results.DoubleTapped += (_, _) => _ = ApplyChartEntryAsync(session);
        var apply = Ui.Button("Open selected", MaterialIconKind.Check, () => _ = ApplyChartEntryAsync(session), "primary");
        var cancel = Ui.Button("Cancel", MaterialIconKind.Close, () => CloseChartEntry(session, restoreFocus: true));
        var root = Ui.V(8, Ui.H3("Chart symbol / period"), session.Input, session.Connection, session.Results, session.Hint,
            Ui.Faint("Choose a data connection explicitly. The trading account is not selected here."), Ui.H(8, apply, cancel));
        root.Tag = Hotkeys.ChartQuickEntryScope;
        root.Margin = new Thickness(12);
        root.MaxWidth = 520;
        root.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && !HasPreedit(session.Input)) { CloseChartEntry(session, restoreFocus: true); e.Handled = true; }
        }, RoutingStrategies.Bubble);
        root.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (IsCurrentChartEntry(session) && !SessionHasFocus(session)) CloseChartEntry(session, restoreFocus: false);
        }, DispatcherPriority.Input);
        ((ISetLogicalParent)session.Popup).SetParent(chart.View);
        session.Popup.Child = new Border { Child = root, Background = Ui.Bg2, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        session.Popup.PlacementTarget = chart.View;
        session.Popup.Closed += (_, _) => CloseChartEntry(session, restoreFocus: false);
        session.Deactivated = (_, _) => CloseChartEntry(session, restoreFocus: false);
        owner.Deactivated += session.Deactivated;
        session.ConnectionsChanged = () => Dispatcher.UIThread.Post(() => CloseChartEntry(session, restoreFocus: false));
        _svc.Connections.Changed += session.ConnectionsChanged;
        session.Input.Text = initialText;
        session.Popup.IsOpen = true;
        UpdateChartEntry(session);
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCurrentChartEntry(session)) return;
            session.Input.Focus();
            session.Input.CaretIndex = session.Input.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private static bool HasPreedit(TextBox box)
    {
        foreach (var visual in box.GetVisualDescendants())
            if (visual is TextPresenter presenter && !string.IsNullOrEmpty(presenter.PreeditText)) return true;
        return false;
    }

    private static bool SessionHasFocus(ChartEntrySession session)
    {
        if (session.Connection.IsDropDownOpen) return true;
        var focused = TopLevel.GetTopLevel(session.Input)?.FocusManager?.GetFocusedElement() as Visual;
        for (var node = focused; node != null; node = node.GetVisualParent())
            if (node is Control control && ReferenceEquals(control.Tag, Hotkeys.ChartQuickEntryScope)) return true;
        return false;
    }

    private bool IsCurrentChartEntry(ChartEntrySession session) => ReferenceEquals(_chartEntry, session) && !session.Closing &&
        ReferenceEquals(session.Tab.Model, session.Model) && session.Tab.View.IsEffectivelyVisible &&
        ReferenceEquals(TopLevel.GetTopLevel(session.Tab.View), session.Owner);

    private void CloseChartEntry(ChartEntrySession session, bool restoreFocus)
    {
        if (session.Closing) return;
        session.Closing = true;
        session.Generation++;
        session.Search?.Cancel();
        session.Search?.Dispose();
        session.Search = null;
        if (session.Deactivated != null) session.Owner.Deactivated -= session.Deactivated;
        if (session.ConnectionsChanged != null) _svc.Connections.Changed -= session.ConnectionsChanged;
        if (ReferenceEquals(_chartEntry, session)) _chartEntry = null;
        session.Popup.IsOpen = false;
        ((ISetLogicalParent)session.Popup).SetParent(null);
        session.Popup.Child = null;
        if (restoreFocus && ReferenceEquals(session.Tab.Model, session.Model) && session.Tab.View.IsEffectivelyVisible && session.Owner.IsActive)
            session.Tab.View.Focus();
    }

    internal static bool TryChartEntryPeriod(string text, out ChartPeriod period)
    {
        text = text.Trim();
        if (text.Length > 0 && text.All(char.IsAsciiDigit) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) && minutes is > 0 and <= 525600)
        { period = new ChartPeriod(PeriodType.Minutes, minutes); return true; }
        if (!ChartPeriod.TryParse(text, out period)) return false;
        decimal maximum = period.Type switch { PeriodType.Months => 1200, PeriodType.Weeks => 5200, PeriodType.Days => 36500, _ => 1000000 };
        return period.Value <= maximum && (period.Type is PeriodType.Volume or PeriodType.Delta || decimal.Truncate(period.Value) == period.Value);
    }

    private void UpdateChartEntry(ChartEntrySession session)
    {
        session.Search?.Cancel();
        session.Search?.Dispose();
        session.Search = null;
        int generation = ++session.Generation;
        session.Results.ItemsSource = null;
        session.Results.SelectedIndex = -1;
        if (!IsCurrentChartEntry(session) || !session.Popup.IsOpen) return;
        string text = session.Input.Text?.Trim() ?? "";
        session.Hint.Text = "Arrows select a visible result · Enter opens selected · Escape cancels · no keyboard orders";
        if (text.Length == 0) return;
        if (text == "?") { ShowChartShortcuts(session.Tab); return; }
        var cancellation = session.Search = new CancellationTokenSource();
        _ = LoadChartEntryAsync(session, text, session.ProfileId, generation, cancellation.Token);
    }

    private async Task LoadChartEntryAsync(ChartEntrySession session, string text, string profileId, int generation, CancellationToken token)
    {
        try
        {
            var instruments = await InstrumentPicker.InstrumentsAsync(_svc, profileId, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentChartEntry(session) || generation != session.Generation || profileId != session.ProfileId || session.Input.Text?.Trim() != text) return;
            var options = instruments.Where(i => InstrumentPicker.Matches(i, text))
                .OrderByDescending(i => i.Symbol.Equals(text, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(i => i.Symbol.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .ThenBy(i => i.Symbol.Length).ThenBy(i => i.Symbol, StringComparer.OrdinalIgnoreCase).Take(10)
                .Select(i => new ChartEntryOption($"{i.Symbol} · {i.Description}", Instrument: i)).ToList();
            bool exact = options.Any(i => i.Instrument!.Symbol.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (TryChartEntryPeriod(text, out var period))
                options.Insert(exact ? options.Count : 0, new ChartEntryOption($"Period → {period.DisplayName} ({period.Code})", Period: period));
            // Native ListBox selection is the only application authority; never apply a hidden first match.
            session.Results.ItemsSource = options;
            session.Results.SelectedIndex = exact || (options.Count > 0 && options[0].Period != null) ? 0 : -1;
            string connection = _svc.Connections.Find(profileId)?.Name ?? profileId;
            if (options.Count == 0) session.Hint.Text = $"No matches on {connection}. Choose another connection explicitly.";
            else if (profileId != session.Tab.FeedId) session.Hint.Text = $"Selected symbol opens on {connection}; account selection is not changed here. Arrows / Enter / Escape.";
            if (InstrumentPicker.CatalogSnapshot(profileId) is { } catalog && catalog.StatusText.Length > 0)
                session.Hint.Text += "\n" + catalog.StatusText;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsCurrentChartEntry(session) || generation != session.Generation || token.IsCancellationRequested) return;
            var options = new List<ChartEntryOption>();
            if (TryChartEntryPeriod(text, out var period)) options.Add(new($"Period → {period.DisplayName} ({period.Code})", Period: period));
            session.Results.ItemsSource = options;
            session.Results.SelectedIndex = options.Count == 1 ? 0 : -1;
            session.Hint.Text = "Instrument search unavailable: " + error.Message + ". Period selection remains available.";
        }
    }

    private async Task ApplyChartEntryAsync(ChartEntrySession session)
    {
        if (!IsCurrentChartEntry(session) || HasPreedit(session.Input) || session.Results.SelectedItem is not ChartEntryOption selected) return;
        var tab = session.Tab;
        string profileId = session.ProfileId;
        var period = session.Model.Period;
        var settings = session.Model.Settings.Clone();
        if (selected.Period is { } nextPeriod)
        {
            CloseChartEntry(session, restoreFocus: true);
            (tab.Window?.Toolbar ?? _toolbar).SetPeriod(nextPeriod);
            return;
        }
        if (selected.Instrument is not { } instrument) return;
        // Keep the captured chart, not whichever tab became active while discovery was running.
        CloseChartEntry(session, restoreFocus: false);
        await OpenChartSelectionAsync(tab, profileId, instrument.Symbol, period, settings, session.Model, session.Owner);
        if (tab.View.IsEffectivelyVisible && ReferenceEquals(TopLevel.GetTopLevel(tab.View), session.Owner) && session.Owner.IsActive) tab.View.Focus();
    }

    internal bool ExecuteChartNavigationHotkey(string action, ChartTab chart)
    {
        if (chart.Model == null) return false;
        switch (action)
        {
            case "reset-view": chart.View.Controller?.ResetView(); return true;
            case "rectangle-zoom": chart.View.Controller?.SelectRectangleZoom(); chart.View.Focus(); return true;
            case "toggle-session-lines": chart.Model.Settings.ShowSessionLines = !chart.Model.Settings.ShowSessionLines; chart.Model.Invalidate(); Workspace.Save(this); return true;
            case "chart-timezone": OpenChartSettings(chart); return true;
            case "chart-shortcuts": ShowChartShortcuts(chart); return true;
            case "chart-quick-entry":
                if (TopLevel.GetTopLevel(chart.View) is Window owner) OpenChartEntry(chart, owner, "");
                return true;
            default: return false;
        }
    }

    internal void ShowChartNavigation(ChartTab? chart, Control anchor)
    {
        if (chart?.Model == null) return;
        var menu = new MenuFlyout();
        void Item(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Item("Rectangle zoom · Ctrl/Command + drag", () => { chart.View.Controller?.SelectRectangleZoom(); chart.View.Focus(); });
        Item("Reset chart view · Alt+R", () => chart.View.Controller?.ResetView());
        Item(chart.Model.Viewport.AutoScale ? "Disable automatic price scale" : "Enable automatic price scale", () => chart.View.Controller?.ToggleAutoScale());
        Item(chart.Model.Settings.ShowSessionLines ? "Hide session lines" : "Show session lines", () => { chart.Model.Settings.ShowSessionLines = !chart.Model.Settings.ShowSessionLines; chart.Model.Invalidate(); Workspace.Save(this); });
        Item(chart.Model.Settings.ShowClock ? "Hide chart clock" : "Show chart clock", () => { chart.Model.Settings.ShowClock = !chart.Model.Settings.ShowClock; Workspace.Save(this); });
        Item("Timezone / clock settings…", () => OpenChartSettings(chart));
        Item("Symbol / period entry…", () => { if (TopLevel.GetTopLevel(chart.View) is Window owner) OpenChartEntry(chart, owner, ""); });
        Item("Keyboard help · ?", () => ShowChartShortcuts(chart));
        menu.ShowAt(anchor);
    }

    private void ShowChartShortcuts(ChartTab chart)
    {
        if (TopLevel.GetTopLevel(chart.View) is not Window owner) return;
        if (_chartEntry is { } entry) CloseChartEntry(entry, restoreFocus: false);
        _chartHelp?.Close();
        var body = Ui.V(8, Ui.H2("Chart keyboard help"),
            Ui.Faint("Type a symbol or period on the focused chart. Choose the visible result with arrows; Enter opens it; Escape cancels. Numeric symbols and periods appear as separate choices."),
            Ui.Faint("Ctrl/Command + drag: rectangle zoom. Wheel: time zoom. Shift + wheel: pan. Ctrl/Command + wheel: price zoom. Alt+R: reset. Escape: cancel drawing/gesture."),
            Ui.Faint("Quick entry only changes chart data or period. It cannot enter orders. Connection switching is explicit; trading accounts and order confirmations remain separate."));
        foreach (var definition in Hotkeys.All.Where(h => h.Group is "Chart" or "Chart windows"))
        {
            string? gesture = Hotkeys.GestureOf(_svc.Settings, definition.Id);
            if (gesture != null) body.Children.Add(Ui.H(12, new TextBlock { Text = Hotkeys.Pretty(gesture), MinWidth = 120, FontFamily = Ui.MonoFont }, new TextBlock { Text = definition.Name, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 }));
        }
        var help = new Window { Title = "Chart keyboard help", Width = 560, Height = 560, Content = new ScrollViewer { Content = body, Padding = new Thickness(20) }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _chartHelp = help;
        help.KeyDown += (_, e) => { if (e.Key == Key.Escape) { help.Close(); e.Handled = true; } };
        help.Closed += (_, _) => { if (ReferenceEquals(_chartHelp, help)) _chartHelp = null; };
        help.Show(owner);
    }
}
