using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;
using TradingHub.Feeds.Venues;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

/// <summary>
/// The order-flow, book and derivatives library studies through the real catalog, library groups, settings window, chart
/// renderer and the third-party data opt-in switch. Isolated simulated prints on a fake aggregate; derivatives and the
/// third-party levels service use offline fixtures (no network). Repeat with TRADINGHUB_LANG=es and zh-CN.
/// </summary>
internal static class LibraryFlowScene
{
    private static readonly Type[] Studies =
    {
        typeof(FlowByExchange), typeof(BarStatsStrip), typeof(PositioningNlNs), typeof(LiquidityTracker), typeof(BookMirror), typeof(LiquidationLevels),
        typeof(PrintAbsorption), typeof(TrappedTraders), typeof(HighlightsAndNotifications), typeof(SpotPerpetualDominance), typeof(DecisionBoard),
        typeof(DeltaDeviationBands), typeof(VolumeConcentrationBubbles), typeof(FootprintImbalanceAlerts), typeof(LiquidityImbalanceOscillator),
        typeof(OrderBookPressure), typeof(LeverageLiquidationBands), typeof(EstimatedLiquidationMap), typeof(LiquidationBursts),
    };

    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        int checks = 0, failures = 0;
        void Check(bool value, string description)
        {
            checks++;
            Console.WriteLine($"{(value ? "OK" : "FAIL")} Library flow: {description}");
            if (!value) failures++;
        }
        string language = Lang.Current;

        // Library registration: every study is in the catalog and lands in its order-flow, book or derivatives group.
        foreach (var type in Studies)
        {
            var info = App.Services.Catalog.Find(type.FullName!);
            Check(info?.Type == type, "the real catalog exposes " + type.Name);
            if (info == null) continue;
            string group = IndicatorLibrary.DefaultGroupOf(info);
            Check(group is IndicatorCategories.OrderFlow or IndicatorCategories.OrderBook or IndicatorCategories.Derivatives, $"{type.Name} is grouped under {group}");
            string name = Indicator.DefaultName(type);
            if (language != Lang.English) Check(Lang.T(name) != name, $"{type.Name} display name is translated in {language}: {Lang.T(name)}");
        }

