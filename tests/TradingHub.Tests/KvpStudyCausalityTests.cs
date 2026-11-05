using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public class KvpStudyCausalityTests
{
    private static readonly DateTime Start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly record struct Bar(decimal Open, decimal High, decimal Low, decimal Close, decimal Delta = 0, decimal Volume = 20);
    private static ChartModel Model(params Bar[] bars)
    {
        var instrument = new Instrument { Symbol = "CAUSAL-STUDY-FIXTURE", FeedId = "sim", TickSize = 1, VolumeStep = 1 };
        var data = new InstrumentData(instrument, new SimulatedFeed()); long id = 1;
        for (int i = 0; i < bars.Length; i++)
        {
            var bar = bars[i]; DateTime time = Start.AddMinutes(i);
            data.Trades.Add(new Trade(id++, time, bar.Close, (bar.Volume + bar.Delta) / 2, TradeSide.Buy));
            data.Trades.Add(new Trade(id++, time.AddSeconds(1), bar.Close, (bar.Volume - bar.Delta) / 2, TradeSide.Sell));
        }
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        for (int i = 0; i < bars.Length; i++)
        {
            model.Series[i].OpenTicks = (long)bars[i].Open; model.Series[i].HighTicks = (long)bars[i].High;
            model.Series[i].LowTicks = (long)bars[i].Low; model.Series[i].CloseTicks = (long)bars[i].Close;
        }
        return model;
    }
    private static T Add<T>(ChartModel model, T indicator) where T : Indicator { Assert.Null(model.AddIndicator(indicator, applyDefaultColors: false).Error); return indicator; }
    private static OptionalValueDataSeries Series(Indicator indicator, string id) => Assert.IsType<OptionalValueDataSeries>(indicator.DataSeries.Single(x => x.Id == id));
    [Fact]
    public void Swings_and_breaks_are_available_at_confirmation_not_at_the_retroactive_pivot()
    {
        using var model = Model(new(9, 10, 8, 9), new(14, 15, 13, 14), new(10, 11, 9, 10), new(15, 17, 14, 16));
        var study = Add(model, new MarketStructure { SwingRadius = 1 });
        var swing = Assert.Single(study.Events, e => e.Kind == StructureEventKind.SwingHigh);
        Assert.Equal(1, swing.SourceBar); Assert.Equal(2, swing.AvailableBar);
        Assert.DoesNotContain(study.Events, e => e.Kind == StructureEventKind.Bos);
        Assert.Equal(0m, Series(study, "Structure.Signal")[1]);
        model.Series[3].IsClosed = true; study.HostCalculate(3); study.HostCalculate(3);
        Assert.Equal(3, Assert.Single(study.Events, e => e.Kind == StructureEventKind.Bos).AvailableBar);
        Assert.Equal(1m, Series(study, "Structure.Trend")[3]);
        model.Series[3].IsClosed = false; study.HostCalculate(3);
        Assert.DoesNotContain(study.Events, e => e.Kind == StructureEventKind.Bos); Assert.Equal(0m, Series(study, "Structure.Trend")[3]);
    }
    [Fact]
    public void Fvg_requires_three_completed_candles_and_mitigation_is_not_a_forming_wick()
    {
        using var model = Model(new(8, 10, 7, 9), new(10, 12, 9, 11), new(13, 15, 12, 14), new(11, 14, 8, 11));
        var study = Add(model, new MarketStructure { SwingRadius = 1 });
        var gap = Assert.Single(study.Zones, z => z.Kind == StructureZoneKind.FairValueGap);
        Assert.Equal(10m, gap.Low); Assert.Equal(12m, gap.High); Assert.Equal(2, gap.AvailableBar); Assert.Equal(-1, gap.EndBar);
        model.Series[3].IsClosed = true; study.HostCalculate(3);
        Assert.Equal(3, Assert.Single(study.Zones, z => z.Kind == StructureZoneKind.FairValueGap).EndBar);
        model.Series[3].IsClosed = false; study.HostCalculate(3);
        Assert.Equal(-1, Assert.Single(study.Zones, z => z.Kind == StructureZoneKind.FairValueGap).EndBar);
    }
    [Fact]
    public void Cvd_divergence_is_closed_right_confirmed_and_latest_confirmation_can_be_retracted()
    {
        using var model = Model(new(10, 10, 10, 10, 5), new(15, 15, 15, 15, 5), new(11, 11, 11, 11, -4), new(12, 12, 12, 12, -3), new(18, 18, 18, 18, 2), new(14, 14, 14, 14, -1), new(13, 13, 13, 13));
        var study = Add(model, new CumulativeDeltaCandles { PivotRadius = 1 });
        var divergence = Assert.Single(study.Divergences); Assert.False(divergence.Bullish); Assert.Equal(4, divergence.PivotBar); Assert.Equal(5, divergence.AvailableBar);
        Assert.Equal(0m, Series(study, "CVD.Divergence")[4]); Assert.Equal(-1m, Series(study, "CVD.Divergence")[5]);
        model.Series[5].IsClosed = false; study.HostCalculate(5); Assert.Empty(study.Divergences); Assert.Equal(0m, Series(study, "CVD.Divergence")[5]);
    }
    [Fact]
    public void Po3_tspots_and_higher_candles_only_publish_after_the_aggregate_closes()
    {
        using var model = Model(new(10, 12, 9, 11), new(11, 12, 9, 10), new(10, 11, 8, 10), new(10, 11, 8, 10), new(10, 12, 9, 11), new(11, 12, 9, 11), new(11, 13, 10, 12));
        var study = Add(model, new HtfStructure { UseMappedPeriods = false, FirstPeriod = StudyTimeFrame.Minutes2, SecondPeriod = StudyTimeFrame.Minutes5 });
        var pattern = Assert.Single(study.Patterns); Assert.Equal(1, pattern.Direction); Assert.False(pattern.TwoSided);
        Assert.Equal(4, pattern.AvailableBar); Assert.Equal(8.5m, pattern.TSpot3Low); Assert.Equal(10m, pattern.TSpot3High);
        Assert.Equal(6, pattern.TSpot4AvailableBar); Assert.Equal(10.25m, pattern.TSpot4Low); Assert.Equal(11m, pattern.TSpot4High);
        Assert.Equal(0m, Series(study, "HtfStructure.PO3")[2]); Assert.Equal(1m, Series(study, "HtfStructure.PO3")[4]);
        Assert.Equal(3, study.FirstClosedCandles.Count); study.HostCalculate(6); study.HostCalculate(6); Assert.Single(study.Patterns); Assert.Equal(3, study.FirstClosedCandles.Count);
    }
    [Fact]
    public void Forming_flow_bubble_is_preview_only_and_replacement_does_not_create_confirmed_history()
    {
        using var model = Model(new(10, 10, 10, 10, 2), new(10, 10, 10, 10, 12));
        var study = Add(model, new DeltaBubbles { Trigger = DeltaBubbles.TriggerKind.FixedMagnitude, MinimumMagnitude = 10, ShowPreview = true });
        var preview = Assert.Single(study.Bubbles); Assert.True(preview.Preview); Assert.Equal(0m, Series(study, "DeltaBubbles.Signal")[1]);
        model.Series[1].IsClosed = true; study.HostCalculate(1); study.HostCalculate(1);
        var confirmed = Assert.Single(study.Bubbles); Assert.False(confirmed.Preview); Assert.Equal(1, confirmed.AvailableBar); Assert.Equal(12m, Series(study, "DeltaBubbles.Signal")[1]);
    }
    [Fact]
    public void Sequential_correction_replay_matches_a_fresh_structure_consumer()
    {
        using var model = Model(Enumerable.Range(0, 80).Select(i => new Bar(i % 9, i % 9 + 2, i % 9 - 2, i % 9 + 1)).ToArray());
        var original = Add(model, new MarketStructure { SwingRadius = 2 });
        model.Series[10].HighTicks = 100; model.Series[10].CloseTicks = 90; original.HostCalculate(10);
        var replay = Add(model, new MarketStructure { SwingRadius = 2 });
        Assert.Equal(replay.Events, original.Events); Assert.Equal(replay.Zones, original.Zones);
        for (int b = 0; b < model.Series.Count; b++) Assert.Equal(Series(replay, "Structure.Trend")[b], Series(original, "Structure.Trend")[b]);
    }
    [Fact]
    public void Real_trade_store_close_transitions_match_every_replayed_prefix_and_repeated_forming_update()
    {
        var instrument = new Instrument { Symbol = "REAL-CLOSE-PREFIX", FeedId = "sim", TickSize = 1, VolumeStep = 1 };
        var data = new InstrumentData(instrument, new SimulatedFeed());
        using var live = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var structure = Add(live, new MarketStructure { SwingRadius = 1 });
        var cvd = Add(live, new CumulativeDeltaCandles { PivotRadius = 1 });
        var flow = Add(live, new AnchoredFlowVwap { Mode = AnchoredFlowVwap.AnchorMode.Swing, SwingRadius = 1, Weight = AnchoredFlowVwap.FlowWeight.Volume });
        decimal[] prices = { 10, 15, 11, 12, 18, 14, 13 }, deltas = { 5, 5, -4, -3, 2, -1, 1 };
        var prefix = new List<Trade>(); long id = 1;
        for (int b = 0; b < prices.Length; b++)
        {
            foreach (var trade in new[]
            {
                new Trade(id++, Start.AddMinutes(b), prices[b], (20 + deltas[b]) / 2, TradeSide.Buy),
                new Trade(id++, Start.AddMinutes(b).AddSeconds(1), prices[b], (20 - deltas[b]) / 2, TradeSide.Sell)
            })
            {
                prefix.Add(trade); data.Trades.Add(trade); live.Update(); live.Update();
                var replayData = new InstrumentData(instrument, new SimulatedFeed()); foreach (var item in prefix) replayData.Trades.Add(item);
                using var replay = new ChartModel(replayData, ChartPeriod.M1, new ChartSettings());
                var replayStructure = Add(replay, new MarketStructure { SwingRadius = 1 });
                var replayCvd = Add(replay, new CumulativeDeltaCandles { PivotRadius = 1 });
                var replayFlow = Add(replay, new AnchoredFlowVwap { Mode = AnchoredFlowVwap.AnchorMode.Swing, SwingRadius = 1, Weight = AnchoredFlowVwap.FlowWeight.Volume });
                Assert.Equal(replayStructure.Events, structure.Events); Assert.Equal(replayStructure.Zones, structure.Zones);
                Assert.Equal(replayCvd.Divergences, cvd.Divergences);
                for (int i = 0; i < live.Series.Count; i++)
                {
                    Assert.Equal(Series(replayStructure, "Structure.Signal")[i], Series(structure, "Structure.Signal")[i]);
                    Assert.Equal(Series(replayCvd, "CVD.Divergence")[i], Series(cvd, "CVD.Divergence")[i]);
                    Assert.Equal(Series(replayCvd, "Value")[i], Series(cvd, "Value")[i]);
                    Assert.Equal(Series(replayFlow, "AnchoredFlowVwap.Value")[i], Series(flow, "AnchoredFlowVwap.Value")[i]);
                    Assert.Equal(Series(replayFlow, "AnchoredFlowVwap.Value").IsAvailable(i), Series(flow, "AnchoredFlowVwap.Value").IsAvailable(i));
                }
            }
        }
    }
    [Fact]
    public void Cisds_and_PO3_projection_prices_are_available_only_after_both_confirmations()
    {
        using var model = Model(new(10, 12, 9, 11), new(11, 12, 9, 10), new(10, 11, 8, 9), new(9, 11, 8, 10), new(10, 13, 9, 12), new(12, 13, 10, 12), new(12, 14, 10, 13));
        var study = Add(model, new HtfStructure { UseMappedPeriods = false, FirstPeriod = StudyTimeFrame.Minutes2, SecondPeriod = StudyTimeFrame.Minutes5 });
        var pattern = Assert.Single(study.Patterns); Assert.Equal(4, pattern.ProjectionAvailableBar); Assert.Equal(11m, pattern.CisdLevel);
        Assert.Null(study.ProjectionAt(0, 2, 3)); Assert.Equal(17m, study.ProjectionAt(0, 2, 4)); Assert.Equal(18.5m, study.ProjectionAt(0, 2.5m, 4));
        Assert.Equal(23m, study.ProjectionAt(0, 4, 4)); Assert.Equal(24.5m, study.ProjectionAt(0, 4.5m, 4));
    }
    [Fact]
    public void Historical_activity_filters_use_confirmation_availability_not_the_future_final_state()
    {
        var zone = new StructureZone(StructureZoneKind.FairValueGap, 2, 4, 1, 10, 12, 7, false, 8);
        Assert.False(zone.IsActiveAt(3)); Assert.True(zone.IsActiveAt(4)); Assert.True(zone.IsActiveAt(7)); Assert.False(zone.IsActiveAt(8));
        var gap = new HtfStructure.Gap(StudyTimeFrame.Hour, 1, 2, 4, 10, 12, 7, 8);
        Assert.True(gap.IsActiveAt(7)); Assert.False(gap.IsActiveAt(8));
        var level = new NakedPoc.Level(StudyTimeFrame.Day, 10, 0, 2, 3, 7, 8, StudyCoverage.Observed);
        Assert.True(level.IsUntouchedAt(7)); Assert.False(level.IsUntouchedAt(8));
        var pattern = new DoubleInsideDay.Pattern(0, 3, 20, 0, 0, 7, 1, 8);
        Assert.Equal(0, pattern.StateAt(7)); Assert.Equal(1, pattern.StateAt(8));
    }
    [Fact]
    public void Unavailable_lower_bar_stops_projection_without_fabricating_a_price_invalidation()
    {
        using var model = Model(new(10, 12, 9, 11), new(11, 12, 9, 10), new(10, 11, 8, 9), new(9, 11, 8, 10), new(10, 13, 9, 12), new(12, 13, 10, 12), new(12, 14, 10, 13));
        var candle = model.Series[5]; candle.Trades = 0; candle.Volume = candle.Bid = candle.Ask = candle.Between = 0;
        candle.AddTrade(1, 1, 100, TradeSide.Buy, candle.Time.Ticks, 10, provenance: new TradeProvenance(DataQuality.Compact, 900), resolutionCompatible: false);
        var study = Add(model, new HtfStructure { UseMappedPeriods = false, FirstPeriod = StudyTimeFrame.Minutes2, SecondPeriod = StudyTimeFrame.Minutes5 });
        var pattern = Assert.Single(study.Patterns);
        Assert.Equal(5, pattern.CoverageGapAvailableBar); Assert.Equal(-1, pattern.InvalidatedBar);
        Assert.Equal(17m, study.ProjectionAt(0, 2, 4)); Assert.Null(study.ProjectionAt(0, 2, 5));
    }
}
