using TradingHub.Indicators.Kvs;

namespace TradingHub.Tests;

/// <summary>
/// KVS running window state: sma/wma/stdev/sum/highest/lowest equal a direct calculation over their window, and a live
/// history (forming-bar updates, corrections of earlier bars, call sites skipped by some updates, changing lengths,
/// missing and infinite values) ends exactly where one full recalculation of the final inputs ends.
/// </summary>
public sealed class KvsReviewRuntimeTests
{
    private const int Sites = 10;

    /// <summary>One bar as the script sees it: the value, the window length and whether its condition calls the functions.</summary>
    private readonly record struct Input(double Value, double Length, bool Called);

    /// <summary>What the generated OnCalculate does: restart the bar, then call every site the condition reaches.</summary>
    private static void Calculate(KvsRuntime runtime, IReadOnlyList<Input> inputs, int bar, double[] row)
    {
        runtime.Restart(bar);
        var (value, length, called) = inputs[bar];
        if (!called)
        {
            Array.Fill(row, double.NaN);
            return;
        }

        row[0] = runtime.Sma(0, bar, value, length);
        row[1] = runtime.Wma(1, bar, value, length);
        row[2] = runtime.Stdev(2, bar, value, length);
        row[3] = runtime.Sum(3, bar, value, length);
        row[4] = runtime.Highest(4, bar, value, length);
        row[5] = runtime.Lowest(5, bar, value, length);
        row[6] = runtime.Ema(6, bar, value, length);
        row[7] = runtime.Cum(7, bar, value);
        row[8] = runtime.Change(8, bar, value, length);
        row[9] = runtime.Crossover(9, bar, value, 100) ? 1 : 0;
    }

    private static int WindowLength(double length) =>
        double.IsNaN(length) || double.IsInfinity(length) ? 1 : (int)Math.Clamp(Math.Round(length, MidpointRounding.AwayFromZero), 1, KvsRuntime.MaxLength);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(25)]
    [InlineData(0)] // a length that changes along the history, sometimes na
    public void Live_updates_end_where_a_full_recalculation_ends_and_match_a_direct_window_calculation(int fixedLength)
    {
        var random = new Random(9000 + fixedLength);
        double level = 100;
        Input Draw(int bar)
        {
            double roll = random.NextDouble(), value;
            if (roll < 0.01) value = double.NaN;
            else if (roll < 0.015) value = double.PositiveInfinity;
            else if (roll < 0.02) value = double.NegativeInfinity;
            else value = level += Math.Round(random.NextDouble() * 4 - 2, 2);
            double length = fixedLength > 0 ? fixedLength : bar % 41 == 40 ? double.NaN : 1.5 + bar / 13 % 6;
            return new Input(value, length, random.NextDouble() >= 0.1);
        }

        var inputs = new List<Input>();
        var rows = new List<double[]>();
        var live = new KvsRuntime(Sites);
        while (inputs.Count < 400)
        {
            double operation = random.NextDouble();
            if (inputs.Count == 0 || operation < 0.55)
            {
                inputs.Add(Draw(inputs.Count)); // a new bar
                rows.Add(new double[Sites]);
                Calculate(live, inputs, inputs.Count - 1, rows[^1]);
            }
            else if (operation < 0.9)
            {
                int bar = inputs.Count - 1; // an update of the forming bar
                inputs[bar] = Draw(bar);
                Calculate(live, inputs, bar, rows[bar]);
            }
            else
            {
                int from = Math.Max(0, inputs.Count - 1 - random.Next(10)); // a correction recalculates from an earlier bar
                inputs[from] = Draw(from);
                for (int bar = from; bar < inputs.Count; bar++) Calculate(live, inputs, bar, rows[bar]);
            }
        }

        foreach (var runtime in new[] { new KvsRuntime(Sites), live })
        {
            runtime.Reset();
            var row = new double[Sites];
            for (int bar = 0; bar < inputs.Count; bar++)
            {
                Calculate(runtime, inputs, bar, row);
                Assert.Equal(rows[bar], row);
                AssertWindows(inputs, bar, row);
            }
        }
    }

    /// <summary>Direct calculation: a bar whose functions were not called, or before bar 0, is missing.</summary>
    private static void AssertWindows(IReadOnlyList<Input> inputs, int bar, double[] row)
    {
        if (!inputs[bar].Called) return;
        int n = WindowLength(inputs[bar].Length);
        var window = new double[n]; // newest first
        for (int age = 0; age < n; age++)
            window[age] = bar - age >= 0 && inputs[bar - age].Called ? inputs[bar - age].Value : double.NaN;
        bool complete = !window.Any(double.IsNaN);
        double sum = 0, weighted = 0;
        for (int age = 0; age < n; age++)
        {
            sum += window[age];
            weighted += (n - age) * window[age];
        }

        double mean = sum / n, squares = 0;
        foreach (double value in window) squares += (value - mean) * (value - mean);
        Close(complete ? sum / n : double.NaN, row[0], 1e-9);
        Close(complete ? weighted / (n * (n + 1.0) / 2) : double.NaN, row[1], 1e-9);
        Close(complete ? Math.Sqrt(squares / n) : double.NaN, row[2], 1e-6);
        Close(complete ? sum : double.NaN, row[3], 1e-9);
        Assert.Equal(complete ? window.Max() : double.NaN, row[4]);
        Assert.Equal(complete ? window.Min() : double.NaN, row[5]);
    }

    private static void Close(double expected, double actual, double tolerance)
    {
        if (!double.IsFinite(expected))
        {
            Assert.Equal(expected, actual);
            return;
        }

        Assert.True(Math.Abs(expected - actual) <= tolerance * Math.Max(1, Math.Abs(expected)), $"expected {expected}, got {actual}");
    }

    [Fact]
    public void Series_restart_returns_a_bar_to_where_a_full_recalculation_starts_it()
    {
        var series = new KvsSeries();
        series.Set(0, 5); // onStart
        series.Started();
        series.Set(0, 7);
        series.Restart(0);
        Assert.Equal(5, series.Get(0));
        series.Set(3, 1);
        series.Restart(3);
        Assert.True(double.IsNaN(series.Get(3)));
        series.Set(4, 2);
        Assert.True(double.IsNaN(series.Get(3, -1))); // never a future bar
        Assert.Equal(2, series.Get(4));
        series.Clear();
        series.Restart(0);
        Assert.True(double.IsNaN(series.Get(0))); // a full recalculation forgets the old onStart value
    }

    [Fact]
    public void Offsets_sign_and_round_treat_missing_values_as_missing()
    {
        Assert.Equal(-1, KvsRuntime.Offset(double.NaN));
        Assert.Equal(-1, KvsRuntime.Offset(double.PositiveInfinity));
        Assert.Equal(-1, KvsRuntime.Offset(-1));
        Assert.Equal(2, KvsRuntime.Offset(2.9));
        Assert.True(double.IsNaN(KvsRuntime.Sign(double.NaN)));
        Assert.Equal(-1, KvsRuntime.Sign(-0.1));
        Assert.Equal(3, KvsRuntime.Round(2.5));
        Assert.Equal(-3, KvsRuntime.Round(-2.5));
    }
}
