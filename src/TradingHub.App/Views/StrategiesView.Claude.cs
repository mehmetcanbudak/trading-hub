using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;

namespace TradingHub.App.Views;

/// <summary>Panel de Claude Code dentro de la pestaña Código: pedís estrategias y ves el código en el editor.</summary>
internal sealed partial class StrategiesView
{
    private IStrategyChatBridge _claude = new ClaudeCodeBridge();
    private Action? _unhookChat;
    private long _chatBinding;
    private bool _chatBusy;
    private ChatTurn? _activeChatTurn;
    private bool _chatTornDown;
    private sealed class ChatTurn(IStrategyChatBridge bridge, long binding, ChatConversation conversation, CancellationToken cancellation)
    {
        public IStrategyChatBridge Bridge { get; } = bridge;
        public long Binding { get; } = binding;
        public ChatConversation Conversation { get; } = conversation;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private bool _selectingEngine;
    private readonly ComboBox _chatEngine = new() { ItemsSource = new[] { "claude", "codex" }, MinWidth = 95 };
    private readonly TextBox _chatModel = new() { Watermark = "CLI default model", MinWidth = 135, MaxWidth = 230 };
    private readonly TextBox _chatEffort = new() { Watermark = "Effort", Width = 75 };
    private readonly TextBlock _chatTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 14 };
    private string EngineTitle => _claude.Engine == "codex" ? "Codex" : "Claude";
    private readonly StackPanel _chat = new() { Spacing = 10, Margin = new Thickness(14, 12) };
    private readonly ScrollViewer _chatScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBox _prompt = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 64,
        MaxHeight = 160,
        Watermark = "Ask for a new strategy or changes to the open strategy…",
    };
    private readonly TextBlock _claudeStatus = new() { Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private Button _sendButton = null!;
    private Button _stopButton2 = null!;
    private SelectableTextBlock? _currentReply;
    private Dictionary<string, DateTime> _filesBefore = new();
    private Dictionary<string, DateTime> _indicatorsBefore = new();
    private DateTime _requestStartedUtc;
    private bool _gotReplyText;
    private DateTime _loadedFileTime;
    private readonly ToggleButton _labToggle = new();
    private readonly TextBlock _claudeHint = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private HashSet<string> _reportsBefore = new();
    private Control? _workingNote;

    // conversación actual (se guarda en el historial) y ventana aparte del chat
    private ChatConversation _conv = new();
    private Grid? _claudeGrid;
    private GridSplitter? _claudeSplit;
    private Control? _claudePanel;
    private Window? _claudeWindow;
    private Button? _claudeDetach;

    private Control BuildClaudePanel()
    {
        var newChat = Ui.IconButton(MaterialIconKind.ChatPlusOutline, "New conversation", () =>
        {
            if (_chatBusy) return;
            ChatHistory.Save(_conv);
            _claude.NewConversation();
            _conv = new ChatConversation { Engine = _claude.Engine };
            _unhookChat?.Invoke();
            HookChatBridge();
            _chat.Children.Clear();
            ShowSuggestions();
        }, 17);
        var history = new Button { Content = Ui.Icon(MaterialIconKind.History, 17) }.Classes("icon");
        ToolTip.SetTip(history, "Conversation history (resume where you left off)");
        history.Click += (_, _) => ShowHistoryMenu(history);
        _claudeDetach = Ui.IconButton(MaterialIconKind.OpenInNew, "Detach chat into a window", ToggleClaudeWindow, 17);
        var settings = App.Services.Settings;
        string initial = StrategyChatEngines.IsKnown(settings.StrategyChatEngine) ? settings.StrategyChatEngine : "claude";
        _claude = StrategyChatEngines.Create(initial);
        _conv.Engine = initial;
        _selectingEngine = true;
        _chatEngine.SelectedItem = initial;
        _selectingEngine = false;
        LoadChatOptions();
        _chatEngine.SelectionChanged += (_, _) =>
        {
            if (!_selectingEngine && _chatEngine.SelectedItem is string engine) SwitchChatEngine(engine);
        };
        _chatModel.LostFocus += (_, _) => SaveChatOptions();
        _chatEffort.LostFocus += (_, _) => SaveChatOptions();
        var research = Ui.Button("Research access", MaterialIconKind.CogOutline, async () =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            try { await TradingViewResearchService.Default.ConfigureAsync(owner); }
            catch (Exception ex) { AddNote(MaterialIconKind.AlertCircleOutline, "Could not open research access: " + ex.Message, Ui.Down); }
        });
        var pine = Ui.Button("Import Pine", MaterialIconKind.Import, () => _ = ImportPineAsync());
        var title = Ui.V(3, Ui.H(8, _chatTitle, _chatEngine), _claudeStatus, Ui.H(4, _chatModel, _chatEffort), Ui.H(4, research, pine));
        // laboratorio: Claude corre backtests, optimizaciones y walk-forward por su cuenta
        _labToggle.Content = Ui.H(6, Ui.Icon(MaterialIconKind.FlaskOutline, 15), new TextBlock { Text = "Laboratory", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _labToggle.Padding = new Thickness(8, 4);
        ToolTip.SetTip(_labToggle, "Laboratory mode: the selected assistant can download data, run backtests, optimization, walk-forward and Monte Carlo, assess strategies and save reports. Financial changes remain proposals.");
        _labToggle.IsCheckedChanged += (_, _) => UpdateClaudeHint();
        var headRight = Ui.H(6, _labToggle, history, newChat, _claudeDetach);
        var head = new DockPanel { Margin = new Thickness(14, 10, 10, 10) };
        DockPanel.SetDock(headRight, Dock.Right);
        head.Children.Add(headRight);
        head.Children.Add(title);

        _sendButton = Ui.Button("Send", MaterialIconKind.Send, () => _ = SendToClaudeAsync(_prompt.Text ?? ""), "primary");
        ToolTip.SetTip(_sendButton, Keys.Label("Enter"));
        _stopButton2 = Ui.Button("Stop response", MaterialIconKind.Stop, () => _claude.Cancel());
        _stopButton2.IsVisible = false;
        _prompt.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(Keys.Command))
            {
                _ = SendToClaudeAsync(_prompt.Text ?? "");
                e.Handled = true;
            }
        };
        var actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var right = Ui.H(8, _stopButton2, _sendButton);
        DockPanel.SetDock(right, Dock.Right);
        actions.Children.Add(right);
        UpdateClaudeHint();
        actions.Children.Add(_claudeHint);
        var input = new Border { Child = Ui.V(0, _prompt, actions), Padding = new Thickness(12), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };

        _chatScroll.Content = _chat;
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(new Border { Child = head, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) }.Also(b => DockPanel.SetDock(b, Dock.Top)));
        DockPanel.SetDock(input, Dock.Bottom);
        root.Children.Add(input);
        root.Children.Add(_chatScroll);

        HookChatBridge();
        DetachedFromVisualTree += (_, _) => TearDownChat();
        _ = DetectClaudeAsync();
        ShowSuggestions();
        return new Border { Child = root, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(1, 0, 0, 0) };
    }
    private void HookChatBridge()
    {
        var bridge = _claude;
        long binding = ++_chatBinding;
        void Text(string text) { if (binding == _chatBinding && ReferenceEquals(bridge, _claude) && _activeChatTurn is { } turn && IsCurrentChatTurn(turn)) OnClaudeText(text); }
        void Tool(string tool, string? file) { if (binding == _chatBinding && ReferenceEquals(bridge, _claude) && _activeChatTurn is { } turn && IsCurrentChatTurn(turn)) OnClaudeTool(tool, file); }
        void ToolFinished() { if (binding == _chatBinding && ReferenceEquals(bridge, _claude) && _activeChatTurn is { } turn && IsCurrentChatTurn(turn)) ReloadIfChangedOnDisk(); }
        bridge.Text += Text; bridge.ToolUsed += Tool; bridge.ToolFinished += ToolFinished;
        _unhookChat = () => { bridge.Text -= Text; bridge.ToolUsed -= Tool; bridge.ToolFinished -= ToolFinished; };
    }
    private bool IsCurrentChatTurn(ChatTurn turn) =>
        !_chatTornDown && !turn.Cancellation.IsCancellationRequested && ReferenceEquals(turn, _activeChatTurn)
        && turn.Binding == _chatBinding && ReferenceEquals(turn.Bridge, _claude) && ReferenceEquals(turn.Conversation, _conv);

    private async Task CompleteChatTurnAsync(ChatTurn turn, StrategyChatResult result)
    {
        try
        {
            if (!IsCurrentChatTurn(turn))
            {
                turn.Conversation.SessionId = turn.Bridge.SessionId ?? turn.Conversation.SessionId;
                if (result.IsError)
                    turn.Conversation.Messages.Add(new ChatMessage { Role = "note", Text = result.Text.Length > 400 ? result.Text[..400] + "…" : result.Text });
                ChatHistory.Save(turn.Conversation);
                return;
            }
            _stopButton2.IsVisible = false;
            await OnClaudeFinishedAsync(result, turn);
        }
        catch (Exception ex)
        {
            if (_svc.Work.IsStopping) turn.Completion.TrySetException(ex);
            else
            {
                if (IsCurrentChatTurn(turn)) AddNote(MaterialIconKind.AlertCircleOutline, "Could not finish assistant response: " + ex.Message, Ui.Down);
                CrashLog.Write(ex);
            }
        }
        finally { turn.Completion.TrySetResult(true); }
    }

    private void TearDownChat()
    {
        if (_chatTornDown) return;
        _chatTornDown = true;
        ++_chatBinding;
        _unhookChat?.Invoke();
        if (_activeChatTurn is not { } turn) return;
        turn.Cancellation.Cancel();
        turn.Bridge.Cancel();
    }


    private void SetChatOptionsEnabled(bool enabled)
    {
        _chatEngine.IsEnabled = enabled; _chatModel.IsEnabled = enabled; _chatEffort.IsEnabled = enabled; _labToggle.IsEnabled = enabled;
    }

    private void SaveChatOptions()
    {
        var settings = App.Services.Settings;
        settings.StrategyChatEngine = _claude.Engine;
        if (_claude.Engine == "codex") { settings.StrategyCodexModel = _chatModel.Text?.Trim() ?? ""; settings.StrategyCodexEffort = _chatEffort.Text?.Trim() ?? ""; }
        else { settings.StrategyClaudeModel = _chatModel.Text?.Trim() ?? ""; settings.StrategyClaudeEffort = _chatEffort.Text?.Trim() ?? ""; }
        settings.Save();
    }

    private void LoadChatOptions()
    {
        var settings = App.Services.Settings;
        _chatModel.Text = _claude.Engine == "codex" ? settings.StrategyCodexModel : settings.StrategyClaudeModel;
        _chatEffort.Text = _claude.Engine == "codex" ? settings.StrategyCodexEffort : settings.StrategyClaudeEffort;
        _chatTitle.Text = EngineTitle;
        if (_claudeWindow != null) _claudeWindow.Title = $"{EngineTitle} · strategies";
    }

    private void SwitchChatEngine(string engine)
    {
        if (_chatBusy || !StrategyChatEngines.IsKnown(engine))
        {
            _selectingEngine = true; _chatEngine.SelectedItem = _claude.Engine; _selectingEngine = false;
            return;
        }
        if (engine == _claude.Engine) return;
        SaveChatOptions();
        ChatHistory.Save(_conv);
        _unhookChat?.Invoke();
        _claude = StrategyChatEngines.Create(engine);
        _conv = new ChatConversation { Engine = engine };
        HookChatBridge();
        _selectingEngine = true; _chatEngine.SelectedItem = engine; _selectingEngine = false;
        LoadChatOptions();
        SaveChatOptions();
        _chat.Children.Clear();
        _currentReply = null;
        UpdateClaudeHint();
        ShowSuggestions();
        _ = DetectClaudeAsync();
    }

    private Task ImportPineAsync() => RunViewOperationAsync(ImportPineOwnedAsync);

    private async Task ImportPineOwnedAsync(CancellationToken cancellation)
    {
        if (_chatBusy || TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            if (await PineImports.PickAsync(owner, AppPaths.UserIndicators) is { } source)
            {
                cancellation.ThrowIfCancellationRequested();
                await ReviewPineAsync(source);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { AddNote(MaterialIconKind.AlertCircleOutline, "Could not import Pine source: " + ex.Message, Ui.Down); }
    }

    /// <summary>Navigation opens a local preview only; the dialog owns the later explicit draft-only Send action.</summary>
    internal async Task ReviewPineAsync(PineImportSource source)
    {
        if (_chatTornDown || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (_chatBusy)
        {
            AddNote(MaterialIconKind.InformationOutline, $"Pine source is preserved locally at {source.Path}. Finish the active assistant response, then import the saved Pine file to review a handoff. No Pine source was sent.", Ui.Warn);
            return;
        }
        _tabs.Select("code");
        ShowTab("code");
        var preview = PineImports.Preview(source, _claude.Engine, _chatModel.Text, _chatEffort.Text);
        try { await Windows.PineReviewWindow.ShowOwnedAsync(_svc, owner, preview); }
        catch (OperationCanceledException) when (_svc.PineHandoffs.IsStopping) { /* App-owned shutdown canceled the pending review before egress. */ }
        catch (ObjectDisposedException) when (_svc.PineHandoffs.IsStopping) { /* True shutdown has already rejected new handoffs. */ }
        catch (Exception error)
        {
            if (!_chatTornDown) AddNote(MaterialIconKind.AlertCircleOutline, "Pine review did not complete cleanly: " + error.Message, Ui.Down);
        }
    }


    private void UpdateClaudeHint()
    {
        bool lab = _labToggle.IsChecked == true;
        _claudeHint.Text = lab
            ? $"{EngineTitle} laboratory: runs tests and saves reports. Financial changes remain proposals for review."
            : $"Uses your own {EngineTitle} CLI session. Files may be edited in the configured strategy and indicator directories. Provider billing may not be reported.";
        _claudeHint.Foreground = lab ? Ui.Accent : null;
        _prompt.Watermark = lab ? "Example: evaluate and optimize this strategy with 60 days of BTCUSDT history; report the outcome" : "Ask for a strategy or a change…";
    }

    private async Task DetectClaudeAsync()
    {
        var bridge = _claude;
        bool found = bridge.Engine == "codex" ? CodexBridge.FindCli() != null : ClaudeCodeBridge.FindCli() != null;
        await Task.CompletedTask;
        if (!ReferenceEquals(bridge, _claude)) return;
        _claudeStatus.Text = found ? $"{EngineTitle} · user CLI session" : $"{EngineTitle} CLI is not installed";
        _sendButton.IsEnabled = found;
        if (!found) AddNote(MaterialIconKind.AlertCircleOutline, $"Install {EngineTitle} CLI and sign in in Terminal. No API key is needed here.", Ui.Warn);
    }

    private void ShowSuggestions()
    {
        if (_chat.Children.Count > 0) return;
        var intro = new TextBlock
        {
            Text = "Describe a strategy: your selected assistant edits files, and Trading Hub compiles the result.",
            Classes = { "muted", "small" },
            TextWrapping = TextWrapping.Wrap,
        };
        var chips = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (s, lab) in new[]
                 {
                     ("Research and optimize the open strategy: backtest, optimization, walk-forward and Monte Carlo, then prepare a report with a verdict", true),
                     ("Review the live portfolio and propose position-size adjustments based on recent results", true),
                     ("Create a strategy trading the opening 30-minute range breakout", false),
                     ("Explain what the currently open strategy does", false),
                     ("Add a filter to trade only between 10:00 and 17:00", false),
                     ("Improve risk management using ATR-based stops", false),
                 })
        {
            var content = lab
                ? (Control)Ui.H(6, Ui.Icon(MaterialIconKind.FlaskOutline, 14, Ui.Accent), new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 300 })
                : new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var b = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6) }.Classes("ghost");
            b.Click += (_, _) =>
            {
                if (lab) _labToggle.IsChecked = true;
                _prompt.Text = s;
                _prompt.Focus();
            };
            chips.Children.Add(b);
        }

        _chat.Children.Add(Ui.V(10, intro, chips));
    }

    private void AddBubble(string text, bool user)
    {
        if (_chat.Children.Count == 1 && _chat.Children[0] is StackPanel { Children.Count: 2 } sp && sp.Children[1] is WrapPanel) _chat.Children.Clear();
        var tb = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        var bubble = new Border
        {
            Child = tb,
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(10),
            Background = user ? Ui.Bg3 : Ui.Bg2,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Margin = user ? new Thickness(40, 0, 0, 0) : new Thickness(0),
        };
        _chat.Children.Add(bubble);
        if (!user) _currentReply = tb;
        ScrollChatToEnd();
    }

    private Control AddNote(MaterialIconKind icon, string text, IBrush? color = null)
    {
        var row = Ui.H(6, Ui.Icon(icon, 14, color ?? Ui.Text2), new TextBlock { Text = text, Classes = { "small" }, Foreground = color ?? Ui.Text2, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
        _chat.Children.Add(row);
        ScrollChatToEnd();
        return row;
    }

    private void ScrollChatToEnd() => Dispatcher.UIThread.Post(() => _chatScroll.ScrollToEnd(), DispatcherPriority.Background);

    private Task SendToClaudeAsync(string text) => RunViewOperationAsync(cancellation => SendToClaudeOwnedAsync(text, cancellation));


    private async Task SendToClaudeOwnedAsync(string text, CancellationToken cancellation)
    {
        text = text.Trim();
        if (text.Length == 0 || _chatBusy || _chatTornDown) return;
        var turn = new ChatTurn(_claude, _chatBinding, _conv, cancellation);
        _activeChatTurn = turn;
        try
        {
            await SendChatRequestAsync(text, turn);
        }
        catch (Exception ex) when (!cancellation.IsCancellationRequested)
        {
            if (IsCurrentChatTurn(turn))
            {
                RemoveWorkingNote();
                AddNote(MaterialIconKind.AlertCircleOutline, "Could not prepare assistant request: " + ex.Message, Ui.Down);
            }
            CrashLog.Write(ex);
        }
        finally
        {
            if (ReferenceEquals(turn, _activeChatTurn))
            {
                bool restore = IsCurrentChatTurn(turn);
                _activeChatTurn = null;
                _chatBusy = false;
                if (restore)
                {
                    SetChatOptionsEnabled(true);
                    _editor.IsReadOnly = false;
                    _sendButton.IsVisible = true;
                    _stopButton2.IsVisible = false;
                }
            }
            turn.Completion.TrySetResult(true);
            turn.Cancellation.Dispose();
        }
    }

    private async Task SendChatRequestAsync(string text, ChatTurn turn)
    {
        SaveChatOptions();
        _chatBusy = true;
        SetChatOptionsEnabled(false);
        _conv.Engine = _claude.Engine;
        _conv.Model = _chatModel.Text;
        _conv.Effort = _chatEffort.Text;
        if (_editor.IsDirty) Save();
        _prompt.Text = "";
        AddBubble(text, user: true);
        if (_conv.Messages.Count == 0)
        {
            _conv.Title = text.Length > 70 ? text[..70].TrimEnd() + "…" : text;
            _conv.Lab = _labToggle.IsChecked == true;
            _conv.Strategy = _current != null ? Path.GetFileName(_current.Path) : null;
        }

        _conv.Messages.Add(new ChatMessage { Role = "user", Text = text });
        ChatHistory.Save(_conv);
        _currentReply = null;
        _gotReplyText = false;
        _filesBefore = Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").ToDictionary(f => f, File.GetLastWriteTimeUtc);
        Directory.CreateDirectory(AppPaths.UserIndicators);
        _indicatorsBefore = Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);
        _requestStartedUtc = DateTime.UtcNow;
        _loadedFileTime = _current != null && File.Exists(_current.Path) ? File.GetLastWriteTimeUtc(_current.Path) : default;

        // contexto: archivo abierto y errores actuales
        var context = new List<string>();
        if (_current != null) context.Add($"File open in the editor: {Path.GetFileName(_current.Path)}");
        context.Add($"Indicator directory (for indicators, not strategies): {AppPaths.UserIndicators}");
        var errors = _current?.LastCompile?.Diagnostics.Where(d => d.Severity == "error").Take(15).ToList();
        if (errors is { Count: > 0 }) context.Add("Current compilation errors:\n" + string.Join('\n', errors.Select(d => $"- Line {d.Line}: {d.Message} ({d.Id})")));
        string prompt = context.Count > 0 ? $"[Trading Hub context]\n{string.Join("\n", context)}\n\n[Request]\n{text}" : text;

        _sendButton.IsVisible = false;
        _stopButton2.IsVisible = true;
        _editor.IsReadOnly = true;
        bool lab = _labToggle.IsChecked == true;
        _reportsBefore = Lab.ReportStore.List().Select(r => r.Path).ToHashSet();
        _workingNote = AddNote(MaterialIconKind.ProgressClock, $"{EngineTitle} is working…");
        var result = await turn.Bridge.SendAsync(new StrategyChatRequest(prompt, AppPaths.Strategies,
            ClaudeCodeBridge.StrategySystemPrompt + ClaudeCodeBridge.IndicatorSystemPrompt + (lab ? ClaudeCodeBridge.LabSystemPrompt : ""), lab,
            new[] { AppPaths.UserIndicators, Directory.CreateDirectory(Lab.LabServer.CapturesDir).FullName },
            _chatModel.Text, _chatEffort.Text, TradingViewResearchService.Default.CaptureAccess()), turn.Cancellation.Token);
        // This callback is queued even after cancellation so earlier stream/Finished callbacks and the final save stay owned.
        Dispatcher.UIThread.Post(() => _ = CompleteChatTurnAsync(turn, result));
        await turn.Completion.Task;
    }

    /// <summary>Las capturas del gráfico que miró Claude en este pedido, en el chat (tocás una y se abre grande).</summary>
    private void ShowSnapshots()
    {
        var dir = Lab.LabServer.CapturesDir;
        if (!Directory.Exists(dir)) return;
        foreach (var f in new DirectoryInfo(dir).GetFiles("*.png").Where(f => f.LastWriteTimeUtc >= _requestStartedUtc).OrderBy(f => f.LastWriteTimeUtc).Take(4))
        {
            try
            {
                var bmp = new Avalonia.Media.Imaging.Bitmap(f.FullName);
                var img = new Image { Source = bmp, MaxWidth = 420, Stretch = Stretch.Uniform, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                var path = f.FullName;
                img.PointerReleased += (_, _) => TradingHub.App.Windows.Shell.Open(path);
                ToolTip.SetTip(img, "What the assistant reviewed (click to enlarge)");
                _chat.Children.Add(Ui.V(6, Ui.H(8, Ui.Icon(MaterialIconKind.ChartBoxOutline, 15, Ui.Accent), new TextBlock { Text = "Chart screenshot inspected", Classes = { "small" } }),
                    new Border { Child = img, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left }));
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }
        }
    }

    /// <summary>Indicadores que Claude creó o cambió en este pedido: los compila y deja el resultado en el chat.</summary>
    private void ReportIndicators()
    {
        if (!Directory.Exists(AppPaths.UserIndicators)) return;
        var changed = Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories)
            .Where(f => !_indicatorsBefore.TryGetValue(f, out var t) || File.GetLastWriteTimeUtc(f) > t).ToList();
        if (changed.Count == 0 || _svc.Scripts is not { } scripts) return;
        var res = scripts.CompileAll();
        string names = string.Join(", ", changed.Select(Path.GetFileNameWithoutExtension));
        var errors = res?.Diagnostics.Where(d => d.Severity == "error" && changed.Any(f => string.IsNullOrEmpty(d.File) || Path.GetFullPath(d.File) == Path.GetFullPath(f))).ToList() ?? new();
        var open = Ui.Button("Open in indicator editor", MaterialIconKind.CodeBraces, () =>
        {
            if (TopLevel.GetTopLevel(this) is Window w) new Windows.CodeEditorWindow(_svc, Windows.CodeKind.Indicator).Show(w);
        });
        open.Padding = new Thickness(10, 4);
        if (res?.Success == true)
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CheckCircle, 15, Ui.Up),
                new TextBlock { Text = $"Indicator {names} compiled: available under Indicators → My indicators", Classes = { "small" }, Foreground = Ui.Up, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }), open));
        else
        {
            string detail = string.Join("\n", (errors.Count > 0 ? errors : res?.Diagnostics.Where(d => d.Severity == "error").ToList() ?? new()).Take(12)
                .Select(d => $"- {Path.GetFileName(d.File)} line {d.Line}: {d.Message} ({d.Id})"));
            var fix = Ui.Button("Ask assistant to fix errors", MaterialIconKind.AutoFix, () => _ = SendToClaudeAsync($"Fix compilation errors in indicator {names} (in the indicator directory):\n{detail}"), "primary");
            fix.Padding = new Thickness(10, 4);
            int count = errors.Count > 0 ? errors.Count : res?.Diagnostics.Count(d => d.Severity == "error") ?? 0;
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CloseCircle, 15, Ui.Down),
                new TextBlock { Text = $"Indicator {names} has {count} compilation error(s)", Classes = { "small" }, Foreground = Ui.Down, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }), Ui.H(8, fix, open)));
        }
    }

    private void OnClaudeText(string chunk)
    {
        if (_conv.Messages.LastOrDefault() is { Role: "assistant" } last && _gotReplyText) last.Text += chunk;
        else _conv.Messages.Add(new ChatMessage { Role = "assistant", Text = chunk });
        _gotReplyText = true;
        RemoveWorkingNote();
        if (_currentReply == null) AddBubble(chunk, user: false);
        else _currentReply.Text += chunk;
        ScrollChatToEnd();
    }

    private void OnClaudeTool(string tool, string? file)
    {
        RemoveWorkingNote();
        _currentReply = null; // lo que escriba después va en otra burbuja
        if (tool.StartsWith("mcp__tradinghub__", StringComparison.Ordinal))
        {
            var (labIcon, labVerb) = tool["mcp__tradinghub__".Length..] switch
            {
                "list_strategies" => (MaterialIconKind.RobotOutline, "Reviewing strategies"),
                "compile_strategy" => (MaterialIconKind.HammerWrench, "Compiling"),
                "compile_indicator" => (MaterialIconKind.HammerWrench, "Compiling indicator"),
                "chart_snapshot" => (MaterialIconKind.ChartBoxOutline, "Reviewing chart"),
                "list_indicators" => (MaterialIconKind.ChartBellCurveCumulative, "Reviewing your indicators"),
                "list_data" => (MaterialIconKind.DatabaseOutline, "Inspecting saved data"),
                "download_data" => (MaterialIconKind.Download, "Downloading data"),
                "run_backtest" => (MaterialIconKind.PlayOutline, "Backtest"),
                "run_optimization" => (MaterialIconKind.Tune, "Optimizing parameters"),
                "run_walk_forward" => (MaterialIconKind.ChartTimelineVariant, "Walk-forward"),
                "list_runs" => (MaterialIconKind.History, "Reviewing previous runs"),
                "analyze_portfolio" => (MaterialIconKind.ChartTimelineVariant, "Analyzing portfolio"),
                "suggest_portfolio" => (MaterialIconKind.ScaleBalance, "Finding uncorrelated strategies"),
                "live_portfolio" => (MaterialIconKind.Pulse, "Reviewing live portfolio"),
                "propose_sizing" => (MaterialIconKind.ScaleBalance, "Preparing sizing proposals (subject to your approval)"),
                "save_report" => (MaterialIconKind.FileDocumentOutline, "Saving report"),
                var other => (MaterialIconKind.FlaskOutline, other),
            };
            AddNote(labIcon, string.IsNullOrEmpty(file) ? labVerb : $"{labVerb} · {file}", Ui.Accent);
            return;
        }

        string name = file != null ? Path.GetFileName(file) : "";
        var (icon, verb) = tool switch
        {
            "Write" => (MaterialIconKind.FilePlusOutline, "Writing"),
            "Edit" or "MultiEdit" => (MaterialIconKind.FileEditOutline, "Editing"),
            "Read" => (MaterialIconKind.FileEyeOutline, "Reading"),
            _ => (MaterialIconKind.FolderSearchOutline, "Searching"),
        };
        AddNote(icon, $"{verb} {name}".Trim());
    }

    private void RemoveWorkingNote()
    {
        if (_workingNote != null) _chat.Children.Remove(_workingNote);
        _workingNote = null;
    }

    /// <summary>Si Claude cambió el archivo abierto, se muestra al instante en el editor.</summary>
    private void ReloadIfChangedOnDisk()
    {
        if (_current == null || !File.Exists(_current.Path)) return;
        var t = File.GetLastWriteTimeUtc(_current.Path);
        if (t == _loadedFileTime) return;
        _loadedFileTime = t;
        _editor.Text = File.ReadAllText(_current.Path);
        UpdateFileInfo();
    }

    private async Task OnClaudeFinishedAsync(StrategyChatResult r, ChatTurn turn)
    {
        _conv.SessionId = _claude.SessionId ?? _conv.SessionId;
        if (r.IsError) _conv.Messages.Add(new ChatMessage { Role = "note", Text = r.NeedsLogin ? $"{EngineTitle} requested login." : r.Text.Length > 400 ? r.Text[..400] + "…" : r.Text });
        else if (!_gotReplyText && !string.IsNullOrWhiteSpace(r.Text)) _conv.Messages.Add(new ChatMessage { Role = "assistant", Text = r.Text, ReportedCostUsd = r.CostUsd, TokenUsage = r.Usage });
        else if (_conv.Messages.LastOrDefault() is { Role: "assistant" } last) { last.ReportedCostUsd = r.CostUsd; last.TokenUsage = r.Usage; }
        ChatHistory.Save(_conv);
        RemoveWorkingNote();
        if (r.IsError)
        {
            if (r.NeedsLogin)
            {
                var login = Ui.Button($"Sign in to {EngineTitle}", MaterialIconKind.Login, () =>
                {
                    if (_claude.Engine == "claude") ClaudeCodeBridge.OpenLoginTerminal();
                    else if (CodexBridge.OpenLoginTerminal() is { } error) AddNote(MaterialIconKind.AlertCircleOutline, error, Ui.Down);
                }, "primary");
                var card = Ui.Card(Ui.V(8,
                    new TextBlock { Text = $"{EngineTitle} requires sign-in", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Use your own CLI account in Terminal; never paste tokens or passwords into this chat.", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap },
                    login), 12);
                _chat.Children.Add(card);
            }
            else
            {
                AddNote(MaterialIconKind.AlertCircleOutline, r.Text.Length > 400 ? r.Text[..400] + "…" : r.Text, Ui.Down);
            }

            ScrollChatToEnd();
            return;
        }

        if (!_gotReplyText && !string.IsNullOrWhiteSpace(r.Text)) AddBubble(r.Text, user: false);

        // ¿creó una estrategia nueva? se abre sola
        _catalog.Refresh();
        var created = Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").Where(f => !_filesBefore.ContainsKey(f)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        var target = created != null ? _catalog.Files.FirstOrDefault(f => f.Path == created) : _current;
        if (target != null && !ReferenceEquals(target, _current)) await SelectOwnedAsync(target, turn.Cancellation.Token);
        else
        {
            ReloadIfChangedOnDisk();
            await CompileOwnedAsync(turn.Cancellation.Token, silent: true);
        }
        if (!IsCurrentChatTurn(turn)) return;

        var res = _current?.LastCompile;
        int errors = res?.Diagnostics.Count(d => d.Severity == "error") ?? 0;
        if (res?.Success == true && _generation != null)
        {
            var test = Ui.Button("Test in backtest", MaterialIconKind.PlayOutline, () => _ = RunViewOperationAsync(async stopping =>
            {
                stopping.ThrowIfCancellationRequested();
                _tabs.Select("backtest");
                ShowTab("backtest");
                await RunBacktestOwnedAsync(stopping);
            }));
            test.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CheckCircle, 15, Ui.Up), new TextBlock { Text = $"{_generation.DisplayName} compiles without errors", Classes = { "small" }, Foreground = Ui.Up, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }), test));
        }
        else if (errors > 0)
        {
            var fix = Ui.Button("Ask assistant to fix errors", MaterialIconKind.AutoFix, () => _ = SendToClaudeAsync("Fix the compilation errors in the open file."), "primary");
            fix.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CloseCircle, 15, Ui.Down), new TextBlock { Text = $"{errors} compilation error(s)", Classes = { "small" }, Foreground = Ui.Down }), fix));
        }

        // ¿creó o cambió indicadores? se compilan y se avisa si quedaron en «Mis indicadores» o qué falla
        ReportIndicators();

        // lo que miró del gráfico: la misma imagen, para que veas de qué habla
        ShowSnapshots();

        // reportes nuevos del laboratorio
        foreach (var rep in Lab.ReportStore.List().Where(x => !_reportsBefore.Contains(x.Path)).Reverse())
        {
            var path = rep.Path;
            var open = Ui.Button("View report", MaterialIconKind.FileDocumentOutline, () => OpenReport(path), "primary");
            open.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.FileChartOutline, 15, Ui.Accent), new TextBlock { Text = rep.Title, Classes = { "small" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 }, VerdictPill(rep.Verdict)), open));
            BuildStrategyMenu();
        }

        if (_labToggle.IsChecked == true) RefreshHistory();
        if (TradingHub.Strategies.LiveStore.LoadProposals().Any(p => p.Source == "claude" && p.IsPending && p.CreatedUtc > DateTime.UtcNow.AddMinutes(-30)))
        {
            var review = Ui.Button("Review and approve", MaterialIconKind.ScaleBalance, () =>
            {
                if (TopLevel.GetTopLevel(this) is not TradingHub.App.Windows.MainWindow main) return;
                main.Navigate("live");
                (main.Page("live") as LiveView)?.Select("proposals");
            }, "primary");
            review.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.ScaleBalance, 15, Ui.Accent), new TextBlock { Text = "The assistant proposed sizing adjustments awaiting approval", Classes = { "small" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 }), review));
        }
        if (r.Seconds > 0) AddNote(MaterialIconKind.TimerOutline, r.Seconds >= 120 ? $"{r.Seconds / 60:0.#} min" : $"{r.Seconds:0} s");
        AddNote(MaterialIconKind.InformationOutline, r.CostUsd is { } cost ? $"Reported cost: US${cost:0.00}" : "Cost not reported by this CLI.");
        if (r.Usage is { } usage) AddNote(MaterialIconKind.InformationOutline, $"Tokens: {usage.InputTokens} input · {usage.CachedInputTokens} cached · {usage.OutputTokens} output");
        ScrollChatToEnd();
    }

    // ===================================================================== historial de conversaciones

    private void ShowHistoryMenu(Control anchor)
    {
        var list = new StackPanel { Spacing = 2, Width = 360 };
        var convs = ChatHistory.List();
        if (convs.Count == 0)
            list.Children.Add(new TextBlock { Text = "No saved conversations yet.", Classes = { "muted", "small" }, Margin = new Thickness(8) });
        var flyout = new Flyout { Content = new ScrollViewer { Content = list, MaxHeight = 460 }, Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var c in convs.Take(60))
        {
            var conv = c;
            var title = new TextBlock { Text = string.IsNullOrWhiteSpace(conv.Title) ? "(untitled)" : conv.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5, FontWeight = ReferenceEquals(conv.Id, _conv.Id) || conv.Id == _conv.Id ? FontWeight.SemiBold : FontWeight.Normal };
            var meta = new TextBlock
            {
                Text = $"{conv.Engine} · {Ui.Ago(conv.UpdatedUtc)} · {conv.Messages.Count(m => m.Role == "user")} message(s){(conv.Lab ? " · laboratory" : "")}{(conv.Strategy != null ? " · " + conv.Strategy : "")}",
                Classes = { "faint", "small" },
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var open = new Button { Content = Ui.V(1, title, meta), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5) }.Classes("ghost");
            open.Click += (_, _) =>
            {
                flyout.Hide();
                LoadConversation(conv);
            };
            var delete = Ui.IconButton(MaterialIconKind.Close, "Delete this conversation from history", () =>
            {
                DeleteConversation(conv);
                flyout.Hide();
            }, 14);
            var row = new DockPanel();
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            row.Children.Add(open);
            list.Children.Add(row);
        }

        flyout.ShowAt(anchor);
    }
    private void DeleteConversation(ChatConversation conversation)
    {
        if (_chatBusy) return;
        ChatHistory.Delete(conversation.Id);
        if (conversation.Id != _conv.Id) return;
        _claude.NewConversation();
        _conv = new ChatConversation { Engine = _claude.Engine };
        _unhookChat?.Invoke();
        HookChatBridge();
        _chat.Children.Clear();
        _currentReply = null;
        ShowSuggestions();
    }


    /// <summary>Muestra una conversación guardada y la deja lista para seguirla (Claude retoma su sesión).</summary>
    private void LoadConversation(ChatConversation conv)
    {
        if (_chatBusy) return;
        if (!StrategyChatEngines.IsKnown(conv.Engine))
        {
            AddNote(MaterialIconKind.AlertCircleOutline, $"Cannot resume unknown assistant engine '{conv.Engine}'.", Ui.Down);
            return;
        }
        ChatHistory.Save(_conv);
        SwitchChatEngine(conv.Engine);
        _unhookChat?.Invoke();
        try { _claude.Resume(conv.SessionId); }
        catch (ArgumentException ex)
        {
            HookChatBridge();
            AddNote(MaterialIconKind.AlertCircleOutline, "Cannot resume saved conversation: " + ex.Message, Ui.Down);
            return;
        }
        _conv = conv;
        HookChatBridge();
        if (conv.Model != null) _chatModel.Text = conv.Model;
        if (conv.Effort != null) _chatEffort.Text = conv.Effort;
        _labToggle.IsChecked = conv.Lab;
        _chat.Children.Clear();
        foreach (var m in conv.Messages)
        {
            if (m.Role == "note") AddNote(MaterialIconKind.InformationOutline, m.Text);
            else AddBubble(m.Text, user: m.Role == "user");
        }

        _currentReply = null;
        AddNote(MaterialIconKind.History, conv.SessionId != null
            ? "Conversation resumed: continue with the existing context."
            : "Saved conversation has no CLI session. The next message starts a new session.");
        ScrollChatToEnd();
    }

    // ===================================================================== chat en una ventana aparte

    private void ToggleClaudeWindow()
    {
        if (_claudeWindow != null) DockClaude();
        else DetachClaude();
    }

    private void DetachClaude()
    {
        if (_claudePanel == null || _claudeGrid == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var s = App.Services.Settings;
        _claudeGrid.Children.Remove(_claudePanel);
        _claudeGrid.ColumnDefinitions[2].Width = new GridLength(0);
        if (_claudeSplit != null) _claudeSplit.IsVisible = false;
        var w = new Window
        {
            Title = $"{EngineTitle} · strategies",
            Width = Math.Clamp(s.ClaudeWindowWidth, 320, 1600),
            Height = Math.Clamp(s.ClaudeWindowHeight, 360, 2400),
            MinWidth = 320,
            MinHeight = 360,
            Background = Ui.Bg1,
            Content = _claudePanel,
        };
        var pos = new PixelPoint(s.ClaudeWindowX, s.ClaudeWindowY);
        if (s.ClaudeWindowX >= 0 && w.Screens.All.Any(sc => sc.WorkingArea.Contains(pos)))
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = pos;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        w.Closing += (_, _) =>
        {
            s.ClaudeWindowX = w.Position.X;
            s.ClaudeWindowY = w.Position.Y;
            if (w.Width > 100) s.ClaudeWindowWidth = w.Width;
            if (w.Height > 100) s.ClaudeWindowHeight = w.Height;
            s.Save();
        };
        w.Closed += (_, _) => DockClaude();
        _claudeWindow = w;
        if (_claudeDetach != null)
        {
            _claudeDetach.Content = Ui.Icon(MaterialIconKind.DockRight, 17);
            ToolTip.SetTip(_claudeDetach, "Dock chat beside the editor");
        }

        w.Show(owner);
    }

    private void DockClaude()
    {
        var w = _claudeWindow;
        if (w == null || _claudePanel == null || _claudeGrid == null) return;
        _claudeWindow = null;
        w.Content = null;
        if (_claudePanel.Parent == null)
        {
            Grid.SetColumn(_claudePanel, 2);
            _claudeGrid.Children.Add(_claudePanel);
        }

        _claudeGrid.ColumnDefinitions[2].Width = new GridLength(400);
        if (_claudeSplit != null) _claudeSplit.IsVisible = true;
        if (_claudeDetach != null)
        {
            _claudeDetach.Content = Ui.Icon(MaterialIconKind.OpenInNew, 17);
            ToolTip.SetTip(_claudeDetach, "Detach chat into a window");
        }

        if (w.IsVisible) w.Close();
    }

    /// <summary>Le pide al laboratorio que analice la cartera en vivo y proponga tamaños (los aprobás en En vivo → Propuestas).</summary>
    internal void AskClaudeForSizing()
    {
        _labToggle.IsChecked = true;
        _tabs.Select("code");
        ShowTab("code");
        _ = SendToClaudeAsync("Analyze the live portfolio using live_portfolio (PnL, trades, drawdown and recent streak for each strategy). Where appropriate, propose size changes with propose_sizing and explain each reason. Apply nothing: I approve every change in Trading Hub under Live → Proposals.");
    }
}
