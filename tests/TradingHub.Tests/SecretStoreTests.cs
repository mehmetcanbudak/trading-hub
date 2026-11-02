using System.Runtime.InteropServices;
using System.Text;
using TradingHub.App.Services;

namespace TradingHub.Tests;

/// <summary>Credential encoding/layout and isolated fake storage; native writes require explicit opt-in.</summary>
public class SecretStoreTests
{
    [Fact]
    public void Credential_HasTheWindowsLayout()
    {
        // CREDENTIALW from wincred.h: 80 bytes on x64/ARM64 and 52 bytes on x86.
        bool x64 = IntPtr.Size == 8;
        Assert.Equal(x64 ? 80 : 52, Marshal.SizeOf<WindowsCredentialStore.Credential>());
        Assert.Equal(x64 ? 24 : 16, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.LastWritten)));
        Assert.Equal(x64 ? 32 : 24, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.CredentialBlobSize)));
        Assert.Equal(x64 ? 40 : 28, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.CredentialBlob)));
        Assert.Equal(x64 ? 48 : 32, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.Persist)));
        Assert.Equal(x64 ? 72 : 48, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.UserName)));
    }

    [Fact]
    public void Credential_KeepsTheSecretAsUtf8AndUnderstandsOnesSavedByHand()
    {
        foreach (var secret in new[] { "a", "abc123", "password ñ € 中文", new string('x', WindowsCredentialStore.MaxBlobBytes) })
            Assert.Equal(secret, WindowsCredentialStore.Decode(WindowsCredentialStore.Encode(secret)));
        // Windows credential tools store ASCII passwords as UTF-16.
        Assert.Equal("hunter2", WindowsCredentialStore.Decode(Encoding.Unicode.GetBytes("hunter2")));
        Assert.Equal("Trading Hub/ab12cd34:secret", WindowsCredentialStore.TargetName("Trading Hub", "ab12cd34:secret"));
    }

    [Fact]
    public void FileSecretStore_SavesReadsAndDeletes()
    {
        var path = Path.Combine(Path.GetTempPath(), "tradinghub-secrets-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FileSecretStore(path);
            Assert.Equal("", store.Get("x:secret"));
            store.Set("x:secret", "s3cr3t");
            store.Set("x:token", "tok");
            Assert.Equal("s3cr3t", new FileSecretStore(path).Get("x:secret"));
            store.Set("x:secret", null);
            Assert.Equal("", store.Get("x:secret"));
            Assert.Equal("tok", store.Get("x:token"));
            store.Set("x:token", "");
            Assert.Equal("", store.Get("x:token"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [WindowsFact]
    public void CredentialManager_AMissingCredentialIsEmpty()
    {
        // Only query/delete a unique absent test entry; no user's credentials are touched.
        var store = new WindowsCredentialStore("Trading Hub (test)");
        var account = Guid.NewGuid().ToString("N") + ":secret";
        Assert.Equal("", store.Get(account));
        store.Set(account, null);
    }

    [WindowsOptInFact("TRADINGHUB_TEST_CREDMAN")]
    public void CredentialManager_SavesReadsAndDeletes()
    {
        var store = new WindowsCredentialStore("Trading Hub (test)");
        var account = Guid.NewGuid().ToString("N") + ":secret";
        try
        {
            store.Set(account, "password ñ € 中文");
            Assert.Equal("password ñ € 中文", store.Get(account));
            Assert.Throws<ArgumentException>(() => store.Set(account, new string('x', WindowsCredentialStore.MaxBlobBytes + 1)));
            Assert.Equal("password ñ € 中文", store.Get(account));
            store.Set(account, "replacement");
            Assert.Equal("replacement", store.Get(account));
        }
        finally
        {
            store.Set(account, null);
        }

        Assert.Equal("", store.Get(account));
    }

    [Fact]
    public void CredentialStore_RejectsOversizedUtf8BeforeCallingWindows()
    {
        var store = new WindowsCredentialStore("Trading Hub (test)");
        Assert.Throws<ArgumentException>(() => store.Set("unused:secret", new string('x', WindowsCredentialStore.MaxBlobBytes + 1)));
        Assert.Throws<ArgumentException>(() => store.Set("unused:secret", new string('中', WindowsCredentialStore.MaxBlobBytes / 3 + 1)));
    }

    [Fact]
    public void FakeSecretFiles_IsolateTheSameAccountWithoutUsingPlatformCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradinghub-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = new FileSecretStore(Path.Combine(root, "first.json"));
            var second = new FileSecretStore(Path.Combine(root, "second.json"));
            first.Set("account:secret", "first value");
            second.Set("account:secret", "second value");
            first.Set("account:secret", null);
            Assert.Equal("", first.Get("account:secret"));
            Assert.Equal("second value", second.Get("account:secret"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
