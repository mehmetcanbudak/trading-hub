using TradingHub.Indicators.Native;

namespace TradingHub.Tests;

public class CompletedStudyCacheTests
{
    private static CompletedStudyKey Key(string provider = "A", string distribution = "10:5;20:5", long correction = 0) =>
        new(provider, "TEST", "rows=1;va=70;session=UTC", "fixture", 1, correction, StudyCoverage.Observed, 0, 1, 2, distribution);
    [Fact]
    public void Equal_count_and_total_correction_provider_config_and_fidelity_cannot_share_completed_result()
    {
        using var cache = new CompletedStudyResultCache(maxEntries: 4, maxBytes: 1024);
        var key = Key(); cache.Store(key, new byte[] { 3, 7 });
        Assert.True(cache.TryRead(key, out var original)); Assert.Equal(new byte[] { 3, 7 }, original);
        original[0] = 99; Assert.True(cache.TryRead(key, out var unchanged)); Assert.Equal(3, unchanged[0]);
        Assert.False(cache.TryRead(Key(distribution: "5:5;25:5"), out _));
        Assert.False(cache.TryRead(Key(provider: "B"), out _)); Assert.False(cache.TryRead(Key(correction: 1), out _));
        Assert.False(cache.TryRead(key with { Configuration = "rows=2" }, out _));
        Assert.False(cache.TryRead(key with { Coverage = StudyCoverage.Compact, ResolutionSeconds = 60 }, out _));
    }
    [Fact]
    public void Cache_is_bounded_and_corrupt_disk_payload_is_a_miss_not_a_result()
    {
        string path = Path.Combine(Path.GetTempPath(), "tradinghub-kvp-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var cache = new CompletedStudyResultCache(maxEntries: 2, maxBytes: 32))
            {
                cache.Store(Key(distribution: "1"), new byte[16]); cache.Store(Key(distribution: "2"), new byte[16]); cache.Store(Key(distribution: "3"), new byte[16]);
                Assert.InRange(cache.EntryCount, 0, 2); Assert.InRange(cache.RetainedBytes, 0, 32);
            }
            using (var cache = new CompletedStudyResultCache(path, maxEntries: 2, maxBytes: 32))
                cache.Store(Key(), new byte[16]);
            File.WriteAllText(Assert.Single(Directory.GetFiles(path, "*.studycache")), "{truncated");
            using var reopened = new CompletedStudyResultCache(path, maxEntries: 2, maxBytes: 32);
            Assert.False(reopened.TryRead(Key(), out _)); Assert.NotNull(reopened.LastError);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
    [Theory]
    [InlineData("schema")]
    [InlineData("checksum")]
    [InlineData("identity")]
    [InlineData("oversize")]
    public void Structurally_valid_but_rejected_disk_entries_fall_back_without_throwing(string corruption)
    {
        string path = Path.Combine(Path.GetTempPath(), "tradinghub-completed-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var cache = new CompletedStudyResultCache(path, maxBytes: 32)) cache.Store(Key(), new byte[] { 1, 2 });
            string file = Assert.Single(Directory.GetFiles(path, "*.studycache"));
            if (corruption == "oversize")
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Write); stream.SetLength(65601);
            }
            else
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
                if (corruption == "schema") node["Schema"] = 999;
                else if (corruption == "checksum") node["Digest"] = new string('0', 64);
                else node["Key"] = System.Text.Json.JsonSerializer.SerializeToNode(Key(provider: "other"));
                File.WriteAllText(file, node.ToJsonString());
            }
            using var reopened = new CompletedStudyResultCache(path, maxBytes: 32);
            Assert.False(reopened.TryRead(Key(), out var payload)); Assert.Empty(payload); Assert.NotNull(reopened.LastError);
            reopened.Store(Key(), new byte[] { 3, 4 }); Assert.True(reopened.TryRead(Key(), out var repaired)); Assert.Equal(new byte[] { 3, 4 }, repaired);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
