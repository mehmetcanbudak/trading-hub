using TradingHub.Feeds;
using TradingHub.Feeds.Venues;
using TradingHub.Indicators;

namespace TradingHub.Charting;

public sealed partial class ChartModel
{
    private void UpdateSourceInfo()
    {
        _instrumentInfo.BaseAsset = Instrument.BaseAsset;
        _instrumentInfo.QuoteAsset = Instrument.QuoteAsset;
        var feeds = Data.Feed is AggregateFeed aggregate ? aggregate.Sources : new[] { Data.Feed };
        _instrumentInfo.Sources = feeds.Select(f => new InstrumentSource(f.Id, f.DisplayName,
            f is ICryptoVenue venue ? venue.Venue : f.DisplayName, f is ICryptoVenue { IsSpot: true })).ToArray();
    }

    /// <summary>
    /// Processed trades of a bar with provenance. When a non-time bar boundary splits one event, each bar receives only
    /// the volume applied to it, so per-source sums reconcile with the bar's own totals.
    /// </summary>
    public void CopyBarTrades(int bar, List<BarTrade> destination)
    {
        destination.Clear();
        if (bar < 0 || bar >= Series.Count) return;
        var candle = Series[bar];
        var store = Data.Trades;
        int first = candle.FirstTradeIndex, last = Math.Min(candle.LastTradeIndex, Math.Min(Series.ProcessedTrades, store.Count) - 1);
        if (first < 0 || last < first) return;
        // An event shared with earlier bars (one large print can fill several volume bars): this bar has what remained
        // after every earlier part, each bar's part of it being its LastTradeVolume.
        decimal earlierParts = 0;
        for (int j = bar - 1; j >= 0 && Series[j].LastTradeIndex == first; j--) earlierParts += Series[j].LastTradeVolume;
        for (int i = first; i <= last; i++)
        {
            decimal volume = store.GetVolume(i);
            if (i == candle.LastTradeIndex) volume = candle.LastTradeVolume;
            else if (i == first) volume -= earlierParts;
            if (volume <= 0) continue;
            destination.Add(new BarTrade(store.GetTime(i), store.GetPrice(i), volume, store.GetSide(i), store.GetProvenance(i)));
        }
    }
}
