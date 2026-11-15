using Avalonia;
using TradingHub.App.Services;

namespace TradingHub.Tests;

/// <summary>TRADINGHUB_RENDERING: explicit diagnostic backends, default when empty, invalid values ignored.</summary>
public class RenderingDiagnosticsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Empty_value_keeps_the_platform_default(string? value)
    {
        Assert.Null(RenderingDiagnostics.ParseRenderingMode(value, out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("metal", new[] { AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software })]
    [InlineData(" OpenGL ", new[] { AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software })]
    [InlineData("Software", new[] { AvaloniaNativeRenderingMode.Software })]
    public void Explicit_backend_is_pinned_with_software_as_last_resort(string value, AvaloniaNativeRenderingMode[] expected)
    {
        Assert.Equal(expected, RenderingDiagnostics.ParseRenderingMode(value, out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("vulkan")]
    [InlineData("gpu")]
    public void Invalid_value_is_ignored_with_a_message(string value)
    {
        Assert.Null(RenderingDiagnostics.ParseRenderingMode(value, out string? error));
        Assert.Contains(RenderingDiagnostics.RenderingVariable, error);
        Assert.Contains(value, error);
    }
}
