using System.Diagnostics;
using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradingHub.App.Localization;
using TradingHub.Rendering;

namespace TradingHub.Tests;

[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection { }

/// <summary>Display translation and migration are process-wide, so these tests run without other collections.</summary>
[Collection("Language")]
public sealed class LangTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public void Dispose()
    {
        Lang.Load(Lang.English);
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_replacement_waits_for_predecessor_exit_even_when_shutdown_exceeds_old_delay(bool predecessorAlreadyExited)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TradingHub restart ' & $ 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "replacement-ran");
        var replacement = Path.Combine(directory, OperatingSystem.IsWindows() ? "replacement.ps1" : "replacement.sh");
        string[] payload = ["space value", "apostrophe's; & $() ` %PATH%", "日本語", "", "double\"quote", @"trailing \", "--flag", "line\nbreak"];
        string replacementExecutable = replacement;
        string[] replacementArguments = payload;
        var predecessorStart = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        if (OperatingSystem.IsWindows())
        {
            // Inspect native argv, not PowerShell's script binder, which has its own empty-string behavior.
            await File.WriteAllTextAsync(replacement, """
                $argv = [Environment]::GetCommandLineArgs()
                $temporary = Join-Path $PSScriptRoot 'replacement-ran.tmp'
                $marker = Join-Path $PSScriptRoot 'replacement-ran'
                [IO.File]::WriteAllText($temporary, ([string]::Join([string][char]0, $argv[7..($argv.Length - 1)]) + [char]0), (New-Object Text.UTF8Encoding($false)))
                [IO.File]::Move($temporary, $marker)
                """);
            replacementExecutable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            replacementArguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", replacement, .. payload];
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                         Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::WriteLine('ready'); [Console]::ReadLine() | Out-Null")) })
                predecessorStart.ArgumentList.Add(argument);
        }
        else
        {
            await File.WriteAllTextAsync(replacement, "#!/bin/sh\nprintf '%s\\0' \"$@\" > \"${0%/*}/replacement-ran.tmp\"\n/bin/mv \"${0%/*}/replacement-ran.tmp\" \"${0%/*}/replacement-ran\"\n");
            File.SetUnixFileMode(replacement, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            predecessorStart.ArgumentList.Add("-c");
            predecessorStart.ArgumentList.Add("printf 'ready\\n'; read release");
        }
        using var predecessor = Process.Start(predecessorStart)!;
        Process? waiter = null;
        try
        {
            Assert.Equal("ready", await predecessor.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
            if (predecessorAlreadyExited)
            {
                predecessor.StandardInput.Close();
                await predecessor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            var waiterStart = Lang.ReplacementStartInfo(predecessor.Id, replacementExecutable, replacementArguments);
            waiterStart.RedirectStandardError = true;
            waiter = Process.Start(waiterStart);
            Assert.NotNull(waiter);
            if (!predecessorAlreadyExited)
            {
                // A pinned process, not a guessed shutdown duration: it cannot exit until this test releases stdin.
                var observation = Stopwatch.StartNew();
                while (observation.Elapsed < TimeSpan.FromSeconds(4))
                {
                    Assert.False(predecessor.HasExited);
                    Assert.False(File.Exists(marker), "Replacement ran before the predecessor exited.");
                    await Task.Delay(25);
                }
                await predecessor.StandardInput.WriteLineAsync("release");
                await predecessor.StandardInput.FlushAsync();
                await predecessor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            await waiter!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(waiter.ExitCode == 0, await waiter.StandardError.ReadToEndAsync());
            var launched = Stopwatch.StartNew();
            while (!File.Exists(marker) && launched.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
            Assert.True(File.Exists(marker), "Replacement never ran after the predecessor exited.");
            Assert.Equal(string.Join('\0', payload) + '\0', await File.ReadAllTextAsync(marker));
        }
        finally
        {
            if (!predecessor.HasExited)
            {
                predecessor.StandardInput.Close();
                await predecessor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            if (waiter != null)
            {
                await waiter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                waiter.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Templates_preserve_data_reorder_repeated_holes_and_literal_braces()
    {
        Lang.Use(Lang.Spanish, new Dictionary<string, string>
        {
            ["Save"] = "Guardar",
            ["Cancel"] = "Cancelar",
            ["Market"] = "Mercado",
            ["Connection {0}"] = "Conexión {0}",
            ["Connection “{0}” saved"] = "Conexión «{0}» guardada",
            ["Buy {0}"] = "Comprar {0}",
            ["Order {0} for {1}"] = "Orden de {1}: {0}",
            ["Order {0}: {0}"] = "Orden {0}: {0}",
            ["{0} of {1}"] = "{0} de {1}",
            ["Use {{x}} and {0}"] = "Usá {{x}} y {0}",
            ["Chart legend"] = "Leyenda del gráfico",
            ["Red"] = "Rojo",
            ["Linked to group {0}"] = "Vinculado al grupo {0}",
            ["Value {0}"] = "Value {0}",
            ["Could not submit: {0}"] = "No se pudo enviar: {0}",
        });
        Assert.Equal("Conexión «Rithmic» guardada", Lang.T("Connection “Rithmic” saved"));
        Assert.Equal("Conexión Rithmic", Lang.T("Connection Rithmic"));
        Assert.Equal("Orden de Ana: 7", Lang.T("Order 7 for Ana"));
        Assert.Equal("Orden 7: 7", Lang.T("Order 7: 7"));
        Assert.Equal("Order 7: 8", Lang.T("Order 7: 8"));
        Assert.Equal("12 de 30", Lang.T("12 of 30"));
        Assert.Equal("Breakout of range", Lang.T("Breakout of range"));
        Assert.Equal("Comprar Mercado", Lang.T("Buy Market"));
        Assert.Equal("Value Mercado", Lang.T("Value Market"));
        Assert.Equal("No se pudo enviar: Orden de Ana: 7", Lang.T("Could not submit: Order 7 for Ana"));
        Assert.Equal("Usá {x} y 5", Lang.T("Use {x} and 5"));
        Assert.Equal("  Guardar ", Lang.T("  Save "));
        Assert.Equal("Guardar\nCancelar", Lang.T("Save\nCancel"));
        Assert.Equal("RITHMIC · GUARDAR", Lang.T("RITHMIC · SAVE"));
        Assert.Equal("Vinculado al grupo rojo", Lang.T("Linked to group red"));
        Assert.Equal("LEYENDA DEL GRÁFICO", Lang.T("CHART LEGEND"));
        Assert.Equal("Unknown user text", Lang.T("Unknown user text"));
        Assert.Null(Lang.Translate("87456.25"));
        Assert.Null(Lang.Translate(null));
        Assert.Null(Lang.Translate("Español"));
        Assert.Equal("Leyenda del gráfico", UiText.Tr("Chart legend"));
    }

    [Fact]
    public void Invalid_template_cannot_drop_or_fabricate_user_data()
    {
        Lang.Use(Lang.Spanish, new Dictionary<string, string>
        {
            ["Order {0} for {1}"] = "Orden {0}",
            ["Balance {0}"] = "Saldo {0} en {1}",
        });
        Assert.Equal("Order 7 for Ana", Lang.T("Order 7 for Ana"));
        Assert.Equal("Balance 123", Lang.T("Balance 123"));
    }

    [Theory]
    [InlineData(Lang.Spanish, "Guardar", "Ajustes", "Conexiones", "Datos", "Estrategias", "Leyenda del gráfico")]
    [InlineData(Lang.Chinese, "保存", "设置", "连接", "数据", "策略", "图表图例")]
    public void Real_catalogs_cover_navigation_controls_and_Skia_text(string code, string save, string settings,
        string connections, string data, string strategies, string legend)
    {
        Lang.Load(code);
        Assert.Equal(save, Lang.T("Save"));
        Assert.Equal(settings, Lang.T("Settings"));
        Assert.Equal(connections, Lang.T("Connections"));
        Assert.Equal(data, Lang.T("Data"));
        Assert.Equal(strategies, Lang.T("Strategies"));
        Assert.Equal(legend, UiText.Tr("Chart legend"));
        Assert.Equal("New provider message", Lang.T("New provider message"));
    }

    [Fact]
    public void Real_catalog_plural_variants_are_derived_from_source_conditions_not_suffix_guesses()
    {
        Lang.Load(Lang.Spanish);
        Assert.Equal("1 error", Lang.T("1 error"));
        Assert.Equal("32 errores", Lang.T("32 errors"));
        Assert.Equal("1 ejecutada hoy", Lang.T("1 filled today"));
        Assert.Equal("4 ejecutadas hoy", Lang.T("4 filled today"));
        Assert.Equal("Conexión \"Rithmic\" guardada", Lang.T("Connection \"Rithmic\" saved"));
        Lang.Load(Lang.Chinese);
        Assert.Equal("1 个错误", Lang.T("1 error"));
        Assert.Equal("32 个错误", Lang.T("32 errors"));
        Assert.Equal("今日成交 4 笔", Lang.T("4 filled today"));
        Assert.Equal("连接“Rithmic”已保存", Lang.T("Connection \"Rithmic\" saved"));
    }

    [Fact]
    public void Stop_order_type_is_distinct_from_actions_and_candle_prices_are_not_window_verbs()
    {
        Lang.Load(Lang.Chinese);
        Assert.Equal("止单", Lang.T("Stop"));
        Assert.Equal("停止策略", Lang.T("Stop strategy"));
        Assert.Equal("停止回放", Lang.T("Stop replay"));
        Assert.Equal("停止录制", Lang.T("Stop recording"));
        Assert.Equal("开盘价", Lang.T("Open price"));
        Assert.Equal("收盘价", Lang.T("Close price"));
        Assert.Equal("平仓", Lang.T("Close position"));
        Assert.Equal("关闭", Lang.T("Close"));
        Lang.Load(Lang.Spanish);
        Assert.Equal("Stop", Lang.T("Stop"));
        Assert.Equal("Detener estrategia", Lang.T("Stop strategy"));
        Assert.Equal("Precio de apertura", Lang.T("Open price"));
        Assert.Equal("Precio de cierre", Lang.T("Close price"));
        Assert.Equal("Cerrar posición", Lang.T("Close position"));
    }

    [Fact]
    public void Existing_English_users_do_not_silently_change_language_but_new_installs_follow_the_OS()
    {
        foreach (var (culture, expectedNewLanguage) in new[]
        {
            ("zh-CN", Lang.Chinese), ("es-AR", Lang.Spanish), ("en-GB", Lang.English), ("de-DE", Lang.English),
        })
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(Lang.English, Lang.Default(existingUser: true));
            Assert.Equal(expectedNewLanguage, Lang.Default(existingUser: false));
        }
        Lang.Load(null);
        Assert.Equal(Lang.English, Lang.Current);
        Assert.Equal("Save", Lang.T("Save"));
        Assert.Null(UiText.Translate);
        Lang.Load("unknown-language");
        Assert.Equal(Lang.English, Lang.Current);
    }

    [Theory]
    [InlineData(Lang.English, "en-US", "1,234.5")]
    [InlineData(Lang.Spanish, "es-AR", "1.234,5")]
    [InlineData(Lang.Chinese, "zh-CN", "1,234.5")]
    public void Switching_languages_updates_display_current_and_default_thread_cultures(string code, string name, string number)
    {
        // Starting from another selected language catches stale cultures, notably switching back to Spanish.
        Lang.Load(Lang.Chinese);
        Lang.Load(code);
        Assert.Equal(name, TradingHub.Common.UiCulture.Current.Name);
        Assert.Equal(name, CultureInfo.CurrentCulture.Name);
        Assert.Equal(name, CultureInfo.CurrentUICulture.Name);
        Assert.Equal(name, CultureInfo.DefaultThreadCurrentCulture?.Name);
        Assert.Equal(name, CultureInfo.DefaultThreadCurrentUICulture?.Name);
        Assert.Equal(number, 1234.5.ToString("#,##0.##"));
    }

    [Fact]
    public void Real_catalogs_preserve_all_upstream_aliases_and_checkpoint_fork_correspondence_and_holes()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "TradingHub.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "tools", "TradingHub.I18n", "provenance.json")));
        foreach (var code in new[] { Lang.Spanish, Lang.Chinese })
        {
            using var stream = typeof(Lang).Assembly.GetManifestResourceStream($"TradingHub.App.Localization.{code}.json");
            Assert.NotNull(stream);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream!)!;
            static string Holes(string value) => string.Join(",", Regex.Matches(value, @"(?<!\{)\{(\d+)\}(?!\})")
                .Select(m => m.Groups[1].Value).OrderBy(x => x, StringComparer.Ordinal));
            foreach (var (key, value) in map) Assert.Equal(Holes(key), Holes(value));
            foreach (var upstream in provenance.RootElement.GetProperty("entries").EnumerateObject())
            {
                var keys = upstream.Value.GetProperty("aliases").EnumerateArray()
                    .Concat(upstream.Value.GetProperty("correspondence").EnumerateArray())
                    .Select(item => item.GetProperty("key").GetString()!).Distinct(StringComparer.Ordinal).ToArray();
                Assert.NotEmpty(keys);
                foreach (var key in keys) Assert.True(map.ContainsKey(key), $"{code}: lost {upstream.Name} → {key}");
            }
        }
    }
}
