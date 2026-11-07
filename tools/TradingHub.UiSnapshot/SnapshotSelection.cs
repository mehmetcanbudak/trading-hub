using System.Globalization;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Tests;

internal enum TradingSnapshotAction
{
    Layout2, Layout2r, Layout3, Layout4, Layout6,
    Jitter, Jitterold, Float, Brackets, Drawings, Drawsettings,
    Links, Toolbar, Trader, Accounts, Crossdemo, Strategy, Domfloat, Domsettings,
}

internal enum StrategySnapshotAction
{
    Backtest, Optimize, Wfo, Code, History, Reports, Portfolio, Cartera,
    Claude, Claudelab, Claudefloat,
}

internal enum ConnectionSnapshotAction { Rithmic, Amp }

internal sealed record SnapshotContext(MainWindow Window, Action<double> Pump, string Output,
    double Seconds, FakeRithmicServer Rithmic);

internal sealed record SnapshotScene(Func<SnapshotContext, ValueTask<int>> Run,
    bool Standalone = false, bool SupportsWindows = true, Func<AppServices>? CreateServices = null)
{
    public bool IsSupported => SupportsWindows || !OperatingSystem.IsWindows();
}

internal abstract record SnapshotSelection(string Name)
{
    internal sealed record Scene(string Name, SnapshotScene Definition) : SnapshotSelection(Name);
    internal sealed record Page(string Name) : SnapshotSelection(Name);
    internal abstract record Trading(string Name) : SnapshotSelection(Name);
    internal sealed record TradingAction(string Name, TradingSnapshotAction Action) : Trading(Name);
    internal sealed record TradingHistory(string Name, double Hours, ChartPeriod? Period) : Trading(Name);
    internal sealed record StrategyAction(string Name, StrategySnapshotAction Action, string? FileName) : SnapshotSelection(Name);
    internal sealed record ConnectionAction(string Name, ConnectionSnapshotAction Action) : SnapshotSelection(Name);

    public static SnapshotSelection Parse(string name, IReadOnlyDictionary<string, SnapshotScene> scenes)
    {
        if (scenes.TryGetValue(name, out var scene))
        {
            if (!scene.IsSupported) throw new ArgumentException($"Snapshot scene '{name}' is unavailable on this platform.");
            return new Scene(name, scene);
        }

        var parts = name.Split('+');
        if (parts is [var page] && MainWindow.HasPageId(page)) return new Page(name);
        if (parts.Length >= 2 && parts[0] == "trading")
        {
            string action = parts[1];
            if (action.StartsWith("history", StringComparison.Ordinal) && parts.Length is 2 or 3)
            {
                double hours = action == "historyall" ? -1 :
                    double.TryParse(action["history".Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
                        ? parsed : throw new ArgumentException($"Invalid history duration in snapshot selector '{name}'.");
                ChartPeriod? period = null;
                if (parts.Length == 3)
                {
                    if (!ChartPeriod.TryParse(parts[2], out var selected))
                        throw new ArgumentException($"Invalid history period in snapshot selector '{name}'.");
                    period = selected;
                }
                return new TradingHistory(name, hours, period);
            }
            if (parts.Length == 2 && ExactAction(action, out TradingSnapshotAction selectedAction))
                return new TradingAction(name, selectedAction);
        }
        else if (parts.Length is 2 or 3 && parts[0] == "strategies" &&
                 ExactAction(parts[1], out StrategySnapshotAction strategyAction) &&
                 (parts.Length == 2 || parts[2].Length > 0))
            return new StrategyAction(name, strategyAction, parts.Length == 3 ? parts[2] : null);
        else if (parts.Length == 2 && parts[0] == "connections" &&
                 ExactAction(parts[1], out ConnectionSnapshotAction connectionAction))
            return new ConnectionAction(name, connectionAction);
        throw new ArgumentException($"Unknown snapshot scene or action '{name}'.");
    }

    private static bool ExactAction<T>(string text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) &&
        string.Equals(text, value.ToString().ToLowerInvariant(), StringComparison.Ordinal);
}
