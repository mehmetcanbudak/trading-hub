using System.Drawing;
using System.Globalization;
using TradingHub.Data;

namespace TradingHub.Indicators.Kvs
{
    /// <summary>
    /// One number per bar for a KVS script input, output or variable. Missing values are NaN.
    /// Writing the same bar again replaces it, so the forming bar can be recalculated on every update.
    /// </summary>
    public sealed class KvsSeries
    {
        private double[] _values = CreateBuffer(256);
        private int _count;

        public void Clear()
        {
            Array.Fill(_values, double.NaN, 0, _count);
            _count = 0;
        }

        public void Set(int bar, double value)
        {
            if (bar < 0) return;
            if (bar >= _values.Length)
            {
                int length = _values.Length;
                while (length <= bar) length *= 2;
                var grown = CreateBuffer(length);
                Array.Copy(_values, grown, _count);
                _values = grown;
            }

            if (bar >= _count) _count = bar + 1;
            _values[bar] = value;
        }

        /// <summary>The value <paramref name="offset"/> bars ago (0 = this bar); NaN when absent.</summary>
        public double Get(int bar, int offset = 0)
        {
            int index = bar - offset;
            return index >= 0 && index < _count ? _values[index] : double.NaN;
        }

        private static double[] CreateBuffer(int length)
        {
            var values = new double[length];
            Array.Fill(values, double.NaN);
            return values;
        }
    }

    /// <summary>
    /// Built-in KVS functions. Every call site in a script owns a separate state slot, so the same function can be used
    /// several times. State is indexed by bar number: recalculating the forming bar never advances anything twice.
    /// Windowed functions return NaN until their whole window holds values (the usual Pine-style warm-up).
    /// </summary>
    public sealed class KvsRuntime
    {
        public const int MaxLength = 100_000;
        private readonly KvsSeries[] _input;
        private readonly KvsSeries[] _output;
        private readonly KvsSeries[] _second;
        private readonly HashSet<(int Bar, string Message)> _alerted = new();

        public KvsRuntime(int sites)
        {
            int count = Math.Max(1, sites);
            _input = new KvsSeries[count];
            _output = new KvsSeries[count];
            _second = new KvsSeries[count];
            for (int i = 0; i < count; i++)
            {
                _input[i] = new KvsSeries();
                _output[i] = new KvsSeries();
                _second[i] = new KvsSeries();
            }
        }

        /// <summary>Clears per-bar state before a full recalculation. Alerts already raised stay raised.</summary>
        public void Reset()
        {
            for (int i = 0; i < _input.Length; i++)
            {
                _input[i].Clear();
                _output[i].Clear();
                _second[i].Clear();
            }
        }

        private static int Length(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) ? 1 : (int)Math.Clamp(Math.Round(value), 1, MaxLength);

        public double Ema(int site, int bar, double value, double length) => Smooth(site, bar, value, 2.0 / (Length(length) + 1));

        public double Rma(int site, int bar, double value, double length) => Smooth(site, bar, value, 1.0 / Length(length));

        private double Smooth(int site, int bar, double value, double alpha)
        {
            double previous = _output[site].Get(bar, 1);
            double result = double.IsNaN(value) ? previous : double.IsNaN(previous) ? value : alpha * value + (1 - alpha) * previous;
            _output[site].Set(bar, result);
            return result;
        }

        /// <summary>Stores this bar's input and reports whether the complete window has values.</summary>
        private bool Window(int site, int bar, double value, double length, out int n)
        {
            _input[site].Set(bar, value);
            n = Length(length);
            if (bar + 1 < n) return false;
            for (int i = 0; i < n; i++)
                if (double.IsNaN(_input[site].Get(bar, i))) return false;
            return true;
        }

        public double Sma(int site, int bar, double value, double length)
        {
            if (!Window(site, bar, value, length, out int n)) return double.NaN;
            double sum = 0;
            for (int i = 0; i < n; i++) sum += _input[site].Get(bar, i);
            return sum / n;
        }

        public double Wma(int site, int bar, double value, double length)
        {
            if (!Window(site, bar, value, length, out int n)) return double.NaN;
            double sum = 0, weights = 0;
            for (int i = 0; i < n; i++)
            {
                double weight = n - i; // newest bar has the largest weight
                sum += _input[site].Get(bar, i) * weight;
                weights += weight;
            }

            return sum / weights;
        }

        /// <summary>Population standard deviation of the window.</summary>
        public double Stdev(int site, int bar, double value, double length)
        {
            if (!Window(site, bar, value, length, out int n)) return double.NaN;
            double mean = 0;
            for (int i = 0; i < n; i++) mean += _input[site].Get(bar, i);
            mean /= n;
            double squares = 0;
            for (int i = 0; i < n; i++)
            {
                double d = _input[site].Get(bar, i) - mean;
                squares += d * d;
            }

            return Math.Sqrt(squares / n);
        }

