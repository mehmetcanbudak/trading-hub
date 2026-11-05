using Avalonia.Threading;
using TradingHub.Common.Logging;
using TradingHub.Scripting;

namespace TradingHub.App.Services;

/// <summary>
/// Compiles the user's recursive indicator source tree and retains the last good catalog on errors.
/// Exact-content polling avoids macOS FileSystemWatcher's synchronous system-wide flush at startup.
/// </summary>
public sealed class UserScripts : IDisposable, IAsyncDisposable
{
    public const string Source = "Mis indicadores";
    private readonly IndicatorCatalog _catalog;
    private readonly ScriptCompiler _compiler = new();
    private readonly IndicatorSourceFiles _sources;
    private readonly Action<Action> _dispatch;
    private readonly bool _uiDispatcher;
    private readonly Func<IReadOnlyList<ScriptSource>, CancellationToken, ScriptCompileResult> _compile;
    private readonly CancellationTokenSource _compileStop = new();
    private readonly CancellationToken _compileToken;
    private readonly object _compileGate = new();
    private readonly object _publicationGate = new();
    private readonly object _customDebounceGate = new();
    private readonly IScriptDirectoryMonitor? _monitor;
    private readonly DispatcherTimer? _debounce;
    private readonly System.Threading.Timer? _customDebounce;
    private readonly TaskCompletionSource _monitorDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _compiledVersion = -1;
    private long _customDueAt;
    private bool _retryCompilation;
    private int _notificationQueued;
    private int _disposed;

    /// <param name="dispatch">An optional queued dispatcher adapter; its caller owns affinity. Production uses Avalonia's UI dispatcher.</param>
    /// <param name="compile">Optional compiler adapter receiving immutable captured inputs, never mutable file paths.</param>
    public UserScripts(IndicatorCatalog catalog, string? folder = null, Action<Action>? dispatch = null,
        Func<IReadOnlyList<ScriptSource>, CancellationToken, ScriptCompileResult>? compile = null)
    {
        _uiDispatcher = dispatch == null;
        if (_uiDispatcher) Dispatcher.UIThread.VerifyAccess();
        _catalog = catalog;
        Folder = Path.GetFullPath(folder ?? AppPaths.UserIndicators);
        _sources = new IndicatorSourceFiles(Folder);
        _dispatch = dispatch ?? (action => Dispatcher.UIThread.Post(action));
        _compileToken = _compileStop.Token;
        _compile = compile ?? ((sources, cancellation) => _compiler.CompileSources(sources, "TradingHubUserIndicators", cancellation));
        Directory.CreateDirectory(Folder);
        WriteExampleIfEmpty();
        if (_uiDispatcher)
        {
            var debounce = _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            debounce.Tick += (_, _) => { debounce.Stop(); if (!IsDisposed) CompileAll(); };
        }
        else _customDebounce = new System.Threading.Timer(_ => CustomDebounceElapsed(), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            // Preserve the reviewed platform policy. The explicit dispatcher adapter uses content scanning for isolated consumers.
            _monitor = OperatingSystem.IsMacOS() || !_uiDispatcher
                ? new ContentScriptDirectoryMonitor(Folder, QueueChange, MonitorUnavailable)
                : new NativeScriptDirectoryMonitor(Folder, QueueChange);
        }
        catch (Exception error)
        {
            MonitorError = error.Message;
            this.LogWarn("Could not watch indicator directory: " + error.Message);
        }
    }

    public string Folder { get; }
    public ScriptCompileResult? LastResult { get; private set; }
    public string? MonitorError { get; private set; }
    public event Action<ScriptCompileResult>? Compiled;
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void MonitorUnavailable(Exception error)
    {
        if (IsDisposed) return;
        MonitorError = error.Message;
        this.LogWarn("Could not read custom indicators: " + error.Message);
    }

