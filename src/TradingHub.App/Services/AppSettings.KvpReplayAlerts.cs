namespace TradingHub.App.Services;

public sealed partial class AppSettings
{
    public decimal ReplayStartBalance { get; set; } = 10_000m;
    public decimal ReplayFeePerUnit { get; set; }
    public decimal ReplayNotionalFeeRate { get; set; }
    public int ReplayRandomSeed { get; set; } = 1;
    public int ReplayLengthDays { get; set; } = 3;
}
