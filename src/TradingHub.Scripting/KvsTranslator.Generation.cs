using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using TradingHub.Indicators;

namespace TradingHub.Scripting;

/// <summary>
/// Parsing and C# generation for KVS scripts (machine-facing: every text literal here is KVS or C# source, not UI text;
/// the localization extractor skips this file). User-visible errors come from <see cref="KvsMessages"/>.
/// </summary>
public static partial class KvsTranslator
{
    private sealed record Param(string Name, string Default, bool IsBool, string? Min, string? Max, string? Description, int Line);
    private sealed record Input(string Name, string Source, Dictionary<string, string> Options, int Line, int Column);
    private sealed record Output(string Name, string Kind, Dictionary<string, string> Options, int Line, int Column);
    private sealed record Statement(string Text, int Line, int Column);

    private static readonly Dictionary<string, string> Implicit = new(StringComparer.Ordinal)
    {
        ["open"] = "ohlcv.open", ["high"] = "ohlcv.high", ["low"] = "ohlcv.low", ["close"] = "ohlcv.close", ["volume"] = "ohlcv.volume",
        ["hl2"] = "ohlcv.hl2", ["hlc3"] = "ohlcv.hlc3", ["ohlc4"] = "ohlcv.ohlc4", ["time"] = "ohlcv.time", ["delta"] = "trades.delta",
        ["buyvol"] = "trades.buy", ["sellvol"] = "trades.sell", ["trades"] = "trades.count",
    };

    private static readonly Dictionary<string, string> Windowed = new(StringComparer.Ordinal)
    {
        ["ema"] = "Ema", ["rma"] = "Rma", ["sma"] = "Sma", ["wma"] = "Wma", ["stdev"] = "Stdev",
        ["highest"] = "Highest", ["lowest"] = "Lowest", ["sum"] = "Sum", ["change"] = "Change",
    };

    private static readonly Dictionary<string, (string Target, int MinArgs, int MaxArgs)> MathFunctions = new(StringComparer.Ordinal)
    {
        ["abs"] = ("Math.Abs", 1, 1), ["min"] = ("Math.Min", 2, 2), ["max"] = ("Math.Max", 2, 2), ["sqrt"] = ("Math.Sqrt", 1, 1),
        ["log"] = ("Math.Log", 1, 1), ["log10"] = ("Math.Log10", 1, 1), ["exp"] = ("Math.Exp", 1, 1), ["pow"] = ("Math.Pow", 2, 2),
        ["round"] = ("Math.Round", 1, 1), ["floor"] = ("Math.Floor", 1, 1), ["ceil"] = ("Math.Ceiling", 1, 1), ["sign"] = ("(double)Math.Sign", 1, 1),
        ["nz"] = ("KvsRuntime.Nz", 1, 2), ["na"] = ("KvsRuntime.Na", 1, 1), ["sin"] = ("Math.Sin", 1, 1), ["cos"] = ("Math.Cos", 1, 1),
        ["tan"] = ("Math.Tan", 1, 1),
    };

    private static readonly string FunctionList = string.Join(", ", Windowed.Keys.Concat(new[] { "cum", "crossover", "crossunder" }).Concat(MathFunctions.Keys));

    /// <summary>Script names cannot hide members the chart host relies on (Name, Panel, DataSeries…).</summary>
    private static readonly HashSet<string> ReservedNames = typeof(Indicator)
        .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(m => m is not ConstructorInfo)
        .Select(m => m.Name)
        .Concat(new[] { "bar", "value", "c", "lastBar", "bar_index", "na", "PI", "true", "false", "and", "or", "not", "if", "else", "var", "let", "const" })
        .ToHashSet(StringComparer.Ordinal);

    // ------------------------------------------------------------------ lexical helpers

    /// <summary>Removes comments without changing line numbers or columns (replaced by spaces / kept newlines).</summary>
    private static string[] StripComments(string source)
    {
        var text = source.Replace("\r\n", "\n", StringComparison.Ordinal).ToCharArray();
        bool inString = false, block = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (block)
            {
                if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    text[i] = text[i + 1] = ' ';
                    i++;
                    block = false;
                }
                else if (text[i] != '\n') text[i] = ' ';
                continue;
            }

