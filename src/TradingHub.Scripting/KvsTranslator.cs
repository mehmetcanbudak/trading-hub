using System.Text;

namespace TradingHub.Scripting;

/// <summary>A KVS script error at a 1-based source line and column.</summary>
public sealed class KvsException(int line, int column, string message) : Exception(message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}

/// <summary>
/// KVS: a small declarative indicator script language (also accepting the call form used by wrun-style scripts:
/// <c>param("n", 20, {...})</c>, <c>input("c", ohlcv.close)</c>, <c>output("x", line, overlay)</c>,
/// <c>function onBar() {...}</c>, <c>out_x(...)</c>). A <c>.kvs</c> file is translated to one C# indicator class and
/// compiled by <see cref="ScriptCompiler"/> together with the user's C# files: same compiler, same references, same
/// last-good catalog and source monitor. Generated code is never compiled or executed through any other path.
/// <c>#line</c> directives map compiler diagnostics back to the script line.
/// <code>
/// name "EMA bands"
/// param length = 20 { min: 1, max: 500, description: "Bars" }
/// param mult = 2
/// input src = ohlcv.close
/// output basis = line { overlay: true, color: "#f2b53a", width: 2 }
/// output upper = line { overlay: true, color: "#3d8bfd" }
/// onBar {
///   basis = ema(src, length)
///   upper = basis + mult * stdev(src, length)
///   if (crossover(src, upper)) { alert("closed above the band") }
/// }
/// </code>
/// </summary>
public static partial class KvsTranslator
{
    public const string Extension = ".kvs";
    public const string Namespace = "TradingHub.UserScripts.Kvs";
    /// <summary>Category of every compiled script (shown in the indicator settings subtitle).</summary>
    public const string Category = "KVS scripts";
    private const string ClassPrefix = "Kvs_";

    public static bool IsScript(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>C# class of a <c>.kvs</c> file: <c>Kvs_</c> + the file name with non-ASCII-alphanumerics as <c>_</c>.</summary>
    public static string ClassNameFor(string fileNameWithoutExtension)
    {
        var sb = new StringBuilder(ClassPrefix);
        foreach (char c in fileNameWithoutExtension) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    public static string FullTypeNameFor(string fileNameWithoutExtension) => Namespace + "." + ClassNameFor(fileNameWithoutExtension);

    /// <summary>Library description of a compiled script.</summary>
    public static string DescriptionFor(string fileName) => $"KVS script: {fileName}";

    /// <summary>
    /// The file name written in <c>#line</c> directives. A directive file name cannot hold a quote or a line terminator;
    /// diagnostics are mapped back through the syntax tree's own path, never through this text.
    /// </summary>
    private static string LinePath(string path) => SingleLine(path).Replace("\"", "'", StringComparison.Ordinal);

    /// <summary>
    /// Text for a generated comment or directive: control characters and every C# line terminator (U+0085, U+2028,
    /// U+2029 included) become «_», so a file name cannot end the comment's line and reach the generated code.
    /// </summary>
    private static string SingleLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text) sb.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? '_' : c);
        return sb.ToString();
    }

    /// <summary>1-based column of a line's first non-blank character (the statement column used for C# diagnostics).</summary>
    public static int StatementColumn(string source, int line)
    {
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (line < 1 || line > lines.Length) return 1;
        string text = lines[line - 1];
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i < text.Length ? i + 1 : 1;
    }
}

/// <summary>User-visible KVS diagnostics (English source; translated by the UI catalog like other display text).</summary>
internal static class KvsMessages
{
    public const string OptionsBraces = "Options go between braces: { key: value, … }";
    public const string UnterminatedString = "This text has no closing quote (\")";
    public const string TextAfterBlock = "Nothing may follow the brace that closes onBar / onStart";
    public const string NoOutput = "The script has no output (output name = line { … })";

    public static string NotANumber(string text) => $"«{text}» is not a number";
    public static string Reserved(string name) => $"«{name}» is a reserved name; choose another one";
    public static string AlreadyDeclared(string name) => $"«{name}» is already declared";
    public static string UnexpectedDeclaration(string text) => $"Unexpected «{text}» (expected name, param, input, output, level, onStart or onBar)";
    public static string UnclosedBlock(int line) => $"The block opened on line {line} is missing its closing brace";
    public static string OutputType(string kind) => $"Output type «{kind}»: use line, histogram or dots";
    public static string InputSide(string side) => $"Input side «{side}»: use BUY or SELL";
    public static string InputSource(string source) =>
        $"Input «{source}»: use ohlcv.open/high/low/close/volume/hl2/hlc3/ohlc4/time, trades.volume/buy/sell/delta/count or book.bid/ask/delta";
    public static string NoSuchOutput(string name) => $"There is no output «{name}»";
    public static string AssignedUndeclared(string name) => $"«{name}» is not declared; declare it with output or var first";
    public static string UnexpectedStatement(string text) => $"Unexpected statement «{text}»";
    public static string MissingBracket(string name) => $"Missing «]» after «{name}[»";
    public static string ParameterHistory(string name) => $"«{name}» is a parameter, not a series; it has no history";
    public static string Undeclared(string name) => $"«{name}» is not declared (param, input, output or var)";
    public static string UnexpectedToken(string token) => $"Unexpected «{token}» in an expression";
    public static string MissingParenthesis(string name) => $"Missing «)» for «{name}(»";
    public static string EmptyArgument(string name) => $"«{name}(…)» has an empty argument";
    public static string Usage(string signature) => $"Use the form {signature}";
    public static string ParameterRange(string text) => $"«{text}» is too large for a parameter (at most ±7.9e28)";
    public static string SameType(string other) => $"«{other}» already compiles to the same indicator type; rename one of the two files";
    public static string TranslationFailed(string detail) => $"The script could not be translated: {detail}";
    public static string ArgumentCount(string name, string count) => $"«{name}(…)» takes {count} argument(s)";
    public static string UnknownFunction(string name, string available) => $"Unknown function «{name}» (available: {available})";
}
