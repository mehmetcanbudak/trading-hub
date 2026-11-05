using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;

namespace TradingHub.App.Windows;

/// <summary>Import and navigation are local-only. Sending requires a separate cancellation-default confirmation.</summary>
internal sealed class PineReviewWindow : Window
{
    private readonly PineAssistantPreview _preview;
    private readonly Func<IStrategyChatBridge> _bridgeFactory;
    private readonly TextBox _draft = ReadOnlyText("");
    private readonly TextBlock _status = new() { Text = "Nothing has been sent.", TextWrapping = TextWrapping.Wrap };
    private readonly Button _send;
    private readonly Button _cancel;
    private readonly TabControl _tabs;
    private readonly CancellationToken _applicationStop;
    private readonly CancellationTokenRegistration _stopRegistration;
    private IStrategyChatBridge? _bridge;
    private CancellationTokenSource? _cancellation;
    private Task? _active;
    private bool _closingAfterDrain;
    private Task? _closeTask;

    private PineReviewWindow(PineAssistantPreview preview, Func<IStrategyChatBridge>? bridgeFactory, CancellationToken applicationStop)
    {
        _preview = preview;
        _bridgeFactory = bridgeFactory ?? (() => StrategyChatEngines.Create(preview.Engine));
        _applicationStop = applicationStop;
        Title = "Pine source and assistant egress preview";
        Width = 980;
        Height = 760;
        MinWidth = 700;
        MinHeight = 480;
        Background = Ui.Bg0;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Window;
        var disclosure = new TextBlock
        {
            Text = $"Local source preserved at {preview.Source.Path}\nSelected assistant: {preview.AssistantTitle} · model: {(string.IsNullOrWhiteSpace(preview.Model) ? "CLI default" : preview.Model)} · effort: {(string.IsNullOrWhiteSpace(preview.Effort) ? "CLI default" : preview.Effort)}\n" +
                "Trading Hub hands the reviewed source, request and application instructions below to your selected CLI. The CLI may add its own system/account context and retain content under its policies; provider billing or subscription limits apply. This handoff grants no strategy/indicator directories, laboratory or research access.\n" +
                "This is AI-assisted drafting, not a Pine compiler/runtime or an equivalence guarantee. Responses stay as text; nothing is saved as C#, compiled or executed automatically.",
            TextWrapping = TextWrapping.Wrap,
            Classes = { "muted", "small" },
        };
        _tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Original Pine source", Content = ReadOnlyText(preview.Source.Text) },
                new TabItem { Header = "Request to send", Content = ReadOnlyText(preview.Prompt) },
                new TabItem { Header = "Assistant instructions", Content = ReadOnlyText(preview.InstructionsPreview) },
                new TabItem { Header = "Assistant draft (text only)", Content = _draft },
            },
        };
        _send = Ui.Button($"Send source to {preview.AssistantTitle}", MaterialIconKind.Send, BeginSend, "primary");
        _send.IsDefault = false;
        _cancel = Ui.Button("Close without sending", MaterialIconKind.Close, () =>
        {
            if (_active is { IsCompleted: false }) _cancellation?.Cancel();
            else Close();
        });
        _cancel.IsDefault = true;
        _cancel.IsCancel = true;
        var bar = Ui.H(10, _cancel, _send);
        bar.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = Ui.V(8, _status, bar);
        footer.Margin = new Thickness(14);
        var header = Ui.V(8, Ui.H3("Review before sharing source"), disclosure);
        header.Margin = new Thickness(14);
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(_tabs);
        Content = root;
        Closing += (_, e) =>
        {
            if (_closingAfterDrain || _active is not { IsCompleted: false }) return;
            e.Cancel = true;
            _cancellation?.Cancel();
            _closeTask ??= DrainAndCloseAsync();
        };
        _stopRegistration = applicationStop.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _send.IsEnabled = false;
            Close();
        }));
        Closed += (_, _) => _stopRegistration.Dispose();
    }

    /// <summary>Registration precedes callback execution/first await, including an unanswered consent dialog.</summary>
    internal static Task ShowOwnedAsync(AppServices services, Window owner, PineAssistantPreview preview,
        Func<IStrategyChatBridge>? bridgeFactory = null) => services.PineHandoffs.RunAsync(async cancellation =>
    {
        cancellation.ThrowIfCancellationRequested();
        var review = new PineReviewWindow(preview, bridgeFactory, cancellation);
        try { await review.ShowDialog(owner); }
        finally
        {
            try { if (review._active != null) await review._active; }
            finally { if (review._closeTask != null) await review._closeTask; }
        }
    });

    private void BeginSend()
    {
        if (_applicationStop.IsCancellationRequested || _active is { IsCompleted: false }) return;
        _active = SendAsync();
        _ = ObserveSendAsync(_active);
    }

    private async Task ObserveSendAsync(Task active)
    {
        try { await active; }
        catch (Exception error) { _status.Text = "The assistant did not drain cleanly: " + error.Message; }
    }

    private async Task SendAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_applicationStop);
        _cancellation = cancellation;
        _send.IsEnabled = false;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (!await ConfirmDialog.ShowAsync(this, $"Send source to {_preview.AssistantTitle}",
                    "This shares the reviewed Pine source and instructions with your selected CLI's model provider using your own session. Charges or subscription limits may apply. The response is an unvalidated text draft only.",
                    "Send reviewed source", cancelByDefault: true, cancellationToken: cancellation.Token)) return;
            cancellation.Token.ThrowIfCancellationRequested();
            _cancel.Content = "Stop response";
            _status.Text = "Preparing the selected assistant. Source has not been compiled or executed.";
            await RequestDraftAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _status.Text = "Response canceled. No C# source was saved, compiled or executed.";
        }
        finally
        {
            _cancellation = null;
            _send.IsEnabled = !_applicationStop.IsCancellationRequested;
            _cancel.Content = "Close";
        }
    }

    private async Task RequestDraftAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _bridge = _bridgeFactory();
            var result = await PineAssistantHandoff.SendAsync(_preview, approved: true, _bridge, cancellationToken);
            _draft.Text = result?.Text ?? "";
            _tabs.SelectedIndex = 3;
            _status.Text = result?.Status switch
            {
                StrategyChatStatus.Succeeded => "Draft received. Review attribution, data assumptions and unsupported behavior before manually saving or compiling any code.",
                StrategyChatStatus.Cancelled => "Response stopped. No C# source was saved, compiled or executed.",
                _ => "The assistant did not produce a successful draft. See the response; no C# source was saved or executed.",
            };
            if (result?.CostUsd is { } cost) _status.Text += $" Reported cost: US${cost:0.00}.";
            else _status.Text += " Cost not reported by this CLI.";
        }
        catch (OperationCanceledException) when (_bridge?.IsRunning != true) { _status.Text = "Response canceled. No C# source was saved, compiled or executed."; }
        catch (Exception ex)
        {
            _status.Text = "Could not request a draft: " + ex.Message;
            if (_bridge?.IsRunning == true) throw;
        }
        finally { if (_bridge?.IsRunning != true) _bridge = null; }
    }

    private async Task DrainAndCloseAsync()
    {
        try { if (_active != null) await _active; }
        catch (Exception error) { _status.Text = "The assistant did not drain cleanly: " + error.Message; }
        finally
        {
            _closingAfterDrain = true;
            Close();
        }
    }

    private static TextBox ReadOnlyText(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = Ui.MonoFont,
        FontSize = 12,
        Margin = new Thickness(10),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };
}
