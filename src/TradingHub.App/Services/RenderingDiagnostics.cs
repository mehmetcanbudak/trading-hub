using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services;

/// <summary>
/// Diagnostic-only rendering controls. TRADINGHUB_RENDERING pins the macOS Avalonia.Native backend for measurement;
/// without it the measured platform default (OpenGl, then Software) is kept.
/// </summary>
internal static class RenderingDiagnostics
{
    public const string RenderingVariable = "TRADINGHUB_RENDERING";

    /// <summary>
    /// Parses a TRADINGHUB_RENDERING value. Empty keeps the platform default (null). A pinned GPU backend keeps Software
    /// as the last resort so an unavailable backend still opens; the frame log reports the backend actually used.
    /// Unknown values are rejected (null with an error message) and the default is kept.
    /// </summary>
    public static IReadOnlyList<AvaloniaNativeRenderingMode>? ParseRenderingMode(string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value)) return null;
        switch (value.Trim().ToLowerInvariant())
        {
            case "metal": return [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software];
            case "opengl": return [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software];
            case "software": return [AvaloniaNativeRenderingMode.Software];
            default:
                error = $"{RenderingVariable}='{value}' ignored: use metal, opengl or software. The default rendering order is kept.";
                return null;
        }
    }

    /// <summary>Avalonia.Native options for the desktop, applying TRADINGHUB_RENDERING on macOS only.</summary>
    public static AvaloniaNativePlatformOptions NativeOptions()
    {
        // In-window menus and drop-downs: as separate windows the first click was sometimes lost.
        var options = new AvaloniaNativePlatformOptions { OverlayPopups = true };
        if (!OperatingSystem.IsMacOS()) return options;
        var modes = ParseRenderingMode(Environment.GetEnvironmentVariable(RenderingVariable), out string? error);
        if (error != null) Log.Write(LogLevel.Warning, nameof(RenderingDiagnostics), error);
        if (modes != null) options.RenderingMode = modes;
        FrameProbe.Current?.Note($"rendering_request={(modes == null ? "default" : string.Join('>', modes))}");
        return options;
    }
}

/// <summary>
/// Opt-in frame-timing probe (TRADINGHUB_FRAME_LOG=&lt;csv path&gt;). Records, per Skia view, the UI-thread picture recording
/// time, the render-thread playback time with the backend actually used (Metal, OpenGL or Software), and one process
/// CPU/RSS sample per second. TRADINGHUB_FRAME_LOG_REDRAW=continuous re-invalidates charts after each frame to measure
/// sustained throughput. Without TRADINGHUB_FRAME_LOG nothing is created and callers skip all work.
/// </summary>
internal sealed class FrameProbe
{
    public const string LogVariable = "TRADINGHUB_FRAME_LOG";
    public const string RedrawVariable = "TRADINGHUB_FRAME_LOG_REDRAW";

    public static FrameProbe? Current { get; } = Create();

    private readonly ConcurrentQueue<string> _pending = new();
    private readonly StreamWriter _writer;
    private readonly Timer _sampler;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly object _flushLock = new();
    private TimeSpan _lastCpu;
    private long _lastSample;

    /// <summary>Re-invalidate charts after each recorded frame (sustained-redraw measurement only).</summary>
    public bool ContinuousRedraw { get; }

    private FrameProbe(string path, bool continuous)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(path, append: false) { AutoFlush = false };
        _writer.WriteLine("kind,t_ms,view,id,width,height,dur_ms,backend,cpu_pct,rss_mb");
        ContinuousRedraw = continuous;
        _lastCpu = _process.TotalProcessorTime;
        _lastSample = _origin;
        Note($"os={Environment.OSVersion.VersionString};cores={Environment.ProcessorCount};continuous={continuous}");
        _sampler = new Timer(_ => Sample(), null, 1000, 1000);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    private static FrameProbe? Create()
    {
        string? path = Environment.GetEnvironmentVariable(LogVariable);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            bool continuous = string.Equals(Environment.GetEnvironmentVariable(RedrawVariable), "continuous", StringComparison.OrdinalIgnoreCase);
            return new FrameProbe(path, continuous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Write(LogLevel.Warning, nameof(FrameProbe), $"{LogVariable} ignored: {ex.Message}");
            return null;
        }
    }

    private double Ms(long timestamp) => Stopwatch.GetElapsedTime(_origin, timestamp).TotalMilliseconds;

    /// <summary>Free-form header/context line.</summary>
    public void Note(string text) => _pending.Enqueue($"note,{Ms(Stopwatch.GetTimestamp()).ToString("F3", CultureInfo.InvariantCulture)},\"{text}\"");

    /// <summary>
    /// Notes activation and screen changes so macOS background throttling and the display (refresh rate, scaling)
    /// a measurement ran on are attributable in the log.
    /// </summary>
    public void Watch(Avalonia.Controls.Window window)
    {
        string screen = "";
        void NoteScreen()
        {
            var s = window.Screens.ScreenFromWindow(window);
            string current = s == null ? "unknown" : $"{s.DisplayName}:{s.Bounds.Width}x{s.Bounds.Height}@{s.Scaling:0.##};window={window.ClientSize.Width:0}x{window.ClientSize.Height:0}";
            if (current == screen) return;
            screen = current;
            Note("screen=" + current);
        }

        window.Opened += (_, _) => NoteScreen();
        window.PositionChanged += (_, _) => NoteScreen();
        window.Resized += (_, _) => NoteScreen();
        window.Activated += (_, _) => Note("window=active");
        window.Deactivated += (_, _) => Note("window=inactive");
    }

    /// <summary>One timed phase ("record" on the UI thread, "draw" on the render thread) of a Skia view.</summary>
    public void Phase(string kind, object view, float width, float height, long start, string backend)
    {
        long end = Stopwatch.GetTimestamp();
        _pending.Enqueue(string.Create(CultureInfo.InvariantCulture,
            $"{kind},{Ms(start):F3},{view.GetType().Name},{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(view)},{width:F0},{height:F0},{Stopwatch.GetElapsedTime(start, end).TotalMilliseconds:F3},{backend},,"));
    }

    private void Sample()
    {
        try
        {
            _process.Refresh();
            long now = Stopwatch.GetTimestamp();
            var cpu = _process.TotalProcessorTime;
            double wall = Stopwatch.GetElapsedTime(_lastSample, now).TotalMilliseconds;
            double pct = wall > 0 ? (cpu - _lastCpu).TotalMilliseconds / wall * 100 : 0;
            _lastCpu = cpu;
            _lastSample = now;
            _pending.Enqueue(string.Create(CultureInfo.InvariantCulture, $"cpu,{Ms(now):F3},,,,,,,{pct:F1},{_process.WorkingSet64 / 1048576.0:F1}"));
        }
        catch (InvalidOperationException)
        {
            // process information unavailable: skip this sample
        }

        Flush();
    }

    private void Flush()
    {
        lock (_flushLock)
        {
            try
            {
                while (_pending.TryDequeue(out string? line)) _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // diagnostics must never disturb the app
            }
        }
    }
}
