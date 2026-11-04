using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace TradingHub.App.Services;

public enum StrategyChatStatus { Succeeded, Failed, Cancelled }
public sealed record StrategyTokenUsage(long InputTokens, long CachedInputTokens, long OutputTokens);
public sealed record StrategyChatResult(StrategyChatStatus Status, string Text, double? CostUsd, double Seconds, bool NeedsLogin, StrategyTokenUsage? Usage = null)
{
    public bool IsError => Status != StrategyChatStatus.Succeeded;
}
public sealed record StrategyChatRequest
{
    public string Prompt { get; }
    public string WorkDirectory { get; }
    public string SystemPrompt { get; }
    public bool Laboratory { get; }
    public System.Collections.Immutable.ImmutableArray<string> ExtraDirectories { get; }
    public string? Model { get; }
    public string? Effort { get; }
    public TradingViewAccess? TradingView { get; }
    public StrategyChatRequest(string prompt, string workDirectory, string systemPrompt, bool laboratory = false,
        IReadOnlyList<string>? extraDirectories = null, string? model = null, string? effort = null, TradingViewAccess? tradingView = null)
    {
        Prompt = prompt; WorkDirectory = workDirectory; SystemPrompt = systemPrompt; Laboratory = laboratory;
        ExtraDirectories = System.Collections.Immutable.ImmutableArray.CreateRange(extraDirectories ?? Array.Empty<string>());
        Model = model; Effort = effort; TradingView = tradingView;
    }
}

public interface IStrategyChatBridge
{
    string Engine { get; }
    string? SessionId { get; }
    bool IsRunning { get; }
    event Action<string>? Text;
    event Action<string, string?>? ToolUsed;
    event Action? ToolFinished;
    event Action<StrategyChatResult>? Finished;
    void NewConversation();
    void Resume(string? sessionId);
    Task<StrategyChatResult> SendAsync(StrategyChatRequest request, CancellationToken cancellationToken = default);
    void Cancel();
}

public static class StrategyChatEngines
{
    public static bool IsKnown(string? engine) => engine is "claude" or "codex";
    public static IStrategyChatBridge Create(string engine) => engine switch
    {
        "claude" => new ClaudeCodeBridge(), "codex" => new CodexBridge(),
        _ => throw new ArgumentException("Unknown strategy assistant engine: " + engine, nameof(engine)),
    };
}

