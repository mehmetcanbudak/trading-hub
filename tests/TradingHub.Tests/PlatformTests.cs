using Avalonia.Input;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.Common;

namespace TradingHub.Tests;

/// <summary>Native Windows test; skipped on other platforms.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}

/// <summary>
/// Opt-in native Windows integration test. Credential tests use a unique test service and remove their entries.
/// </summary>
public sealed class WindowsOptInFactAttribute : FactAttribute
{
    public WindowsOptInFactAttribute(string variable)
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
        else if (Environment.GetEnvironmentVariable(variable) != "1") Skip = $"Set {variable}=1 to run";
    }
}

/// <summary>
/// Native shortcut and font selection, plus per-directory single-instance admission.
/// </summary>
public class PlatformTests
{
    [Fact]
    public void Shortcuts_UseMacSymbolsOnMacAndNamesWithPlusOnWindows()
    {
        Assert.Equal("⌘K", Keys.Format(KeyModifiers.Meta, "K", mac: true));
        Assert.Equal("⇧⌘D", Keys.Format(KeyModifiers.Meta | KeyModifiers.Shift, "D", mac: true));
        Assert.Equal("⌃⌥⇧⌘A", Keys.Format(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta, "A", mac: true));
        Assert.Equal("Ctrl+K", Keys.Format(KeyModifiers.Control, "K", mac: false));
        Assert.Equal("Ctrl+Shift+D", Keys.Format(KeyModifiers.Control | KeyModifiers.Shift, "D", mac: false));
        Assert.Equal("Ctrl+Alt+A", Keys.Format(KeyModifiers.Control | KeyModifiers.Alt, "A", mac: false));
        Assert.Equal("F5", Keys.Format(KeyModifiers.None, "F5", mac: false));
    }

    [Fact]
    public void CommandKey_IsCmdOnMacAndCtrlOnWindows()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? KeyModifiers.Control : KeyModifiers.Meta, Keys.Command);
        Assert.Equal(OperatingSystem.IsWindows() ? "Ctrl+Shift+D" : "⇧⌘D", Keys.Label("D", shift: true));
        Assert.Equal(OperatingSystem.IsWindows() ? "Ctrl+Enter" : "⌘Enter", Keys.Label("Enter"));
    }

    [Fact]
    public void Hotkeys_KeepMacSymbolsAndUseNamesOnWindows()
    {
        Assert.Equal("⌥1", Hotkeys.Pretty("Alt+D1", mac: true));
        Assert.Equal("Alt+1", Hotkeys.Pretty("Alt+D1", mac: false));
        Assert.Equal("⌃⇧Tab", Hotkeys.Pretty("Ctrl+Shift+Tab", mac: true));
        Assert.Equal("Ctrl+Shift+Tab", Hotkeys.Pretty("Ctrl+Shift+Tab", mac: false));
        Assert.Equal("↩", Hotkeys.Pretty("Enter", mac: true));
        Assert.Equal("Enter", Hotkeys.Pretty("Enter", mac: false));
        Assert.Equal("—", Hotkeys.Pretty(null, mac: false));
    }


    [Fact]
    public void MonoFont_IsMenloOnMacAndCascadiaOrConsolasOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("Cascadia Mono, Consolas", PlatformFonts.Mono);
            Assert.Equal("Consolas", PlatformFonts.MonoSkia);
            Assert.Equal("Segoe UI", PlatformFonts.SansSkia);
        }
        else
        {
            Assert.StartsWith("Menlo", PlatformFonts.Mono);
            Assert.Equal("Menlo", PlatformFonts.MonoSkia);
            Assert.Equal("Helvetica Neue", PlatformFonts.SansSkia);
        }

        Assert.Equal(PlatformFonts.MonoSkia, new TradingHub.Charting.ChartTheme().MonoFontFamily);
    }

    // Per-directory single-instance admission.

    [Fact]
    public void SingleInstance_DependsOnlyOnTheDataFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "th-data-a");
        var name = SingleInstance.NameFor(folder);
        Assert.StartsWith(@"Local\TradingHub-", name);
        Assert.Equal(name, SingleInstance.NameFor(folder + Path.DirectorySeparatorChar));
        Assert.Equal(name, SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "TH-DATA-A")));
        Assert.NotEqual(name, SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "th-data-b")));
    }

    [WindowsFact]
    public void SecondInstance_RequestsTheExistingWindowAndDoesNotAcquireTheDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradinghub-instance-" + Guid.NewGuid().ToString("N"));
        var name = SingleInstance.NameFor(root);
        using var mutex = new Mutex(true, name, out var created);
        Assert.True(created);
        try
        {
            using var request = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-mostrar");
            string? error = null;
            Assert.False(SingleInstance.Claim(root, message => error = message));
            Assert.True(request.WaitOne(TimeSpan.FromSeconds(2)));
            Assert.Null(error);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    [WindowsFact]
    public void AnExistingFileLease_BlocksAWriterEvenWithoutANamedMutex()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradinghub-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string? error = null;
            using (var lease = new FileStream(Path.Combine(root, ".tradinghub.instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.False(SingleInstance.Claim(root, message => error = message));
            Assert.NotNull(error);

            // A failed admission must release its temporary mutex and event.
            using var mutex = new Mutex(true, SingleInstance.NameFor(root), out var created);
            Assert.True(created);
            mutex.ReleaseMutex();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [WindowsFact]
    public void AnUnavailableDataDirectory_ReportsTheFailureAndDoesNotAdmitAWriter()
    {
        var path = Path.Combine(Path.GetTempPath(), "tradinghub-instance-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "not a directory");
        try
        {
            string? error = null;
            Assert.False(SingleInstance.Claim(path, message => error = message));
            Assert.NotNull(error);
            Assert.Equal("not a directory", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