        // Offline fixtures: a derivatives service whose transport always fails, and a third-party levels fixture.
        using var derivatives = new DerivativesData(new DerivativesTransport(new HttpClient(new Offline()), OwnsHttp: true,
            ConnectWebSocket: (_, _) => Task.FromException<WebSocket>(new WebSocketException("Offline fixture."))));
        var start = DateTime.UtcNow.AddHours(-2);
        start = new DateTime(start.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        long asOf = new DateTimeOffset(DateTime.UtcNow.AddMinutes(-10)).ToUnixTimeMilliseconds();
        string levelsJson = $$"""{"v":2,"coin":"BTC","attribution":"Fixture attribution","snapshot":{"asOf":{{asOf}}},"mark":80000,"positions":42,"step":10,"bins":[[7995,900000,0,0,0,3,0,600000,0],[7997,400000,0,0,0,2,0,100000,0],[8001,0,0,700000,200000,0,4,0,300000],[8002,0,0,300000,0,0,1,0,100000]]}""";
        using var levelsService = new LiquidationLevelsService(new HttpClient(new Fixture(levelsJson)), ownsHttp: true);

        var venues = new[] { new FakeVenue("binance-futures", "Binance Futures", "Binance", false), new FakeVenue("bybit-linear", "Bybit", "Bybit", false), new FakeVenue("binance-spot", "Binance Spot", "Binance", true) };
        using var feed = new AggregateFeed(venues);
        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = AggregateFeed.FeedId, TickSize = 0.1m, VolumeStep = 0.001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        using var data = new InstrumentData(instrument, feed) { RecordLive = false };
        long id = 0;
        var random = new Random(7);
        decimal price = 80000;
        for (int i = 0; i < 2400; i++)
        {
            var time = start.AddSeconds(i * 3);
            int source = i % 3;
            bool buy = source switch { 0 => random.Next(10) < 6, 1 => random.Next(10) < 4, _ => random.Next(2) == 0 };
            price += buy ? 0.5m : -0.5m;
            decimal size = random.Next(25) == 0 ? 8 : 0.05m * (1 + random.Next(10));
            data.Trades.Add(new Trade(++id, time, price, size, buy ? TradeSide.Buy : TradeSide.Sell, new TradeProvenance(DataQuality.Raw, SourceIdentity: venues[source].Id)));
        }
        using var model = new ChartModel(data, ChartPeriod.M5, new ChartSettings { TimeZoneId = "UTC", Style = ChartStyle.Candles });
        var view = new ChartView { Model = model };
        var fixture = new Window { Title = "Order-flow library — isolated fixture", Width = 1400, Height = 1000, Content = view };
        fixture.Show(owner); pump(0.15);
        try
        {
            var flow = new FlowByExchange { Ranking = FlowByExchange.RankWindow.Visible };
            var stats = new BarStatsStrip { Trades = true, DeltaWhales = true };
            var levels = new LiquidationLevels { Service = levelsService };
            var board = new DecisionBoard { Service = derivatives };
            var studies = new Indicator[]
            {
                flow, stats, new SpotPerpetualDominance(), levels, board, new PositioningNlNs { Service = derivatives }, new EstimatedLiquidationMap { Service = derivatives },
                new LiquidationBursts { Service = derivatives }, new PrintAbsorption { ThresholdNotional = 100_000 }, new TrappedTraders(), new HighlightsAndNotifications(),
                new VolumeConcentrationBubbles(), new FootprintImbalanceAlerts(), new LeverageLiquidationBands(),
            };
            var slots = studies.Select(s => model.AddIndicator(s, applyDefaultColors: false)).ToList();
            model.Update(); view.InvalidateVisual(); pump(0.3);
            Check(slots.All(s => s.Error == null), "all studies calculate through the actual ChartModel: " + string.Join("; ", slots.Where(s => s.Error != null).Select(s => s.Error)));
            Check(model.InstrumentInfo.Sources.Count == 3, "the aggregate exposes three markets to the studies");

            // Wait for the fixture snapshot of third-party levels (it never touches the network).
            var until = DateTime.UtcNow.AddSeconds(10);
            while (levelsService.Latest.Snapshot == null && DateTime.UtcNow < until) { model.Update(); view.InvalidateVisual(); pump(0.05); }
            Check(levelsService.Latest.Snapshot?.Bins.Count == 4, "the third-party levels study reads its labelled fixture snapshot");
            Check(levels.GetAlertLevels().Any(l => l.Name.Contains("third-party estimate", StringComparison.Ordinal)), "third-party levels are alertable and labelled as estimates");
            pump(0.2);
            using (var frame = fixture.CaptureRenderedFrame())
            {
                Check(frame != null, "the library studies render in an actual chart frame");
                frame?.Save(Path.Combine(outDir, $"library_flow_chart_{language}.png"));
            }
            Check(flow.RankedExchanges.SequenceEqual(new[] { "Binance", "Bybit" }), "the flow column ranks exchanges by traded notional: " + string.Join(", ", flow.RankedExchanges));
            var result = board.Evaluate(model.Series.Count - 1);
            Check(result.Signs[4] == null && result.Readings[4] == "—", "the decision board leaves unavailable open interest out of the score");

            // Settings window: translated option labels of a new study are real editors.
            var settings = new IndicatorsWindow(App.Services, model);
            settings.Show(fixture); pump(0.15);
            try
            {
                var slot = slots[0];
                var list = settings.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.Items.OfType<ListBoxItem>().Any(i => ReferenceEquals(i.Tag, slot)));
                if (list != null) { list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(i => ReferenceEquals(i.Tag, slot)); pump(0.1); }
                foreach (var expander in settings.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
                pump(0.1);
                var property = typeof(FlowByExchange).GetProperty(nameof(FlowByExchange.Ranking))!;
                string label = Lang.T(PropertyGrid.NameOf(property));
                if (language != Lang.English) Check(label != PropertyGrid.NameOf(property), $"Ranking label is translated in {language}: {label}");
                var grid = settings.GetVisualDescendants().OfType<PropertyGrid>().SingleOrDefault();
                Check(grid?.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == label) == true, "the flow column exposes its ranking window");
                using var frame = settings.CaptureRenderedFrame();
                frame?.Save(Path.Combine(outDir, $"library_flow_settings_{language}.png"));
            }
            finally { settings.Close(); }
        }
        finally
        {
            fixture.Close();
            foreach (var v in venues) v.Dispose();
        }

        // Third-party data stays opt-in: the switch is off by default and controls whether the service exists at all.
        bool previous = App.Services.Settings.ThirdPartyLiquidationLevels;
        owner.Navigate("settings"); pump(0.2);
        var page = owner.Page("settings")!;
        var toggle = page.GetVisualDescendants().OfType<ToggleSwitch>().SingleOrDefault(t => t.Name == "ThirdPartyLiquidationLevels");
        Check(toggle != null, "settings expose the third-party liquidation-levels switch");
        if (toggle != null)
        {
            Check(toggle.IsChecked == false && LiquidationLevelsService.Shared == null, "third-party liquidation levels are off by default and no client exists");
            toggle.BringIntoView(); pump(0.1);
            using (var frame = owner.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, $"library_flow_settings_third_party_{language}.png"));
            toggle.IsChecked = true; pump(0.05);
            Check(App.Services.Settings.ThirdPartyLiquidationLevels && LiquidationLevelsService.Shared != null, "turning the switch on creates the client (fetching happens only from the study)");
            toggle.IsChecked = false; pump(0.05);
            Check(!App.Services.Settings.ThirdPartyLiquidationLevels && LiquidationLevelsService.Shared == null, "turning the switch off removes the client");
            toggle.IsChecked = previous;
        }
        File.WriteAllText(Path.Combine(outDir, $"library-flow-results-{language}.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures, language }));
        return failures;
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline fixture."));
    }

    private sealed class Fixture(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private sealed class FakeVenue(string id, string name, string venue, bool spot) : IMarketDataFeed, ICryptoVenue
    {
        public string Id => id;
        public string DisplayName => name;
        public string Venue => venue;
        public bool IsSpot => spot;
        public bool SupportsHistory => false;
        public bool IsLive => false;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(Array.Empty<Instrument>());
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => new End();
        public void Dispose() { }
        private sealed class End : IDisposable { public void Dispose() { } }
    }
}
