using System.Diagnostics;
using System.Text;
using Avalonia;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;

namespace TradingHub.Tests;

/// <summary>
/// Native Windows integrations and cross-platform command resolution/prompt transport.
/// </summary>
public class WindowsIntegrationTests
{
    [Fact]
    public void Reveal_TellsExplorerToSelectTheFile() =>
        Assert.Equal(@"/select,""C:\My reports\report (1).html""", Shell.RevealArguments(@"C:\My reports\report (1).html"));

    [WindowsOptInFact("TRADINGHUB_TEST_POWER")]
    public void KeepAwake_WindowsAcceptsTheExecutionState() => Assert.True(SleepGuard.ProbeWindowsExecutionState());


    [Fact]
    public void TrayPanel_SitsOnTheTaskbarAndInsideTheScreen()
    {
        var size = new PixelSize(380, 400);
        // Bottom taskbar: above the click without covering the taskbar.
        Assert.Equal(new PixelPoint(1920 - 380 - 8, 1032 - 400 - 8), MenuBar.TrayPanelPosition(new PixelRect(0, 0, 1920, 1032), new PixelPoint(1800, 1060), size, 8));
        // Top taskbar: below it.
        Assert.Equal(40 + 8, MenuBar.TrayPanelPosition(new PixelRect(0, 40, 1920, 1040), new PixelPoint(1800, 20), size, 8).Y);
        // Left taskbar: beside it at the click's height.
        Assert.Equal(new PixelPoint(60 + 8, 1050 - 400 - 8), MenuBar.TrayPanelPosition(new PixelRect(60, 0, 1860, 1080), new PixelPoint(30, 1050), size, 8));
        // Secondary display to the left of the primary, with negative coordinates.
        Assert.Equal(new PixelPoint(-380 - 8, 1040 - 400 - 8), MenuBar.TrayPanelPosition(new PixelRect(-1920, 0, 1920, 1040), new PixelPoint(-100, 1060), size, 8));
        // Panel taller than the display: align at the top.
        Assert.Equal(8, MenuBar.TrayPanelPosition(new PixelRect(0, 0, 1366, 728), new PixelPoint(1300, 750), new PixelSize(380, 900), 8).Y);
    }

    private const string NpmShim = "@ECHO off\r\nGOTO start\r\n:find_dp0\r\nSET dp0=%~dp0\r\nEXIT /b\r\n:start\r\nSETLOCAL\r\nCALL :find_dp0\r\n\r\n" +
                                   "IF EXIST \"%dp0%\\node.exe\" (\r\n  SET \"_prog=%dp0%\\node.exe\"\r\n) ELSE (\r\n  SET \"_prog=node\"\r\n  SET PATHEXT=%PATHEXT:;.JS;=;%\r\n)\r\n\r\n" +
                                   "endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & \"%_prog%\"  \"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n";

