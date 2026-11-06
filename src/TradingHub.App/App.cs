using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Strategies;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingHub.UiSnapshot")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingHub.Tests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingHub.IbSnapshotCrashFixture")]

namespace TradingHub.App;

public sealed class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    private readonly Func<AppServices> _createServices;

    public App() : this(static () => new AppServices()) { }
    internal App(Func<AppServices> createServices) => _createServices = createServices ?? throw new ArgumentNullException(nameof(createServices));

    /// <summary>How Trading Hub quits (null without a desktop, as in tools).</summary>
    internal static QuitFlow? Quit { get; private set; }

    public override void Initialize()
    {
        Name = TradingHub.Common.Product.Name;
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://TradingHub/")) { Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml") });
        Styles.Add(new StyleInclude(new Uri("avares://TradingHub/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        Styles.Add(new Material.Icons.Avalonia.MaterialIconStyles(null));
        Styles.Add(new StyleInclude(new Uri("avares://TradingHub/")) { Source = new Uri("avares://TradingHub/Styles/TradingHubTheme.axaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = _createServices();
        TradingHub.App.Services.AppTheme.Apply(Services.Settings.ThemeMode);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow(Services);
            // Main-window closing is intercepted by QuitFlow before this lifetime can exit.
            if (OperatingSystem.IsWindows()) desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = main;
            UseQuit(desktop);
            // Closing a window can keep Trading Hub available in the macOS menu bar or Windows system tray.
            var menuBar = TradingHub.App.Services.MenuBar.Install(this, Services, main);
            SingleInstance.OnShowRequested(() => Dispatcher.UIThread.Post(() =>
            {
                if (menuBar != null) menuBar.ShowMain();
                else
                {
                    if (!main.IsVisible) main.Show();
                    if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                    main.Activate();
                }
            }));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Every quit of this lifetime takes one path (<see cref="QuitFlow"/>): ⌘Q, a Dock or logout quit (its shutdown request)
    /// and the menu bar's «Quit Trading Hub» (<see cref="MenuBar.Quit"/>). The services shut down when the app exits.
    /// </summary>
    internal static QuitFlow UseQuit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var quit = new QuitFlow(Services, () => desktop.MainWindow,
            new ApplicationQuitTerminal(Services, () => desktop.Shutdown()));
        desktop.ShutdownRequested += quit.OnShutdownRequested;
        desktop.Exit += (_, _) =>
        {
            Services.Shutdown();
            // Normal terminal exit already completed this; the native fallback is idempotent and
            // native-store process/I/O drains do not depend on the UI dispatcher.
            Keychain.ShutdownAsync().GetAwaiter().GetResult();
        };
        return Quit = quit;
    }
}

/// <summary>What happens to the live strategies' orders at the broker when Trading Hub quits.</summary>
internal enum QuitMode
{
    /// <summary>Their stop loss and take profit stay working at the broker, and the next start adopts them.</summary>
    KeepStops,

    /// <summary>Their orders are canceled and their positions stay open (what ⌘Q always did).</summary>
    CancelStops,

    /// <summary>Their positions are closed at market: the only quit that liquidates.</summary>
    ClosePositions,
}

/// <summary>One ordered terminal operation: drain, optional replacement handoff, then lifetime exit.</summary>
internal interface IQuitTerminalLifetime
{
    Task CompleteAsync(Action? beforeExit);
}

/// <summary>The desktop always uses this composition; a replacement cannot outrun owned shutdown.</summary>
internal sealed class ApplicationQuitTerminal(AppServices services, Action exit) : IQuitTerminalLifetime
{
    private readonly object _gate = new();
    private TaskCompletionSource? _completion;

    public Task CompleteAsync(Action? beforeExit)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_completion != null) return _completion.Task;
            completion = _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = CompleteCoreAsync(completion, beforeExit);
        return completion.Task;
    }

    private async Task CompleteCoreAsync(TaskCompletionSource completion, Action? beforeExit)
    {
        try
        {
            await services.ShutdownAsync();
            await Keychain.ShutdownAsync();
            beforeExit?.Invoke();
        }
        catch (Exception error) { CrashLog.Write(error); }
        finally
        {
            try { exit(); }
            finally { completion.TrySetResult(); }
        }
    }
}

/// <summary>
/// Quitting Trading Hub, the same from ⌘Q (the platform's shutdown request, also on a Dock or logout quit), from the menu
/// bar's «Quit Trading Hub» and from closing the main window when it is not kept in the menu bar. While live strategies
/// hold positions it first asks what to do with their orders (keep the stops at the broker, cancel them, close the
/// positions, or don't quit); without positions it quits as ⌘Q always did. Then it saves the live state, stops the
/// strategies that way and exits, once.
/// </summary>
internal sealed class QuitFlow(AppServices svc, Func<Window?> owner, IQuitTerminalLifetime terminal)
{
    private static CultureInfo En => TradingHub.Common.UiCulture.Current;
    private QuitDialog? _asking;
    private bool _quitting;
    private bool _terminalCompleted;

    /// <summary>Bounded wait for broker acknowledgment of quit-time closes and cancellations.</summary>
    internal TimeSpan CloseWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The protection decision is accepted. Terminal completion still precedes any actual exit.</summary>
    internal bool IsQuitting => _quitting;

    /// <summary>Owned terminal work and the desktop handoff have completed; acceptance alone is not this boundary.</summary>
    internal bool IsTerminalComplete => _terminalCompleted;

    /// <summary>Live strategies holding a position: what the question is about.</summary>
    private List<LiveStrategyRunner> Holding() =>
        svc.Runners.Where(r => r is { IsRunning: true, Persist: true } && r.OwnPosition != 0).ToList();

    /// <summary>
    /// Every first platform request is canceled and posted to the asynchronous protected path, even without
    /// positions: owned provider, authoring and recorder drains must not be bypassed by a fast exit.
    /// </summary>
    public void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_quitting)
        {
            e.Cancel = !_terminalCompleted;
            return;
        }

        e.Cancel = true;
        Dispatcher.UIThread.Post(() => _ = QuitAsync());
    }

    /// <summary>The menu bar's «Quit Trading Hub» (and a ⌘Q that has to ask first).</summary>
    public async Task QuitAsync(Action? beforeExit = null)
    {
        if (_quitting) return;
        if (_asking != null)
        {
            _asking.Activate(); // already asking
            return;
        }

        var mode = QuitMode.CancelStops;
        var holding = Holding();
        if (holding.Count > 0)
        {
            QuitMode? chosen;
            _asking = new QuitDialog(holding.Select(Describe).ToList());
            try
            {
                chosen = await _asking.ShowOverAsync(owner());
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                return; // it could not ask: nothing changes
            }
            finally
            {
                _asking = null;
            }

            if (chosen is not { } choice) return; // Don't quit: nothing changes
            mode = choice;
        }

        _quitting = true;
        try
        {
            await StopAsync(mode);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            beforeExit = null;
        }
        finally
        {
            await terminal.CompleteAsync(beforeExit);
            _terminalCompleted = true;
        }
    }

    /// <summary>Saves and stops once, then waits for requested order changes or warns before disconnecting.</summary>
    private async Task StopAsync(QuitMode mode)
    {
        var stopping = svc.Runners.Where(r => r.IsRunning || r.IsClosing || r.HasWorkingOrders).ToList();
        svc.StopLiveForQuit(mode);
        bool Unconfirmed(LiveStrategyRunner r) => r.HasWorkingOrders || r.IsClosing || (mode == QuitMode.ClosePositions && r.OwnPosition != 0);
        var until = DateTime.UtcNow + CloseWait;
        if (stopping.Any(Unconfirmed)) svc.Live.Notify("Waiting for broker confirmation before quitting…");
        while (stopping.Any(Unconfirmed) && DateTime.UtcNow < until) await Task.Delay(100);
        var open = stopping.Where(Unconfirmed).ToList();
        if (open.Count == 0) return;
        string title = mode == QuitMode.ClosePositions || open.Any(r => r.IsClosing) ? "Positions not closed" : "Orders not confirmed";
        await NoticeDialog.ShowAsync(owner(), title,
            $"Trading Hub could not confirm the requested closes or cancellations within {CloseWait.TotalSeconds.ToString("0", En)} seconds. " +
            "Requests may still be awaiting account data or broker acknowledgment. Positions and orders may still be working. Check them at the broker.\n\n" +
            string.Join("\n", open.Select(r => "• " + Describe(r))), "Quit");
    }

    /// <summary>«MA Cross» · long 1 MNQ · Simulated account · 2 protective orders working.</summary>
    private static string Describe(LiveStrategyRunner r)
    {
        decimal pos = r.OwnPosition;
        int protective = r.ProtectionOrderIds.Count;
        string orders = protective switch
        {
            0 => "no stop or target working",
            1 => "1 protective order working",
            _ => $"{protective} protective orders working",
        };
        return $"«{r.StrategyName}» · {(pos > 0 ? "long" : "short")} {Math.Abs(pos).ToString("0.####", En)} {r.ExecInstrument.Symbol} · " +
               $"{TradingHub.Common.EnglishText.Of(r.Account.Name)} · {orders}";
    }
}
