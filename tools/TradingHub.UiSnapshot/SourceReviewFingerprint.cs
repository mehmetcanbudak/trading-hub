using System.Text.Json;

namespace TradingHub.UiSnapshot;

// Exports the unchanged audit's exact hash only for individually supplied review decisions.
// This does not approve paths, edit the manifest or replace the independent full source gate.
internal static class SourceReviewFingerprint
{
    public static int Run(string sourceRoot, string reviewedPaths, string output)
    {
        string root = Path.GetFullPath(sourceRoot);
        var reviews = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(reviewedPaths))
            ?? throw new InvalidDataException("The per-path source review input is empty.");
        if (reviews.Count == 0) throw new InvalidDataException("No individually reviewed source files were supplied.");
        var fingerprints = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (relative, reason) in reviews)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new InvalidDataException("A reviewed source path requires its own reason: " + relative);
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!relative.StartsWith("src/", StringComparison.Ordinal) || !relative.EndsWith(".cs", StringComparison.Ordinal) ||
                Path.GetRelativePath(root, path).Replace('\\', '/') != relative)
                throw new InvalidDataException("Source review paths must be canonical relative C# paths inside src: " + relative);
            bool template = relative.EndsWith("/StrategyCatalog.cs", StringComparison.Ordinal) ||
                relative.EndsWith("/UserScripts.cs", StringComparison.Ordinal) || relative.EndsWith("/CodeEditorWindow.cs", StringComparison.Ordinal);
            fingerprints.Add(relative, new
            {
                approvedStructure = EnglishLocalizationAudit.Structure(File.ReadAllText(path), template),
                reviewReason = reason,
            });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { sourceRoot = root, fingerprints }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Exported {fingerprints.Count} explicit per-path source fingerprints using the unchanged English audit algorithm.");
        return 0;
    }
}