    private const string PnpmShim = "@SETLOCAL\r\n@IF EXIST \"%~dp0\\node.exe\" (\r\n  \"%~dp0\\node.exe\" \"%~dp0\\global\\5\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n" +
                                    ") ELSE (\r\n  @SET PATHEXT=%PATHEXT:;.JS;=;%\r\n  node \"%~dp0\\global\\5\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" %*\r\n)\r\n";

    [Fact]
    public void ClaudeCli_ReadsWhatNpmAndPnpmShimsRun()
    {
        Assert.Equal(@"node_modules\@anthropic-ai\claude-code\cli.js", ClaudeCodeBridge.CmdShimTarget(NpmShim));
        Assert.Equal(@"global\5\node_modules\@anthropic-ai\claude-code\cli.js", ClaudeCodeBridge.CmdShimTarget(PnpmShim));
        Assert.Equal(@"node_modules\@anthropic-ai\claude-code\bin\claude.exe", ClaudeCodeBridge.CmdShimTarget("@ECHO off\r\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*\r\n"));
        Assert.Null(ClaudeCodeBridge.CmdShimTarget("@echo off\r\nnode %*\r\n"));
    }

    [Fact]
    public void ClaudeCli_AnNpmShimRunsNodeWithTheScriptSoTheArgumentsArriveIntact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tradinghub-shim-" + Guid.NewGuid().ToString("N"));
        try
        {
            var script = Path.Combine(dir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, "// cli");
            var cmd = Path.Combine(dir, "claude.cmd");
            File.WriteAllText(cmd, NpmShim);

            // Use node from PATH if it is not bundled beside the shim.
            var (file, prefix) = ClaudeCodeBridge.WindowsCommand(cmd, _ => @"C:\Program Files\nodejs\node.exe");
            Assert.Equal(@"C:\Program Files\nodejs\node.exe", file);
            Assert.Equal(new[] { Path.GetFullPath(script) }, prefix);

            // A node executable next to the shim takes precedence.
            File.WriteAllText(Path.Combine(dir, "node.exe"), "");
            Assert.Equal(Path.Combine(dir, "node.exe"), ClaudeCodeBridge.WindowsCommand(cmd, _ => null).File);

            // Follow yarn's nested shim to the JavaScript entry point.
            var outer = Path.Combine(dir, "yarn", "claude.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(outer)!);
            File.WriteAllText(outer, "@\"%~dp0\\..\\claude.cmd\" %*\r\n");
            Assert.Equal(new[] { Path.GetFullPath(script) }, ClaudeCodeBridge.WindowsCommand(outer, _ => null).Prefix);

            // Native executables run directly; unknown shims must not corrupt prompts through cmd.exe.
            Assert.Empty(ClaudeCodeBridge.WindowsCommand(Path.Combine(dir, "claude.exe")).Prefix);
            var odd = Path.Combine(dir, "unknown", "claude.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(odd)!);
            File.WriteAllText(odd, "@echo off\r\nnode %*\r\n");
            Assert.Throws<InvalidOperationException>(() => ClaudeCodeBridge.WindowsCommand(odd, _ => null));

            var cycle = Path.Combine(dir, "cycle.cmd");
            File.WriteAllText(cycle, "@\"%~dp0\\cycle.cmd\" %*\r\n");
            Assert.Throws<InvalidOperationException>(() => ClaudeCodeBridge.WindowsCommand(cycle, _ => null));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ClaudeCli_WindowsLooksInTheInstallerNpmAndThePath()
    {
        var list = ClaudeCodeBridge.WindowsCandidates("H", "R", "L", @"C:\tools;""C:\with spaces"";;").ToList();
        Assert.Equal(Path.Combine("H", ".local", "bin", "claude.exe"), list[0]);
        Assert.True(list.IndexOf(Path.Combine("R", "npm", "claude.cmd")) > 0);
        Assert.Contains(Path.Combine(@"C:\tools", "claude.exe"), list);
        Assert.Contains(Path.Combine(@"C:\with spaces", "claude.cmd"), list);
        Assert.Contains(Path.Combine(@"C:\tools", "claude.bat"), list);
        Assert.Equal(new[] { @"C:\Users\a\.local\bin\claude.exe", @"C:\Users\a\AppData\Roaming\npm\claude.cmd" },
            ClaudeCodeBridge.ParseWhere("C:\\Users\\a\\.local\\bin\\claude.exe\r\nC:\\Users\\a\\AppData\\Roaming\\npm\\claude\r\nC:\\Users\\a\\AppData\\Roaming\\npm\\claude.cmd\r\n"));
    }

    [WindowsFact]
    public async Task ClaudeCli_LoginPreservesPathsWithSpacesAndCmdMetacharacters()
    {
        var root = Path.Combine(Path.GetTempPath(), "tradinghub-login-100% & ! (test)-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cli = Path.Combine(root, "claude.cmd");
            File.WriteAllText(cli, "@echo off\r\nif \"%~1\"==\"/login\" (echo expected-login) else (exit /b 2)\r\n");
            var psi = new ProcessStartInfo("cmd.exe", ClaudeCodeBridge.WindowsLoginArguments("%TRADINGHUB_CLAUDE_LOGIN%", keepOpen: false))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            psi.Environment["TRADINGHUB_CLAUDE_LOGIN"] = cli;
            using var process = Process.Start(psi)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, process.ExitCode);
                Assert.Equal("expected-login", (await output).Trim());
                Assert.Equal("", await error);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ClaudeCli_OnlyAPromptTooLongForWindowsGoesThroughStdin()
    {
        var system = ClaudeCodeBridge.StrategySystemPrompt + ClaudeCodeBridge.IndicatorSystemPrompt + ClaudeCodeBridge.LabSystemPrompt;
        var shortPrompt = "Quotes \" and slashes \\\\; $ & % ! ^ < >\r\n中文 😀\nsecond line";
        var shortArgs = new[] { "-p", shortPrompt, "--append-system-prompt", system };
        var shortPsi = new ProcessStartInfo("claude.exe");
        foreach (var argument in shortArgs) shortPsi.ArgumentList.Add(argument);
        Assert.Null(ClaudeCodeBridge.MovePromptToStdinIfTooLong(shortPsi));
        Assert.Equal(shortArgs, shortPsi.ArgumentList);

        var args = new[] { "-p", shortPrompt + new string('x', 40_000), "--output-format", "stream-json", "--append-system-prompt", system };
        var psi = new ProcessStartInfo("claude.exe");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var moved = ClaudeCodeBridge.MovePromptToStdinIfTooLong(psi);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(args[1], moved);
            Assert.Equal(new[] { "-p", "--output-format", "stream-json", "--append-system-prompt", system }, psi.ArgumentList);
        }
        else
        {
            Assert.Null(moved); // macOS retains argument transport.
            Assert.Equal(args, psi.ArgumentList);
        }
    }

    [Fact]
    public async Task PromptStdin_PreservesUnicodeWhitespaceAndShellCharactersWithoutAddingANewline()
    {
        bool windows = OperatingSystem.IsWindows();
        var executable = windows
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
            : "/bin/cat";
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
        };
        if (windows)
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("[Console]::InputEncoding = [Text.UTF8Encoding]::new($false); " +
                "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); [Console]::Write([Console]::In.ReadToEnd())");
        }

        var prompt = "  start\r\nquotes \" ' \\ & | ^ %PATH% ! $ `\t中文 😀\n" + new string('x', 40_000) + "  end";
        using var process = Process.Start(psi)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await ClaudeCodeBridge.FeedStdinAsync(process, prompt).WaitAsync(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(prompt, await output);
            Assert.Equal("", await error);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
