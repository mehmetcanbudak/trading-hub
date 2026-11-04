namespace TradingHub.App.Services;

/// <summary>Local application MCP launch identity and explicitly forwarded non-secret runtime/store paths.</summary>
public static class StrategyMcpProcess
{
    public static IReadOnlyDictionary<string, string> ChildEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Codex's stdio client filters inherited environment. Do not rely on GUI inheritance or copy all variables:
        // in particular provider/model credentials and OAuth material must never enter these configurations.
        foreach (string name in new[]
        {
            "TRADINGHUB_DATA_DIR", "TRADINGHUB_STRATEGIES_DIR", "TRADINGHUB_INDICATORS_DIR",
            "TRADINGHUB_BACKTESTS_DIR", "TRADINGHUB_LIVE_DIR", "TRADINGHUB_KEYCHAIN_FILE", "TRADINGHUB_SLEEP_GUARD",
            "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_ROOT(x86)",
            "DOTNET_HOST_PATH", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "TMPDIR", "TMP", "TEMP",
            "HOME", "USERPROFILE", "CFFIXED_USER_HOME", "PATH",
        })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) result.Add(name, value);
        return result;
    }

    public static (string Command, string[] Arguments) LabCommand() =>
        Environment.GetEnvironmentVariable("TRADINGHUB_MCP_COMMAND") is { Length: > 0 } custom
            ? (custom, ["--mcp"]) : AppCommand("--mcp");

    /// <summary>Sealed actual application identity. The original Lab fixture override must never reroute research.</summary>
    public static (string Command, string[] Arguments) AppCommand(params string[] arguments)
    {
        string assembly = typeof(StrategyMcpProcess).Assembly.Location;
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application executable is unavailable.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (executable, [assembly, .. arguments]);
        if (Path.GetFileNameWithoutExtension(executable).Equals("TradingHub", StringComparison.OrdinalIgnoreCase))
            return (executable, arguments);
        // Test/snapshot hosts must not recursively launch themselves as if they were the application.
        string appHost = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "TradingHub.exe" : "TradingHub");
        if (File.Exists(appHost)) return (appHost, arguments);
        string? dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(dotnet) && Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            dotnet = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!string.IsNullOrEmpty(dotnet) && File.Exists(dotnet)) return (dotnet, [assembly, .. arguments]);
        throw new InvalidOperationException("Trading Hub's apphost or .NET host is unavailable for the local MCP server.");
    }
}
