using Avalonia;

namespace TradingHub.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--import-ib"))
        {
            Environment.ExitCode = Services.IbImportCommand.Run(args, Console.Out, Console.Error);
            return;
        }
        if (args.Contains("--research-mcp"))
        {
            Environment.ExitCode = Services.TradingViewResearchMcp.RunAsync(args).GetAwaiter().GetResult();
            return;
        }

        bool headless = args.Contains("--mcp");
        // Only the desktop applies an explicitly staged relocation, before paths or a writer context are frozen.
        try
        {
            if (!headless) _ = Services.StoragePreferences.ApplyPendingRelocation();
            _ = TradingHub.Common.StorageLocations.Startup;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            string message = "Trading Hub could not open its explicitly selected storage. No empty replacement account or data store was created. Restore the selected location or correct the storage selection before restarting. " + error.Message;
            if (headless) Console.Error.WriteLine(message);
            else Services.SingleInstance.ReportStartupError(message);
            Environment.ExitCode = 2;
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };

        // The desktop is the data writer; a standalone MCP reader must not claim its instance lease.
        if (!headless && !Services.SingleInstance.Claim(Services.AppPaths.Root)) return;
        LoadLanguage(persist: !headless);
        if (headless)
        {
            Environment.ExitCode = Lab.LabServer.RunAsync().GetAwaiter().GetResult();
            return;
        }

        WaitForDisplay();
        try
        {
            BuildAvaloniaApp().AfterSetup(_ =>
            {
                Localization.Lang.Install();
                // un error en un control se registra y la app sigue andando (no se cierra en pleno trading)
                Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
                {
                    CrashLog.Write(e.Exception);
                    e.Handled = true;
                };
            }).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            throw;
        }
    }

    /// <summary>
    /// Loads the selected language before either desktop or MCP work. Existing English-fork settings without a language
    /// stay English. A standalone MCP process reads the preference without rewriting settings owned by the desktop.
    /// </summary>
    private static void LoadLanguage(bool persist)
    {
        try
        {
            bool existing = Services.AppSettings.Saved;
            var s = Services.AppSettings.Load();
            if (s.Language == null)
            {
                s.Language = Localization.Lang.Default(existing);
                if (persist) s.Save();
            }

            Localization.Lang.Load(s.Language);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            Localization.Lang.Load(Localization.Lang.English);
        }
    }

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, uint[]? activeDisplays, out uint displayCount);

    /// <summary>
    /// Con la pantalla apagada o bloqueada macOS no tiene pantalla activa y Avalonia no puede arrancar el dibujo
    /// (error -6661): en vez de cerrarse, Trading Hub espera a que vuelva la pantalla y ahí abre. En Windows no hace falta.
    /// </summary>
    private static void WaitForDisplay()
    {
        if (!OperatingSystem.IsMacOS()) return;
        for (int i = 0; ; i++)
        {
            try
            {
                if (CGGetActiveDisplayList(0, null, out var count) != 0 || count > 0) return;
            }
            catch (Exception)
            {
                return; // ante la duda arranca como siempre
            }

            if (i == 0) CrashLog.Write(new InvalidOperationException("No active display (off or locked): Trading Hub opens when a display becomes available"));
            Thread.Sleep(2000);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            // menús y desplegables dentro de la ventana: como ventanas aparte, a veces se perdía el primer clic
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
            .LogToTrace();
}

/// <summary>Guarda los errores inesperados en crash.log, en la carpeta de datos de la app.</summary>
internal static class CrashLog
{
    public static void Write(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Services.AppPaths.Root);
            File.AppendAllText(Path.Combine(Services.AppPaths.Root, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // nada que hacer
        }
    }
}
