// Scene "external-data": the options-data and ETF-flows connections against loopback fake servers (never real
// providers): options studies wait while unconfigured, saving a connection configures them without a restart, each
// connection tests and renders, a chart shows options studies with provenance, and disabling returns to waiting.
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.External;
using TradingHub.Indicators.Native.Macro;
using TradingHub.Indicators.Native.Options;
using TradingHub.Market;
using TradingHub.Tests;

internal static class ExternalDataScene
{
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "OK " : "FAIL")}  external-data: {what}");
            if (!ok) failures.Add(what);
        }
        void Shot(string name)
        {
            pump(1);
            using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(outDir, name + ".png"));
        }
        void Await(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted && DateTime.UtcNow < until) pump(0.05);
        }

        var now = DateTime.UtcNow;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddDays(-3);
        string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        string Chain()
        {
            var rows = new List<string>();
            foreach (int days in new[] { 1, 7, 30 })
                for (int k = 80; k <= 120; k += 5)
                    foreach (var right in new[] { "C", "P" })
                    {
                        double m = Math.Log(k / 100.0), iv = 0.5 + 0.6 * m * m;
                        double oi = 300 * Math.Exp(-m * m * 40) * (right == "P" && k < 100 ? 1.6 : 1);
                        rows.Add($$"""{"symbol":"BTC-{{days}}-{{k}}-{{right}}","expiry":"{{Iso(now.Date.AddDays(days).AddHours(8))}}","strike":{{k}},"right":"{{right}}","mark":2,"iv":{{iv.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"oi":{{oi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}},"volume":{{(oi / 4).ToString("0", System.Globalization.CultureInfo.InvariantCulture)}}}""");
                    }

            return $$"""{"time":"{{Iso(now.AddMinutes(-1))}}","spot":100,"contracts":[{{string.Join(",", rows)}}]}""";
        }
        string Trades()
        {
            var rows = new List<string>();
            for (var t = start; t < now; t = t.AddMinutes(20))
                rows.Add($$"""{"time":"{{Iso(t)}}","symbol":"BTC-7-{{95 + 5 * (t.Hour % 3)}}-{{(t.Minute == 0 ? "C" : "P")}}","expiry":"{{Iso(now.Date.AddDays(7).AddHours(8))}}","strike":{{95 + 5 * (t.Hour % 3)}},"right":"{{(t.Minute == 0 ? "C" : "P")}}","price":2.5,"size":{{1 + t.Hour % 7}},"side":"{{(t.Hour % 2 == 0 ? "buy" : "sell")}}","iv":0.52,"spot":100,"venue":"{{(t.Hour % 3 == 0 ? "OKX" : "Deribit")}}"}""");
            return "[" + string.Join(",", rows) + "]";
        }
        string Daily() => "[" + string.Join(",", Enumerable.Range(1, 120).Select(i =>
            $$"""{"date":"{{now.Date.AddDays(-i):yyyy-MM-dd}}","atmIv":{{(0.45 + 0.15 * Math.Sin(i / 9.0)).ToString(System.Globalization.CultureInfo.InvariantCulture)}},"rr25":{{(-2 + Math.Cos(i / 5.0)).ToString(System.Globalization.CultureInfo.InvariantCulture)}},"pcVolume":1.1,"pcOi":0.9,"callVolume":{{5000 + i}},"putVolume":4000,"gex":{{(i - 60) * 1e6}}}""")) + "]";

        using var options = new FakeHttpServer();
        options.OnHttp = req => req.Headers.GetValueOrDefault("Authorization") != "Bearer opt-KEY"
            ? (401, "{}")
            : req.Route switch
            {
                "/v1/chain" => (200, Chain()),
                "/v1/trades" => (200, Trades()),
                "/v1/daily" => (200, Daily()),
                _ => (404, "{}"),
            };
        using var etfServer = new FakeHttpServer();
        etfServer.OnHttp = _ => (200, $$"""[{"date":"{{now.AddDays(-3):yyyy-MM-dd}}","net":-123400000},{"date":"{{now.AddDays(-2):yyyy-MM-dd}}","netMillions":55.5},{"date":"{{now.AddDays(-1):yyyy-MM-dd}}","netMillions":210}]""");

        var svc = App.Services;
        var conns = svc.Connections;
        Check(!AppPaths.Root.StartsWith(TradingHub.Common.Product.DefaultAppData, StringComparison.Ordinal), "isolated data folder");
        var hub = OptionsData.Shared;
        Check(hub != null && hub.Provider == null && hub.Unavailable("BTC") == OptionsData.NotConfigured, "without a connection the options studies wait");
        Check(EtfFlows.Shared?.Provider == null, "without a connection ETF flows wait");
        conns.Profiles.RemoveAll(p => p.IsExternalData);

        // ---- chart before any connection: the waiting state
        window.Navigate("trading");
        var tab = window.Trading.ActiveTab;
        if (tab?.Model == null || hub == null)
        {
            Check(false, "the trading page has an active chart");
            return failures.Count;
        }

        var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = "sim", TickSize = 0.1m, VolumeStep = 1, BaseAsset = "BTC", QuoteAsset = "USDT" };
        using var data = new InstrumentData(instrument, new SimulatedFeed()) { RecordLive = false };
        long id = 0;
        for (var t = start; t < now; t = t.AddMinutes(5))
        {
            decimal price = 100m + Math.Round(3m * (decimal)Math.Sin((t - start).TotalMinutes / 97.0), 1);
            data.Trades.Add(new Trade(++id, t, price, 1 + id % 4, id % 3 == 0 ? TradeSide.Sell : TradeSide.Buy));
        }

        using var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        var dealers = new DealerPositioning();
        var exposure = new GreekExposure { CardPosition = CardCorner.TopLeft };
        var ivRank = new IvRank();
        var etfPane = new SpotEtfFlow();
        var studies = new TradingHub.Indicators.Indicator[] { dealers, exposure, ivRank, etfPane };
        foreach (var ind in studies) model.AddIndicator(ind, applyDefaultColors: false);
        void Recalculate()
        {
            foreach (var ind in studies) ind.RecalculateValues();
        }
        var originalModel = tab.Model;
        tab.Model = model;
        tab.View.Model = model;
        try
        {
            model.Update();
            Check(dealers.Status == OptionsData.NotConfigured && ivRank.Status == OptionsData.NotConfigured && etfPane.Status == EtfFlows.NotConfigured,
                "studies show the waiting message instead of zeros");
            Shot("external_waiting");

            // ---- add the connections: saving configures the shared data immediately
            var pOpt = new ConnectionProfile { Id = "options-data", Kind = ConnectionKind.OptionsData, Name = "Options data", OptionsSource = OptionsSource.CustomJson, ExternalUrl = options.Http + "/v1", ExternalUnderlyings = "BTC" };
            var pEtf = new ConnectionProfile { Id = "etf-flows", Kind = ConnectionKind.EtfFlows, Name = "ETF flows", ExternalUrl = etfServer.Http };
            conns.Profiles.AddRange(new[] { pOpt, pEtf });
            conns.SetSecret(pOpt, "opt-KEY");
            conns.Save();
            Check(!pOpt.HasMarketData && !pEtf.HasMarketData, "external data connections are not chart markets");
            Check(hub.Provider is JsonOptionsApi && EtfFlows.Shared?.Provider is JsonEtfFlowsApi, "saving configures the studies' providers without a restart");
            Check(hub.Unavailable("BTC") == null && hub.Unavailable("ETH") != null, "an underlying outside the list stays unavailable");
            foreach (var p in new[] { pOpt, pEtf })
            {
                var test = conns.TestAsync(p, CancellationToken.None);
                Await(test);
                Check(test.IsCompletedSuccessfully && test.Result.Ok, $"test {p.Name}: {(test.IsCompletedSuccessfully ? test.Result.Message : test.Exception?.GetBaseException().Message)}");
            }

            window.Navigate("connections");
            pump(1);
            var pages = (Dictionary<string, Control>)window.GetType().GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var view = pages["connections"];
            var select = view.GetType().GetMethod("Select", BindingFlags.NonPublic | BindingFlags.Instance)!;
            select.Invoke(view, new object[] { pOpt });
            Shot("external_connection_options");
            select.Invoke(view, new object[] { pEtf });
            Shot("external_connection_etf");

            // ---- the chart now loads the chain, trades, daily history and ETF flows by itself
            window.Navigate("trading");
            for (int i = 0; i < 400 && (dealers.Status != null || ivRank.Status != null || etfPane.Status != null || hub.TradeCoverage("BTC") == null); i++)
            {
                model.Update();
                pump(0.05);
                if (i % 20 == 19) Recalculate();
            }

            Await(hub.WhenIdleAsync());
            Recalculate();
            model.Update();
            Check(dealers.Status == null && dealers.Provenance?.StartsWith("Custom options API", StringComparison.Ordinal) == true,
                $"dealer positioning shows levels with provenance ({dealers.Status ?? dealers.Provenance})");
            Check(ivRank.Status == null, $"IV rank reads the daily history ({ivRank.Status})");
            Check(etfPane.Status == null, $"ETF flows load ({etfPane.Status})");
            Shot("external_options_chart");

            // ---- disabling the options connection returns the studies to waiting; ETF flows stay
            pOpt.Enabled = false;
            conns.Save();
            Recalculate();
            model.Update();
            Check(hub.Provider == null && EtfFlows.Shared?.Provider != null, "disabled, the options studies return to waiting");
            Check(dealers.Status == OptionsData.NotConfigured, $"dealer positioning waits again ({dealers.Status})");
        }
        finally
        {
            tab.Model = originalModel;
            tab.View.Model = originalModel;
            conns.Profiles.RemoveAll(p => p.IsExternalData);
            conns.Save();
            pump(0.2);
        }

        Console.WriteLine(failures.Count == 0 ? "EXTERNAL-DATA: all OK" : $"EXTERNAL-DATA: {failures.Count} failure(s)");
        return failures.Count;
    }
}
