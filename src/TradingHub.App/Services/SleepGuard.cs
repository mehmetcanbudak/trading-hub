using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TradingHub.App.Services;

/// <summary>
/// Modo sin dormir. Mientras haya estrategias en vivo o agentes de Claude trabajando, la Mac no se duerme por
/// inactividad: una aserción de energía de macOS (como hace una app de video), sin contraseña y se suelta sola cuando no
/// hay nada corriendo. Con la tapa cerrada macOS duerme igual; para seguir así hace falta «seguir con la tapa cerrada»:
/// pide tu contraseña de administrador una vez por sesión de Trading Hub y deja la Mac sin dormir (pmset disablesleep) solo
/// mientras Trading Hub esté abierta. Se deshace solo al cerrar Trading Hub, aunque se cierre de golpe, y opcionalmente solo con el
/// cargador conectado.
/// </summary>
internal sealed class SleepGuard : IDisposable
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;
    private const uint LevelOn = 255;

    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(IntPtr assertionType, uint level, IntPtr reason, out uint assertionId);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint assertionId);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    private uint _assertion;
    private bool _held;
    private Process? _helper;
    private DateTime _lastLidCheck;

    /// <summary>Sobre qué carpeta se arma el aviso para el ayudante (existe mientras la tapa cerrada tenga que funcionar).</summary>
    public string FlagDirectory { get; set; } = AppPaths.Root;

    /// <summary>Para las pruebas: en vez de pedir permiso y correr el ayudante, guarda el AppleScript que correría.</summary>
    public Func<string, Process?>? RunAdminScript { get; set; }

    /// <summary>Hay una aserción activa: la Mac no se duerme por inactividad.</summary>
    public bool Awake => _held;

    /// <summary>Por qué está despierta (o null).</summary>
    public string? Reason { get; private set; }

    /// <summary>El ayudante de la tapa cerrada está corriendo (ya se dio el permiso).</summary>
    public bool LidHelperRunning => _helper is { HasExited: false } || (RunAdminScript != null && File.Exists(Flag));

    /// <summary>El ayudante no arrancó (por ejemplo, cancelaste el permiso).</summary>
    public string? LidError { get; private set; }

    private string Flag => Path.Combine(FlagDirectory, "tapa-cerrada.activo");
    private string AcOnlyFlag => Path.Combine(FlagDirectory, "tapa-cerrada.solo-con-cargador");

    /// <summary>
    /// En herramientas y pruebas (datos en otra carpeta) no se toca la energía de la Mac ni se pide ningún permiso,
    /// salvo con un ayudante de mentira (<see cref="RunAdminScript"/>).
    /// </summary>
    private static readonly bool Sandboxed =
        Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } || Environment.GetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD") == "off";

    /// <summary>Lo llama la app cada pocos segundos con lo que está corriendo.</summary>
    public void Update(bool keepAwake, bool lidClosed, bool onlyOnPower, string? reason)
    {
        if (Sandboxed && RunAdminScript == null) return;
        bool needed = keepAwake && reason != null;
        if (needed && !_held) Hold(reason!);
        else if (!needed && _held) Release();
        Reason = needed ? reason : null;

        // tapa cerrada: se arranca recién cuando hay algo que lo necesita y se mantiene mientras esté activado
        try
        {
            if (onlyOnPower) File.WriteAllText(AcOnlyFlag, "1");
            else if (File.Exists(AcOnlyFlag)) File.Delete(AcOnlyFlag);
        }
        catch
        {
            // sin carpeta: se reintenta
        }

        if (lidClosed && needed && !LidHelperRunning && (DateTime.UtcNow - _lastLidCheck).TotalSeconds > 30) StartLidHelper();
        else if (!lidClosed && LidHelperRunning) StopLidHelper();
    }

    private void Hold(string reason)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (Sandboxed)
        {
            _held = true; // en pruebas: se simula
            return;
        }

        IntPtr type = IntPtr.Zero, name = IntPtr.Zero;
        try
        {
            type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", Utf8);
            name = CFStringCreateWithCString(IntPtr.Zero, "Trading Hub: " + reason, Utf8);
            _held = IOPMAssertionCreateWithName(type, LevelOn, name, out _assertion) == 0;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
        finally
        {
            if (type != IntPtr.Zero) CFRelease(type);
            if (name != IntPtr.Zero) CFRelease(name);
        }
    }

    private void Release()
    {
        if (!_held) return;
        if (Sandboxed)
        {
            _held = false;
            return;
        }

        try
        {
            IOPMAssertionRelease(_assertion);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        _held = false;
    }

    /// <summary>
    /// El ayudante con permisos de administrador (macOS te pide la contraseña): mientras exista el aviso y Trading Hub esté
    /// abierta, la Mac no se duerme al cerrar la tapa (si se eligió, solo con el cargador). Al terminar deja todo como estaba.
    /// No ejecuta nada de archivos: solo mira si existen.
    /// </summary>
    internal string BuildScript(int pid)
    {
        static string Q(string path) => "'" + path.Replace("'", "'\\''") + "'";
        string sh =
            $"flag={Q(Flag)}; ac={Q(AcOnlyFlag)}; cur=x; " +
            $"while kill -0 {pid} 2>/dev/null && [ -f \"$flag\" ]; do " +
            "want=1; if [ -f \"$ac\" ] && ! /usr/bin/pmset -g ps | /usr/bin/head -1 | /usr/bin/grep -q 'AC Power'; then want=0; fi; " +
            "if [ \"$want\" != \"$cur\" ]; then /usr/bin/pmset -a disablesleep $want; cur=$want; fi; " +
            "sleep 5; done; /usr/bin/pmset -a disablesleep 0";
        string escaped = sh.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"do shell script \"{escaped}\" with prompt \"Trading Hub needs permission to keep your Mac running with the lid closed while strategies or agents work. This is restored when Trading Hub quits.\" with administrator privileges";
    }

    private void StartLidHelper()
    {
        _lastLidCheck = DateTime.UtcNow;
        LidError = null;
        try
        {
            Directory.CreateDirectory(FlagDirectory);
            File.WriteAllText(Flag, Environment.ProcessId.ToString());
            string script = BuildScript(Environment.ProcessId);
            if (RunAdminScript != null)
            {
                _helper = RunAdminScript(script);
                return;
            }

            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            var p = Process.Start(psi)!;
            _helper = p;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                // terminó: o cancelaste el permiso (código distinto de 0) o se apagó el modo
                if (p.ExitCode != 0) LidError = "Not enabled: administrator permission is required.";
            };
        }
        catch (Exception ex)
        {
            LidError = ex.Message;
            CrashLog.Write(ex);
        }
    }

    private void StopLidHelper()
    {
        try
        {
            if (File.Exists(Flag)) File.Delete(Flag); // el ayudante lo ve en ≤ 5 s y deja la Mac como estaba
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        _helper = null;
    }

    /// <summary>¿macOS tiene el «no dormir» puesto ahora? (lo puede leer cualquiera)</summary>
    public static bool SystemSleepDisabled()
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/pmset", "-g") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return text.Split('\n').Any(l => l.Contains("SleepDisabled", StringComparison.Ordinal) && l.TrimEnd().EndsWith('1'));
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Release();
        StopLidHelper();
    }
}
