using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Replay;

namespace TradingHub.App.Views;

/// <summary>Practice workflow inspired by KVP 50220ba91613161685f84384825d855cb4dbeb76; all accounting is run-owned.</summary>
public sealed partial class TradingView
{
    private readonly Border _replayBar = new() { IsVisible = false };
    private readonly ToggleButton _replayPlay = new() { Name = "ReplayPlay", Width = 34, Height = 30, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _replayClock = new() { Name = "ReplayClock", VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _replayProgress = new() { Minimum = 0, Maximum = 1, Height = 4, Width = 100, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _replayReveal = new() { Name = "ReplayReveal", Content = "Reveal" };
    private readonly Button _replayEnd = new() { Name = "ReplayEnd", Content = "End practice (close at executable print)" };
    private readonly HashSet<Guid> _reportedReplays = new();
    private Ui.Segmented<double>? _replaySpeed;
    private ReplayFeed? ActiveReplay => _active?.Model?.Data.Feed as ReplayFeed;
    private ReplaySession? ActivePractice => _active?.Model == null ? null : _svc.ReplaySessions.TryGet(_active.Model.Data);

    private Control BuildReplayBar()
    {
        _replayPlay.Click += (_, _) =>
        {
            if (ActiveReplay is { } feed && ActivePractice?.Ended != true) feed.Paused = _replayPlay.IsChecked != true;
            UpdateReplayBar();
        };
        _replaySpeed = new Ui.Segmented<double>(new[] { 1.0, 2, 5, 10, 30, 60, 300 }.Select(v => (v, $"{v:0}×", (string?)null)));
        _replaySpeed.SelectionChanged += value => { if (ActiveReplay is { } feed) feed.Speed = value; };
        _replayReveal.Click += (_, _) => { if (_active != null) RevealReplay(_active); };
        _replayEnd.Click += (_, _) => { if (ActivePractice is { } run) EndPractice(run); };
        var result = Ui.Button("Result / journal", null, () => { if (ActivePractice is { } run) ShowPracticeResult(run); });
        result.Name = "ReplayResult";
        Button Jump(string text, TimeSpan by) => Ui.Button(text, null, () =>
        {
            if (ActiveReplay is { } feed && ActivePractice?.Ended != true) feed.SeekForward(feed.Clock + by);
        });
        _replayBar.Child = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6,
            Margin = new Thickness(12, 8), Children = {
                new TextBlock { Text = "PRACTICE", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center },
                _replayPlay, _replaySpeed, _replayClock, _replayProgress, Jump("+5 min", TimeSpan.FromMinutes(5)),
                Jump("+30 min", TimeSpan.FromMinutes(30)), _replayReveal, _replayEnd, result } };
        _replayBar.Background = Ui.Bg1;
        _replayBar.BorderBrush = Ui.Line;
        _replayBar.BorderThickness = new Thickness(0, 1, 0, 0);
        return _replayBar;
    }

    private void UpdateReplayBar()
    {
        var feed = ActiveReplay;
        var run = ActivePractice;
        _replayBar.IsVisible = feed != null;
        if (feed == null || run == null || _active?.Model == null) return;
        _replayPlay.IsEnabled = !run.Ended && !feed.IsFinished;
        _replayPlay.IsChecked = !feed.Paused && !run.Ended && !feed.IsFinished;
        _replayPlay.Content = Ui.Icon(feed.Paused || feed.IsFinished || run.Ended ? MaterialIconKind.Play : MaterialIconKind.Pause, 18);
        _replaySpeed?.Select(feed.Speed);
        string clock = run.IsBlind ? "+" + (feed.Clock - feed.Start).ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture)
            : TimeZoneInfo.ConvertTimeFromUtc(feed.Clock, _active.Model.Settings.TimeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        _replayClock.Text = $"{(run.Ended ? "Ended · " : "")}{clock} · seed {run.Selection.Seed}";
        _replayProgress.Value = feed.Progress;
        _replayReveal.IsVisible = run.IsBlind;
        _replayEnd.IsEnabled = !run.Ended;
    }

    internal Task OpenReplayAsync(Instrument source, DateTime startUtc, DateTime endUtc, ChartPeriod period) =>
        OpenPracticeAsync(ReplaySelection.Selected(source, startUtc, endUtc), period);

    internal Task OpenPracticeAsync(ReplaySelection selection, ChartPeriod period)
    {
        var run = _svc.OpenPracticeReplay(selection.Source, selection.StartUtc, selection.EndUtc, TimeSpan.FromHours(3), selection.Seed, selection.Blind);
        var data = run.Data;
        var page = CreatePage();
        var chart = CreateChart(data.Instrument.FeedId, selection.Source.Symbol, run.AccountId, 0);
        chart.OnClosed = () => { if (!run.Ended) EndPractice(run); run.Dispose(); };
        page.Charts.Add(chart);
        page.Active = chart;
        chart.Page = page;
        var settings = _active?.Model?.Settings.Clone() ?? new ChartSettings();
        settings.BlindReplay = run.IsBlind;
        settings.BlindReplayStart = selection.StartUtc;
        var model = new ChartModel(data, period, settings) { Trading = run.Account, Account = run.Account };
        model.Settings.TimeZoneId = _svc.Settings.TimeZoneId;
        model.Viewport.BarWidth = model.Settings.Style == ChartStyle.Footprint ? 110 : 8;
        model.Brackets = _svc.BracketsFor(run.Account);
        model.AlertRaised += alert => PublishChartAlert(chart, alert);
        if (_active?.Model != null)
            foreach (var indicator in _active.Model.Indicators.Select(slot => slot.Indicator))
            {
                var copy = Workspace.CloneIndicator(_svc, indicator);
                if (copy != null) model.AddIndicator(copy, applyDefaultColors: false);
            }
        chart.Model = model;
        chart.View.Model = model;
        _pages.Add(page);
        Arrange(page);
        _tabControl.Items.Add(page.Item);
        _tabControl.SelectedItem = page.Item;
        UpdateTitles(chart);
        _status(run.IsBlind ? "Blind practice: instrument and date are concealed, not secured. Start playback when ready."
            : $"Practice of {selection.Source.Symbol} · seed {selection.Seed} · isolated account, deterministic estimated fills. Start playback when ready.");
        return Task.CompletedTask;
    }

    private IReadOnlyList<ReplayCandidate> ReplayCandidates()
    {
        var sources = AllCharts.Where(c => c.Model != null).Select(c => c.Model!.Data.Feed is ReplayFeed replay ? replay.Source : c.Model.Instrument).ToList();
        foreach (var profile in _svc.Connections.Enabled)
        {
            var saved = _svc.InstrumentCatalog.Read(profile.Id, InstrumentCatalog.IdentityFor(profile));
            if (saved != null) sources.AddRange(saved.Instruments);
        }
        return sources.DistinctBy(i => i.Key).Select(source => new ReplayCandidate(source,
            TickFile.AvailableDays(AppPaths.Ticks, source.FeedId, source.Symbol).Order().ToArray())).Where(c => c.Days.Count > 0).ToArray();
    }

    internal async Task OpenRandomReplayAsync(bool blind, int lengthDays = 3, int? seed = null)
    {
        try
        {
            var selection = ReplaySelection.Random(ReplayCandidates(), seed ?? _svc.Settings.ReplayRandomSeed, blind, lengthDays);
            await OpenPracticeAsync(selection, _active?.Model?.Period ?? ChartPeriod.M5);
        }
        catch (Exception error) { _status("Practice could not start: " + error.Message); }
    }

    internal void RevealReplay(ChartTab tab)
    {
        var run = tab.Model == null ? null : _svc.ReplaySessions.TryGet(tab.Model.Data);
        if (run == null || tab.Model == null) return;
        run.Reveal();
        tab.Model.Settings.BlindReplay = false;
        tab.Model.Invalidate();
        UpdateTitles(tab);
        _toolbar.Sync();
        tab.Trader?.Rebind();
        if (ReferenceEquals(tab, _active)) BindSidePanel(tab, force: true);
        UpdatePanelTitles();
        UpdateReplayBar();
    }

    private void EndPractice(ReplaySession run)
    {
        foreach (var chart in AllCharts.Where(c => c.Model != null && ReferenceEquals(c.Model.Data, run.Data)))
            foreach (var strategy in chart.Strategies) StopStrategy(strategy);
        foreach (var runner in _svc.Runners.Where(r => ReferenceEquals(r.Account, run.Account)).ToArray())
            runner.Stop(flatten: false);
        // A user end cancels future queued feed data before marking the final price.
        run.Feed?.Stop();
        var result = _svc.ReplaySessions.End(run);
        var tab = AllCharts.FirstOrDefault(c => c.Model != null && ReferenceEquals(c.Model.Data, run.Data));
        if (tab != null) RevealReplay(tab);
        if (_reportedReplays.Add(run.RunId)) _svc.Alerts.Raise(AlertKind.System, run.Selection.Source.Symbol, "Replay result",
            $"Practice ended · {result.Fills.Count} fills · net {result.RealizedNet:0.00} · fees {result.Commission:0.00} · seed {result.Seed}", timeUtc: run.ClockUtc, allowExternal: false);
        UpdateReplayBar();
    }
    private void TickReplayRuns()
    {
        foreach (var run in _svc.ReplaySessions.All)
            if (!run.Ended && run.Feed?.IsFinished == true && !run.Data.IsLoadingHistory)
            {
                // IsFinished is a producer fence: every publication precedes it, but Hub.Pump may not have consumed them yet.
                run.Data.Pump();
                if (!run.Ended && run.Data.ReplayInputDrained) EndPractice(run);
            }
        UpdateReplayBar();
    }

    private bool UpdatePracticeTradeMarks(ChartTab chart)
    {
        if (chart.Model is not { } model || _svc.ReplaySessions.TryGet(model.Data) is not { } run) return false;
        string key = model.Settings.ShowTradeMarks ? run.AccountId + ":" + run.Account.Journal.Count : "off";
        if (key == chart.MarksKey) return true;
        chart.MarksKey = key;
        var fills = model.Settings.ShowTradeMarks ? run.Account.Journal : Array.Empty<TradingHub.Trading.AccountFill>();
        model.TradeMarks = fills.Select(f => new TradeMark(f.Time, f.Price, f.Buy, f.Quantity)).ToArray();
        model.TradeLinks = TradingHub.Trading.RoundTrips.Analyze(fills).Trades.Select(t =>
            new TradeLink(t.OpenTime, t.OpenPrice, t.CloseTime, t.ClosePrice, t.IsLong, t.NetPnl)).ToArray();
        model.Invalidate();
        return true;
    }

    private void ShowPracticeResult(ReplaySession run)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var result = run.Result();
        string instrument = run.IsBlind ? "Practice (concealed)" : run.Selection.Source.Symbol;
        var journal = string.Join(Environment.NewLine, result.Fills.Select(f =>
            $"{(run.IsBlind ? (f.Time - result.StartUtc).ToString() : f.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}  {(f.Buy ? "Buy" : "Sell")} {f.Quantity} @ {f.Price} · fee {f.Commission}"));
        new Window { Title = "Practice result / run-owned journal", Width = 740, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 10, Children = {
                new TextBlock { Text = $"{instrument} · run {result.RunId:N} · seed {result.Seed}", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"Net realized {result.RealizedNet:0.00} · open {result.OpenPnl:0.00} · fees {result.Commission:0.00} · equity {result.Equity:0.00} · position {result.Position}", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Run-owned practice estimates. Market orders fill at the last delivered executable print; resting orders share print volume. Without reliable executable data, an open position is reported rather than inventing a closing fill. Not exchange queue execution or live performance.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = run.DataWarning ?? "Raw recorded tape; actual historical book queues are not simulated.", TextWrapping = TextWrapping.Wrap },
                new TextBox { Name = "ReplayJournal", Text = journal, IsReadOnly = true, AcceptsReturn = true, Height = 320 } } } }.Show(owner);
    }