/// <summary>Owned CLI lifetime only. Protocols and policy flags remain adapter-specific.</summary>
public abstract class StrategyChatProcess : IStrategyChatBridge
{
    private CancellationTokenSource? _cancellation;
    private readonly TradingViewResearchService _research;
    protected StrategyChatProcess(TradingViewResearchService? research = null) => _research = research ?? TradingViewResearchService.Default;
    private Process? _process;
    private int _running;
    protected bool Terminal;
    protected bool Failed;
    protected string FinalText = "";
    protected double? ReportedCost;
    protected StrategyTokenUsage? Usage;
    public abstract string Engine { get; }
    public string? SessionId { get; private set; }
    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public event Action<string>? Text;
    public event Action<string, string?>? ToolUsed;
    public event Action? ToolFinished;
    public event Action<StrategyChatResult>? Finished;
    public void NewConversation() { if (IsRunning) throw new InvalidOperationException("Stop the active response first."); SessionId = null; }
    public void Resume(string? sessionId)
    {
        if (IsRunning) throw new InvalidOperationException("Stop the active response first.");
        ValidateSession(sessionId);
        SessionId = sessionId;
    }
    private static void ValidateSession(string? sessionId)
    {
        if (sessionId != null && (sessionId.Length is 0 or > 256 || sessionId[0] == '-' ||
            sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')))
            throw new ArgumentException("Invalid assistant session ID. Only an exact CLI-issued ID can be resumed.");
    }
    protected void ObserveSession(string sessionId)
    {
        ValidateSession(sessionId);
        if (SessionId != null && SessionId != sessionId)
            throw new InvalidDataException($"{Engine} returned a different session while resuming; the response was rejected.");
        SessionId = sessionId;
    }
    protected abstract string? FindExecutable();
    protected abstract Task<ProcessStartInfo> BuildStartAsync(string cli, StrategyChatRequest request, CancellationToken token);
    protected abstract void Parse(JsonElement root);
    protected virtual void ResetParser() { }
    protected virtual string Input(StrategyChatRequest request) => request.SystemPrompt + Localization.Lang.ForClaude + "\n\n" + request.Prompt;
    protected void EmitText(string value) { var handler = Text; Post(() => handler?.Invoke(value)); }
    protected void EmitTool(string name, string? file) { var handler = ToolUsed; Post(() => handler?.Invoke(name, file)); }
    protected void EmitToolFinished() { var handler = ToolFinished; Post(() => handler?.Invoke()); }
    protected static void Post(Action action) => Dispatcher.UIThread.Post(action);
    protected static string Value(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    protected static void Add(ProcessStartInfo psi, params string[] args) { foreach (var arg in args) psi.ArgumentList.Add(arg); }
    protected static string Quote(string value) => JsonSerializer.Serialize(value);

    public async Task<StrategyChatResult> SendAsync(StrategyChatRequest request, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("An assistant response is already running.");
        var watch = Stopwatch.StartNew();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation = cancellation;
        var token = cancellation.Token;
        var diagnostics = new StringBuilder();
        Terminal = Failed = false; FinalText = ""; ReportedCost = null; Usage = null; ResetParser();
        void AccessChanged() { if (request.TradingView != null) Cancel(); }
        _research.AccessChanged += AccessChanged;
        StrategyChatResult result;
        try
        {
            token.ThrowIfCancellationRequested();
            if (request.TradingView is { } captured)
            {
                var current = _research.CaptureAccess();
                if (current == null || current.GrantId != captured.GrantId || current.ApprovalId != captured.ApprovalId ||
                    current.CatalogFingerprint != captured.CatalogFingerprint || !current.ApprovedToolNames.SequenceEqual(captured.ApprovedToolNames))
                    throw new InvalidOperationException("Research access changed. Review and approve access before sending another request.");
            }
            var cli = FindExecutable() ?? throw new FileNotFoundException($"{Engine} CLI was not found. Install it and sign in in Terminal, then retry.");
            var start = await BuildStartAsync(cli, request, token);
            token.ThrowIfCancellationRequested();
            _process = Process.Start(start) ?? throw new IOException("The assistant process did not start.");
            using var registration = token.Register(() => Kill(_process));
            var errors = DrainDiagnosticsAsync(_process.StandardError, diagnostics);
            var feed = FeedAsync(_process, Input(request), token);
            var lines = ReadOutputAsync(_process.StandardOutput, token);
            await Task.WhenAll(lines, feed, _process.WaitForExitAsync(token));
            await errors;
            token.ThrowIfCancellationRequested();
            if (_process.ExitCode != 0) { Failed = true; FinalText = Append(FinalText, $"{Engine} exited with code {_process.ExitCode}. " + diagnostics.ToString().Trim()); }
            if (!Terminal) { Failed = true; FinalText = Append(FinalText, $"{Engine} exited without a response. " + diagnostics.ToString().Trim()); }
            bool login = Failed && new[] { "authenticate", "login", "not logged in", "OAuth", "unauthorized", "token_expired", "sign in", "refresh token" }.Any(value => FinalText.Contains(value, StringComparison.OrdinalIgnoreCase));
            result = new(Failed ? StrategyChatStatus.Failed : StrategyChatStatus.Succeeded, FinalText, ReportedCost, watch.Elapsed.TotalSeconds, login, Usage);
        }
        catch (Exception ex)
        {
            Kill(_process);
            result = token.IsCancellationRequested ? new(StrategyChatStatus.Cancelled, "Response stopped.", null, watch.Elapsed.TotalSeconds, false)
                : new(StrategyChatStatus.Failed, $"Could not run {Engine}: {ex.Message}", null, watch.Elapsed.TotalSeconds, false);
        }
        finally
        {
            _research.AccessChanged -= AccessChanged;
            _process?.Dispose(); _process = null; _cancellation = null;
            Volatile.Write(ref _running, 0);
        }
        var finished = Finished;
        Post(() => finished?.Invoke(result));
        return result;
    }
    private static string Append(string first, string second) => first.Length == 0 ? second : first + "\n" + second;
    private async Task ReadOutputAsync(StreamReader reader, CancellationToken token)
    {
        try
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (line.Length == 0) continue;
                try { using var doc = JsonDocument.Parse(line); Parse(doc.RootElement); }
                catch (JsonException) { /* A terminal protocol event is still required. */ }
            }
        }
        catch { Kill(_process); throw; }
    }
    private static async Task FeedAsync(Process process, string prompt, CancellationToken token)
    {
        try { await process.StandardInput.WriteAsync(prompt.AsMemory(), token); await process.StandardInput.FlushAsync(token); }
        catch { Kill(process); throw; }
        finally { process.StandardInput.Close(); }
    }
    private static async Task DrainDiagnosticsAsync(StreamReader reader, StringBuilder output)
    {
        var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            if (output.Length < 16384) output.Append(buffer, 0, Math.Min(count, 16384 - output.Length));
    }
    protected static async Task<string> HelpAsync(string cli, string workDir, CancellationToken token, params string[] args)
    {
        var start = ClaudeCodeBridge.Prepare(cli, workDir); Add(start, args);
        start.Environment["PATH"] = Path.GetDirectoryName(Path.GetFullPath(cli)) + Path.PathSeparator + start.Environment["PATH"];
        using var process = Process.Start(start) ?? throw new IOException("Could not inspect CLI capabilities.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var registration = timeout.Token.Register(() => Kill(process));
        process.StandardInput.Close();
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        await Task.WhenAll(DrainDiagnosticsAsync(process.StandardOutput, stdout), DrainDiagnosticsAsync(process.StandardError, stderr), process.WaitForExitAsync(timeout.Token));
        if (process.ExitCode != 0) throw new InvalidOperationException("CLI capability inspection failed: " + stderr);
        return stdout + "\n" + stderr;
    }
    public void Cancel() { _cancellation?.Cancel(); Kill(_process); }
    private static void Kill(Process? process) { try { if (process is { HasExited: false }) process.Kill(true); } catch (InvalidOperationException) { } }
}
