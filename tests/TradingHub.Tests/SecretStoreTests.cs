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

    [Fact]
    public async Task FileSecretStore_AsyncWritesAreSerializedWithoutLosingOtherAccounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradinghub-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "secrets.json");
        try
        {
            await using var store = new FileSecretStore(path);
            await Task.WhenAll(Enumerable.Range(0, 32).Select(index => store.SetAsync("account:" + index, "value:" + index)));
            await using var reopened = new FileSecretStore(path);
            for (int index = 0; index < 32; index++)
                Assert.Equal("value:" + index, await reopened.GetAsync("account:" + index));
            await Task.WhenAll(Enumerable.Range(0, 16).Select(index => store.SetAsync("account:" + index, null)));
            for (int index = 0; index < 32; index++)
                Assert.Equal(index < 16 ? "" : "value:" + index, await reopened.GetAsync("account:" + index));
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("not-json fake-private-value")]
    [InlineData("null")]
    [InlineData("{\"account:secret\":null,\"other\":\"fake-private-value\"}")]
    [InlineData("{\"account:secret\":[\"fake-private-value\"]}")]
    public async Task FileSecretStore_MalformedStateIsNotAbsenceAndCannotBeOverwritten(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), "tradinghub-secrets-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, contents);
        try
        {
            await using var store = new FileSecretStore(path);
            var read = await Assert.ThrowsAsync<IOException>(() => store.GetAsync("account:secret"));
            var save = await Assert.ThrowsAsync<IOException>(() => store.SetAsync("account:secret", "replacement"));
            var delete = await Assert.ThrowsAsync<IOException>(() => store.SetAsync("account:secret", null));
            foreach (var error in new[] { read, save, delete })
            {
                Assert.DoesNotContain("fake-private-value", error.ToString());
                Assert.DoesNotContain(path, error.ToString());
                Assert.Null(error.InnerException);
            }
            Assert.Equal(contents, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FileSecretStore_IoFailuresRemainVisibleWithoutExposingPrivatePathsOrValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradinghub-fake-private-value-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var unreadable = new FileSecretStore(root);
            var read = await Assert.ThrowsAsync<IOException>(() => unreadable.GetAsync("account:secret"));
            await using var unwritable = new FileSecretStore(Path.Combine(root, "missing-parent", "secrets.json"));
            var save = await Assert.ThrowsAsync<IOException>(() => unwritable.SetAsync("account:secret", "fake-private-value"));
            foreach (var error in new[] { read, save })
            {
                Assert.DoesNotContain("fake-private-value", error.ToString());
                Assert.DoesNotContain(root, error.ToString());
                Assert.Null(error.InnerException);
            }
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FileSecretStore_CanceledAndClosedOperationsCannotChangeSavedCredentials()
    {
        var path = Path.Combine(Path.GetTempPath(), "tradinghub-secrets-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FileSecretStore(path);
            await store.SetAsync("account:secret", "saved");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetAsync("account:secret", "replacement", cancel.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync("account:secret", cancel.Token));
            Assert.Equal("saved", await store.GetAsync("account:secret"));
            await store.DisposeAsync();
            await store.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetAsync("account:secret"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SetAsync("account:secret", null));
            await using var reopened = new FileSecretStore(path);
            Assert.Equal("saved", await reopened.GetAsync("account:secret"));
        }
        finally { File.Delete(path); }
    }
}
