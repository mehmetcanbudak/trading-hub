using System.Text;
using TradingHub.App.Services;

namespace TradingHub.Tests;

public sealed class PineImportTests
{
    [Fact]
    public async Task ImportPreservesOriginalBytesAndDuplicateNamesWithoutCreatingExecutableSource()
    {
        using var fixture = new PineFixture();
        byte[] original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("//@version=5\r\n// Copyright author\r\nindicator(\"Original\")\r\nplot(close)\r\n")).ToArray();
        using var firstInput = new MemoryStream(original);
        using var secondInput = new MemoryStream(original);
        var first = await PineImports.PreserveAsync(firstInput, "source.pine", fixture.Root);
        var second = await PineImports.PreserveAsync(secondInput, "SOURCE.pine", fixture.Root);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(original, File.ReadAllBytes(first.Path));
        Assert.Equal(original, File.ReadAllBytes(second.Path));
        Assert.Contains("// Copyright author\r\n", first.Text);
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.cs", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PreviewDeclineAndPreCanceledActionHaveNoEgressAndDoNotCreateAssistantWorkspace()
    {
        using var fixture = new PineFixture();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("//@version=5\nindicator(\"Local\")\nplot(close)"));
        var source = await PineImports.PreserveAsync(input, "Local.pine", fixture.Root);
        var preview = PineImports.Preview(source, "codex", "selected-model", "high");
        var bridge = new DraftBridge("codex");
        Assert.Null(await PineAssistantHandoff.SendAsync(preview, approved: false, bridge));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PineAssistantHandoff.SendAsync(preview, approved: true, bridge, canceled.Token));
        Assert.Equal(0, bridge.Calls);
        Assert.Null(bridge.Request);
        Assert.Single(Directory.EnumerateFiles(fixture.Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task ExplicitSendUsesExactlyTheReviewedSourceAndSelectedAssistantWithoutExecutingOrPersistingTheDraft(string engine)
    {
        using var fixture = new PineFixture();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("//@version=5\n// Keep this attribution\nindicator(\"Reviewed\")\nplot(close)"));
        var source = await PineImports.PreserveAsync(input, "Reviewed.pine", fixture.Root);
        var preview = PineImports.Preview(source, engine, "chosen-model", "chosen-effort");
        // A later disk edit is not quietly substituted for the content the user approved.
        File.WriteAllText(source.Path, "different private source that was not previewed");
        var bridge = new DraftBridge(engine);
        var result = await PineAssistantHandoff.SendAsync(preview, approved: true, bridge);
        Assert.Equal(StrategyChatStatus.Succeeded, result!.Status);
        Assert.Equal(1, bridge.Calls);
        Assert.Equal(preview.Prompt, bridge.Request!.Prompt);
        Assert.Contains("plot(close)", bridge.Request.Prompt);
        Assert.DoesNotContain("different private source", bridge.Request.Prompt);
        Assert.True(bridge.Request.TextOnly);
        Assert.False(bridge.Request.Laboratory);
        Assert.Null(bridge.Request.TradingView);
        Assert.Empty(bridge.Request.ExtraDirectories);
        Assert.Equal("chosen-model", bridge.Request.Model);
        Assert.Equal("chosen-effort", bridge.Request.Effort);
        Assert.False(Directory.Exists(bridge.Request.WorkDirectory));
        Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.cs", SearchOption.AllDirectories));
        Assert.Equal("different private source that was not previewed", File.ReadAllText(source.Path));
        Assert.Contains("This is only unvalidated draft text", result.Text);
    }

    [Fact]
    public async Task ActiveCancellationDrainsTheRequestAndRemovesOnlyItsTemporaryWorkspace()
    {
        using var fixture = new PineFixture();
        var source = new PineImportSource(Path.Combine(fixture.Root, "source.pine"), "source.pine", "plot(close)", "fixture");
        File.WriteAllText(source.Path, source.Text);
        var bridge = new DraftBridge("codex") { Hold = true };
        using var cancellation = new CancellationTokenSource();
        var pending = PineAssistantHandoff.SendAsync(PineImports.Preview(source, "codex", null, null), true, bridge, cancellation.Token);
        await bridge.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Directory.Exists(bridge.Request!.WorkDirectory));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(Directory.Exists(bridge.Request.WorkDirectory));
        Assert.Equal("plot(close)", File.ReadAllText(source.Path));
    }

    [Fact]
    public void TextOnlyRequestsCannotAccidentallyGrantLabResearchOrUserDirectories()
    {
        Assert.Throws<ArgumentException>(() => new StrategyChatRequest("source", "workspace", "instructions", laboratory: true, textOnly: true));
        Assert.Throws<ArgumentException>(() => new StrategyChatRequest("source", "workspace", "instructions", extraDirectories: new[] { "user-indicators" }, textOnly: true));
    }

    [Fact]
    public async Task AppOwnershipRegistersBeforePendingReviewAwaitRejectsNewSendsAndWaitsForItsCancellationDrain()
    {
        using var fixture = new PineFixture();
        var owner = new PineHandoffRegistry();
        var source = new PineImportSource(Path.Combine(fixture.Root, "source.pine"), "source.pine", "plot(close)", "fixture");
        var preview = PineImports.Preview(source, "codex", null, null);
        var bridge = new DraftBridge("codex");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        Task pending = owner.RunAsync(async cancellation =>
        {
            captured = cancellation;
            await release.Task; // a pending UI preview/confirmation has not chosen the egress action
            await PineAssistantHandoff.SendAsync(preview, true, bridge, cancellation);
        });
        Task stopped = owner.StopAsync();
        Assert.True(captured.IsCancellationRequested);
        Assert.False(stopped.IsCompleted);
        Assert.Equal(0, bridge.Calls);
        Assert.Throws<ObjectDisposedException>(() => { _ = owner.RunAsync(_ => Task.CompletedTask); });
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await stopped;
        await owner.StopAsync();
        Assert.Equal(0, bridge.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Root));
    }

    private sealed class DraftBridge(string engine) : IStrategyChatBridge
    {
        public string Engine => engine;
        public string? SessionId => null;
        public bool IsRunning => false;
        public int Calls { get; private set; }
        public StrategyChatRequest? Request { get; private set; }
        public bool Hold { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<string>? Text { add { } remove { } }
        public event Action<string, string?>? ToolUsed { add { } remove { } }
        public event Action? ToolFinished { add { } remove { } }
        public event Action<StrategyChatResult>? Finished { add { } remove { } }
        public void NewConversation() { }
        public void Resume(string? sessionId) => throw new NotSupportedException();
        public void Cancel() { }
        public async Task<StrategyChatResult> SendAsync(StrategyChatRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Request = request;
            Assert.True(Directory.Exists(request.WorkDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(request.WorkDirectory));
            Started.TrySetResult();
            if (Hold) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new(StrategyChatStatus.Succeeded, "// This is only unvalidated draft text\npublic class NotAutomaticallyCompiled { }", null, 0, false);
        }
    }
    private sealed class PineFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pine-import-" + Guid.NewGuid().ToString("N"));
        public PineFixture() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
