using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TradingHub.Common;

/// <summary>Requests supported file/namespace synchronization. Successful OS calls are not a hardware power-loss guarantee.</summary>
public static class StorageSync
{
    private const int Interrupted = 4;
    private const int DarwinFullSync = 51;

    /// <summary>Retains Flush(true) for native files and the supplied flush contract for other streams.
    /// Returns true only when an additional Darwin full-drive-flush request was accepted.</summary>
    public static bool Flush(Stream stream)
    {
        if (stream is not FileStream file)
        {
            stream.Flush();
            return false;
        }
        file.Flush(flushToDisk: true);
        if (!OperatingSystem.IsMacOS()) return false;
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
                // Some filesystems do not implement full-drive flush. The managed disk flush above still completed.
                if (error is 22 or 45 or 78) return false; // EINVAL, ENOTSUP, ENOSYS on Darwin.
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

    private static IOException Failure(string operation, int error) =>
        new($"{operation} failed (OS error {error}).", new Win32Exception(error));

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
