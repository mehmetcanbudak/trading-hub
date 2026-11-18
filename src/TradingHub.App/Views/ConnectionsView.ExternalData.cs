using Avalonia.Controls;
using Material.Icons;
using TradingHub.App.Services;

namespace TradingHub.App.Views;

internal sealed partial class ConnectionsView
{
    private static string HostOf(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? u.Host : url.Trim();

    /// <summary>List summary of an options or ETF flows connection.</summary>
    private static string DescribeExternal(ConnectionProfile p) => p.Kind == ConnectionKind.OptionsData && p.OptionsSource == OptionsSource.Deribit
        ? "Deribit · BTC and ETH · public data"
        : string.IsNullOrWhiteSpace(p.ExternalUrl) ? "no API URL configured"
        : ConnectionManager.ExternalUrlProblem(p.ExternalUrl) ?? $"custom API · {HostOf(p.ExternalUrl)}";

    /// <summary>Refuses to save a custom API URL that would send the key in clear text (or is not a URL).</summary>
    private static void RequireUsableExternalUrl(string url)
    {
        if (url.Length > 0 && ConnectionManager.ExternalUrlProblem(url) is { } problem) throw new InvalidOperationException(problem);
    }

    /// <summary>Options data for the options studies: Deribit's public data or a custom API returning the documented JSON.</summary>
    private Control OptionsDataSection(ConnectionProfile p, List<Action> apply)
    {
        var source = new ComboBox { MinWidth = 300 };
        foreach (var v in Enum.GetValues<OptionsSource>()) source.Items.Add(new ComboBoxItem { Content = Ui.EnumName(v), Tag = v });
        source.SelectedItem = source.Items.OfType<ComboBoxItem>().First(i => (OptionsSource)i.Tag! == p.OptionsSource);
        var url = new TextBox { Text = p.ExternalUrl, Watermark = "https://provider.example/api/options" };
        var header = new TextBox { Text = p.ExternalHeader, Watermark = "Authorization" };
        var key = SecretBox(p, null, "API key (optional)");
        var unders = new TextBox { Text = p.ExternalUnderlyings, Watermark = "BTC, ETH, SPY (blank = all)" };
        var custom = Ui.V(14,
            Ui.Field("API base URL", url, "Trading Hub requests /chain, /trades and /daily under this URL."),
            TwoColumns(Ui.Field("Key header", header, "\"Authorization\" sends the key as a Bearer token."), Ui.Field("API key", key)),
            Ui.Field("Underlyings", unders),
            Note(MaterialIconKind.CodeJson, "Format: GET /chain?underlying=BTC → {\"time\",\"spot\",\"contracts\":[{\"symbol\",\"expiry\",\"strike\",\"right\":\"C|P\",\"bid\",\"ask\",\"mark\",\"iv\",\"delta\",\"gamma\",\"oi\",\"volume\",\"contractSize\"}]}; GET /trades?underlying=BTC&from=…&to=… → [{\"time\",\"symbol\",\"expiry\",\"strike\",\"right\",\"price\",\"size\",\"side\":\"buy|sell\",\"iv\",\"spot\",\"contractSize\",\"venue\"}]; GET /daily → [{\"date\",\"atmIv\",\"rr25\",\"pcVolume\",\"pcOi\",\"gex\",\"callWall\",\"putWall\",\"gammaFlip\",\"expectedMove\"}]. IV as a fraction (0.55 = 55 %), prices in USD per unit of the underlying, contractSize = multiplier (default 1). Missing greeks are computed; other missing values stay unknown."),
            Note(MaterialIconKind.CurrencyUsd, "A custom API is usually a paid or self-hosted service. It is called only while this connection is enabled; check your provider's rate limits and terms."));
        void Sync() => custom.IsVisible = (OptionsSource)((ComboBoxItem)source.SelectedItem!).Tag! == OptionsSource.CustomJson;
        source.SelectionChanged += (_, _) => Sync();
        Sync();
        apply.Add(() =>
        {
            var chosen = (OptionsSource)((ComboBoxItem)source.SelectedItem!).Tag!;
            string address = url.Text?.Trim() ?? "";
            if (chosen == OptionsSource.CustomJson) RequireUsableExternalUrl(address);
            p.OptionsSource = chosen;
            p.ExternalUrl = address;
            p.ExternalHeader = string.IsNullOrWhiteSpace(header.Text) ? "Authorization" : header.Text.Trim();
            p.ExternalUnderlyings = unders.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(key.Text)) _svc.Connections.SetSecret(p, key.Text.Trim());
            key.Text = "";
        });
        return Ui.V(14,
            Ui.H2("Options data"),
            Note(MaterialIconKind.InformationOutline, "The options studies (dealer gamma, walls, max pain, IV rank, skew, premium flow, options dashboard…) read their data from here. Without this connection they show a waiting message and make no requests; once it is saved and enabled, open charts start using it. The chart symbol selects the underlying (BTCUSDT → BTC)."),
            Ui.Field("Source", source),
            custom,
            Note(MaterialIconKind.ShieldCheckOutline, $"Deribit is public: no account or key; it covers BTC and ETH options. A custom API key is stored in {Os.SecretStoreShort}, never in files. Daily history is completed by one snapshot per day that Trading Hub records while an options study is open."));
    }

    /// <summary>Daily spot ETF flows from a custom API (documented format).</summary>
    private Control EtfFlowsSection(ConnectionProfile p, List<Action> apply)
    {
        var url = new TextBox { Text = p.ExternalUrl, Watermark = "https://provider.example/api/etf" };
        var header = new TextBox { Text = p.ExternalHeader, Watermark = "Authorization" };
        var key = SecretBox(p, null, "API key (optional)");
        var assets = new TextBox { Text = p.ExternalUnderlyings, Watermark = "BTC, ETH (blank = all)" };
        apply.Add(() =>
        {
            string address = url.Text?.Trim() ?? "";
            RequireUsableExternalUrl(address);
            p.ExternalUrl = address;
            p.ExternalHeader = string.IsNullOrWhiteSpace(header.Text) ? "Authorization" : header.Text.Trim();
            p.ExternalUnderlyings = assets.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(key.Text)) _svc.Connections.SetSecret(p, key.Text.Trim());
            key.Text = "";
        });
        return Ui.V(14,
            Ui.H2("ETF flows"),
            Note(MaterialIconKind.InformationOutline, "The Spot ETF flows study reads the daily net flow of the chart coin's spot ETFs from here. Without this connection it shows a waiting message and makes no requests."),
            Ui.Field("API base URL", url, "Trading Hub requests /flows?asset=BTC&from=YYYY-MM-DD&to=YYYY-MM-DD under this URL."),
            TwoColumns(Ui.Field("Key header", header, "\"Authorization\" sends the key as a Bearer token."), Ui.Field("API key", key)),
            Ui.Field("Assets", assets),
            Note(MaterialIconKind.CodeJson, "Format: [{\"date\":\"2026-11-03\",\"net\":-123400000,\"funds\":{\"IBIT\":-80000000}}] in USD (or \"netMillions\" in millions). {\"data\":[…]} is also accepted."),
            Note(MaterialIconKind.ShieldCheckOutline, $"Usually a paid or self-hosted service, called only while this connection is enabled. The key is stored in {Os.SecretStoreShort}, never in files."));
    }
}
