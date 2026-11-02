using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel;

namespace TradingHub.App.Services;

/// <summary>
/// Windows Credential Manager stores one generic credential per account, scoped to this user on this
/// computer (not a roaming profile). Credential target names remain stable across interface languages.
/// </summary>
internal sealed class WindowsCredentialStore(string service) : ISecretStore
{
    private const uint CredTypeGeneric = 1;          // CRED_TYPE_GENERIC
    private const uint CredPersistLocalMachine = 2;  // CRED_PERSIST_LOCAL_MACHINE
    private const int ErrorNotFound = 1168;          // ERROR_NOT_FOUND

    /// <summary>CRED_MAX_CREDENTIAL_BLOB_SIZE, in bytes.</summary>
    internal const int MaxBlobBytes = 5 * 512;

    /// <summary>The stable target displayed in Windows Credential Manager.</summary>
    internal static string TargetName(string service, string account) => $"{service}/{account}";

    public string Get(string account)
    {
        if (!CredRead(TargetName(service, account), CredTypeGeneric, 0, out var ptr))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return "";
            throw new Win32Exception(error, "Could not read a secret from Windows Credential Manager.");
        }

        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return "";
            if (cred.CredentialBlobSize > MaxBlobBytes) throw new InvalidDataException("Windows Credential Manager returned an oversized secret.");
            var blob = new byte[cred.CredentialBlobSize];
            try
            {
                Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
                return Decode(blob);
            }
            finally
            {
                Array.Clear(blob);
            }
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string account, string? secret)
    {
        string target = TargetName(service, account);
        if (string.IsNullOrEmpty(secret))
        {
            if (!CredDelete(target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() is var error and not ErrorNotFound)
                throw new Win32Exception(error, "Could not delete a secret from Windows Credential Manager.");
            return;
        }

        if (Encoding.UTF8.GetByteCount(secret) > MaxBlobBytes)
            throw new ArgumentException($"Windows Credential Manager accepts at most {MaxBlobBytes} bytes per secret. The existing secret was not changed.", nameof(secret));
        var blob = Encode(secret);

        IntPtr targetPtr = IntPtr.Zero, userPtr = IntPtr.Zero, blobPtr = IntPtr.Zero;
        try
        {
            targetPtr = Marshal.StringToHGlobalUni(target);
            userPtr = Marshal.StringToHGlobalUni(account);
            blobPtr = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };
            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save a secret in Windows Credential Manager.");
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                Array.Clear(blob);
                Marshal.Copy(blob, 0, blobPtr, blob.Length);
                Marshal.FreeHGlobal(blobPtr);
            }

            if (targetPtr != IntPtr.Zero) Marshal.FreeHGlobal(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeHGlobal(userPtr);
            Array.Clear(blob);
        }
    }

    /// <summary>Trading Hub stores UTF-8, supporting up to 2560 ASCII token characters.</summary>
    internal static byte[] Encode(string secret) => Encoding.UTF8.GetBytes(secret);

    /// <summary>
    /// Read Trading Hub's UTF-8 or an ASCII password saved as UTF-16 by Windows credential tools.
    /// </summary>
    internal static string Decode(byte[] blob)
    {
        bool utf16 = blob.Length >= 2 && blob.Length % 2 == 0;
        for (int i = 1; utf16 && i < blob.Length; i += 2) utf16 = blob[i] == 0 && blob[i - 1] != 0;
        return utf16 ? Encoding.Unicode.GetString(blob) : Encoding.UTF8.GetString(blob);
    }

    // ===================================================================== advapi32

    /// <summary>CREDENTIALW: identical x64/ARM64 layout (80 bytes); x86 uses 52 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
