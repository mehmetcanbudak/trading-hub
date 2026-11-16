using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TradingHub.Common;

/// <summary>Requests supported file/namespace synchronization. Successful OS calls are not a hardware power-loss guarantee.</summary>
public static class StorageSync
{
    private const int Interrupted = 4;
    private const int DarwinFullSync = 51;

    /// <summary>Persists a native file with exactly one drive-flush request: Darwin F_FULLFSYNC (falling back to fsync
    /// where the filesystem lacks it) or Flush(true) elsewhere; other streams use their own flush contract.
    /// Returns true only when a Darwin full-drive-flush request was accepted.</summary>
    /// <remarks>.NET 10's Flush(true) itself issues F_FULLFSYNC on macOS, so calling it before an explicit F_FULLFSYNC
    /// paid for two drive cache flushes. The explicit request keeps the guarantee independent of runtime internals.</remarks>
    public static bool Flush(Stream stream)
    {
        if (stream is not FileStream file)
        {
            stream.Flush();
            return false;
        }
        if (!OperatingSystem.IsMacOS())
        {
            file.Flush(flushToDisk: true);
            return false;
        }
        file.Flush(flushToDisk: false); // hand buffered bytes to the OS before the drive flush
        bool retained = false;
        var handle = file.SafeFileHandle;
        try
        {
            handle.DangerousAddRef(ref retained);
            int fd = handle.DangerousGetHandle().ToInt32();
            while (DarwinFcntl(fd, DarwinFullSync) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == Interrupted) continue;
                if (error is 22 or 45 or 78) // EINVAL, ENOTSUP, ENOSYS: no full-drive flush on this filesystem.
                {
                    file.Flush(flushToDisk: true); // fsync; its errors remain visible
                    return false;
                }
                throw Failure("Full file synchronization", error);
            }
            return true;
        }
        finally
        {
            if (retained) handle.DangerousRelease();
        }
    }

    /// <summary>Synchronizes an existing directory's namespace on macOS/Linux. Returns false on platforms without
    /// this implementation; file flushes remain required there. Errors on supported platforms remain visible.</summary>
    public static bool SyncDirectory(string directory)
    {
        bool mac = OperatingSystem.IsMacOS();
        if (!mac && !OperatingSystem.IsLinux()) return false;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        int fd;
        while ((fd = mac ? DarwinOpen(directory, 0) : UnixOpen(directory, 0)) < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == Interrupted) continue;
            throw Failure("Opening directory for synchronization", error);
        }
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        while ((mac ? DarwinFsync(fd) : UnixFsync(fd)) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == Interrupted) continue;
            throw Failure("Directory synchronization", error);
        }
        return true;
    }

    internal const int MoveFileReplaceExisting = 0x1, MoveFileWriteThrough = 0x8;

    /// <summary>Same-volume rename. On Windows it uses MoveFileEx with MOVEFILE_WRITE_THROUGH so the rename is flushed
    /// before returning (Windows has no directory-handle synchronization); elsewhere <see cref="File.Move(string, string, bool)"/>,
    /// followed by the caller's <see cref="SyncDirectory"/>. A read-only destination is never replaced: Windows refuses
    /// it (error 5, <see cref="UnauthorizedAccessException"/>), but a POSIX rename only needs write access to the
    /// directory and would silently publish over a file the user or another program made read-only, so that case is
    /// refused the same way here, as the direct writes these renames replaced were.</summary>
    public static void Rename(string source, string destination, bool overwrite) =>
        Rename(source, destination, overwrite, OperatingSystem.IsWindows(), WindowsMove);

    /// <summary>Platform dispatch with an injectable native mover returning a Win32 error code (0 on success).</summary>
    internal static void Rename(string source, string destination, bool overwrite, bool windows, Func<string, string, int, int> windowsMove)
    {
        if (!windows)
        {
            if (overwrite && File.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReadOnly))
                throw new UnauthorizedAccessException($"Rename refused: '{destination}' is read-only.");
            File.Move(source, destination, overwrite);
            return;
        }
        int flags = MoveFileWriteThrough | (overwrite ? MoveFileReplaceExisting : 0);
        int error = windowsMove(WindowsPath(source), WindowsPath(destination), flags);
        if (error == 0) return;
        string message = $"Write-through rename to '{destination}' failed (OS error {error}).";
        throw error switch
        {
            2 => new FileNotFoundException(message, source),
            3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message, new Win32Exception(error)),
            _ => new IOException(message, new Win32Exception(error)),
        };
    }

    // MoveFileExW accepts paths beyond MAX_PATH only with the extended-length prefix.
    internal static string WindowsPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Length < 260 || full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static int WindowsMove(string source, string destination, int flags) =>
        MoveFileEx(source, destination, flags) ? 0 : Marshal.GetLastPInvokeError();

    private static IOException Failure(string operation, int error) =>
        new($"{operation} failed (OS error {error}).", new Win32Exception(error));

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int DarwinFcntl(int descriptor, int command);
    [DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)]
    private static extern int DarwinOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libSystem.B.dylib", EntryPoint = "fsync", SetLastError = true)]
    private static extern int DarwinFsync(int descriptor);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int descriptor);
}
