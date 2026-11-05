using System.Runtime.Loader;
using TradingHub.App.Services;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Scripting;

namespace TradingHub.Tests;

public sealed class IndicatorAuthoringTests
{
    [Fact]
    public void ExactMetadataTypeMappingIgnoresCommentsFilenamesAndSameNamedTypes()
    {
        var sources = new IndicatorSources(new[]
        {
            new IndicatorSourceDocument("Misleading.cs", "namespace First; /* class Signal {} */ public class Signal { }", "First author"),
            new IndicatorSourceDocument("ActualMultiClass.cs", "namespace Second; public class Other { } public class Signal { }", "Second author"),
        });
        Assert.Equal("ActualMultiClass.cs", Assert.Single(sources.View("Second.Signal")).Id);
        Assert.Equal("Misleading.cs", Assert.Single(sources.View("First.Signal")).Id);
        Assert.Throws<NotSupportedException>(() => sources.View("Signal"));
    }

    [Fact]
    public void CompletePartialMultiClassClosureCopiesOverloadedConstructorsSelfReferencesAndHelpersWithoutChangingOriginals()
    {
        using var fixture = new SourceFixture();
        var sources = ClosureSources();
        string oldFile = fixture.Files.Create("Existing.cs", "namespace Existing; public static class SavedIdentity { public const string Id = \"keep-me\"; }");
        byte[] original = File.ReadAllBytes(oldFile);
        var copy = sources.Fork("BuiltIn.Original", "EditableSignal", fixture.Root);
        Assert.Equal(4, copy.Files.Count);
        Assert.Equal(original, File.ReadAllBytes(oldFile));
        Assert.Equal("Original.cs", sources.View("BuiltIn.Original")[0].Id);
        var result = new ScriptCompiler().Compile(fixture.Files.Paths(), "AuthoringClosure");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        try
        {
            var type = result.Assembly!.GetType(copy.TypeName, throwOnError: true)!;
            using var indicator = (Indicator)Activator.CreateInstance(type)!;
            Assert.Equal(17, type.GetProperty("Output")!.GetValue(indicator));
            Assert.Equal("Original", type.GetProperty("UnchangedLiteral")!.GetValue(null));
            Assert.Equal(type, type.GetProperty("SelfType")!.GetValue(null));
            using var alternate = (Indicator)type.GetMethod("Create")!.Invoke(null, new object[] { 2 })!;
            Assert.Equal(13, type.GetProperty("Output")!.GetValue(alternate));
            var catalog = new IndicatorCatalog();
            catalog.Scan(result.Assembly, UserScripts.Source);
            Assert.Equal(copy.TypeName, Assert.Single(catalog.Items).Type.FullName);
            Assert.All(copy.Files, path => Assert.Contains("Copyright Fixture Author; retain this license notice.", File.ReadAllText(path)));
            string entryText = fixture.Files.Read(copy.EntryPath);
            string renamed = fixture.Files.Rename(copy.EntryPath, "RenamedSourceOnly", entryText);
            Assert.Equal(entryText, fixture.Files.Read(renamed));
            Assert.Contains(renamed, IndicatorSources.UserFiles(type, fixture.Root));
            var afterRename = new ScriptCompiler().Compile(fixture.Files.Paths(), "RenamedAuthoring");
            Assert.True(afterRename.Success, string.Join("\n", afterRename.Diagnostics));
            Assert.NotNull(afterRename.Assembly!.GetType(copy.TypeName));
            AssemblyLoadContext.GetLoadContext(afterRename.Assembly)!.Unload();
        }
        finally { if (result.Assembly != null) AssemblyLoadContext.GetLoadContext(result.Assembly)!.Unload(); }
    }

    [Fact]
    public void RepeatedCopyNamesAreIndependentTypesAndNeverOverwriteExistingUserSources()
    {
        using var fixture = new SourceFixture();
        string existing = fixture.Files.Create("EditableSignal.cs", "public class AlreadyHere { }");
        byte[] before = File.ReadAllBytes(existing);
        var sources = ClosureSources();
        var first = sources.Fork("BuiltIn.Original", "EditableSignal", fixture.Root);
        var second = sources.Fork("BuiltIn.Original", "EditableSignal", fixture.Root);
        Assert.NotEqual(first.TypeName, second.TypeName);
        Assert.NotEqual(Path.GetDirectoryName(first.EntryPath), Path.GetDirectoryName(second.EntryPath));
        Assert.Equal(before, File.ReadAllBytes(existing));
        var compiled = new ScriptCompiler().Compile(fixture.Files.Paths(), "RepeatedCopyNames");
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        var catalog = new IndicatorCatalog();
        catalog.Scan(compiled.Assembly!, UserScripts.Source);
        Assert.Equal(2, catalog.Items.Count);
        AssemblyLoadContext.GetLoadContext(compiled.Assembly!)!.Unload();
    }

