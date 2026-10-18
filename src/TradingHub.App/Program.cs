using Avalonia;

namespace TradingHub.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // modo laboratorio: Trading Hub como servidor MCP para Claude Code (sin ventanas)
        if (args.Contains("--mcp"))
        {
            Environment.ExitCode = Lab.LabServer.RunAsync().GetAwaiter().GetResult();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };

        WaitForDisplay();
        try
        {
            BuildAvaloniaApp().AfterSetup(_ =>
            {
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

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, uint[]? activeDisplays, out uint displayCount);

    /// <summary>
    /// Con la pantalla apagada o bloqueada macOS no tiene pantalla activa y Avalonia no puede arrancar el dibujo
    /// (error -6661): en vez de cerrarse, Trading Hub espera a que vuelva la pantalla y ahí abre.
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
