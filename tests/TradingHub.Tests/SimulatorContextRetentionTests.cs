using System.Diagnostics;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>Each process composes its own simulator ownership context (the simulated portfolio is in memory).
/// Earlier processes' idle simulator contexts must not accumulate; live, nonempty or non-simulator contexts stay.</summary>
public sealed class SimulatorContextRetentionTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-simulator-contexts-" + Guid.NewGuid().ToString("N"));
    private int _children;

    public SimulatorContextRetentionTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".ledger-fixture-root"), "simulator context fixture");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Live => Path.Combine(_root, "Live");

    [Fact]
    public async Task Successive_simulator_compositions_leave_only_the_current_process_context()
    {
        await RunToExit("exit");
        string second = await RunToExit("exit");
        Assert.Equal(new[] { OrderContext.Hash(second) }, Contexts());

        var current = OrderContext.ProcessSimulation("SIM-1", Live);
        Assert.Equal(new[] { OrderContext.Hash(current.Descriptor.Id) }, Contexts());
    }

    [Fact]
    public async Task A_live_process_context_is_kept_until_that_process_ends()
    {
        using var held = Start("hold");
        var error = held.StandardError.ReadToEndAsync();
        string heldId = Assert.IsType<string>(await held.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
        Assert.Equal("checkpoint", await held.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
        OrderContext current;
        try
        {
            current = OrderContext.ProcessSimulation("SIM-1", Live);
            Assert.Equal(Sorted(OrderContext.Hash(heldId), OrderContext.Hash(current.Descriptor.Id)), Contexts());
        }
        finally
        {
            await held.StandardInput.WriteLineAsync("continue");
            await held.StandardInput.FlushAsync();
            await held.WaitForExitAsync().WaitAsync(Deadline);
        }
        Assert.True(held.ExitCode == 0, await error);

        // The ended process's context is removed; this process's own live context is not.
        var next = OrderContext.ProcessSimulation("SIM-1", Live);
        Assert.Equal(Sorted(OrderContext.Hash(current.Descriptor.Id), OrderContext.Hash(next.Descriptor.Id)), Contexts());
        GC.KeepAlive(current);
    }

    [Fact]
    public async Task Intents_non_simulator_and_unreadable_contexts_are_kept()
    {
        string withIntent = await RunToExit("intent");
        var native = new OrderContext(new AccountContextDescriptor("retained-native", "Rithmic", "futures", "loopback-retained",
            "retained-native", "retained-native-generation"), Live);
        string unreadable = Path.Combine(Live, "execution-state", "unreadable-context");
        Directory.CreateDirectory(unreadable);
        File.WriteAllText(Path.Combine(unreadable, "context.json"), "not an ownership record");
        await RunToExit("exit");

        var current = OrderContext.ProcessSimulation("SIM-1", Live);
        Assert.Equal(Sorted(OrderContext.Hash(withIntent), OrderContext.Hash(native.Descriptor.Id), "unreadable-context",
            OrderContext.Hash(current.Descriptor.Id)), Contexts());
        Assert.Single(Directory.GetFiles(Path.Combine(Live, "execution-state", OrderContext.Hash(withIntent), "intent")));
        Assert.True(File.Exists(Path.Combine(Live, "execution-state", OrderContext.Hash(native.Descriptor.Id), "context.json")));
        Assert.Equal("not an ownership record", File.ReadAllText(Path.Combine(unreadable, "context.json")));
    }

    private string[] Contexts()
    {
        string directory = Path.Combine(Live, "execution-state");
        return Directory.Exists(directory) ? Sorted(Directory.GetDirectories(directory).Select(Path.GetFileName).ToArray()!) : Array.Empty<string>();
    }

    private static string[] Sorted(params string[] names) => names.Order(StringComparer.Ordinal).ToArray();

    private async Task<string> RunToExit(string mode)
    {
        using var child = Start(mode);
        var error = child.StandardError.ReadToEndAsync();
        string id = Assert.IsType<string>(await child.StandardOutput.ReadLineAsync().WaitAsync(Deadline));
        await child.WaitForExitAsync().WaitAsync(Deadline);
        Assert.True(child.ExitCode == 0, await error);
        return id;
    }

    private Process Start(string mode)
    {
        string scratch = Path.Combine(_root, "child-" + ++_children);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LedgerCrashFixture", "TradingHub.LedgerCrashFixture.dll"));
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add("simulator-context");
        start.ArgumentList.Add(mode);
        foreach (string store in new[] { "DATA", "STRATEGIES", "INDICATORS", "BACKTESTS", "LIVE" })
            start.Environment["TRADINGHUB_" + store + "_DIR"] = Path.Combine(scratch, store.ToLowerInvariant());
        start.Environment["TRADINGHUB_KEYCHAIN_FILE"] = Path.Combine(scratch, "fake-keychain.json");
        start.Environment["TRADINGHUB_SLEEP_GUARD"] = "off";
        return Process.Start(start)!;
    }
}
