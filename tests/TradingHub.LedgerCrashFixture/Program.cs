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
            if (args[1].StartsWith("execution-", StringComparison.Ordinal)) return ExecutionRestart(args[1]);
            if (args[1] == "projection-recovery-batch") return RecoveryBatch();
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
                    if (LiveStore.SavePendingLedgerEvents(Key) is { } error) throw new IOException(error);
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
                int pending = LiveStore.PendingLedgerEvents(Key);
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

    private static int RecoveryBatch()
    {
        const int count = 512;
        const string batchKey = "recovered-batch-fixture";
        const string resetKey = "recovered-batch-reset-fixture";
        Console.WriteLine("started");
        Console.Out.Flush();
        var source = new ExecutionLog();
        var context = new OrderContext(new("batch-account", "simulator", "paper", "fixture", "batch", "generation"),
            LiveStore.Root, source);
        var owner = context.LedgerOwner(batchKey, LiveStore.CaptureLedgerGeneration(LiveStore.Root, batchKey));
        var projector = AccountExecutionProjector.For(source, context);
        var ticket = context.Prepare(owner, new("test:TEST", OrderDirections.Buy, OrderTypes.Limit,
            count, 100, StrategyQuantity: count));
        if (ticket.Error != null) throw new IOException(ticket.Error);
        for (int i = 0; i < count; i++)
        {
            var fill = new LiveFill(Time.AddTicks(i), true, 1, 100, "TEST", 1)
            {
                Kind = LiveFillKind.Execution, OrderId = ticket.LogicalOrderId,
                ExecutionId = new(new("simulator", context.Descriptor.Id, "paper", "test:TEST")
                    { ContextId = context.Descriptor.Id }, "batch-" + i),
                ContextId = context.Descriptor.Id, OwnerId = owner.Id, OwnerGeneration = owner.LedgerGeneration,
                Fees = Array.Empty<ExecutionFee>(), FeesComplete = true, Currency = "USD",
            };
            var admitted = LiveStore.AdmitExecution(LiveStore.Root, batchKey, owner.LedgerGeneration, "receipt-" + i, fill);
            if (admitted.Status != LiveStore.ExecutionProjectionStatus.Applied) throw new IOException(admitted.Error);
        }
        Progress("batch admitted");
        var ledger = new LiveLedger(batchKey, 1);
        using var delivered = new ManualResetEventSlim();
        AccountExecutionProjector.ProjectionSubscription? slot = null;
        slot = projector.Subscribe(owner, () =>
        {
            if (slot == null) return;
            var updates = slot.DrainCommitted();
            foreach (var fill in updates) ledger.Apply(fill);
            slot.AcknowledgeCommitted(updates.Count);
            if (ledger.Fills.Count == count) delivered.Set();
        });
        using var subscription = slot;
        var resetSource = new ExecutionLog();
        var resetContext = new OrderContext(new("batch-reset", "simulator", "paper", "fixture", "reset", "generation"),
            LiveStore.Root, resetSource);
        var resetOwner = resetContext.LedgerOwner(resetKey, LiveStore.CaptureLedgerGeneration(LiveStore.Root, resetKey));
        var resetProjector = AccountExecutionProjector.For(resetSource, resetContext);
        using var resetSlot = resetProjector.Subscribe(resetOwner, () => { });
        Exception? failure = null;
        // Only this private child thread has a bounded stack. No global ThreadPool settings change.
        var thread = new Thread(() =>
        {
            try
            {
                if (!resetProjector.TryResetOwner(resetOwner, resetSlot, 1, () =>
                {
                    LiveStore.LoadLedger(LiveStore.Root, batchKey, 1);
                    // Reader recovery queued the whole batch while the real ownership gate was held.
                    resetProjector.Synchronize();
                })) throw new IOException("The unrelated flat fixture reset was refused.");
            }
            catch (Exception ex) { failure = ex; }
        }, maxStackSize: 128 * 1024);
        thread.Start();
        thread.Join();
        if (failure != null) throw new IOException("The owned recovery batch failed.", failure);
        if (!delivered.Wait(TimeSpan.FromSeconds(10))) throw new IOException("The committed recovery batch was not fully delivered.");
        if (ledger.Fills.Count != count || ledger.Position != count)
            throw new IOException("Recovered execution quantity was lost or duplicated.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ledger.Fills, ledger.Position, ledger.AveragePrice, ledger.Realized, OpenPnL = 0,
            ledger.SkippedLines, Pending = LiveStore.PendingLedgerEvents(LiveStore.Root, batchKey),
        }));
        Console.Out.Flush();
        return 0;
    }

    private static int ExecutionRestart(string operation)
    {
        const string instrument = "test:TEST";
        const decimal pointValue = 10m;
        var source = new ExecutionLog();
        var context = new OrderContext(new AccountContextDescriptor("owned-execution-crash-context", "fixture-native",
            "owned-futures", "loopback-fixture", "local:owned-execution-crash", "fixture-generation", "fixture-account"),
            LiveStore.Root, source);
        bool writer = operation is "execution-receipt-crash" or "execution-ledger-crash";
        if (writer)
        {
            Guid generation = LiveStore.CaptureLedgerGeneration(LiveStore.Root, Key);
            var owner = context.LedgerOwner(Key, generation);
            var entry = Prepare(owner, buy: true);
            var report = Report(buy: true, price: 100m, fee: 3m);
            source.Publish(report);
            var receipt = ExecutionReceipt.Create(new(source.Epoch, 1), report);
            var fill = new LiveFill(report.Time, true, 2m, 100m, "TEST", pointValue)
            {
                Kind = LiveFillKind.Execution, OrderId = entry.LogicalOrderId, ExecutionId = report.Identity,
                Fees = report.Fees, FeesComplete = true, Currency = "USD", ContextId = context.Descriptor.Id,
                OwnerId = owner.Id, OwnerGeneration = generation,
            };
            var admitted = LiveStore.AdmitExecution(LiveStore.Root, Key, generation, receipt.Id, fill);
            if (admitted.Status != LiveStore.ExecutionProjectionStatus.Applied) throw new IOException(admitted.Error ?? "Owned execution receipt was not admitted.");
            context.SaveReceipt(receipt);
            if (operation == "execution-ledger-crash")
            {
                var projected = LiveStore.ProjectExecution(LiveStore.Root, Key, generation, fill, receipt.Id);
                if (projected.Status != LiveStore.ExecutionProjectionStatus.Applied) throw new IOException(projected.Error ?? "Owned execution did not commit before its acknowledgement boundary.");
            }
            // Parent kills this exact owned child after the public durable receive/commit boundary.
            Pause();
            return 0;
        }
        if (operation is not ("execution-recover" or "execution-correct" or "execution-retired")) return 64;
        Console.WriteLine("started");
        Console.Out.Flush();
        var entryReport = Report(buy: true, price: operation == "execution-correct" ? 102m : 100m,
            fee: operation == "execution-correct" ? 4m : 3m);
        var recoveredEntry = context.Resolve(entryReport) ?? throw new IOException("Fresh process did not recover the exact native order owner.");
        var projector = AccountExecutionProjector.For(source, context);
        projector.Synchronize();
        source.Publish(entryReport);
        var exitReport = Report(buy: false, price: 105m, fee: 3m);
        if (context.Resolve(exitReport) == null)
        {
            if (operation == "execution-retired") throw new IOException("Retired fixture has no existing exit lineage.");
            Prepare(recoveredEntry.Owner, buy: false);
        }
        source.Publish(exitReport);
        projector.Synchronize();
        if (context.PendingReceipts().Count != 0 || context.HasStorageFault) throw new IOException(context.LastError ?? "Execution recovery left an unacknowledged durable receipt.");
        var ledger = LiveStore.LoadLedger(LiveStore.Root, Key, pointValue);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ledger.Fills, ledger.Position, ledger.AveragePrice, ledger.Realized, OpenPnL = ledger.OpenPnL(110m),
            ledger.SkippedLines, Pending = LiveStore.PendingLedgerEvents(Key),
            DispatchUncertain = recoveredEntry.DispatchUncertain, ClosedTrades = ledger.Trades.Count,
            Quality = ledger.FinancialQuality, ledger.AuthoritativeNet, LedgerExists = File.Exists(LiveStore.LedgerFile(Key)),
        }));
        Console.Out.Flush();
        return 0;

        PreparedOrderIntent Prepare(OrderOwner owner, bool buy)
        {
            var ticket = context.Prepare(owner, new OrderRequest(instrument, buy ? OrderDirections.Buy : OrderDirections.Sell,
                OrderTypes.Limit, 2m, buy ? 103m : 104m, ReduceOnly: !buy, StrategyQuantity: 2m, MaximumOwnedQuantity: 2m));
            if (ticket.Error != null) throw new IOException(ticket.Error);
            if (context.StartDispatch(ticket, "TEST", "FAKE") is { } dispatchError) throw new IOException(dispatchError);
            if (context.BindNative(ticket, new NativeOrderIdentity("fixture:order", buy ? "entry" : "exit")) is { } bindingError) throw new IOException(bindingError);
            return ticket;
        }

        ExecutionReport Report(bool buy, decimal price, decimal fee) => new(
            new ExecutionIdentity(new ExecutionScope(context.Descriptor.Provider, "fixture-account", "loopback-fixture", instrument)
                { ContextId = context.Descriptor.Id }, buy ? "native-entry-execution" : "native-exit-execution"),
            buy ? 1 : 2, "TEST", buy ? Time : Time.AddMinutes(1), buy, 2m, price, pointValue,
            "USD", new[] { new ExecutionFee(fee, "USD") }, true)
        {
            ContextId = context.Descriptor.Id, NativeOrder = new NativeOrderIdentity("fixture:order", buy ? "entry" : "exit"),
        };
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
