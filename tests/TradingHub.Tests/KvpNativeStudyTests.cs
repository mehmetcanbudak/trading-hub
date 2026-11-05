using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public class KvpNativeStudyTests
{
    private static readonly DateTime Start = new(2026, 1, 31, 23, 58, 0, DateTimeKind.Utc);
    private static ChartModel Model(params (DateTime Time, decimal Price, decimal Volume, TradeSide Side)[] trades)
    {
        var instrument = new Instrument { Symbol = "KVP-NATIVE-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 };
        var data = new InstrumentData(instrument, new SimulatedFeed());
        long id = 1;
        foreach (var t in trades) data.Trades.Add(new Trade(id++, t.Time, t.Price, t.Volume, t.Side));
        return new ChartModel(data, ChartPeriod.M1, new ChartSettings());
    }
    private static T Add<T>(ChartModel model, T study) where T : Indicator
    {
        Assert.Null(model.AddIndicator(study, applyDefaultColors: false).Error);
        return study;
    }
    private static OptionalValueDataSeries Series(Indicator study, string id) => Assert.IsType<OptionalValueDataSeries>(study.DataSeries.Single(s => s.Id == id));
    [Fact]
    public void Weighted_moments_keep_small_spread_at_large_price_and_merge_independently()
    {
        var a = new WeightedStudyMoments(); a.Add(1000000000000m, 1); a.Add(1000000000002m, 1);
        Assert.Equal(1000000000001m, a.Mean); Assert.Equal(1m, a.Deviation);
        var b = new WeightedStudyMoments(); b.Add(1000000000004m, 2); a.Merge(b);
        Assert.Equal(1000000000002.5m, a.Mean); Assert.InRange(a.Deviation, 1.65831239517769m, 1.65831239517771m);
    }
    [Fact]
    public void Simultaneous_vwap_resets_on_actual_calendar_and_replaces_forming_contribution()
    {
        using var model = Model((Start, 10, 1, TradeSide.Buy), (Start.AddMinutes(1), 20, 3, TradeSide.Sell), (Start.AddMinutes(2), 100, 2, TradeSide.Buy));
        var study = Add(model, new MultiPeriodVwap());
        Assert.Equal(17.5m, Series(study, "MultiVwap.Month")[1]);
        Assert.Equal(100m, Series(study, "MultiVwap.Month")[2]);
        Assert.InRange(Math.Abs(45m - Series(study, "MultiVwap.Year")[2]), 0, 0.000000000000000000000001m);
        model.Series[2].Levels.Clear(); model.Series[2].Volume = model.Series[2].Bid = model.Series[2].Ask = model.Series[2].Between = 0;
        model.Series[2].AddTrade(30, 30, 2, TradeSide.Buy, model.Series[2].Time.Ticks, 0);
        study.HostCalculate(2); study.HostCalculate(2);
        Assert.Equal(30m, Series(study, "MultiVwap.Month")[2]); Assert.InRange(Math.Abs(130m / 6 - Series(study, "MultiVwap.Year")[2]), 0, 0.000000000000000000000001m);
    }
    [Fact]
    public void Cvd_warmup_and_repeated_current_bar_do_not_double_count_or_seed_missing_flow()
    {
        using var model = Model((Start, 10, 2, TradeSide.Buy), (Start.AddMinutes(1), 11, 1, TradeSide.Sell), (Start.AddMinutes(2), 12, 5, TradeSide.Buy));
        var study = Add(model, new CumulativeDeltaCandles { SmoothingPeriod = 2 });
        Assert.Equal(6m, Series(study, "Value")[2]);
        study.HostCalculate(2); study.HostCalculate(2); Assert.Equal(6m, Series(study, "Value")[2]);
        Assert.False(Series(study, "CVD.Smoothed").IsAvailable(0)); Assert.Equal(3.5m, Series(study, "CVD.Smoothed")[2]);
    }
    [Fact]
    public void Triple_bollinger_has_real_warmup_and_uses_only_completed_higher_candles()
    {
        using var model = Model(Enumerable.Range(0, 7).Select(i => (Start.Date.AddMinutes(i), (decimal)(i + 1), 1m, TradeSide.Buy)).ToArray());
        var study = Add(model, new TripleBollinger { Length = 2, UseMappedPeriods = false, FirstPeriod = StudyTimeFrame.Minutes2, SecondPeriod = StudyTimeFrame.Minutes5, ThirdPeriod = StudyTimeFrame.Minutes15 });
        var first = Series(study, "TripleBB.0.Middle");
        Assert.False(first.IsAvailable(2)); Assert.Equal(3m, first[4]); Assert.Equal(3m, first[5]); Assert.Equal(5m, first[6]);
    }
}
