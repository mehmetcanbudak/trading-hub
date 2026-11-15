// "safety" scene part: exchange-side protection. A simulated fixture account that offers a linked stop/target contract,
// switched on and off: Chart Trader names how the open position's automatic stop and target are held. The Tradovate
// connection editor shows the opt-in switch off by default. Fake accounts only; nothing reaches a venue.
using Avalonia.Controls;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

internal static partial class SafetyScene
{
    private static void ExchangeSideProtection(MainWindow window, Action<double> pump, TradingView tv, ChartTab chart,
        Dictionary<string, ITradingAccount> cache, Action<bool, string> check, Action<Window, string> shot)
    {
        var svc = App.Services;
        var model = chart.Model!;
        var profile = new ConnectionProfile
        {
            Id = "safety-linked", Kind = ConnectionKind.BinanceFutures, Name = "Fixture linked protection",
            ApiKey = "fixture-cached-account-only", Testnet = true,
        };
        svc.Connections.Profiles.Add(profile);
        var inner = new SimBroker(model.Data, new Portfolio { AccountID = "SAFETY-LINKED", Name = "Fixture linked protection", BeginBalance = 10_000m, Balance = 10_000m },
            new TradingStatistics()) { Name = "Fixture linked protection" };
        var linked = new FixtureLinkedAccount(inner);
        string key = "safety-linked|" + model.Data.Instrument.Key;
        cache[key] = linked;
        var template = svc.Settings.BracketTemplateFor(model.Data.Instrument.Key);
        var saved = template.Clone();
        try
        {
            template.Enabled = true;
            template.StopTicks = 20;
            template.BreakevenAfterFirstTarget = false;
            template.Targets.Clear();
            template.Targets.Add(new BracketTarget { Ticks = 40, Percent = 100 });
            tv.SetChartAccount(chart, "safety-linked");
            tv.SetTraderVisible(chart, true);
            pump(0.4);
            var trader = chart.Trader;
            check(trader != null && ReferenceEquals(tv.AccountOf(chart), linked), "the chart routes to the linked-protection fixture account");
            if (trader == null) return;
            var status = Field<TextBlock>(trader, "_protection");
            var brackets = svc.BracketsFor(linked);

            void Trade(OrderDirections direction, decimal quantity)
            {
                var task = tv.TradeAsync(chart, OrderTypes.Market, direction, 0, quantity);
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!task.IsCompleted && DateTime.UtcNow < until) pump(0.05);
                pump(0.2);
                trader.Refresh();
            }

            Trade(OrderDirections.Buy, 2);
            check(linked.Position.Volume == 2 && linked.LinkedPairs == 1 && brackets.Mode == ProtectionMode.ExchangeSide,
                $"switched on: the automatic stop and target go out as one linked pair (pairs {linked.LinkedPairs}, mode {brackets.Mode})");
            check(status.Text == Lang.T("Exchange-side protection"), $"Chart Trader shows exchange-side protection («{status.Text}»)");
            shot(window, "safety_protection_exchange_side");
            Trade(OrderDirections.Sell, 2);
            check(linked.Position.Volume == 0 && !linked.ActiveOrders.Any(), "closing the position leaves no stop or target working");

            linked.Enabled = false;
            Trade(OrderDirections.Buy, 2);
            check(linked.LinkedPairs == 1 && linked.ActiveOrders.Count() == 2 && brackets.Mode == ProtectionMode.AppManaged,
                $"switched off: the same stop and target go out as separate orders (pairs {linked.LinkedPairs}, mode {brackets.Mode})");
            check(status.Text == Lang.T("App-managed protection"), $"Chart Trader shows app-managed protection («{status.Text}»)");
            shot(window, "safety_protection_app_managed");
            Trade(OrderDirections.Sell, 2);
            check(linked.Position.Volume == 0, "the fixture position closes");
        }
        finally
        {
            template.Enabled = saved.Enabled;
            template.StopTicks = saved.StopTicks;
            template.BreakevenAfterFirstTarget = saved.BreakevenAfterFirstTarget;
            template.Targets.Clear();
            template.Targets.AddRange(saved.Targets);
            tv.SetChartAccount(chart, "sim");
            cache.Remove(key);
            svc.Connections.Profiles.Remove(profile);
        }

