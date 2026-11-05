namespace TradingHub.App.Services;

public sealed partial class AppSettings
{
    public bool RecordDepth { get; set; }
    public int DepthRetentionDays { get; set; } = 14;
    public int DepthMaxDiskMegabytes { get; set; } = 256;
}
