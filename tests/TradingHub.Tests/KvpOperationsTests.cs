using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TradingHub.App.Services;
using TradingHub.Common;
using TradingHub.Market;
using TradingHub.Networking;

namespace TradingHub.Tests;

public sealed class KvpOperationsTests
{
    [Fact]
    public async Task RelocationPublishesAllVerifiedRootsTogetherAndRetainsEveryOriginal()
    {
        using var fixture = new OperationsStorageFixture();
        var store = fixture.Store();
        var before = store.Resolve();
        var selection = fixture.NewSelection();
        var preview = await store.PreviewAsync(selection, CancellationToken.None);
        Assert.True(preview.HasChanges);
        await store.StageAsync(preview, CancellationToken.None);
        Assert.Equal(before, store.Resolve());
        Assert.False(Directory.Exists(selection.AppRoot));
        var result = await store.ApplyPendingAsync(CancellationToken.None);
        Assert.True(result.Success, result.Message);
        var reopened = fixture.Store().Resolve();
        Assert.Equal(selection.AppRoot, reopened.AppRoot);
        Assert.Equal(selection.MarketRoot, reopened.MarketRoot);
        Assert.Equal(selection.ScriptsRoot, reopened.ScriptsRoot);
        fixture.AssertOriginals();
        Assert.Equal("saved settings", File.ReadAllText(Path.Combine(reopened.AppRoot, "settings.fixture")));
        Assert.Equal(new byte[] { 4, 2, 9, 0, 7 }, File.ReadAllBytes(Path.Combine(reopened.MarketRoot, "Ticks", "raw.fixture")));
        Assert.Equal("public class SavedStrategy {}", File.ReadAllText(Path.Combine(reopened.StrategiesRoot, "SavedStrategy.cs")));
        Assert.True(Directory.Exists(Path.Combine(reopened.IndicatorsRoot, "EmptyFolder")));
        Assert.False(store.HasPending);
        Assert.Equal(fixture.AppRoot, store.BootstrapRoot);
    }

