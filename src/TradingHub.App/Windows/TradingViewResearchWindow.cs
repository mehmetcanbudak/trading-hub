using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TradingHub.App.Services;

namespace TradingHub.App.Windows;

/// <summary>OAuth is outside chat; untrusted tool metadata is displayed as text, never executed as instructions.</summary>
public sealed class TradingViewResearchWindow : Window
{
    private readonly TradingViewResearchService _service;
    private readonly CancellationTokenSource _closing = new();
    private readonly TextBlock _status;
    private readonly StackPanel _catalog;
    private readonly Button _connect;
    private readonly Button _reviewButton;
    private readonly Button _approve;
    private readonly Button _off;
    private readonly Dictionary<string, CheckBox> _selections = new(StringComparer.Ordinal);
    private TradingViewResearchReview? _review;
    private bool _busy;
    public Task? PendingOperation { get; private set; }

    public TradingViewResearchWindow(TradingViewResearchService service)
    {
        _service = service;
        Title = "TradingView Research — explicit tool approval";
        Width = 940;
        Height = 760;
        MinWidth = 650;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _status = new TextBlock { Name = "ResearchStatus", Text = service.CaptureAccess() is null ? service.Status : "Research enabled for the saved local grant. Review or turn off below.", TextWrapping = TextWrapping.Wrap };
        _catalog = new StackPanel { Name = "ResearchCatalog", Spacing = 12 };
        _connect = new Button { Name = "ResearchConnect", Content = "Connect and discover metadata" };
        _reviewButton = new Button { Name = "ResearchReview", Content = "Review current grant" };
        _approve = new Button { Name = "ResearchApprove", Content = "Approve selected research tools", IsEnabled = false };
        _off = new Button { Name = "ResearchOff", Content = "Turn off / revoke" };
        var cancel = new Button { Name = "ResearchCancel", Content = "Cancel review", IsCancel = true };
        _connect.Click += (_, _) => Start(ConnectAndReviewAsync);
        _reviewButton.Click += (_, _) => Start(ReviewAsync);
        _approve.Click += (_, _) => Start(ApproveSelectedAsync);
        // Revocation must remain actionable while OAuth/discovery or a chat request is in flight.
        _off.Click += (_, _) => PendingOperation = RevokeImmediatelyAsync();
        cancel.Click += (_, _) => CancelReview();
        var heading = new StackPanel { Spacing = 9, Children = {
            new TextBlock { Text = "Optional TradingView research", FontSize = 22, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = service.Endpoint, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Off by default. Connecting opens TradingView OAuth in your browser. Essential or above is required; trials are excluded. Requests and research arguments are shared with TradingView and results may be sent to your chosen assistant. This connection cannot expose orders, watchlists or alerts.", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Discovery only initializes MCP and reads every tools/list page. No model turn or remote tool is used. A local grant ID is not a verified signed-in account name. Server descriptions, schemas and read-only annotations are untrusted metadata; only the reviewed research intersection can be enabled.", TextWrapping = TextWrapping.Wrap },
            new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { _connect, _reviewButton, _off } },
            _status,
            new Separator()
        } };
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 12, 0, 0), Children = { cancel, _approve } };
        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(heading);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = _catalog, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = root;
        _service.AccessChanged += AccessChanged;
        Closed += (_, _) =>
        {
            _closing.Cancel();
            if (_review != null) service.CancelReview();
            _service.AccessChanged -= AccessChanged;
            _closing.Dispose();
        };
    }
    private void AccessChanged() => Dispatcher.UIThread.Post(() => _status.Text = _service.Status);
    private void Start(Func<Task> action)
    {
        if (_busy) return;
        PendingOperation = ExecuteAsync(action);
    }
    private async Task ExecuteAsync(Func<Task> action)
    {
        _busy = true;
        UpdateButtons();
        try { await action(); }
        catch (Exception error) { _status.Text = TradingViewResearchService.SafeError(error); }
        finally { _busy = false; UpdateButtons(); }
    }
    private void UpdateButtons()
    {
        _connect.IsEnabled = !_busy;
        _reviewButton.IsEnabled = !_busy;
        _off.IsEnabled = true;
        _approve.IsEnabled = !_busy && _review != null && _selections.Values.Any(c => c.IsChecked == true);
    }
    private async Task RevokeImmediatelyAsync()
    {
        try { await TurnOffAsync(); }
        catch (Exception error) { _status.Text = TradingViewResearchService.SafeError(error); }
    }
    public async Task ConnectAndReviewAsync()
    {
        ClearReview();
        _status.Text = "Connect requested. Authorize in the browser; metadata discovery does not enable any tool.";
        ShowReview(await _service.ConnectAndReviewAsync(_closing.Token));
    }
    public async Task ReviewAsync()
    {
        ClearReview();
        _status.Text = "Reading all authenticated metadata pages. Existing approval is disabled while reviewing.";
        ShowReview(await _service.ReviewAsync(_closing.Token));
    }
    public void ShowReview(TradingViewResearchReview review)
    {
        ClearReview();
        _review = review;
        _catalog.Children.Add(new TextBlock { Text = $"Local grant {review.GrantId} · Catalog {review.Fingerprint}\nSelect the exact offered research tools you approve. All selections initially remain off.", TextWrapping = TextWrapping.Wrap });
        foreach (var tool in review.Tools)
        {
            var select = new CheckBox { Name = "ResearchTool_" + tool.Name, Content = new TextBlock { Text = tool.Name },
                IsEnabled = tool.Candidate, IsChecked = false };
            if (tool.Candidate)
            {
                _selections.Add(tool.Name, select);
                select.IsCheckedChanged += (_, _) => UpdateButtons();
            }
            var panel = new StackPanel { Spacing = 4, Children = {
                select,
                new TextBlock { Text = tool.Candidate ? "Research candidate — explicit selection required" : "DENIED — watchlist, alert, mutation, unknown or ambiguous side effects", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = tool.Description, TextWrapping = TextWrapping.Wrap },
                new Expander { Header = "Actual server schema and metadata (untrusted)", Content = new TextBox {
                    Text = tool.Definition.GetRawText(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                    FontFamily = FontFamily.Default, MinHeight = 90, MaxHeight = 260 } }
            } };
            _catalog.Children.Add(new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Child = panel });
        }
        _status.Text = _service.Status;
        UpdateButtons();
    }
    public void SetToolSelected(string name, bool selected)
    {
        if (!_selections.TryGetValue(name, out var checkbox)) throw new TradingViewResearchException("The tool is denied or was not offered in this review.");
        checkbox.IsChecked = selected;
    }
    public async Task ApproveSelectedAsync()
    {
        if (_review == null) throw new TradingViewResearchException("Discover and review the actual catalog first.");
        await _service.ApproveAsync(_review, _selections.Where(p => p.Value.IsChecked == true).Select(p => p.Key), _closing.Token);
        ClearReview();
        _status.Text = _service.Status;
        UpdateButtons();
    }
    public void CancelReview()
    {
        _service.CancelReview();
        ClearReview();
        _status.Text = _service.Status;
        UpdateButtons();
    }
    public async Task TurnOffAsync()
    {
        await _service.RevokeAsync(_closing.Token);
        ClearReview();
        _status.Text = _service.Status;
        UpdateButtons();
    }
    private void ClearReview()
    {
        _review = null;
        _catalog.Children.Clear();
        _selections.Clear();
    }
}
