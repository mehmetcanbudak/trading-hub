using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TradingHub.App.Services;
using TradingHub.Common;

namespace TradingHub.App.Controls;

/// <summary>Actual Settings controls; injected preferences/restart keep storage failure scenarios isolated.</summary>
public sealed class StorageLocationPanel : UserControl
{
    private readonly StoragePreferences _preferences;
    private readonly Action _restart;
    private readonly TextBox _app = new() { Name = "StorageAppRoot", Watermark = "Keep the existing default" };
    private readonly TextBox _market = new() { Name = "StorageMarketRoot", Watermark = "Keep market data with application data" };
    private readonly TextBox _scripts = new() { Name = "StorageScriptsRoot", Watermark = "Keep the existing scripts location" };
    private readonly Button _preview = new() { Name = "StoragePreview", Content = "Preview and hash" };
    private readonly Button _stage = new() { Name = "StorageStage", Content = "Use after restart", IsEnabled = false };
    private readonly Button _cancel = new() { Name = "StorageCancel", Content = "Cancel relocation" };
    private readonly Button _restartButton = new() { Name = "StorageRestart", Content = "Restart safely" };
    private readonly TextBlock _state = new() { Name = "StorageStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "muted", "small" } };
    private readonly TextBlock _currentApp = new() { Name = "StorageCurrentAppRoot", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _currentMarket = new() { Name = "StorageCurrentMarketRoot", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _currentScripts = new() { Name = "StorageCurrentScriptsRoot", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private StorageRelocationPreview? _approved;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private (string App, string Market, string Scripts) _lastInput;
    public Task CurrentOperation { get; private set; } = Task.CompletedTask;

    public StorageLocationPanel(StoragePreferences preferences, Action restart)
    {
        _preferences = preferences;
        _restart = restart;
        var selection = preferences.Selection;
        _app.Text = selection.AppRoot ?? "";
        _market.Text = selection.MarketRoot ?? "";
        _scripts.Text = selection.ScriptsRoot ?? "";
        _lastInput = (_app.Text ?? "", _market.Text ?? "", _scripts.Text ?? "");
        foreach (var input in new[] { _app, _market, _scripts }) input.TextChanged += (_, _) => InvalidatePreview();
        _preview.Click += (_, _) => CurrentOperation = PreviewAsync();
        _stage.Click += (_, _) => CurrentOperation = StageAsync();
        _cancel.Click += (_, _) =>
        {
            _cancellation?.Cancel();
            _preferences.CancelPending();
            _approved = null;
            _stage.IsEnabled = false;
            _restartButton.IsVisible = false;
            _state.Text = "Relocation cancelled. Original locations and files remain authoritative.";
        };
        _restartButton.Click += (_, _) =>
        {
            try { _restart(); RefreshStatus(); }
            catch (Exception ex) { _state.Text = "Protected restart failed; active locations remain unchanged. " + ex.Message; }
        };
        DetachedFromVisualTree += (_, _) => _cancellation?.Cancel();
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Ui.Muted("Opt-in locations only. Choose new, nonexistent, nonoverlapping absolute folders. No automatic directory migration or deletion is performed."),
                Field("Application settings, accounts and local state", _app),
                Field("Market history, candles and offline catalogs", _market),
                Field("C# scripts (existing folder names remain)", _scripts),
                Ui.Faint("Environment overrides always win. Preview can be cancelled. The approved copy runs only after protected shutdown; every file is hashed before one atomic location switch. Old files are retained. Copy/hash/permission failures keep the old locations active."),
                Field("Current application", _currentApp),
                Field("Market", _currentMarket),
                Field("Scripts", _currentScripts),
                Ui.H(8, _preview, _stage, _cancel, _restartButton),
                _state,
            },
        };
        RefreshStatus();
    }
    private static Control Field(string label, Control input) => Ui.V(3, Ui.Muted(label), input);
    private void InvalidatePreview()
    {
        if (_busy) return;
        var input = (_app.Text ?? "", _market.Text ?? "", _scripts.Text ?? "");
        if (input == _lastInput) return;
        _lastInput = input;
        _approved = null;
        _stage.IsEnabled = false;
        if (_preferences.HasPending) _preferences.CancelPending();
        _restartButton.IsVisible = false;
        _state.Text = "Locations edited. Preview and verify the effective selection before staging.";
    }
    public void RefreshStatus()
    {
        _restartButton.IsVisible = _preferences.HasPending;
        var current = _preferences.Current;
        _currentApp.Text = current.AppRoot;
        _currentMarket.Text = current.MarketRoot;
        _currentScripts.Text = current.ScriptsRoot;
        _state.Text = _preferences.LastResult?.Message ?? (_preferences.HasPending
            ? "Relocation is staged for protected restart. Current locations are unchanged."
            : "Current locations are active.");
    }
    private async Task PreviewAsync()
    {
        if (_busy) return;
        _approved = null;
        SetBusy(true);
        using var cancellation = _cancellation = new();
        try
        {
            var selection = new StorageLocationSelection(_app.Text, _market.Text, _scripts.Text);
            var progress = new Progress<StorageRelocationProgress>(p =>
            {
                if (_cancellation == cancellation && !cancellation.IsCancellationRequested)
                    _state.Text = $"{p.Phase}: {p.RelativePath}";
            });
            var preview = await _preferences.PreviewAsync(selection, cancellation.Token, progress);
            cancellation.Token.ThrowIfCancellationRequested();
            _approved = preview;
            _state.Text = preview.HasChanges
                ? $"Preview: {preview.Files.Count:N0} files/folders · {preview.Bytes:N0} bytes. Original data remains active until a complete verified copy succeeds after restart."
                : "No effective change. Existing defaults and explicit environment overrides remain active.";
        }
        catch (OperationCanceledException) { _state.Text = "Preview cancelled. No location or original file changed."; }
        catch (Exception ex) { _state.Text = "Preview failed; original locations are unchanged. " + ex.Message; }
        finally
        {
            if (_cancellation == cancellation) _cancellation = null;
            SetBusy(false);
        }
    }
    private async Task StageAsync()
    {
        if (_busy || _approved == null) return;
        SetBusy(true);
        using var cancellation = _cancellation = new();
        try
        {
            await _preferences.StageAsync(_approved, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _state.Text = "Verified preview approved. Restart safely to copy and verify the final drained files. Nothing is switched before complete success.";
            _restartButton.IsVisible = true;
        }
        catch (OperationCanceledException)
        {
            _preferences.CancelPending();
            _state.Text = "Staging cancelled. Original locations remain active.";
        }
        catch (Exception ex) { _state.Text = "Staging failed. Original locations remain active. " + ex.Message; }
        finally
        {
            if (_cancellation == cancellation) _cancellation = null;
            SetBusy(false);
        }
    }
    private void SetBusy(bool value)
    {
        _busy = value;
        _app.IsEnabled = _market.IsEnabled = _scripts.IsEnabled = _preview.IsEnabled = !value;
        _stage.IsEnabled = !value && _approved is { HasChanges: true };
        _restartButton.IsEnabled = !value;
    }
}
