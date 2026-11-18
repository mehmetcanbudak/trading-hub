using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Kvs;
using TradingHub.Market;
using TradingHub.Scripting;

namespace TradingHub.Tests;

/// <summary>KVS scripts are translated, compiled by the ordinary script compiler and calculated; errors point at the script.</summary>
public sealed class KvsScriptTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int Bars = 30;
    private readonly string _dir = Directory.CreateTempSubdirectory("kvs-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Bar i: buy (i+1)@(100+i) then sell 1@(100+i): close 100+i, delta i, volume i+2.</summary>
    private static ChartModel Market(TradeSide buy = TradeSide.Buy, TradeSide sell = TradeSide.Sell)
    {
        var instrument = new Instrument { Symbol = "KVS-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 };
        var data = new InstrumentData(instrument, new SimulatedFeed());
        long id = 1;
        for (int i = 0; i < Bars; i++)
        {
            data.Trades.Add(new Trade(id++, Start.AddMinutes(i), 100 + i, i + 1, buy));
            data.Trades.Add(new Trade(id++, Start.AddMinutes(i).AddSeconds(30), 100 + i, 1, sell));
        }

        return new ChartModel(data, ChartPeriod.M1, new ChartSettings());
    }

    private string Write(string name, string source)
    {
        string path = Path.Combine(_dir, name + KvsTranslator.Extension);
        File.WriteAllText(path, source);
        return path;
    }

    private (Indicator Indicator, ChartModel Model) Run(string source, string name = "Probe", ChartModel? model = null)
    {
        var result = new ScriptCompiler().Compile(new[] { Write(name, source) }, "KvsTest");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var type = result.Assembly!.GetType(KvsTranslator.FullTypeNameFor(name))!;
        var indicator = (Indicator)Activator.CreateInstance(type)!;
        model ??= Market();
        Assert.Null(model.AddIndicator(indicator, applyDefaultColors: false).Error);
        return (indicator, model);
    }

    private static decimal Value(Indicator indicator, int series, int bar) => ((ValueDataSeries)indicator.DataSeries[series])[bar];

    private ScriptDiagnostic FirstError(string source, string name = "Broken")
    {
        string path = Write(name, source);
        var result = new ScriptCompiler().Compile(new[] { path }, "KvsBad");
        Assert.False(result.Success);
        var error = result.Diagnostics.First(d => d.Severity == "error");
        Assert.Equal(path, error.File);
        return error;
    }

    [Fact]
    public void Short_form_bands_match_a_reference_ema_and_parameters_recalculate()
    {
        var (indicator, model) = Run("""
            name "Bands"
            param length = 5 { min: 1, max: 500, description: "Bars" }
            param mult = 2
            input src = ohlcv.close
            output basis = line { overlay: true, color: "#f2b53a", width: 2 }
            output upper = line { overlay: true }
            onBar {
              basis = ema(src, length)
              var dev = stdev(src, length)
              upper = basis + mult * dev
            }
            """);
        double ema = double.NaN, alpha = 2.0 / 6;
        for (int i = 0; i < Bars; i++) ema = double.IsNaN(ema) ? 100 + i : alpha * (100 + i) + (1 - alpha) * ema;
        Assert.Equal(ema, (double)Value(indicator, 0, Bars - 1), 6);
        // stdev of 5 consecutive integers (population) = √2
        Assert.Equal(ema + 2 * Math.Sqrt(2), (double)Value(indicator, 1, Bars - 1), 6);
        Assert.Equal(0m, Value(indicator, 1, 3)); // warm-up: no value before the window is full
        Assert.Equal("Bands", Indicator.DefaultName(indicator.GetType()));
        Assert.NotEqual(IndicatorDataProvider.NewPanel, indicator.Panel);
        Assert.Equal(System.Drawing.Color.FromArgb(255, 0xf2, 0xb5, 0x3a), ((ValueDataSeries)indicator.DataSeries[0]).Color);

        indicator.GetType().GetProperty("mult")!.SetValue(indicator, 0m);
        model.Update();
        Assert.Equal(Value(indicator, 0, Bars - 1), Value(indicator, 1, Bars - 1));
    }

    [Fact]
    public void Wrun_call_form_cumulative_delta_and_sign_colors()
    {
        var (indicator, _) = Run("""
            const len = param("len", 3, { min: 2, max: 200, description: "length" })
            input("buy", trades.volume, { side: "BUY" })
            input("sell", trades.volume, { side: "SELL" })
            output("cvd", line, lower, { color_by: "sign", colors: ["#26c281", "#f0525a"] })
            output("avg", line, lower, { color: "#ffffff", opacity: 0.5 })
            function onBar() {
              out_cvd(cum(buy - sell))
              out_avg(sma(cvd, len))
            }
            """, "Wrun");
        decimal expected = 0;
        for (int i = 0; i < Bars; i++) expected += i;
        Assert.Equal(expected, Value(indicator, 0, Bars - 1));
        // KVS series are double: sma = (378 + 406 + 435) / 3.0, published through KvsRuntime.ToDecimal, whose runtime
        // decimal(double) conversion keeps 15 significant digits (documented by System.Decimal(Double)). The expected value
        // is that literal rather than a compile-time (decimal)(double) fold, which the build host evaluated to all 28
        // digits (406.33333333333331438552704640) on Windows ARM64 while the runtime returned 406.333333333333.
        Assert.Equal(406.333333333333m, Value(indicator, 1, Bars - 1));
        Assert.Equal(IndicatorDataProvider.NewPanel, indicator.Panel);
        Assert.Equal(128, ((ValueDataSeries)indicator.DataSeries[1]).Color.A);
        Assert.Equal(System.Drawing.Color.FromArgb(255, 0x26, 0xc2, 0x81), ((ValueDataSeries)indicator.DataSeries[0]).Colors[Bars - 1]);
    }

    [Fact]
    public void History_conditions_ternary_and_implicit_price_series()
    {
        var (indicator, _) = Run("""
            output step = histogram { show_zero: true }
            output index = dots
            onBar {
              var prev = close[1]
              if (na(prev)) {
                step = -1
              } else if (close - prev == 1 and not (bar_index < 2)) {
                step = delta > 10 ? 2 : 1
              } else {
                step = 0
              }
              index = bar_index
              index += .5
            }
            level 0 { color: "#ff0000" }
            """);
        Assert.Equal(-1m, Value(indicator, 0, 0));
        Assert.Equal(0m, Value(indicator, 0, 1));
        Assert.Equal(1m, Value(indicator, 0, 5));
        Assert.Equal(2m, Value(indicator, 0, 11));
        Assert.Equal(7.5m, Value(indicator, 1, 7));
        Assert.True(((ValueDataSeries)indicator.DataSeries[0]).ShowZeroValue);
        Assert.Equal(VisualMode.Dots, ((ValueDataSeries)indicator.DataSeries[1]).VisualType);
        Assert.Single(indicator.LineSeries);
    }

    [Fact]
    public void Missing_aggressor_side_and_missing_recorded_depth_stay_missing_instead_of_zero()
    {
        var (indicator, _) = Run("""
            input bid = book.bid { within: 5 }
            output flags = line
            onBar {
              flags = (na(bid) ? 1 : 0) + (na(buyvol) ? 10 : 0) + (na(delta) ? 100 : 0)
            }
            """, "Gaps", Market(TradeSide.None, TradeSide.None));
        Assert.Equal(111m, Value(indicator, 0, Bars - 1));
    }

    [Theory]
    [InlineData("output x = line\nonBar {\n  x = foo(close)\n}", 3, 7, "foo")]
    [InlineData("output x = line\nonBar {\n  y = close\n}", 3, 3, "y")]
    [InlineData("param n = abc\noutput x = line", 1, 11, "abc")]
    [InlineData("output x = line\n  onBar {\n x = 1\n", 2, 3, "missing its closing")]
    [InlineData("output x = line\noutput x = line", 2, 8, "already declared")]
    [InlineData("param Name = 1\noutput x = line", 1, 7, "reserved")]
    [InlineData("input q = ohlcv.vwap\noutput x = line", 1, 11, "ohlcv.vwap")]
    [InlineData("output x = ribbon", 1, 12, "ribbon")]
    [InlineData("output x = line\nonBar {\n  x = close & open\n}", 3, 13, "&")]
    [InlineData("output x = line\nonBar {\n  x = sma(close)\n}", 3, 7, "sma(value, length)")]
    [InlineData("param n = 3\noutput x = line\nonBar {\n  x = n[1]\n}", 4, 7, "parameter")]
    [InlineData("name \"No output\"", 1, 1, "no output")]
    [InlineData("print close", 1, 1, "Unexpected")]
    public void Script_errors_report_line_and_column(string source, int line, int column, string text)
    {
        var error = FirstError(source);
        Assert.Equal("KVS", error.Id);
        Assert.Equal(line, error.Line);
        Assert.Equal(column, error.Column);
        Assert.Contains(text, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Csharp_type_errors_map_back_to_the_script_statement()
    {
        var error = FirstError("output x = line\nonBar {\n  x = 1\n    x = close > open\n}\n", "Typed");
        Assert.StartsWith("CS", error.Id);
        Assert.Equal(4, error.Line);
        Assert.Equal(5, error.Column);
    }

    [Fact]
    public void A_broken_script_fails_the_whole_compilation_so_the_last_good_catalog_is_kept()
    {
        string good = Path.Combine(_dir, "Good.cs");
        File.WriteAllText(good, "using TradingHub.Indicators; public class GoodProbe : Indicator { protected override void OnCalculate(int bar, decimal value) { } }");
        string bad = Write("Bad", "output x = line\nonBar {\n  x = nope\n}");
        var result = new ScriptCompiler().Compile(new[] { good, bad }, "KvsMixed");
        Assert.False(result.Success);
        Assert.Null(result.Assembly);
        Assert.Contains(result.Diagnostics, d => d.File == bad && d.Line == 3);
    }

    [Fact]
    public void Class_names_are_stable_and_ascii()
    {
        Assert.Equal("Kvs_My_script_2", KvsTranslator.ClassNameFor("My script-2"));
        Assert.Equal("TradingHub.UserScripts.Kvs.Kvs_A", KvsTranslator.FullTypeNameFor("A"));
        Assert.Equal("Kvs____", KvsTranslator.ClassNameFor("é ü"));
    }

    [Fact]
    public void User_scripts_publish_script_types_and_file_operations_keep_the_language()
    {
        var catalog = new TradingHub.App.Services.IndicatorCatalog();
        string path = Write("Published", "output x = line\nonBar {\n  x = close\n}");
        using (var scripts = new TradingHub.App.Services.UserScripts(catalog, _dir, _ => { }))
            Assert.True(scripts.CompileAll()!.Success);
        var info = Assert.Single(catalog.Items);
        Assert.Equal("TradingHub.UserScripts.Kvs.Kvs_Published", info.Type.FullName);
        Assert.Equal(TradingHub.App.Services.UserScripts.Source, info.Source);
        Assert.Equal(new[] { path }, TradingHub.App.Services.IndicatorSources.UserFiles(info.Type, _dir));

        var files = new TradingHub.App.Services.IndicatorSourceFiles(_dir);
        Assert.Contains(path, files.EditablePaths());
        Assert.DoesNotContain(path, files.Paths()); // C# semantic indexes never parse script text
        string text = File.ReadAllText(path);
        Assert.Throws<ArgumentException>(() => files.Rename(path, "Published.cs", text));
        string renamed = files.Rename(path, "Renamed", text);
        Assert.Equal(Path.Combine(_dir, "Renamed.kvs"), renamed);
        Assert.Equal(text, files.Read(renamed));
    }

    [Fact]
    public void Comments_and_strings_do_not_change_lines_or_blocks()
    {
        var (indicator, _) = Run("""
            /* a block comment
               over two lines */
            output x = line // trailing { brace in a comment
            onBar {
              x = 3 // } not a block end
              if (x > 1) { alert("price { above } // still a string") }
            }
            """, "Comments");
        Assert.Equal(3m, Value(indicator, 0, Bars - 1));
        var error = FirstError("/*\n\n*/\noutput x = line\nonBar {\n  x = what\n}", "CommentLines");
        Assert.Equal(6, error.Line);
    }

    [Fact]
    public void Runtime_windows_warm_up_and_alerts_fire_once_per_bar_even_after_recalculation()
    {
        var runtime = new KvsRuntime(4);
        Assert.True(double.IsNaN(runtime.Sma(0, 0, 1, 3)));
        Assert.True(double.IsNaN(runtime.Sma(0, 1, 2, 3)));
        Assert.Equal(2, runtime.Sma(0, 2, 3, 3));
        Assert.Equal(2, runtime.Sma(0, 2, 3, 3)); // recalculating the forming bar is idempotent
        runtime.Wma(1, 0, 1, 3);
        runtime.Wma(1, 1, 2, 3);
        Assert.Equal((1 * 1 + 2 * 2 + 3 * 3) / 6.0, runtime.Wma(1, 2, 3, 3), 10);
        Assert.False(runtime.Crossover(2, 0, 1, 2));
        Assert.True(runtime.Crossover(2, 1, 3, 2));
        Assert.Equal(5, runtime.Highest(3, 0, 5, 1));

        Assert.False(runtime.ShouldAlert(0, 7, lastBar: false));
        Assert.True(runtime.ShouldAlert(0, 7, lastBar: true));
        Assert.False(runtime.ShouldAlert(0, 7, lastBar: true)); // the same alert statement, whatever its message now says
        runtime.Reset();
        Assert.False(runtime.ShouldAlert(0, 7, lastBar: true));
        Assert.True(runtime.ShouldAlert(1, 7, lastBar: true));
        Assert.True(runtime.ShouldAlert(0, 8, lastBar: true));

        Assert.Equal(System.Drawing.Color.FromArgb(0x80, 1, 2, 3), KvsRuntime.ParseColor("#80010203", System.Drawing.Color.Red));
        Assert.Equal(System.Drawing.Color.Red, KvsRuntime.ParseColor("#zzzzzz", System.Drawing.Color.Red));
        Assert.Equal(0m, KvsRuntime.ToDecimal(double.NaN));
    }
}
