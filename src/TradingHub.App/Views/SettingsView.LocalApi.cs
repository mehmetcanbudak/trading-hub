using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services.LocalApi;

namespace TradingHub.App.Views;

internal sealed partial class SettingsView
{
    private Control BuildLocalApiSettings()
    {
        var settings = _svc.Settings;
        var address = new TextBlock { Name = "LocalApiAddress", TextWrapping = TextWrapping.Wrap, Classes = { "small" } };
        var status = new TextBlock { Name = "LocalApiStatus", TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } };
        var ignored = new TextBlock { Name = "LocalApiInvalidOrigins", TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" }, IsVisible = false };

        void Refresh()
        {
            int bound = Math.Clamp(settings.LocalApiPort, 1024, 65535);
            address.Text = $"Base URL: http://127.0.0.1:{bound.ToString(CultureInfo.InvariantCulture)}/api/v1";
            status.Text = _svc.LocalApiStatus;
        }

        void Apply()
        {
            settings.Save();
            _svc.ApplyLocalApi();
            Refresh();
        }

        var enabled = Switch(settings.LocalApiEnabled, value =>
        {
            settings.LocalApiEnabled = value;
            _svc.ApplyLocalApi();
            Refresh();
        });
        enabled.Name = "LocalApiEnabled";

        var port = new NumericUpDown
        {
            Name = "LocalApiPort", Minimum = 1024, Maximum = 65535, Increment = 1, FormatString = "0", Width = 140,
            Value = Math.Clamp(settings.LocalApiPort, 1024, 65535),
        };
        port.ValueChanged += (_, _) =>
        {
            int value = (int)Math.Clamp(port.Value ?? 48231, 1024, 65535);
            if (value == settings.LocalApiPort) return;
            settings.LocalApiPort = value;
            Apply();
        };

        var origins = new TextBox
        {
            Name = "LocalApiAllowedOrigins",
            Text = string.Join("\n", settings.LocalApiAllowedOrigins ?? new List<string>()),
            Watermark = "http://localhost:3000",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 64,
            Width = 280,
        };
        origins.LostFocus += (_, _) =>
        {
            var entries = (origins.Text ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var invalid = new List<string>();
            var valid = LocalMarketDataApiOptions.NormalizeOrigins(entries, invalid);
            ignored.Text = invalid.Count == 0 ? "" : $"Ignored invalid origins: {string.Join(", ", invalid)}";
            ignored.IsVisible = invalid.Count > 0;
            if (valid.SequenceEqual(settings.LocalApiAllowedOrigins ?? new List<string>(), StringComparer.Ordinal)) return;
            settings.LocalApiAllowedOrigins = valid;
            Apply();
        };

        Refresh();
        return Section("Local market-data API", MaterialIconKind.LanConnect,
            Setting("Serve market data to local tools",
                "Off by default. When on, Trading Hub serves read-only market data (candles, trades, order book and recorded depth of instruments you already have open) on 127.0.0.1 only, for your own tools on this computer. It never exposes credentials, accounts or orders and cannot change anything. Web pages on other sites are refused unless their origin is listed below.",
                enabled),
            Setting("Port", "Loopback TCP port of the local API (1024–65535).", port),
            Setting("Allowed browser origins",
                "Only needed for browser-based tools. One origin per line or separated by commas, such as http://localhost:3000. Invalid entries are ignored.",
                origins),
            Ui.V(4, address, status, ignored));
    }
}
