using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using TradingHub.Charting;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Localization;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public class EnglishLocalizationTests
{
    [Theory]
    [InlineData("Cruce de medias", "Moving-average crossover")]
    [InlineData("Cuenta simulada", "Simulated account")]
    [InlineData("Técnicos", "Technical")]
    [InlineData("Mis indicadores", "My indicators")]
    [InlineData("ganadora", "Profitable")]
    [InlineData("importado", "Imported")]
    [InlineData("bayesiana", "Bayesian")]
    [InlineData("User-defined name", "User-defined name")]
    [InlineData("Mi indicador personalizado", "Mi indicador personalizado")]
    public void Legacy_labels_are_translated_exactly_without_rewriting_custom_names(string stored, string displayed)
    {
        Assert.Equal(displayed, EnglishText.Of(stored));
    }

    [Fact]
    public void Sdk_defaults_are_English_and_user_overrides_still_take_precedence()
    {
        Assert.Equal("Bars", Strings.Bars);
        Assert.Equal("Font", Strings.Font);
        Assert.Equal("Height", Strings.Height);
        Assert.Equal("Middle", Strings.Middle);
        Assert.Equal("Time", Strings.Time);
        Assert.Equal("Up", Strings.Up);
        Assert.Equal("Zero value", Strings.ZeroValue);
        Assert.Equal("Fallback", Strings.Get("__unknown_localization_key", "Fallback"));

        var labels = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static);
        Assert.True(labels.Length > 1000);
        foreach (var label in labels)
            Assert.DoesNotMatch("[áéíóúñ¿¡]", (string)label.GetValue(null)!);

        var original = Strings.Bars;
        try
        {
            Strings.Set(nameof(Strings.Bars), "User-provided label");
            Assert.Equal("User-provided label", Strings.Bars);
        }
        finally
        {
            Strings.Set(nameof(Strings.Bars), original);
        }
    }

    [Theory]
    [InlineData("Bid x Ask (clásico)", "Bid x Ask (classic)")]
    [InlineData("Escalera de volumen + perfil", "Volume ladder + profile")]
    [InlineData("Escalera de delta + perfil de volumen", "Delta ladder + volume profile")]
    [InlineData("Doble escalera de delta", "Double delta ladder")]
    [InlineData("Perfil de volumen por vela", "Per-bar volume profile")]
    public void Footprint_display_names_do_not_change_persisted_template_names(string stored, string displayed)
    {
        var layout = new FootprintLayout { Name = stored };
        Assert.Equal(displayed, layout.DisplayName);
        var json = JsonSerializer.Serialize(layout);
        Assert.DoesNotContain("DisplayName", json);
        var restored = JsonSerializer.Deserialize<FootprintLayout>(json)!;
        Assert.Equal(stored, restored.Name);
        Assert.Equal(displayed, restored.DisplayName);
    }

    [Fact]
    public void Sdk_enum_display_labels_are_English_without_renaming_enum_values()
    {
        Assert.Equal("Bars", typeof(CandleVisualMode).GetField(nameof(CandleVisualMode.Bars))!
            .GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()!.GetName());
        Assert.Equal("Money", typeof(VolumeFilterUnit).GetField(nameof(VolumeFilterUnit.Money))!
            .GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()!.GetName());
    }

    [Fact]
    public void Legacy_script_metadata_is_English_but_parameter_keys_are_unchanged()
    {
        var strategy = new LegacyStrategy();
        Assert.Equal("Moving-average crossover", Strategy.DisplayName(typeof(LegacyStrategy)));
        var parameter = typeof(LegacyStrategy).GetProperty(nameof(LegacyStrategy.Rapida))!;
        Assert.Equal("Fast average", Strategy.ParameterName(parameter));
        Assert.Contains("Rapida", strategy.GetParameters().Keys);
        Assert.DoesNotContain("Fast average", strategy.GetParameters().Keys);
        strategy.SetParameters(new Dictionary<string, object?> { ["Rapida"] = 17 });
        Assert.Equal(17, strategy.Rapida);
        Assert.Equal("Cumulative delta (CVD)", new LegacyIndicator().Name);
    }

    [Theory]
    [InlineData("Puede ser suerte: 20%")]
    [InlineData("Probabilidad de que la ventaja sea real y no suerte: 95%")]
    [InlineData("Probabilidad de que no sea suerte: 40%")]
    [InlineData("May be luck: 20%")]
    [InlineData("Probability that the edge is real rather than luck: 95%")]
    public void Saved_validation_reports_keep_recognizing_old_and_new_luck_messages(string message)
    {
        Assert.True(ValidationGate.IsLuckLine(message));
        Assert.False(ValidationGate.IsLuckLine("A custom report line"));
    }

    [Theory]
    [InlineData("diario", PeriodType.Days)]
    [InlineData("semanal", PeriodType.Weeks)]
    [InlineData("mensual", PeriodType.Months)]
    public void Existing_period_aliases_remain_compatible(string alias, PeriodType type)
    {
        Assert.True(ChartPeriod.TryParse(alias, out var period));
        Assert.Equal(type, period.Type);
        Assert.Equal(1, period.Value);
    }

    [Theory]
    [InlineData(PeriodType.Weeks, 1, "W")]
    [InlineData(PeriodType.Weeks, 2, "2W")]
    [InlineData(PeriodType.Months, 1, "Month")]
    [InlineData(PeriodType.Months, 2, "2mo")]
    public void English_week_and_month_labels_round_trip_through_the_existing_period_parser(PeriodType type, int value, string label)
    {
        var period = new ChartPeriod(type, value);
        Assert.Equal(label, period.DisplayName);
        Assert.True(ChartPeriod.TryParse(label, out var parsed));
        Assert.Equal(period, parsed);
    }

    [Strategy("Cruce de medias")]
    private sealed class LegacyStrategy : Strategy
    {
        [TradingHub.Strategies.Parameter("Media rápida")]
        public int Rapida { get; set; } = 10;
    }

    [DisplayName("Delta acumulado (CVD)")]
    private sealed class LegacyIndicator : Indicator
    {
        protected override void OnCalculate(int bar, decimal value) { }
    }
}
