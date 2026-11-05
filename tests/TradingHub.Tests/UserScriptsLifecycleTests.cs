using System.Collections.Concurrent;
using System.Runtime.Loader;
using System.Text;
using TradingHub.App.Services;
using TradingHub.Indicators;
using TradingHub.Scripting;

namespace TradingHub.Tests;

public sealed class UserScriptsLifecycleTests
{
    [Fact]
    public async Task RecursiveHotReloadDetectsSameSizeAtomicRenameAndDeleteAndRetainsLastGoodAfterFailure()
    {
        using var fixture = new ScriptFixture();
        using var scripts = new UserScripts(fixture.Catalog, fixture.Root, fixture.Queue.Enqueue);
        Assert.True(scripts.CompileAll()!.Success);
        Assert.Equal(7, fixture.Value());
        string path = fixture.Path;
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, Source(9)); // same size and same timestamp are not mistaken for unchanged content
        File.SetLastWriteTimeUtc(path, stamp);
        await fixture.RunNextAsync();
        Assert.Equal(9, fixture.Value());
        string replacement = path + ".atomic-save";
        File.WriteAllText(replacement, Source(8));
        File.Move(replacement, path, overwrite: true);
        await fixture.RunNextAsync();
        Assert.Equal(8, fixture.Value());
        var lastGoodType = Assert.Single(fixture.Catalog.Items).Type;
        File.WriteAllText(path, "this is not compilable C#");
        await fixture.RunNextAsync();
        Assert.False(scripts.LastResult!.Success);
        Assert.Equal(lastGoodType, Assert.Single(fixture.Catalog.Items).Type);
        Assert.Equal(8, fixture.Value());
        File.WriteAllText(path, Source(6));
        await fixture.RunNextAsync();
        Assert.True(scripts.LastResult!.Success);
        Assert.Equal(6, fixture.Value());
        string nested = Directory.CreateDirectory(System.IO.Path.Combine(fixture.Root, "nested", "deeper")).FullName;
        string additional = System.IO.Path.Combine(nested, "Recursive.cs");
        File.WriteAllText(additional, Source(4, "RecursiveProbe"));
        await fixture.RunNextAsync();
        Assert.Equal(2, fixture.Catalog.Items.Count);
        string renamed = System.IO.Path.Combine(nested, "FileOnlyRename.cs");
        File.Move(additional, renamed);
        await fixture.RunNextAsync();
        Assert.Equal(2, fixture.Catalog.Items.Count);
        Assert.Contains(fixture.Catalog.Items, i => i.Type.FullName == "HotReload.RecursiveProbe");
        File.Delete(renamed);
        await fixture.RunNextAsync();
        Assert.Single(fixture.Catalog.Items);
        File.Delete(path);
        await fixture.RunNextAsync();
        Assert.Empty(fixture.Catalog.Items);
    }

    [Fact]
    public async Task QuietDebounceCoalescesRepeatedSavesAndDisposedQueuedCallbacksCannotCompileOrPublish()
    {
        using var fixture = new ScriptFixture();
        using var scripts = new UserScripts(fixture.Catalog, fixture.Root, fixture.Queue.Enqueue);
        int published = 0;
        scripts.Compiled += _ => published++;
        Assert.True(scripts.CompileAll()!.Success);
        File.WriteAllText(fixture.Path, Source(8));
        await Task.Delay(300);
        File.WriteAllText(fixture.Path, Source(9));
        await fixture.WaitQueuedAsync();
        Assert.Single(fixture.Queue);
        Assert.Equal(1, published);
        Assert.Equal(7, fixture.Value());
        scripts.Dispose();
        Assert.True(fixture.Queue.TryDequeue(out var queued));
        queued!();
        scripts.CompileAll(); // explicit commands after true shutdown are inert too
        Assert.Equal(1, published);
        Assert.Equal(7, fixture.Value());
        await Task.Delay(300);
        Assert.Empty(fixture.Queue);
        scripts.Dispose();
    }

    [Fact]
    public async Task AsyncShutdownRejectsNewWorkAndDrainsConcurrentActiveCompilerWithoutPublishingItsCanceledGeneration()
    {
        using var fixture = new ScriptFixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool hold = false;
        int compileCalls = 0;
        var compiler = new ScriptCompiler();
        var scripts = new UserScripts(fixture.Catalog, fixture.Root, fixture.Queue.Enqueue, (sources, token) =>
        {
            Interlocked.Increment(ref compileCalls);
            if (Volatile.Read(ref hold))
            {
                entered.Set();
                release.Wait();
                // A slow adapter may not honor cancellation. Even its real successful emission must not publish late.
                return compiler.CompileSources(sources, "LifecycleConsumer");
            }
            return compiler.CompileSources(sources, "LifecycleConsumer", token);
        });
        try
        {
            Assert.True(scripts.CompileAll()!.Success);
            int published = 0;
            scripts.Compiled += _ => published++;
            File.WriteAllText(fixture.Path, Source(9));
            Volatile.Write(ref hold, true);
            Task<ScriptCompileResult?> active = Task.Run(scripts.CompileAll);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task first = scripts.StopAsync();
            Task second = scripts.StopAsync();
            Assert.True(scripts.CompileAll()!.Success); // rejected immediately, not queued behind the held compiler
            Assert.Equal(2, Volatile.Read(ref compileCalls));
            try
            {
                await Task.Delay(100);
                Assert.False(first.IsCompleted);
                Assert.False(second.IsCompleted);
            }
            finally { release.Set(); }
            await Task.WhenAll(first, second, active).WaitAsync(TimeSpan.FromSeconds(30));
            while (fixture.Queue.TryDequeue(out var action)) action();
            Assert.Equal(0, published);
            Assert.Equal(7, fixture.Value());
        }
        finally { release.Set(); await scripts.DisposeAsync(); }
    }

    [Fact]
    public async Task MissingOrUnreadableRootRetainsLastGoodAndReadRetryRecoversWithoutAnotherEdit()
    {
        using var fixture = new ScriptFixture();
        await using var scripts = new UserScripts(fixture.Catalog, fixture.Root, fixture.Queue.Enqueue);
        var initial = scripts.CompileAll();
        Assert.True(initial!.Success);
        string unavailable = fixture.Root + ".unavailable";
        Directory.Move(fixture.Root, unavailable);
        try
        {
            Assert.Same(initial, scripts.CompileAll());
            await Task.Delay(1100);
            Assert.Same(initial, scripts.LastResult);
            Assert.Equal(7, fixture.Value());
        }
        finally { Directory.Move(unavailable, fixture.Root); }
        await fixture.RunNextAsync();
        Assert.True(scripts.LastResult!.Success);
        Assert.NotSame(initial, scripts.LastResult);
        Assert.Equal(7, fixture.Value());

        var readable = scripts.LastResult;
        using (var held = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Same(readable, scripts.CompileAll());
            Assert.Equal(7, fixture.Value());
        }
        await fixture.RunNextAsync();
        Assert.True(scripts.LastResult!.Success);
        Assert.NotSame(readable, scripts.LastResult);
        File.Delete(fixture.Path);
        var prior = scripts.LastResult;
        Assert.Null(scripts.CompileAll());
        Assert.Same(prior, scripts.LastResult);
        Assert.Empty(fixture.Catalog.Items);
    }

    [Fact]
    public async Task ReentrantDisposeStopsRemainingCompiledSubscribersAndAsyncStopStillObservesFullDrain()
    {
        using var fixture = new ScriptFixture();
        await using var scripts = new UserScripts(fixture.Catalog, fixture.Root, fixture.Queue.Enqueue);
        int first = 0, late = 0;
        scripts.Compiled += _ => { first++; scripts.Dispose(); };
        scripts.Compiled += _ => late++;
        var result = scripts.CompileAll();
        Assert.True(result!.Success);
        Assert.Equal(1, first);
        Assert.Equal(0, late);
        Assert.Same(result, scripts.CompileAll());
        await scripts.StopAsync();
        Assert.Equal(0, late);
    }

    [Fact]
    public void CapturedCompilerInputsNeverReopenChangedOrMissingDiagnosticPathsAndKeepSourceCoordinates()
    {
        using var fixture = new ScriptFixture();
        var captured = new ScriptSource(fixture.Path, File.ReadAllText(fixture.Path));
        File.WriteAllText(fixture.Path, Source(9));
        File.Delete(fixture.Path);
        var compiler = new ScriptCompiler();
        var compiled = compiler.CompileSources([captured], "ReviewedCapturedInput");
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        try
        {
            fixture.Catalog.Scan(compiled.Assembly!, UserScripts.Source);
            Assert.Equal(7, fixture.Value());
        }
        finally { AssemblyLoadContext.GetLoadContext(compiled.Assembly!)!.Unload(); }

        File.WriteAllText(fixture.Path, Source(9));
        var invalid = new ScriptSource(fixture.Path, "public class BrokenCapture\n{\n    missing_type Value;\n}");
        var diagnostics = compiler.CompileSources([invalid], "ReviewedCapturedDiagnostic");
        Assert.False(diagnostics.Success);
        Assert.Contains(diagnostics.Diagnostics, diagnostic => diagnostic.Id == "CS0246" &&
            diagnostic.File == fixture.Path && diagnostic.Line == 3 && diagnostic.Column == 5);
        File.Delete(fixture.Path);
        var skipped = compiler.Compile([fixture.Path], "ReviewedVanishedFile");
        Assert.True(skipped.Success);
        Assert.Null(skipped.Assembly!.GetType("HotReload.Probe"));
        AssemblyLoadContext.GetLoadContext(skipped.Assembly)!.Unload();
    }

    [Fact]
    public async Task ImmutableByteSnapshotsPreserveBomDecodingAndUnchangedContentDoesNotRecompile()
    {
        using var fixture = new ScriptFixture();
        File.WriteAllText(fixture.Path, Source(9) + "\n// UTF-16 Ω\n", Encoding.Unicode);
        await using var scripts = new UserScripts(fixture.Catalog, fixture.Root, fixture.Queue.Enqueue);
        var initial = scripts.CompileAll();
        Assert.True(initial!.Success);
        Assert.Equal(9, fixture.Value());
        await Task.Delay(1100);
        Assert.Empty(fixture.Queue);
        Assert.Same(initial, scripts.LastResult);
    }

    [Fact]
    public async Task NewerReconciledGenerationCannotBeOverwrittenByAnOlderRealCompilerResult()
    {
        using var fixture = new ScriptFixture();
        var compiler = new ScriptCompiler();
        UserScripts? scripts = null;
        ScriptCompileResult? newer = null;
        bool replace = false, inside = false;
        scripts = new UserScripts(fixture.Catalog, fixture.Root, fixture.Queue.Enqueue, (captured, cancellation) =>
        {
            if (replace && !inside)
            {
                inside = true;
                File.WriteAllText(fixture.Path, Source(9));
                newer = scripts!.CompileAll(); // reconcile and compile the later version while older captured inputs still exist
            }
            return compiler.CompileSources(captured, "ReviewedGeneration", cancellation);
        });
        try
        {
            Assert.True(scripts.CompileAll()!.Success);
            int published = 0;
            scripts.Compiled += _ => published++;
            File.WriteAllText(fixture.Path, Source(8));
            replace = true;
            var result = scripts.CompileAll();
            Assert.True(newer!.Success);
            Assert.Same(newer, result);
            Assert.Same(newer, scripts.LastResult);
            Assert.Equal(9, fixture.Value());
            Assert.Equal(1, published);
        }
        finally { await scripts.DisposeAsync(); }
    }

    private static string Source(int value, string name = "Probe") => $$"""
        using TradingHub.Indicators;
        namespace HotReload;
        public class {{name}} : Indicator
        {
            public int Value => {{value}};
            protected override void OnCalculate(int bar, decimal value) { }
        }
        """;

    private sealed class ScriptFixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "user-scripts-lifetime-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "Probe.cs");
        public IndicatorCatalog Catalog { get; } = new();
        public ConcurrentQueue<Action> Queue { get; } = new();
        public ScriptFixture() { Directory.CreateDirectory(Root); File.WriteAllText(Path, Source(7)); }
        public int Value()
        {
            Type type = Catalog.Items.Single(i => i.Type.FullName == "HotReload.Probe").Type;
            using var instance = (Indicator)Activator.CreateInstance(type)!;
            return (int)type.GetProperty("Value")!.GetValue(instance)!;
        }
        public async Task WaitQueuedAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { while (Queue.IsEmpty) await Task.Delay(25, deadline.Token); }
            catch (OperationCanceledException) { Assert.Fail("The recursive source monitor did not dispatch its debounced source change."); }
        }
        public async Task RunNextAsync()
        {
            await WaitQueuedAsync();
            Assert.True(Queue.TryDequeue(out var action));
            action!();
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
