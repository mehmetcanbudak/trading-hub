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
    /// <summary>Draft-only handoffs disable execution/edit tools and all laboratory/research access.</summary>
    public bool TextOnly { get; }
    public StrategyChatRequest(string prompt, string workDirectory, string systemPrompt, bool laboratory = false,
        IReadOnlyList<string>? extraDirectories = null, string? model = null, string? effort = null, TradingViewAccess? tradingView = null, bool textOnly = false)
    {
        Prompt = prompt; WorkDirectory = workDirectory; SystemPrompt = systemPrompt; Laboratory = laboratory;
        if (textOnly && (laboratory || tradingView != null || extraDirectories is { Count: > 0 }))
            throw new ArgumentException("Text-only requests cannot access laboratory, research or extra directories.");
        TextOnly = textOnly;
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
    private readonly object _lifetimeGate = new();
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
        lock (_lifetimeGate) _cancellation = cancellation;
        var token = cancellation.Token;
        var diagnostics = new StringBuilder();
        Terminal = Failed = false; FinalText = ""; ReportedCost = null; Usage = null; ResetParser();
        void AccessChanged() { if (request.TradingView != null) Cancel(); }
        _research.AccessChanged += AccessChanged;
        Process? process = null;
        Task feed = Task.CompletedTask, lines = Task.CompletedTask, errors = Task.CompletedTask;
        Task? exit = null;
        CancellationTokenRegistration termination = default;
        Exception? failure = null;
        Exception? terminationFailure = null;
        int exitCode = 0;
        int terminationRequested = 0;
        void Terminate()
        {
            if (process == null || Interlocked.Exchange(ref terminationRequested, 1) != 0) return;
            try { Kill(process); }
            catch (Exception error) { Interlocked.CompareExchange(ref terminationFailure, error, null); }
        }
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
            process = Process.Start(start) ?? throw new IOException("The assistant process did not start.");
            termination = token.Register(Terminate);
            // Request cancellation asks the child to stop; it is never the child-exit/drain barrier.
            exit = process.WaitForExitAsync(CancellationToken.None);
            errors = DrainDiagnosticsAsync(process.StandardError, diagnostics, Terminate);
            lines = ReadOutputAsync(process.StandardOutput, token, Terminate);
            feed = FeedAsync(process, Input(request), token, Terminate);
            await Task.WhenAll(lines, feed, errors, exit);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            failure = ex;
            Terminate();
        }
        finally
        {
            _research.AccessChanged -= AccessChanged;
            if (process != null)
            {
                try
                {
                    if (failure != null || token.IsCancellationRequested) Terminate();
                    await (exit ??= process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
                    var redirected = Task.WhenAll(feed, lines, errors);
                    try { await redirected.ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        if (redirected.IsFaulted) failure = Combine(failure, redirected.Exception ?? error);
                        else failure ??= error;
                    }
                    exitCode = process.ExitCode;
                }
                finally
                {
                    termination.Dispose();
                    process.Dispose();
                }
            }
        }
        StrategyChatResult result;
        if (terminationFailure != null)
            result = new(StrategyChatStatus.Failed, $"Could not terminate {Engine}: {terminationFailure.Message}", null, watch.Elapsed.TotalSeconds, false);
        else if (failure is CliTerminationException)
            result = new(StrategyChatStatus.Failed, failure.Message, null, watch.Elapsed.TotalSeconds, false);
        else if (token.IsCancellationRequested)
            result = new(StrategyChatStatus.Cancelled, "Response stopped." +
                (failure is not null and not OperationCanceledException ? " " + failure.Message : ""), null, watch.Elapsed.TotalSeconds, false);
        else if (failure != null)
            result = new(StrategyChatStatus.Failed, $"Could not run {Engine}: {failure.Message}", null, watch.Elapsed.TotalSeconds, false);
        else
        {
            if (exitCode != 0) { Failed = true; FinalText = Append(FinalText, $"{Engine} exited with code {exitCode}. " + diagnostics.ToString().Trim()); }
            if (!Terminal) { Failed = true; FinalText = Append(FinalText, $"{Engine} exited without a response. " + diagnostics.ToString().Trim()); }
            bool login = Failed && new[] { "authenticate", "login", "not logged in", "OAuth", "unauthorized", "token_expired", "sign in", "refresh token" }.Any(value => FinalText.Contains(value, StringComparison.OrdinalIgnoreCase));
            result = new(Failed ? StrategyChatStatus.Failed : StrategyChatStatus.Succeeded, FinalText, ReportedCost, watch.Elapsed.TotalSeconds, login, Usage);
        }
        var finished = Finished;
        lock (_lifetimeGate)
        {
            _cancellation = null;
            Volatile.Write(ref _running, 0);
            Post(() => finished?.Invoke(result));
        }
        return result;
    }
    private static string Append(string first, string second) => first.Length == 0 ? second : first + "\n" + second;
    private static Exception Combine(Exception? first, Exception second) =>
        first == null || ReferenceEquals(first, second) ? second : new AggregateException(first, second);
    private async Task ReadOutputAsync(StreamReader reader, CancellationToken token, Action terminate)
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
        catch { terminate(); throw; }
    }
    private static async Task FeedAsync(Process process, string prompt, CancellationToken token, Action terminate)
    {
        try { await process.StandardInput.WriteAsync(prompt.AsMemory(), token); await process.StandardInput.FlushAsync(token); }
        catch { terminate(); throw; }
        finally { process.StandardInput.Close(); }
    }
    private static async Task DrainDiagnosticsAsync(StreamReader reader, StringBuilder output, Action? terminate = null)
    {
        try
        {
            var buffer = new char[2048]; int count;
            while ((count = await reader.ReadAsync(buffer)) != 0)
                if (output.Length < 16384) output.Append(buffer, 0, Math.Min(count, 16384 - output.Length));
        }
        catch { terminate?.Invoke(); throw; }
    }
    protected static async Task<string> HelpAsync(string cli, string workDir, CancellationToken token, params string[] args)
    {
        var start = ClaudeCodeBridge.Prepare(cli, workDir); Add(start, args);
        start.Environment["PATH"] = Path.GetDirectoryName(Path.GetFullPath(cli)) + Path.PathSeparator + start.Environment["PATH"];
        using var process = Process.Start(start) ?? throw new IOException("Could not inspect CLI capabilities.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Exception? terminationFailure = null;
        int terminationRequested = 0;
        void Terminate()
        {
            if (Interlocked.Exchange(ref terminationRequested, 1) != 0) return;
            try { Kill(process); }
            catch (Exception error) { Interlocked.CompareExchange(ref terminationFailure, error, null); }
        }
        using var registration = timeout.Token.Register(Terminate);
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        Task output = Task.CompletedTask, errors = Task.CompletedTask;
        Task? exit = null;
        Exception? failure = null;
        try
        {
            exit = process.WaitForExitAsync(CancellationToken.None);
            output = DrainDiagnosticsAsync(process.StandardOutput, stdout, Terminate);
            errors = DrainDiagnosticsAsync(process.StandardError, stderr, Terminate);
            process.StandardInput.Close();
            await Task.WhenAll(output, errors, exit).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; Terminate(); }
        finally
        {
            if (failure != null || timeout.IsCancellationRequested) Terminate();
            await (exit ??= process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
            var redirected = Task.WhenAll(output, errors);
            try { await redirected.ConfigureAwait(false); }
            catch (Exception error) { failure = Combine(failure, redirected.Exception ?? error); }
        }
        if (terminationFailure != null) throw new CliTerminationException("Could not terminate CLI capability inspection.", terminationFailure);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (process.ExitCode != 0) throw new InvalidOperationException("CLI capability inspection failed: " + stderr);
        return stdout + "\n" + stderr;
    }
    public void Cancel() { lock (_lifetimeGate) _cancellation?.Cancel(); }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { /* The child exited between inspection and termination. */ }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { /* Another stop path already terminated this child. */ }
    }
    private sealed class CliTerminationException(string message, Exception inner) : IOException(message, inner);
}
