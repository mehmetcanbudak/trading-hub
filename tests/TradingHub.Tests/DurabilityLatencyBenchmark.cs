using System.Diagnostics;
using System.Text;
using TradingHub.Common;
using TradingHub.Strategies;
using TradingHub.Trading;
using Xunit.Abstractions;

namespace TradingHub.Tests;

/// <summary>Opt-in latency measurement of durable order-path writers on the temp volume (the internal SSD on a
/// standard Mac). Set TRADINGHUB_DURABILITY_BENCHMARK=1; otherwise it returns immediately so the suite stays fast.
/// It reports numbers only and asserts no timing, so machine load cannot make it flaky.</summary>
[Collection("LiveStore")]
public sealed class DurabilityLatencyBenchmark(ITestOutputHelper output)
{
    private const int Warmup = 10;
    private const int Iterations = 200;

    [Fact]
    public void Measures_durable_writer_latency_when_requested()
    {
        if (Environment.GetEnvironmentVariable("TRADINGHUB_DURABILITY_BENCHMARK") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-durability-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            output.WriteLine($"Durability latency on {RuntimeEnvironment()} at {root}; {Iterations} samples after {Warmup} warmups.");
            byte[] record = Encoding.UTF8.GetBytes(new string('x', 180) + "\n");
            Primitive(root, "append + Flush(false) (no disk request)", record, file => file.Flush(false), syncDirectory: false);
            Primitive(root, "append + Flush(true) (fsync)", record, file => file.Flush(true), syncDirectory: false);
            Primitive(root, "append + StorageSync.Flush (F_FULLFSYNC)", record, file => StorageSync.Flush(file), syncDirectory: false);
            Primitive(root, "append + StorageSync.Flush + SyncDirectory", record, file => StorageSync.Flush(file), syncDirectory: true);

            var journal = new AccountJournal(Path.Combine(root, "journal"));
            var time = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
            int fill = 0;
            Report("AccountJournal.Add", () => Assert.True(journal.Add("benchmark",
                new AccountFill("fill-" + fill++, time.AddSeconds(fill), "TEST", true, 1m, 100m, 1m))));

            var context = new OrderContext(new("benchmark-context", "simulator", "paper", "benchmark", "benchmark", "generation"),
                Path.Combine(root, "execution"));
            var owner = context.ManualOwner;
            Report("OrderContext.Prepare (durable intent)", () => Assert.Null(context.Prepare(owner,
                new("test:TEST", OrderDirections.Buy, OrderTypes.Limit, 1m, 100m)).Error));

            string live = Path.Combine(root, "live");
            int ledger = 0;
            Report("LiveStore.AppendFill (WAL + ledger)", () => Assert.Null(LiveStore.AppendFill(live, "benchmark|test:TEST|sim",
                new LiveFill(time.AddSeconds(ledger++), ledger % 2 == 0, 1m, 100m))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private void Primitive(string root, string name, byte[] record, Action<FileStream> flush, bool syncDirectory)
    {
        string directory = Path.Combine(root, "primitive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var file = new FileStream(Path.Combine(directory, "append.jsonl"), FileMode.Append, FileAccess.Write, FileShare.Read, 4096);
        Report(name, () =>
        {
            file.Write(record);
            flush(file);
            if (syncDirectory) StorageSync.SyncDirectory(directory);
        });
    }

    private void Report(string name, Action operation)
    {
        for (int i = 0; i < Warmup; i++) operation();
        var samples = new double[Iterations];
        for (int i = 0; i < Iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            operation();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(samples);
        output.WriteLine($"{name,-48} median {samples[Iterations / 2],8:F3} ms  p95 {samples[Iterations * 95 / 100],8:F3} ms  " +
            $"max {samples[^1],8:F3} ms  mean {samples.Average(),8:F3} ms");
    }

    private static string RuntimeEnvironment() =>
        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}";
}
