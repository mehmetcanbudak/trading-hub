using System.Collections.Concurrent;
using TradingHub.App.Services;

namespace TradingHub.Tests;

/// <summary>Editable copies of built-ins: untouched copies follow the original, edited ones are only flagged.</summary>
public sealed class IndicatorSourceCopiesTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("source-copies-").FullName;
    private string Root => Path.Combine(_base, "Indicators");

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private static IndicatorSources Original(int factor, string helperExtra = "", string unrelated = "") => new(new[]
    {
        new IndicatorSourceDocument("Original.cs", """
            using TradingHub.Indicators;
            namespace BuiltIn;
            public class Original : Indicator
            {
                public int Output => Helpers.Multiply(3);
                protected override void OnCalculate(int bar, decimal value) { }
            }
            """, "Fixture author"),
        new IndicatorSourceDocument("Helpers.cs",
            $"namespace BuiltIn;\ninternal static class Helpers {{ public static int Multiply(int value) => value * {factor}{helperExtra}; }}\n", "Fixture author"),
        new IndicatorSourceDocument("Other.cs",
            $"using TradingHub.Indicators;\nnamespace BuiltIn;\npublic class Other : Indicator {{ protected override void OnCalculate(int bar, decimal value) {{ }} {unrelated} }}\n", "Fixture author"),
    });

    private static SourceCopyStatus Single(IReadOnlyList<SourceCopyStatus> statuses) => Assert.Single(statuses);

    private string[] SiblingsOfRoot() => Directory.EnumerateFileSystemEntries(_base).Select(Path.GetFileName).Where(n => n != "Indicators").ToArray()!;

    [Fact]
    public void Copy_lifecycle_refreshes_untouched_copies_and_never_overwrites_edits()
    {
        var copy = Original(2).Fork("BuiltIn.Original", "OriginalEdited", Root);
        string directory = Path.GetDirectoryName(copy.EntryPath)!;
        string helper = Directory.EnumerateFiles(directory, "*Helpers.cs").Single();
        var manifest = IndicatorSourceCopies.ReadManifest(directory)!;
        Assert.Equal("BuiltIn.Original", manifest.OriginalType);
        Assert.Equal(copy.TypeName, manifest.CopyType);
        Assert.Equal(new[] { "Helpers.cs", "Original.cs" }, manifest.Originals.Keys.Order()); // only the dependency closure

        // unchanged original, and a change outside the copy's closure
        Assert.Equal(SourceCopyState.Current, Single(IndicatorSourceCopies.Review(Root, _ => Original(2))).State);
        Assert.Equal(SourceCopyState.Current, Single(IndicatorSourceCopies.Review(Root, _ => Original(2, unrelated: "public int Extra => 1;"))).State);

        // untouched copy, updated original: regenerated under the same type identity
        var refreshed = Single(IndicatorSourceCopies.Review(Root, _ => Original(5)));
        Assert.Equal(SourceCopyState.Refreshed, refreshed.State);
        Assert.Equal(copy.TypeName, refreshed.CopyType);
        Assert.Contains("value * 5", File.ReadAllText(helper));
        Assert.Empty(SiblingsOfRoot()); // no staging, parking or backup left behind
        Assert.Equal(SourceCopyState.Current, Single(IndicatorSourceCopies.Review(Root, _ => Original(5))).State);

        // the user edits the copy; the original changes again: flagged, edit kept
        File.AppendAllText(helper, "// my change\n");
        var flagged = Single(IndicatorSourceCopies.Review(Root, _ => Original(7)));
        Assert.Equal(SourceCopyState.OriginalUpdated, flagged.State);
        Assert.Contains("value * 5", File.ReadAllText(helper));
        Assert.EndsWith("// my change\n", File.ReadAllText(helper));

        // keep edits: no longer flagged for this original, still treated as edited for the next one
        IndicatorSourceCopies.KeepEdits(directory, _ => Original(7));
        Assert.Equal(SourceCopyState.Current, Single(IndicatorSourceCopies.Review(Root, _ => Original(7))).State);
        Assert.Equal(SourceCopyState.OriginalUpdated, Single(IndicatorSourceCopies.Review(Root, _ => Original(9))).State);
        Assert.EndsWith("// my change\n", File.ReadAllText(helper));

        // explicit restore: new original under the same type, previous edits preserved outside the compiled folder
        string backup = IndicatorSourceCopies.RestoreOriginal(Root, directory, _ => Original(9));
        Assert.StartsWith(Path.Combine(_base, IndicatorSourceCopies.BackupFolderName), backup);
        Assert.EndsWith("// my change\n", File.ReadAllText(Directory.EnumerateFiles(backup, "*Helpers.cs").Single()));
        Assert.Contains("value * 9", File.ReadAllText(helper));
        Assert.Equal(copy.TypeName, IndicatorSourceCopies.ReadManifest(directory)!.CopyType);
        Assert.Equal(SourceCopyState.Current, Single(IndicatorSourceCopies.Review(Root, _ => Original(9))).State);
    }

    [Fact]
    public void Added_files_count_as_edits_and_missing_originals_leave_the_copy_alone()
    {
        var copy = Original(2).Fork("BuiltIn.Original", "OriginalEdited", Root);
        string directory = Path.GetDirectoryName(copy.EntryPath)!;
        var before = Directory.EnumerateFiles(directory).ToDictionary(f => f, File.ReadAllText);
        Assert.True(IndicatorSourceCopies.IsUntouched(directory, IndicatorSourceCopies.ReadManifest(directory)!));
        File.WriteAllText(Path.Combine(directory, "Notes.cs"), "// my helper file\n");
        Assert.False(IndicatorSourceCopies.IsUntouched(directory, IndicatorSourceCopies.ReadManifest(directory)!));
        Assert.Equal(SourceCopyState.OriginalUpdated, Single(IndicatorSourceCopies.Review(Root, _ => Original(3))).State);
        File.Delete(Path.Combine(directory, "Notes.cs"));

        Assert.Equal(SourceCopyState.OriginalUnavailable, Single(IndicatorSourceCopies.Review(Root, _ => null)).State);
        Assert.Equal(SourceCopyState.OriginalUnavailable, Single(IndicatorSourceCopies.Review(Root, _ => new IndicatorSources(Array.Empty<IndicatorSourceDocument>()))).State);
        Assert.Equal(before, Directory.EnumerateFiles(directory).ToDictionary(f => f, File.ReadAllText));
    }

    [Fact]
    public void An_untouched_copy_whose_new_original_cannot_compile_is_flagged_not_replaced()
    {
        var copy = Original(2).Fork("BuiltIn.Original", "OriginalEdited", Root);
        string directory = Path.GetDirectoryName(copy.EntryPath)!;
        var before = Directory.EnumerateFiles(directory).ToDictionary(f => f, File.ReadAllText);
        var status = Single(IndicatorSourceCopies.Review(Root, _ => Original(2, " + MissingType.Value")));
        Assert.Equal(SourceCopyState.OriginalUpdated, status.State);
        Assert.Contains("cannot be copied safely", status.Detail);
        Assert.Equal(before, Directory.EnumerateFiles(directory).ToDictionary(f => f, File.ReadAllText));
        Assert.Empty(SiblingsOfRoot());
    }

    [Fact]
    public void Built_in_copies_record_their_assembly_and_resolve_from_loaded_indicators()
    {
        var type = typeof(TradingHub.Indicators.Native.MovingAverage);
        var copy = IndicatorSources.ForAssembly(type.Assembly).Fork(type.FullName!, "MovingAverageEdited", Root);
        var manifest = IndicatorSourceCopies.ReadManifest(Path.GetDirectoryName(copy.EntryPath)!)!;
        Assert.Equal(type.Assembly.GetName().Name, manifest.OriginalAssembly);
        Assert.Equal(SourceCopyState.Current, Single(IndicatorSourceCopies.Review(Root)).State);
    }
}

