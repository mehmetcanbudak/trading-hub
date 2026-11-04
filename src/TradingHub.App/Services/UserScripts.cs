using Avalonia.Threading;
using TradingHub.Common.Logging;
using TradingHub.Scripting;

namespace TradingHub.App.Services;

/// <summary>
/// Indicadores propios del usuario: compila los .cs de ~/Documents/Trading Hub/Indicadores al arrancar y
/// cada vez que cambian (recarga en caliente), y los suma al catálogo.
/// </summary>
public sealed class UserScripts : IDisposable
{
    public const string Source = "Mis indicadores";
    private readonly IndicatorCatalog _catalog;
    private readonly ScriptCompiler _compiler = new();
    private readonly IScriptDirectoryMonitor? _monitor;
    private readonly DispatcherTimer _debounce;
    private readonly object _publicationGate = new();
    private long _compiledVersion = -1;
    private int _notificationQueued;
    private int _disposed;

    public UserScripts(IndicatorCatalog catalog)
    {
        Dispatcher.UIThread.VerifyAccess();
        _catalog = catalog;
        Folder = AppPaths.UserIndicators;
        Directory.CreateDirectory(Folder);
        WriteExampleIfEmpty();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (!IsDisposed) CompileAll();
        };

        try
        {
            _monitor = OperatingSystem.IsMacOS()
                ? new ContentScriptDirectoryMonitor(Folder, QueueChange,
                    ex => this.LogWarn("Could not read custom indicators: " + ex.Message))
                : new NativeScriptDirectoryMonitor(Folder, QueueChange);
        }
        catch (Exception ex)
        {
            this.LogWarn("Could not watch indicator directory: " + ex.Message);
        }
    }

    public string Folder { get; }
    public ScriptCompileResult? LastResult { get; private set; }

    /// <summary>Se dispara después de cada compilación (con éxito o con errores).</summary>
    public event Action<ScriptCompileResult>? Compiled;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void QueueChange()
    {
        if (IsDisposed || Interlocked.Exchange(ref _notificationQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _notificationQueued, 0);
            if (!IsDisposed && _monitor?.Version != _compiledVersion) Restart();
        });
    }

    private void Restart()
    {
        if (IsDisposed) return;
        _debounce.Stop();
        _debounce.Start();
    }

    public ScriptCompileResult? CompileAll()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (IsDisposed) return LastResult;
        _debounce.Stop();
        ScriptCompileResult res;
        int fileCount;
        long version;
        try
        {
            // These owned bytes remain stable even if a live path changes or vanishes during compilation.
            // This is a versioned scan, not an atomic filesystem snapshot.
            var snapshot = _monitor?.Capture() ?? new ScriptDirectorySnapshot(0, ScriptDirectoryFiles.Capture(Folder));
            version = snapshot.Version;
            fileCount = snapshot.Sources.Count;
            if (IsDisposed) return LastResult;
            if (fileCount == 0)
            {
                lock (_publicationGate)
                {
                    if (IsDisposed) return LastResult;
                    _compiledVersion = version;
                    _catalog.RemoveSource(Source);
                    return null;
                }
            }
            res = _compiler.CompileSources(snapshot.DecodeSources(), "TradingHubUserIndicators");
            if (_monitor != null && _monitor.Version != version)
            {
                // A later observed version owns the next compilation; unchanged polls reuse captured inputs.
                Restart();
                return LastResult;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A partial save/unreadable tree must retain the last-good catalog and retry without another edit.
            if (!IsDisposed)
            {
                this.LogWarn("Could not read custom indicators: " + ex.Message);
                Restart();
            }
            return LastResult;
        }
        catch (OperationCanceledException) when (IsDisposed) { return LastResult; }

        lock (_publicationGate)
        {
            // Disposal can reject an active compiler's result without waiting for the UI or Roslyn.
            if (IsDisposed) return LastResult;
            _compiledVersion = version;
            LastResult = res;
            if (res.Success && res.Assembly != null)
            {
                _catalog.RemoveSource(Source);
                if (IsDisposed) return LastResult; // catalog subscribers can dispose reentrantly
                _catalog.Scan(res.Assembly, Source);
                if (IsDisposed) return LastResult;
                this.LogInfo($"Custom indicators compiled ({fileCount} files, {res.Elapsed.TotalMilliseconds:0} ms)");
            }
            else
            {
                foreach (var d in res.Diagnostics.Where(d => d.Severity == "error").Take(20)) this.LogWarn(d.ToString());
            }

            foreach (var handler in Delegate.EnumerateInvocationList(Compiled))
            {
                if (IsDisposed) break;
                handler(res);
            }
            return res;
        }
    }

    private void WriteExampleIfEmpty()
    {
        if (Directory.EnumerateFiles(Folder, "*.cs", SearchOption.AllDirectories).Any()) return;
        File.WriteAllText(Path.Combine(Folder, "DeltaPercentageExample.cs"), ExampleSource);
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

    public void Dispose()
    {
        lock (_publicationGate) Interlocked.Exchange(ref _disposed, 1);
        if (Dispatcher.UIThread.CheckAccess()) _debounce.Stop();
        else Dispatcher.UIThread.Post(_debounce.Stop);
        // No publication/scan lock is held while draining; the worker never awaits UI work.
        _monitor?.Dispose();
    }
}
