// DOM trading switch behaviour adapted from gouqizi2214730-source/trading-hub a8c917f (MIT with Commons Clause; see
// THIRD_PARTY_NOTICES.md). Default on keeps today's DOM trading; off blocks every DOM gesture before the order path.
using Avalonia;
using Avalonia.Controls;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.Charting.Dom;

namespace TradingHub.App.Views;

/// <summary>
/// The DOM trading switch (persisted, separate from chart trading) and the trade-tape notional filter.
/// On: the DOM trades exactly as before. Off: DOM clicks, drags and the DOM order buttons are blocked before any
/// order call; quantity entry, scrolling and recentering still work.
/// </summary>
public sealed partial class TradingView
{
    private Button? _domLock;
    private readonly List<Control> _domOrderRows = new();

    /// <summary>True when DOM order gestures and buttons may reach the order path.</summary>
    internal bool DomTradingEnabled => _svc.Settings.DomTrading;

    private Button BuildDomLock()
    {
        _domLock = new Button().Classes("icon");
        _domLock.Name = "DomTradingLock";
        _domLock.Click += (_, _) => SetDomTrading(!_svc.Settings.DomTrading);
        return _domLock;
    }

    /// <summary>Switches DOM trading, persists it and applies it to the ladder and the DOM buttons.</summary>
    internal void SetDomTrading(bool enabled)
    {
        _svc.Settings.DomTrading = enabled;
        _svc.Settings.Save();
        ApplyDomTrading();
        _status(enabled ? "DOM trading on" : "DOM trading off: the DOM is read-only");
    }

    private void ApplyDomTrading()
    {
        bool on = _svc.Settings.DomTrading;
        _dom.TradingEnabled = on;
        foreach (var row in _domOrderRows) row.IsEnabled = on;
        if (_domLock == null) return;
        _domLock.Content = Ui.Icon(on ? MaterialIconKind.LockOpenVariantOutline : MaterialIconKind.LockOutline, 15, on ? null : Ui.Accent);
        ToolTip.SetTip(_domLock, on
            ? "DOM trading on. Click to make the DOM read-only (chart trading is unaffected)."
            : "DOM trading off: the DOM is read-only. Click to enable DOM trading.");
        // the state is otherwise only an icon (and the DOM's read-only badge is drawn): assistive technology reads this
        Avalonia.Automation.AutomationProperties.SetName(_domLock, Localization.Lang.T(on ? "DOM trading on" : "DOM trading off: the DOM is read-only"));
    }

    /// <summary>Defence in depth for DOM-originated orders: the gesture router already blocks them when off.</summary>
    private bool DomOrderAllowed()
    {
        if (_svc.Settings.DomTrading) return true;
        _status(DomOrderGestures.TradingOffMessage);
        return false;
    }

    private ChoiceButton<int> BuildTapeFilter()
    {
        var filter = new ChoiceButton<int>(MaterialIconKind.FilterOutline,
            new[] { (0, "All trades"), (10_000, "≥ 10K notional"), (50_000, "≥ 50K notional"), (100_000, "≥ 100K notional"), (500_000, "≥ 500K notional"), (1_000_000, "≥ 1M notional") },
            "Show only trades of at least this notional (quantity × price × point value)",
            v => v == 0 ? "All" : v >= 1_000_000 ? $"≥{v / 1_000_000}M" : $"≥{v / 1000}K");
        filter.MinHeight = 26;
        filter.Padding = new Thickness(8, 2);
        filter.Select((int)Math.Clamp(_svc.Settings.TapeMinNotional, 0, int.MaxValue));
        filter.Changed += v =>
        {
            _svc.Settings.TapeMinNotional = v;
            _svc.Settings.Save();
            if (_tape.Tape != null) _tape.Tape.MinNotional = v;
            _tape.InvalidateVisual();
        };
        return filter;
    }

    /// <summary>Keeps the step chooser on "Auto" while auto tick is on; leaving auto tick returns to one tick per row.</summary>
    private void SyncDomStep()
    {
        if (_svc.Settings.Dom.AutoStep)
        {
            _domStep?.Select(0);
            return;
        }
        if (_domStep == null || _domStep.Selected != 0) return;
        _domStep.Select(1);
        if (_dom.Dom is { } dom) dom.Step = 1;
        _dom.Recenter();
    }
}
