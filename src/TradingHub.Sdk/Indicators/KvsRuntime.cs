using System.Drawing;
using System.Globalization;
using TradingHub.Data;

namespace TradingHub.Indicators.Kvs
{
    /// <summary>
    /// One number per bar for a KVS script input, output or variable. Missing values are NaN.
    /// Writing the same bar again replaces it, and <see cref="Restart"/> forgets what an earlier update of the forming bar
    /// assigned, so a recalculated forming bar gets the value a full recalculation gives it.
    /// </summary>
    public sealed class KvsSeries
    {
        private double[] _values = CreateBuffer(256);
        private int _count;
        private double _start = double.NaN;

        public void Clear()
        {
            Array.Fill(_values, double.NaN, 0, _count);
            _count = 0;
            _start = double.NaN;
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

        /// <summary>The value <paramref name="offset"/> bars ago (0 = this bar); NaN when absent. A negative offset (a future bar) is absent.</summary>
        public double Get(int bar, int offset = 0) => offset < 0 ? double.NaN : At(bar - offset);

        internal double At(int index) => index >= 0 && index < _count ? _values[index] : double.NaN;

        /// <summary>Records bar 0 as <c>onStart</c> left it: every calculation of bar 0 starts from that value.</summary>
        public void Started() => _start = At(0);

        /// <summary>
        /// Called before each calculation of <paramref name="bar"/>: returns it to the value a full recalculation starts it
        /// from (missing, or bar 0's <c>onStart</c> value), so a recalculated forming bar never keeps an earlier update's assignment.
        /// </summary>
        public void Restart(int bar)
        {
            if (bar == 0 && !double.IsNaN(_start)) Set(0, _start);
            else if (bar >= 0 && bar < _count) _values[bar] = double.NaN;
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
    /// several times. State is indexed by bar number and <see cref="Restart"/> runs before each calculation of a bar:
    /// recalculating the forming bar never advances anything twice, and a call skipped by this update stays missing.
    /// Windowed functions return NaN until their whole window holds values (the usual Pine-style warm-up). They keep running
    /// window state, so with an unchanging length a full recalculation costs a constant amount per bar and call site.
    /// </summary>
    public sealed class KvsRuntime
    {
        public const int MaxLength = 100_000;
        private readonly KvsSeries[] _input;
        private readonly KvsSeries[] _output;
        private readonly KvsSeries[] _second;
        private readonly RunningSums?[] _sums;
        private readonly RunningExtreme?[] _extremes;
        private readonly HashSet<(int Alert, int Bar)> _alerted = new();

        public KvsRuntime(int sites)
        {
            int count = Math.Max(1, sites);
            _input = new KvsSeries[count];
            _output = new KvsSeries[count];
            _second = new KvsSeries[count];
            _sums = new RunningSums?[count];
            _extremes = new RunningExtreme?[count];
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

            Array.Clear(_sums);
            Array.Clear(_extremes);
        }

        /// <summary>
        /// Called before each calculation of <paramref name="bar"/>: forgets what an earlier calculation of the same bar
        /// stored, so every call site starts that bar from the state a full recalculation gives it.
        /// </summary>
        public void Restart(int bar)
        {
            for (int i = 0; i < _input.Length; i++)
            {
                _input[i].Restart(bar);
                _output[i].Restart(bar);
                _second[i].Restart(bar);
                _sums[i]?.Restart(bar);
                _extremes[i]?.Restart(bar);
            }
        }

        private static int Length(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) ? 1 : (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 1, MaxLength);

        public double Ema(int site, int bar, double value, double length) => Smooth(site, bar, value, 2.0 / (Length(length) + 1));

        public double Rma(int site, int bar, double value, double length) => Smooth(site, bar, value, 1.0 / Length(length));

        private double Smooth(int site, int bar, double value, double alpha)
        {
            double previous = _output[site].Get(bar, 1);
            double result = double.IsNaN(value) ? previous : double.IsNaN(previous) ? value : alpha * value + (1 - alpha) * previous;
            _output[site].Set(bar, result);
            return result;
        }

        /// <summary>Stores this bar's input and advances the call site's window sums to this bar.</summary>
        private RunningSums Sums(int site, int bar, double value, double length)
        {
            _input[site].Set(bar, value);
            var sums = _sums[site] ??= new RunningSums();
            sums.Advance(_input[site], bar, Length(length));
            return sums;
        }

        public double Sma(int site, int bar, double value, double length)
        {
            var sums = Sums(site, bar, value, length);
            return sums.Total() / sums.Length;
        }

        public double Wma(int site, int bar, double value, double length) => Sums(site, bar, value, length).Weighted();

        /// <summary>Population standard deviation of the window.</summary>
        public double Stdev(int site, int bar, double value, double length) => Sums(site, bar, value, length).Deviation();

        public double Sum(int site, int bar, double value, double length) => Sums(site, bar, value, length).Total();

        public double Highest(int site, int bar, double value, double length) => Extreme(site, bar, value, length, highest: true);

        public double Lowest(int site, int bar, double value, double length) => Extreme(site, bar, value, length, highest: false);

        private double Extreme(int site, int bar, double value, double length, bool highest)
        {
            _input[site].Set(bar, value);
            return (_extremes[site] ??= new RunningExtreme(highest)).Advance(_input[site], bar, Length(length));
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

        /// <summary>−1, 0 or 1; a missing value stays missing (<see cref="Math.Sign(double)"/> throws on NaN).</summary>
        public static double Sign(double value) => double.IsNaN(value) ? double.NaN : Math.Sign(value);

        /// <summary>Nearest integer with halves away from zero, as in Pine: round(2.5) = 3 and round(−2.5) = −3.</summary>
        public static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

        /// <summary>
        /// The offset of <c>x[n]</c>: NaN, ∞ and negative values become −1, which <see cref="KvsSeries.Get"/> reads as missing,
        /// so a script never sees a future bar (which only a live update would already have written).
        /// </summary>
        public static int Offset(double value) => value >= 0 && value <= int.MaxValue ? (int)value : -1;

        /// <summary>Chart series value; NaN/∞ become 0, which outputs hide unless they opt into drawing zeros.</summary>
        public static decimal ToDecimal(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) ? 0m : (decimal)Math.Clamp(value, -7.9e27, 7.9e27);

        /// <summary>
        /// True once per bar for each <c>alert</c> statement of the script, only on the last bar: a message that changes with
        /// every update of the forming bar does not repeat. Recalculation does not repeat an alert.
        /// </summary>
        public bool ShouldAlert(int alert, int bar, bool lastBar) => lastBar && _alerted.Add((alert, bar));

        public static string Text(double value) => value.ToString("G10", CultureInfo.InvariantCulture);

        public static string Text(bool value) => value ? "true" : "false";

        public static string Text(KvsText value) => value.ToString();

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

        /// <summary>Counts and sums of one window. Missing and infinite values are counted, never added.</summary>
        private struct WindowSums
        {
            /// <summary>NaN (or before the first bar), +∞ and −∞ values in the window.</summary>
            public int Missing, Positive, Negative;

            /// <summary>Σv, Σweight·v (the newest value weighs the window length), Σ(v − Shift) and Σ(v − Shift)².</summary>
            public double Sum, Weighted, Shift, Shifted, Squares;

            public static WindowSums Exact(KvsSeries input, int end, int length)
            {
                var sums = new WindowSums();
                int finite = 0;
                for (int age = 0; age < length; age++)
                {
                    double value = input.At(end - age);
                    if (sums.Count(value, 1)) continue;
                    sums.Sum += value;
                    sums.Weighted += (length - age) * value;
                    finite++;
                }

                // deviations from the window mean stay small while the window slides (no cancellation in the variance)
                sums.Shift = finite > 0 ? sums.Sum / finite : 0;
                for (int age = 0; age < length; age++)
                {
                    double value = input.At(end - age);
                    if (!double.IsFinite(value)) continue;
                    double deviation = value - sums.Shift;
                    sums.Shifted += deviation;
                    sums.Squares += deviation * deviation;
                }

                return sums;
            }

            /// <summary>The window one bar later: <paramref name="end"/> enters and <c>end − length</c> leaves.</summary>
            public readonly WindowSums Slide(KvsSeries input, int end, int length)
            {
                var sums = this;
                sums.Weighted -= sums.Sum; // every value ages one bar; the leaving one (weight 1) drops to weight 0
                double gone = input.At(end - length), added = input.At(end);
                if (!sums.Count(gone, -1))
                {
                    double deviation = gone - sums.Shift;
                    sums.Sum -= gone;
                    sums.Shifted -= deviation;
                    sums.Squares -= deviation * deviation;
                }

                if (!sums.Count(added, 1))
                {
                    double deviation = added - sums.Shift;
                    sums.Sum += added;
                    sums.Weighted += length * added;
                    sums.Shifted += deviation;
                    sums.Squares += deviation * deviation;
                }

                return sums;
            }

            /// <summary>Counts a missing or infinite value; false for a finite value, which the caller adds.</summary>
            private bool Count(double value, int step)
            {
                if (double.IsNaN(value)) Missing += step;
                else if (double.IsPositiveInfinity(value)) Positive += step;
                else if (double.IsNegativeInfinity(value)) Negative += step;
                else return false;
                return true;
            }

            /// <summary>NaN for an incomplete window; the infinity's sign when it holds one (NaN for both signs); else null.</summary>
            public readonly double? Special() =>
                Missing > 0 || (Positive > 0 && Negative > 0) ? double.NaN
                : Positive > 0 ? double.PositiveInfinity
                : Negative > 0 ? double.NegativeInfinity
                : null;
        }

        /// <summary>
        /// Window sums of one sma/wma/stdev/sum call site for the window ending at the last calculated bar, plus the window a
        /// bar earlier (to recalculate that bar). Sums are computed exactly whenever a window ends on a multiple of its length
        /// and slid one bar at a time in between, so rounding never accumulates and a value depends only on the inputs: live
        /// updates and a full recalculation agree exactly. An earlier bar, a skipped bar or a new length rebuilds the window.
        /// </summary>
        private sealed class RunningSums
        {
            private WindowSums _current, _previous;
            private bool _hasPrevious;
            private int _bar = int.MinValue;

            public int Length { get; private set; }

            public void Restart(int bar)
            {
                if (_bar < bar) return;
                if (_bar == bar && _hasPrevious)
                {
                    _current = _previous;
                    _bar = bar - 1;
                }
                else _bar = int.MinValue;

                _hasPrevious = false;
            }

            public void Advance(KvsSeries input, int bar, int length)
            {
                WindowSums previous;
                if (_bar == bar - 1 && Length == length) previous = _current;
                else if (_bar == bar && _hasPrevious && Length == length) previous = _previous;
                else previous = Rebuild(input, bar - 1, length);
                _current = bar % length == 0 ? WindowSums.Exact(input, bar, length) : previous.Slide(input, bar, length);
                _previous = previous;
                _hasPrevious = true;
                _bar = bar;
                Length = length;
            }

            private static WindowSums Rebuild(KvsSeries input, int end, int length)
            {
                if (end < 0) return new WindowSums { Missing = length };
                int start = end - end % length;
                var sums = WindowSums.Exact(input, start, length);
                for (int bar = start + 1; bar <= end; bar++) sums = sums.Slide(input, bar, length);
                return sums;
            }

            public double Total() => _current.Special() ?? _current.Sum;

            public double Weighted() => _current.Special() ?? _current.Weighted / (Length * (Length + 1.0) / 2);

            public double Deviation()
            {
                if (_current.Missing > 0 || _current.Positive > 0 || _current.Negative > 0) return double.NaN;
                double mean = _current.Shifted / Length;
                return Math.Sqrt(Math.Max(0, _current.Squares / Length - mean * mean));
            }
        }

        /// <summary>
        /// Monotonic queue of one highest/lowest call site: the window's bar indices whose values strictly decrease (highest)
        /// or increase (lowest) from the front, so the front holds the result. The last update can be undone, which is how the
        /// forming bar is recalculated; anything else (an earlier bar, a skipped bar, a new length) rebuilds the queue.
        /// </summary>
        private sealed class RunningExtreme(bool highest)
        {
            private int[] _queue = [];
            private int _head, _tail; // logical positions in the ring _queue, whose capacity is the length + 1
            private int _missing;
            private int _bar = int.MinValue, _length;
            private bool _canUndo;
            private int _undoHead, _undoTail, _undoMissing, _undoSlot, _undoIndex;

            public void Restart(int bar)
            {
                if (_bar < bar) return;
                if (_bar == bar && _canUndo) Undo();
                else
                {
                    _bar = int.MinValue;
                    _canUndo = false;
                }
            }

            public double Advance(KvsSeries input, int bar, int length)
            {
                if (_bar == bar && _canUndo && _length == length) Undo();
                if (_bar != bar - 1 || _length != length) Rebuild(input, bar - 1, length);
                Push(input, bar, length);
                return _missing > 0 ? double.NaN : input.At(_queue[_head % _queue.Length]);
            }

            private void Undo()
            {
                _head = _undoHead;
                _tail = _undoTail;
                _missing = _undoMissing;
                if (_undoSlot >= 0) _queue[_undoSlot % _queue.Length] = _undoIndex;
                _bar--;
                _canUndo = false;
            }

            private void Rebuild(KvsSeries input, int end, int length)
            {
                if (_queue.Length != length + 1) _queue = new int[length + 1];
                _head = _tail = 0;
                _missing = 0;
                for (int index = end - length + 1; index <= end; index++)
                {
                    double value = input.At(index);
                    if (double.IsNaN(value))
                    {
                        _missing++;
                        continue;
                    }

                    Drop(input, value);
                    _queue[_tail++ % _queue.Length] = index;
                }

                _bar = end;
                _length = length;
                _canUndo = false;
            }

            /// <summary>Slides the window ending a bar earlier to <paramref name="bar"/>, remembering how to undo it.</summary>
            private void Push(KvsSeries input, int bar, int length)
            {
                _undoHead = _head;
                _undoTail = _tail;
                _undoMissing = _missing;
                _undoSlot = -1;
                int gone = bar - length;
                if (double.IsNaN(input.At(gone))) _missing--;
                else if (_head < _tail && _queue[_head % _queue.Length] == gone) _head++;
                double value = input.At(bar);
                if (double.IsNaN(value)) _missing++;
                else
                {
                    // only this slot is overwritten: what Drop removed stays in the ring for Undo
                    Drop(input, value);
                    _undoSlot = _tail;
                    _undoIndex = _queue[_tail % _queue.Length];
                    _queue[_tail++ % _queue.Length] = bar;
                }

                _bar = bar;
                _canUndo = true;
            }

            /// <summary>Removes queued values the newer <paramref name="value"/> makes irrelevant (it is at least as extreme).</summary>
            private void Drop(KvsSeries input, double value)
            {
                while (_tail > _head)
                {
                    double last = input.At(_queue[(_tail - 1) % _queue.Length]);
                    if (highest ? last > value : last < value) break;
                    _tail--;
                }
            }
        }
    }

    /// <summary>
    /// Text in a KVS expression (alert messages). A number or condition joined to it is written as <c>alert(value)</c>
    /// writes it, with the invariant culture, so a message never depends on the interface language.
    /// </summary>
    public readonly struct KvsText(string value)
    {
        private readonly string? _value = value;

        public static KvsText operator +(KvsText a, KvsText b) => new(a.ToString() + b.ToString());

        public static KvsText operator +(KvsText a, double b) => new(a.ToString() + KvsRuntime.Text(b));

        public static KvsText operator +(double a, KvsText b) => new(KvsRuntime.Text(a) + b.ToString());

        public static KvsText operator +(KvsText a, bool b) => new(a.ToString() + KvsRuntime.Text(b));

        public static KvsText operator +(bool a, KvsText b) => new(KvsRuntime.Text(a) + b.ToString());

        public override string ToString() => _value ?? "";
    }
}