    private void QueueChange()
    {
        if (IsDisposed) return;
        MonitorError = null;
        if (!_uiDispatcher)
        {
            if (_monitor?.Version != Volatile.Read(ref _compiledVersion)) ArmCustomDebounce(retry: false);
            return;
        }
        if (Interlocked.Exchange(ref _notificationQueued, 1) != 0) return;
        _dispatch(() =>
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
            if (!IsDisposed && _monitor?.Version != Volatile.Read(ref _compiledVersion)) Restart();
        });
    }

    private void Restart()
    {
        if (IsDisposed) return;
        if (_uiDispatcher) { _debounce!.Stop(); _debounce.Start(); }
        else ArmCustomDebounce(retry: true);
    }

    private void ArmCustomDebounce(bool retry)
    {
        lock (_customDebounceGate)
        {
            if (IsDisposed) return;
            _retryCompilation |= retry;
            _customDueAt = Environment.TickCount64 + 600;
            _customDebounce!.Change(600, Timeout.Infinite);
        }
    }

    private void CustomDebounceElapsed()
    {
        lock (_customDebounceGate)
        {
            if (IsDisposed) return;
            long remaining = _customDueAt - Environment.TickCount64;
            if (remaining > 0) { _customDebounce!.Change((int)remaining, Timeout.Infinite); return; }
            if (Interlocked.Exchange(ref _notificationQueued, 1) != 0) return;
        }
        try
        {
            _dispatch(() =>
            {
                Interlocked.Exchange(ref _notificationQueued, 0);
                if (IsDisposed) return;
                bool retry;
                lock (_customDebounceGate)
                {
                    if (IsDisposed || _customDueAt > Environment.TickCount64) return;
                    retry = _retryCompilation;
                    _retryCompilation = false;
                }
                if (retry || _monitor?.Version != Volatile.Read(ref _compiledVersion)) CompileAll();
            });
        }
        catch { Interlocked.Exchange(ref _notificationQueued, 0); throw; }
    }

    private void StopDebounce()
    {
        if (_uiDispatcher) { _debounce!.Stop(); return; }
        lock (_customDebounceGate)
        {
            if (IsDisposed) return;
            _customDebounce!.Change(Timeout.Infinite, Timeout.Infinite);
            _customDueAt = 0;
            _retryCompilation = false;
        }
    }

    public ScriptCompileResult? CompileAll()
    {
        if (_uiDispatcher) Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed) return LastResult;
        lock (_compileGate)
        {
            if (IsDisposed) return LastResult;
            StopDebounce();
            ScriptCompileResult result;
            ScriptDirectorySnapshot snapshot;
            try
            {
                // Versioned complete scan, not an atomic filesystem snapshot; captured bytes are never reopened by Roslyn.
                snapshot = _monitor?.Capture() ?? new ScriptDirectorySnapshot(0, ScriptDirectoryFiles.Capture(Folder));
                MonitorError = null;
                if (IsDisposed) return LastResult;
                if (snapshot.Sources.Count == 0)
                {
                    lock (_publicationGate)
                    {
                        if (IsDisposed) return LastResult;
                        Volatile.Write(ref _compiledVersion, snapshot.Version);
                        _catalog.RemoveSource(Source);
                        return null; // retain the previous LastResult, matching the reviewed no-source contract
                    }
                }
                result = _compile(snapshot.DecodeSources(), _compileToken);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (!IsDisposed) { MonitorUnavailable(error); Restart(); }
                return LastResult;
            }
            catch (OperationCanceledException) when (IsDisposed) { return LastResult; }

            lock (_publicationGate)
            {
                if (IsDisposed) return LastResult;
                if (_monitor != null && _monitor.Version != snapshot.Version)
                {
                    Restart(); // a later observed version owns the next compilation
                    return LastResult;
                }
                Volatile.Write(ref _compiledVersion, snapshot.Version);
                LastResult = result;
                if (result.Success && result.Assembly != null)
                {
                    _catalog.RemoveSource(Source);
                    if (IsDisposed) return LastResult;
                    _catalog.Scan(result.Assembly, Source);
                    if (IsDisposed) return LastResult;
                    this.LogInfo($"Custom indicators compiled ({snapshot.Sources.Count} files, {result.Elapsed.TotalMilliseconds:0} ms)");
                }
                else
                    foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == "error").Take(20)) this.LogWarn(diagnostic.ToString());
                foreach (var handler in Delegate.EnumerateInvocationList(Compiled))
                {
                    if (IsDisposed) break;
                    handler(result);
                }
                return result;
            }
        }
    }

    private void WriteExampleIfEmpty()
    {
        if (Directory.EnumerateFiles(Folder, "*.cs", SearchOption.AllDirectories).Any()) return;
        _sources.Create("DeltaPercentageExample.cs", ExampleSource);
    }

    public const string ExampleSource = """
        // Custom indicator example. Saving recompiles it automatically.
        // The compatible API accepts indicators written for ATAS.
        using System.ComponentModel;
        using System.ComponentModel.DataAnnotations;
        using System.Drawing;
        using TradingHub.Indicators;

        [DisplayName("Example: delta % per bar")]
        [Category("My indicators")]
        public class DeltaPercentageExample : Indicator
        {
            private readonly ValueDataSeries _series = new("DeltaPct", "Delta %")
            {
                VisualType = VisualMode.Histogram,
            };

            [Display(Name = "Highlight threshold (%)", GroupName = "Settings")]
            public decimal HighlightThreshold { get; set; } = 30;

            public DeltaPercentageExample()
            {
                Panel = IndicatorDataProvider.NewPanel;
                DataSeries[0] = _series;
            }

            protected override void OnCalculate(int bar, decimal value)
            {
                var candle = GetCandle(bar);
                var pct = candle.Volume > 0 ? candle.Delta / candle.Volume * 100 : 0;
                _series[bar] = pct;
                _series.Colors[bar] = Math.Abs(pct) >= HighlightThreshold
                    ? (pct > 0 ? Color.FromArgb(255, 73, 209, 73) : Color.FromArgb(255, 232, 69, 72))
                    : Color.FromArgb(255, 110, 120, 140);
            }
        }
        """;

    /// <summary>Full true-shutdown barrier: reject work now, then await monitor, callbacks and active compilation off the UI thread.</summary>
    public Task StopAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0) _ = CompleteStopAsync();
        return _drained.Task;
    }

    private async Task CompleteStopAsync()
    {
        Exception? failure = null;
        try { _compileStop.Cancel(); }
        catch (Exception error) { failure = error; }
        try
        {
            if (_debounce != null)
            {
                if (Dispatcher.UIThread.CheckAccess()) _debounce.Stop();
                else Dispatcher.UIThread.Post(_debounce.Stop);
            }
        }
        catch (Exception error) { failure = Combine(failure, error); }
        try { if (_monitor != null) await _monitor.StopAsync().ConfigureAwait(false); }
        catch (Exception error) { failure = Combine(failure, error); }
        try
        {
            if (_customDebounce != null)
            {
                ValueTask timerDrain;
                lock (_customDebounceGate) timerDrain = _customDebounce.DisposeAsync();
                await timerDrain.ConfigureAwait(false);
            }
        }
        catch (Exception error) { failure = Combine(failure, error); }
        if (failure == null) _monitorDrained.TrySetResult();
        else _monitorDrained.TrySetException(failure);
        try
        {
            // Unlike synchronous Dispose, the app-owned async barrier also awaits a compiler that ignores cancellation.
            await Task.Run(() => { lock (_compileGate) { } }).ConfigureAwait(false);
        }
        catch (Exception error) { failure = Combine(failure, error); }
        try { _compileStop.Dispose(); }
        catch (Exception error) { failure = Combine(failure, error); }
        if (failure == null) _drained.TrySetResult();
        else _drained.TrySetException(failure);
    }

    private static Exception Combine(Exception? first, Exception second) => first == null ? second : new AggregateException(first, second);
    public ValueTask DisposeAsync() => new(StopAsync());

    public void Dispose()
    {
        bool start;
        lock (_publicationGate) start = Interlocked.CompareExchange(ref _disposed, 1, 0) == 0;
        if (start) _ = CompleteStopAsync();
        // Preserve reviewed off-thread/reentrant Dispose: no UI or active-Roslyn drain is awaited here.
        // True application exit uses StopAsync/DisposeAsync for the complete owned drain.
        _monitorDrained.Task.GetAwaiter().GetResult();
    }
}
