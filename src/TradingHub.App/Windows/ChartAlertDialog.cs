using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.Charting;

namespace TradingHub.App.Windows;

/// <summary>
/// Creates chart alerts: price or an indicator line + condition (crossing, crossing up/down, touching, entering/exiting a
/// channel, moving up/down by a percent) + a fixed value, an indicator line, a captured line or a drawing line, with an
/// explicit trigger policy. Below, this chart's alerts (pause, resume, delete). Answers whether anything changed.
/// </summary>
internal sealed class ChartAlertDialog : TradingHubDialog<bool>
{
    private const int MaxMessageLength = 300;

    private readonly ChartModel _model;
    private readonly List<ChartAlertOperand> _sources;
    private readonly ComboBox _left = new() { Name = "ChartAlertLeft", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _condition = new() { Name = "ChartAlertCondition", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _right = new() { Name = "ChartAlertRight", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _rightValue = new() { Name = "ChartAlertRightValue", FormatString = "0.########", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _lower = new() { Name = "ChartAlertLower", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _lowerValue = new() { Name = "ChartAlertLowerValue", FormatString = "0.########", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _percent = new() { Name = "ChartAlertPercent", Minimum = 0.01m, Maximum = 1000, Value = 1, Increment = 0.1m, FormatString = "0.##", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _bars = new() { Name = "ChartAlertBars", Minimum = 1, Maximum = 1000, Value = 1, Increment = 1, FormatString = "0", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _trigger = new() { Name = "ChartAlertTrigger", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _message = new() { Name = "ChartAlertMessage", Watermark = "Optional note added to the notification", MaxLength = MaxMessageLength };
    private readonly ComboBox _expiry = new() { Name = "ChartAlertExpiry", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CalendarDatePicker _expiryDate = new() { Name = "ChartAlertExpiryDate", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TimePicker _expiryTime = new() { Name = "ChartAlertExpiryTime", ClockIdentifier = "24HourClock", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Control _expiryCustom;
    private readonly TextBlock _error = new() { Name = "ChartAlertError", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Warn };
    private readonly StackPanel _list = new() { Name = "ChartAlertList", Spacing = 4 };
    private readonly Control _rightRow, _lowerRow, _percentRow;
    private bool _changed;

    internal ChartAlertDialog(ChartModel model, ChartAlertOperand? right = null, ChartAlertOperand? lower = null, ChartAlertCondition? condition = null)
        : base("Chart alerts", 580)
    {
        _model = model;
        _sources = new List<ChartAlertOperand>
        {
            new() { Source = ChartAlertSource.Price, Field = "close", Label = "Price (close)" },
            new() { Source = ChartAlertSource.Price, Field = "high", Label = "Bar high" },
            new() { Source = ChartAlertSource.Price, Field = "low", Label = "Bar low" },
        };
        _sources.AddRange(ChartAlertEngine.Lines(model));
        _sources.AddRange(ChartAlertEngine.DrawingLines(model));

        foreach (var s in _sources) _left.Items.Add(Item(s));
        FillTargets(_right);
        FillTargets(_lower);
        foreach (var c in Enum.GetValues<ChartAlertCondition>()) _condition.Items.Add(new ComboBoxItem { Content = ChartAlert.ConditionText(c), Tag = c });
        foreach (var t in Enum.GetValues<ChartAlertTrigger>()) _trigger.Items.Add(new ComboBoxItem { Content = ChartAlert.TriggerText(t), Tag = t });
        _left.SelectedIndex = 0;
        _trigger.SelectedIndex = (int)ChartAlertTrigger.OncePerBar;
        foreach (var (text, span) in new (string, TimeSpan?)[] { ("Never expires", null), ("1 hour", TimeSpan.FromHours(1)), ("1 day", TimeSpan.FromDays(1)),
                     ("1 week", TimeSpan.FromDays(7)), ("30 days", TimeSpan.FromDays(30)), ("Until a date and time", TimeSpan.Zero) })
            _expiry.Items.Add(new ComboBoxItem { Content = text, Tag = span });
        _expiry.SelectedIndex = 0;
        var tomorrow = DateTime.Now.AddDays(1);
        _expiryDate.SelectedDate = tomorrow.Date;
        _expiryTime.SelectedTime = new TimeSpan(tomorrow.Hour, 0, 0);
        _expiryCustom = Ui.Row(8, _expiryDate, _expiryTime);
        _expiry.SelectionChanged += (_, _) => UpdateVisibility();
        _condition.SelectedIndex = (int)(condition ?? ChartAlertCondition.Crossing);

        decimal last = model.Data.LastPrice != 0 ? model.Data.LastPrice : model.Series.Count > 0 ? model.Instrument.FromTicks(model.Series[^1].CloseTicks) : 0;
        decimal step = model.Instrument.TickSize > 0 ? model.Instrument.TickSize : 1;
        _rightValue.Increment = _lowerValue.Increment = step;
        _rightValue.Value = _lowerValue.Value = last;
        if (right != null) Select(_right, right);
        else _right.SelectedIndex = 0;
        if (lower != null) Select(_lower, lower);
        else _lower.SelectedIndex = 0;

        _rightRow = Ui.Field("Value or line", Ui.Row(8, _right, _rightValue));
        _lowerRow = Ui.Field("Lower channel edge", Ui.Row(8, _lower, _lowerValue));
        _percentRow = Ui.Row(8, Ui.Field("Percent", _percent), Ui.Field("Over bars", _bars, "1 = since the previous bar."));
        _right.SelectionChanged += (_, _) => { PairChannel(); UpdateVisibility(); };
        _lower.SelectionChanged += (_, _) => UpdateVisibility();
        _condition.SelectionChanged += (_, _) => { PairChannel(); UpdateVisibility(); };

        var create = new Button { Name = "ChartAlertCreate", Content = "Create alert", IsDefault = true }.Classes("primary");
        create.Click += (_, _) => Create();
        var done = new Button { Name = "ChartAlertDone", Content = "Done", IsCancel = true };
        done.Click += (_, _) => Close(_changed);
        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Create alert", Classes = { "h2" } },
                Ui.Field("Condition", _left),
                _condition,
                _rightRow,
                _lowerRow,
                _percentRow,
                Ui.Field("Trigger", _trigger, "Alerts arm on the bar forming when they are created or loaded: history never fires them. Every time fires at most once a minute."),
                Ui.Field("Expires", _expiry, "Expired alerts stay listed but are no longer checked (local time)."),
                _expiryCustom,
                Ui.Field("Message", _message, "Shown in the app; sent to webhook or Telegram only if you approved that destination for this alert kind (Alerts window)."),
                _error,
                new Separator { Margin = new Thickness(0, 6) },
                new TextBlock { Text = "Alerts on this chart", Classes = { "h3" } },
                new ScrollViewer { Content = _list, MaxHeight = 220 },
            },
        };
        Content = Shell(form, done, create);
        PairChannel();
        UpdateVisibility();
        FillList();
    }

    /// <summary>
    /// Opens the dialog over <paramref name="owner"/>; true when alerts were added, paused or deleted, however it is closed
    /// (Done, Esc or the window's close button).
    /// </summary>
    public static async Task<bool> ShowAsync(Window? owner, ChartModel model, ChartAlertOperand? right = null, ChartAlertOperand? lower = null, ChartAlertCondition? condition = null)
    {
        var dialog = new ChartAlertDialog(model, right, lower, condition);
        await dialog.ShowOverAsync(owner);
        return dialog._changed;
    }

    private static ComboBoxItem Item(ChartAlertOperand op) =>
        new() { Content = (op.Source == ChartAlertSource.Drawing ? "✎ " : "") + op.DisplayLabel(), Tag = op };

    /// <summary>Targets: a fixed value first, then drawn lines (what is alerted most), then price and indicator lines.</summary>
    private void FillTargets(ComboBox box)
    {
        box.Items.Add(new ComboBoxItem { Content = "Fixed value", Tag = null });
        foreach (var s in _sources.Where(o => o.Source == ChartAlertSource.Drawing)) box.Items.Add(Item(s));
        foreach (var s in _sources.Where(o => o.Source != ChartAlertSource.Drawing)) box.Items.Add(Item(s));
    }

    private static void Select(ComboBox box, ChartAlertOperand op)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is ChartAlertOperand o && o.SameTarget(op))
            {
                box.SelectedItem = item;
                return;
            }
        }

        // a line picked with a right-click that the list does not offer (it ended on the left, or repeats a level)
        var extra = Item(op.Copy());
        box.Items.Add(extra);
        box.SelectedItem = extra;
    }

    private ChartAlertCondition Condition => _condition.SelectedItem is ComboBoxItem { Tag: ChartAlertCondition c } ? c : ChartAlertCondition.Crossing;

    private static ChartAlertOperand? Target(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as ChartAlertOperand;

    /// <summary>A channel condition on one edge of a drawing pairs the other edge as the lower one.</summary>
    private void PairChannel()
    {
        if (!ChartAlert.IsChannel(Condition) || Target(_right) is not { Source: ChartAlertSource.Drawing } edge) return;
        string? other = edge.Field switch { "upper" => "lower", "lower" => "upper", "top" => "bottom", "bottom" => "top", _ => null };
        if (other == null) return;
        if (Target(_lower) is { } current && current.Drawing == edge.Drawing && current.Field == other) return;
        var match = _lower.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is ChartAlertOperand o && o.Drawing == edge.Drawing && o.Field == other);
        if (match != null) _lower.SelectedItem = match;
    }

    private void UpdateVisibility()
    {
        bool percent = ChartAlert.IsPercent(Condition);
        _percentRow.IsVisible = percent;
        _rightRow.IsVisible = !percent;
        _lowerRow.IsVisible = ChartAlert.IsChannel(Condition);
        _expiryCustom.IsVisible = _expiry.SelectedItem is ComboBoxItem { Tag: TimeSpan custom } && custom == TimeSpan.Zero;
        _rightValue.IsVisible = Target(_right) == null;
        _lowerValue.IsVisible = Target(_lower) == null;
    }

    /// <summary>The chosen expiry in UTC (presets count from now; the custom date and time are local).</summary>
    private DateTime? ExpiryUtc()
    {
        if (_expiry.SelectedItem is not ComboBoxItem { Tag: TimeSpan span }) return null;
        if (span > TimeSpan.Zero) return DateTime.UtcNow + span;
        var date = _expiryDate.SelectedDate?.Date ?? DateTime.Today;
        var local = DateTime.SpecifyKind(date + (_expiryTime.SelectedTime ?? TimeSpan.Zero), DateTimeKind.Local);
        return local.ToUniversalTime();
    }

    /// <summary>The alert the form describes, or null with the reason shown.</summary>
    internal ChartAlert? Build()
    {
        _error.Text = "";
        if (Target(_left) is not { } left)
        {
            _error.Text = "Choose what the alert watches.";
            return null;
        }

        if (ExpiryUtc() is { } expires && expires <= DateTime.UtcNow)
        {
            _error.Text = "The expiry is in the past.";
            return null;
        }

        var condition = Condition;
        var alert = new ChartAlert
        {
            Left = left.Copy(),
            Condition = condition,
            Trigger = _trigger.SelectedItem is ComboBoxItem { Tag: ChartAlertTrigger t } ? t : ChartAlertTrigger.OncePerBar,
            Message = (_message.Text ?? "").Trim(),
            ExpiresUtc = ExpiryUtc(),
        };
        if (ChartAlert.IsPercent(condition))
        {
            // 0 % would hold on every flat bar and flood the destinations
            if (_percent.Value is not { } percent || percent <= 0)
            {
                _error.Text = "Enter a percent above 0.";
                return null;
            }

            alert.Right = ChartAlertOperand.Fixed(percent);
            alert.Bars = (int)(_bars.Value ?? 1);
            return alert;
        }

        alert.Right = Target(_right)?.Copy() ?? ChartAlertOperand.Fixed(_model.Instrument.RoundPrice(_rightValue.Value ?? 0));
        if (alert.Right.SameTarget(alert.Left))
        {
            _error.Text = "The alert compares a line with itself.";
            return null;
        }

        if (ChartAlert.IsChannel(condition))
        {
            alert.Lower = Target(_lower)?.Copy() ?? ChartAlertOperand.Fixed(_model.Instrument.RoundPrice(_lowerValue.Value ?? 0));
            if (alert.Lower.SameTarget(alert.Right))
            {
                _error.Text = "Choose two different channel edges.";
                return null;
            }
        }

        return alert;
    }

    internal void Create()
    {
        if (_model.Alerts.Count >= ChartAlertEngine.MaxAlertsPerChart)
        {
            _error.Text = $"A chart keeps at most {ChartAlertEngine.MaxAlertsPerChart} alerts.";
            return;
        }

        if (Build() is not { } alert) return;
        _model.Alerts.Add(alert);
        _changed = true;
        _model.Invalidate();
        FillList();
    }

    private void FillList()
    {
        _list.Children.Clear();
        if (_model.Alerts.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No alerts on this chart yet.", Classes = { "muted" } });
            return;
        }

        foreach (var a in _model.Alerts.ToList())
        {
            var text = new TextBlock
            {
                Text = a.Describe(translate: true) + " · " + UiText(ChartAlert.TriggerText(a.Trigger)) +
                    (a.IsExpired(DateTime.UtcNow) ? " · " + UiText("expired") : !a.Active ? " · " + UiText("paused") :
                        a.ExpiresUtc is { } until ? " · " + UiText($"until {until.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)}") : ""),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = a.Active ? 1 : 0.55,
            };
            ToolTip.SetTip(text, a.Message.Length > 0 ? a.Message : null);
            var toggle = Ui.Button(a.Active ? "Pause" : "Resume", a.Active ? MaterialIconKind.Pause : MaterialIconKind.Play, () =>
            {
                a.Active = !a.Active;
                if (a.Active) a.Rearm();
                _changed = true;
                FillList();
            });
            var delete = Ui.Button("Delete", MaterialIconKind.DeleteOutline, () =>
            {
                _model.Alerts.Remove(a);
                _changed = true;
                _model.Invalidate();
                FillList();
            });
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(delete, Dock.Right);
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(delete);
            row.Children.Add(toggle);
            row.Children.Add(text);
            _list.Children.Add(row);
        }
    }

    private static string UiText(string text) => TradingHub.Rendering.UiText.Tr(text);
}
