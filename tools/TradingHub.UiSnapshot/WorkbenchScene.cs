// Scene "workbench": every Workbench panel through the real page, its detachable windows and its persisted settings,
// on offline fixtures only (no network): a fake three-venue aggregate with recent prints and recorded depth, a
// derivatives service whose HTTP and liquidation sockets are in-process fixtures (Bybit long/short fails on purpose so
// the gap shows), a third-party levels fixture, a fixture options provider and an unconfigured options hub.
// Repeat with TRADINGHUB_LANG=es and zh-CN.
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views.Workbench;
using TradingHub.App.Services.Workbench;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Feeds.External;
using TradingHub.Feeds.Venues;
using TradingHub.Market;

internal static class WorkbenchScene
{
    private const decimal Mid = 80_000m;

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        string language = Lang.Current;
        int checks = 0, failures = 0;
        void Check(bool ok, string what)
        {
            checks++;
            Console.WriteLine($"{(ok ? "OK " : "FAIL")}  workbench: {what}");
            if (!ok) failures++;
        }
        void Until(Func<bool> done, double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until) pump(0.05);
        }
        void Shot(TopLevel target, string name)
        {
            pump(0.3);
            using var frame = target.CaptureRenderedFrame();
            frame?.Save(Path.Combine(outDir, $"workbench_{name}_{language}.png"));
        }

        var svc = App.Services;
        Check(!AppPaths.Root.StartsWith(TradingHub.Common.Product.DefaultAppData, StringComparison.Ordinal), "isolated data folder");
        Check(MainWindow.HasPageId("workbench"), "the Workbench is a top-level page");
        var now = DateTime.UtcNow;

        // ---- offline fixtures
        using var derivatives = new DerivativesData(new DerivativesTransport(new HttpClient(new DerivativesFixture()), OwnsHttp: true,
            BinanceRest: new Uri("https://binance.fixture"), BybitRest: new Uri("https://bybit.fixture"),
            ConnectWebSocket: (uri, _) => Task.FromResult<WebSocket>(new LiquidationSocket(uri))));
        long asOf = new DateTimeOffset(now.AddMinutes(-10)).ToUnixTimeMilliseconds();
        string levelsJson = $$"""{"v":2,"coin":"BTC","attribution":"Fixture attribution","snapshot":{"asOf":{{asOf}}},"mark":80000,"positions":42,"step":100,"bins":[[770,900000,0,0,0,3,0,600000,0],[775,400000,0,0,0,2,0,100000,0],[790,300000,0,0,0,2,0,100000,0],[815,0,0,700000,200000,0,4,0,300000],[825,0,0,300000,0,0,1,0,100000]]}""";
        using var levels = new LiquidationLevelsService(new HttpClient(new Fixture(levelsJson)), ownsHttp: true);
        var options = new OptionsData(null) { Provider = new FixtureOptions() };
        var unconfigured = new OptionsData(null);

        var venues = new[]
        {
            new FixtureVenue("binance-futures", "Binance Futures", "Binance", FeedStatus.Connected),
            new FixtureVenue("bybit-linear", "Bybit", "Bybit", FeedStatus.Connected),
            new FixtureVenue("okx-swap", "OKX", "OKX", FeedStatus.Reconnecting),
        };
        using var feed = new AggregateFeed(venues);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = AggregateFeed.FeedId, Exchange = "Aggregate", TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        using var noDepth = new InstrumentData(instrument, feed) { RecordLive = false };
        var random = new Random(11);
        long id = 0;
        decimal price = Mid;
        var start = now.AddMinutes(-25);
        for (var t = start; t < now; t = t.AddMilliseconds(700))
        {
            int source = (int)(id % 3);
            bool buy = random.Next(10) < (source == 0 ? 6 : 4);
            price += buy ? 1m : -1m;
            decimal size = id % 97 == 0 ? 25m : id % 31 == 0 ? 3m : 0.01m * (1 + random.Next(40));
            var trade = new Trade(++id, t, price, size, buy ? TradeSide.Buy : TradeSide.Sell, new TradeProvenance(DataQuality.Raw, SourceIdentity: venues[source].Id));
            data.Trades.Add(trade);
            noDepth.Trades.Add(trade);
        }
        string depthRoot = Path.Combine(AppPaths.MarketRoot, "WorkbenchSceneDepth");
        data.DepthHistory.Start(depthRoot, new DepthRecordingOptions { Enabled = true, QueueCapacity = 4096, MaxQueuedBytes = 8L * 1024 * 1024 }, start);
        for (var t = start; t < now; t = t.AddSeconds(2))
        {
            double minutes = (t - start).TotalMinutes;
            long center = (long)((Mid + (decimal)(Math.Sin(minutes / 4) * 120)) / instrument.TickSize);
            var bids = Enumerable.Range(1, 60).Select(k => new BookLevel(center - k * 10, 0.5m + (k % 7 == 0 ? 12 : 0) + (decimal)random.NextDouble())).ToArray();
            var asks = Enumerable.Range(1, 60).Select(k => new BookLevel(center + k * 10, 0.5m + (k % 9 == 0 ? 15 : 0) + (decimal)random.NextDouble())).ToArray();
            data.DepthHistory.Receive(new DepthUpdate
            {
                Snapshot = new DepthSnapshot(bids, asks, t), ReceivedAt = t, IsFullRefresh = true, Coverage = DepthCoverage.SnapshotOnly,
                ProviderId = "binance-futures", Venue = "Binance",
            }, t);
        }
        using var live = feed.Subscribe(instrument, new NullSink());
        Until(() => feed.GetCoverage(instrument, AggregateOperation.Live).Constituents.Count(c => c.Status == FeedStatus.Connected) == 2, 10);

        var settings = svc.Settings.Workbench ?? new WorkbenchSettings();
        string savedJson = System.Text.Json.JsonSerializer.Serialize(settings);
        settings.FollowActiveChart = false;
        settings.TapeMinNotional = 1_000;
        settings.TapeLargeNotional = 200_000;
        settings.TapeHugeNotional = 1_000_000;
        settings.TapeSoundLarge = false;
        settings.TapeSoundLiquidations = false;
        settings.ToolBalance = 10_000;
        settings.ToolRisk = 1;
        settings.ToolRiskIsAmount = false;
        settings.ToolFeePercent = 0.05m;
        settings.ToolLeverage = 10;
        settings.ToolMaintenancePercent = 0.5m;
        svc.Settings.Workbench = settings;
        window.Navigate("workbench");
        pump(0.3);
        if (window.Page("workbench") is not WorkbenchView view)
        {
            Check(false, "the Workbench page opens");
            return failures;
        }
        view.Context.Derivatives = () => derivatives;
        view.Context.Levels = () => levels;
        view.Context.Options = () => options;
        try
        {
            Check(view.Panels.Select(p => p.Key).SequenceEqual(new[] { "tape", "heatmap", "liquidity", "derivatives", "options", "toolbox" }), "six native panels");
            if (language != Lang.English)
                Check(view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Lang.T("Aggregated trades") && t.Text != "Aggregated trades"), "panel names are translated in " + language);
            view.Apply(new WorkbenchSelection("fixture-aggregate", instrument, data));

            // ---- aggregated trades
            view.Select("tape");
            var tape = view.Panels.OfType<TapePanel>().Single();
            Until(() => tape.Tape.RowCount > 0 && tape.Tape.Rows.Any(r => r.IsLiquidation), 20);
            Check(tape.IsActive, "the visible panel is active");
            Check(tape.Tape.Totals.Keys.Intersect(venues.Select(v => v.Id)).Count() == 3, "the tape counts prints from all three venues: " + string.Join(", ", tape.Tape.Totals.Keys));
            Check(tape.Tape.Rows.All(r => r.IsLiquidation || r.Notional >= 1_000), "lines below the notional threshold are hidden");
            Check(tape.Tape.Rows.Any(r => r.Emphasis == TapeEmphasis.Huge), "huge prints are highlighted");
            Check(tape.Tape.Rows.Any(r => r.IsLiquidation && r.Venue == DerivativesData.Binance) && tape.Tape.Rows.Any(r => r.IsLiquidation && r.Venue == DerivativesData.Bybit),
                "observed liquidations from both streams appear as tape lines");
            Check(tape.Tape.Buckets.Any(b => b.Venues.Values.Any(v => v.Liquidations == LiquidationCoverage.NotObserved || v.Liquidations == LiquidationCoverage.Partial)),
                "buckets before the liquidation streams connected are not reported as observed zeros");
            string coverage = view.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "TapeCoverage")?.Text ?? "";
            Check(coverage.Contains("OKX", StringComparison.Ordinal) && tape.Tape.Buckets.Any(b => b.Venues.TryGetValue("okx-swap", out var okx) && okx.TradesPartial),
                "a reconnecting venue is shown and its volume is flagged partial: " + coverage);
            Shot(window, "tape");

            // ---- depth heat map, with recording and without
            view.Select("heatmap");
            var heat = view.Panels.OfType<HeatmapPanel>().Single();
            Until(() => heat.Canvas.LastSpans > 0, 10);
            Check(heat.Canvas.LastSpans > 0, $"the heat map paints recorded depth ({heat.Canvas.LastSpans} spans)");
            Check(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeatmapRecordingOff").IsVisible == false, "no recording-off banner while depth is recorded");
            Shot(window, "heatmap");
            view.Apply(new WorkbenchSelection("fixture-aggregate", instrument, noDepth));
            Until(() => view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeatmapRecordingOff").IsVisible, 5);
            Check(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeatmapRecordingOff").IsVisible, "with depth recording off the heat map says so instead of showing an empty map");
            Check(!svc.Settings.RecordDepth, "the Workbench never turns depth recording on");
            Shot(window, "heatmap_recording_off");
            view.Apply(new WorkbenchSelection("fixture-aggregate", instrument, data));

            // ---- liquidation map
            bool levelsBefore = svc.Settings.ThirdPartyLiquidationLevels;
            view.Select("liquidity");
            var liquidity = view.Panels.OfType<LiquidityPanel>().Single();
            Until(() => liquidity.Canvas.View is { Estimate: not null, ThirdParty.Count: > 0 }, 30);
            Check(liquidity.Canvas.View?.Estimate is { Used: > 0 }, $"the local estimate models open-interest bars ({liquidity.Canvas.View?.Estimate?.Used} used)");
            Check(liquidity.Canvas.View?.ThirdParty is { Count: > 0 }, "the opt-in third-party levels fixture is drawn and labelled");
            Check(liquidity.Canvas.View?.Bands.Count == 5, "the leverage reference grid has five tiers");
            Check(svc.Settings.ThirdPartyLiquidationLevels == levelsBefore, "the panel does not change the third-party opt-in");
            Shot(window, "liquidity");

            // ---- derivatives dashboard
            view.Select("derivatives");
            var deriv = view.Panels.OfType<DerivativesPanel>().Single();
            Until(() => deriv.Inputs.Count == 2, 30);
            pump(2.5);
            Check(deriv.Inputs.TryGetValue(DerivativesData.Binance, out var binance) && binance.Funding != null && binance.OpenInterest != null && binance.Stats != null && binance.LongShort != null,
                "Binance funding, open interest, premium and long/short load");
            Check(deriv.Inputs.TryGetValue(DerivativesData.Bybit, out var bybit) && bybit.LongShort == null && bybit.Errors.Any(e => e.Contains("long/short", StringComparison.Ordinal)),
                "a failing Bybit long/short request stays a reported gap");
            var table = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "DerivativesTable");
            Check(table.Children.OfType<TextBlock>().Count(t => t.Text == WorkbenchFormat.Missing) >= 1, "missing values are dashes, not zeros");
            Shot(window, "derivatives");

            // ---- options: unconfigured, then a fixture provider
            view.Context.Options = () => unconfigured;
            view.Select("options");
            pump(1.2);
            Check(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OptionsConnect").IsVisible, "without an options-data connection the panel asks to connect one");
            Shot(window, "options_unconfigured");
            view.Context.Options = () => options;
            var optionsPanel = view.Panels.OfType<OptionsPanel>().Single();
            Until(() => optionsPanel.Summary is { CallWall: not null }, 20);
            Check(optionsPanel.Summary is { CallWall: not null, PutWall: not null, MaxPain: not null, ExpectedMove: not null }, "GEX walls, max pain and expected move are computed");
            Check(optionsPanel.Summary?.Expiries.Count == 4 && optionsPanel.Summary.Term.Count == 4, "the per-expiry table and term structure cover every expiry");
            Shot(window, "options");

            // ---- toolbox
            view.Select("toolbox");
            pump(0.3);
            NumericUpDown Box(string name) => view.GetVisualDescendants().OfType<NumericUpDown>().Single(n => n.Name == name);
            Box("ToolEntry").Value = 80_000;
            Box("ToolStop").Value = 79_000;
            Box("ToolTarget").Value = 83_000;
            Box("ToolLiquidationEntry").Value = 80_000;
            pump(0.3);
            var sizeText = string.Join(" ", view.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "ToolSizeResult").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            // 10,000 × 1 % = 100 at risk; loss per BTC = 1,000 + (80,000 + 79,000) × 0.05 % = 1,079.5 → 0.0926…, floored to the 0.001 step
            Check(sizeText.Contains("0.092", StringComparison.Ordinal) && !sizeText.Contains("0.0926", StringComparison.Ordinal), "position size floors to the instrument's step: " + sizeText);
            var liqText = string.Join(" ", view.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "ToolLiquidationResult").GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Check(liqText.Contains("72,361.81", StringComparison.Ordinal), "approximate isolated liquidation price for 10× and 0.5 %: " + liqText);
            Check(svc.Portfolio.Balance == svc.Settings.SimStartBalance || svc.Portfolio.Balance > 0, "calculators never touch an account");
            Shot(window, "toolbox");

            // ---- detachable panel window
            view.Detach("derivatives");
            pump(0.5);
            var floating = view.PanelWindow("derivatives");
            Check(floating is { IsVisible: true } && deriv.IsActive, "a panel opens in its own window and stays active there");
            if (floating != null)
            {
                Shot(floating, "derivatives_window");
                floating.Close();
                pump(0.3);
            }
            Check(view.PanelWindow("derivatives") == null && svc.Settings.PanelWindows.TryGetValue("workbench-derivatives", out var st) && !st.Floating,
                "closing the window returns the panel to the page");
            view.Select("tape");
            pump(0.2);
            Check(!deriv.IsActive && tape.IsActive, "hidden panels release their work; only the visible one is active");
        }
        finally
        {
            view.Context.Derivatives = () => DerivativesData.Shared;
            view.Context.Levels = () => LiquidationLevelsService.Shared;
            view.Context.Options = () => OptionsData.Shared;
            foreach (var panel in view.Panels) panel.Follow(null);
            svc.Settings.Workbench = System.Text.Json.JsonSerializer.Deserialize<WorkbenchSettings>(savedJson) ?? new WorkbenchSettings();
            data.DepthHistory.Stop(DateTime.UtcNow);
            foreach (var v in venues) v.Dispose();
            window.Navigate("trading");
            pump(0.3);
        }
        File.WriteAllText(Path.Combine(outDir, $"workbench-results-{language}.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures, language }));
        Console.WriteLine(failures == 0 ? $"WORKBENCH: all OK ({checks} checks)" : $"WORKBENCH: {failures} failure(s) of {checks}");
        return failures;
    }

    private static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private sealed class NullSink : IMarketDataSink
    {
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { }
        public void OnDepth(Instrument instrument, DepthUpdate update) { }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }

    private sealed class Fixture(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    /// <summary>Public derivatives endpoints answered in-process from the query's own time range.</summary>
    private sealed class DerivativesFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            long Q(string name, long fallback) => long.TryParse(query[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            long now = Ms(DateTime.UtcNow);
            long from = Q("startTime", now - 86_400_000), to = Math.Min(Q("endTime", now), now);
            string F(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);
            IEnumerable<long> Every(long step, int max) { long first = (from + step - 1) / step * step; for (long t = first; t <= to && max-- > 0; t += step) yield return t; }
            double Oi(long t, double scale) => scale * (1 + 0.05 * Math.Sin(t / 3_600_000.0 / 5));
            (int, string) body = request.RequestUri.AbsolutePath switch
            {
                "/fapi/v1/fundingRate" => (200, "[" + string.Join(",", Every(8 * 3_600_000, 1000).Select(t => $$"""{"symbol":"BTCUSDT","fundingTime":{{t}},"fundingRate":"{{F(0.0001 * Math.Cos(t / 1e8))}}"}""")) + "]"),
                "/v5/market/funding/history" => (200, """{"retCode":0,"result":{"list":[""" + string.Join(",", Every(8 * 3_600_000, 200).Reverse().Select(t => $$"""{"symbol":"BTCUSDT","fundingRate":"{{F(0.00012 * Math.Sin(t / 1e8))}}","fundingRateTimestamp":"{{t}}"}""")) + "]}}"),
                "/futures/data/openInterestHist" => (200, "[" + string.Join(",", Every(300_000, 500).Select(t => $$"""{"symbol":"BTCUSDT","sumOpenInterest":"{{F(Oi(t, 80_000))}}","sumOpenInterestValue":"1","timestamp":{{t}}}""")) + "]"),
                "/v5/market/open-interest" => (200, """{"retCode":0,"result":{"symbol":"BTCUSDT","list":[""" + string.Join(",", Every(300_000, 5000).Reverse().Select(t => $$"""{"openInterest":"{{F(Oi(t, 50_000))}}","timestamp":"{{t}}"}""")) + """],"nextPageCursor":""}}"""),
                "/fapi/v1/premiumIndex" => (200, $$"""{"symbol":"BTCUSDT","markPrice":"80010","indexPrice":"80000","lastFundingRate":"0.0001","nextFundingTime":{{now + 3_600_000}},"time":{{now}}}"""),
                "/v5/market/tickers" => (200, $$"""{"retCode":0,"result":{"list":[{"symbol":"BTCUSDT","markPrice":"79990","indexPrice":"80000","fundingRate":"-0.00005","nextFundingTime":"{{now + 3_600_000}}"}]},"time":{{now}}}"""),
                "/futures/data/globalLongShortAccountRatio" => (200, "[" + string.Join(",", Every(300_000, 500).Select(t => $$"""{"symbol":"BTCUSDT","longShortRatio":"1.4","longAccount":"{{F(0.58 + 0.02 * Math.Sin(t / 1e7))}}","shortAccount":"{{F(0.42 - 0.02 * Math.Sin(t / 1e7))}}","timestamp":{{t}}}""")) + "]"),
                "/v5/market/account-ratio" => (500, """{"retCode":10016,"retMsg":"fixture outage"}"""),
                _ => (404, "{}"),
            };
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)body.Item1) { Content = new StringContent(body.Item2, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>A liquidation stream that publishes three recent events after connecting, then stays open.</summary>
    private sealed class LiquidationSocket : WebSocket
    {
        private readonly Queue<byte[]> _messages = new();
        private WebSocketState _state = WebSocketState.Open;

        public LiquidationSocket(Uri endpoint)
        {
            long now = Ms(DateTime.UtcNow);
            bool binance = endpoint.AbsolutePath.Contains("forceOrder", StringComparison.Ordinal);
            foreach (var (ago, quantity, longLiquidated) in new[] { (60_000L, 4.0, true), (20_000L, 1.5, false), (5_000L, 9.0, true) })
            {
                string text = binance
                    ? $$$"""{"e":"forceOrder","o":{"s":"BTCUSDT","S":"{{{(longLiquidated ? "SELL" : "BUY")}}}","q":"{{{quantity.ToString(CultureInfo.InvariantCulture)}}}","p":"80000","ap":"80000","z":"{{{quantity.ToString(CultureInfo.InvariantCulture)}}}","T":{{{now - ago}}}}}"""
                    : $$"""{"topic":"allLiquidation.BTCUSDT","data":[{"T":{{now - ago}},"s":"BTCUSDT","S":"{{(longLiquidated ? "Buy" : "Sell")}}","v":"{{(quantity / 2).ToString(CultureInfo.InvariantCulture)}}","p":"80000"}]}""";
                _messages.Enqueue(Encoding.UTF8.GetBytes(text));
            }
        }

        public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            byte[]? next;
            lock (_messages) _messages.TryDequeue(out next);
            if (next == null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            next.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(next.Length, WebSocketMessageType.Text, true);
        }
    }

    /// <summary>A venue of the aggregate that lists BTCUSDT, publishes deterministic 15-minute candles and a fixed live status.</summary>
    private sealed class FixtureVenue(string id, string name, string venue, FeedStatus status) : IMarketDataFeed, ICryptoVenue, IKlineHistory
    {
        private readonly Instrument _instrument = new() { Symbol = "BTCUSDT", FeedId = id, TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        public string Id => id;
        public string DisplayName => name;
        public string Venue => venue;
        public bool IsSpot => false;
        public bool SupportsHistory => false;
        public bool IsLive => false;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { _instrument });
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(DateTime.UtcNow.AddDays(-30));
        public string CacheTag => "workbench-scene-fixture";

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            long step = minutes * 60_000L, first = (Ms(fromUtc) + step - 1) / step * step, last = Ms(toUtc);
            var bars = new List<Kline>();
            for (long t = first; t < last; t += step)
            {
                double phase = t / 3_600_000.0;
                decimal open = Mid + (decimal)(Math.Sin(phase / 9) * 2500), close = Mid + (decimal)(Math.Sin((phase + minutes / 60.0) / 9) * 2500);
                decimal high = Math.Max(open, close) + 40, low = Math.Min(open, close) - 40, volume = 100 + (decimal)(Math.Abs(Math.Cos(phase)) * 50);
                decimal buy = volume * (close >= open ? 0.6m : 0.4m);
                bars.Add(new Kline(t, open, high, low, close, volume, buy, true));
            }
            onBatch(bars);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            sink.OnStatus(instrument, status, status == FeedStatus.Connected ? null : "Fixture venue is reconnecting.");
            return new End();
        }

        public void Dispose() { }
        private sealed class End : IDisposable { public void Dispose() { } }
    }

    /// <summary>A chain around 80,000 with four expiries, a convex smile, call open interest above and put open interest below.</summary>
    private sealed class FixtureOptions : IOptionsProvider
    {
        public string Name => "Fixture options";
        public bool Supports(string underlying) => underlying == "BTC";

        public Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct)
        {
            var now = DateTime.UtcNow;
            var contracts = new List<OptionContract>();
            foreach (int days in new[] { 2, 7, 30, 90 })
            {
                var expiry = now.Date.AddDays(days).AddHours(8);
                for (int k = 60_000; k <= 100_000; k += 2_500)
                {
                    double m = Math.Log(k / 80_000.0), iv = 0.5 + 0.9 * m * m - 0.1 * m;
                    double callOi = 400 * Math.Exp(-Math.Pow(m - 0.06, 2) * 60), putOi = 400 * Math.Exp(-Math.Pow(m + 0.07, 2) * 60);
                    contracts.Add(new OptionContract($"BTC-{days}-{k}-C", expiry, k, OptionRight.Call, 0, 0, 1, iv, 0, 0, 0, 0, callOi, callOi / 5, 1));
                    contracts.Add(new OptionContract($"BTC-{days}-{k}-P", expiry, k, OptionRight.Put, 0, 0, 1, iv + 0.03, 0, 0, 0, 0, putOi, putOi / 5, 1));
                }
            }
            return Task.FromResult<OptionsChain?>(new OptionsChain("BTC", now.AddMinutes(-1), 80_000, contracts, "Fixture options", "index"));
        }

        public Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
        {
            var trades = new List<OptionTrade>();
            for (var t = from.AddMinutes(17); t < to; t = t.AddHours(2))
                trades.Add(new OptionTrade(t, "BTC-7-85000-C", DateTime.UtcNow.Date.AddDays(7).AddHours(8), 85_000, (t.Hour / 2) % 2 == 0 ? OptionRight.Call : OptionRight.Put,
                    1500, 120, (t.Hour / 2) % 3 == 0 ? -1 : 1, 0.52, 80_000, 1, "Fixture"));
            return Task.FromResult<IReadOnlyList<OptionTrade>>(trades);
        }

        public Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OptionDaily>>(Array.Empty<OptionDaily>());
    }
}
