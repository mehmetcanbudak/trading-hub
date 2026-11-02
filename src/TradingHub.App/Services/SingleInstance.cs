using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TradingHub.App.Services;

/// <summary>
/// One Windows process per data directory. A second launch requests the first window and exits.
/// The exclusive file lease also protects aliases and other logon sessions that do not share Local named objects.
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
        MessageBox(IntPtr.Zero, message, "Trading Hub could not start", 0x10);
    });

    internal static bool Claim(string dataRoot, Action<string> reportError)
    {
        if (!OperatingSystem.IsWindows()) return true;
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

            Directory.CreateDirectory(dataRoot);
            lease = new FileStream(Path.Combine(dataRoot, ".tradinghub.instance.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
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
