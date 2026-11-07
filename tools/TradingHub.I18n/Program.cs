using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// English display literals/templates are canonical. Extraction is deliberately inclusive: review pending entries
// before translating identifiers, paths, arbitrary user content or generated code. rebase.py records immutable
// checkpoint/fork/upstream correspondence and reviewed overrides; coverage prevents losing upstream translations.
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

switch (args.FirstOrDefault())
{
    case "extract" when args.Length == 3:
        Extract(args[1], args[2]);
        break;
    case "pending" when args.Length is 4 or 5:
        Pending(args[1], args[2], args[3], args.Length > 4 ? int.Parse(args[4]) : 400);
        break;
    case "merge" when args.Length >= 3:
        Merge(args[1], args.Skip(2).ToArray());
        break;
    case "check" when args is [_, _, _] or [_, _, _, "--require-complete"] or
        [_, _, _, "--require-complete", "--reviewed-exclusions", _]:
        Environment.ExitCode = Check(args[1], args[2], args.Length > 3, args.Length == 6 ? args[5] : null);
        break;
    case "coverage" when args.Length == 3:
        Environment.ExitCode = Coverage(args[1], args[2]);
        break;
    default:
        Console.WriteLine("Usage: extract <src> <catalog.json> | pending <catalog.json> <language.json> <folder> [size] | merge <language.json> <chunks...> | check <catalog.json> <language.json> [--require-complete [--reviewed-exclusions <exclusions.json>]] | coverage <provenance.json> <language.json>");
        Environment.ExitCode = 1;
        break;
}

void Extract(string src, string output)
{
    var found = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
        if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.Contains("/Atas/") || rel.Contains("/Localization/") || rel.EndsWith(".g.cs")) continue;
        // Machine-facing MCP protocol and assistant prompts are not display catalogs.
        if (rel.EndsWith("Lab/LabServer.cs")) continue;
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            IEnumerable<string> keys = node switch
            {
                InterpolatedStringExpressionSyntax interpolation => ExpandedTemplates(interpolation),
                LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => new[] { Escape(literal.Token.ValueText) },
                BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) && binary.Parent is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } && Concat(binary) is { } key => new[] { key },
                _ => Array.Empty<string>(),
            };
            foreach (var key in keys.Where(IsText))
            {
                if (!found.TryGetValue(key, out var where)) found[key] = where = new SortedSet<string>(StringComparer.Ordinal);
                where.Add($"{rel}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
            }
        }
    }
    File.WriteAllText(output, JsonSerializer.Serialize(found.ToDictionary(kv => kv.Key, kv => string.Join(" ", kv.Value)), json));
    Console.WriteLine($"{found.Count} texts ({found.Keys.Count(k => Holes(k).Length > 0)} templates) → {output}");
}

static string Escape(string text) => text.Replace("{", "{{").Replace("}", "}}");

static string Template(InterpolatedStringExpressionSyntax expression)
{
    var text = new StringBuilder();
    int hole = 0;
    foreach (var part in expression.Contents)
        if (part is InterpolatedStringTextSyntax literal) text.Append(Escape(literal.TextToken.ValueText));
        else text.Append('{').Append(hole++).Append('}');
    return text.ToString();
}

