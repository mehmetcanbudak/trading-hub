using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TradingHub.App.Services;
using TradingHub.Strategies;

namespace TradingHub.AssistantCliFixture;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length < 2) throw new InvalidOperationException("A fixture mode and owned expectation file are required.");
            return args[0] switch
            {
                "--fake-codex" => await CodexAsync(args[1], args[2..]),
                "--mcp-child" => await ChildAsync(args[1], args[2..]),
                "--resolve-host" => ResolveHost(args[1]),
                _ => throw new InvalidOperationException("Unknown fixture mode."),
            };
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static async Task<int> CodexAsync(string expectedPath, string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("--ignore-user-config --ignore-rules --sandbox --json --add-dir DIR");
            return 0;
        }
        _ = await Console.In.ReadToEndAsync();
        string config = args.First(a => a.StartsWith("mcp_servers=", StringComparison.Ordinal))["mcp_servers=".Length..];
        using var parsed = JsonDocument.Parse(InlineTomlToJson(config));
        int children = 0;
        foreach (var server in parsed.RootElement.EnumerateObject())
        {
            var definition = server.Value;
            string configuredCommand = definition.GetProperty("command").GetString()!;
            string executable = configuredCommand;
            if (server.Name == "tradingview_research")
            {
                // TEST FIXTURE ONLY: consume the sealed production config/env, then execute our local probe instead.
                // There is deliberately no production research executable override.
                string probe = Path.Combine(Path.GetDirectoryName(expectedPath)!, "fake local MCP");
                if (configuredCommand == probe) throw new InvalidOperationException("The Lab command override escaped into production research configuration.");
                var configuredArgs = definition.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray();
                if (!configuredArgs.Contains("--research-mcp")) throw new InvalidOperationException("The actual research application mode was omitted.");
                if (Path.GetFileNameWithoutExtension(configuredCommand).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                    && (configuredArgs.Length == 0 || Path.GetFileName(configuredArgs[0]) != "TradingHub.dll"))
                    throw new InvalidOperationException("The research dotnet command omitted the application assembly.");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(expectedPath)!, "research-launch.json"),
                    JsonSerializer.Serialize(new { command = configuredCommand, args = configuredArgs }));
                executable = probe;
            }
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in definition.GetProperty("args").EnumerateArray()) start.ArgumentList.Add(argument.GetString()!);
            // Codex/rmcp-client's documented default inherited environment is deliberately narrow.
            // Start from empty, preserve only its POSIX defaults, then apply the explicitly configured server env.
            start.Environment.Clear();
            foreach (string name in new[] { "HOME", "LOGNAME", "PATH", "SHELL", "USER", "__CF_USER_TEXT_ENCODING", "LANG", "LC_ALL", "TERM", "TMPDIR", "TZ" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            if (definition.TryGetProperty("env", out var environment))
                foreach (var variable in environment.EnumerateObject()) start.Environment[variable.Name] = variable.Value.GetString()!;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Configured MCP child did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            var exit = process.WaitForExitAsync();
            Exception? failure = null;
            try
            {
                await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"owned-fixture\",\"version\":\"1\"}}}");
                process.StandardInput.Close();
                await exit.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception error)
            {
                failure = error;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            finally
            {
                await exit;
                await Task.WhenAll(output, errors);
            }
            string stdout = await output, stderr = await errors;
            string capture = Path.Combine(Path.GetDirectoryName(expectedPath)!, server.Name + ".mcp");
            await File.WriteAllTextAsync(capture + ".stdout.txt", stdout);
            await File.WriteAllTextAsync(capture + ".stderr.txt", stderr);
            if (failure != null)
                throw new InvalidOperationException("Configured MCP child failed: " + failure.Message
                    + "\nChild stdout:\n" + stdout + "\nChild stderr:\n" + stderr, failure);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Configured MCP child exited with code " + process.ExitCode
                    + ".\nChild stdout:\n" + stdout + "\nChild stderr:\n" + stderr);
            using var response = JsonDocument.Parse(stdout);
            if (response.RootElement.GetProperty("id").GetInt32() != 1 || !response.RootElement.TryGetProperty("result", out _))
                throw new InvalidOperationException("Configured MCP child did not answer initialization.");
            children++;
        }
        if (children != 2) throw new InvalidOperationException("The isolated fixture requires both configured local servers.");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(expectedPath)!, "children.json"), JsonSerializer.Serialize(children));
        Console.WriteLine("{\"type\":\"turn.completed\"}");
        return 0;
    }

    private static async Task<int> ChildAsync(string expectedPath, string[] args)
    {
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(expectedPath))!;
        // Fail BEFORE initializing any application getter or credential-store type if isolation is missing.
        foreach (var item in expected)
            if (Environment.GetEnvironmentVariable(item.Key) != item.Value) throw new InvalidOperationException("Missing or changed isolated child setting: " + item.Key);
        if (Environment.GetEnvironmentVariable("TRADINGHUB_UNAPPROVED_FIXTURE") != null)
            throw new InvalidOperationException("An unapproved inherited variable reached the MCP child.");
        foreach (string key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "ANTHROPIC_API_KEY" })
            if (Environment.GetEnvironmentVariable(key) != null) throw new InvalidOperationException("An unapproved credential variable reached the MCP child.");
        if (expected["TRADINGHUB_SLEEP_GUARD"] != "off") throw new InvalidOperationException("Fixture sleep guard must be off.");
        Equal(AppPaths.Root, expected["TRADINGHUB_DATA_DIR"], "application data");
        Equal(AppPaths.Strategies, expected["TRADINGHUB_STRATEGIES_DIR"], "strategies");
        Equal(AppPaths.UserIndicators, expected["TRADINGHUB_INDICATORS_DIR"], "indicators");
        _ = new StrategyCatalog(); // Actual catalog consumer resolves and reads only the configured fixture store.
        Equal(StrategyStore.Root, expected["TRADINGHUB_BACKTESTS_DIR"], "backtests");
        if (Keychain.Get("assistant-fixture-marker") != "owned-fixture")
            throw new InvalidOperationException("Actual Keychain consumer did not select the owned fake secrets file.");
        string? line = await Console.In.ReadLineAsync();
        using var request = JsonDocument.Parse(line ?? throw new InvalidOperationException("MCP initialization was not received."));
        if (request.RootElement.GetProperty("method").GetString() != "initialize") throw new InvalidOperationException("Expected MCP initialization only.");
        string mode = args.Contains("--research-mcp") ? "research" : args.Contains("--mcp") ? "lab" : throw new InvalidOperationException("The generated application mode was missing.");
        if (mode == "research" && (!args.Contains("--approval") || args.Length < 3)) throw new InvalidOperationException("Research approval identity was not passed.");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(expectedPath)!, mode + "-receipt.json"), JsonSerializer.Serialize(new
        {
            mode, appData = AppPaths.Root, strategies = AppPaths.Strategies, indicators = AppPaths.UserIndicators,
            backtests = StrategyStore.Root, live = Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR"),
            fakeKeychain = Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE"),
            dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT"), temp = Environment.GetEnvironmentVariable("TMPDIR"),
        }));
        Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"owned-child\",\"version\":\"1\"}}}");
        return 0;
    }

    private static int ResolveHost(string output)
    {
        Environment.SetEnvironmentVariable("TRADINGHUB_MCP_COMMAND", null);
        var launch = StrategyMcpProcess.AppCommand("--mcp");
        if (!Path.GetFileNameWithoutExtension(launch.Command).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || launch.Arguments.Length != 2 || launch.Arguments[0] != typeof(StrategyMcpProcess).Assembly.Location || launch.Arguments[1] != "--mcp")
            throw new InvalidOperationException("A dotnet-hosted MCP launch must include the actual App assembly before --mcp.");
        File.WriteAllText(output, JsonSerializer.Serialize(new { command = launch.Command, args = launch.Arguments }));
        return 0;
    }

    private static void Equal(string actual, string expected, string name)
    {
        string actualPath = Path.GetFullPath(actual), expectedPath = Path.GetFullPath(expected);
        if (!actualPath.Equals(expectedPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The actual " + name + " consumer escaped the isolated store."
                + "\nExpected: " + expectedPath + "\nActual: " + actualPath);
    }

    // Generated adapter values use quoted-key inline TOML with JSON-compatible scalar/array syntax.
    // Convert separators only outside quoted strings; paths containing '=' or escaped quotes stay intact.
    private static string InlineTomlToJson(string value)
    {
        var result = new StringBuilder(value.Length); bool quoted = false, escaped = false;
        foreach (char c in value)
        {
            if (quoted)
            {
                result.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
            }
            else { if (c == '"') quoted = true; result.Append(c == '=' ? ':' : c); }
        }
        return result.ToString();
    }
}