    [Fact]
    public async Task AnUnavailablePublishedRootFailsClosedInsteadOfOpeningAnEmptyOrStaleReplacement()
    {
        using var fixture = new OperationsStorageFixture();
        var store = fixture.Store();
        var selection = fixture.NewSelection();
        await store.StageAsync(await store.PreviewAsync(selection, CancellationToken.None), CancellationToken.None);
        Assert.True((await store.ApplyPendingAsync(CancellationToken.None)).Success);
        Directory.Move(selection.AppRoot!, fixture.PathFor("temporarily-unavailable-app"));
        Assert.Throws<IOException>(() => fixture.Store().Resolve());
        Assert.Equal(selection, fixture.Store().Selection);
        Assert.False(Directory.Exists(selection.AppRoot));
        Assert.True(File.Exists(Path.Combine(fixture.PathFor("temporarily-unavailable-app"), "settings.fixture")));
        fixture.AssertOriginals();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelocationPublishesCanonicalAuthorityDespiteAliasRetargeting(bool retargetDuringCopy)
    {
        using var fixture = new OperationsStorageFixture();
        var firstParent = fixture.PathFor("verified-parent");
        var secondParent = fixture.PathFor("unverified-parent");
        Directory.CreateDirectory(firstParent);
        Directory.CreateDirectory(secondParent);
        var alias = fixture.PathFor("selected-parent-alias");
        CreateDirectoryAlias(alias, firstParent);
        bool retargeted = false;
        var store = fixture.Store(async (source, target, ct) =>
        {
            await CopyAsync(source, target, ct);
            if (retargetDuringCopy && !retargeted)
            {
                retargeted = true;
                Directory.Delete(alias);
                CreateDirectoryAlias(alias, secondParent);
            }
        });
        var selection = new StorageLocationSelection(AppRoot: Path.Combine(alias, "app"));
        await store.StageAsync(await store.PreviewAsync(selection, CancellationToken.None), CancellationToken.None);
        var result = await store.ApplyPendingAsync(CancellationToken.None);
        Assert.True(result.Success, result.Message);
        var verifiedRoot = Path.Combine(firstParent, "app");
        Assert.Equal(verifiedRoot, store.Selection.AppRoot);
        Assert.Equal(verifiedRoot, store.Resolve().AppRoot);
        Directory.CreateDirectory(Path.Combine(secondParent, "app"));
        File.WriteAllText(Path.Combine(secondParent, "app", "settings.fixture"), "unverified destination data");
        // A later retarget of the originally approved alias must not change the published authority either.
        Directory.Delete(alias);
        CreateDirectoryAlias(alias, secondParent);
        Assert.Equal(verifiedRoot, fixture.Store().Resolve().AppRoot);
        Assert.Equal("saved settings", File.ReadAllText(Path.Combine(store.Resolve().AppRoot, "settings.fixture")));
        Assert.Equal(new byte[] { 4, 2, 9, 0, 7 }, File.ReadAllBytes(Path.Combine(store.Resolve().MarketRoot, "Ticks", "raw.fixture")));
        Assert.Equal("unverified destination data", File.ReadAllText(Path.Combine(secondParent, "app", "settings.fixture")));
        fixture.AssertOriginals();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACaseVariantAuthorityIsCopiedOrRejectedRatherThanSkippedWhenAnotherRootChanges(bool existingDestination)
    {
        using var fixture = new OperationsStorageFixture();
        var store = fixture.Store();
        var original = store.Resolve();
        var caseVariant = fixture.PathFor("ORIGINAL-APP");
        bool distinctOnThisVolume = !Directory.Exists(caseVariant);
        if (distinctOnThisVolume && existingDestination)
        {
            Directory.CreateDirectory(caseVariant);
            File.WriteAllText(Path.Combine(caseVariant, "settings.fixture"), "unverified case-variant data");
        }
        var selection = new StorageLocationSelection(AppRoot: caseVariant, ScriptsRoot: fixture.PathFor("case-variant-scripts"));
        if (!distinctOnThisVolume || existingDestination)
        {
            await Assert.ThrowsAsync<IOException>(() => store.PreviewAsync(selection, CancellationToken.None));
            Assert.Equal(original, store.Resolve());
            if (distinctOnThisVolume)
                Assert.Equal("unverified case-variant data", File.ReadAllText(Path.Combine(caseVariant, "settings.fixture")));
        }
        else
        {
            var preview = await store.PreviewAsync(selection, CancellationToken.None);
            await store.StageAsync(preview, CancellationToken.None);
            var result = await store.ApplyPendingAsync(CancellationToken.None);
            Assert.True(result.Success, result.Message);
            Assert.Equal(caseVariant, store.Resolve().AppRoot);
            Assert.Equal("saved settings", File.ReadAllText(Path.Combine(caseVariant, "settings.fixture")));
            Assert.Equal(new byte[] { 4, 2, 9, 0, 7 }, File.ReadAllBytes(Path.Combine(caseVariant, "Ticks", "raw.fixture")));
        }
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task AResolvedAuthorityChangeRejectsPublicationEvenWhenCopiedSourceBytesAreUnchanged()
    {
        using var fixture = new OperationsStorageFixture();
        string? changedLiveAuthority = null;
        var store = new StorageLocationStore(fixture.AppRoot, fixture.ScriptsRoot,
            name => name == "TRADINGHUB_LIVE_DIR" ? changedLiveAuthority : null,
            async (source, target, ct) =>
            {
                await CopyAsync(source, target, ct);
                changedLiveAuthority = fixture.PathFor("different-live-authority");
            });
        var originalSelection = store.Selection;
        await store.StageAsync(await store.PreviewAsync(fixture.NewSelection(), CancellationToken.None), CancellationToken.None);
        var result = await store.ApplyPendingAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Equal(originalSelection, fixture.Store().Selection);
        Assert.Equal(fixture.AppRoot, store.Resolve().AppRoot);
        Assert.Equal(changedLiveAuthority, store.Resolve().LiveRoot);
        fixture.AssertOriginals();
    }

    [Theory]
    [InlineData("partial-copy")]
    [InlineData("hash-mismatch")]
    [InlineData("permission")]
    public async Task CopyOrHashFailureNeverPublishesOrRemovesOriginalFiles(string failure)
    {
        using var fixture = new OperationsStorageFixture();
        int copies = 0;
        var store = fixture.Store(async (source, target, ct) =>
        {
            if (failure == "permission") throw new UnauthorizedAccessException("fixture denied write");
            await CopyAsync(source, target, ct);
            if (failure == "hash-mismatch")
            {
                var bytes = await File.ReadAllBytesAsync(target, ct);
                bytes[0] ^= 0x7f;
                await File.WriteAllBytesAsync(target, bytes, ct);
            }
            if (failure == "partial-copy" && ++copies == 2) throw new IOException("fixture interrupted copy");
        });
        var before = store.Resolve();
        await store.StageAsync(await store.PreviewAsync(fixture.NewSelection(), CancellationToken.None), CancellationToken.None);
        var result = await store.ApplyPendingAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Equal(before, fixture.Store().Resolve());
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task CancellingAnActiveCopyDrainsWithoutPublishingAndCancelsThePendingApproval()
    {
        using var fixture = new OperationsStorageFixture();
        using var cancellation = new CancellationTokenSource();
        var store = fixture.Store(async (source, target, ct) =>
        {
            await CopyAsync(source, target, ct);
            cancellation.Cancel();
        });
        var original = store.Resolve();
        await store.StageAsync(await store.PreviewAsync(fixture.NewSelection(), CancellationToken.None), CancellationToken.None);
        var result = await store.ApplyPendingAsync(cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Equal(original, store.Resolve());
        Assert.False(store.HasPending);
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task FailureAtTheAtomicPointerWriteLeavesCopiedTargetsInactiveAndOldDataAuthoritative()
    {
        using var fixture = new OperationsStorageFixture();
        var store = fixture.Store();
        var original = store.Resolve();
        var selection = fixture.NewSelection();
        await store.StageAsync(await store.PreviewAsync(selection, CancellationToken.None), CancellationToken.None);
        // Portable publication-denied fixture: a directory cannot be atomically replaced by a file.
        Directory.CreateDirectory(Path.Combine(fixture.AppRoot, "storage-locations.json"));
        var result = await store.ApplyPendingAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(original, store.Resolve());
        Assert.True(File.Exists(Path.Combine(selection.AppRoot!, "settings.fixture")));
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task AChangedSourceDuringCopyIsRejectedRatherThanPublishingAnOutdatedSnapshot()
    {
        using var fixture = new OperationsStorageFixture();
        bool changed = false;
        var store = fixture.Store(async (source, target, ct) =>
        {
            await CopyAsync(source, target, ct);
            if (!changed)
            {
                changed = true;
                await File.WriteAllTextAsync(Path.Combine(fixture.AppRoot, "late.fixture"), "latest original data", ct);
            }
        });
        var original = store.Resolve();
        await store.StageAsync(await store.PreviewAsync(fixture.NewSelection(), CancellationToken.None), CancellationToken.None);
        var result = await store.ApplyPendingAsync(CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(original, store.Resolve());
        Assert.Equal("latest original data", File.ReadAllText(Path.Combine(fixture.AppRoot, "late.fixture")));
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task AllSixExplicitFixtureOverridesWinAndNoEffectiveRelocationCanBeStaged()
    {
        using var fixture = new OperationsStorageFixture();
        var environment = new Dictionary<string, string>
        {
            ["TRADINGHUB_DATA_DIR"] = fixture.PathFor("override-data"),
            ["TRADINGHUB_INDICATORS_DIR"] = fixture.PathFor("override-indicators"),
            ["TRADINGHUB_STRATEGIES_DIR"] = fixture.PathFor("override-strategies"),
            ["TRADINGHUB_BACKTESTS_DIR"] = fixture.PathFor("override-backtests"),
            ["TRADINGHUB_LIVE_DIR"] = fixture.PathFor("override-live"),
            ["TRADINGHUB_KEYCHAIN_FILE"] = fixture.PathFor("override-secrets.json"),
        };
        var store = StorageLocations.CreateStore(name => environment.GetValueOrDefault(name));
        var effective = store.Resolve(fixture.NewSelection());
        Assert.Equal(environment["TRADINGHUB_DATA_DIR"], effective.AppRoot);
        Assert.Equal(effective.AppRoot, effective.MarketRoot);
        Assert.Equal(effective.AppRoot, effective.ScriptsRoot);
        Assert.Equal(environment["TRADINGHUB_INDICATORS_DIR"], effective.IndicatorsRoot);
        Assert.Equal(environment["TRADINGHUB_STRATEGIES_DIR"], effective.StrategiesRoot);
        Assert.Equal(environment["TRADINGHUB_BACKTESTS_DIR"], effective.BacktestsRoot);
        Assert.Equal(environment["TRADINGHUB_LIVE_DIR"], effective.LiveRoot);
        Assert.Equal(environment["TRADINGHUB_KEYCHAIN_FILE"], effective.KeychainFile);
        var preview = await store.PreviewAsync(fixture.NewSelection(), CancellationToken.None);
        Assert.False(preview.HasChanges);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StageAsync(preview, CancellationToken.None));
        fixture.AssertOriginals();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedScriptsAuthorityReopensWithoutAcquiringOrNormalizingUnusedDocuments(bool relativeDefaultPath)
    {
        using var fixture = new OperationsStorageFixture();
        var owner = fixture.Store();
        var selection = new StorageLocationSelection(ScriptsRoot: fixture.PathFor("selected-scripts"));
        await owner.StageAsync(await owner.PreviewAsync(selection, CancellationToken.None), CancellationToken.None);
        var publication = await owner.ApplyPendingAsync(CancellationToken.None);
        Assert.True(publication.Success, publication.Message);

        Func<string> documents = relativeDefaultPath
            ? () => "relative-documents"
            : () => throw new IOException();
        var reopened = StorageLocations.CreateStore(_ => null, () => fixture.AppRoot, documents);
        Assert.Equal(selection, reopened.Selection);
        var effective = reopened.Resolve();
        Assert.Equal(fixture.AppRoot, effective.AppRoot);
        Assert.Equal(selection.ScriptsRoot, effective.ScriptsRoot);
        Assert.Equal("public class SavedStrategy {}", File.ReadAllText(Path.Combine(effective.StrategiesRoot, "SavedStrategy.cs")));
        Assert.True(Directory.Exists(Path.Combine(effective.IndicatorsRoot, "EmptyFolder")));
        Assert.False(reopened.HasPending);
        fixture.AssertOriginals();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredDocumentsDefaultFailureRemainsVisibleWithoutChangingOriginalAuthority(bool relativeDefaultPath)
    {
        using var fixture = new OperationsStorageFixture();
        Func<string> documents = relativeDefaultPath
            ? () => "relative-documents"
            : () => throw new IOException();
        var store = StorageLocations.CreateStore(_ => null, () => fixture.AppRoot, documents);
        var error = Record.Exception(() => store.Resolve());
        Assert.NotNull(error);
        Assert.Equal(new StorageLocationSelection(), store.Selection);
        Assert.False(store.HasPending);
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task MarketOnlyRelocationDoesNotCopyAccountSettingsAndRetainsRawHistory()
    {
        using var fixture = new OperationsStorageFixture();
        var store = fixture.Store();
        var selection = new StorageLocationSelection(MarketRoot: fixture.PathFor("new-market-only"));
        await store.StageAsync(await store.PreviewAsync(selection, CancellationToken.None), CancellationToken.None);
        Assert.True((await store.ApplyPendingAsync(CancellationToken.None)).Success);
        Assert.Equal(fixture.AppRoot, store.Resolve().AppRoot);
        Assert.False(File.Exists(Path.Combine(selection.MarketRoot!, "settings.fixture")));
        Assert.Equal(new byte[] { 4, 2, 9, 0, 7 }, File.ReadAllBytes(Path.Combine(selection.MarketRoot!, "Ticks", "raw.fixture")));
        fixture.AssertOriginals();
    }

    [Fact]
    public async Task OfflineCatalogPreservesProviderIdentityAgeMetadataAndExplicitCancellation()
    {
        using var fixture = new OperationsStorageFixture();
        var now = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var catalog = new InstrumentCatalog(fixture.PathFor("catalog"), () => now);
        var instrument = new Instrument { FeedId = "fixture-feed", Symbol = "SAME", Exchange = "Fixture venue A", TickSize = 0.25m, PointValue = 50m };
        using var feed = new SearchZoomFeed(instrument);
        var first = await catalog.LoadAsync("profile", "provider-A", feed, CancellationToken.None);
        Assert.False(first.IsOffline);
        now = now.AddDays(3);
        feed.Response = _ => throw new IOException("fixture offline");
        var offline = await catalog.LoadAsync("profile", "provider-A", feed, CancellationToken.None);
        Assert.True(offline.IsOffline);
        Assert.True(offline.IsStale);
        Assert.Equal(first.CapturedUtc, offline.CapturedUtc);
        Assert.Equal(TimeSpan.FromDays(3), now - offline.CapturedUtc);
        Assert.Equal("profile", offline.ProfileId);
        Assert.Equal("provider-A", offline.ProviderIdentity);
        Assert.Equal(feed.DisplayName, offline.ProviderLabel);
        Assert.Equal(0.25m, Assert.Single(offline.Instruments).TickSize);
        Assert.Null(catalog.Read("profile", "provider-B"));
        int requests = feed.Requests;
        var diskOnly = await catalog.LoadAsync("profile", "provider-A", feed, CancellationToken.None, allowNetwork: false);
        Assert.Equal(requests, feed.Requests);
        Assert.True(diskOnly.IsOffline);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.LoadAsync("profile", "provider-A", feed, cancelled.Token));
        Assert.NotNull(catalog.Read("profile", "provider-A"));
    }

    [Fact]
    public async Task ExplicitProxyRoutesOnlyItsClientToALoopbackFixtureAndLeavesGlobalDefaultsUnchanged()
    {
        var defaultProxy = HttpClient.DefaultProxy;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var received = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string? firstLine = await reader.ReadLineAsync(timeout.Token);
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nfixture"), timeout.Token);
                return firstLine;
            }, timeout.Token);
            var factory = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = $"http://127.0.0.1:{port}" });
            using var client = factory.CreateHttpClient(TimeSpan.FromSeconds(5));
            string result = await client.GetStringAsync("http://market.invalid/symbols", timeout.Token);
            Assert.Equal("fixture", result);
            Assert.Equal("GET http://market.invalid/symbols HTTP/1.1", await received);
            Assert.Same(defaultProxy, HttpClient.DefaultProxy);
            using var direct = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Direct }).CreateHandler();
            Assert.False(direct.UseProxy);
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("https://127.0.0.1:8443/")]
    [InlineData("socks5://127.0.0.1:1080")]
    public void ExplicitProxySchemesConstructOwnedHandlersWithoutSendingARequest(string address)
    {
        var factory = new ProxyHandlerFactory(new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = address });
        using var handler = factory.CreateHandler();
        Assert.Equal(new Uri(address), handler.Proxy!.GetProxy(new Uri("https://market.invalid/symbols")));
    }

    [Fact]
    public void ProxySecretsStayPrivateAndAddressEmbeddedCredentialsAreRejectedBeforePersistence()
    {
        using var fixture = new OperationsStorageFixture();
        var settings = new AppSettings();
        var secrets = new FileSecretStore(fixture.PathFor("private-fixture-secrets.json"));
        int saves = 0;
        var proxy = new AppNetworkProxy(settings, secrets, () => saves++);
        var policy = new NetworkProxyPolicy { Mode = NetworkProxyMode.Custom, Address = "http://127.0.0.1:8123" };
        proxy.Save(policy, "private-user", "private-password", replaceCredentials: true);
        string ordinarySettings = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("private-user", ordinarySettings);
        Assert.DoesNotContain("private-password", ordinarySettings);
        using var handler = proxy.CreateFactory().CreateHandler();
        var credential = handler.Proxy!.Credentials!.GetCredential(new Uri(policy.Address), "Basic");
        Assert.Equal("private-user", credential?.UserName);
        Assert.Equal("private-password", credential?.Password);
        Assert.Throws<ArgumentException>(() => proxy.Save(policy with { Address = "http://secret:password@127.0.0.1:8123" }, "", "", true));
        Assert.Equal(1, saves);
        proxy.ClearCredentials();
        using var cleared = proxy.CreateFactory().CreateHandler();
        Assert.Null(cleared.Proxy!.Credentials);
    }

    private static void CreateDirectoryAlias(string alias, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(alias, target);
            return;
        }
        // A fixture-owned directory junction does not require enabling developer mode or administrator access.
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", alias, target }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("The fixture junction process could not start.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("The fixture junction process did not finish.");
        }
        if (process.ExitCode != 0) throw new IOException("The fixture junction could not be created: " + process.StandardError.ReadToEnd());
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, ct);
    }
}