        // The Tradovate connection editor: the opt-in switch exists, off by default.
        var tradovate = new ConnectionProfile { Id = "safety-tradovate", Kind = ConnectionKind.Tradovate, Name = "Fixture Tradovate", Testnet = true };
        svc.Connections.Profiles.Add(tradovate);
        try
        {
            window.Navigate("connections");
            pump(0.6);
            var pages = Field<Dictionary<string, Control>>(window, "_pages");
            var view = pages["connections"];
            view.GetType().GetMethod("Select", Private)!.Invoke(view, new object[] { tradovate });
            pump(0.6);
            string label = Lang.T("Exchange-side protection (Tradovate OCO)");
            var toggle = view.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Content is string text && text == label);
            check(toggle != null && toggle.IsChecked == false && !tradovate.ExchangeSideProtection,
                "the Tradovate connection offers exchange-side protection, off by default");
            toggle?.BringIntoView();
            pump(0.3);
            shot(window, "safety_protection_connection_toggle");
        }
        finally
        {
            svc.Connections.Profiles.Remove(tradovate);
            window.Navigate("trading");
            pump(0.4);
        }
    }
}

/// <summary>A simulated account offering a linked stop/target contract (fixture only): legs sent in one link scope form a group.</summary>
internal sealed class FixtureLinkedAccount(SimBroker inner) : ITradingAccount, ITrackedOrderReplacement, INativeProtection
{
    private readonly Dictionary<Order, string> _groups = new();
    private List<Order>? _scope;
    public bool Enabled { get; set; } = true;
    public int LinkedPairs { get; private set; }
    public string Name => inner.Name;
    public bool IsReal => false;
    public Instrument Instrument => inner.Instrument;
    public Position Position => ((ITradingAccount)inner).Position;
    public IEnumerable<Order> ActiveOrders => inner.ActiveOrders;
    public string? LastError => inner.LastError;
    public decimal LastPrice => inner.LastPrice;
    public event Action? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }
    public OrderContext OrderContext => inner.OrderContext;
    public IExecutionSource Executions => inner.Executions;
    public NativeProtectionFeatures NativeProtection => Enabled
        ? NativeProtectionFeatures.LinkedStopAndTarget | NativeProtectionFeatures.NativeAmendment : NativeProtectionFeatures.None;
    public string? NativeProtectionFault => null;
    public IDisposable LinkProtection(OrderOwner owner)
    {
        var scope = _scope = new List<Order>();
        return new LinkClose(() =>
        {
            _scope = null;
            if (scope.Count != 2) return;
            LinkedPairs++;
            foreach (var leg in scope) _groups[leg] = "fixture-pair-" + LinkedPairs;
        });
    }
    public string? ProtectionGroupOf(Order order) => _groups.GetValueOrDefault(order);
    public Order Submit(PreparedOrderIntent intent)
    {
        var order = inner.Submit(intent);
        _scope?.Add(order);
        return order;
    }
    public Order Replace(Order order, PreparedOrderIntent intent) => inner.Replace(order, intent);
    public bool IsReplacementPending(Order order) => inner.IsReplacementPending(order);
    private readonly HashSet<Order> _cancelRequested = new();
    public void Cancel(Order order)
    {
        _cancelRequested.Add(order);
        inner.Cancel(order);
    }
    public bool CancelRequested(Order order) => _cancelRequested.Contains(order);
    public void CancelAt(decimal price) => inner.CancelAt(price);
    public void CancelAll() => inner.CancelAll();

    private sealed class LinkClose(Action close) : IDisposable
    {
        public void Dispose() => close();
    }
}
