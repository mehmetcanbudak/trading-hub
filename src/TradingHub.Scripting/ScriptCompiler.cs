using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TradingHub.Scripting;

/// <summary>Un error o advertencia de compilación.</summary>
public sealed record ScriptDiagnostic(string File, int Line, int Column, string Severity, string Id, string Message)
{
    public override string ToString() => $"{Path.GetFileName(File)}({Line},{Column}): {Severity} {Id}: {Message}";
}

/// <summary>A captured source input; its diagnostic path need not still exist on disk.</summary>
public readonly record struct ScriptSource(string Path, string Text);

public sealed class ScriptCompileResult
{
    public bool Success { get; init; }
    public Assembly? Assembly { get; init; }
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; init; } = Array.Empty<ScriptDiagnostic>();
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Compila indicadores en C# en tiempo de ejecución (Roslyn), igual que ATAS compila los suyos.
/// Acepta código escrito para ATAS: traduce sus namespaces (ATAS.Indicators, OFT.Rendering...)
/// a los de Trading Hub, que tienen la misma API.
/// </summary>
public sealed class ScriptCompiler
{
    private static readonly (Regex Pattern, string Replacement)[] NamespaceMap =
    {
        (new Regex(@"\bATAS\.Indicators\.Technical\b"), "TradingHub.Indicators.Technical"),
        (new Regex(@"\bATAS\.Indicators\.Drawing\b"), "TradingHub.Indicators.Drawing"),
        (new Regex(@"\bATAS\.Indicators\b"), "TradingHub.Indicators"),
        (new Regex(@"\bATAS\.DataFeedsCore\b"), "TradingHub.Trading"),
        (new Regex(@"\bOFT\.Localization\b"), "TradingHub.Localization"),
        (new Regex(@"\bOFT\.Attributes\.Editors\b"), "TradingHub.Attributes.Editors"),
        (new Regex(@"\bOFT\.Attributes\b"), "TradingHub.Attributes"),
        (new Regex(@"\bOFT\.Rendering\.Context\.GDIPlus\b"), "TradingHub.Rendering.Context"),
        (new Regex(@"\bOFT\.Rendering\.Settings\b"), "TradingHub.Rendering.Settings"),
        (new Regex(@"\bOFT\.Rendering\.Tools\b"), "TradingHub.Rendering.Tools"),
        (new Regex(@"\bOFT\.Rendering\.Control\b"), "TradingHub.Rendering.Control"),
        (new Regex(@"\bOFT\.Rendering\.Context\b"), "TradingHub.Rendering.Context"),
        (new Regex(@"\bOFT\.Rendering\b"), "TradingHub.Rendering"),
        (new Regex(@"\bUtils\.Common\.Logging\b"), "TradingHub.Common.Logging"),
        (new Regex(@"\bUtils\.Common\.Collections\b"), "TradingHub.Common.Collections"),
        (new Regex(@"\bUtils\.Common\b"), "TradingHub.Common"),
    };

    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using Q = QuanTAlib;
        global using CrossColor = System.Drawing.Color;
        global using CrossColors = System.Drawing.Color;
        global using CrossKey = Avalonia.Input.Key;
        global using CrossKeyEventArgs = Avalonia.Input.KeyEventArgs;
        global using CrossSolidBrush = TradingHub.Common.UniversalSolidBrush;
        global using CrossPen = TradingHub.Common.UniversalPen;
        global using CrossPens = TradingHub.Common.UniversalPens;
        """;

    /// <summary>Adapta el código escrito para ATAS a los namespaces de Trading Hub.</summary>
    public static string Preprocess(string source)
    {
        foreach (var (p, r) in NamespaceMap) source = p.Replace(source, r);
        return source;
    }

    private static List<MetadataReference> BuildReferences()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Explicitly load the pinned dependency before enumerating loaded assemblies.
        // A cold editor process must not depend on an indicator having run first.
        foreach (var assembly in new[] { typeof(QuanTAlib.AbstractBase).Assembly, typeof(TradingHub.Strategies.Strategy).Assembly })
        {
            if (seen.Add(Path.GetFileName(assembly.Location)))
                refs.Add(MetadataReference.CreateFromFile(assembly.Location));
        }
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (path.Length > 0 && File.Exists(path) && seen.Add(Path.GetFileName(path)))
                    refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
            if (seen.Add(Path.GetFileName(asm.Location))) refs.Add(MetadataReference.CreateFromFile(asm.Location));
        }

        return refs;
    }

    /// <summary>The editor and source-copy preflight share the exact runtime compilation pipeline.</summary>
    public static CSharpCompilation CreateCompilation(IReadOnlyDictionary<string, string> sources, string assemblyName, bool adaptCompatibleNamespaces = true) =>
        CreateCompilationCore(sources.Select(source => new ScriptSource(source.Key, source.Value)), assemblyName, adaptCompatibleNamespaces);

    private static CSharpCompilation CreateCompilationCore(IEnumerable<ScriptSource> sources, string assemblyName,
        bool adaptCompatibleNamespaces = true, CancellationToken cancellationToken = default)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: new[] { "CROSS_PLATFORM", "TRADINGHUB" });
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(GlobalUsings, parse, path: "GlobalUsings.g.cs", encoding: System.Text.Encoding.UTF8) };
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trees.Add(CSharpSyntaxTree.ParseText(adaptCompatibleNamespaces ? Preprocess(source.Text) : source.Text,
                parse, path: source.Path, encoding: System.Text.Encoding.UTF8));
        }
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            nullableContextOptions: NullableContextOptions.Annotations,
            allowUnsafe: true);
        return CSharpCompilation.Create(assemblyName + "_" + Guid.NewGuid().ToString("N")[..8], trees, BuildReferences(), options);
    }

    /// <summary>Emits without loading or running user code. A failed preflight never publishes a partial source copy.</summary>
    public static ScriptCompileResult ValidateSources(IReadOnlyDictionary<string, string> sources)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var pe = new MemoryStream();
        var emit = CreateCompilation(sources, "TradingHubSourcePreflight").Emit(pe);
        return new ScriptCompileResult { Success = emit.Success, Diagnostics = Describe(emit.Diagnostics), Elapsed = sw.Elapsed };
    }

    /// <summary>Compiles .cs files into a collectible in-memory assembly.</summary>
    public ScriptCompileResult Compile(IReadOnlyList<string> files, string assemblyName) =>
        CompileCore(ReadSources(files), assemblyName);

    /// <summary>Compiles captured text without reopening its diagnostic paths.</summary>
    public ScriptCompileResult CompileSources(IReadOnlyList<ScriptSource> sources, string assemblyName, CancellationToken cancellationToken = default) =>
        CompileCore(sources, assemblyName, cancellationToken);

    private static IEnumerable<ScriptSource> ReadSources(IReadOnlyList<string> files)
    {
        foreach (string file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue; // preserve the file-based caller's vanished-file behavior
            }
            yield return new(file, text);
        }
    }

    private static ScriptCompileResult CompileCore(IEnumerable<ScriptSource> sources, string assemblyName, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        var compilation = CreateCompilationCore(sources, assemblyName, cancellationToken: cancellationToken);
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emit = compilation.Emit(pe, pdb, cancellationToken: cancellationToken);
        var diags = Describe(emit.Diagnostics);

        if (!emit.Success)
            return new ScriptCompileResult { Success = false, Diagnostics = diags, Elapsed = sw.Elapsed };

        pe.Position = 0;
        pdb.Position = 0;
        var alc = new AssemblyLoadContext(assemblyName, isCollectible: true);
        var asm = alc.LoadFromStream(pe, pdb);
        return new ScriptCompileResult { Success = true, Assembly = asm, Diagnostics = diags, Elapsed = sw.Elapsed };
    }

    private static IReadOnlyList<ScriptDiagnostic> Describe(IEnumerable<Diagnostic> diagnostics) => diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error || (d.Severity == DiagnosticSeverity.Warning && !IgnoredWarnings.Contains(d.Id)))
        .Select(d =>
        {
            var span = d.Location.GetLineSpan();
            return new ScriptDiagnostic(span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                d.Severity == DiagnosticSeverity.Error ? "error" : "warning", d.Id, d.GetMessage(TradingHub.Common.UiCulture.Current));
        }).ToList();

    private static readonly HashSet<string> IgnoredWarnings = new()
    {
        "CS0168", "CS0219", "CS0414", "CS0649", "CS0169", "CS0067", "CS0618", "CS0612", "CS8632", "CS1998", "CS4014", "CS0162", "CS0108", "CS0114",
    };
}
