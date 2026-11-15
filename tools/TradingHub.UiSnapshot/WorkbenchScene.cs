// Scene "workbench": every Workbench panel through the real page, its detachable windows and its persisted settings,
// on offline fixtures only (no network): a fake three-venue aggregate with recent prints and recorded depth, a
// derivatives service whose HTTP and liquidation sockets are in-process fixtures (Bybit long/short fails on purpose so
// the gap shows), a third-party levels fixture, an options provider that publishes DVOL (with gaps), a chain-only
// provider and an unconfigured options hub; venue candles drift upward so closed bars confirm structure breaks.
// Repeat with TRADINGHUB_LANG=es and zh-CN.
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
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
            OkxRest: new Uri("https://okx.fixture"), BitgetRest: new Uri("https://bitget.fixture"), HyperliquidRest: new Uri("https://hyperliquid.fixture"),
            ConnectWebSocket: (uri, _) => Task.FromResult<WebSocket>(new LiquidationSocket(uri))));
        long asOf = new DateTimeOffset(now.AddMinutes(-10)).ToUnixTimeMilliseconds();
        string levelsJson = $$"""{"v":2,"coin":"BTC","attribution":"Fixture attribution","snapshot":{"asOf":{{asOf}}},"mark":80000,"positions":42,"step":100,"bins":[[770,900000,0,0,0,3,0,600000,0],[775,400000,0,0,0,2,0,100000,0],[790,300000,0,0,0,2,0,100000,0],[815,0,0,700000,200000,0,4,0,300000],[825,0,0,300000,0,0,1,0,100000]]}""";
        using var levels = new LiquidationLevelsService(new HttpClient(new Fixture(levelsJson)), ownsHttp: true);
        var options = new OptionsData(null) { Provider = new FixtureIndexedOptions() };
        var chainOnly = new OptionsData(null) { Provider = new FixtureOptions() };
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

        // ---- untranslated English: every visible text, tooltip, window title and canvas label of each panel
        var probe = new EnglishProbe(language, new[]
        {
            instrument.Symbol, instrument.Exchange, feed.DisplayName, WorkbenchFormat.Venue(DerivativesData.Binance), WorkbenchFormat.Venue(DerivativesData.Bybit),
            "Fixture options", "Fixture chain", "Fixture attribution", "fixture outage",
        }.Concat(venues.SelectMany(v => new[] { v.DisplayName, v.Venue })));
        int inspectedTexts = 0, drawnTexts = 0;
        IEnumerable<string> Visible(Visual root)
        {
            if (root is Window { Title: { Length: > 0 } title }) yield return title;
            foreach (var visual in root.GetVisualDescendants())
            {
                if (visual is not Control { IsEffectivelyVisible: true } control) continue;
                if (control is TextBlock { Text: { Length: > 0 } text }) yield return text;
                if (ToolTip.GetTip(control) is string { Length: > 0 } tip) yield return tip;
            }
        }
        void NoEnglish(string where, IReadOnlyCollection<string> texts)
        {
            if (language == Lang.English) return;
            var bad = texts.Distinct().Select(t => (Text: t, Words: probe.Find(t))).Where(x => x.Words.Count > 0).ToList();
            Check(bad.Count == 0, $"{where}: no untranslated English in {texts.Count} {language} texts"
                + (bad.Count == 0 ? "" : ": " + string.Join(" | ", bad.Take(10).Select(b => $"\"{b.Text}\" [{string.Join(", ", b.Words)}]"))));
        }
        // Captures the surface, then checks its controls and everything its canvases drew in that frame.
        void Inspect(TopLevel target, Visual root, string name, bool canvases = true)
        {
            var drawn = new System.Collections.Concurrent.ConcurrentQueue<string>();
            WorkbenchCanvas.DrawnText = drawn;
            foreach (var canvas in root.GetVisualDescendants().OfType<WorkbenchCanvas>()) canvas.InvalidateVisual();
            try { Shot(target, name); }
            finally { WorkbenchCanvas.DrawnText = null; }
            var texts = Visible(root).ToList();
            inspectedTexts += texts.Count;
            drawnTexts += drawn.Count;
            if (canvases) Check(drawn.Count > 0, $"{name}: the panel's canvases drew labels ({drawn.Count})");
            NoEnglish(name + " controls", texts);
            NoEnglish(name + " canvases", drawn.ToArray());
        }
        if (language != Lang.English)
            Check(probe.Find("Coverage: OKX reconnecting").Count > 0 && probe.Find("Liquidations from").Count > 0
                  && probe.Find(Lang.T("Aggregated trades")).Count == 0 && probe.Find("BTCUSDT · Binance Futures 80,000.5 · OKX 1.2K/3.4M · 10×").Count == 0,
                "the untranslated-English detector flags English and accepts data, symbols and venue names");

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
            Inspect(window, view, "tape");

            // ---- depth heat map, with recording and without
            view.Select("heatmap");
            var heat = view.Panels.OfType<HeatmapPanel>().Single();
            Until(() => heat.Canvas.LastSpans > 0, 10);
            Check(heat.Canvas.LastSpans > 0, $"the heat map paints recorded depth ({heat.Canvas.LastSpans} spans)");
            Check(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeatmapRecordingOff").IsVisible == false, "no recording-off banner while depth is recorded");
            Inspect(window, view, "heatmap");
            view.Apply(new WorkbenchSelection("fixture-aggregate", instrument, noDepth));
            Until(() => view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeatmapRecordingOff").IsVisible, 5);
            Check(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "HeatmapRecordingOff").IsVisible, "with depth recording off the heat map says so instead of showing an empty map");
            Check(!svc.Settings.RecordDepth, "the Workbench never turns depth recording on");
            Inspect(window, view, "heatmap_recording_off");
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
            Inspect(window, view, "liquidity");

            // ---- derivatives dashboard
            view.Select("derivatives");
            var deriv = view.Panels.OfType<DerivativesPanel>().Single();
            // all five venues post together; the funding board reads each venue's bulk endpoint
            Until(() => deriv.Inputs.Count == 5 && deriv.Boards.Count == 5 && deriv.Flow != null, 30);
            pump(2.5);
            Check(deriv.Inputs.Count == 5 && DerivativesData.PerpetualVenues.All(deriv.Inputs.ContainsKey), "all five venues load: " + string.Join(", ", deriv.Inputs.Keys));
            Check(deriv.Inputs.TryGetValue(DerivativesData.Binance, out var binance) && binance.Funding != null && binance.OpenInterest != null && binance.Stats != null && binance.LongShort != null,
                "Binance funding, open interest, premium and long/short load");
            Check(deriv.Inputs.TryGetValue(DerivativesData.Bybit, out var bybit) && bybit.LongShort == null && bybit.Errors.Count > 0,
                "a failing Bybit long/short request stays a reported gap: " + string.Join(" | ", bybit?.Errors ?? Array.Empty<string>()));
            Check(new[] { DerivativesData.Okx, DerivativesData.Bitget, DerivativesData.Hyperliquid }.All(v => deriv.Inputs.TryGetValue(v, out var i) && i.Funding is { Samples.Count: > 0 } && i.Stats != null && i.Errors.Count == 0),
                "OKX, Bitget and Hyperliquid funding and premium load from their published shapes: "
                + string.Join(" | ", deriv.Inputs.Values.SelectMany(i => i.Errors)));
            var table = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "DerivativesTable");
            Check(table.Children.OfType<TextBlock>().Count(t => t.Text == WorkbenchFormat.Missing) >= 1, "missing values are dashes, not zeros");
            string? TableCell(string venueName, int column)
            {
                int? row = table.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == 0 && t.Text == venueName) is { } name ? Grid.GetRow(name) : null;
                return row == null ? null : table.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetRow(t) == row && Grid.GetColumn(t) == column)?.Text;
            }
            Check(TableCell(WorkbenchFormat.Venue(DerivativesData.Hyperliquid), 9) == Lang.T("not published"),
                "Hyperliquid's long/short ratio says 'not published' instead of a value: " + TableCell(WorkbenchFormat.Venue(DerivativesData.Hyperliquid), 9));
            var board = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "DerivativesFundingBoard");
            var boardCoins = board.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Parent is Border b && Grid.GetColumn(b) == 0 && Grid.GetRow(b) > 0).Select(t => t.Text).ToArray();
            Check(boardCoins.SequenceEqual(DerivativesDashboard.BoardCoins) && deriv.Boards.Values.All(b => b is { Count: 5 })
                  && !board.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Lang.T("failed")),
                $"the funding board lists five coins with every venue's bulk rate ({string.Join(", ", boardCoins)})");
            var liquidationFeed = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "DerivativesLiquidationFeed");
            string feedCoverage = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "DerivativesLiquidationCoverage").Text ?? "";
            Check(liquidationFeed.RowDefinitions.Count > 1 && liquidationFeed.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Lang.T(WorkbenchFormat.Venue(DerivativesData.Binance)))
                  && feedCoverage.Contains(Lang.T($"Liquidation stream observed: {WorkbenchFormat.Venue(DerivativesData.Binance)}"), StringComparison.Ordinal),
                $"the liquidation feed lists observed liquidations and names the observing streams ({liquidationFeed.RowDefinitions.Count - 1} rows): {feedCoverage}");
            Check(deriv.Flow is { FirstTrade: not null, MeasuredVolume: > 0 } flow && flow.Price.Any(p => p != null) && flow.Cvd.Any(c => c != null),
                $"price and CVD are computed from the instrument's measured trades ({deriv.Flow?.MeasuredVolume} measured)");
            Inspect(window, view, "derivatives");

            // ---- options: unconfigured, then a fixture provider
            view.Context.Options = () => unconfigured;
            view.Select("options");
            pump(1.2);
            Check(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "OptionsConnect").IsVisible, "without an options-data connection the panel asks to connect one");
            Inspect(window, view, "options_unconfigured", canvases: false);
            var optionsPanel = view.Panels.OfType<OptionsPanel>().Single();
            TextBlock DvolStatus() => view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "OptionsDvolStatus");
            Control DvolCanvas() => view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "OptionsDvol");
            string noIndex = Lang.T($"Volatility index (DVOL): not provided by this source ({chainOnly.Provider!.Name}).");
            view.Context.Options = () => chainOnly;
            Until(() => DvolStatus().Text == noIndex, 20);
            Check(DvolStatus().Text == noIndex && !DvolCanvas().IsVisible && optionsPanel.DvolWindow == null,
                "a source without a volatility index says so instead of drawing one: " + DvolStatus().Text);
            view.Context.Options = () => options;
            Until(() => optionsPanel.Summary is { CallWall: not null } && optionsPanel.DvolWindow != null, 20);
            Check(optionsPanel.Summary is { CallWall: not null, PutWall: not null, MaxPain: not null, ExpectedMove: not null }, "GEX walls, max pain and expected move are computed");
            Check(optionsPanel.Summary?.Expiries.Count == 4 && optionsPanel.Summary.Term.Count == 4, "the per-expiry table and term structure cover every expiry");
            // the fixture skips two days and publishes one NaN close: three gaps, never zeros
            Check(optionsPanel.DvolWindow is { MissingDays: >= 3, Today: not null } dvol && dvol.Closes.Count(c => c == null) == dvol.MissingDays && dvol.Closes.All(c => c is null or > 0)
                  && DvolCanvas().IsVisible,
                $"the published DVOL window shows its missing days as gaps ({optionsPanel.DvolWindow?.MissingDays} missing) and today's value separately");
            Inspect(window, view, "options");

            // ---- toolbox
            view.Select("toolbox");
            var toolbox = view.Panels.OfType<ToolboxPanel>().Single();
            Until(() => toolbox.StructureResult != null, 30);
            pump(0.3);
            var structure = toolbox.StructureResult;
            var structureRows = view.GetVisualDescendants().OfType<StackPanel>().Single(s => s.Name == "ToolStructureResult").GetVisualDescendants().OfType<TextBlock>().Count(t => !string.IsNullOrEmpty(t.Text));
            Check(structure is { ClosedBars: > 0, LastClose: { } lastClose, LastBreak: { } lastBreak } && lastBreak.ConfirmedAt <= lastClose && lastBreak.FormedAt <= lastBreak.ConfirmedAt && lastClose <= DateTime.UtcNow
                  && structureRows > 0,
                $"the structure summary lists breaks confirmed by closed bars ({structure?.ClosedBars} bars, last break {structure?.LastBreak?.Kind} confirmed {structure?.LastBreak?.ConfirmedAt:u}, {structureRows} rows)");
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
            Inspect(window, view, "toolbox", canvases: false);

            // ---- detachable panel window
            view.Detach("derivatives");
            pump(0.5);
            var floating = view.PanelWindow("derivatives");
            Check(floating is { IsVisible: true } && deriv.IsActive, "a panel opens in its own window and stays active there");
            if (floating != null)
            {
                Inspect(floating, floating, "derivatives_window");
                Check(floating.Title == string.Join(" · ", Lang.T(deriv.Title), Lang.T("Workbench")), "the panel window's title is translated: " + floating.Title);
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
        File.WriteAllText(Path.Combine(outDir, $"workbench-results-{language}.json"), System.Text.Json.JsonSerializer.Serialize(new { checks, failures, language, inspectedTexts, drawnTexts }));
        Console.WriteLine(failures == 0 ? $"WORKBENCH: all OK ({checks} checks, {inspectedTexts} control texts, {drawnTexts} canvas labels)" : $"WORKBENCH: {failures} failure(s) of {checks}");
        return failures;
    }

    /// <summary>
    /// Finds untranslated English in displayed text. Its vocabulary is the running language's own catalog: words of
    /// the English source keys that no translation keeps (so brand names, units and loan words the translators kept
    /// pass), plus English function words that never belong in a Spanish or Chinese sentence. All-capital tokens
    /// (symbols, acronyms such as BTC, OI or GEX), numbers and the fixtures' own data names are data, not English.
    /// A text that is itself an English catalog key with a different translation is untranslated as a whole.
    /// </summary>
    private sealed class EnglishProbe
    {
        private static readonly Regex Word = new(@"[^\W\d_]+", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> Function = new(StringComparer.Ordinal)
        {
            "the", "and", "of", "to", "is", "are", "was", "were", "for", "with", "from", "at", "by", "this", "that", "these", "those",
            "not", "only", "than", "when", "while", "after", "before", "since", "until", "below", "above", "yet", "never", "into",
            "which", "there", "their", "its", "have", "been", "will", "would", "can", "could", "should", "or", "in", "on", "it", "be", "an", "as", "if",
        };
        private readonly Dictionary<string, string> _catalog = new(StringComparer.Ordinal);
        private readonly HashSet<string> _english = new(StringComparer.Ordinal);
        private readonly HashSet<string> _data = new(StringComparer.Ordinal);

        public EnglishProbe(string language, IEnumerable<string> data)
        {
            if (language == Lang.English) return;
            using var stream = typeof(Lang).Assembly.GetManifestResourceStream($"TradingHub.App.Localization.{language}.json")
                ?? throw new InvalidOperationException("Missing catalog " + language);
            foreach (var (key, value) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!)
                _catalog[key.Replace("{{", "{").Replace("}}", "}")] = value.Replace("{{", "{").Replace("}}", "}");
            var kept = new HashSet<string>(_catalog.Values.SelectMany(Words), StringComparer.Ordinal);
            foreach (var word in _catalog.Keys.SelectMany(Words))
                if (word.Length >= 3 && !kept.Contains(word)) _english.Add(word);
            foreach (var word in data.SelectMany(Words)) _data.Add(word);
        }

        /// <summary>Lower-cased ASCII words that are not all-capital tokens.</summary>
        private static IEnumerable<string> Words(string text)
        {
            foreach (Match match in Word.Matches(text))
            {
                string word = match.Value;
                if (!word.All(char.IsAscii) || word.Length > 1 && word.All(char.IsUpper)) continue;
                yield return word.ToLowerInvariant();
            }
        }

        /// <summary>The English words of a displayed text (empty when it contains none).</summary>
        public List<string> Find(string text)
        {
            var found = new List<string>();
            if (_catalog.TryGetValue(text, out var translated) && translated != text && Word.IsMatch(text)) found.Add("(untranslated text)");
            foreach (var word in Words(text).Distinct())
                if (!_data.Contains(word) && (Function.Contains(word) || _english.Contains(word))) found.Add(word);
            return found;
        }
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

    /// <summary>
    /// Public derivatives endpoints of the five venues answered in-process from the query's own time range, in each
    /// venue's published shape (see DerivativesVenueTests). Bybit long/short fails on purpose; Hyperliquid publishes none.
    /// </summary>
    private sealed class DerivativesFixture : HttpMessageHandler
    {
        private static readonly (string Coin, double Price)[] Coins = { ("BTC", 80_000), ("ETH", 3_000), ("SOL", 150), ("XRP", 0.6), ("DOGE", 0.15) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            long Q(string name, long fallback) => long.TryParse(query[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            long now = Ms(DateTime.UtcNow);
            long from = Q("startTime", Q("begin", now - 86_400_000)), to = Math.Min(Q("endTime", Q("end", now)), now);
            const long Hour = 3_600_000, Settle = 8 * Hour;
            long nextSettle = (now / Settle + 1) * Settle;
            string F(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);
            IEnumerable<long> Every(long step, int max) { long first = (from + step - 1) / step * step; for (long t = first; t <= to && max-- > 0; t += step) yield return t; }
            // newest first, within [lo, hi], at most max rows
            IEnumerable<long> Newest(long step, long lo, long hi, int max) { for (long t = hi / step * step; t >= lo && max-- > 0; t -= step) yield return t; }
            static long Period(string? name) => name is { Length: > 1 } p && long.TryParse(p[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n * (char.ToLowerInvariant(p[^1]) switch { 'm' => 60_000L, 'h' => Hour, 'd' => 24 * Hour, _ => Hour }) : Hour;
            double Oi(long t, double scale) => scale * (1 + 0.05 * Math.Sin(t / 3_600_000.0 / 5));
            string? symbol = query["symbol"], instId = query["instId"];
            (int, string) body = (uri.Host, uri.AbsolutePath) switch
            {
                // ---- Binance USD-M and Bybit linear
                (_, "/fapi/v1/fundingRate") => (200, "[" + string.Join(",", Every(Settle, 1000).Select(t => $$"""{"symbol":"BTCUSDT","fundingTime":{{t}},"fundingRate":"{{F(0.0001 * Math.Cos(t / 1e8))}}"}""")) + "]"),
                (_, "/v5/market/funding/history") => (200, """{"retCode":0,"result":{"list":[""" + string.Join(",", Every(Settle, 200).Reverse().Select(t => $$"""{"symbol":"BTCUSDT","fundingRate":"{{F(0.00012 * Math.Sin(t / 1e8))}}","fundingRateTimestamp":"{{t}}"}""")) + "]}}"),
                (_, "/futures/data/openInterestHist") => (200, "[" + string.Join(",", Every(300_000, 500).Select(t => $$"""{"symbol":"BTCUSDT","sumOpenInterest":"{{F(Oi(t, 80_000))}}","sumOpenInterestValue":"1","timestamp":{{t}}}""")) + "]"),
                (_, "/v5/market/open-interest") => (200, """{"retCode":0,"result":{"symbol":"BTCUSDT","list":[""" + string.Join(",", Every(300_000, 5000).Reverse().Select(t => $$"""{"openInterest":"{{F(Oi(t, 50_000))}}","timestamp":"{{t}}"}""")) + """],"nextPageCursor":""}}"""),
                (_, "/fapi/v1/premiumIndex") when symbol == null => (200, "[" + string.Join(",", Coins.Select(c => $$"""{"symbol":"{{c.Coin}}USDT","markPrice":"{{F(c.Price)}}","indexPrice":"{{F(c.Price)}}","lastFundingRate":"0.0001","nextFundingTime":{{nextSettle}},"time":{{now}}}""")) + "]"),
                (_, "/fapi/v1/premiumIndex") => (200, $$"""{"symbol":"BTCUSDT","markPrice":"80010","indexPrice":"80000","lastFundingRate":"0.0001","nextFundingTime":{{now + 3_600_000}},"time":{{now}}}"""),
                (_, "/fapi/v1/fundingInfo") => (200, "[]"),
                (_, "/v5/market/tickers") when symbol == null => (200, """{"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[""" + string.Join(",", Coins.Select(c => $$"""{"symbol":"{{c.Coin}}USDT","markPrice":"{{F(c.Price)}}","indexPrice":"{{F(c.Price)}}","fundingRate":"-0.00005","nextFundingTime":"{{nextSettle}}","fundingIntervalHour":"8"}""")) + $$"""]},"time":{{now}}}"""),
                (_, "/v5/market/tickers") => (200, $$"""{"retCode":0,"result":{"list":[{"symbol":"BTCUSDT","markPrice":"79990","indexPrice":"80000","fundingRate":"-0.00005","nextFundingTime":"{{now + 3_600_000}}"}]},"time":{{now}}}"""),
                (_, "/futures/data/globalLongShortAccountRatio") => (200, "[" + string.Join(",", Every(300_000, 500).Select(t => $$"""{"symbol":"BTCUSDT","longShortRatio":"1.4","longAccount":"{{F(0.58 + 0.02 * Math.Sin(t / 1e7))}}","shortAccount":"{{F(0.42 - 0.02 * Math.Sin(t / 1e7))}}","timestamp":{{t}}}""")) + "]"),
                (_, "/v5/market/account-ratio") => (500, """{"retCode":10016,"retMsg":"fixture outage"}"""),
                // ---- OKX
                ("okx.fixture", "/api/v5/public/funding-rate-history") => (200, """{"code":"0","data":[""" + string.Join(",", Newest(Settle, now - 10 * 24 * Hour, Math.Min(now, Q("after", long.MaxValue) - 1), 400)
                    .Select(t => $$"""{"formulaType":"withRate","fundingRate":"{{F(0.00008 * Math.Cos(t / 1e8))}}","fundingTime":"{{t}}","instId":"BTC-USDT-SWAP","instType":"SWAP","method":"current_period","realizedRate":""}""")) + """],"msg":""}"""),
                ("okx.fixture", "/api/v5/public/funding-rate") => (200, """{"code":"0","data":[""" + string.Join(",", Coins.Where(c => instId == "ANY" || instId == $"{c.Coin}-USDT-SWAP")
                    .Select(c => $$"""{"formulaType":"withRate","fundingRate":"0.00006","fundingTime":"{{nextSettle}}","instId":"{{c.Coin}}-USDT-SWAP","instType":"SWAP","method":"current_period","nextFundingRate":"","nextFundingTime":"{{nextSettle + Settle}}","premium":"0.0001","ts":"{{now}}"}""")) + """],"msg":""}"""),
                ("okx.fixture", "/api/v5/public/mark-price") => (200, $$"""{"code":"0","data":[{"instId":"BTC-USDT-SWAP","instType":"SWAP","markPx":"80005","ts":"{{now}}"}],"msg":""}"""),
                ("okx.fixture", "/api/v5/market/index-tickers") => (200, $$"""{"code":"0","data":[{"instId":"BTC-USDT","idxPx":"80000","ts":"{{now}}"}],"msg":""}"""),
                ("okx.fixture", "/api/v5/public/open-interest") => (200, $$"""{"code":"0","data":[{"instId":"BTC-USDT-SWAP","instType":"SWAP","oi":"{{F(Oi(now, 3_000_000))}}","oiCcy":"{{F(Oi(now, 30_000))}}","oiUsd":"1","ts":"{{now}}"}],"msg":""}"""),
                ("okx.fixture", "/api/v5/rubik/stat/contracts/open-interest-history") => (200, """{"code":"0","data":[""" + string.Join(",", Newest(Period(query["period"]), from, to, 100)
                    .Select(t => $$"""["{{t}}","{{F(Oi(t, 3_000_000))}}","{{F(Oi(t, 30_000))}}","1"]""")) + """],"msg":""}"""),
                ("okx.fixture", "/api/v5/rubik/stat/contracts/long-short-account-ratio-contract") => (200, """{"code":"0","data":[""" + string.Join(",", Newest(Period(query["period"]), from, to, 100)
                    .Select(t => $$"""["{{t}}","{{F(2 + 0.2 * Math.Sin(t / 1e7))}}"]""")) + """],"msg":""}"""),
                // ---- Bitget
                ("bitget.fixture", "/api/v2/mix/market/contracts") => (200, $$"""{"code":"00000","msg":"success","requestTime":{{now}},"data":[""" + string.Join(",", Coins
                    .Select(c => $$"""{"symbol":"{{c.Coin}}USDT","baseCoin":"{{c.Coin}}","quoteCoin":"USDT","sizeMultiplier":"0.001","symbolType":"perpetual","symbolStatus":"normal","fundInterval":"8"}""")) + "]}"),
                ("bitget.fixture", "/api/v2/mix/market/history-fund-rate") => (200, $$"""{"code":"00000","msg":"success","requestTime":{{now}},"data":[""" + string.Join(",", Newest(Settle, now - 10 * 24 * Hour, now, 100)
                    .Select(t => $$"""{"symbol":"BTCUSDT","fundingRate":"{{F(0.00007 * Math.Sin(t / 1e8))}}","fundingTime":"{{t}}"}""")) + "]}"),
                ("bitget.fixture", "/api/v2/mix/market/current-fund-rate") => (200, $$"""{"code":"00000","msg":"success","requestTime":{{now}},"data":[""" + string.Join(",", Coins.Where(c => symbol == null || symbol == $"{c.Coin}USDT")
                    .Select(c => $$"""{"symbol":"{{c.Coin}}USDT","fundingRate":"0.00004","fundingRateInterval":"8","nextUpdate":"{{nextSettle}}","minFundingRate":"-0.003","maxFundingRate":"0.003"}""")) + "]}"),
                ("bitget.fixture", "/api/v2/mix/market/ticker") => (200, $$"""{"code":"00000","msg":"success","requestTime":{{now}},"data":[{"symbol":"BTCUSDT","lastPr":"80002","ts":"{{now}}","indexPrice":"80000","fundingRate":"0.00004","holdingAmount":"36789.1","markPrice":"80002"}]}"""),
                ("bitget.fixture", "/api/v2/mix/market/open-interest") => (200, $$$"""{"code":"00000","msg":"success","requestTime":{{{now}}},"data":{"openInterestList":[{"symbol":"BTCUSDT","size":"36789.1944"}],"ts":"{{{now}}}"}}"""),
                ("bitget.fixture", "/api/v2/mix/market/account-long-short") => (200, $$"""{"code":"00000","msg":"success","requestTime":{{now}},"data":[""" + string.Join(",", Newest(Period(query["period"]), now - 30 * Period(query["period"]), now, 30).Reverse()
                    .Select(t => $$"""{"longAccountRatio":"0.6","shortAccountRatio":"0.4","longShortAccountRatio":"1.5","ts":"{{t}}"}""")) + "]}"),
                // ---- Hyperliquid (POST /info)
                ("hyperliquid.fixture", "/info") => await Hyperliquid(request, now, cancellationToken).ConfigureAwait(false),
                _ => (404, "{}"),
            };
            return new HttpResponseMessage((HttpStatusCode)body.Item1) { Content = new StringContent(body.Item2, Encoding.UTF8, "application/json") };
        }

        private static async Task<(int, string)> Hyperliquid(HttpRequestMessage request, long now, CancellationToken ct)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() == "metaAndAssetCtxs")
            {
                string universe = string.Join(",", Coins.Select(c => $$"""{"szDecimals":2,"name":"{{c.Coin}}","maxLeverage":40}"""));
                string contexts = string.Join(",", Coins.Select(c => string.Create(CultureInfo.InvariantCulture,
                    $$"""{"funding":"0.0000125","openInterest":"37277.79","premium":"-0.0004","oraclePx":"{{c.Price}}","markPx":"{{c.Price}}"}""")));
                return (200, $$"""[{"universe":[{{universe}}]},[{{contexts}}]]""");
            }
            long start = root.GetProperty("startTime").GetInt64(), end = Math.Min(root.GetProperty("endTime").GetInt64(), now);
            const long Hour = 3_600_000;
            var rows = new List<string>();
            for (long t = (start + Hour - 1) / Hour * Hour; t <= end && rows.Count < 500; t += Hour)
                rows.Add(string.Create(CultureInfo.InvariantCulture, $$"""{"coin":"BTC","fundingRate":"0.0000125","premium":"-0.0001","time":{{t + 21}}}"""));
            return (200, "[" + string.Join(",", rows) + "]");
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
        private static readonly long Anchor = Ms(DateTime.UtcNow);
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
                // a slow upward drift makes each swing high exceed the previous one, so closed bars confirm structure breaks
                double phase = t / 3_600_000.0, drift = (t - Anchor) / 3_600_000.0 * 20;
                decimal open = Mid + (decimal)(Math.Sin(phase / 9) * 2500 + drift), close = Mid + (decimal)(Math.Sin((phase + minutes / 60.0) / 9) * 2500 + drift + minutes / 60.0 * 20);
                decimal high = Math.Max(open, close) + 40, low = Math.Min(open, close) - 40, volume = 100 + (decimal)(Math.Abs(Math.Cos(phase)) * 50);
                decimal buy = volume * (close >= open ? 0.6m : 0.4m);
                bars.Add(new Kline(t, open, high, low, close, volume, buy, true));
            }
            onBatch(bars);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            sink.OnStatus(instrument, status, null);
            return new End();
        }

        public void Dispose() { }
        private sealed class End : IDisposable { public void Dispose() { } }
    }

    /// <summary>A chain around 80,000 with four expiries, a convex smile, call open interest above and put open interest below.</summary>
    private class FixtureOptions(string name = "Fixture chain") : IOptionsProvider
    {
        public string Name => name;
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
            return Task.FromResult<OptionsChain?>(new OptionsChain("BTC", now.AddMinutes(-1), 80_000, contracts, name, "index"));
        }

        public Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
        {
            var trades = new List<OptionTrade>();
            for (var t = from.AddMinutes(17); t < to; t = t.AddHours(2))
                trades.Add(new OptionTrade(t, "BTC-7-85000-C", DateTime.UtcNow.Date.AddDays(7).AddHours(8), 85_000, (t.Hour / 2) % 2 == 0 ? OptionRight.Call : OptionRight.Put,
                    1500, 120, (t.Hour / 2) % 3 == 0 ? -1 : 1, 0.52, 80_000, 1, "Fixture"));
            return Task.FromResult<IReadOnlyList<OptionTrade>>(trades);
        }

        public virtual Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OptionDaily>>(Array.Empty<OptionDaily>());
    }

    /// <summary>
    /// The same chain from a source that publishes DVOL: daily ATM IV rows (fractions) for the last 40 days and today,
    /// with days 5 and 6 missing and day 10 a NaN close.
    /// </summary>
    private sealed class FixtureIndexedOptions() : FixtureOptions("Fixture options"), IVolatilityIndexSource
    {
        public string VolatilityIndexName => "DVOL";

        public override Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
        {
            var rows = new List<OptionDaily>();
            var today = DateTime.UtcNow.Date;
            for (int ago = 40; ago >= 0; ago--)
            {
                if (ago is 5 or 6) continue;
                double iv = ago == 10 ? double.NaN : 0.5 + 0.05 * Math.Sin(ago / 3.0);
                rows.Add(new OptionDaily(today.AddDays(-ago), 80_000, iv, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            }
            return Task.FromResult<IReadOnlyList<OptionDaily>>(rows);
        }
    }
}
