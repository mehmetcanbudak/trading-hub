using TradingHub.Scripting;

namespace TradingHub.Tests;

/// <summary>ATAS namespace adaptation rewrites code, never the user's own strings or comments.</summary>
public sealed class ScriptPreprocessTests
{
    [Fact]
    public void Namespaces_are_rewritten_in_code_and_the_most_specific_one_wins()
    {
        string adapted = ScriptCompiler.Preprocess("""
            using ATAS.Indicators;
            using OFT.Rendering.Context.GDIPlus;
            var x = ATAS.Indicators.Technical.Sma.Name;
            """);
        Assert.Contains("using TradingHub.Indicators;", adapted);
        Assert.Contains("using TradingHub.Rendering.Context;", adapted);
        Assert.Contains("TradingHub.Indicators.Technical.Sma.Name", adapted);
        Assert.DoesNotContain("ATAS.", adapted);
        Assert.DoesNotContain("OFT.", adapted);
    }

    [Fact]
    public void Strings_chars_comments_and_interpolated_text_are_left_alone()
    {
        const string source = """"
            // ATAS.Indicators in a line comment
            /* OFT.Rendering in a block comment */
            var a = "ATAS.Indicators";
            var b = @"Utils.Common\path";
            var c = """ATAS.DataFeedsCore""";
            var d = $"ATAS.Indicators {ATAS.Indicators.Technical.Sma.Name} OFT.Localization";
            """";
        string adapted = ScriptCompiler.Preprocess(source);
        Assert.Contains("// ATAS.Indicators in a line comment", adapted);
        Assert.Contains("/* OFT.Rendering in a block comment */", adapted);
        Assert.Contains("var a = \"ATAS.Indicators\";", adapted);
        Assert.Contains("@\"Utils.Common\\path\"", adapted);
        Assert.Contains("\"\"\"ATAS.DataFeedsCore\"\"\"", adapted);
        // Only the interpolation hole is code.
        Assert.Contains("$\"ATAS.Indicators {TradingHub.Indicators.Technical.Sma.Name} OFT.Localization\"", adapted);
    }
}