static IEnumerable<string> ExpandedTemplates(InterpolatedStringExpressionSyntax expression)
{
    var key = Template(expression);
    var choices = new Dictionary<int, (string Condition, string Yes, string No)>();
    int index = 0;
    foreach (var interpolation in expression.Contents.OfType<InterpolationSyntax>())
    {
        var value = interpolation.Expression;
        while (value is ParenthesizedExpressionSyntax parenthesized) value = parenthesized.Expression;
        if (value is ConditionalExpressionSyntax conditional &&
            conditional.WhenTrue is LiteralExpressionSyntax yes && yes.IsKind(SyntaxKind.StringLiteralExpression) &&
            conditional.WhenFalse is LiteralExpressionSyntax no && no.IsKind(SyntaxKind.StringLiteralExpression))
            choices[index] = (conditional.Condition.WithoutTrivia().ToString(), yes.Token.ValueText, no.Token.ValueText);
        index++;
    }
    var conditions = choices.Values.Select(c => c.Condition).Distinct(StringComparer.Ordinal).ToArray();
    if (conditions.Length == 0 || conditions.Length > 8) return new[] { key };
    var results = new HashSet<string>(StringComparer.Ordinal);
    for (int state = 0; state < (1 << conditions.Length); state++)
    {
        int currentState = state;
        var remaining = Enumerable.Range(0, index).Where(i => !choices.ContainsKey(i)).Select((hole, position) => (hole, position)).ToDictionary(x => x.hole, x => x.position);
        results.Add(Regex.Replace(key, @"(?<!\{)\{(\d+)\}(?!\})", match =>
        {
            int hole = int.Parse(match.Groups[1].Value);
            if (!choices.TryGetValue(hole, out var choice)) return "{" + remaining[hole] + "}";
            return Escape((currentState & (1 << Array.IndexOf(conditions, choice.Condition))) != 0 ? choice.Yes : choice.No);
        }));
    }
    return results;
}

static string? Concat(BinaryExpressionSyntax expression)
{
    var parts = new List<ExpressionSyntax>();
    void Walk(ExpressionSyntax node)
    {
        if (node is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } binary)
        {
            Walk(binary.Left);
            Walk(binary.Right);
        }
        else parts.Add(node);
    }
    Walk(expression);
    if (!parts.Any(p => p is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } or InterpolatedStringExpressionSyntax)) return null;
    var text = new StringBuilder();
    int nextHole = 0;
    foreach (var part in parts)
    {
        if (part is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal) text.Append(Escape(literal.Token.ValueText));
        else if (part is InterpolatedStringExpressionSyntax interpolation)
        {
            var inner = Template(interpolation);
            text.Append(Regex.Replace(inner, @"(?<!\{)\{(\d+)\}(?!\})", m => "{" + (int.Parse(m.Groups[1].Value) + nextHole) + "}"));
            nextHole += interpolation.Contents.OfType<InterpolationSyntax>().Count();
        }
        else text.Append('{').Append(nextHole++).Append('}');
    }
    var result = text.ToString();
    return Regex.Replace(result, @"\{\d+\}", "").Trim().Length < 2 ? null : result;
}

static bool IsText(string key)
{
    if (key.Length > 700 || key.Contains("using TradingHub") || key.Contains("using Vela") || key.Contains("<!doctype") || key.Contains("<html") || key.Contains("public class ")) return false;
    var plain = Regex.Replace(key, @"\{\d+\}", " ").Trim();
    if (!Regex.IsMatch(plain, @"\p{L}{3}")) return false;
    if (Regex.IsMatch(plain, @"^(https?|wss?|file)://") || plain.Contains("/usr/") || plain.StartsWith("--")) return false;
    if (!plain.Contains(' '))
    {
        if (Regex.IsMatch(plain, @"[_./\\:=<>#@\[\]]")) return false;
        if (!char.IsUpper(plain[0])) return false;
        if (Regex.IsMatch(plain, @"^[A-Z0-9]+$")) return false;
        if (Regex.IsMatch(plain, @"[a-z][A-Z]")) return false;
    }
    return true;
}

static Dictionary<string, string> ReadMap(string path) => JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;

void Pending(string catalogPath, string langPath, string folder, int size)
{
    if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
    var catalog = ReadMap(catalogPath);
    var translated = File.Exists(langPath) ? ReadMap(langPath) : new();
    var missing = catalog.Where(kv => !translated.ContainsKey(kv.Key)).OrderBy(kv => kv.Value, StringComparer.Ordinal).ToList();
    Directory.CreateDirectory(folder);
    int count = 0;
    foreach (var chunk in missing.Chunk(size))
        File.WriteAllText(Path.Combine(folder, $"chunk-{++count:00}.json"), JsonSerializer.Serialize(chunk.ToDictionary(kv => kv.Key, kv => kv.Value), json));
    Console.WriteLine($"{missing.Count} untranslated texts in {count} chunks → {folder}");
}