            if (text[i] == '\n') { inString = false; continue; }
            if (text[i] == '"') { inString = !inString; continue; }
            if (inString || text[i] != '/' || i + 1 >= text.Length) continue;
            if (text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') text[i++] = ' ';
                i--;
            }
            else if (text[i + 1] == '*')
            {
                text[i] = text[i + 1] = ' ';
                i++;
                block = true;
            }
        }

        return new string(text).Split('\n');
    }

    private static (int Opens, int Closes) Braces(string text)
    {
        int opens = 0, closes = 0;
        bool inString = false;
        foreach (char c in text)
        {
            if (c == '"') inString = !inString;
            else if (!inString && c == '{') opens++;
            else if (!inString && c == '}') closes++;
        }

        return (opens, closes);
    }

    private static int ColumnOf(string raw, string token, int fallback)
    {
        var m = Regex.Match(raw, @"(?<![\w.])" + Regex.Escape(token) + @"(?!\w)");
        return m.Success ? m.Index + 1 : fallback;
    }

    private static int FirstColumn(string raw)
    {
        int i = 0;
        while (i < raw.Length && char.IsWhiteSpace(raw[i])) i++;
        return i + 1;
    }

    /// <summary>The wrun call form → the short KVS form.</summary>
    private static string Normalize(string line)
    {
        var t = line.Trim();
        var m = Regex.Match(t, @"^(?:const\s+\w+\s*=\s*)?param\(\s*""(\w+)""\s*,\s*([^,{)]+?)\s*(?:,\s*(\{.*\}))?\s*\)\s*;?$");
        if (m.Success) return $"param {m.Groups[1].Value} = {m.Groups[2].Value} {m.Groups[3].Value}".TrimEnd();
        m = Regex.Match(t, @"^(?:const\s+\w+\s*=\s*)?input\(\s*""(\w+)""\s*,\s*([\w.]+)\s*(?:,\s*(\{.*\}))?\s*\)\s*;?$");
        if (m.Success) return $"input {m.Groups[1].Value} = {m.Groups[2].Value} {m.Groups[3].Value}".TrimEnd();
        m = Regex.Match(t, @"^(?:const\s+\w+\s*=\s*)?output\(\s*""(\w+)""\s*,\s*(\w+)\s*(?:,\s*(overlay|lower|panel))?\s*(?:,\s*\{(.*)\})?\s*\)\s*;?$");
        if (m.Success)
        {
            string where = m.Groups[3].Value switch { "overlay" => "overlay: true", "" => "", _ => "overlay: false" };
            string all = string.Join(", ", new[] { where, m.Groups[4].Value.Trim() }.Where(x => x.Length > 0));
            return $"output {m.Groups[1].Value} = {m.Groups[2].Value} {{ {all} }}";
        }

        return Regex.Replace(t, @"^(?:export\s+)?function\s+(onBar|onStart)\s*\(\s*\)\s*\{", "$1 {");
    }

    /// <summary>«{ min: 1, color: "#fff", colors: ["#a", "#b"] }» → dictionary of unquoted text values.</summary>
    private static Dictionary<string, string> Options(string text, int line, int column)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = text.Trim();
        if (text.Length == 0) return options;
        if (!text.StartsWith('{') || !text.EndsWith('}')) throw new KvsException(line, column, KvsMessages.OptionsBraces);
        foreach (Match m in Regex.Matches(text[1..^1], @"(\w+)\s*:\s*(""[^""]*""|\[[^\]]*\]|[^,]+)"))
            options[m.Groups[1].Value] = m.Groups[2].Value.Trim().Trim('"');
        return options;
    }

    private static string Number(string text, int line, int column)
    {
        text = text.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            return value.ToString("R", CultureInfo.InvariantCulture) + "d";
        throw new KvsException(line, column, KvsMessages.NotANumber(text));
    }

    private static string Literal(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal) + "\"";

    private static void CheckName(string name, int line, int column, HashSet<string> declared)
    {
        if (SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) == SyntaxKind.VarKeyword ||
            ReservedNames.Contains(name))
            throw new KvsException(line, column, KvsMessages.Reserved(name));
        if (!declared.Add(name)) throw new KvsException(line, column, KvsMessages.AlreadyDeclared(name));
    }

    // ------------------------------------------------------------------ translation

    /// <summary>Translates one <c>.kvs</c> script to C#. <paramref name="path"/> is written into <c>#line</c> directives.</summary>
    public static string Translate(string source, string fileNameWithoutExtension, string path)
    {
        var raw = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lines = StripComments(source);
        string display = fileNameWithoutExtension;
        var parameters = new List<Param>();
        var inputs = new List<Input>();
        var outputs = new List<Output>();
        var levels = new List<(string Value, string? Color)>();
        var onBar = new List<Statement>();
        var onStart = new List<Statement>();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        List<Statement>? block = null;
        int depth = 0, blockLine = 0, blockColumn = 1;

        for (int i = 0; i < lines.Length; i++)
        {
            int ln = i + 1, col = FirstColumn(lines[i]);
            var t = Normalize(lines[i]);
            if (t.Length == 0) continue;
            if (block != null)
            {
                var (opens, closes) = Braces(t);
                if (depth + opens - closes <= 0)
                {
                    // «x = 1 }» closes the block: keep what precedes its closing brace
                    int close = t.LastIndexOf('}');
                    var inner = t[..close].Trim();
                    if (inner.Length > 0) block.Add(new(inner, ln, col));
                    if (t[(close + 1)..].Trim().Length > 0) throw new KvsException(ln, col + close + 1, KvsMessages.TextAfterBlock);
                    block = null;
                    depth = 0;
                    continue;
                }

                depth += opens - closes;
                block.Add(new(t, ln, col));
                continue;
            }

            Match m;
            if ((m = Regex.Match(t, @"^name\s+""([^""]+)""\s*;?$")).Success)
            {
                display = m.Groups[1].Value.Trim();
                continue;
            }

            if ((m = Regex.Match(t, @"^param\s+(\w+)\s*=\s*([^{]+?)\s*(\{.*\})?\s*;?$")).Success)
            {
                string name = m.Groups[1].Value, value = m.Groups[2].Value.Trim();
                CheckName(name, ln, ColumnOf(raw[i], name, col), declared);
                var o = Options(m.Groups[3].Value, ln, col);
                bool isBool = value is "true" or "false";
                string? min = o.GetValueOrDefault("min"), max = o.GetValueOrDefault("max");
                if (!isBool)
                {
                    if (min != null) Number(min, ln, ColumnOf(raw[i], "min", col));
                    if (max != null) Number(max, ln, ColumnOf(raw[i], "max", col));
                }

                parameters.Add(new Param(name, isBool ? value : Number(value, ln, ColumnOf(raw[i], value, col)), isBool, min, max,
                    o.GetValueOrDefault("description"), ln));
                continue;
            }

            if ((m = Regex.Match(t, @"^input\s+(\w+)\s*=\s*([\w.]+)\s*(\{.*\})?\s*;?$")).Success)
            {
                CheckName(m.Groups[1].Value, ln, ColumnOf(raw[i], m.Groups[1].Value, col), declared);
                inputs.Add(new Input(m.Groups[1].Value, m.Groups[2].Value, Options(m.Groups[3].Value, ln, col), ln, ColumnOf(raw[i], m.Groups[2].Value, col)));
                continue;
            }

            if ((m = Regex.Match(t, @"^output\s+(\w+)\s*=\s*(\w+)\s*(\{.*\})?\s*;?$")).Success)
            {
                CheckName(m.Groups[1].Value, ln, ColumnOf(raw[i], m.Groups[1].Value, col), declared);
                outputs.Add(new Output(m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant(), Options(m.Groups[3].Value, ln, col), ln,
                    ColumnOf(raw[i], m.Groups[2].Value, col)));
                continue;
            }

            if ((m = Regex.Match(t, @"^level\s+(-?[\d.]+(?:[eE][-+]?\d+)?)\s*(\{.*\})?\s*;?$")).Success)
            {
                levels.Add((Number(m.Groups[1].Value, ln, col), Options(m.Groups[2].Value, ln, col).GetValueOrDefault("color")));
                continue;
            }

            if ((m = Regex.Match(t, @"^(onBar|onStart)\s*\{(.*)$")).Success)
            {
                block = m.Groups[1].Value == "onBar" ? onBar : onStart;
                depth = 1;
                blockLine = ln;
                blockColumn = col;
                var rest = m.Groups[2].Value.Trim();
                if (rest.Length == 0) continue;
                var (opens, closes) = Braces(rest);
                if (1 + opens - closes <= 0)
                {
                    int close = rest.LastIndexOf('}');
                    var inner = rest[..close].Trim();
                    if (inner.Length > 0) block.Add(new(inner, ln, ColumnOf(raw[i], inner, col)));
                    block = null;
                    depth = 0;
                }
                else
                {
                    depth += opens - closes;
                    block.Add(new(rest, ln, ColumnOf(raw[i], rest, col)));
                }

                continue;
            }

            throw new KvsException(ln, col, KvsMessages.UnexpectedDeclaration(t));
        }

        if (block != null) throw new KvsException(blockLine, blockColumn, KvsMessages.UnclosedBlock(blockLine));
        if (outputs.Count == 0) throw new KvsException(1, 1, KvsMessages.NoOutput);

        // var / let / const inside blocks are per-bar series too
        var vars = new List<string>();
        foreach (var s in onStart.Concat(onBar))
        {
            var vm = Regex.Match(s.Text, @"^(?:var|let|const)\s+(\w+)\s*=");
            if (!vm.Success || vars.Contains(vm.Groups[1].Value)) continue;
            CheckName(vm.Groups[1].Value, s.Line, ColumnOf(raw[s.Line - 1], vm.Groups[1].Value, s.Column), declared);
            vars.Add(vm.Groups[1].Value);
        }

        var series = new List<string>();
        series.AddRange(inputs.Select(x => x.Name));
        series.AddRange(outputs.Select(x => x.Name));
        series.AddRange(vars);
        var seriesSet = series.ToHashSet(StringComparer.Ordinal);
        var parameterMap = parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);

        // price series used without declaring them (close, high, delta…) become inputs automatically
        foreach (var s in onBar.Concat(onStart))
            foreach (Match token in Regex.Matches(Regex.Replace(s.Text, "\"[^\"]*\"", "\"\""), @"\b([A-Za-z_]\w*)\b"))
            {
                var name = token.Groups[1].Value;
                if (seriesSet.Contains(name) || parameterMap.ContainsKey(name) || !Implicit.TryGetValue(name, out var implicitSource)) continue;
                inputs.Add(new Input(name, implicitSource, new Dictionary<string, string>(), s.Line, s.Column));
                series.Add(name);
                seriesSet.Add(name);
            }

        var context = new Context(seriesSet, parameterMap, raw);
        string cls = ClassNameFor(fileNameWithoutExtension);
        string linePath = LinePath(path);
        // without «overlay: true» on every output, outputs go to a separate panel
        bool overlayAll = outputs.All(o => o.Options.GetValueOrDefault("overlay") == "true");
        var sb = new StringBuilder();
        sb.Append("// <auto-generated> Translated from ").Append(Path.GetFileName(path)).AppendLine(" (KVS indicator script); edit the .kvs file. </auto-generated>");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.ComponentModel;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine("using System.Drawing;");
        sb.AppendLine("using TradingHub.Indicators;");
        sb.AppendLine("using TradingHub.Indicators.Kvs;");
        sb.Append("namespace ").AppendLine(Namespace);
        sb.AppendLine("{");
        sb.Append("[DisplayName(").Append(Literal(display)).AppendLine(")]");
        sb.Append("[Category(").Append(Literal(Category)).AppendLine(")]");
        sb.Append("[Description(").Append(Literal(DescriptionFor(Path.GetFileName(path)))).AppendLine(")]");
        sb.Append("public class ").Append(cls).AppendLine(" : Indicator");
        sb.AppendLine("{");
        foreach (var name in series) sb.Append("    private readonly KvsSeries S_").Append(name).AppendLine(" = new KvsSeries();");
        var palette = new[] { "#3d8bfd", "#f2b53a", "#26c281", "#f0525a", "#b478ff", "#ffffff" };
        for (int k = 0; k < outputs.Count; k++)
        {
            var o = outputs[k];
            string visual = o.Kind switch
            {
                "histogram" or "histo" or "bar" or "bars" or "columns" => "VisualMode.Histogram",
                "dots" or "points" or "dot" or "circles" => "VisualMode.Dots",
                "cross" => "VisualMode.Cross",
                "line" or "area" or "step" => "VisualMode.Line",
                _ => throw new KvsException(o.Line, o.Column, KvsMessages.OutputType(o.Kind)),
            };
            string color = o.Options.GetValueOrDefault("color") ?? palette[k % palette.Length];
            int width = o.Options.TryGetValue("width", out var w) && int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wi) ? Math.Clamp(wi, 1, 10) : 1;
            string label = o.Options.GetValueOrDefault("label") ?? o.Name;
            bool showZero = o.Options.GetValueOrDefault("show_zero") == "true";
            string colorExpr = $"KvsRuntime.ParseColor({Literal(color)}, Color.White)";
            if (o.Options.TryGetValue("opacity", out var op) && double.TryParse(op, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
                colorExpr = $"Color.FromArgb({(int)Math.Round(Math.Clamp(opacity, 0, 1) * 255)}, {colorExpr})";
            sb.Append("    private readonly ValueDataSeries O_").Append(o.Name).Append(" = new ValueDataSeries(").Append(Literal(o.Name)).Append(", ").Append(Literal(label))
                .Append(") { Color = ").Append(colorExpr).Append(", Width = ").Append(width.ToString(CultureInfo.InvariantCulture))
                .Append(", VisualType = ").Append(visual).Append(", ShowZeroValue = ").Append(showZero ? "true" : "false").AppendLine(" };");
        }

        sb.AppendLine("    private readonly KvsRuntime _f;");
        foreach (var p in parameters)
        {
            sb.Append("#line ").Append(p.Line).Append(" \"").Append(linePath).AppendLine("\"");
            sb.Append("    [Display(Name = ").Append(Literal(p.Name)).Append(", GroupName = \"Parameters\", Order = ").Append(p.Line);
            if (p.Description != null) sb.Append(", Description = ").Append(Literal(p.Description));
            sb.Append(")]");
            if (!p.IsBool && (p.Min != null || p.Max != null))
                sb.Append(" [Range(").Append(p.Min != null ? Number(p.Min, p.Line, 1) : "double.MinValue").Append(", ")
                    .Append(p.Max != null ? Number(p.Max, p.Line, 1) : "double.MaxValue").Append(")]");
            sb.AppendLine();
            if (p.IsBool)
                sb.Append("    public bool ").Append(p.Name).Append(" { get => _p_").Append(p.Name).Append("; set { _p_").Append(p.Name)
                    .AppendLine(" = value; RecalculateValues(); } }")
                    .Append("    private bool _p_").Append(p.Name).Append(" = ").Append(p.Default).AppendLine(";");
            else
                sb.Append("    public decimal ").Append(p.Name).Append(" { get => (decimal)_p_").Append(p.Name).Append("; set { _p_").Append(p.Name)
                    .AppendLine(" = (double)value; RecalculateValues(); } }")
                    .Append("    private double _p_").Append(p.Name).Append(" = ").Append(p.Default).AppendLine(";");
        }

        sb.AppendLine("#line default");
        sb.Append("    public ").Append(cls).AppendLine("() : base(true)");
        sb.AppendLine("    {");
        sb.AppendLine("        _f = new KvsRuntime(SITES);");
        if (!overlayAll) sb.AppendLine("        Panel = IndicatorDataProvider.NewPanel;");
        sb.Append("        DataSeries[0] = O_").Append(outputs[0].Name).AppendLine(";");
        foreach (var o in outputs.Skip(1)) sb.Append("        DataSeries.Add(O_").Append(o.Name).AppendLine(");");
        for (int k = 0; k < levels.Count; k++)
            sb.Append("        LineSeries.Add(new LineSeries(\"L").Append(k).Append("\", \"Level\") { Value = (decimal)").Append(levels[k].Value)
                .Append(", Color = KvsRuntime.ParseColor(").Append(Literal(levels[k].Color ?? "#80808c")).AppendLine(", Color.Gray) });");
        sb.AppendLine("    }");

        sb.AppendLine("    protected override void OnRecalculate()");
        sb.AppendLine("    {");
        sb.AppendLine("        _f.Reset();");
        foreach (var name in series) sb.Append("        S_").Append(name).AppendLine(".Clear();");
        sb.AppendLine("        int bar = 0;");
        sb.AppendLine("        bool lastBar = false;");
        foreach (var s in onStart) sb.AppendLine(TranslateStatement(s, context, linePath));
        sb.AppendLine("#line default");
        sb.AppendLine("    }");

        sb.AppendLine("    protected override void OnCalculate(int bar, decimal value)");
        sb.AppendLine("    {");
        sb.AppendLine("        var c = GetCandle(bar);");
        sb.AppendLine("        bool lastBar = bar == CurrentBar - 1;");
        foreach (var x in inputs) sb.AppendLine(InputLine(x, linePath));
        sb.AppendLine("#line default");
        foreach (var s in onBar) sb.AppendLine(TranslateStatement(s, context, linePath));
        sb.AppendLine("#line default");
        foreach (var o in outputs)
        {
            sb.Append("        { double v = S_").Append(o.Name).Append(".Get(bar); O_").Append(o.Name).AppendLine("[bar] = KvsRuntime.ToDecimal(v);");
            if (o.Options.GetValueOrDefault("color_by") == "sign")
            {
                var colors = (o.Options.GetValueOrDefault("colors") ?? "").Trim('[', ']').Split(',').Select(x => x.Trim().Trim('"')).ToArray();
                string up = colors.Length > 0 && colors[0].Length > 0 ? colors[0] : "#26c281", down = colors.Length > 1 && colors[1].Length > 0 ? colors[1] : "#f0525a";
                sb.Append("          if (!double.IsNaN(v)) O_").Append(o.Name).Append(".Colors[bar] = v >= 0 ? KvsRuntime.ParseColor(").Append(Literal(up))
                    .Append(", Color.Green) : KvsRuntime.ParseColor(").Append(Literal(down)).AppendLine(", Color.Red);");
            }

            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("}");
        return sb.ToString().Replace("SITES", Math.Max(1, context.Sites).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private sealed class Context(HashSet<string> series, Dictionary<string, Param> parameters, string[] raw)
    {
        public HashSet<string> Series { get; } = series;
        public Dictionary<string, Param> Parameters { get; } = parameters;
        public string[] Raw { get; } = raw;
        public int Sites { get; set; }
    }

    private static string InputLine(Input x, string linePath)
    {
        int within = x.Options.TryGetValue("within", out var w) && int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wi) ? Math.Clamp(wi, 0, 100_000) : 20;
        string side = (x.Options.GetValueOrDefault("side") ?? "").ToUpperInvariant();
        string expr = x.Source.ToLowerInvariant() switch
        {
            "ohlcv.open" or "open" => "(double)c.Open",
            "ohlcv.high" or "high" => "(double)c.High",
            "ohlcv.low" or "low" => "(double)c.Low",
            "ohlcv.close" or "close" => "(double)c.Close",
            "ohlcv.volume" or "volume" => "(double)c.Volume",
            "ohlcv.hl2" => "(double)((c.High + c.Low) / 2)",
            "ohlcv.hlc3" => "(double)((c.High + c.Low + c.Close) / 3)",
            "ohlcv.ohlc4" => "(double)((c.Open + c.High + c.Low + c.Close) / 4)",
            "ohlcv.time" => "(c.Time - DateTime.UnixEpoch).TotalSeconds",
            "trades.volume" => side switch { "BUY" => "KvsRuntime.Side(c, true)", "SELL" => "KvsRuntime.Side(c, false)", "" => "(double)c.Volume",
                _ => throw new KvsException(x.Line, x.Column, KvsMessages.InputSide(side)) },
            "trades.buy" => "KvsRuntime.Side(c, true)",
            "trades.sell" => "KvsRuntime.Side(c, false)",
            "trades.delta" or "delta" => "KvsRuntime.Delta(c)",
            "trades.count" => "(double)c.Ticks",
            "book.bid" => $"KvsRuntime.BookBid(GetDepthSamples(bar), {within})",
            "book.ask" => $"KvsRuntime.BookAsk(GetDepthSamples(bar), {within})",
            "book.delta" => $"KvsRuntime.BookDelta(GetDepthSamples(bar), {within})",
            _ => throw new KvsException(x.Line, x.Column, KvsMessages.InputSource(x.Source)),
        };
        return $"#line {x.Line} \"{linePath}\"\n        S_{x.Name}.Set(bar, {expr});";
    }

    private static string TranslateStatement(Statement s, Context context, string linePath) =>
        $"#line {s.Line} \"{linePath}\"\n        " + StatementBody(s.Text, s, context);

    private static string StatementBody(string text, Statement s, Context context)
    {
        var t = text.Trim().TrimEnd(';').Trim();
        Match m;
        if (t is "{" or "}" or "} else {" or "else {" or "}else{" or "} else" or "else") return t;
        // if (…) { a; b } on one line
        if ((m = Regex.Match(t, @"^(\}\s*)?(else\s+)?if\s*\((.*)\)\s*\{(.+)\}$")).Success)
        {
            var inner = m.Groups[4].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => StatementBody(x, s, context));
            return $"{m.Groups[1].Value}{m.Groups[2].Value}if ({Condition(m.Groups[3].Value, s, context)}) {{ {string.Join(" ", inner)} }}";
        }

        if ((m = Regex.Match(t, @"^(\}\s*)?(else\s+)?if\s*\((.*)\)\s*\{$")).Success)
            return $"{m.Groups[1].Value}{m.Groups[2].Value}if ({Condition(m.Groups[3].Value, s, context)}) {{";
        if ((m = Regex.Match(t, @"^(\}\s*)?(else\s+)?if\s*\((.*)\)\s*([^{].*)$")).Success)
            return $"{m.Groups[1].Value}{m.Groups[2].Value}if ({Condition(m.Groups[3].Value, s, context)}) {{ {StatementBody(m.Groups[4].Value, s, context)} }}";
        if ((m = Regex.Match(t, @"^(?:var|let|const)\s+(\w+)\s*=\s*(.+)$")).Success)
            return $"S_{m.Groups[1].Value}.Set(bar, (double)({Expression(m.Groups[2].Value, s, context)}));";
        if ((m = Regex.Match(t, @"^out_(\w+)\s*\((.*)\)$")).Success)
        {
            if (!context.Series.Contains(m.Groups[1].Value))
                throw new KvsException(s.Line, ColumnOf(context.Raw[s.Line - 1], "out_" + m.Groups[1].Value, s.Column), KvsMessages.NoSuchOutput(m.Groups[1].Value));
            return $"S_{m.Groups[1].Value}.Set(bar, (double)({Expression(m.Groups[2].Value, s, context)}));";
        }

        if ((m = Regex.Match(t, @"^alert\s*\((.*)\)$")).Success)
        {
            string message = $"KvsRuntime.Text({Expression(m.Groups[1].Value, s, context)})";
            return $"{{ string __message = {message}; if (_f.ShouldAlert(bar, __message, lastBar)) AddAlert(\"alert1\", __message); }}";
        }

        if ((m = Regex.Match(t, @"^(\w+)\s*([+\-*/])?=(?!=)\s*(.+)$")).Success)
        {
            var name = m.Groups[1].Value;
            if (!context.Series.Contains(name))
                throw new KvsException(s.Line, ColumnOf(context.Raw[s.Line - 1], name, s.Column), KvsMessages.AssignedUndeclared(name));
            string rhs = Expression(m.Groups[3].Value, s, context);
            string op = m.Groups[2].Value;
            return op.Length == 0 ? $"S_{name}.Set(bar, (double)({rhs}));" : $"S_{name}.Set(bar, S_{name}.Get(bar) {op} (double)({rhs}));";
        }

        throw new KvsException(s.Line, s.Column, KvsMessages.UnexpectedStatement(t));
    }

    private static string Condition(string text, Statement s, Context context) => $"(bool)({Expression(text, s, context)})";

    private static readonly Regex Token = new(@"""[^""]*""|(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?|[A-Za-z_]\w*|&&|\|\||==|!=|<=|>=|[-+*/%()\[\],?:<>!]|\S", RegexOptions.Compiled);

    /// <summary>A KVS expression → C#: series with [n], parameters, functions with their own state slot, and / or / not.</summary>
    private static string Expression(string text, Statement s, Context context)
    {
        var tokens = Token.Matches(text).Select(m => m.Value).ToList();
        var sb = new StringBuilder();
        string raw = context.Raw[s.Line - 1];
        for (int i = 0; i < tokens.Count; i++)
        {
            var tk = tokens[i];
            if (tk[0] == '"')
            {
                sb.Append(Literal(tk[1..^1]));
                continue;
            }

            if (char.IsDigit(tk[0]) || tk[0] == '.' && tk.Length > 1)
            {
                string number = tk[0] == '.' ? "0" + tk : tk.TrimEnd('.');
                sb.Append(number.Contains('.') || number.Contains('e') || number.Contains('E') ? number : number + ".0").Append('d');
                continue;
            }

            if (char.IsLetter(tk[0]) || tk[0] == '_')
            {
                switch (tk)
                {
                    case "and": sb.Append(" && "); continue;
                    case "or": sb.Append(" || "); continue;
                    case "not": sb.Append('!'); continue;
                    case "true" or "false": sb.Append(tk); continue;
                    case "na" when !(i + 1 < tokens.Count && tokens[i + 1] == "("): sb.Append("double.NaN"); continue;
                    case "bar_index": sb.Append("(double)bar"); continue;
                    case "PI": sb.Append("Math.PI"); continue;
                }

                if (i + 1 < tokens.Count && tokens[i + 1] == "(")
                {
                    i = Call(tk, tokens, i, sb, s, context);
                    continue;
                }

                if (context.Series.Contains(tk))
                {
                    if (i + 1 < tokens.Count && tokens[i + 1] == "[")
                    {
                        int end = Matching(tokens, i + 1, "[", "]");
                        if (end < 0) throw new KvsException(s.Line, ColumnOf(raw, tk, s.Column), KvsMessages.MissingBracket(tk));
                        var inner = string.Join(" ", tokens.Skip(i + 2).Take(end - i - 2));
                        sb.Append("S_").Append(tk).Append(".Get(bar, (int)(").Append(Expression(inner, s, context)).Append("))");
                        i = end;
                    }
                    else sb.Append("S_").Append(tk).Append(".Get(bar)");
                    continue;
                }

                if (context.Parameters.TryGetValue(tk, out var p))
                {
                    if (i + 1 < tokens.Count && tokens[i + 1] == "[")
                        throw new KvsException(s.Line, ColumnOf(raw, tk, s.Column), KvsMessages.ParameterHistory(tk));
                    sb.Append("_p_").Append(p.Name);
                    continue;
                }

                throw new KvsException(s.Line, ColumnOf(raw, tk, s.Column), KvsMessages.Undeclared(tk));
            }

            switch (tk)
            {
                case "?": sb.Append(" ? "); break;
                case ":": sb.Append(" : "); break;
                case "&&": sb.Append(" && "); break;
                case "||": sb.Append(" || "); break;
                case "+" or "-" or "*" or "/" or "%" or "(" or ")" or "," or "<" or ">" or "<=" or ">=" or "==" or "!=" or "!": sb.Append(tk); break;
                default: throw new KvsException(s.Line, ColumnOf(raw, tk, s.Column), KvsMessages.UnexpectedToken(tk));
            }
        }

        return sb.ToString();
    }

    private static int Matching(List<string> tokens, int open, string opener, string closer)
    {
        int depth = 0;
        for (int j = open; j < tokens.Count; j++)
        {
            if (tokens[j] == opener) depth++;
            else if (tokens[j] == closer && --depth == 0) return j;
        }

        return -1;
    }

    private static int Call(string name, List<string> tokens, int i, StringBuilder sb, Statement s, Context context)
    {
        string raw = context.Raw[s.Line - 1];
        int column = ColumnOf(raw, name, s.Column);
        int end = Matching(tokens, i + 1, "(", ")");
        if (end < 0) throw new KvsException(s.Line, column, KvsMessages.MissingParenthesis(name));
        var args = new List<string>();
        var current = new List<string>();
        int depth = 0;
        for (int j = i + 2; j < end; j++)
        {
            var x = tokens[j];
            if (x is "(" or "[") depth++;
            else if (x is ")" or "]") depth--;
            if (x == "," && depth == 0)
            {
                args.Add(string.Join(" ", current));
                current.Clear();
                continue;
            }

            current.Add(x);
        }

        if (current.Count > 0 || args.Count > 0) args.Add(string.Join(" ", current));
        if (args.Any(a => a.Trim().Length == 0)) throw new KvsException(s.Line, column, KvsMessages.EmptyArgument(name));
        var translated = args.Select(a => Expression(a, s, context)).ToList();
        if (Windowed.TryGetValue(name, out var method))
        {
            int min = name == "change" ? 1 : 2;
            if (translated.Count < min || translated.Count > 2) throw new KvsException(s.Line, column, KvsMessages.Usage(name == "change" ? "change(value) / change(value, length)" : name + "(value, length)"));
            sb.Append("_f.").Append(method).Append('(').Append(context.Sites++).Append(", bar, (double)(").Append(translated[0]).Append("), ")
                .Append(translated.Count > 1 ? $"(double)({translated[1]})" : "1d").Append(')');
        }
        else if (name == "cum")
        {
            if (translated.Count != 1) throw new KvsException(s.Line, column, KvsMessages.Usage("cum(value)"));
            sb.Append("_f.Cum(").Append(context.Sites++).Append(", bar, (double)(").Append(translated[0]).Append("))");
        }
        else if (name is "crossover" or "crossunder")
        {
            if (translated.Count != 2) throw new KvsException(s.Line, column, KvsMessages.Usage(name + "(a, b)"));
            sb.Append("_f.").Append(name == "crossover" ? "Crossover" : "Crossunder").Append('(').Append(context.Sites++)
                .Append(", bar, (double)(").Append(translated[0]).Append("), (double)(").Append(translated[1]).Append("))");
        }
        else if (MathFunctions.TryGetValue(name, out var function))
        {
            if (translated.Count < function.MinArgs || translated.Count > function.MaxArgs)
                throw new KvsException(s.Line, column, KvsMessages.ArgumentCount(name, function.MinArgs == function.MaxArgs
                    ? function.MinArgs.ToString(CultureInfo.InvariantCulture) : function.MinArgs.ToString(CultureInfo.InvariantCulture) + "–" + function.MaxArgs.ToString(CultureInfo.InvariantCulture)));
            sb.Append(function.Target).Append('(').Append(string.Join(", ", translated.Select(a => $"(double)({a})"))).Append(')');
        }
        else throw new KvsException(s.Line, column, KvsMessages.UnknownFunction(name, FunctionList));

        return end;
    }
}
