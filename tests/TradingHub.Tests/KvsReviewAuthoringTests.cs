using TradingHub.App.Services;

namespace TradingHub.Tests;

/// <summary>
/// Authoring review regressions: editable-copy reviews, the compiled source set of a folder below bin/obj, case-only
/// renames and KVS scripts that would compile to one indicator type.
/// </summary>
public sealed class KvsReviewAuthoringTests : IDisposable
{
    private const string Script = "output x = line\nonBar {\n  x = close\n}\n";
    private readonly string _base = Directory.CreateTempSubdirectory("kvs-review-authoring-").FullName;
    private string Root => Path.Combine(_base, "Indicators");

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private static IndicatorSources Original(int factor = 2, string helperExtra = "") => new(new[]
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
            $"namespace BuiltIn;\ninternal static class Helpers {{ public static int Multiply(int value) => value * {factor}; }}\n{helperExtra}", "Fixture author"),
    });

    [Fact]
    public void An_edited_copy_is_not_flagged_when_the_updated_original_regenerates_the_same_copy()
    {
        var copy = Original().Fork("BuiltIn.Original", "OriginalEdited", Root);
        string helper = Directory.EnumerateFiles(Path.GetDirectoryName(copy.EntryPath)!, "*Helpers.cs").Single();
        File.AppendAllText(helper, "// my change\n");

        // a declaration outside the copy's closure joins a document the copy comes from: the copy text is unchanged
        const string unrelated = "internal static class Unused { }\n";
        Assert.Equal(SourceCopyState.Current, Assert.Single(IndicatorSourceCopies.Review(Root, _ => Original(helperExtra: unrelated))).State);
        Assert.EndsWith("// my change\n", File.ReadAllText(helper));

        // still an edited copy: a real change of the original is flagged, never applied
        Assert.Equal(SourceCopyState.OriginalUpdated, Assert.Single(IndicatorSourceCopies.Review(Root, _ => Original(3, unrelated))).State);
        Assert.EndsWith("// my change\n", File.ReadAllText(helper));
    }

    [Fact]
    public void A_source_folder_below_a_bin_or_obj_directory_compiles_but_its_own_build_output_does_not()
    {
        string root = Path.Combine(_base, "obj", "bin", "Indicators");
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "A.cs"), "");
        File.WriteAllText(Path.Combine(root, "sub", "B.kvs"), "");
        File.WriteAllText(Path.Combine(root, "obj", "Generated.cs"), "");
        Assert.Equal(new[] { Path.Combine(root, "A.cs"), Path.Combine(root, "sub", "B.kvs") },
            ScriptDirectoryFiles.Enumerate(root).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_rename_that_only_changes_letter_case_works_and_a_real_collision_is_refused()
    {
        var files = new IndicatorSourceFiles(Root);
        string path = files.Create("Foo.cs", "// foo\n");
        string renamed = files.Rename(path, "foo", "// foo\n");
        Assert.Equal(Path.Combine(Root, "foo.cs"), renamed);
        Assert.Equal(new[] { "foo.cs" }, Directory.EnumerateFiles(Root).Select(Path.GetFileName));
        Assert.Equal("// foo\n", File.ReadAllText(renamed));

        files.Create("Bar.cs", "// bar\n");
        Assert.Throws<IOException>(() => files.Rename(renamed, "BAR", "// foo\n"));
        Assert.Equal("// bar\n", File.ReadAllText(Path.Combine(Root, "Bar.cs")));
    }

    [Fact]
    public void Creating_or_renaming_a_script_onto_another_scripts_indicator_type_is_refused()
    {
        var files = new IndicatorSourceFiles(Root);
        string sub = Path.Combine(Root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "my-ind.kvs"), Script);
        Assert.Throws<IOException>(() => files.Create("my_ind.kvs", Script)); // both are Kvs_my_ind
        Assert.False(File.Exists(Path.Combine(Root, "my_ind.kvs")));

        string other = files.Create("Other.kvs", Script);
        Assert.Throws<IOException>(() => files.Rename(other, "my ind", Script));
        Assert.Equal(Path.Combine(Root, "Other2.kvs"), files.Rename(other, "Other2", Script));
    }
}