void Merge(string langPath, string[] chunks)
{
    var translated = File.Exists(langPath) ? ReadMap(langPath) : new();
    int added = 0;
    foreach (var chunk in chunks)
        foreach (var (key, value) in ReadMap(chunk))
            if (!string.IsNullOrWhiteSpace(value))
            {
                translated[key] = value;
                added++;
            }
    File.WriteAllText(langPath, JsonSerializer.Serialize(translated.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value), json));
    Console.WriteLine($"{added} translations merged; {translated.Count} in {langPath}");
}

static string Holes(string text) => string.Join(",", Regex.Matches(text, @"(?<!\{)\{(\d+)\}(?!\})").Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal));

static int CheckHoles(Dictionary<string, string> language)
{
    int bad = 0;
    foreach (var (key, value) in language)
        if (Holes(key) != Holes(value))
        {
            Console.WriteLine($"Mismatched data holes: «{key}» → «{value}»");
            bad++;
        }
    return bad;
}

static int Check(string catalogPath, string langPath, bool requireComplete, string? reviewedExclusions)
{
    var catalog = ReadMap(catalogPath);
    var language = ReadMap(langPath);
    int bad = CheckHoles(language);
    Dictionary<string, NonDisplayExclusion>? exclusions = reviewedExclusions == null ? null :
        JsonSerializer.Deserialize<Dictionary<string, NonDisplayExclusion>>(File.ReadAllText(reviewedExclusions)) ??
        throw new InvalidDataException("The reviewed non-display exclusion contract is empty.");
    int invalid = 0;
    if (exclusions != null)
        foreach (var (key, exclusion) in exclusions)
            if (string.IsNullOrWhiteSpace(exclusion.Reason) || !catalog.TryGetValue(key, out string? locations) ||
                exclusion.Locations != locations)
            {
                Console.WriteLine($"Stale or unreviewed non-display exclusion: «{key}»");
                invalid++;
            }
    int missing = 0;
    foreach (string key in catalog.Keys)
        if (!language.ContainsKey(key) && exclusions?.ContainsKey(key) != true)
        {
            missing++;
            if (requireComplete) Console.WriteLine($"Untranslated display candidate: «{key}»");
        }
    Console.WriteLine($"{language.Count} translations, {missing} untranslated candidates, {bad} mismatched templates, {invalid} invalid non-display exclusions");
    return bad > 0 || invalid > 0 || requireComplete && missing > 0 ? 1 : 0;
}

static int Coverage(string provenancePath, string langPath)
{
    var language = ReadMap(langPath);
    using var provenance = JsonDocument.Parse(File.ReadAllText(provenancePath));
    int missing = 0, entries = 0;
    foreach (var upstream in provenance.RootElement.GetProperty("entries").EnumerateObject())
    {
        entries++;
        var keys = upstream.Value.GetProperty("aliases").EnumerateArray()
            .Concat(upstream.Value.GetProperty("correspondence").EnumerateArray())
            .Select(item => item.GetProperty("key").GetString()!).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) { Console.WriteLine($"Lost upstream entry: {upstream.Name}"); missing++; }
        foreach (var key in keys.Where(key => !language.ContainsKey(key)))
        {
            Console.WriteLine($"Lost translation for {upstream.Name}: {key}");
            missing++;
        }
    }
    int bad = CheckHoles(language);
    Console.WriteLine($"{entries} upstream provenance entries, {missing} missing translations, {bad} mismatched templates");
    return missing > 0 || bad > 0 ? 1 : 0;
}

sealed record NonDisplayExclusion(
    [property: JsonPropertyName("locations")] string Locations,
    [property: JsonPropertyName("reason")] string Reason);
