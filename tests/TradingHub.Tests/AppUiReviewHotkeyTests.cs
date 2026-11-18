using Avalonia.Input;
using TradingHub.App.Services;

namespace TradingHub.Tests;

/// <summary>Shortcut matching: Option keeps typing in text fields on macOS, and user bindings win over defaults.</summary>
public class AppUiReviewHotkeyTests
{
    private static KeyEventArgs Press(Key key, KeyModifiers modifiers) => new() { Key = key, KeyModifiers = modifiers };

    [Theory]
    [InlineData(Key.N)] // Option+N: the ñ dead key
    [InlineData(Key.C)] // Option+C: ç
    [InlineData(Key.H)]
    public void OptionShortcuts_DoNotFireWhileTypingOnMac(Key key)
    {
        var settings = new AppSettings();
        var e = Press(key, KeyModifiers.Alt);

        Assert.Null(Hotkeys.Match(settings, e, typing: true, mac: true));
        // outside a text field they still work on macOS, and Windows keeps Alt as a shortcut modifier
        Assert.NotNull(Hotkeys.Match(settings, e, typing: false, mac: true));
        Assert.NotNull(Hotkeys.Match(settings, e, typing: true, mac: false));
    }

    [Fact]
    public void CtrlAndCmdShortcuts_StillFireWhileTypingOnMac()
    {
        var settings = new AppSettings();
        settings.Hotkeys["toggle-dom"] = "Cmd+Shift+D";

        Assert.Equal("next-tab", Hotkeys.Match(settings, Press(Key.Tab, KeyModifiers.Control), typing: true, mac: true));
        Assert.Equal("toggle-dom", Hotkeys.Match(settings, Press(Key.D, KeyModifiers.Meta | KeyModifiers.Shift), typing: true, mac: true));
    }

    [Fact]
    public void UserBinding_WinsOverANewDefaultWithTheSameKeys()
    {
        var settings = new AppSettings();
        // bound before tool-cross got Alt+C as its default; toggle-dom comes later in the list
        settings.Hotkeys["toggle-dom"] = "Alt+C";

        Assert.Equal("toggle-dom", Hotkeys.Match(settings, Press(Key.C, KeyModifiers.Alt), typing: false, mac: true));
        Assert.Null(Hotkeys.GestureOf(settings, "tool-cross"));
        Assert.Equal("Alt+F", Hotkeys.GestureOf(settings, "tool-fib"));
    }

    [Fact]
    public void UserBinding_WinsOverADefaultWrittenWithModifiersInAnotherOrder()
    {
        var settings = new AppSettings();
        // the Settings capture stores Avalonia's order; the default is written "Alt+Shift+T"
        settings.Hotkeys["chart-shortcuts"] = "Shift+Alt+T";

        Assert.Equal("chart-shortcuts", Hotkeys.Match(settings, Press(Key.T, KeyModifiers.Alt | KeyModifiers.Shift), typing: false, mac: true));
        Assert.Null(Hotkeys.GestureOf(settings, "tool-trend"));
    }

    [Fact]
    public void Default_ComesBackWhenTheUserBindingIsCleared()
    {
        var settings = new AppSettings();
        settings.Hotkeys["toggle-dom"] = "";

        Assert.Equal("Alt+C", Hotkeys.GestureOf(settings, "tool-cross"));
        Assert.Equal("tool-cross", Hotkeys.Match(settings, Press(Key.C, KeyModifiers.Alt), typing: false, mac: true));
    }
}
