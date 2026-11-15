namespace TradingHub.App.Services;

public sealed partial class AppSettings
{
    /// <summary>Workbench page choices: selected panel, tape thresholds, panel ranges and calculator inputs.</summary>
    public WorkbenchSettings Workbench { get; set; } = new();
}

/// <summary>Persisted Workbench choices (additive settings; defaults keep sounds and estimates conservative).</summary>
public sealed class WorkbenchSettings
{
    public string Panel { get; set; } = "tape";
    public bool FollowActiveChart { get; set; } = true;
    public decimal TapeMinNotional { get; set; } = 1_000;
    public decimal TapeLargeNotional { get; set; } = 100_000;
    public decimal TapeHugeNotional { get; set; } = 1_000_000;
    public decimal TapeMinLiquidationNotional { get; set; }
    public bool TapeMergeSameMillisecond { get; set; } = true;
    public int TapeBucketSeconds { get; set; } = 10;
    public bool TapeSoundLarge { get; set; }
    public bool TapeSoundLiquidations { get; set; }
    public List<string> TapeHiddenVenues { get; set; } = new();
    public int HeatmapMinutes { get; set; } = 15;
    public int HeatmapGrouping { get; set; } = 1;
    public bool HeatmapTrades { get; set; } = true;
    public decimal LiquidityRangePercent { get; set; } = 12;
    public decimal LiquidityMaintenancePercent { get; set; } = 0.5m;
    public int DerivativesHours { get; set; } = 24;
    public double OptionsRangePercent { get; set; } = 20;
    public int? OptionsMaxDays { get; set; }
    public decimal OptionsLargeTradePremium { get; set; } = 100_000;
    public decimal ToolBalance { get; set; } = 10_000;
    public decimal ToolRisk { get; set; } = 1;
    public bool ToolRiskIsAmount { get; set; }
    public decimal ToolFeePercent { get; set; } = 0.05m;
    public decimal ToolLeverage { get; set; } = 10;
    public decimal ToolMaintenancePercent { get; set; } = 0.5m;
    public decimal ToolFundingNotional { get; set; } = 10_000;
    public decimal ToolFundingRatePercent { get; set; } = 0.01m;
    public decimal ToolFundingIntervalHours { get; set; } = 8;
    public decimal ToolFundingHoldHours { get; set; } = 24;
}

