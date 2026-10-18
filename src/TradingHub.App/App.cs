using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using TradingHub.App.Services;
using TradingHub.App.Windows;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingHub.UiSnapshot")]

namespace TradingHub.App;

public sealed class App : Application
{
    public static AppServices Services { get; private set; } = null!;

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
            desktop.ShutdownRequested += (_, _) => Services.Shutdown();
            // el ícono en la barra de menú de la Mac (cerrar la ventana deja a Trading Hub corriendo ahí)
            TradingHub.App.Services.MenuBar.Install(this, Services, main);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
