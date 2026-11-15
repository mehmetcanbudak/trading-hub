using Avalonia;
using Avalonia.Controls;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Feeds;
using TradingHub.Feeds.Venues;

namespace TradingHub.App.Views;

internal sealed partial class ConnectionsView
{
    /// <summary>Aggregate crypto: which markets are combined, the spot/perpetual filter and the DOM source.</summary>
    private Control AggregateSection(ConnectionProfile p, List<Action> apply)
    {
        var all = CryptoVenues.All;
        var checks = all.Select(v => new CheckBox
        {
            Content = v.Name, IsChecked = p.AggregateSources.Contains(v.Id, StringComparer.OrdinalIgnoreCase), Tag = v.Id,
            Margin = new Thickness(0, 0, 18, 6),
        }).ToList();
        apply.Add(() =>
        {
            // Keep the configured order (the first market anchors price), then append newly ticked ones.
            var ticked = checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag!).ToList();
            var ordered = p.AggregateSources.Where(id => ticked.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
            ordered.AddRange(ticked.Where(id => !ordered.Contains(id, StringComparer.OrdinalIgnoreCase)));
            p.AggregateSources = ordered.Count > 0 ? ordered : CryptoVenues.DefaultAggregate.ToList();
        });
        var perpetuals = new WrapPanel();
        var spots = new WrapPanel();
        for (int i = 0; i < all.Count; i++) (all[i].IsSpot ? spots : perpetuals).Children.Add(checks[i]);

        var markets = new ComboBox { MinWidth = 220 };
        foreach (var m in Enum.GetValues<AggregateMarkets>()) markets.Items.Add(new ComboBoxItem { Content = Ui.EnumName(m), Tag = m });
        markets.SelectedItem = markets.Items.OfType<ComboBoxItem>().First(i => (AggregateMarkets)i.Tag! == p.AggregateMarkets);
        apply.Add(() => p.AggregateMarkets = (markets.SelectedItem as ComboBoxItem)?.Tag is AggregateMarkets value ? value : AggregateMarkets.Both);

        var book = new ComboBox { MinWidth = 320 };
        book.Items.Add(new ComboBoxItem { Content = "First market's book only", Tag = AggregateBookMode.FirstVenue });
        book.Items.Add(new ComboBoxItem { Content = "Composite of all market books (not executable)", Tag = AggregateBookMode.Consolidated });
        book.SelectedItem = book.Items.OfType<ComboBoxItem>().First(i => (AggregateBookMode)i.Tag! == p.AggregateBook);
        apply.Add(() => p.AggregateBook = (book.SelectedItem as ComboBoxItem)?.Tag is AggregateBookMode mode ? mode : AggregateBookMode.FirstVenue);

        var recommended = Ui.Button("Recommended", MaterialIconKind.StarOutline, () =>
        {
            foreach (var c in checks) c.IsChecked = CryptoVenues.Recommended.Contains((string)c.Tag!);
        }, "ghost");
        var everything = Ui.Button("All", MaterialIconKind.CheckAll, () =>
        {
            foreach (var c in checks) c.IsChecked = true;
        }, "ghost");
        var original = Ui.Button("Binance + Bybit", MaterialIconKind.Restore, () =>
        {
            foreach (var c in checks) c.IsChecked = CryptoVenues.DefaultAggregate.Contains((string)c.Tag!);
        }, "ghost");

        return Ui.V(14,
            Ui.H2("Markets to combine"),
            Note(MaterialIconKind.InformationOutline, "Trades and candles of every selected market are combined into one instrument per coin: BTC-USDT-SWAP (OKX), BTC-USD (Coinbase) and BTC (Hyperliquid) join BTCUSDT. USDT, USD and USDC prices are combined without conversion. A market whose price is more than 3% away from the first market is left out and reported as partial coverage."),
            Ui.V(6,
                new TextBlock { Text = "Perpetuals", Classes = { "faint", "small" } }, perpetuals,
                new TextBlock { Text = "Spot", Classes = { "faint", "small" } }, spots,
                Ui.H(6, recommended, everything, original)),
            Ui.Field("Spot / perpetual", markets, "Leaves out spot or perpetual markets without unticking them."),
            Ui.Field("Order book (DOM and heatmap)", book, "A composite book sums the sizes of every connected market at each price. It can cross and is never an executable quote."),
            Note(MaterialIconKind.ShieldCheckOutline, "Public data: no account or keys. The aggregate never routes orders; trade on each market's own connection."));
    }

    /// <summary>A single public crypto market (data only).</summary>
    private Control PublicVenueSection(ConnectionProfile p, List<Action> apply)
    {
        var venue = new ComboBox { MinWidth = 260 };
        foreach (var entry in CryptoVenues.Standalone)
            venue.Items.Add(new ComboBoxItem { Content = entry.Name + (entry.IsSpot ? " (spot)" : " (perpetuals)"), Tag = entry.Id });
        venue.SelectedItem = venue.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag!, p.PublicVenueId, StringComparison.OrdinalIgnoreCase))
            ?? venue.Items[0];
        apply.Add(() => p.PublicVenueId = (venue.SelectedItem as ComboBoxItem)?.Tag as string ?? OkxFeed.SwapId);
        return Ui.V(14,
            Ui.H2("Public crypto market"),
            Ui.Field("Market", venue, "Perpetual sizes are converted to coins (contracts × contract value; Deribit inverse USD contracts ÷ price)."),
            Note(MaterialIconKind.ShieldCheckOutline, "Public data: no account or keys, and no orders. Hyperliquid and MEXC publish only the best 20 book levels; MEXC trades have no exchange identifier."));
    }
}