/// <summary>The example indicator is offered once per indicator folder.</summary>
public sealed class IndicatorExampleSeedingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("indicator-example-").FullName;
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Start()
    {
        using var scripts = new UserScripts(new IndicatorCatalog(), _root, _queue.Enqueue, (_, _) => new TradingHub.Scripting.ScriptCompileResult());
    }

    private string Example => Path.Combine(_root, "DeltaPercentageExample.cs");

    [Fact]
    public void Deleting_the_example_keeps_it_deleted()
    {
        Start();
        Assert.Equal(UserScripts.ExampleSource, File.ReadAllText(Example));
        Assert.True(File.Exists(Path.Combine(_root, UserScripts.ExampleMarker)));
        File.Delete(Example);
        Start();
        Assert.False(File.Exists(Example));
    }

    [Fact]
    public void An_existing_script_or_source_suppresses_the_example_and_is_never_replaced()
    {
        string script = Path.Combine(_root, "Mine.kvs");
        File.WriteAllText(script, "output x = line");
        Start();
        Assert.False(File.Exists(Example));
        Assert.True(File.Exists(Path.Combine(_root, UserScripts.ExampleMarker)));
        File.Delete(script);
        Start();
        Assert.False(File.Exists(Example));
    }

    [Fact]
    public void A_user_file_named_like_the_example_is_untouched()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        File.WriteAllText(Path.Combine(_root, "nested", "DeltaPercentageExample.cs"), "// mine");
        Start();
        Assert.Equal("// mine", File.ReadAllText(Path.Combine(_root, "nested", "DeltaPercentageExample.cs")));
        Assert.False(File.Exists(Example));
    }
}
