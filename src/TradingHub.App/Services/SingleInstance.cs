using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TradingHub.App.Services;

/// <summary>
/// One writer per data directory. Windows also requests activation of the first window.
/// The exclusive file lease protects storage relocation and aliases on both desktop platforms.
/// macOS retains its application-activation behavior.
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _showRequest;
    private static FileStream? _fileLease;
    private static string? _claimedName;

    internal static string NameFor(string dataRoot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).TrimEnd('\\', '/').ToUpperInvariant()));
        return @"Local\TradingHub-" + Convert.ToHexString(hash, 0, 8);
    }

    public static bool Claim(string dataRoot) => Claim(dataRoot, message =>
    {
        CrashLog.Write(new IOException(message));
        ReportStartupError(message);
    });

    internal static void ReportStartupError(string message)
    {
        Console.Error.WriteLine(message);
        if (OperatingSystem.IsWindows()) MessageBox(IntPtr.Zero, message, "Trading Hub could not start", 0x10);
        else if (OperatingSystem.IsMacOS())
        {
            try
            {
                string escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace('\r', ' ').Replace('\n', ' ');
                var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
                start.ArgumentList.Add("-e");
                start.ArgumentList.Add("display alert \"Trading Hub could not start\" message \"" + escaped + "\" as critical");
                using var alert = System.Diagnostics.Process.Start(start);
                alert?.WaitForExit();
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); }
        }
    }

    internal static bool Claim(string dataRoot, Action<string> reportError)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (_fileLease != null)
            {
                if (_claimedName == NameFor(dataRoot)) return true;
                reportError("This process already owns a different data directory.");
                return false;
            }
            _fileLease = TryAcquireDataLease(dataRoot);
            if (_fileLease == null)
            {
                reportError("Trading Hub could not establish exclusive access to its data directory. Close the other instance or check directory permissions before retrying.");
                return false;
            }
            _claimedName = NameFor(dataRoot);
            return true;
        }
        Mutex? mutex = null;
        EventWaitHandle? request = null;
        FileStream? lease = null;
        bool ownsMutex = false;
        try
        {
            string name = NameFor(dataRoot);
            if (_mutex != null || _fileLease != null)
            {
                if (_claimedName == name) return true;
                throw new InvalidOperationException("This process already owns a different data directory.");
            }

            request = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-mostrar");
            mutex = new Mutex(true, name, out ownsMutex);
            if (!ownsMutex)
            {
                AllowSetForegroundWindow(AsfwAny);
                request.Set();
                return false;
            }

            lease = TryAcquireDataLease(dataRoot) ?? throw new IOException("The data directory is already in use or unavailable.");
            _mutex = mutex;
            _showRequest = request;
            _fileLease = lease;
            _claimedName = name;
            mutex = null;
            request = null;
            lease = null;
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                reportError("Trading Hub could not establish exclusive access to its data directory. " +
                    "Another instance may be using it, or Windows may have denied access. No data was opened. " +
                    "Close any other instance and check the directory permissions before retrying.\n\n" + ex.Message);
            }
            catch (Exception reportException)
            {
                CrashLog.Write(reportException);
            }

            return false;
        }
        finally
        {
            lease?.Dispose();
            request?.Dispose();
            if (ownsMutex && mutex != null) mutex.ReleaseMutex();
            mutex?.Dispose();
        }
    }

    /// <summary>Holds the same writer lease during pre-start relocation; never opens application data.</summary>
    internal static FileStream? TryAcquireDataLease(string dataRoot)
    {
        try
        {
            Directory.CreateDirectory(dataRoot);
            return new FileStream(Path.Combine(dataRoot, ".tradinghub.instance.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The callback runs on a background thread; callers dispatch window activation to the UI thread.</summary>
    public static void OnShowRequested(Action show)
    {
        if (_showRequest is not { } request) return;
        new Thread(() =>
        {
            while (request.WaitOne())
            {
                try
                {
                    show();
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex);
                }
            }
        }) { IsBackground = true, Name = "Trading Hub: show existing instance" }.Start();
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
