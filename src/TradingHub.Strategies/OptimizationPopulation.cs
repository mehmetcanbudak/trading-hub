using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingHub.Strategies;

/// <summary>Complete measured-population statistics, independent of the bounded rows retained for browsing.</summary>
public sealed class OptimizationPopulation
{
    public int MeasuredCombinations { get; init; }
    public int ProfitableCombinations { get; init; }
    public List<OptimizationParameterPopulation> Parameters { get; init; } = new();
    [JsonIgnore] public decimal? ProfitablePercent => MeasuredCombinations > 0 ? 100m * ProfitableCombinations / MeasuredCombinations : null;

    public static OptimizationPopulation Capture(IReadOnlyList<OptimizationRow> rows, IReadOnlyList<ParameterRange> ranges)
    {
        var parameters = new List<OptimizationParameterPopulation>();
        foreach (var range in ranges.Where(range => range.Enabled))
        {
            var groups = new Dictionary<string, OptimizationValuePopulation>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                string value = ValueText(row.Parameters.GetValueOrDefault(range.Name));
                if (!groups.TryGetValue(value, out var group)) groups[value] = group = new() { Value = value };
                group.Combinations++;
                if (row.Metrics.NetProfit > 0) group.ProfitableCombinations++;
                group.NetProfitSum += row.Metrics.NetProfit;
                group.SharpeSum += row.Metrics.SharpeRatio;
            }
            parameters.Add(new() { Name = range.Name, Values = groups.Values.ToList() });
        }
        return new()
        {
            MeasuredCombinations = rows.Count,
            ProfitableCombinations = rows.Count(row => row.Metrics.NetProfit > 0),
            Parameters = parameters,
        };
    }

    private static string ValueText(object? value) => value switch
    {
        JsonElement element => element.ToString(),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "",
    };
}

public sealed class OptimizationParameterPopulation
{
    public string Name { get; init; } = "";
    public List<OptimizationValuePopulation> Values { get; init; } = new();
}

public sealed class OptimizationValuePopulation
{
    public string Value { get; init; } = "";
    public int Combinations { get; set; }
    public int ProfitableCombinations { get; set; }
    public decimal NetProfitSum { get; set; }
    public double SharpeSum { get; set; }
    [JsonIgnore] public decimal? ProfitablePercent => Combinations > 0 ? 100m * ProfitableCombinations / Combinations : null;
    [JsonIgnore] public decimal? AverageNetProfit => Combinations > 0 ? NetProfitSum / Combinations : null;
    [JsonIgnore] public double? AverageSharpe => Combinations > 0 ? SharpeSum / Combinations : null;
}
