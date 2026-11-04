using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Data;

internal static class IbRefreshScene
{
    public static int Run(MainWindow window, Action<double> pump, string output)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR")))
            throw new InvalidOperationException("Data refresh proof requires an explicit isolated root.");
        var start = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        string seed = Path.Combine(AppPaths.Root, "owned-refresh-seed"), incoming = Path.Combine(AppPaths.Root, "owned-refresh-incoming");
        Directory.CreateDirectory(seed);
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(seed, "MNQ.csv"), $"time,open,high,low,close,volume\n{new DateTimeOffset(start).ToUnixTimeSeconds()},100,102,99,101,12\n");
        File.WriteAllText(Path.Combine(incoming, "MNQ.csv"), $"time,open,high,low,close,volume\n{new DateTimeOffset(start.AddDays(1)).ToUnixTimeSeconds()},200,202,199,201,20\n");
        IbBarImport.Commit(IbBarImport.PreviewFolder(seed, AppPaths.Ticks, "MNQ"), AppPaths.Ticks);
        window.Navigate("data");
        pump(0.1);
        var view = window.GetVisualDescendants().OfType<DataView>().Single(v => v.IsEffectivelyVisible);
        int VisibleDays() => view.GetVisualDescendants().OfType<ListBox>().SelectMany(box => box.Items.OfType<ListBoxItem>())
            .Select(item => item.Tag).OfType<SymbolData>().SingleOrDefault(symbol => symbol.FeedId == "ib" && symbol.Symbol == "MNQ")?.Days.Count ?? 0;
        var initialDeadline = DateTime.UtcNow.AddSeconds(30);
        while (VisibleDays() != 1 && DateTime.UtcNow < initialDeadline) pump(0.01);
        if (VisibleDays() != 1) throw new InvalidOperationException("The initial data index did not show the owned day.");
        var preview = IbBarImport.PreviewFolder(incoming, AppPaths.Ticks, "MNQ");
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = Task.Run(() => IbBarImport.Commit(preview, AppPaths.Ticks, ct: watchdog.Token, beforePublish: _ =>
        {
            entered.TrySetResult();
            release.Wait(watchdog.Token);
        }));
        bool responsive = false;
        string? importError = null;
        try
        {
            while (!entered.Task.IsCompleted && !publication.IsCompleted && !watchdog.IsCancellationRequested) pump(0.01);
            if (!entered.Task.IsCompleted) throw new InvalidOperationException("The owned import did not reach its publication checkpoint.");
            var refresh = view.GetVisualDescendants().OfType<Button>().Single(button => button.IsEffectivelyVisible && ToolTip.GetTip(button) as string == "Refresh");
            Dispatcher.UIThread.Post(() =>
            {
                responsive = !publication.IsCompleted && !watchdog.IsCancellationRequested;
                release.Set(); // Only a responsive UI can release the held import before its safety watchdog.
            });
            refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            while (!publication.IsCompleted) pump(0.01);
            try { publication.GetAwaiter().GetResult(); }
            catch (Exception error) { importError = error.GetType().Name + ": " + error.Message; }
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (responsive && VisibleDays() != 2 && DateTime.UtcNow < deadline) pump(0.01);
            pump(0.1);
            bool passed = responsive && importError == null && VisibleDays() == 2;
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Data surface did not render.");
            frame.Save(Path.Combine(output, "ib_data_refresh_responsiveness.png"));
            string evidence = JsonSerializer.Serialize(new { passed, uiReleasedHeldImport = responsive, visibleDays = VisibleDays(), expectedDays = 2, importError,
                authority = "Actual DataView refresh and owned public import; causal UI-release handshake, not an elapsed-time benchmark" }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "ib-data-refresh-evidence.json"), evidence);
            Console.WriteLine(evidence);
            return passed ? 0 : 1;
        }
        finally
        {
            release.Set();
            while (!publication.IsCompleted) pump(0.01);
            try { publication.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}
