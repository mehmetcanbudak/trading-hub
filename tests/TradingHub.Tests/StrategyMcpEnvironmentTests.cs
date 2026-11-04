using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TradingHub.App.Services;

namespace TradingHub.Tests;

[Collection("StrategyAssistant")]
public sealed class StrategyMcpEnvironmentTests
{
    [UnixAssistantFact]
    public async Task CodexFilteredEnvironmentLaunchesBothGeneratedMcpChildrenIntoActualIsolatedConsumers()
    {
        string root = Path.Combine(Path.GetTempPath(), "assistant MCP Unicode 'quoted' = " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string dotnet = DotnetHost();
        string fixture = Path.Combine(AppContext.BaseDirectory, "AssistantCliFixture", "TradingHub.AssistantCliFixture.dll");
        Assert.True(File.Exists(fixture), "The spawned assistant fixture must be built and copied with the test project.");
        var environment = new Dictionary<string, string>
        {
            ["TRADINGHUB_DATA_DIR"] = Path.Combine(root, "data"),
            ["TRADINGHUB_STRATEGIES_DIR"] = Path.Combine(root, "strategies"),
            ["TRADINGHUB_INDICATORS_DIR"] = Path.Combine(root, "indicators"),
            ["TRADINGHUB_BACKTESTS_DIR"] = Path.Combine(root, "backtests"),
            ["TRADINGHUB_LIVE_DIR"] = Path.Combine(root, "live"),
            ["TRADINGHUB_KEYCHAIN_FILE"] = Path.Combine(root, "fake-keychain.json"),
            ["TRADINGHUB_SLEEP_GUARD"] = "off",
            ["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!,
            ["DOTNET_ROOT_ARM64"] = Path.GetDirectoryName(dotnet)!,
            ["DOTNET_HOST_PATH"] = dotnet,
            ["DOTNET_CLI_HOME"] = Path.Combine(root, "cli-home"),
            ["NUGET_PACKAGES"] = Path.Combine(root, "nuget"),
            ["TMPDIR"] = Path.Combine(root, "tmp"),
            ["TMP"] = Path.Combine(root, "tmp"),
            ["TEMP"] = Path.Combine(root, "tmp"),
            ["HOME"] = Path.Combine(root, "home"),
            ["CFFIXED_USER_HOME"] = Path.Combine(root, "home"),
        };
        foreach (var item in environment.Where(p => p.Key.EndsWith("DIR", StringComparison.Ordinal) || p.Key is "DOTNET_CLI_HOME" or "NUGET_PACKAGES" or "TMP" or "TEMP" or "HOME" or "CFFIXED_USER_HOME"))
            Directory.CreateDirectory(item.Value);
        File.WriteAllText(environment["TRADINGHUB_KEYCHAIN_FILE"], "{\"assistant-fixture-marker\":\"owned-fixture\"}");
        string expected = Path.Combine(root, "expected.json"); File.WriteAllText(expected, JsonSerializer.Serialize(environment));
        string codex = Path.Combine(root, "fake codex"); string mcp = Path.Combine(root, "fake local MCP");
        WriteExecutable(codex, $"#!/bin/sh\nexec {ShellQuote(dotnet)} {ShellQuote(fixture)} --fake-codex {ShellQuote(expected)} \"$@\"\n");
        WriteExecutable(mcp, $"#!/bin/sh\nexec {ShellQuote(dotnet)} {ShellQuote(fixture)} --mcp-child {ShellQuote(expected)} \"$@\"\n");
        var changes = new Dictionary<string, string>(environment)
        {
            ["TRADINGHUB_CODEX_CLI"] = codex, ["TRADINGHUB_MCP_COMMAND"] = mcp,
            ["TRADINGHUB_UNAPPROVED_FIXTURE"] = "synthetic-not-a-secret",
        };
        var previous = changes.ToDictionary(p => p.Key, p => Environment.GetEnvironmentVariable(p.Key));
        try
        {
            foreach (var item in changes) Environment.SetEnvironmentVariable(item.Key, item.Value);
            await using var research = new TradingViewResearchService(Path.Combine(root, "research-state.json"), new EmptySecrets(),
                (_, _) => Task.FromResult<ITradingViewResearchTransport>(new MetadataTransport()));
            var review = await research.ConnectAndReviewAsync();
            var access = await research.ApproveAsync(review, new[] { "get_ohlcv" });
            var bridge = new CodexBridge(research);
            var result = await bridge.SendAsync(new("Fixture only; start owned local MCP children.", root, "No network or model call.", true, tradingView: access))
                .WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal(StrategyChatStatus.Succeeded, result.Status);
            Assert.Equal("2", File.ReadAllText(Path.Combine(root, "children.json")));
            using var sealedLaunch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "research-launch.json")));
            Assert.NotEqual(mcp, sealedLaunch.RootElement.GetProperty("command").GetString());
            Assert.Contains("--research-mcp", sealedLaunch.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
            foreach (string mode in new[] { "lab", "research" })
            {
                using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, mode + "-receipt.json")));
                Assert.Equal(environment["TRADINGHUB_DATA_DIR"], receipt.RootElement.GetProperty("appData").GetString());
                Assert.Equal(environment["TRADINGHUB_STRATEGIES_DIR"], receipt.RootElement.GetProperty("strategies").GetString());
                Assert.Equal(environment["TRADINGHUB_INDICATORS_DIR"], receipt.RootElement.GetProperty("indicators").GetString());
                Assert.Equal(environment["TRADINGHUB_BACKTESTS_DIR"], receipt.RootElement.GetProperty("backtests").GetString());
                Assert.Equal(environment["TRADINGHUB_LIVE_DIR"], receipt.RootElement.GetProperty("live").GetString());
                Assert.Equal(environment["TRADINGHUB_KEYCHAIN_FILE"], receipt.RootElement.GetProperty("fakeKeychain").GetString());
                Assert.Equal(environment["DOTNET_ROOT"], receipt.RootElement.GetProperty("dotnetRoot").GetString());
                Assert.Equal(environment["TMPDIR"], receipt.RootElement.GetProperty("temp").GetString());
            }
        }
        finally
        {
            foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
            Directory.Delete(root, true);
        }
    }

    [UnixAssistantFact]
    public async Task DotnetHostedMcpLaunchIncludesActualApplicationAssemblyBeforeMode()
    {
        string output = Path.Combine(Path.GetTempPath(), "assistant-host-" + Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo(DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AssistantCliFixture", "TradingHub.AssistantCliFixture.dll"));
        start.ArgumentList.Add("--resolve-host"); start.ArgumentList.Add(output);
        start.Environment.Remove("TRADINGHUB_MCP_COMMAND");
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode == 0, await stderr + await stdout);
            using var resolved = JsonDocument.Parse(File.ReadAllText(output));
            var arguments = resolved.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray();
            Assert.Equal(2, arguments.Length); Assert.EndsWith("TradingHub.dll", arguments[0]); Assert.Equal("--mcp", arguments[1]);
        }
        finally { File.Delete(output); }
    }

    private static string DotnetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        string root = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
        string host = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Assert.True(File.Exists(host), "A real .NET host is required for the spawned consumer fixture.");
        return host;
    }
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private sealed class EmptySecrets : ISecretStore
    {
        public string Get(string account) => "";
        public void Set(string account, string? secret) { Assert.True(string.IsNullOrEmpty(secret)); }
    }
    private sealed class MetadataTransport : ITradingViewResearchTransport
    {
        public event Action? CatalogChanged;
        public event Action? Disconnected;
        public void ChangeCatalog() => CatalogChanged?.Invoke();
        public void Disconnect() => Disconnected?.Invoke();
        public Task<TradingViewToolPage> ListToolsAsync(string? cursor, CancellationToken token) => Task.FromResult(new TradingViewToolPage(
            [JsonSerializer.SerializeToElement(new { name = "get_ohlcv", description = "Owned metadata fixture", inputSchema = new { type = "object" } })], null));
        public Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken token) => throw new InvalidOperationException("No remote tool calls are authorized in this fixture.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
