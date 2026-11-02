using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;

namespace TradingHub.App.Localization;

/// <summary>
/// Translates canonical English display text at control and Skia boundaries. Catalogs include fixed text and
/// numbered data templates; missing entries remain English. Language changes take effect after a safe restart.
/// </summary>
public static class Lang
{
    public const string Spanish = "es", English = "en", Chinese = "zh-CN";

    public static IReadOnlyList<(string Code, string Name)> Available { get; } = new[]
    {
        (English, "English"),
        (Spanish, "Español"),
        (Chinese, "中文（简体）"),
    };

    public static string Current { get; private set; } = English;

    private static readonly Dictionary<string, string> Exact = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Upper = new(StringComparer.Ordinal);
    private static readonly Dictionary<(char, char, char), List<Template>> ByStart = new();
    private static readonly List<Template> Loose = new();
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static readonly Regex Hole = new(@"(?<!\{)\{(\d+)\}(?!\})", RegexOptions.CultureInvariant);
    private const int PrefixLength = 3;
    private sealed record Template(Regex Pattern, string Output, int Literal);

    /// <summary>Existing English-fork settings without Language remain English; new installs follow the OS.</summary>
    public static string Default(bool existingUser)
    {
        if (existingUser) return English;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "es" => Spanish,
            "zh" => Chinese,
            _ => English,
        };
    }

    /// <summary>Load before constructing either the desktop UI or MCP services.</summary>
    public static void Load(string? code)
    {
        Dictionary<string, string>? map = null;
        if (code is Spanish or Chinese)
        {
            using var stream = typeof(Lang).Assembly.GetManifestResourceStream($"TradingHub.App.Localization.{code}.json");
            if (stream != null) map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        Use(code, map ?? new Dictionary<string, string>());
        var culture = TradingHub.Common.UiCulture.Current;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
    }

    internal static void Use(string? code, IReadOnlyDictionary<string, string> map)
    {
        lock (Gate)
        {
            Current = code is English or Spanish or Chinese ? code : English;
            Exact.Clear();
            Upper.Clear();
            ByStart.Clear();
            Loose.Clear();
            Cache.Clear();
            if (Current != English)
            {
                foreach (var (key, value) in map)
                {
                    if (string.IsNullOrEmpty(value) || value == key && !HasHoles(key)) continue;
                    // Reject malformed entries rather than losing or inventing user data at runtime.
                    if (!Hole.Matches(key).Select(m => m.Groups[1].Value).Order().SequenceEqual(
                            Hole.Matches(value).Select(m => m.Groups[1].Value).Order())) continue;
                    if (!HasHoles(key))
                    {
                        Exact[Unescape(key)] = Unescape(value);
                        Upper.TryAdd(Unescape(key).ToUpperInvariant(), Unescape(value));
                        continue;
                    }
                    var template = Compile(key, value);
                    if (template == null) continue;
                    var start = LiteralStart(key);
                    if (start.Length >= PrefixLength)
                    {
                        var prefix = (start[0], start[1], start[2]);
                        if (!ByStart.TryGetValue(prefix, out var list)) ByStart[prefix] = list = new List<Template>();
                        list.Add(template);
                    }
                    else Loose.Add(template);
                }
                // Specific messages win over generic templates, deterministically.
                foreach (var list in ByStart.Values) list.Sort((a, b) => b.Literal.CompareTo(a.Literal));
                Loose.Sort((a, b) => b.Literal.CompareTo(a.Literal));
            }
            TradingHub.Common.UiCulture.Current = TradingHub.Common.UiCulture.For(Current);
            TradingHub.Rendering.UiText.Translate = Current == English ? null : Translate;
        }
    }

    /// <summary>Append last: selected prose language overrides older English-only reply instructions, never code.</summary>
    public static string ForClaude => "\n\nIMPORTANT LANGUAGE OVERRIDE: The user's Trading Hub interface language is " +
        (Current == Spanish ? "Spanish. Reply and write report prose in Spanish" :
         Current == Chinese ? "Simplified Chinese. Reply and write report prose in Simplified Chinese" :
         "English. Reply and write report prose in English") +
        ", overriding any earlier instruction about the reply language. Generated C# code, filenames, class names, " +
        "identifiers, parameter names and code comments MUST remain English in every interface language. " +
        "Preserve existing user code names unless the user explicitly requests renaming.";

    /// <summary>Never start a replacement until the existing protection-aware quit flow accepts quitting.</summary>
    public static void Restart()
    {
        if (App.Quit is { } quit) _ = quit.QuitAsync(beforeExit: LaunchReplacement);
    }

    private static void LaunchReplacement()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var commandLine = Environment.GetCommandLineArgs();
            // A framework-dependent invocation needs its managed entry point as well as the app arguments.
            var arguments = commandLine.AsSpan(Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
            if (OperatingSystem.IsMacOS() && exe.Contains(".app/Contents/MacOS/", StringComparison.Ordinal))
            {
                var bundle = exe[..(exe.IndexOf(".app/", StringComparison.Ordinal) + 4)];
                using var replacement = System.Diagnostics.Process.Start(ReplacementStartInfo(Environment.ProcessId, "/usr/bin/open", arguments, bundle));
            }
            else
            {
                using var replacement = System.Diagnostics.Process.Start(ReplacementStartInfo(Environment.ProcessId, exe, arguments));
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    // Per-call process boundary also lets regression tests use disposable marker executables, never the app.
    internal static System.Diagnostics.ProcessStartInfo ReplacementStartInfo(int predecessorId, string executable,
        ReadOnlySpan<string> arguments = default, string? bundle = null)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows PowerShell 5 uses .NET Framework, whose ProcessStartInfo has no ArgumentList.
            // Quote native argv, then quote the resulting literal for PowerShell; never evaluate app arguments.
            var commandLine = new StringBuilder();
            foreach (var argument in arguments)
            {
                if (commandLine.Length > 0) commandLine.Append(' ');
                commandLine.Append('"');
                int backslashes = 0;
                foreach (var character in argument)
                {
                    if (character == '\\') { backslashes++; continue; }
                    commandLine.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes).Append(character);
                    backslashes = 0;
                }
                commandLine.Append('\\', backslashes * 2).Append('"');
            }
            var script = "$ErrorActionPreference='Stop'; $previous=$null; try { $previous=[System.Diagnostics.Process]::GetProcessById(" +
                predecessorId.ToString(CultureInfo.InvariantCulture) + ") } catch [System.ArgumentException] {};" +
                " if ($null -ne $previous) { try { $previous.WaitForExit() } finally { $previous.Dispose() } };" +
                " $start=New-Object System.Diagnostics.ProcessStartInfo; $start.UseShellExecute=$false; $start.FileName='" +
                executable.Replace("'", "''") + "'; $start.Arguments='" + commandLine.ToString().Replace("'", "''") +
                "'; $child=[System.Diagnostics.Process]::Start($start); if ($null -ne $child) { $child.Dispose() }";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { CreateNoWindow = true, UseShellExecute = false };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(encoded);
            return start;
        }
        var shell = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        shell.ArgumentList.Add("-c");
        // Signal zero only observes existence. The polling interval never authorizes an early replacement.
        shell.ArgumentList.Add("while kill -0 \"$1\" 2>/dev/null; do /bin/sleep 0.1; done; shift; exec \"$@\"");
        shell.ArgumentList.Add("TradingHubRestart");
        shell.ArgumentList.Add(predecessorId.ToString(CultureInfo.InvariantCulture));
        shell.ArgumentList.Add(executable);
        if (bundle != null)
        {
            shell.ArgumentList.Add("-n");
            shell.ArgumentList.Add("-a");
            shell.ArgumentList.Add(bundle);
            if (!arguments.IsEmpty) shell.ArgumentList.Add("--args");
        }
        foreach (var argument in arguments) shell.ArgumentList.Add(argument);
        return shell;
    }

    public static string T(string text) => Translate(text) ?? text;

    public static string? Translate(string? text)
    {
        if (Current == English || string.IsNullOrEmpty(text)) return null;
        for (int i = 0; i < Available.Count; i++)
            if (Available[i].Name == text) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(text, out var cached)) return cached;
            var result = Find(text);
            // Prices, times and other data change continuously; bound retained display text.
            if (Cache.Count > 20_000) Cache.Clear();
            Cache[text] = result;
            return result;
        }
    }

    private static string? Find(string text)
    {
        if (Exact.TryGetValue(text, out var hit)) return hit;
        if (!HasLetter(text)) return null;
        if (!HasLower(text) && Upper.TryGetValue(text, out var upper)) return upper.ToUpperInvariant();
        if (char.IsLower(text[0]) && Part(text) is var part && !ReferenceEquals(part, text)) return part;
        var trimmed = text.Trim();
        if (trimmed.Length != text.Length && trimmed.Length > 0 && Find(trimmed) is { } inner)
        {
            int lead = text.IndexOf(trimmed, StringComparison.Ordinal);
            return text[..lead] + inner + text[(lead + trimmed.Length)..];
        }
        return Match(text) ?? Pieces(text, "\n") ?? Pieces(text, " · ");
    }

    private static string? Pieces(string text, string separator)
    {
        if (!text.Contains(separator, StringComparison.Ordinal)) return null;
        var parts = text.Split(separator);
        bool any = false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (Find(parts[i]) is not { } translated) continue;
            parts[i] = translated;
            any = true;
        }
        return any ? string.Join(separator, parts) : null;
    }

    private static string? Match(string text)
    {
        if (text.Length >= PrefixLength && ByStart.TryGetValue((text[0], text[1], text[2]), out var list)
            && Match(text, list) is { } translated) return translated;
        return Match(text, Loose);
    }

    private static string? Match(string text, List<Template> candidates)
    {
        foreach (var template in candidates)
        {
            var match = template.Pattern.Match(text);
            if (!match.Success) continue;
            return Hole.Replace(template.Output, hole => Part(match.Groups["p" + hole.Groups[1].Value].Value))
                .Replace("{{", "{").Replace("}}", "}");
        }
        return null;
    }

    private static string Part(string value)
    {
        if (value.Length == 0) return value;
        if (Exact.TryGetValue(value, out var hit)) return hit;
        if (!HasLetter(value)) return value;
        if (!HasLower(value) && Upper.TryGetValue(value, out var caps)) return caps.ToUpperInvariant();
        if (char.IsLower(value[0]) && Exact.TryGetValue(char.ToUpperInvariant(value[0]) + value[1..], out var upper))
            return upper.Length > 0 && char.IsUpper(upper[0]) ? char.ToLowerInvariant(upper[0]) + upper[1..] : upper;
        // Nested status/error templates can themselves contain known display text.
        return Match(value) ?? Pieces(value, "\n") ?? Pieces(value, " · ") ?? value;
    }

    private static Template? Compile(string key, string output)
    {
        try
        {
            // Near-empty templates may only consume numbers, not arbitrary user/provider prose.
            string hole = Hole.Replace(key, "").Count(char.IsLetter) < 3 ? @"[-+−]?\d[\d.,]*" : ".*?";
            var pattern = new StringBuilder("^");
            var seen = new HashSet<string>();
            int literal = 0;
            foreach (Match token in Regex.Matches(key, @"\{\{|\}\}|\{(\d+)\}|[^{}]+|[{}]"))
            {
                if (token.Groups[1].Success)
                {
                    var name = "p" + token.Groups[1].Value;
                    pattern.Append(seen.Add(name) ? $"(?<{name}>{hole})" : $@"\k<{name}>");
                }
                else
                {
                    var text = Unescape(token.Value);
                    pattern.Append(Regex.Escape(text));
                    literal += text.Length;
                }
            }
            if (literal < 2) return null;
            pattern.Append('$');
            return new Template(new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), output, literal);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool HasLetter(string text)
    {
        foreach (char value in text)
            if (char.IsLetter(value)) return true;
        return false;
    }

    private static bool HasLower(string text)
    {
        foreach (char value in text)
            if (char.IsLower(value)) return true;
        return false;
    }

    private static bool HasHoles(string key) => Hole.IsMatch(key);
    private static string Unescape(string text) => text.Replace("{{", "{").Replace("}}", "}");
    private static string LiteralStart(string key) => Unescape(Regex.Match(key, @"^((?:\{\{|\}\}|[^{}])*)").Groups[1].Value);

    [ThreadStatic] private static bool _applying;
    private static bool _installed;

    /// <summary>Install once before UI creation, including every upstream control boundary.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Hook<TextBlock>(TextBlock.TextProperty);
        Hook<Run>(Run.TextProperty);
        Hook<ContentControl>(ContentControl.ContentProperty);
        Hook<HeaderedContentControl>(HeaderedContentControl.HeaderProperty);
        Hook<HeaderedItemsControl>(HeaderedItemsControl.HeaderProperty);
        Hook<HeaderedSelectingItemsControl>(HeaderedSelectingItemsControl.HeaderProperty);
        Hook<TextBox>(TextBox.WatermarkProperty);
        Hook<ComboBox>(ComboBox.PlaceholderTextProperty);
        Hook<Window>(Window.TitleProperty);
        Hook<NativeMenuItem>(NativeMenuItem.HeaderProperty);
        Hook<TrayIcon>(TrayIcon.ToolTipTextProperty);
        Hook<Control>(ToolTip.TipProperty);
        Hook<ToggleSwitch>(ToggleSwitch.OnContentProperty);
        Hook<ToggleSwitch>(ToggleSwitch.OffContentProperty);
    }

    private static void Hook<T>(AvaloniaProperty property) where T : AvaloniaObject
    {
        property.Changed.AddClassHandler<T>((control, change) =>
        {
            if (_applying || change.NewValue is not string source || Translate(source) is not { } translated || translated == source) return;
            _applying = true;
            try { control.SetCurrentValue(property, translated); }
            finally { _applying = false; }
        });
    }
}