        public double Highest(int site, int bar, double value, double length)
        {
            if (!Window(site, bar, value, length, out int n)) return double.NaN;
            double result = double.NegativeInfinity;
            for (int i = 0; i < n; i++) result = Math.Max(result, _input[site].Get(bar, i));
            return result;
        }

        public double Lowest(int site, int bar, double value, double length)
        {
            if (!Window(site, bar, value, length, out int n)) return double.NaN;
            double result = double.PositiveInfinity;
            for (int i = 0; i < n; i++) result = Math.Min(result, _input[site].Get(bar, i));
            return result;
        }

        public double Sum(int site, int bar, double value, double length)
        {
            if (!Window(site, bar, value, length, out int n)) return double.NaN;
            double sum = 0;
            for (int i = 0; i < n; i++) sum += _input[site].Get(bar, i);
            return sum;
        }

        /// <summary>value − value <paramref name="length"/> bars ago (NaN without that history).</summary>
        public double Change(int site, int bar, double value, double length)
        {
            _input[site].Set(bar, value);
            return value - _input[site].Get(bar, Length(length));
        }

        /// <summary>Cumulative sum from the first bar; NaN inputs add nothing.</summary>
        public double Cum(int site, int bar, double value)
        {
            double previous = _output[site].Get(bar, 1);
            double result = (double.IsNaN(previous) ? 0 : previous) + (double.IsNaN(value) ? 0 : value);
            _output[site].Set(bar, result);
            return result;
        }

        public bool Crossover(int site, int bar, double a, double b)
        {
            _input[site].Set(bar, a);
            _second[site].Set(bar, b);
            return a > b && _input[site].Get(bar, 1) <= _second[site].Get(bar, 1);
        }

        public bool Crossunder(int site, int bar, double a, double b)
        {
            _input[site].Set(bar, a);
            _second[site].Set(bar, b);
            return a < b && _input[site].Get(bar, 1) >= _second[site].Get(bar, 1);
        }

        public static double Nz(double value) => Nz(value, 0);

        public static double Nz(double value, double replacement) => double.IsNaN(value) || double.IsInfinity(value) ? replacement : value;

        public static bool Na(double value) => double.IsNaN(value);

        /// <summary>Chart series value; NaN/∞ become 0, which outputs hide unless they opt into drawing zeros.</summary>
        public static decimal ToDecimal(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) ? 0m : (decimal)Math.Clamp(value, -7.9e27, 7.9e27);

        /// <summary>True once per bar and message, only on the last bar. Recalculation does not repeat an alert.</summary>
        public bool ShouldAlert(int bar, string message, bool lastBar) => lastBar && _alerted.Add((bar, message));

        public static string Text(double value) => value.ToString("G10", CultureInfo.InvariantCulture);

        public static string Text(bool value) => value ? "true" : "false";

        public static string Text(string value) => value;

        /// <summary>«#RRGGBB» or «#AARRGGBB»; anything else returns <paramref name="fallback"/>.</summary>
        public static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            var h = hex.Trim().TrimStart('#');
            if (h.Length is not (6 or 8) || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb)) return fallback;
            if (h.Length == 6) argb |= 0xFF000000;
            return Color.FromArgb(unchecked((int)argb));
        }

        /// <summary>
        /// Average resting bid/ask size within <paramref name="withinTicks"/> of each side's best price over the bar's
        /// recorded usable depth samples. NaN when the bar has no usable recorded depth (never an invented zero).
        /// </summary>
        public static double BookBid(IReadOnlyList<DepthSample> samples, int withinTicks) => Book(samples, withinTicks, bid: true);

        public static double BookAsk(IReadOnlyList<DepthSample> samples, int withinTicks) => Book(samples, withinTicks, bid: false);

        public static double BookDelta(IReadOnlyList<DepthSample> samples, int withinTicks) =>
            BookBid(samples, withinTicks) - BookAsk(samples, withinTicks);

        private static double Book(IReadOnlyList<DepthSample> samples, int withinTicks, bool bid)
        {
            decimal sum = 0;
            int count = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                if (!samples[i].IsUsable) continue;
                var (b, a) = samples[i].Within(withinTicks);
                sum += bid ? b : a;
                count++;
            }

            return count == 0 ? double.NaN : (double)(sum / count);
        }

        /// <summary>Aggressor-side volume; NaN when the bar has volume but no observed or estimated side split.</summary>
        public static double Side(IndicatorCandle candle, bool buy)
        {
            if (candle.Ask == 0 && candle.Bid == 0 && candle.Volume != 0) return double.NaN;
            return (double)(buy ? candle.Ask : candle.Bid);
        }

        public static double Delta(IndicatorCandle candle)
        {
            if (candle.Ask == 0 && candle.Bid == 0 && candle.Volume != 0) return double.NaN;
            return (double)candle.Delta;
        }
    }
}
