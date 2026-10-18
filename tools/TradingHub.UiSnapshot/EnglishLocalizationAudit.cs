using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TradingHub.UiSnapshot;

internal static class EnglishLocalizationAudit
{
    private const string Baseline = "9ee74a4c527ac43120423acd41e006ec9d7877ad";
    private static readonly Regex Spanish = new(@"[áéíóúñ¿¡]|\b(?:el|los|las|un|una|del|para|por|con|sin|que|su|sus|solo|desde|hasta|hay|tiene|tenés|podés|pudo|pasa|cero|operaciones|velas|vela|datos|cuenta|muestra|rango|tendencia|riesgo|ganancia|guardar|crear|nuevo|nueva|borrar|compilar|compilado|detener|pausada|corriendo|abrir|cerrar|cargando|ninguna|cantidad|seleccionar|anterior|siguiente|reiniciar|probar|todos|todas|mercado|comprar|vender|valor|nombre|fecha|hora|al|en|se|tu|tus|vos|mis|mismo|misma|antes|ahora|resultados|fuera|según|mientras|indicador|indicadores|alerta|agente|volumen|escalera|doble|perfil|celdas|histograma|simulada|simulador|ganadora|prometedora|perdedora|descartada|capturas|laboratorio|estrategias|reportes|calentamiento|prueba|actualizar|puede|suerte|probabilidad|verde|rojo|azul|celeste|amarillo|naranja|violeta|rosa|gris|blanco|cobra|cobran|costo|coste|precio|compra|venta|compró|vendió|deslizamiento|contrato|periodo|largos|cortos|principales|mejores|combinaciones|glosario|medio|opera|estado|paneles|extender|pesos|neto|meses|pedido|barras|dinero|toda|media|grilla|bayesiana|modo|oscuro|claro|continuo|llenados|reglas|propuestas|portafolios|portafolio|negro|grabando|importado)\b|(?-i: o | y )", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private sealed record AllowedLiteral(string Path, string Value, string Reason, bool AllowMentions = false);
    private sealed record Rules(List<AllowedLiteral> AllowedLiterals, Dictionary<string, string> ApprovedStructure);

    private static bool IsText(SyntaxToken token) => token.IsKind(SyntaxKind.StringLiteralToken)
        || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
        || token.Kind().ToString().Contains("RawStringLiteralToken", StringComparison.Ordinal);

    private static SyntaxNode Parse(string source) => CSharpSyntaxTree.ParseText(source,
        new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();

    internal static bool LooksSpanish(string text) => Spanish.IsMatch(text);

    private static InterpolatedStringExpressionSyntax? RawTemplate(SyntaxToken token) => token.Parent?.AncestorsAndSelf()
        .OfType<InterpolatedStringExpressionSyntax>().FirstOrDefault(x => x.StringStartToken.Kind().ToString().Contains("RawStringStartToken", StringComparison.Ordinal));

    private static string TemplateCode(InterpolatedStringExpressionSyntax raw) => string.Concat(raw.Contents.Select((part, i) =>
        part is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : "__slot" + i));

    internal static string Structure(string source, bool templateFile)
    {
        var pieces = new List<string>();
        foreach (var token in Parse(source.ReplaceLineEndings("\n")).DescendantTokens())
        {
            if (templateFile && RawTemplate(token) is { } raw)
            {
                if (token != raw.StringStartToken) continue;
                pieces.Add(token.Text + Structure(TemplateCode(raw), false));
                pieces.AddRange(raw.Contents.OfType<InterpolationSyntax>().Select(x => Structure(x.Expression.ToString(), false)));
            }
            else pieces.Add(!IsText(token) ? token.RawKind + ":" + token.Text
                : templateFile && token.Kind().ToString().Contains("RawStringLiteralToken", StringComparison.Ordinal)
                    ? Structure(token.ValueText, false) : "TEXT");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", pieces))));
    }

    private static string Git(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output;
    }

    internal static int Run(string root)
    {
        root = Path.GetFullPath(root);
        var rules = JsonSerializer.Deserialize<Rules>(File.ReadAllText(Path.Combine(root, "tools/english-localization-review.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var allowed = rules.AllowedLiterals.ToDictionary(x => (x.Path, x.Value));
        if (allowed.Values.Any(x => string.IsNullOrWhiteSpace(x.Reason))) throw new InvalidDataException("Every compatibility exception needs a reason.");
        var mentions = rules.AllowedLiterals.Where(x => x.AllowMentions).ToLookup(x => x.Path);
        var used = new HashSet<(string, string)>();
        int files = 0, strings = 0, failures = 0;
        void Fail(string text) { failures++; Console.WriteLine("FAIL " + text); }

        // Self-check the language heuristic, tokenization and non-text fingerprint before trusting the scan.
        string rawOne = "class A { string Code => $$\"\"\"class {{name}} { int Size => 1; }\"\"\"; }";
        if (Structure(rawOne, true) == Structure(rawOne.Replace("=> 1", "=> 2"), true))
            throw new InvalidOperationException("Raw template fingerprint self-check failed.");
        string multiline = "class A { string Code => $$\"\"\"\nclass {{name}} { int Size => 1; }\n\"\"\"; }";
        if (Structure(multiline, true) != Structure(multiline.ReplaceLineEndings("\r\n"), true)
            || Structure(multiline, false) != Structure(multiline.ReplaceLineEndings("\r\n"), false))
            throw new InvalidOperationException("Source fingerprint must ignore LF/CRLF differences.");
        if (!LooksSpanish("Cerrar la cuenta") || LooksSpanish("Close account")
            || Structure("class A { string Name => \"Hola\"; }", false) != Structure("class A { string Name => \"Hello\"; }", false)
            || Structure("class A { int Size => 1; }", false) == Structure("class A { int Size => 2; }", false)
            || Parse("class A { /* ñ */ string Name => $\"Hello {1}\"; }").DescendantTokens().Count(IsText) != 1)
            throw new InvalidOperationException("Localization audit self-check failed.");

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/Atas/", StringComparison.Ordinal) || relative.EndsWith("/Strings.g.cs", StringComparison.Ordinal)) continue;
            files++;
            bool template = relative.EndsWith("/StrategyCatalog.cs", StringComparison.Ordinal) || relative.EndsWith("/UserScripts.cs", StringComparison.Ordinal) || relative.EndsWith("/CodeEditorWindow.cs", StringComparison.Ordinal);
            var syntax = Parse(File.ReadAllText(file));
            if (syntax.ContainsDiagnostics && syntax.GetDiagnostics().Any(x => x.Severity == DiagnosticSeverity.Error)) Fail(relative + ": C# syntax errors");
            foreach (var token in syntax.DescendantTokens().Where(IsText))
            {
                strings++;
                // The left side of this display dictionary is deliberately a legacy compatibility key.
                if (relative.EndsWith("/EnglishText.cs", StringComparison.Ordinal)
                    && token.Parent?.AncestorsAndSelf().OfType<ImplicitElementAccessSyntax>().Any() == true) continue;
                    SyntaxToken[] tokens;
                if (template && RawTemplate(token) is { } raw)
                {
                    if (raw.Contents.OfType<InterpolatedStringTextSyntax>().FirstOrDefault()?.TextToken != token) continue;
                    tokens = Parse(TemplateCode(raw)).DescendantTokens().Where(IsText)
                        .Concat(raw.Contents.OfType<InterpolationSyntax>().SelectMany(x => x.Expression.DescendantTokens().Where(IsText))).ToArray();
                }
                else tokens = template && token.Kind().ToString().Contains("RawStringLiteralToken", StringComparison.Ordinal)
                    ? Parse(token.ValueText).DescendantTokens().Where(IsText).ToArray() : new[] { token };
                foreach (var item in tokens)
                {
                    var key = (relative, item.ValueText);
                    if (allowed.ContainsKey(key)) { used.Add(key); continue; }
                    var text = item.ValueText;
                    foreach (var entry in mentions[relative])
                    {
                        string pattern = @"(?<![\p{L}\p{N}_])" + Regex.Escape(entry.Value) + @"(?![\p{L}\p{N}_])";
                        if (!Regex.IsMatch(text, pattern)) continue;
                        used.Add((entry.Path, entry.Value));
                        text = Regex.Replace(text, pattern, "");
                    }
                    if (!LooksSpanish(text)) continue;
                    Fail($"{relative}:{token.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: untranslated candidate {JsonSerializer.Serialize(item.ValueText)}");
                }
            }
        }
        foreach (var entry in allowed.Keys.Except(used)) Fail($"Stale compatibility exception: {entry.Item1} {JsonSerializer.Serialize(entry.Item2)}");

        var tracked = Git(root, "diff", "--name-only", Baseline, "--", "src").Split('\n');
        var added = Git(root, "ls-files", "--others", "--exclude-standard", "--", "src").Split('\n');
        foreach (var relative in tracked.Concat(added).Where(x => x.EndsWith(".cs", StringComparison.Ordinal)).Distinct())
        {
            string path = Path.Combine(root, relative);
            if (!File.Exists(path)) { Fail(relative + ": source deletion requires explicit review"); continue; }
            bool template = relative.EndsWith("/StrategyCatalog.cs", StringComparison.Ordinal) || relative.EndsWith("/UserScripts.cs", StringComparison.Ordinal) || relative.EndsWith("/CodeEditorWindow.cs", StringComparison.Ordinal);
            string current = Structure(File.ReadAllText(path), template);
            if (rules.ApprovedStructure.TryGetValue(relative, out var approved))
            {
                if (current != approved) Fail(relative + ": non-text structure changed since review");
            }
            else if (added.Contains(relative) || current != Structure(Git(root, "show", Baseline + ":" + relative), template))
                Fail(relative + ": non-text source change needs review");
        }
        Console.WriteLine($"English source audit: {files} files, {strings} text tokens; {used.Count} documented compatibility exceptions; {failures} failures.");
        Console.WriteLine("Scope: built-in text candidates and reviewed source structure; not a natural-language proof or live-trading certification.");
        return failures == 0 ? 0 : 1;
    }
}