    internal void OpenPracticeDialog()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var seed = new NumericUpDown { Name = "PracticeSeed", Value = _svc.Settings.ReplayRandomSeed, Minimum = int.MinValue, Maximum = int.MaxValue, Increment = 1 };
        var days = new NumericUpDown { Name = "PracticeDays", Value = _svc.Settings.ReplayLengthDays, Minimum = 1, Maximum = 366, Increment = 1 };
        var balance = new NumericUpDown { Name = "PracticeBalance", Value = _svc.Settings.ReplayStartBalance, Minimum = 0, Maximum = 1_000_000_000 };
        var perUnit = new NumericUpDown { Name = "PracticeFeePerUnit", Value = _svc.Settings.ReplayFeePerUnit, Minimum = 0, Maximum = 1_000_000, Increment = 0.01m };
        var rate = new NumericUpDown { Name = "PracticeFeeRate", Value = _svc.Settings.ReplayNotionalFeeRate, Minimum = 0, Maximum = 1, Increment = 0.0001m, FormatString = "0.000000" };
        var blind = new CheckBox { Name = "PracticeBlind", Content = "Blind practice (conceal instrument and date; not security isolation)", IsChecked = true };
        var start = new Button { Name = "PracticeStart", Content = "Start random recorded practice" };
        var window = new Window { Title = "Practice replay — isolated account", Width = 620, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(20), Spacing = 10, Children = {
            new TextBlock { Text = "Uses consecutive recorded days from offline catalogs or open chart instruments. No provider request or real account is used. Save/download history first.", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Deterministic random seed" }, seed, new TextBlock { Text = "Consecutive recorded days" }, days,
            new TextBlock { Text = "Starting balance (instrument quote currency; no currency conversion)" }, balance,
            new TextBlock { Text = "Fee per unit per fill" }, perUnit, new TextBlock { Text = "Fee fraction of price × point value × quantity" }, rate, blind, start } } };
        start.Click += async (_, _) =>
        {
            _svc.Settings.ReplayRandomSeed = (int)(seed.Value ?? 1);
            _svc.Settings.ReplayLengthDays = (int)(days.Value ?? 3);
            _svc.Settings.ReplayStartBalance = balance.Value ?? 10_000;
            _svc.Settings.ReplayFeePerUnit = perUnit.Value ?? 0;
            _svc.Settings.ReplayNotionalFeeRate = rate.Value ?? 0;
            _svc.Settings.Save();
            window.Close();
            await OpenRandomReplayAsync(blind.IsChecked == true, _svc.Settings.ReplayLengthDays, _svc.Settings.ReplayRandomSeed);
        };
        window.Show(owner);
    }
}
