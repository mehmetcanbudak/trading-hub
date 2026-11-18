using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Kvs;
using TradingHub.Market;
using TradingHub.Scripting;

namespace TradingHub.Tests;

/// <summary>
/// KVS review regressions: a broken script is a diagnostic and never an exception, a live update of the forming bar gives
/// what a full recalculation gives, and script text and file names always stay data inside the generated C#.
/// </summary>
public sealed class KvsReviewScriptTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int Bars = 30;
    private const string Basic = "output x = line\nonBar {\n  x = close\n}";
    private readonly string _dir = Directory.CreateTempSubdirectory("kvs-review-").FullName;
    private readonly List<IDisposable> _owned = new();
    private long _tradeId = 1;

    public void Dispose()
    {
        foreach (var owned in Enumerable.Reverse(_owned)) owned.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Bar i trades only at 100 + i, so every bar opens where it closes.</summary>
    private (ChartModel Model, InstrumentData Data) Market()
    {
        var data = new InstrumentData(new Instrument { Symbol = "KVS-REVIEW", FeedId = "sim", TickSize = 1, VolumeStep = 1 }, new SimulatedFeed());
        for (int i = 0; i < Bars; i++)
        {
            data.Trades.Add(new Trade(_tradeId++, Start.AddMinutes(i), 100 + i, 1, TradeSide.Buy));
            data.Trades.Add(new Trade(_tradeId++, Start.AddMinutes(i).AddSeconds(30), 100 + i, 1, TradeSide.Sell));
        }

        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        _owned.Add(data);
        _owned.Add(model);
        return (model, data);
    }

    /// <summary>One more print in the given bar, then the chart's ordinary update.</summary>
    private void Tick(InstrumentData data, ChartModel model, int bar, int second, decimal price)
    {
        data.Trades.Add(new Trade(_tradeId++, Start.AddMinutes(bar).AddSeconds(second), price, 1, TradeSide.Buy));
        model.Update();
    }

    private string Write(string name, string source, string? folder = null)
    {
        string directory = folder ?? _dir;
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + KvsTranslator.Extension);
        File.WriteAllText(path, source);
        return path;
    }

    private Indicator Run(string source, string name, ChartModel model)
    {
        var result = new ScriptCompiler().Compile(new[] { Write(name, source) }, "KvsReview");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var indicator = (Indicator)Activator.CreateInstance(result.Assembly!.GetType(KvsTranslator.FullTypeNameFor(name))!)!;
        Assert.Null(model.AddIndicator(indicator, applyDefaultColors: false).Error);
        return indicator;
    }

    private static decimal Value(Indicator indicator, int series, int bar) => ((ValueDataSeries)indicator.DataSeries[series])[bar];

    private ScriptDiagnostic FirstError(string source, string name, string? folder = null)
    {
        string path = Write(name, source, folder);
        var result = new ScriptCompiler().Compile(new[] { path }, "KvsReviewBad");
        Assert.False(result.Success);
        var error = result.Diagnostics.First(d => d.Severity == "error");
        Assert.Equal(path, error.File);
        return error;
    }

    [Theory]
    [InlineData("output x = line\nonBar {\n alert(\"x)\n}", 3, 8)]
    [InlineData("output x = line\nonBar {\n  x = \"a\n}", 3, 7)]
    public void An_unterminated_text_is_a_script_diagnostic_and_never_an_exception(string source, int line, int column)
    {
        string path = Write("Unterminated", source);
        ScriptCompileResult? result;
        // the startup compilation: a typo in one script must be reported, never thrown out of the app's start
        using (var scripts = new TradingHub.App.Services.UserScripts(new TradingHub.App.Services.IndicatorCatalog(), _dir, _ => { }))
            result = scripts.CompileAll();
        Assert.NotNull(result);
        Assert.False(result!.Success);
        var error = Assert.Single(result.Diagnostics, d => d.Severity == "error");
        Assert.Equal((path, "KVS", line, column), (error.File, error.Id, error.Line, error.Column));
        Assert.Contains("closing quote", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Recalculating_the_forming_bar_keeps_nothing_an_earlier_update_assigned()
    {
        var (model, data) = Market();
        var indicator = Run("""
            output signal = line
            output updates = line
            onBar {
              if (close > open) { signal = 1 }
              var seen = nz(seen) + 1
              updates = seen
            }
            """, "Forming", model);
        int last = Bars - 1;
        Assert.Equal(0m, Value(indicator, 0, last));
        Assert.Equal(1m, Value(indicator, 1, last));

        // one update lifts the forming bar's close above its open, the next one drops it below
        Tick(data, model, last, 40, 131);
        indicator.HostCalculate(last); // the host may recalculate the forming bar any number of times
        Assert.Equal(1m, Value(indicator, 0, last));
        Assert.Equal(1m, Value(indicator, 1, last));
        Tick(data, model, last, 50, 127);
        indicator.HostCalculate(last);
        Assert.Equal(0m, Value(indicator, 0, last));
        Assert.Equal(1m, Value(indicator, 1, last));

        indicator.HostRecalculate(); // exactly what a reload shows
        Assert.Equal(0m, Value(indicator, 0, last));
        Assert.Equal(1m, Value(indicator, 1, last));
    }

    [Fact]
    public void Sign_of_a_missing_value_is_missing_instead_of_an_aborted_indicator()
    {
        var (model, _) = Market();
        var indicator = Run("""
            output up = line
            output down = line
            onBar {
              up = nz(sign(change(close)), 5)
              down = sign(close[1] - close)
            }
            """, "Signs", model);
        Assert.Equal(5m, Value(indicator, 0, 0)); // bar 0 has no change: na, and the following bars still calculate
        Assert.Equal(1m, Value(indicator, 0, Bars - 1));
        Assert.Equal(-1m, Value(indicator, 1, Bars - 1));
    }

    [Fact]
    public void Text_names_and_folders_containing_generator_placeholders_are_kept_as_written()
    {
        string folder = Path.Combine(_dir, "OPPOSITES");
        string path = Write("Sites", """
            name "TOP SITES"
            param SITES = 2
            output x = line { label: "SITES" }
            onBar {
              x = ema(close, SITES)
            }
            """, folder);
        var result = new ScriptCompiler().Compile(new[] { path }, "KvsSites");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var type = result.Assembly!.GetType(KvsTranslator.FullTypeNameFor("Sites"))!;
        Assert.Equal("TOP SITES", type.GetCustomAttribute<DisplayNameAttribute>()!.DisplayName);
        Assert.NotNull(type.GetProperty("SITES"));
        Assert.Equal("SITES", ((ValueDataSeries)((Indicator)Activator.CreateInstance(type)!).DataSeries[0]).Name);

        var error = FirstError("output x = line\nonBar {\n  x = close > open\n}", "Typed", folder);
        Assert.StartsWith("CS", error.Id);
        Assert.Equal((3, 3), (error.Line, error.Column));
    }

    [Fact]
    public void Doubled_signs_negate_instead_of_lexing_as_decrement()
    {
        var (model, _) = Market();
        var indicator = Run("""
            output a = line
            output b = line
            onBar {
              a = close - -1
              b = close[1] - -close[2] + +1
            }
            """, "Doubled", model);
        Assert.Equal(130m, Value(indicator, 0, Bars - 1));
        Assert.Equal(128m + 127m + 1m, Value(indicator, 1, Bars - 1));
    }

    [Fact]
    public void Scripts_compiling_to_one_type_are_reported_on_the_later_file_and_generated_code_maps_to_line_one()
    {
        string first = Write("x", Basic, Path.Combine(_dir, "a"));
        string second = Write("x", Basic, Path.Combine(_dir, "b"));
        var result = new ScriptCompiler().Compile(new[] { first, second }, "KvsSameType");
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics, d => d.Severity == "error");
        Assert.Equal((second, "KVS", 1, 1), (error.File, error.Id, error.Line, error.Column));
        Assert.Contains(first, error.Message, StringComparison.Ordinal);

        // a C# type shadowing the runtime breaks the generated constructor and methods, which have no script line
        string shadow = Path.Combine(_dir, "Shadow.cs");
        File.WriteAllText(shadow, "namespace TradingHub.UserScripts.Kvs { public sealed class KvsRuntime { } }");
        var shadowed = new ScriptCompiler().Compile(new[] { shadow, first }, "KvsShadowed");
        Assert.False(shadowed.Success);
        var generated = shadowed.Diagnostics.Where(d => d.File == first).ToList();
        Assert.NotEmpty(generated);
        Assert.All(generated, d => Assert.Equal((1, 1), (d.Line, d.Column)));
    }

    [Fact]
    public void Line_separators_in_script_text_and_file_names_stay_inside_literals_comments_and_directives()
    {
        const string separators = "\u2028#endif\u2029\u0085public static class Injected { }\u2028#if NEVER\u2028";
        string name = "a\u2028#if NEVER\u2028b";
        string path = Path.Combine(_dir, name + KvsTranslator.Extension);
        string source = $"name \"line{separators}two\"\noutput x = line {{ label: \"la{separators}bel\" }}\nonBar {{\n  x = close\n  alert(\"al{separators}ert \" + close)\n}}";
        var result = new ScriptCompiler().CompileSources([new ScriptSource(path, source)], "KvsSeparators");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var type = Assert.Single(result.Assembly!.GetTypes(), t => t.IsPublic);
        Assert.Equal(KvsTranslator.FullTypeNameFor(name), type.FullName);
        Assert.Equal($"line{separators}two", type.GetCustomAttribute<DisplayNameAttribute>()!.DisplayName);
        Assert.Equal($"la{separators}bel", ((ValueDataSeries)((Indicator)Activator.CreateInstance(type)!).DataSeries[0]).Name);
        Assert.DoesNotContain(result.Assembly.GetTypes(), t => t.Name == "Injected");
    }

    [Fact]
    public void A_missing_or_negative_history_offset_reads_missing_instead_of_this_or_a_future_bar()
    {
        var (model, _) = Market();
        var indicator = Run("""
            output flags = line
            onBar {
              flags = (na(close[na]) ? 1 : 0) + (na(close[-1]) ? 10 : 0) + (na(close[1]) ? 100 : 0)
            }
            """, "Offsets", model);
        Assert.Equal(11m, Value(indicator, 0, 5));
        indicator.HostCalculate(5); // later bars already hold values now: a recalculation must not read them
        Assert.Equal(11m, Value(indicator, 0, 5));
    }

    [Fact]
    public void Round_and_window_lengths_take_halves_away_from_zero()
    {
        var (model, _) = Market();
        var indicator = Run("""
            output r = line
            output s = line
            onBar {
              r = round(2.5) * 100 + round(-0.5) * 10 + round(0.5)
              s = sma(close, 2.5)
            }
            """, "Rounding", model);
        Assert.Equal(291m, Value(indicator, 0, Bars - 1));
        Assert.Equal(128m, Value(indicator, 1, Bars - 1)); // a three-bar average of 127, 128 and 129
    }

    [Fact]
    public void Alert_text_joins_values_with_the_invariant_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("es-ES");
        try
        {
            Assert.Equal("Close 1.5 / true", KvsRuntime.Text(new KvsText("Close ") + 1.5 + new KvsText(" / ") + true));
            Assert.Equal("2.25x", KvsRuntime.Text(2.25 + new KvsText("x")));
        }
        finally { CultureInfo.CurrentCulture = culture; }

        var (model, _) = Market();
        Run("output x = line\nonBar {\n  x = close\n  alert(\"Close \" + close + \" above: \" + (close > open))\n}", "AlertText", model);
    }

    [Theory]
    [InlineData("param big = 1e30\noutput x = line", 1, 13)]
    [InlineData("param n = 2 { max: -8e28 }\noutput x = line", 1, 15)]
    public void Parameters_beyond_the_decimal_range_are_script_errors(string source, int line, int column)
    {
        var error = FirstError(source, "Huge");
        Assert.Equal(("KVS", line, column), (error.Id, error.Line, error.Column));
        Assert.Contains("too large", error.Message, StringComparison.Ordinal);
    }
}