    [Fact]
    public void UnsupportedMissingAndExtensionDependenciesRejectBeforePublishingAnyCopy()
    {
        using var fixture = new SourceFixture();
        string preserved = fixture.Files.Create("Keep.cs", "public class Keep { }");
        var missing = new IndicatorSources(new[] { new IndicatorSourceDocument("Missing.cs", """
            using TradingHub.Indicators;
            namespace Unsupported;
            public class Missing : Indicator { public Missing() { MissingHelper.Run(); } protected override void OnCalculate(int bar, decimal value) {} }
            """, "Author") });
        Assert.Throws<NotSupportedException>(() => missing.Fork("Unsupported.Missing", "MyMissing", fixture.Root));
        var extension = new IndicatorSources(new[] { new IndicatorSourceDocument("Extension.cs", """
            using TradingHub.Indicators;
            namespace Unsupported;
            public class Extended : Indicator { public Extended() { var x = 1.Twice(); } protected override void OnCalculate(int bar, decimal value) {} }
            internal static class Extensions { public static int Twice(this int value) => value * 2; }
            """, "Author") });
        Assert.Throws<NotSupportedException>(() => extension.Fork("Unsupported.Extended", "MyExtended", fixture.Root));
        Assert.Equal(new[] { preserved }, fixture.Files.Paths());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root));
    }

    [Fact]
    public void NativeResourceMappingAndForkUseTheActualRuntimeCompilerWithoutRunningIndicatorCode()
    {
        using var fixture = new SourceFixture();
        var builtIn = typeof(MovingAverage);
        var source = IndicatorSources.ForAssembly(builtIn.Assembly);
        Assert.NotEmpty(source.View(builtIn.FullName!));
        var copy = source.Fork(builtIn.FullName!, "PersonalAverage", fixture.Root);
        var compiled = new ScriptCompiler().Compile(fixture.Files.Paths(), "NativeSourceCopy");
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        Assert.NotNull(compiled.Assembly!.GetType(copy.TypeName));
        Assert.Equal("TradingHub.Indicators.Native.MovingAverage", builtIn.FullName);
        AssemblyLoadContext.GetLoadContext(compiled.Assembly)!.Unload();
    }

    [Fact]
    public void NestedExternalGenericArgumentsStillBindToTheCopiedTypeAndAttributeNoticesRemain()
    {
        using var fixture = new SourceFixture();
        var sources = new IndicatorSources(new[] { new IndicatorSourceDocument("Generic.cs", """
            using System;
            using System.Collections.Generic;
            using System.ComponentModel;
            using TradingHub.Indicators;
            namespace GenericFixture;
            // Copyright Attribute Author. This notice is attached to the display attribute.
            [DisplayName("Original")]
            public class Probe : Indicator
            {
                public Type Binding => typeof(Holder<List<Probe>>);
                protected override void OnCalculate(int bar, decimal value) { }
            }
            internal class Holder<T> { }
            """, "Fixture provenance") });
        var copy = sources.Fork("GenericFixture.Probe", "CopiedGeneric", fixture.Root);
        var compiled = new ScriptCompiler().Compile(copy.Files, "GenericClosure");
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        try
        {
            var type = compiled.Assembly!.GetType(copy.TypeName)!;
            using var instance = (Indicator)Activator.CreateInstance(type)!;
            var binding = (Type)type.GetProperty("Binding")!.GetValue(instance)!;
            Assert.Equal(type, binding.GenericTypeArguments[0].GenericTypeArguments[0]);
            Assert.Contains("// Copyright Attribute Author.", File.ReadAllText(copy.EntryPath));
        }
        finally { AssemblyLoadContext.GetLoadContext(compiled.Assembly!)!.Unload(); }
    }

    [Fact]
    public void CopyPreflightNeverRunsSourceStaticOrInstanceConstructors()
    {
        using var fixture = new SourceFixture();
        string marker = Path.Combine(fixture.Root, "must-not-execute");
        string literal = System.Text.Json.JsonSerializer.Serialize(marker);
        var sources = new IndicatorSources(new[] { new IndicatorSourceDocument("Constructor.cs", $$"""
            using TradingHub.Indicators;
            namespace ConstructorFixture;
            public class ConstructorProbe : Indicator
            {
                static ConstructorProbe() { System.IO.File.WriteAllText({{literal}}, "static constructor ran"); }
                public ConstructorProbe() { System.IO.File.WriteAllText({{literal}}, "instance constructor ran"); }
                protected override void OnCalculate(int bar, decimal value) { }
            }
            """, "Fixture author") });
        var copy = sources.Fork("ConstructorFixture.ConstructorProbe", "MyConstructorProbe", fixture.Root);
        Assert.True(File.Exists(copy.EntryPath));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void FileRenameKeepsTypeIdentityAndExternalChangesAndCaseCollisionsRemainUntouched()
    {
        using var fixture = new SourceFixture();
        string path = fixture.Files.Create("Source.cs", "public class PersistedIndicatorIdentity { }");
        string expected = fixture.Files.Read(path);
        fixture.Files.Create("Occupied.cs", "public class Occupied { }");
        Assert.Throws<IOException>(() => fixture.Files.Rename(path, "occupied", expected));
        Assert.Throws<ArgumentException>(() => fixture.Files.Rename(path, "../escape", expected));
        Assert.Throws<IOException>(() => fixture.Files.Create("source", "replacement"));
        File.WriteAllText(path, "public class NewerUserEdit { }");
        Assert.Throws<IOException>(() => fixture.Files.Save(path, expected, "stale editor"));
        Assert.Throws<IOException>(() => fixture.Files.Delete(path, expected));
        Assert.Equal("public class NewerUserEdit { }", fixture.Files.Read(path));
        string renamed = fixture.Files.Rename(path, "Renamed", fixture.Files.Read(path));
        Assert.Equal("public class NewerUserEdit { }", fixture.Files.Read(renamed));
        fixture.Files.Delete(renamed, fixture.Files.Read(renamed));
        Assert.False(File.Exists(renamed));
        Assert.Equal("public class Occupied { }", fixture.Files.Read(Path.Combine(fixture.Root, "Occupied.cs")));
    }

    private static IndicatorSources ClosureSources() => new(new[]
    {
        new IndicatorSourceDocument("Original.cs", """
            // Copyright Fixture Author; retain this license notice.
            using System;
            using System.ComponentModel;
            using TradingHub.Indicators;
            namespace BuiltIn;
            [DisplayName("Original built-in")]
            public partial class Original : Indicator
            {
                public int Output { get; }
                public Original() : this(3) { }
                public Original(int seed) { Output = Helpers.Multiply(seed) + Support.Offset.Value; }
                public static Original Create(int seed) => new Original(seed);
                public static Type SelfType => typeof(Original);
                protected override void OnCalculate(int bar, decimal value) { }
            }
            public class UnrelatedIndicator : Indicator { protected override void OnCalculate(int bar, decimal value) { } }
            """, "Copyright Fixture Author; retain this license notice."),
        new IndicatorSourceDocument("Partial.cs", """
            namespace BuiltIn;
            public partial class Original { public static string UnchangedLiteral => "Original"; }
            """, "Copyright Fixture Author; retain this license notice."),
        new IndicatorSourceDocument("Helpers.cs", """
            namespace BuiltIn;
            internal static class Helpers { public static int Multiply(int value) => value * 4; }
            """, "Copyright Fixture Author; retain this license notice."),
        new IndicatorSourceDocument("Offset.cs", """
            namespace BuiltIn.Support;
            internal static class Offset { public static int Value => 5; }
            """, "Copyright Fixture Author; retain this license notice."),
    });

    private sealed class SourceFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "indicator-authoring-" + Guid.NewGuid().ToString("N"));
        public IndicatorSourceFiles Files { get; }
        public SourceFixture() { Directory.CreateDirectory(Root); Files = new(Root); }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
