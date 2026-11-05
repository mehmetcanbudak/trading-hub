using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TradingHub.App.Services;

/// <summary>Optional user-authenticated CLI. Billing is unknown unless explicitly reported by the protocol.</summary>
public sealed class CodexBridge : StrategyChatProcess
{
    public CodexBridge(TradingViewResearchService? research = null) : base(research) { }
    public override string Engine => "codex";
    public static string? FindCli()
    {
        if (Environment.GetEnvironmentVariable("TRADINGHUB_CODEX_CLI") is { Length: > 0 } custom) return File.Exists(custom) ? custom : null;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directories = new List<string>
        {
            Path.Combine(home, ".local", "bin"), Path.Combine(home, ".npm-global", "bin"), Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".volta", "bin"), "/opt/homebrew/bin", "/usr/local/bin",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
        };
        directories.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        string nvm = Path.Combine(home, ".nvm", "versions", "node");
        try
        {
            if (Directory.Exists(nvm)) directories.AddRange(Directory.GetDirectories(nvm).OrderByDescending(d => d, StringComparer.Ordinal).Select(d => Path.Combine(d, "bin")));
        }
        catch (IOException) { /* PATH and explicit application locations remain available. */ }
        catch (UnauthorizedAccessException) { /* Do not require access to another installation. */ }
        return directories.SelectMany(dir => (OperatingSystem.IsWindows() ? new[] { "codex.exe", "codex.cmd" } : new[] { "codex" }).Select(name => Path.Combine(dir.Trim('"'), name)))
            .Concat(new[] { "/Applications/Codex.app/Contents/Resources/codex", Path.Combine(home, "Applications", "Codex.app", "Contents", "Resources", "codex") }).FirstOrDefault(File.Exists);
    }
    public static string? OpenLoginTerminal()
    {
        string? cli = FindCli();
        if (cli == null) return "Codex CLI was not found. Install Codex and run codex login in Terminal.";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe", "/d /v:off /s /k \"\"%TRADINGHUB_CODEX_LOGIN%\" login\"") { UseShellExecute = false };
                start.Environment["TRADINGHUB_CODEX_LOGIN"] = cli;
                Process.Start(start)?.Dispose();
            }
            else
            {
                string command = "'" + cli.Replace("'", "'\\''") + "' login";
                var start = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add("tell application \"Terminal\" to do script \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                start.ArgumentList.Add("-e"); start.ArgumentList.Add("tell application \"Terminal\" to activate");
                Process.Start(start)?.Dispose();
            }
            return null;
        }
        catch (Exception ex) { return "Could not open Codex sign-in: " + ex.Message + ". Run codex login in Terminal."; }
    }
    protected override string? FindExecutable() => FindCli();
    protected override async Task<ProcessStartInfo> BuildStartAsync(string cli, StrategyChatRequest request, CancellationToken token)
    {
        string help = await HelpAsync(cli, request.WorkDirectory, token, "exec", "--help");
        foreach (string flag in new[] { "--ignore-user-config", "--ignore-rules", "--sandbox", "--json" })
            if (!help.Contains(flag, StringComparison.Ordinal)) throw new InvalidOperationException($"Codex isolation requires {flag}; this CLI does not advertise support. Update Codex before using strategy chat.");
        if (request.TextOnly)
        {
            string features = await HelpAsync(cli, request.WorkDirectory, token, "--ignore-user-config", "--ignore-rules", "features", "list");
            foreach (string feature in new[] { "shell_tool", "apps" })
                if (!features.Split('\n').Any(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == feature))
                    throw new InvalidOperationException($"Draft-only Pine handoff requires Codex feature control {feature}. Update Codex before sending source.");
        }
        var psi = ClaudeCodeBridge.Prepare(cli, request.WorkDirectory);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = Encoding.UTF8;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var key in psi.Environment.Keys.ToArray())
            if ((key.StartsWith("CODEX", comparison) && key != "CODEX_HOME") || key.StartsWith("OPENAI", comparison)) psi.Environment.Remove(key);
        psi.Environment["PATH"] = Path.GetDirectoryName(Path.GetFullPath(cli)) + Path.PathSeparator + psi.Environment["PATH"];
        Add(psi, "exec", "--ignore-user-config", "--ignore-rules", "--json", "--sandbox", request.TextOnly ? "read-only" : "workspace-write", "--skip-git-repo-check");
        Config("approval_policy", Quote("never"));
        Config("sandbox_workspace_write.network_access", "false");
        Config("sandbox_workspace_write.exclude_tmpdir_env_var", "true");
        Config("sandbox_workspace_write.exclude_slash_tmp", "true");
        Config("features.hooks", "false");
        Config("allow_login_shell", "false");
        Config("project_doc_max_bytes", "0");
        Config("web_search", Quote("disabled"));
        Config("skills.include_instructions", "false");
        Config("skills.bundled.enabled", "false");
        if (request.TextOnly)
        {
            Config("features.shell_tool", "false");
            Config("features.apps", "false");
            Config("features.view_image", "false");
            Config("features.multi_agent", "false");
            Config("features.code_mode.enabled", "false");
        }
        // Explicit untrusted entries suppress every project-scoped config/hooks/rules layer.
        // User config is ignored, but CLI-owned authentication and exact-session storage remain available.
        for (var dir = new DirectoryInfo(Path.GetFullPath(request.WorkDirectory)); dir != null; dir = dir.Parent)
            Config("projects." + Quote(dir.FullName) + ".trust_level", Quote("untrusted"));
        var servers = new Dictionary<string, object>();
        if (request.Laboratory)
        {
            var launch = StrategyMcpProcess.LabCommand();
            servers.Add("tradinghub", new { command = launch.Command, args = launch.Arguments, env = StrategyMcpProcess.ChildEnvironment(), enabled_tools = ClaudeCodeBridge.LabTools, required = true, tool_timeout_sec = 3600 });
        }
        if (request.TradingView != null)
        {
            using var config = JsonDocument.Parse(request.TradingView.CreateMcpConfigJson());
            var server = config.RootElement.GetProperty("mcpServers").GetProperty("tradingview_research");
            servers.Add("tradingview_research", new { command = Value(server, "command"), args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray(), env = StrategyMcpProcess.ChildEnvironment(), enabled_tools = request.TradingView.ApprovedToolNames, required = true });
        }
        Config("mcp_servers", ToToml(servers));
        if (!string.IsNullOrWhiteSpace(request.Model)) Add(psi, "--model", request.Model);
        if (!string.IsNullOrWhiteSpace(request.Effort)) Config("model_reasoning_effort", Quote(request.Effort));
        foreach (var directory in request.ExtraDirectories) if (Directory.Exists(directory)) Add(psi, "--add-dir", directory);
        if (SessionId != null && !request.TextOnly) Add(psi, "resume", SessionId);
        Add(psi, "-");
        return psi;
        void Config(string key, string value) => Add(psi, "-c", key + "=" + value);
    }
    private static string ToToml(object value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return Encode(document.RootElement);
        static string Encode(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Select(p => Quote(p.Name) + "=" + Encode(p.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Encode)) + "]",
            _ => element.GetRawText(),
        };
    }
    private readonly HashSet<string> _messages = new(StringComparer.Ordinal);
    protected override void ResetParser() => _messages.Clear();
    protected override void Parse(JsonElement root)
    {
        switch (Value(root, "type"))
        {
            case "thread.started":
                ObserveSession(Value(root, "thread_id"));
                break;
            case "item.started":
            case "item.completed":
                if (!root.TryGetProperty("item", out var item)) break;
                string type = Value(item, "type"), eventType = Value(root, "type");
                if (type == "agent_message" && eventType == "item.completed" && _messages.Add(Value(item, "id")))
                {
                    string text = Value(item, "text"); FinalText += text; EmitText(text);
                }
                else if (type == "file_change" && eventType == "item.completed")
                {
                    if (item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
                        foreach (var change in changes.EnumerateArray()) EmitTool(Value(change, "kind") == "add" ? "Write" : "Edit", Value(change, "path"));
                    EmitToolFinished();
                }
                else if (type is "command_execution" or "mcp_tool_call")
                {
                    if (eventType == "item.started") EmitTool(type == "mcp_tool_call" ? "mcp__" + Value(item, "server") + "__" + Value(item, "tool") : "Command", Value(item, "command"));
                    else EmitToolFinished();
                }
                break;
            case "turn.completed":
                Terminal = true;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                    && usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt64(out long inputTokens)
                    && usage.TryGetProperty("cached_input_tokens", out var cached) && cached.TryGetInt64(out long cachedTokens)
                    && usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt64(out long outputTokens))
                    Usage = new(inputTokens, cachedTokens, outputTokens);
                break;
            case "turn.failed":
                Terminal = Failed = true;
                FinalText = root.TryGetProperty("error", out var error)
                    ? error.ValueKind == JsonValueKind.String ? error.GetString() ?? "Codex turn failed." : Value(error, "message")
                    : "Codex turn failed.";
                break;
            case "error":
                Failed = true; FinalText = Value(root, "message"); break;
        }
    }
}
