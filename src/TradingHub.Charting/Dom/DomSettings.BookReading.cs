// Behaviour adapted from gouqizi2214730-source/trading-hub 6554d5e (MIT with Commons Clause; see THIRD_PARTY_NOTICES.md);
// unlike the source, every feature is opt-in and the DOM keeps trading behind its own switch.
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace TradingHub.Charting.Dom;

/// <summary>How DOM book and traded sizes are shown.</summary>
public enum DomUnit
{
    [Display(Name = "Quantity (contracts / coins)")] Quantity,
    [Display(Name = "Notional (quantity × price × point value)")] Notional,
}

/// <summary>Optional book-reading features. Every default keeps the existing DOM look.</summary>
public sealed partial class DomSettings
{
    [Display(Name = "Automatic row grouping (auto tick)", GroupName = "Book reading", Order = 10,
        Description = "Chooses the ticks per row so the visible rows span the range below, never wider than the received book.")]
    public bool AutoStep { get; set; }

    [Display(Name = "Auto tick: range on each side of the price (%)", GroupName = "Book reading", Order = 11)]
    [Range(0.02, 20)]
    public double AutoRangePercent { get; set; } = 0.5;

    [Display(Name = "Size unit", GroupName = "Book reading", Order = 12,
        Description = "Notional multiplies size by price and the instrument point value, in the instrument's quote currency.")]
    public DomUnit Unit { get; set; } = DomUnit.Quantity;

    [Display(Name = "Book changes and recent trades window (seconds)", GroupName = "Book reading", Order = 13,
        Description = "Book changes compare recorded received snapshots in this window (depth recording must be on); recent trades sum observed prints.")]
    [Range(5, 3600)]
    public int ChangeWindowSeconds { get; set; } = 60;

    [Display(Name = "Distance marks from the price (%)", GroupName = "Book reading", Order = 14,
        Description = "Comma-separated percentages marked on both sides of the mid price (empty = none).")]
    public string DistanceMarks { get; set; } = "";

    [Display(Name = "Stats bar (spread, liquidity within 0.5 / 2 / 10 %)", GroupName = "Book reading", Order = 15)]
    public bool ShowStats { get; set; }

    [Display(Name = "Compare with the other side on hover (mirror)", GroupName = "Book reading", Order = 16)]
    public bool HoverMirror { get; set; }

    [Display(Name = "Highlight the largest visible sizes automatically", GroupName = "Book reading", Order = 17,
        Description = "When the fixed highlight size is 0: sizes ≥ 85 % of the largest visible level and ≥ 3 × the median are outlined.")]
    public bool AutoLargeSize { get; set; }

    /// <summary>Parsed distance marks in percent, ascending, without duplicates or out-of-range values.</summary>
    public double[] Marks() => (DistanceMarks ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => double.TryParse(x.Replace('%', ' ').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0)
        .Where(v => v > 0 && v < 100).Distinct().OrderBy(v => v).ToArray();
}
