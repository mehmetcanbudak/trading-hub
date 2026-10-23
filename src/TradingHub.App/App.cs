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

namespace TradingHub.App;

public sealed class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    /// <summary>How Trading Hub quits (null without a desktop, as in tools).</summary>
    internal static QuitFlow? Quit { get; private set; }

    public override void Initialize()
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
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
        Services = new AppServices();
        TradingHub.App.Services.AppTheme.Apply(Services.Settings.ThemeMode);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow(Services);
            desktop.MainWindow = main;
            UseQuit(desktop);
            // el ícono en la barra de menú de la Mac (cerrar la ventana deja a Trading Hub corriendo ahí)
            TradingHub.App.Services.MenuBar.Install(this, Services, main);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Every quit of this lifetime takes one path (<see cref="QuitFlow"/>): ⌘Q, a Dock or logout quit (its shutdown request)
    /// and the menu bar's «Quit Trading Hub» (<see cref="MenuBar.Quit"/>). The services shut down when the app exits.
    /// </summary>
    internal static QuitFlow UseQuit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var quit = new QuitFlow(Services, () => desktop.MainWindow, () => desktop.Shutdown());
        desktop.ShutdownRequested += quit.OnShutdownRequested;
        desktop.Exit += (_, _) => Services.Shutdown();
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

/// <summary>
/// Quitting Trading Hub, the same from ⌘Q (the platform's shutdown request, also on a Dock or logout quit) and from the menu
/// bar's «Quit Trading Hub». While live strategies hold positions it first asks what to do with their orders (keep the
/// stops at the broker, cancel them, close the positions, or don't quit); without positions it quits as ⌘Q always did.
/// Then it saves the live state, stops the strategies that way and exits, once.
/// </summary>
internal sealed class QuitFlow(AppServices svc, Func<Window?> owner, Action exit)
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private QuitDialog? _asking;
    private bool _quitting;

    /// <summary>How long «Close positions and quit» waits for the broker to confirm the closes.</summary>
    internal TimeSpan CloseWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Live strategies holding a position: what the question is about.</summary>
    private List<LiveStrategyRunner> Holding() =>
        svc.Runners.Where(r => r is { IsRunning: true, Persist: true } && r.OwnPosition != 0).ToList();

    /// <summary>
    /// ⌘Q (and a Dock or logout quit). Without positions the strategies stop right away, canceling their orders, and the app
    /// closes as before. Otherwise this request is canceled and it asks; once decided it exits by itself, without a new
    /// request (so it never asks twice).
    /// </summary>
    public void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_quitting) return;
        if (_asking == null && Holding().Count == 0)
        {
            _quitting = true;
            svc.StopLiveForQuit(QuitMode.CancelStops);
            return;
        }

        e.Cancel = true;
        Dispatcher.UIThread.Post(() => _ = QuitAsync());
    }

    /// <summary>The menu bar's «Quit Trading Hub» (and a ⌘Q that has to ask first).</summary>
    public async Task QuitAsync()
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
        }
        finally
        {
            exit();
        }
    }

    /// <summary>Saves and stops the live strategies that way; with closing, waits for the closes and says if some did not.</summary>
    private async Task StopAsync(QuitMode mode)
    {
        var positioned = Holding();
        svc.StopLiveForQuit(mode);
        if (mode != QuitMode.ClosePositions || positioned.Count == 0) return;
        var until = DateTime.UtcNow + CloseWait;
        if (positioned.Any(Open)) svc.Live.Notify("Closing positions before quitting…");
        while (positioned.Any(Open) && DateTime.UtcNow < until) await Task.Delay(100);
        var open = positioned.Where(Open).ToList();
        if (open.Count == 0) return;
        await NoticeDialog.ShowAsync(owner(), "Positions not closed",
            $"Trading Hub sent market orders to close these positions, but the broker did not confirm them within {CloseWait.TotalSeconds.ToString("0", En)} seconds. " +
            "They may still be open, and their stops were canceled. Check them at the broker.\n\n" + string.Join("\n", open.Select(r => "• " + Describe(r))), "Quit");

        static bool Open(LiveStrategyRunner r) => r.IsClosing || r.OwnPosition != 0;
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
