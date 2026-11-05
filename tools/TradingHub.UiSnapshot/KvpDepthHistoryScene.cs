using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Localization;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

internal static class KvpDepthHistoryScene
{
    private static readonly DateTime Start = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);
    private const decimal ExactSize = 0.1234567890123456789012345678m;

    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        var evidence = new Evidence(owner, pump, Path.GetFullPath(outDir));
        bool previousRecording = App.Services.Settings.RecordDepth;
        try
        {
            evidence.Section("recording permission", () =>
            {
                evidence.Check(!previousRecording, "fresh isolated composition has not granted depth recording permission", false, previousRecording);
                SetRecording(evidence, false);
            });
            evidence.Section("legacy archive and compact consumers", () => History(evidence));
            evidence.Section("mixed quality consumers", () => MixedQuality(evidence));
            evidence.Section("distribution correction and provider caches", () => Corrections(evidence));
            evidence.Section("received depth and selected historical heatmap", () => RecordedDepth(evidence));
            evidence.Section("retention and visible recording failures", () => RecordingLimits(evidence));
            evidence.Section("sparse signed price consumers", () => SparsePrices(evidence));
        }
        finally
        {
            evidence.Section("restore isolated recording preference", () => SetRecording(evidence, previousRecording));
            evidence.Save();
        }
        return evidence.Failures;
    }

    private static Instrument Instrument(string purpose, string symbol, decimal tick = 1) => new()
    {
        FeedId = "kvp-depth-history-" + purpose + "-" + Guid.NewGuid().ToString("N"),
        Symbol = symbol,
        Description = "Deterministic isolated " + purpose + " fixture",
        Exchange = "fixture-venue-a",
        TickSize = tick,
        VolumeStep = 0.00000001m,
        PointValue = 1,
    };

    private static CompositeVolumeProfile AddProfile(Evidence e, KvpDepthHistoryFixture fixture, string? cache = null)
    {
        var profile = new CompositeVolumeProfile
        {
            PriceGrouping = 1, ComposeProfiles = false, ShowBuyerSellerPoc = true,
            ShowOuterValueArea = true, CompletedCacheDirectory = cache ?? "",
        };
        var slot = fixture.Model.AddIndicator(profile, applyDefaultColors: false);
        fixture.Pump();
        e.Check(slot.Error == null, "actual Composite Volume Profile calculates without a slot error", null, slot.Error);
        return profile;
    }

    private static void History(Evidence e)
    {
        string root = e.PathFor("legacy-ticks"), cache = e.PathFor("compact-cache");
        var source = Instrument("legacy", "LEGACY");
        var day = DateOnly.FromDateTime(Start);
        Trade[] raw =
        [
            new(1, Start, 100, 2, TradeSide.Buy), new(2, Start.AddSeconds(1), 101, 3, TradeSide.Sell),
            new(3, Start.AddSeconds(2), 100, 5, TradeSide.Buy), new(4, Start.AddSeconds(3), 100, 7, TradeSide.Buy),
            new(5, Start.AddSeconds(4), 101, 1, TradeSide.Sell), new(6, Start.AddSeconds(5), 100, 2, TradeSide.Buy),
        ];
        string rawPath = TickFile.DayPath(root, source.FeedId, source.Symbol, day);
        using (var writer = new TickFile.Writer(rawPath, source, includeProvenance: false))
            foreach (var trade in raw) writer.Write(trade);
        byte[] original = File.ReadAllBytes(rawPath);
        e.Remember("legacySource", new { rawPath, sha256 = Hash(original), bytes = original.Length, events = 6, volume = 20 });
        var read = TickFile.Read(rawPath, out int dropped, requireComplete: true);
        e.Check(dropped == 0 && SameEvents(raw, read), "legacy VTK1 consumer reads every original ID/time/price/quantity/side", "six exact events; no dropped records", new { dropped, events = read.Count });
        using (var chart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("legacy-depth"), source, read, Start.AddMinutes(5), ChartPeriod.M5))
        {
            var candle = chart.Model.GetCandle(0);
            var profile = AddProfile(e, chart);
            e.Check(chart.Model.Series.Count == 1 && candle.Volume == 20 && candle.HasObservedFootprint && !candle.HasAggregatedFootprint,
                "actual raw chart candle retains observed footprint and conserved volume", "one observed candle, volume 20", CandleSummary(candle));
            e.Check(candle.MaxTradeVolume == 7 && profile.Profiles.Single().Poc == 100 && profile.Profiles.Single().Total == 20,
                "raw study consumes independently worked 100:16 and 101:4 distribution", "POC 100; total 20; maximum individual print 7", ProfileSummary(profile));
            e.Capture("kvp_history_legacy_raw.png");
        }

        foreach (int minutes in CompactHistory.Granularities)
        {
            var compact = CompactHistory.LoadDay(root, cache, source, day, minutes);
            var cached = CompactHistory.LoadDay(root, cache, source, day, minutes);
            e.Check(File.ReadAllBytes(rawPath).AsSpan().SequenceEqual(original), $"{minutes}-minute compact cache leaves legacy raw bytes unchanged", Hash(original), Hash(File.ReadAllBytes(rawPath)));
            e.Check(SameEvents(compact, cached) && compact.Sum(trade => trade.Volume) == 20 && compact.Sum(trade => trade.Provenance.ObservedTradeCount) == 6,
                $"{minutes}-minute persisted compact cache conserves volume and underlying event count", "20 volume; six source events", new { rows = compact.Count, volume = compact.Sum(trade => trade.Volume), count = compact.Sum(trade => trade.Provenance.ObservedTradeCount) });
            var display = Instrument("compact" + minutes, "LEGACY");
            using var chart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("compact-depth-" + minutes), display,
                cached, Start.AddMinutes(minutes), ChartPeriod.Parse("M" + minutes));
            var candle = chart.Model.GetCandle(0);
            var profile = AddProfile(e, chart);
            var big = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 1 };
            var bigSlot = chart.Model.AddIndicator(big, applyDefaultColors: false);
            chart.Pump();
            var prices = candle.GetAggregatedPriceLevels().GroupBy(row => row.Price).ToDictionary(group => group.Key, group => group.Sum(row => row.Volume));
            e.Check(!candle.HasObservedFootprint && candle.HasAggregatedFootprint && candle.HasCompactTrades && !candle.IsSynthetic && candle.MaxTradeVolume == 0,
                $"{minutes}-minute cached summaries never become observed footprint or individual prints", "compact aggregate only; maximum print 0", CandleSummary(candle));
            e.Check(candle.ResolutionSeconds == minutes * 60 && prices.Count == 2 && prices.GetValueOrDefault(100) == 16 && prices.GetValueOrDefault(101) == 4,
                $"{minutes}-minute chart/SDK retains resolution and exact measured price rows", new { resolutionSeconds = minutes * 60, at100 = 16, at101 = 4 }, new { candle.ResolutionSeconds, prices });
            var result = profile.Profiles.Single();
            e.Check(result.Total == 20 && result.Poc == 100 && result.Coverage.HasFlag(StudyCoverage.Compact) && !result.Coverage.HasFlag(StudyCoverage.Observed) && !result.Coverage.HasFlag(StudyCoverage.Estimated),
                $"{minutes}-minute actual profile labels measured aggregates, not observed or OHLC estimates", "total 20; POC 100; Compact only", ProfileSummary(profile));
            e.Check(bigSlot.Error == null && big.BubbleCount == 0, $"{minutes}-minute aggregate buckets do not create any Big Trades at threshold one", 0, new { big.BubbleCount, bigSlot.Error });
            e.Capture("kvp_history_compact_" + minutes + ".png");
        }

        string archive = e.Await(Task.Run(() => TickArchive.ArchiveDay(root, source, day)), "lossless archive publishes after owned disk work drains");
        e.Check(File.Exists(archive) && File.ReadAllBytes(rawPath).AsSpan().SequenceEqual(original), "lossless archival retains the byte-identical authoritative legacy file", Hash(original), Hash(File.ReadAllBytes(rawPath)));
        string archiveOnlyRoot = e.PathFor("archive-only");
        string archiveOnly = TickArchive.PathFor(TickFile.DayPath(archiveOnlyRoot, source.FeedId, source.Symbol, day));
        Directory.CreateDirectory(Path.GetDirectoryName(archiveOnly)!);
        File.Copy(archive, archiveOnly);
        var archiveRead = HistorySources.ReadDay(archiveOnlyRoot, source.FeedId, source.Symbol, day);
        e.Check(!File.Exists(TickFile.DayPath(archiveOnlyRoot, source.FeedId, source.Symbol, day)) && SameEvents(raw, archiveRead) && archiveRead.All(trade => trade.IsObservedTrade),
            "archive-only historical consumer recovers all raw events without relying on the original file", "six original observed events", archiveRead.Select(TradeSummary).ToArray());
        var archiveDisplay = Instrument("archive-display", "LEGACY");
        using var archivedChart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("archive-depth"), archiveDisplay, archiveRead, Start.AddMinutes(5), ChartPeriod.M5);
        var archiveProfile = AddProfile(e, archivedChart);
        e.Check(archivedChart.Model.GetCandle(0).MaxTradeVolume == 7 && archiveProfile.Profiles.Single().Total == 20 && archiveProfile.Profiles.Single().Poc == 100,
            "archive-only reloaded source reaches the actual chart and profile with the original numerical result", "maximum print 7; total 20; POC 100", ProfileSummary(archiveProfile));
        e.Check(File.ReadAllBytes(rawPath).AsSpan().SequenceEqual(original), "raw source remains byte-identical after archive-only chart load", Hash(original), Hash(File.ReadAllBytes(rawPath)));
        e.Capture("kvp_history_archive_reload.png");
    }

    private static void MixedQuality(Evidence e)
    {
        var instrument = Instrument("quality", "QUALITY");
        string root = e.PathFor("quality-ticks");
        var synthesized = new List<Trade>();
        DateTime syntheticAt = Start.AddMinutes(2);
        KlineHistory.Synthesize([new Kline(new DateTimeOffset(syntheticAt).ToUnixTimeMilliseconds(), 98, 104, 97, 103, 80, 0, false)],
            instrument, 1, [(syntheticAt, syntheticAt.AddMinutes(1))], synthesized);
        var trades = new List<Trade>
        {
            new(1, Start, 100, 20, TradeSide.Buy, new(DataQuality.Raw, SourceIdentity: instrument.Key)),
            new(2, Start.AddMinutes(1), 101, 100, TradeSide.Sell, new(DataQuality.Compact, 60, instrument.Key, 4, 50)),
        };
        trades.AddRange(synthesized);
        trades.Add(new Trade(4, Start.AddMinutes(3), 102, 200, TradeSide.Buy, new(DataQuality.Raw | DataQuality.InferredSide, SourceIdentity: instrument.Key)));
        HistoricalData.WriteDay(root, instrument, DateOnly.FromDateTime(Start), trades);
        var loaded = HistorySources.ReadDay(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start));
        using var chart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("quality-depth"), instrument, loaded, Start.AddMinutes(4), ChartPeriod.M1);
        e.Check(chart.Model.Series.Count == 4, "persisted raw/compact/OHLC/inferred input creates four actual chart bars", 4, chart.Model.Series.Count);
        var raw = chart.Model.GetCandle(0); var compact = chart.Model.GetCandle(1);
        var synthetic = chart.Model.GetCandle(2); var inferred = chart.Model.GetCandle(3);
        e.Check(raw.HasObservedFootprint && !raw.HasAggregatedFootprint && raw.Volume == 20 && raw.MaxTradeVolume == 20,
            "raw quality remains a real measured print through storage and chart aggregation", "observed 20 print", CandleSummary(raw));
        e.Check(compact.HasAggregatedFootprint && !compact.HasObservedFootprint && compact.ResolutionSeconds == 60 && compact.AggregatedTrades == 50 && compact.MaxTradeVolume == 0,
            "positive-ID measured packet remains compact with its source count and resolution", "60 seconds; 50 source events; no individual print", CandleSummary(compact));
        e.Check(synthetic.IsSynthetic && synthetic.Volume == 80 && !synthetic.HasObservedFootprint && !synthetic.HasAggregatedFootprint && synthetic.MaxTradeVolume == 0,
            "actual synthesized OHLC history never creates observed or aggregated footprint", "80 synthetic volume; no footprint or individual print", CandleSummary(synthetic));
        e.Check(inferred.HasInferredSide && inferred.Quality.HasFlag(DataQuality.Raw) && !inferred.Quality.HasFlag(DataQuality.Compact),
            "a measured price with inferred aggressor keeps that uncertainty instead of becoming measured-side flow", "Raw + InferredSide", CandleSummary(inferred));
        var profile = AddProfile(e, chart);
        var measured = profile.Profiles.Single();
        e.Check(measured.Total == 320 && measured.Coverage.HasFlag(StudyCoverage.Observed) && measured.Coverage.HasFlag(StudyCoverage.Compact) && measured.Coverage.HasFlag(StudyCoverage.InferredSide),
            "mixed actual profile excludes 80 synthetic volume and visibly retains raw/compact/inferred coverage", "320 measured volume; Observed + Compact + InferredSide", ProfileSummary(profile));
        var big = new BigTrades { Mode = BigTrades.ThresholdMode.Fixed, MinVolume = 10 };
        var bigSlot = chart.Model.AddIndicator(big, applyDefaultColors: false);
        var cvd = new CumulativeDeltaCandles();
        var cvdSlot = chart.Model.AddIndicator(cvd, applyDefaultColors: false);
        chart.Pump();
        var flow = (OptionalValueDataSeries)cvd.DataSeries.Single(series => series.Id == "Value");
        e.Check(bigSlot.Error == null && big.BubbleCount == 1,
            "Big Trades renders only the actual 20-lot raw print, not 100 compact, 80 OHLC or 200 inferred-side lots", 1, new { big.BubbleCount, bigSlot.Error });
        e.Check(cvdSlot.Error == null && flow.IsAvailable(0) && flow[0] == 20 && flow.IsAvailable(1) && flow[1] == -80 && !flow.IsAvailable(2) && !flow.IsAvailable(3),
            "actual flow study accepts resolution-compatible measured compact flow but exposes missing OHLC/inferred-side values", "+20, -80, missing, missing", new { raw = flow[0], compact = flow[1], syntheticAvailable = flow.IsAvailable(2), inferredAvailable = flow.IsAvailable(3), cvdSlot.Error, cvd.Status });
        e.Remember("qualityConsumers", new { raw = CandleSummary(raw), compact = CandleSummary(compact), synthetic = CandleSummary(synthetic), inferred = CandleSummary(inferred), profile = ProfileSummary(profile), big.Status, flowStatus = cvd.Status });
        e.Capture("kvp_history_mixed_quality.png");

        var onlySynthetic = Instrument("synthetic", "OHLC-ONLY");
        using var estimateChart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("synthetic-depth"), onlySynthetic, synthesized, Start.AddMinutes(3), ChartPeriod.M1);
        var estimate = AddProfile(e, estimateChart);
        e.Check(estimate.Profiles.Single().Total == 0 && !estimate.Profiles.Single().Coverage.HasFlag(StudyCoverage.Observed),
            "synthetic-only actual profile defaults to missing rather than invented observed footprint", 0, ProfileSummary(estimate));
        using (var settings = new StudySettings(e, estimateChart, estimate)) settings.Toggle(nameof(CompositeVolumeProfile.AllowEstimated), true);
        estimateChart.Pump();
        var estimated = estimate.Profiles.Single();
        e.Check(estimated.Total == 80 && estimated.Coverage.HasFlag(StudyCoverage.Estimated) && !estimated.Coverage.HasFlag(StudyCoverage.Observed) && !estimated.HasBuyerPoc && !estimated.HasSellerPoc,
            "explicit actual settings opt-in displays conserved OHLC estimates without invented side POCs", "80 estimated volume; no observed or buyer/seller POC", ProfileSummary(estimate));
        e.Capture("kvp_history_explicit_ohlc_estimate.png");
    }

    private static void Corrections(Evidence e)
    {
        string root = e.PathFor("correction-ticks"), compactRoot = e.PathFor("correction-compact"), profileRoot = e.PathFor("completed-profiles");
        DateTime at = Start.Date;
        var first = Instrument("provider-a", "SHARED-SYMBOL");
        Trade[] before = [new(1, at, 10, 5, TradeSide.Buy), new(2, at.AddSeconds(1), 20, 5, TradeSide.Buy)];
        Trade[] corrected = [new(1, at, 30, 5, TradeSide.Buy), before[1]];
        var next = new Trade(3, at.AddDays(1), 50, 1, TradeSide.Buy);
        var day = DateOnly.FromDateTime(at);
        HistoricalData.WriteDay(root, first, day, before, replace: true);
        HistoricalData.WriteDay(root, first, day.AddDays(1), [next], replace: true);
        string path = TickFile.DayPath(root, first.FeedId, first.Symbol, day);
        long originalLength = new FileInfo(path).Length;
        DateTime originalTime = File.GetLastWriteTimeUtc(path);
        var initialCompact = CompactHistory.LoadDay(root, compactRoot, first, day, 1);
        var persisted = HistoricalData.Read(root, first.FeedId, first.Symbol, at, at.AddDays(2)).ToArray();
        using var chart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("correction-depth"), first, persisted, at.AddDays(1).AddMinutes(1), ChartPeriod.M1);
        var profile = AddProfile(e, chart, profileRoot);
        e.Check(profile.Profiles.Count == 2 && profile.Profiles[0].Poc == 10 && profile.Profiles[0].Total == 10,
            "completed provider-A profile starts with independently worked lower-price tie POC 10", "completed POC 10; total 10", ProfileSummary(profile));
        e.Check(Directory.Exists(profileRoot) && Directory.EnumerateFiles(profileRoot, "*.studycache").Any(),
            "actual completed-profile consumer publishes an opt-in persisted result", "at least one completed result", Directory.Exists(profileRoot) ? Directory.EnumerateFiles(profileRoot, "*.studycache").Count() : 0);
        long generation = chart.Model.DataGeneration;
        HistoricalData.WriteDay(root, first, day, corrected, replace: true);
        File.SetLastWriteTimeUtc(path, originalTime);
        e.Check(new FileInfo(path).Length == originalLength && File.GetLastWriteTimeUtc(path) == originalTime,
            "price-distribution correction intentionally preserves source byte length and timestamp", new { length = originalLength, timestamp = originalTime }, new { length = new FileInfo(path).Length, timestamp = File.GetLastWriteTimeUtc(path) });
        var changedCompact = CompactHistory.LoadDay(root, compactRoot, first, day, 1);
        e.Check(initialCompact.Sum(trade => trade.Volume) == 10 && changedCompact.Sum(trade => trade.Volume) == 10 &&
            initialCompact.Any(trade => trade.Price == 10) && changedCompact.All(trade => trade.Price != 10) && changedCompact.Any(trade => trade.Price == 30),
            "content-aware compact cache invalidates a same-count/same-volume/same-length correction", "10 volume moves from {10,20} to {20,30}", changedCompact.Select(TradeSummary).ToArray());
        var changed = TickFile.Read(path, out int dropped, requireComplete: true);
        chart.Feed.PushTrades(first, changed[0]);
        chart.Feed.Now = at.AddDays(1).AddMinutes(1);
        chart.Pump(); chart.Pump();
        var candle = chart.Model.GetCandle(0);
        e.Check(dropped == 0 && chart.Data.Trades.Count == 3 && candle.Volume == 10 && candle.GetPriceVolumeInfo(10) == null && candle.GetPriceVolumeInfo(30)?.Ask == 5 && chart.Model.DataGeneration > generation,
            "actual received correction invalidates the candle without changing event count or double-counting volume", "three events; old price absent; 30:5; volume 10", new { events = chart.Data.Trades.Count, generation = chart.Model.DataGeneration, candle = CandleSummary(candle) });
        e.Check(profile.Profiles[0].Poc == 20 && profile.Profiles[0].BuyerPoc == 20 && profile.Profiles[0].Total == 10,
            "actual completed profile cache recalculates corrected 30:5 and 20:5 to POC 20", "POC 20; buyer POC 20; total 10", ProfileSummary(profile));
        chart.Feed.PushTrades(first, changed[0]);
        chart.Feed.Now = at.AddDays(1).AddMinutes(1);
        chart.Pump(); chart.Pump();
        e.Check(chart.Data.Trades.Count == 3 && chart.Model.GetCandle(0).Volume == 10 && profile.Profiles[0].Total == 10,
            "repeated same-identity correction replaces rather than adds to actual candles/profile", "three events; volume 10", ProfileSummary(profile));
        byte[] intact = File.ReadAllBytes(path);
        string compactFile = CompactHistory.CachePath(compactRoot, first.FeedId, first.Symbol, day, 1);
        byte[] broken = File.ReadAllBytes(compactFile); broken[^1] ^= 0x7F; File.WriteAllBytes(compactFile, broken);
        var recomputed = CompactHistory.LoadDay(root, compactRoot, first, day, 1);
        e.Check(recomputed.Sum(trade => trade.Volume) == 10 && recomputed.All(trade => trade.Price != 10) && recomputed.Any(trade => trade.Price == 30) && File.ReadAllBytes(path).AsSpan().SequenceEqual(intact),
            "corrupt compact cache recomputes corrected rows from untouched authoritative ticks", "corrected {20,30}; volume 10; intact raw bytes", recomputed.Select(TradeSummary).ToArray());
        foreach (string file in Directory.EnumerateFiles(profileRoot, "*.studycache")) File.WriteAllText(file, "{deliberately truncated fixture cache");
        chart.Model.RemoveIndicator(chart.Model.Indicators.Single(slot => ReferenceEquals(slot.Indicator, profile)));
        var fresh = AddProfile(e, chart, profileRoot);
        e.Check(fresh.Profiles[0].Poc == 20 && fresh.Profiles[0].Total == 10 && fresh.Profiles[0].BuyerPoc == 20,
            "fresh actual profile consumer treats corrupt completed cache as a miss and recomputes", "POC 20; total 10; buyer POC 20", ProfileSummary(fresh));
        using (var settings = new StudySettings(e, chart, fresh)) settings.Number(nameof(CompositeVolumeProfile.PriceGrouping), 15);
        chart.Pump();
        e.Check(fresh.Profiles[0].Poc == 15 && fresh.Profiles[0].Total == 10,
            "actual price-row settings invalidate the completed result rather than reusing its old POC", "15-price grouping gives tied lower POC 15; volume 10", ProfileSummary(fresh));
        using (var settings = new StudySettings(e, chart, fresh)) settings.Number(nameof(CompositeVolumeProfile.PriceGrouping), 1);
        chart.Pump();
        e.Check(fresh.Profiles[0].Poc == 20 && File.ReadAllBytes(path).AsSpan().SequenceEqual(intact),
            "restoring row settings recovers corrected POC without rewriting history", 20, ProfileSummary(fresh));
        e.Capture("kvp_history_distribution_correction.png");

        var second = Instrument("provider-b", first.Symbol);
        Trade[] independent = [new(1, at, 40, 5, TradeSide.Buy), new(2, at.AddSeconds(1), 60, 5, TradeSide.Buy), next];
        HistoricalData.WriteDay(root, second, day, independent[..2], replace: true);
        HistoricalData.WriteDay(root, second, day.AddDays(1), [next], replace: true);
        var secondRead = HistoricalData.Read(root, second.FeedId, second.Symbol, at, at.AddDays(2)).ToArray();
        using var other = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("provider-b-depth"), second, secondRead, at.AddDays(1).AddMinutes(1), ChartPeriod.M1);
        var otherProfile = AddProfile(e, other, profileRoot);
        e.Check(first.Symbol == second.Symbol && chart.Model.SourceIdentity != other.Model.SourceIdentity && otherProfile.Profiles[0].Poc == 40 && fresh.Profiles[0].Poc == 20,
            "same symbol on independent providers retains separate source identity and actual cached profile results", "provider A POC20; provider B POC40", new { first = chart.Model.SourceIdentity, second = other.Model.SourceIdentity, firstPoc = fresh.Profiles[0].Poc, secondPoc = otherProfile.Profiles[0].Poc });
        e.Remember("correctedProfiles", new { providerA = ProfileSummary(fresh), providerB = ProfileSummary(otherProfile), rawSha256 = Hash(intact) });
        e.Capture("kvp_history_independent_provider.png");
    }

    private static void RecordedDepth(Evidence e)
    {
        var instrument = Instrument("recorded", "RESTING-L2", 0.25m);
        using var chart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("recorded-depth"), instrument,
            [new Trade(1, Start, -1.25m, 1, TradeSide.Buy)], Start.AddSeconds(4), ChartPeriod.M1);
        var probe = new DepthProbe();
        chart.Model.AddIndicator(probe, applyDefaultColors: false);
        var study = new OrderbookDelta { WithinTicks = 0, MaxAgeSeconds = 10, AllowPartialBook = false };
        var slot = chart.Model.AddIndicator(study, applyDefaultColors: false);
        var heatmapSettings = new ChartSettingsWindow(e.Owner.Trading, chart.Chart);
        try
        {
            heatmapSettings.Show(e.Owner); e.Pump(0.15);
            var tabs = heatmapSettings.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, Lang.T("Depth heatmap")));
            e.Pump(0.08);
            var overlay = heatmapSettings.GetVisualDescendants().OfType<CheckBox>().Single(check => Equals(check.Content, Lang.T("Show liquidity heatmap")));
            e.Press(overlay);
            var disabledBook = new OrderBookBuilder(); disabledBook.Set(BookSide.Bid, -5, 999); disabledBook.Set(BookSide.Ask, -3, 1);
            chart.PushBook(disabledBook, Start.AddSeconds(-1), 99, fullRefresh: true);
            e.Check(chart.Model.Settings.ShowLiquidityHeatmap && chart.Data.DepthHistory.Status.RecordingState == DepthRecordingState.Disabled &&
                chart.Data.DepthHistory.Query(Start.AddMinutes(-1), Start.AddMinutes(1)).Samples.Count == 0 && DepthFiles(chart.Data.RecordingRoot!).Length == 0,
                "actual heatmap display checkbox grants no recording permission, retained history or disk writes", "overlay on; recorder disabled; no recorded samples/files", chart.Data.DepthHistory.Status);
            e.Capture("kvp_depth_overlay_without_recording.png", heatmapSettings);
        }
        finally { heatmapSettings.Close(); }
        SetRecording(e, true);
        e.Owner.Navigate("trading"); e.Owner.Trading.Activate(chart.Chart); chart.Pump();
        e.Check(App.Services.Settings.RecordDepth && chart.Data.DepthRecording.Enabled && chart.Data.DepthHistory.Status.RecordingState == DepthRecordingState.Recording,
            "explicit application recording control starts the existing data consumer without replacing the provider", "recording enabled", chart.Data.DepthHistory.Status);
        var book = new OrderBookBuilder();
        book.Set(BookSide.Bid, -5, 10); book.Set(BookSide.Bid, -8, ExactSize);
        book.Set(BookSide.Ask, -3, 2); book.Set(BookSide.Ask, -1, 4);
        int callbacks = probe.Updates.Count;
        chart.PushBook(book, Start, 1, fullRefresh: true, pump: false);
        book.Set(BookSide.Bid, -5, 30); book.Set(BookSide.Ask, -3, 6); book.Set(BookSide.Ask, -1, 0);
        chart.PushBook(book, Start.AddSeconds(1), 2, previousSequence: 1, pump: false);
        chart.SetClock(Start.AddSeconds(4));
        e.Check(probe.Updates.Skip(callbacks).Select(update => update.Sequence).SequenceEqual(new long?[] { 1, 2 }) &&
            probe.Updates.Skip(callbacks).Select(update => update.Snapshot.BestBidSize).SequenceEqual(new decimal[] { 10, 30 }),
            "real sink and UI pump deliver full-refresh then replacement in receive order", "sequences 1,2; bid sizes 10,30", probe.Updates.Skip(callbacks).Select(update => new { update.Sequence, update.Snapshot.BestBidSize }).ToArray());
        var current = chart.Model.MarketDepthInfo.Current;
        e.Check(current != null && current.Snapshot.GetSize(BookSide.Bid, -8) == ExactSize && current.Snapshot.GetSize(BookSide.Ask, -1) == 0 && current.Snapshot.BestBidSize == 30 &&
            current.ProviderId == instrument.FeedId && current.Venue == instrument.Exchange,
            "actual SDK depth retains exact decimal/deep size, removal, negative prices and provider/venue", new { deep = ExactSize, bid = 30, removedAsk = 0, venue = instrument.Exchange }, current);
        e.Check(probe.BestQuotes.LastOrDefault()?.IsAvailable == true && slot.Error == null, "actual depth and best-quote callbacks recover after recording is enabled", "available best quote; no calculation error", slot.Error);
        var main = (OptionalValueDataSeries)study.DataSeries[0];
        var measurement = study.GetMeasurement(0);
        e.Check(measurement is { IsAvailable: true, Bid: 20, Ask: 4, Delta: 16, Samples: 2 } && main.IsAvailable(0) && main[0] == 16,
            "actual sample-average Orderbook Delta gives each 10/2 and 30/6 snapshot one vote", "bid20 ask4 delta16; two snapshots", measurement);
        using (var settings = new StudySettings(e, chart, study))
        {
            settings.Enum(nameof(OrderbookDelta.Aggregation), DepthAggregation.DurationAverage);
            chart.Pump(); measurement = study.GetMeasurement(0);
            e.Check(measurement is { IsAvailable: true, Bid: 25, Ask: 5, Delta: 20 } && measurement.CoveredDuration == TimeSpan.FromSeconds(4) && main[0] == 20,
                "actual duration-average settings weight 10/2 for one second and 30/6 for three", "bid25 ask5 delta20; four covered seconds", measurement);
            e.Capture("kvp_depth_duration_average_settings.png", settings.Window);
            settings.Enum(nameof(OrderbookDelta.Aggregation), DepthAggregation.SampleAverage);
            settings.Enum(nameof(OrderbookDelta.View), OrderbookDelta.BookView.BidAsk);
            chart.Pump();
            e.Check(((OptionalValueDataSeries)study.DataSeries[1]).IsAvailable(0) && ((OptionalValueDataSeries)study.DataSeries[1])[0] == 20 && ((OptionalValueDataSeries)study.DataSeries[2])[0] == 4,
                "actual Bid/Ask mode exposes independently worked resting sizes", "bid20 ask4", study.GetMeasurement(0));
            settings.Enum(nameof(OrderbookDelta.View), OrderbookDelta.BookView.Imbalance); chart.Pump();
            e.Check(main.IsAvailable(0) && Math.Abs(main[0] - 66.66666666666666666666666667m) < 0.000000000000000000000001m,
                "actual imbalance mode calculates 16/24 percent without conflating executions", "66.66666666666666666666666667%", main[0]);
            settings.Enum(nameof(OrderbookDelta.View), OrderbookDelta.BookView.Change); chart.Pump();
            e.Check(!main.IsAvailable(0), "book change has no invented zero-valued predecessor on its first bar", "missing", main.IsAvailable(0));
            settings.Number(nameof(OrderbookDelta.MaxAgeSeconds), 120);
        }
        chart.PushTrade(new Trade(2, Start.AddMinutes(1), -1.25m, 1, TradeSide.Buy));
        book.Set(BookSide.Bid, -5, 50); book.Set(BookSide.Ask, -3, 10);
        chart.PushBook(book, Start.AddMinutes(1), 3, previousSequence: 2);
        chart.SetClock(Start.AddMinutes(1).AddSeconds(4));
        e.Until(() => { chart.Pump(); return !chart.Model.IsLoadingDepthHistory && main.IsAvailable(1); }, "actual new bar and selected-depth read settle for continuous book change");
        e.Check(main.IsAvailable(1) && main[1] == 24, "actual Change mode gives current delta40 minus previous sample delta16", 24, main[1]);
        using (var settings = new StudySettings(e, chart, study))
        {
            settings.Enum(nameof(OrderbookDelta.View), OrderbookDelta.BookView.Delta);
            settings.Number(nameof(OrderbookDelta.MaxAgeSeconds), 10);
        }
        chart.PushTrade(new Trade(3, Start.AddMinutes(2), -1.25m, 1, TradeSide.Buy));
        book.Set(BookSide.Bid, -5, 70);
        chart.PushBook(book, Start.AddMinutes(2).AddSeconds(1), 5, previousSequence: 4);
        e.Check(chart.Data.DepthHistory.Current == null && chart.Data.DepthHistory.Status.Coverage.HasFlag(DepthCoverage.SequenceGap) && probe.BestQuotes.LastOrDefault()?.IsAvailable == false,
            "a real skipped receive sequence clears SDK current book and best quotes instead of publishing fresh liquidity", "missing current/quote; sequence gap", chart.Data.DepthHistory.Status);
        chart.Status(Start.AddMinutes(2).AddSeconds(2), FeedStatus.Disconnected, "Fixture link disconnected");
        chart.Status(Start.AddMinutes(2).AddSeconds(3), FeedStatus.Reconnecting, "Fixture reconnecting");
        chart.Status(Start.AddMinutes(2).AddSeconds(3), FeedStatus.Connected, "Fixture transport connected; full book still required");
        chart.PushBook(book, Start.AddMinutes(2).AddSeconds(4), 6, previousSequence: 5);
        e.Check(chart.Model.MarketDepthInfo.Current == null && !chart.Model.MarketDepthInfo.GetMarketDepthSnapshot().Any(),
            "reconnect plus incremental update cannot fill an unknown book before authoritative full refresh", "missing depth and empty available SDK snapshot", chart.Data.DepthHistory.Status);
        book.Set(BookSide.Bid, -5, 18); book.Set(BookSide.Ask, -3, 3);
        chart.PushBook(book, Start.AddMinutes(2).AddSeconds(5), 7, fullRefresh: true);
        e.Check(chart.Model.MarketDepthInfo.Current?.Snapshot.BestBidSize == 18 && probe.BestQuotes.LastOrDefault()?.IsAvailable == true,
            "actual reconnect full refresh restores exact book and quote availability", "bid18; available quote", chart.Data.DepthHistory.Status);
        chart.PushTrade(new Trade(4, Start.AddMinutes(3), -1.25m, 1, TradeSide.Buy));
        book.Set(BookSide.Bid, -5, 2); book.Set(BookSide.Ask, -3, 2);
        chart.PushBook(book, Start.AddMinutes(3), 8, fullRefresh: true);
        chart.SetClock(Start.AddMinutes(3).AddSeconds(4));
        e.Until(() => { chart.Pump(); return !chart.Model.IsLoadingDepthHistory; }, "gap/recovery history reaches actual study consumer");
        e.Check(main.IsAvailable(3) && main[3] == 0, "balanced received resting book is available zero, distinct from missing depth", "available zero", new { available = main.IsAvailable(3), value = main[3] });
        using (var settings = new StudySettings(e, chart, study)) settings.Enum(nameof(OrderbookDelta.View), OrderbookDelta.BookView.Change);
        chart.Pump();
        e.Check(!main.IsAvailable(3), "Change refuses a numerically available previous average whose interval contains a gap", "missing across preceding gap", main.IsAvailable(3));
        using (var settings = new StudySettings(e, chart, study))
        {
            settings.Enum(nameof(OrderbookDelta.View), OrderbookDelta.BookView.Delta);
            settings.Enum(nameof(OrderbookDelta.Aggregation), DepthAggregation.Last);
        }
        chart.SetClock(Start.AddMinutes(3).AddSeconds(12));
        e.Check(chart.Data.DepthHistory.Status.IsStale && chart.Model.MarketDepthInfo.Current == null && !main.IsAvailable(3) && probe.BestQuotes.LastOrDefault()?.IsAvailable == false,
            "clock-driven staleness removes last-book availability instead of carrying a balanced zero forward", "stale; missing current/last/quote", new { status = chart.Data.DepthHistory.Status, measurement = study.GetMeasurement(3) });
        e.VisibleDepthStatus(chart, "missing or stale");
        e.Capture("kvp_depth_stale_and_gap_status.png");

        DateTime nextDay = Start.AddDays(1);
        chart.PushTrade(new Trade(5, nextDay, -1.25m, 1, TradeSide.Buy));
        book.Set(BookSide.Bid, -5, 100); book.Set(BookSide.Ask, -3, 20);
        chart.PushBook(book, nextDay, 1, fullRefresh: true);
        chart.StopWriter(nextDay.AddSeconds(4));
        var status = chart.Data.DepthHistory.Status;
        e.Check(status.RecordingState == DepthRecordingState.Stopped && status.LastPersistedAt == nextDay.AddSeconds(4) && status.DroppedRecords == 0 && chart.Data.DepthHistory.MemoryUsage.QueuedBytes == 0,
            "actual InstrumentData.Stop drains the writer and persists its terminal missing marker", "stopped; persisted through stop clock; zero queue/loss", status);
        e.Check(DepthFiles(chart.Data.RecordingRoot!).Length >= 2 && chart.Data.DepthHistory.RecordingStatistics.BookRecords > 0 && chart.Data.DepthHistory.RecordingStatistics.GapRecords > 0,
            "real writer persisted both historical days and explicit book/gap records", "at least two day segments; book and gap records", chart.Data.DepthHistory.RecordingStatistics);
        chart.Feed.Now = Start.AddMonths(2);
        chart.Data.DepthHistory.Refresh(chart.Feed.Now);
        e.Check(chart.Data.DepthHistory.Query(Start, nextDay.AddMinutes(1)).Samples.Count == 0,
            "historical selection is tested after the live ring has expired, not against retained current samples", 0, chart.Data.DepthHistory.Query(Start, nextDay.AddMinutes(1)).Samples.Count);
        // A distinct visible range forces a fresh selected disk read; CenterOn is the normal chart navigation seam.
        chart.Model.CenterOn(Start, Start.AddMinutes(3));
        chart.Model.Viewport.BarWidth = 220;
        chart.Model.Viewport.ScrollBars = 2;
        chart.Model.Viewport.AutoScale = false;
        chart.Model.Viewport.PriceBottom = -2.5;
        chart.Model.Viewport.PriceTop = 0.5;
        chart.Select(Start, Start.AddMinutes(4));
        var selected = chart.Model.GetDepthHistory(Start, Start.AddMinutes(4));
        var first = selected.Samples.FirstOrDefault(sample => sample.Update.Sequence == 1 && sample.Time == Start);
        var replacement = selected.Samples.FirstOrDefault(sample => sample.Update.Sequence == 2);
        e.Check(first != null && first.Snapshot.BestBidSize == 10 && first.Snapshot.GetSize(BookSide.Bid, -8) == ExactSize && first.ProviderId == instrument.FeedId && first.Venue == instrument.Exchange && first.SourceTime == Start.AddMilliseconds(-7),
            "actual selected historical-day reader reloads exact sizes, order, source time and provider/venue from disk", new { bid = 10, deep = ExactSize, sourceTime = Start.AddMilliseconds(-7), instrument.Exchange }, first);
        e.Check(replacement != null && replacement.Snapshot.BestBidSize == 30 && replacement.Snapshot.GetSize(BookSide.Ask, -1) == 0 &&
            replacement.Update.Changes.Any(change => change.Side == BookSide.Ask && change.PriceTicks == -1 && change.Size == 0 && change.Origin == BookChangeOrigin.Received),
            "selected persisted replacement/removal retains real received changes rather than reconstructed echoes", "bid30; received remove ask -1", replacement);
        e.Check(selected.Samples.Any(sample => sample.GapBefore == DepthGapReason.Sequence && !sample.IsUsable) && selected.Samples.Any(sample => sample.GapBefore == DepthGapReason.Disconnected),
            "selected historical range exposes sequence and reconnect gaps as missing coverage", "sequence and disconnect markers", selected.Samples.Where(sample => sample.GapBefore != DepthGapReason.None).Select(sample => new { sample.Time, sample.GapBefore, sample.IsUsable }).ToArray());
        var shortQuery = chart.Model.GetDepthHistory(Start, Start.AddSeconds(4));
        var frame = LiquidityHeatmapRenderer.BuildFrame(shortQuery, 2, TimeSpan.FromSeconds(2), minimumTicks: -5, maximumTicks: -3);
        e.Check(frame.Cells.Any(cell => cell.Side == BookSide.Bid && cell.PriceTicks == -6 && cell.RestingSize == 10 && cell.From == Start && cell.To == Start.AddSeconds(1)) &&
            frame.Cells.Any(cell => cell.Side == BookSide.Bid && cell.PriceTicks == -6 && cell.RestingSize == 30 && cell.From == Start.AddSeconds(1) && cell.To == Start.AddSeconds(3)) &&
            frame.Gaps.Any(gap => gap.From == Start.AddSeconds(3) && gap.To == Start.AddSeconds(4)),
            "persisted-source heatmap groups negative ticks downward, keeps exact resting size and shades stale tail", "-6 row: 10 then30, never40; stale 3s..4s", frame);
        e.Check(frame.Cells.All(cell => cell.From >= Start && cell.To <= Start.AddSeconds(4) && cell.PriceTicks is -6 or -4) && !frame.IsTruncated,
            "actual heatmap frame clips selected time/price range without allocating the empty price span", "only selected -6/-4 rows and four seconds", frame);
        using (var historicalModel = new ChartModel(chart.Data, ChartPeriod.Parse("S4"),
            new ChartSettings { TimeZoneId = "UTC", ShowLiquidityHeatmap = true, HeatmapPriceGrouping = 2 }))
        {
            var historicalStudy = new OrderbookDelta { WithinTicks = 0, MaxAgeSeconds = 10, AllowPartialBook = false };
            var historicalSlot = historicalModel.AddIndicator(historicalStudy, applyDefaultColors: false);
            historicalModel.EnsureDepthHistory(Start, Start.AddSeconds(4));
            e.Until(() => { historicalModel.Update(); return !historicalModel.IsLoadingDepthHistory; },
                "actual four-second historical study independently reloads the persisted range");
            var historicalMain = (OptionalValueDataSeries)historicalStudy.DataSeries[0];
            var samples = historicalStudy.GetMeasurement(0);
            e.Check(historicalSlot.Error == null && samples is { IsAvailable: true, Bid: 20, Ask: 4, Delta: 16 } &&
                historicalMain.IsAvailable(0) && historicalMain[0] == 16,
                "persisted-source actual Orderbook Delta reproduces the independently worked sample average after live eviction",
                "bid20 ask4 delta16", samples);
            historicalStudy.Aggregation = DepthAggregation.DurationAverage; historicalModel.Update();
            var duration = historicalStudy.GetMeasurement(0);
            e.Check(duration is { IsAvailable: true, Bid: 25, Ask: 5, Delta: 20 } &&
                duration.CoveredDuration == TimeSpan.FromSeconds(4) && historicalMain[0] == 20,
                "persisted-source actual Orderbook Delta reproduces the duration-weighted result, not the sample average",
                "bid25 ask5 delta20; four covered seconds", duration);
            historicalModel.Viewport.BarWidth = 300;
            historicalModel.Viewport.ScrollBars = 3;
            historicalModel.Viewport.AutoScale = false;
            historicalModel.Viewport.PriceBottom = -2.5; historicalModel.Viewport.PriceTop = 0.5;
            var historicalView = new ChartView { Model = historicalModel };
            var historicalWindow = new Window
            {
                Title = "Recorded historical L2 — actual four-second heatmap and duration-average study",
                Width = 1200, Height = 800, Content = historicalView,
            };
            try
            {
                historicalWindow.Show(e.Owner); e.Pump(0.15);
                e.Until(() => { historicalModel.Update(); return !historicalModel.IsLoadingDepthHistory; },
                    "actual persisted-depth study and heatmap render their selected range");
                historicalView.InvalidateVisual();
                e.Capture("kvp_depth_persisted_orderbook_delta.png", historicalWindow);
            }
            finally { historicalWindow.Close(); }
            e.Remember("persistedDepthMeasurements", new { sampleAverage = samples, durationAverage = duration });
        }
        using var secondChart = new ChartModel(chart.Data, ChartPeriod.M1, new ChartSettings { TimeZoneId = "UTC" });
        secondChart.EnsureDepthHistory(nextDay, nextDay.AddMinutes(1));
        e.Until(() => { secondChart.Update(); return !secondChart.IsLoadingDepthHistory; }, "second actual chart independently loads the other recorded day");
        var otherDay = secondChart.GetDepthHistory(nextDay, nextDay.AddSeconds(1));
        var firstAfter = chart.Model.GetDepthHistory(Start, Start.AddSeconds(1)).Samples.SingleOrDefault(sample => sample.IsUsable);
        e.Check(firstAfter != null && first != null && firstAfter.RecordingId == first.RecordingId && firstAfter.Ordinal == first.Ordinal &&
            firstAfter.Snapshot.BestBidSize == 10 && otherDay.Samples.SingleOrDefault(sample => sample.IsUsable)?.Snapshot.BestBidSize == 100,
            "loading the second chart's day preserves the first chart's exact selected record and independently worked book",
            "same first-day recording/ordinal and bid10 after second-day bid100 loads",
            new { firstBefore = first?.Snapshot.BestBidSize, firstAfter = firstAfter?.Snapshot.BestBidSize, second = otherDay.Samples.Where(sample => sample.IsUsable).Select(sample => sample.Snapshot.BestBidSize).ToArray() });
        chart.Model.Settings.HeatmapMaxAgeSeconds = 10;
        chart.Model.Settings.HeatmapPriceGrouping = 2;
        chart.Model.Invalidate(); chart.View.InvalidateVisual(); e.Pump(0.15);
        e.Until(() => { chart.Model.Update(); return !chart.Model.IsLoadingDepthHistory; }, "selected historical range reaches actual heatmap rendering");
        e.VisibleDepthStatus(chart, "missing or stale");
        e.Remember("selectedDepth", new { from = Start, to = Start.AddMinutes(4), selected.Coverage, selected.Message, selected.IsTruncated, samples = selected.Samples.Select(sample => new { sample.Time, sample.SourceTime, sample.ProviderId, sample.Venue, sample.Update.Sequence, sample.GapBefore, sample.IsUsable, bids = sample.Snapshot.Bids, asks = sample.Snapshot.Asks }).ToArray(), frame, writer = chart.Data.DepthHistory.RecordingStatistics });
        e.Capture("kvp_depth_selected_historical_range.png");
    }

    private static void RecordingLimits(Evidence e)
    {
        SetRecording(e, true);
        var retainedInstrument = Instrument("retention", "RETENTION", 0.25m);
        string root = e.PathFor("retention-depth");
        var options = new DepthRecordingOptions { Enabled = true, RetentionDays = 1, MaxDiskBytes = 4096, MaxSegmentBytes = 1024, MaxMemorySamples = 2, MemoryRetention = TimeSpan.FromSeconds(1) };
        using (var chart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, root, retainedInstrument,
            [new Trade(1, Start, -1.25m, 1, TradeSide.Buy)], Start.AddSeconds(1), ChartPeriod.M1, options))
        {
            byte[] protectedBytes = [11, 22, 33, 44];
            Directory.CreateDirectory(root);
            string protectedPath = Path.Combine(root, "raw-source.fixture"); File.WriteAllBytes(protectedPath, protectedBytes);
            var book = new OrderBookBuilder(); book.Set(BookSide.Bid, -5, 10); book.Set(BookSide.Ask, -3, 2);
            chart.PushBook(book, Start, 1, fullRefresh: true);
            e.Until(() => chart.Data.DepthHistory.Status.LastPersistedAt == Start || chart.Data.DepthHistory.Status.RecordingState == DepthRecordingState.Error, "first retained day reaches actual disk writer");
            DateTime next = Start.AddDays(1);
            for (int i = 0; i < 4; i++)
            {
                book.Set(BookSide.Bid, -5, 20 + i);
                chart.PushBook(book, next.AddSeconds(i), i + 2, fullRefresh: true);
                DateTime persistedAt = next.AddSeconds(i);
                e.Until(() => chart.Data.DepthHistory.Status.LastPersistedAt == persistedAt || chart.Data.DepthHistory.Status.RecordingState == DepthRecordingState.Error, "retention/quota segment rotation completes without hiding write errors");
            }
            chart.StopWriter(next.AddSeconds(4));
            var old = e.Await(chart.Data.DepthHistory.LoadAsync(Start.Date, Start.Date.AddDays(1)), "expired retained-day read completes");
            var latest = e.Await(chart.Data.DepthHistory.LoadAsync(next, next.AddMinutes(1)), "latest retained-day read completes");
            long diskBytes = DepthFiles(root).Sum(file => new FileInfo(file).Length);
            e.Check(chart.Data.DepthHistory.Status.RecordingState == DepthRecordingState.Stopped && old.Samples.Count == 0 && latest.Samples.Any(sample => sample.IsUsable) && diskBytes is > 0 and <= 4096,
                "actual one-day retention and segment quota discard only expired depth while preserving a bounded readable suffix", "old day missing; latest day available; at most4096 bytes", new { old = old.Samples.Count, latest = latest.Samples.Count, diskBytes, status = chart.Data.DepthHistory.Status });
            e.Check(File.ReadAllBytes(protectedPath).AsSpan().SequenceEqual(protectedBytes), "depth retention never deletes or rewrites the independent raw source fixture", Hash(protectedBytes), Hash(File.ReadAllBytes(protectedPath)));
            e.Check(chart.Data.DepthHistory.MemoryUsage.LiveBytes <= chart.Data.DepthHistory.MemoryUsage.LiveLimit && chart.Data.DepthHistory.MemoryUsage.LiveWasTruncated,
                "actual live retention is bounded and exposes its lost prefix instead of complete coverage", "bounded live memory; truncated prefix", chart.Data.DepthHistory.MemoryUsage);
            chart.Select(Start, next.AddMinutes(1));
            var query = chart.Model.GetDepthHistory(Start, next.AddMinutes(1));
            e.Check(query.IsTruncated || query.Coverage.HasFlag(DepthCoverage.Missing), "retained/truncated selected source remains explicitly incomplete", "missing or truncated selected coverage", new { query.Coverage, query.Message, query.IsTruncated });
            e.VisibleDepthStatus(chart, "disk cap");
            e.VisibleDepthStatus(chart, "retention up to 1d");
            e.Remember("retention", new { options, diskBytes, old = old.Samples.Count, latest = latest.Samples.Count, query.Coverage, query.Message, query.IsTruncated, memory = chart.Data.DepthHistory.MemoryUsage });
            e.Capture("kvp_depth_retention_and_quota_status.png");
        }

        foreach (bool quota in new[] { false, true })
        {
            string name = quota ? "quota" : "io";
            string failureRoot = e.PathFor(name + "-failure-root");
            if (!quota) File.WriteAllText(failureRoot, "owned fixture: not a directory");
            var instrument = Instrument(name + "-failure", "DEPTH-FAILURE", 0.25m);
            var limits = quota ? new DepthRecordingOptions { Enabled = true, MaxDiskBytes = 1024, MaxSegmentBytes = 512 } : new DepthRecordingOptions { Enabled = true };
            using var chart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, failureRoot, instrument,
                [new Trade(1, Start, -1.25m, 1, TradeSide.Buy)], Start.AddSeconds(1), ChartPeriod.M1, limits);
            chart.Model.Settings.ShowLiquidityHeatmap = true;
            chart.PushWideBook(Start, quota ? 40 : 2, 1);
            e.Until(() => { chart.Pump(); return chart.Data.DepthHistory.Status.RecordingState == DepthRecordingState.Error; }, name + ": actual writer reports the injected storage failure");
            var status = chart.Data.DepthHistory.Status;
            e.Check(status.DroppedRecords > 0 && !string.IsNullOrWhiteSpace(status.Message) && chart.Model.MarketDepthInfo.Current?.IsUsable == true,
                name + ": recording failure is distinct from an available live book and lost persistence remains visible", "visible error/loss, but usable live book", status);
            if (quota) e.Check(status.Message!.Contains("limit", StringComparison.OrdinalIgnoreCase), "quota error explains that the exact frame cannot fit rather than silently compressing/dropping levels", "explicit disk/segment limit error", status.Message);
            else e.Check(File.ReadAllText(failureRoot) == "owned fixture: not a directory", "I/O failure does not overwrite its blocking fixture", "owned fixture: not a directory", File.ReadAllText(failureRoot));
            e.VisibleDepthStatus(chart, "failed");
            e.Capture("kvp_depth_" + name + "_failure_visible.png");
            chart.StopWriter(Start.AddSeconds(2));
            e.Check(chart.Data.DepthHistory.Status.RecordingState == DepthRecordingState.Error && chart.Data.DepthHistory.MemoryUsage.QueuedBytes == 0,
                name + ": stop drains a faulted writer without changing its error into success", "Error; empty queue", chart.Data.DepthHistory.Status);
            var persisted = e.Await(chart.Data.DepthHistory.LoadAsync(Start, Start.AddMinutes(1)), name + ": persisted failed range read completes");
            e.Check(!persisted.Samples.Any(sample => sample.IsUsable), name + ": failed persistence never appears as a recorded fresh zero or an intact book", "no persisted usable book", persisted);
            e.Remember(name + "Failure", new { status, options = limits, persisted.Message, persisted.IsTruncated });
        }
    }

    private static void SparsePrices(Evidence e)
    {
        var instrument = Instrument("sparse", "SIGNED-SPARSE");
        string root = e.PathFor("sparse-ticks");
        Trade[] trades = [new(1, Start, -1_000_000_000_000m, 3, TradeSide.Buy), new(2, Start.AddSeconds(1), 0, 5, TradeSide.Buy), new(3, Start.AddSeconds(2), 1_000_000_000_000m, 2, TradeSide.Buy)];
        HistoricalData.WriteDay(root, instrument, DateOnly.FromDateTime(Start), trades);
        var persisted = HistorySources.ReadDay(root, instrument.FeedId, instrument.Symbol, DateOnly.FromDateTime(Start));
        using (var chart = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("sparse-profile-depth"), instrument, persisted, Start.AddMinutes(1), ChartPeriod.M1))
        {
            var profile = AddProfile(e, chart);
            var result = profile.Profiles.Single();
            e.Check(result.Levels.Count == 3 && result.Total == 10 && result.Poc == 0 && chart.Model.GetCandle(0).GetAllPriceLevels().Count() == 3,
                "actual persisted-source candle and profile use three sparse rows across extreme signed prices", "three rows; total10; POC0", ProfileSummary(profile));
            e.Capture("kvp_history_sparse_extreme_prices.png");
        }
        var depthInstrument = Instrument("sparse-depth", "SIGNED-BOOK", 0.25m);
        using var depth = KvpDepthHistoryFixture.Open(e.Owner, e.Pump, e.PathFor("sparse-book-depth"), depthInstrument,
            [new Trade(1, Start, -1.25m, 1, TradeSide.Buy)], Start.AddSeconds(4), ChartPeriod.M1, new DepthRecordingOptions { Enabled = true });
        var book = new OrderBookBuilder();
        book.Set(BookSide.Bid, -5, 10); book.Set(BookSide.Bid, long.MinValue + 5, 3);
        book.Set(BookSide.Ask, -3, 2); book.Set(BookSide.Ask, long.MaxValue - 5, 7);
        depth.PushBook(book, Start, 1, fullRefresh: true);
        depth.SetClock(Start.AddSeconds(4));
        depth.StopWriter(Start.AddSeconds(4));
        depth.Data.DepthHistory.Refresh(Start.AddDays(1));
        depth.Select(Start, Start.AddMinutes(1));
        var query = depth.Model.GetDepthHistory(Start, Start.AddSeconds(4));
        var all = LiquidityHeatmapRenderer.BuildFrame(query, 2, TimeSpan.FromSeconds(10));
        var clipped = LiquidityHeatmapRenderer.BuildFrame(query, 2, TimeSpan.FromSeconds(10), minimumTicks: -5, maximumTicks: -3);
        var cap = LiquidityHeatmapRenderer.BuildFrame(query, 2, TimeSpan.FromSeconds(10), maxCells: 2);
        var statistics = DepthStatistics.Measure(query, 0, DepthAggregation.SampleAverage, TimeSpan.FromSeconds(10));
        e.Check(all.Cells.Count == 4 && all.Cells.Any(cell => cell.PriceTicks < -1_000_000_000_000m) && all.Cells.Any(cell => cell.PriceTicks > 1_000_000_000_000m) && !all.IsTruncated,
            "persisted sparse heatmap visits actual occupied signed rows, not every tick in the price span", "four occupied cells, including both extreme signed ticks", all);
        e.Check(clipped.Cells.Count == 2 && clipped.Cells.Any(cell => cell.PriceTicks == -6 && cell.RestingSize == 10) && clipped.Cells.Any(cell => cell.PriceTicks == -4 && cell.RestingSize == 2) && statistics is { Bid: 10, Ask: 2, Delta: 8 },
            "negative-price clipping and inclusive best-distance calculation retain exact known sizes without signed overflow", "two clipped cells; best bid10 ask2 delta8", new { clipped, statistics });
        e.Check(cap.IsTruncated && cap.Cells.Count == 2, "explicit heatmap cell quota is reported as incomplete instead of silently losing sparse rows", "two cells; truncated", cap);
        depth.Model.Settings.ShowLiquidityHeatmap = true;
        depth.Model.Settings.HeatmapPriceGrouping = 2;
        depth.Model.Viewport.BarWidth = 300;
        depth.Model.Viewport.AutoScale = false; depth.Model.Viewport.PriceBottom = -2; depth.Model.Viewport.PriceTop = 0;
        depth.Model.Invalidate(); depth.View.InvalidateVisual(); e.Pump(0.15);
        e.Until(() => { depth.Model.Update(); return !depth.Model.IsLoadingDepthHistory; }, "actual signed sparse book heatmap renders its selected persisted source");
        e.Remember("sparseBook", new { all, clipped, cap, statistics });
        e.Capture("kvp_depth_sparse_negative_heatmap.png");
    }

    private static void SetRecording(Evidence e, bool enabled)
    {
        e.Owner.Navigate("settings"); e.Pump(0.1);
        var toggle = e.Owner.Page("settings")!.GetVisualDescendants().OfType<ToggleSwitch>().Single(control => control.Name == "RecordDepth");
        toggle.BringIntoView(); e.Pump(0.05);
        if (toggle.IsChecked != enabled)
        {
            toggle.Focus();
            toggle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, Source = toggle });
            toggle.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space, Source = toggle });
            e.Pump(0.05);
        }
        e.Check(App.Services.Settings.RecordDepth == enabled && App.Services.Hub.DepthRecording.Enabled == enabled,
            enabled ? "actual recording permission control reaches the live isolated composition" : "actual recording permission control disables persistence without a provider fallback", enabled, App.Services.Settings.RecordDepth);
    }

    private static string[] DepthFiles(string root) => Directory.Exists(root) ? Directory.GetFiles(root, "*.depth", SearchOption.AllDirectories) : [];
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool SameEvents(IReadOnlyList<Trade> expected, IReadOnlyList<Trade> actual) => expected.Count == actual.Count && expected.Zip(actual).All(pair =>
        pair.First.Id == pair.Second.Id && pair.First.Time == pair.Second.Time && pair.First.Price == pair.Second.Price && pair.First.Volume == pair.Second.Volume && pair.First.Side == pair.Second.Side);
    private static object TradeSummary(Trade trade) => new { trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side, trade.Provenance };
    private static object CandleSummary(IndicatorCandle candle) => new { candle.Time, candle.Volume, candle.Close, candle.MaxTradeVolume, candle.Quality, candle.ResolutionSeconds, candle.ObservedTrades, candle.AggregatedTrades, candle.HasObservedFootprint, candle.HasAggregatedFootprint, candle.HasInferredSide, candle.IsFootprintComplete, candle.SourceIdentity };
    private static object ProfileSummary(CompositeVolumeProfile profile) => new { profile.Status, profile.CacheError, profiles = profile.Profiles.Select(value => new { value.Total, value.Poc, value.BuyerPoc, value.SellerPoc, value.HasBuyerPoc, value.HasSellerPoc, value.Coverage, value.ResolutionSeconds, levels = value.Levels.Select(pair => new { price = pair.Key, volume = pair.Value }).ToArray() }).ToArray() };

    private sealed class DepthProbe : Indicator
    {
        public List<DepthUpdate> Updates { get; } = new();
        public List<MarketDataArg> BestQuotes { get; } = new();
        protected override void OnCalculate(int bar, decimal value) { }
        protected override void OnDepthSnapshot(DepthUpdate update) => Updates.Add(update);
        protected override void OnBestBidAskChanged(MarketDataArg depth) => BestQuotes.Add(depth);
    }

    private sealed class StudySettings : IDisposable
    {
        private readonly Evidence _e;
        private readonly KvpDepthHistoryFixture _fixture;
        private readonly Indicator _indicator;
        public Window Window { get; }
        public StudySettings(Evidence e, KvpDepthHistoryFixture fixture, Indicator indicator)
        {
            _e = e; _fixture = fixture; _indicator = indicator;
            var settings = new IndicatorsWindow(App.Services, fixture.Model);
            Window = settings; settings.Show(e.Owner); e.Pump(0.15);
            var slot = fixture.Model.Indicators.Single(item => ReferenceEquals(item.Indicator, indicator));
            var list = settings.GetVisualDescendants().OfType<ListBox>().Single(box => box.Items.OfType<ListBoxItem>().Any(item => ReferenceEquals(item.Tag, slot)));
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => ReferenceEquals(item.Tag, slot));
            e.Pump(0.08);
        }
        private Grid Row(string property)
        {
            PropertyInfo info = _indicator.GetType().GetProperty(property)!;
            string label = Lang.T(PropertyGrid.NameOf(info));
            var grid = Window.GetVisualDescendants().OfType<PropertyGrid>().Single();
            var row = grid.GetVisualDescendants().OfType<Grid>().Single(candidate => candidate.Children.OfType<TextBlock>().Any(text => text.Text == label));
            row.BringIntoView(); _e.Pump(0.04); return row;
        }
        public void Enum(string property, object value)
        {
            var combo = Row(property).Children.OfType<ComboBox>().Single();
            combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, value));
            _fixture.Pump(0.04);
        }
        public void Number(string property, decimal value)
        {
            Row(property).Children.OfType<NumericUpDown>().Single().Value = value;
            _fixture.Pump(0.04);
        }
        public void Toggle(string property, bool value)
        {
            var check = Row(property).Children.OfType<CheckBox>().Single();
            if (check.IsChecked != value) _e.Press(check);
            _fixture.Pump(0.04);
        }
        public void Dispose() { Window.Close(); _fixture.Pump(0.04); }
    }

    private sealed class Evidence
    {
        private sealed record Assertion(string Section, string Name, bool Passed, object? Expected, object? Actual);
        private readonly List<Assertion> _assertions = new();
        private readonly Dictionary<string, object> _observations = new();
        private readonly List<string> _captures = new();
        private string _section = "startup";
        public MainWindow Owner { get; }
        public Action<double> Pump { get; }
        public string OutDir { get; }
        private readonly string _fixtureRoot;
        public int Failures { get; private set; }
        public Evidence(MainWindow owner, Action<double> pump, string outDir)
        {
            Owner = owner; Pump = pump; OutDir = outDir; Directory.CreateDirectory(OutDir);
            _fixtureRoot = Path.Combine(OutDir, "kvp-depth-history-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_fixtureRoot);
            _observations.Add("fixtureRoot", _fixtureRoot);
        }
        public string PathFor(string name) => Path.Combine(_fixtureRoot, name);
        public void Check(bool condition, string name, object? expected = null, object? actual = null)
        {
            _assertions.Add(new(_section, name, condition, expected, actual));
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} KVP depth/history [{_section}]: {name}");
            if (!condition) { Failures++; Console.WriteLine("  Expected: " + JsonSerializer.Serialize(expected) + "\n  Actual: " + JsonSerializer.Serialize(actual)); }
        }
        public void Section(string name, Action action)
        {
            _section = name;
            try { action(); }
            catch (Exception error)
            {
                Check(false, "consumer section completes without an unhandled exception", "no exception", error.ToString());
                try { Capture("kvp_depth_history_failure_" + Failures + ".png"); }
                catch (Exception capture) { Console.WriteLine("Failure capture unavailable: " + capture); }
            }
        }
        public void Until(Func<bool> condition, string description)
        {
            var until = DateTime.UtcNow.AddSeconds(45);
            while (!condition() && DateTime.UtcNow < until) Pump(0.02);
            if (!condition()) throw new TimeoutException(description);
            Check(true, description);
        }
        public T Await<T>(Task<T> task, string description)
        {
            Until(() => task.IsCompleted, description);
            return task.GetAwaiter().GetResult();
        }
        public void Remember(string name, object value) => _observations.Add(name, value);
        public void Capture(string name, Window? window = null)
        {
            Pump(0.08);
            using var frame = (window ?? Owner).CaptureRenderedFrame();
            Check(frame != null, "actual rendered frame is available: " + name, "PNG capture", frame == null ? "missing frame" : "captured");
            if (frame == null) return;
            frame.Save(Path.Combine(OutDir, name)); _captures.Add(name);
        }
        public void Press(Control control)
        {
            control.BringIntoView(); Pump(0.05);
            var window = TopLevel.GetTopLevel(control) as Window ?? throw new InvalidOperationException("The actual control is not attached to a window.");
            var point = control.TranslatePoint(new Point(Math.Max(1, control.Bounds.Width / 2), Math.Max(1, control.Bounds.Height / 2)), window)
                ?? throw new InvalidOperationException("The actual control has no input coordinates.");
            window.MouseDown(point, Avalonia.Input.MouseButton.Left); window.MouseUp(point, Avalonia.Input.MouseButton.Left); Pump(0.05);
        }
        public void VisibleDepthStatus(KvpDepthHistoryFixture fixture, string required)
        {
            Owner.Navigate("trading"); Owner.Trading.Activate(fixture.Chart); fixture.Pump(0.2);
            TextBlock? status = null;
            Until(() =>
            {
                status = Owner.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.IsEffectivelyVisible && text.Text?.Contains("Depth:", StringComparison.Ordinal) == true && text.Text.Contains(required, StringComparison.OrdinalIgnoreCase));
                return status != null;
            }, "actual main-window depth status visibly includes " + required);
            Check(fixture.Model.DepthStatusText.Contains(required, StringComparison.OrdinalIgnoreCase), "actual chart status retains the full diagnostic", required, fixture.Model.DepthStatusText);
            if (status != null)
            {
                ToolTip.SetIsOpen(status, true); Pump(0.15);
                Remember("status-" + _observations.Count, new { text = status.Text, tooltip = ToolTip.GetTip(status)?.ToString(), diagnostic = fixture.Model.DepthStatusText });
            }
        }
        public void Save()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            File.WriteAllText(Path.Combine(OutDir, "kvp-depth-history-results.json"), JsonSerializer.Serialize(new
            {
                scene = "kvp-depth-history", checks = _assertions.Count, failures = Failures,
                assertions = _assertions, observations = _observations, captures = _captures,
                scope = "Actual isolated Avalonia controls, chart/data/study consumers, lossless/compact/profile caches, and persisted depth; no real provider or performance certification.",
            }, options));
        }
    }
}
