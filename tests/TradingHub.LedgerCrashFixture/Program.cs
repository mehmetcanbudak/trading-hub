using System.Diagnostics;
using System.Text.Json;
using System.Text;
using TradingHub.Strategies;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.LedgerCrashFixture;

internal static class Program
{
    private const string Key = "TradingHub.Tests.Strategy|test:TEST|Cuenta simulada";
    private static readonly DateTime Time = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Stopwatch Elapsed = Stopwatch.StartNew();

    private static int Main(string[] args)
    {
        // No implicit store, source-owned root or user profile is ever an acceptable fixture.
        if (args.Length < 2 || !Path.IsPathFullyQualified(args[0])) return 64;
        string root = Path.GetFullPath(args[0]);
        string relative = Path.GetRelativePath(Path.GetTempPath(), root);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relative) || !File.Exists(Path.Combine(root, ".ledger-fixture-root"))) return 64;
        LiveStore.Root = Path.Combine(root, "Live");
        try
        {
            Progress("operation " + args[1]);
            if (args[1] is "write" or "flat-write")
            {
                string checkpoint = args.Length > 2 ? args[2] : "Acknowledged";
                Progress("writer append begin");
                string? error = LiveStore.AppendFill(Key, new LiveFill(Time.AddMinutes(1), false, args[1] == "flat-write" ? 2 : 1, 110),
                    path => new CheckpointStream(path, checkpoint));
                if (error != null) throw new IOException(error);
                Progress("writer append complete");
                if (checkpoint == "Acknowledged") Pause();
                Console.WriteLine("acknowledged");
            }
            else if (args[1] == "batch-crash")
            {
                var first = new LiveFill(Time.AddMinutes(1), false, 1, 110);
                var second = new LiveFill(Time.AddMinutes(2), false, 2, 90);
                var refused = new WriteBudget(0);
                if (LiveStore.AppendFill(Key, first, path => OpenWithBudget(path, refused)) == null)
                    throw new IOException("The fixture's first append unexpectedly succeeded.");
                // The old writer sends both queued fills in one Write. The new writer commits the first transaction,
                // then journals the second. The same total byte budget leaves one complete fill and seven tail bytes.
                var partial = new WriteBudget(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(first) + "\n") + 7);
                if (LiveStore.AppendFill(Key, second, path => OpenWithBudget(path, partial)) == null)
                    throw new IOException("The fixture's partial append unexpectedly succeeded.");
                Pause();
            }
            else
            {
                Console.WriteLine("started");
                Console.Out.Flush();
                bool? initiallyPaused = null, paused = null;
                if (args[1] == "append")
                {
                    Progress("append begin");
                    // The per-call seam runs the same SavePending transaction; null adds no pause or injected fault.
                    string? error = LiveStore.AppendFill(Key, new LiveFill(Time.AddMinutes(2), false, 2, 90),
                        path => new CheckpointStream(path, checkpoint: null));
                    if (error != null) throw new IOException(error);
                    Progress("append complete");
                }
                else if (args[1] == "save-pending")
                {
                    Progress("save pending begin");
                    if (LiveStore.SavePendingFills(Key) is { } error) throw new IOException(error);
                    Progress("save pending complete");
                }
                else if (args[1] == "resume-flat")
                {
                    var instrument = new Instrument { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
                    var data = new InstrumentData(instrument, new SimulatedFeed());
                    var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());
                    using var runner = new LiveStrategyRunner(new TradingHub.Tests.Strategy(), data, broker, ChartPeriod.M1);
                    if (runner.Key != Key) throw new IOException("The fixture strategy identity changed.");
                    runner.Start();
                    initiallyPaused = runner.Paused;
                    var fills = runner.Ledger.Fills.ToArray();
                    var trades = runner.Ledger.Trades.ToArray();
                    if (runner.Ledger.Position != 0) throw new IOException("The restarted fixture did not recover its flat ledger.");
                    runner.SetPaused(false);
                    paused = runner.Paused;
                    if (!runner.Ledger.Fills.SequenceEqual(fills) || !runner.Ledger.Trades.SequenceEqual(trades) || broker.Orders.Count != 0)
                        throw new IOException("Manual resume altered fills, trades or orders.");
                }
                else if (args[1] == "delete") LiveStore.DeleteFills(Key);
                else if (args[1] != "read") return 64;
                Progress("load ledger begin");
                var ledger = LiveStore.LoadLedger(Key, 1);
                Progress("load ledger complete");
                Progress("pending fills begin");
                int pending = LiveStore.PendingFills(Key);
                Progress("pending fills complete");
                Progress("snapshot serialization begin");
                string snapshot = JsonSerializer.Serialize(new
                {
                    ledger.Fills, ledger.Position, ledger.AveragePrice, ledger.Realized,
                    OpenPnL = ledger.OpenPnL(80), ledger.SkippedLines, Pending = pending,
                    InitiallyPaused = initiallyPaused, Paused = paused,
                });
                Progress("snapshot serialization complete");
                Console.WriteLine(snapshot);
                Console.Out.Flush();
                Progress("snapshot published");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // Stderr is a diagnostic side channel; stdout remains the asserted checkpoint/snapshot protocol.
    private static void Progress(string phase) =>
        Console.Error.WriteLine($"[ledger-fixture pid={Environment.ProcessId} elapsed={Elapsed.Elapsed}] {phase}");

    private static void Pause()
    {
        Progress("checkpoint waiting");
        Console.WriteLine("checkpoint");
        Console.Out.Flush();
        if (Console.ReadLine() != "continue") throw new IOException("The fixture checkpoint was not released.");
        Progress("checkpoint released");
    }

    private sealed class WriteBudget(int remaining)
    {
        public int Remaining = remaining;
    }

    private static Stream OpenWithBudget(string path, WriteBudget budget) =>
        path.EndsWith(".jsonl", StringComparison.Ordinal)
            ? new BudgetStream(path, budget)
            : new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);

    private sealed class BudgetStream(string path, WriteBudget budget) : Stream
    {
        private readonly FileStream _file = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set => _file.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _file.Read(buffer);
        public override int ReadByte() => _file.ReadByte();
        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
        public override void SetLength(long value) => throw new IOException("Injected rollback failure");
        public override void Flush() => _file.Flush(flushToDisk: true);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int count = Math.Min(budget.Remaining, buffer.Length);
            _file.Write(buffer[..count]);
            budget.Remaining -= count;
            if (count == buffer.Length) return;
            _file.Flush(flushToDisk: true);
            throw new IOException("Injected partial append failure");
        }
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CheckpointStream(string path, string? checkpoint) : Stream
    {
        private readonly FileStream _file = Open(path);
        private readonly bool _main = path.EndsWith(".jsonl", StringComparison.Ordinal);
        private bool _closed;
        private string Label => _main ? "ledger" : "intent temporary";

        private static FileStream Open(string path)
        {
            string label = Path.GetFileName(path);
            Progress(label + " open begin");
            var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 0);
            Progress(label + " open complete");
            return file;
        }

        public override bool CanRead => _file.CanRead;
        public override bool CanSeek => _file.CanSeek;
        public override bool CanWrite => _file.CanWrite;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set => _file.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _file.Read(buffer);
        public override int ReadByte() => _file.ReadByte();
        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
        public override void SetLength(long value)
        {
            Progress($"{Label} truncate begin ({value} bytes)");
            _file.SetLength(value);
            Progress(Label + " truncate complete");
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Progress($"{Label} write begin ({buffer.Length} bytes)");
            if (_main && checkpoint == "IntentPublished") Pause();
            if (_main && checkpoint == "PartialWrite")
            {
                int count = Math.Min(7, buffer.Length);
                _file.Write(buffer[..count]);
                Progress(Label + " partial flush begin");
                _file.Flush(flushToDisk: true);
                Progress(Label + " partial flush complete");
                Pause();
                _file.Write(buffer[count..]);
            }
            else _file.Write(buffer);
            Progress(Label + " write complete");
        }
        public override void Flush()
        {
            Progress(Label + " flush begin");
            _file.Flush(flushToDisk: true);
            Progress(Label + " flush complete");
            if ((_main && checkpoint == "MainFlushed") || (!_main && checkpoint == "IntentTemporary")) Pause();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                Progress(Label + " close begin");
                _file.Dispose();
                _closed = true;
                Progress(Label + " close complete");
                if (_main && checkpoint == "MainClosed") Pause();
            }
            base.Dispose(disposing);
        }
    }
}