/// <summary>Real isolated roots shared by storage regressions and actual Settings smoke controls.</summary>
public sealed class OperationsStorageFixture : IDisposable
{
    private readonly string _root;
    public string AppRoot { get; }
    public string ScriptsRoot { get; }
    public OperationsStorageFixture()
    {
        _root = Directory.CreateTempSubdirectory("tradinghub-operations-").FullName;
        // Resolve the temporary directory's OS aliases using the public resolver, not a production root.
        var canonical = new StorageLocationStore(_root, _root).Resolve().AppRoot;
        _root = canonical;
        AppRoot = PathFor("original-app");
        ScriptsRoot = PathFor("original-scripts");
        Directory.CreateDirectory(Path.Combine(AppRoot, "Ticks"));
        Directory.CreateDirectory(Path.Combine(ScriptsRoot, "Estrategias"));
        Directory.CreateDirectory(Path.Combine(ScriptsRoot, "Indicadores", "EmptyFolder"));
        File.WriteAllText(Path.Combine(AppRoot, "settings.fixture"), "saved settings");
        File.WriteAllBytes(Path.Combine(AppRoot, "Ticks", "raw.fixture"), new byte[] { 4, 2, 9, 0, 7 });
        File.WriteAllText(Path.Combine(ScriptsRoot, "Estrategias", "SavedStrategy.cs"), "public class SavedStrategy {}");
    }
    public string PathFor(string name) => Path.Combine(_root, name);
    public StorageLocationSelection NewSelection() => new(PathFor("new-app"), PathFor("new-market"), PathFor("new-scripts"));
    public StorageLocationStore Store(Func<string, string, CancellationToken, Task>? copy = null) => new(AppRoot, ScriptsRoot, copyFile: copy);
    public void AssertOriginals()
    {
        Assert.Equal("saved settings", File.ReadAllText(Path.Combine(AppRoot, "settings.fixture")));
        Assert.Equal(new byte[] { 4, 2, 9, 0, 7 }, File.ReadAllBytes(Path.Combine(AppRoot, "Ticks", "raw.fixture")));
        Assert.Equal("public class SavedStrategy {}", File.ReadAllText(Path.Combine(ScriptsRoot, "Estrategias", "SavedStrategy.cs")));
        Assert.True(Directory.Exists(Path.Combine(ScriptsRoot, "Indicadores", "EmptyFolder")));
    }
    public void Dispose()
    {
        RemoveDirectoryLinks(new DirectoryInfo(_root));
        Directory.Delete(_root, recursive: true);
    }

    // Windows recursive delete treats a junction as a volume mount point and fails with ERROR_INVALID_PARAMETER.
    // Removing each link entry first leaves its target untouched on every platform.
    private static void RemoveDirectoryLinks(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateDirectories())
        {
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint)) child.Delete();
            else RemoveDirectoryLinks(child);
        }
    }
}
