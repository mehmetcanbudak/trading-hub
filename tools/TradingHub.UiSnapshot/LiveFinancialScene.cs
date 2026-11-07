using TradingHub.App.Services;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Common;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

internal static class LiveFinancialScene
{
    private static readonly DateTime Start = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Instrument Contract = new()
    {
        Symbol = "FINANCIAL-UI", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 10m,
    };

    public static int Run(MainWindow window, Action<double> pump, string output, bool correctAfterStop = false, bool stoppedOpen = false)
    {
        string? isolated = Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR");
        if (string.IsNullOrWhiteSpace(isolated) || Path.GetFullPath(isolated) != Path.GetFullPath(LiveStore.Root))
            throw new InvalidOperationException("Financial UI proof requires its explicit isolated live store.");
        using var feed = new FixedFeed();
        using var data = new InstrumentData(Contract, feed) { RecordLive = false };
        data.Start(Start);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (data.IsLoadingHistory && DateTime.UtcNow < deadline) { data.Pump(); pump(0.01); }
        if (data.IsLoadingHistory || data.HistoryError != null || data.LastPrice != 110m)
            throw new InvalidOperationException("Financial UI fixture history failed.");
        using var broker = new SimBroker(data, new Portfolio(), new TradingStatistics()) { CommissionPerUnit = 1.5m };
        var strategy = new EntryStrategy();
        using var runner = new LiveStrategyRunner(strategy, data, broker, ChartPeriod.M1);
        var service = App.Services;
        string portfolioId = "execution-accounting-" + Guid.NewGuid().ToString("N");
        try
        {
            runner.Start();
            service.Runners.Add(runner);
            service.Live.SavePortfolio(new LivePortfolioDef
            {
                Id = portfolioId, Name = "Execution accounting fixture", Members = new() { runner.Key },
            });
            strategy.BuyAt(100m, 2m);
            feed.Emit(new Trade(2, Start.AddMinutes(1), 100m, 2m, TradeSide.Sell));
            data.Pump();
            deadline = DateTime.UtcNow.AddSeconds(30);
            while ((broker.Position?.Volume != 2m || runner.Ledger.Position != 2m || runner.Ledger.HasPendingFinancials) && DateTime.UtcNow < deadline)
            {
                data.Pump();
                pump(0.01); // The actual UI context dispatches broker/execution notifications asynchronously.
            }
            if (broker.Position?.Volume != 2m || runner.Ledger.NetRealized != -3m || runner.Ledger.KnownOpenPnL(100m) != 0m)
                throw new InvalidOperationException($"Actual simulator control failed: broker={broker.Position?.Volume}, ledger={runner.Ledger.Position}, net={runner.Ledger.NetRealized}, quality={runner.Ledger.FinancialQuality}, open={runner.Ledger.KnownOpenPnL(100m)}.");
            decimal? expectedTotal = -3m;
            if (correctAfterStop)
            {
                strategy.SellAt(105m, 2m);
                feed.Emit(new Trade(3, Start.AddMinutes(2), 105m, 2m, TradeSide.Buy));
                deadline = DateTime.UtcNow.AddSeconds(30);
                while ((broker.Position?.Volume != 0 || runner.Ledger.NetRealized != 94m) && DateTime.UtcNow < deadline)
                {
                    data.Pump();
                    pump(0.01);
                }
                if (broker.Position?.Volume != 0 || runner.Ledger.NetRealized != 94m)
                    throw new InvalidOperationException("The complete simulator round-trip control did not produce net94.");
                runner.Stop(flatten: false);
                var reports = broker.Executions.Capture(ExecutionCursor.Start);
                var original = reports.Changes.Single(report => report.Buy);
                ((ExecutionLog)broker.Executions).Publish(original with { Price = 102m, Fees = new[] { new ExecutionFee(4m, "USD") } });
                deadline = DateTime.UtcNow.AddSeconds(30);
                while (LiveStore.LoadLedger(runner.Key, 10m).NetRealized != 53m && DateTime.UtcNow < deadline) pump(0.01);
                if (LiveStore.LoadLedger(runner.Key, 10m).NetRealized != 53m)
                    throw new InvalidOperationException("The account projector did not persist the stopped correction: expected60gross minus7fees.");
                expectedTotal = 53m;
            }
            else if (stoppedOpen)
            {
                runner.Stop(flatten: false);
                expectedTotal = null; // Complete executions are not a current valuation of stopped exposure.
            }

            window.Navigate("live");
            pump(0.1);
            var view = window.GetVisualDescendants().OfType<LiveView>().Single(v => v.IsEffectivelyVisible);
            view.Select(stoppedOpen ? "strategies" : "portfolios");
            pump(0.2);
            var label = view.GetVisualDescendants().OfType<TextBlock>().Single(t =>
                t.IsEffectivelyVisible && t.Text == TradingHub.App.Localization.Lang.T("PnL"));
            var card = (StackPanel)label.Parent!;
            string? displayed = card.Children.OfType<TextBlock>().ElementAt(1).Text;
            string expected = expectedTotal?.ToString("+#,##0.00;-#,##0.00;0.00", UiCulture.Current) ?? "—";
            string scene = stoppedOpen ? "live-stopped-open" : correctAfterStop ? "live-stopped" : "live-financial";
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Financial surface did not render.");
            frame.Save(Path.Combine(output, stoppedOpen ? "live_stopped_open.png" : correctAfterStop ? "live_financial_stopped_correction.png" : "live_financial_open_entry_fee.png"));
            bool correct = displayed == expected;
            if (stoppedOpen)
            {
                var countLabel = view.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.IsEffectivelyVisible && text.Text == TradingHub.App.Localization.Lang.T("Closed trades"));
                string? count = ((StackPanel)countLabel.Parent!).Children.OfType<TextBlock>().ElementAt(1).Text;
                correct &= count == "0"; // Missing marks must not hide a known closed-trade count.
            }
            string evidence = JsonSerializer.Serialize(new
            {
                actualSimulatorPosition = broker.Position?.Volume,
                retainedRunnerNet = runner.Ledger.NetRealized,
                durableNet = LiveStore.LoadLedger(runner.Key, Contract.PointValue).NetRealized,
                openGross = runner.Ledger.KnownOpenPnL(100m),
                expectedPortfolioTotal = expectedTotal, expectedDisplay = expected, actualDisplay = displayed,
                stoppedOpen, passed = correct, authority = "Actual Avalonia live financial surface with simulator executions; no broker/model calls",
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, scene + "-evidence.json"), evidence);
            Console.WriteLine(evidence);
            return correct ? 0 : 1;
        }
        finally
        {
            runner.Stop(flatten: false);
            service.Runners.Remove(runner);
            service.Live.DeletePortfolio(portfolioId);
            LiveStore.RemoveRunner(runner.Key, deleteFills: true);
            window.Navigate("trading");
        }
    }

    public static int RunJournalRevision(MainWindow window, Action<double> pump, string output)
    {
        string? isolated = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR");
        if (string.IsNullOrWhiteSpace(isolated) || Path.GetFullPath(isolated) != Path.GetFullPath(AppPaths.Root))
            throw new InvalidOperationException("Journal UI proof requires its explicit isolated application store.");
        var service = App.Services;
        if (service.Journal.CountOf(AccountCenter.SimId) != 0 || service.SimBrokers.Any(b => b.Position?.Volume != 0))
            throw new InvalidOperationException("Journal UI proof requires a fresh, flat simulator fixture.");
        window.Navigate("trading");
        pump(0.2);
        var trading = window.Trading;
        var chart = trading.ActiveTab ?? throw new InvalidOperationException("The fixture chart did not open.");
        var model = chart.Model ?? throw new InvalidOperationException("The fixture chart has no rendered model.");
        string oldAccount = chart.AccountId;
        bool oldMarks = model.Settings.ShowTradeMarks;
        double oldTop = model.Viewport.PriceTop, oldBottom = model.Viewport.PriceBottom;
        try
        {
            chart.AccountId = AccountCenter.SimId;
            model.Settings.ShowTradeMarks = true;
            model.Viewport.PriceTop = 5020;
            model.Viewport.PriceBottom = 4980;
            string symbol = (chart.CrossData ?? model.Data).Instrument.Symbol;
            DateTime time = DateTime.UtcNow;
            var entry = new AccountFill("journal-ui-entry", time, symbol, true, 2m, 5000.25m, 50m, 0m, "journal-ui-entry-order");
            var exit = new AccountFill("journal-ui-exit", time, symbol, false, 2m, 5005.25m, 50m, 0m, "journal-ui-exit-order");
            service.Journal.Add(AccountCenter.SimId, entry);
            service.Journal.Add(AccountCenter.SimId, exit);
            pump(0.6);
            decimal beforeMark = model.TradeMarks.Single(mark => mark.Buy).Price;
            decimal beforeLink = model.TradeLinks.Single().Pnl;
            var account = service.AccountCenter.Accounts().Single(a => a.Id == AccountCenter.SimId);
            decimal beforeAnalysis = service.AccountCenter.Snapshot(account).Analysis.RealizedNet;
            decimal beforeSummary = service.AccountCenter.SimSummary().DayPnl;
            window.Navigate("accounts");
            pump(0.3);
            var view = window.GetVisualDescendants().OfType<AccountsView>().Single(v => v.IsEffectivelyVisible);
            var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
            void Select(string title)
            {
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, title));
                pump(0.4);
            }
            decimal DisplayedTrade()
            {
                Select("Trades");
                var rows = view.GetVisualDescendants().OfType<ListBox>().Select(list => list.ItemsSource)
                    .OfType<IEnumerable<AccountTrade>>().Single();
                return rows.Single().Pnl;
            }
            decimal DisplayedStatistics()
            {
                var title = view.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.IsEffectivelyVisible && text.Text == "NET PNL");
                string text = ((StackPanel)title.Parent!).Children.OfType<TextBlock>().ElementAt(1).Text!;
                return decimal.Parse(text, System.Globalization.NumberStyles.Number, UiCulture.Current);
            }
            void Capture(string name)
            {
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("The journal surface did not render.");
                frame.Save(Path.Combine(output, name));
            }
            decimal beforeTrade = DisplayedTrade();
            Select("Statistics");
            decimal beforeStats = DisplayedStatistics();
            Capture("account_journal_revision_before.png");
            if (beforeAnalysis != 500m || beforeSummary != 500m || beforeTrade != 500m || beforeStats != 500m ||
                beforeMark != 5000.25m || beforeLink != 500m)
                throw new InvalidOperationException($"Journal UI baseline failed: analysis={beforeAnalysis}, summary={beforeSummary}, trade={beforeTrade}, statistics={beforeStats}, mark={beforeMark}, link={beforeLink}.");

            service.Journal.Add(AccountCenter.SimId, entry with { Price = 5001.25m });
            decimal afterAnalysis = service.AccountCenter.Snapshot(account).Analysis.RealizedNet;
            decimal afterSummary = service.AccountCenter.SimSummary().DayPnl;
            decimal firstAfterStats = DisplayedStatistics();
            decimal afterStats = firstAfterStats;
            var statisticsDeadline = DateTime.UtcNow.AddSeconds(30);
            while (afterStats != 400m && DateTime.UtcNow < statisticsDeadline)
            {
                pump(0.01);
                afterStats = DisplayedStatistics();
            }
            Capture("account_journal_revision_statistics_after.png");
            decimal afterTrade = DisplayedTrade();
            Capture("account_journal_revision_trade_after.png");
            window.Navigate("trading");
            pump(0.6);
            decimal afterMark = model.TradeMarks.Single(mark => mark.Buy).Price;
            decimal afterLink = model.TradeLinks.Single().Pnl;
            Capture("account_journal_revision_chart_after.png");
            bool correct = afterAnalysis == 400m && afterSummary == 400m && afterStats == 400m && afterTrade == 400m &&
                afterMark == 5001.25m && afterLink == 400m && service.Journal.CountOf(AccountCenter.SimId) == 2;
            string evidence = JsonSerializer.Serialize(new
            {
                before = new { analysis = beforeAnalysis, summary = beforeSummary, trade = beforeTrade, statistics = beforeStats, mark = beforeMark, link = beforeLink },
                after = new { analysis = afterAnalysis, summary = afterSummary, trade = afterTrade, statistics = afterStats, mark = afterMark, link = afterLink },
                expectedAfter = new { net = 400m, entry = 5001.25m, effectiveExecutions = 2 },
                firstAfterStatistics = firstAfterStats,
                passed = correct,
                authority = "Actual AccountCenter, AccountsView statistics/trades and TradingView marks/links; owned journal facts only, no provider orders or model calls",
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "account-journal-revision-evidence.json"), evidence);
            Console.WriteLine(evidence);
            return correct ? 0 : 1;
        }
        finally
        {
            chart.AccountId = oldAccount;
            model.Settings.ShowTradeMarks = oldMarks;
            model.Viewport.PriceTop = oldTop;
            model.Viewport.PriceBottom = oldBottom;
        }
    }

    public static int RunStoppedChartRevision(MainWindow window, Action<double> pump, string output)
    {
        string? isolated = Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR");
        if (string.IsNullOrWhiteSpace(isolated) || Path.GetFullPath(isolated) != Path.GetFullPath(LiveStore.Root))
            throw new InvalidOperationException("Chart revision proof requires its explicit isolated live store.");
        window.Navigate("trading");
        pump(0.2);
        var chart = window.Trading.ActiveTab ?? throw new InvalidOperationException("The fixture chart did not open.");
        var model = chart.Model ?? throw new InvalidOperationException("The fixture chart has no rendered model.");
        if (model.Series.Count < 3) throw new InvalidOperationException("The chart fixture needs three visible simulated candles.");
        DateTime entryAt = model.Series[model.Series.Count - 3].Time;
        DateTime exitAt = model.Series[model.Series.Count - 2].Time;
        decimal entryPrice = model.Instrument.RoundPrice(model.Data.LastPrice);
        decimal correctedPrice = entryPrice + 2m, exitPrice = entryPrice + 5m;
        const string strategyId = "StoppedChartRevisionFixture";
        var account = App.Services.AccountFor(model.Data, AccountCenter.SimId);
        string? accountScope = account is IAccountExecutionSource executions ? executions.PersistentAccountIdentity : account.Name;
        if (string.IsNullOrWhiteSpace(accountScope))
            throw new InvalidOperationException("The chart fixture's simulated account identity is unavailable.");
        string key = $"{strategyId}|{model.Instrument.FeedId}:{model.Instrument.Symbol}|{accountScope}";
        var cs = new ChartStrategy
        {
            StrategyId = strategyId, Live = true, Paused = true, AccountId = AccountCenter.SimId,
            HistoryFrom = model.Data.HistoryFrom,
        };
        cs.Overlay.Name = "Stopped chart revision fixture";
        cs.Overlay.Color = new SkiaSharp.SKColor(180, 120, 255);
        var scope = new ExecutionScope("fixture", accountScope, "paper", model.Instrument.Key, "stopped-chart-revision");
        var entry = new LiveFill(entryAt, true, 2m, entryPrice)
        {
            Kind = LiveFillKind.Execution, OrderId = "entry", ExecutionId = new(scope, "entry"),
            Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
        };
        try
        {
            if (LiveStore.AppendFill(key, entry) is { } entryError) throw new IOException(entryError);
            if (LiveStore.AppendFill(key, new LiveFill(exitAt, false, 2m, exitPrice)
            {
                Kind = LiveFillKind.Execution, OrderId = "exit", ExecutionId = new(scope, "exit"),
                Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
            }) is { } exitError) throw new IOException(exitError);
            LiveStore.SaveRunners(LiveStore.LoadRunners().Where(info => info.Key != key).Append(new LiveRunnerInfo
            {
                Key = key, Strategy = cs.Overlay.Name, StrategyId = strategyId, Source = "chart",
                FeedId = model.Instrument.FeedId, Symbol = chart.Symbol, Period = model.Period.Code,
                Account = "sim", AccountId = AccountCenter.SimId, PointValue = 10m, Paused = true,
            }));
            cs.SavedLedger = LiveStore.LoadLedger(key, 10m);
            chart.Strategies.Add(cs);
            model.Strategies.Add(cs.Overlay);
            model.CenterOn(entryAt, exitAt);
            pump(0.6);
            decimal beforeEntry = cs.Overlay.Signals.Single(signal => signal.Entry).Price;
            decimal beforeNet = cs.Overlay.Links.Single().Pnl;
            if (beforeEntry != entryPrice || beforeNet != 94m)
                throw new InvalidOperationException($"Restored chart baseline failed: entry={beforeEntry}, net={beforeNet}.");

            if (LiveStore.AppendFill(key, entry with { Price = correctedPrice, Fees = new[] { new ExecutionFee(4m, "USD") } }) is { } correctionError)
                throw new IOException(correctionError);
            pump(0.6);
            decimal afterEntry = cs.Overlay.Signals.Single(signal => signal.Entry).Price;
            decimal afterNet = cs.Overlay.Links.Single().Pnl;
            if (LiveStore.AppendFill(key, entry with
                { Price = correctedPrice, Fees = Array.Empty<ExecutionFee>(), FeesComplete = false }) is { } pendingError)
                throw new IOException(pendingError);
            pump(0.6);
            var pendingEntry = cs.Overlay.Signals.Single(signal => signal.Entry);
            var pendingExit = cs.Overlay.Signals.Single(signal => !signal.Entry);
            bool pendingLabelIsNumeric = decimal.TryParse(pendingExit.Label, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
            var pending = new
            {
                quality = cs.SavedLedger?.FinancialQuality.ToString(),
                net = cs.SavedLedger?.NetRealized,
                monetaryLinks = cs.Overlay.Links.Count,
                exitLabel = pendingExit.Label,
                entryTime = pendingEntry.Time, entryPrice = pendingEntry.Price,
                exitTime = pendingExit.Time, exitPrice = pendingExit.Price,
            };
            bool pendingAmountsHidden = cs.SavedLedger is { HasPendingFinancials: true, NetRealized: null } &&
                !pendingLabelIsNumeric && cs.Overlay.Links.Count == 0 &&
                pendingEntry.Time == entryAt && pendingEntry.Price == correctedPrice &&
                pendingExit.Time == exitAt && pendingExit.Price == exitPrice;
            using (var pendingFrame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Pending chart did not render."))
                pendingFrame.Save(Path.Combine(output, "live_stopped_chart_pending.png"));
            if (LiveStore.AppendFill(key, entry with { Price = correctedPrice, Fees = new[] { new ExecutionFee(4m, "USD") } }) is { } recoveryError)
                throw new IOException(recoveryError);
            pump(0.6);
            decimal recoveredNet = cs.Overlay.Links.Single().Pnl;
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Restored chart did not render.");
            frame.Save(Path.Combine(output, "live_stopped_chart_revision.png"));
            bool correct = afterEntry == correctedPrice && afterNet == 53m && cs.SavedLedger?.Fills.Count == 2 &&
                pendingAmountsHidden && recoveredNet == 53m && cs.SavedLedger is { HasPendingFinancials: false };
            string evidence = JsonSerializer.Serialize(new
            {
                fixture = new { entryAt, exitAt, entryPrice, correctedPrice, exitPrice, pointValue = 10m, quantity = 2m },
                before = new { entry = beforeEntry, net = beforeNet },
                after = new { entry = afterEntry, net = afterNet },
                expectedAfter = new { entry = correctedPrice, net = 53m, effectiveExecutions = 2 },
                pending, pendingAmountsHidden, recoveredNet,
                passed = correct, authority = "Actual restored paused chart overlay after committed price/fee correction, fee unavailability and complete-fee recovery; private history only",
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "live-stopped-chart-evidence.json"), evidence);
            Console.WriteLine(evidence);
            return correct ? 0 : 1;
        }
        finally
        {
            chart.Strategies.Remove(cs);
            model.Strategies.Remove(cs.Overlay);
            LiveStore.RemoveRunner(key, deleteFills: true);
        }
    }

    public static int RunExcursions(Action<double> pump, string output)
    {
        const string key = "excursion-correction-fixture";
        string symbol = "EXCURSION-" + Guid.NewGuid().ToString("N");
        var instrument = new Instrument { Symbol = symbol, FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 10m };
        string path = TickFile.DayPath(AppPaths.Ticks, "test", symbol, DateOnly.FromDateTime(Start));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new TickFile.Writer(path, instrument))
        {
            writer.Write(new Trade(1, Start, 100m, 1m, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddMinutes(1), 97m, 1m, TradeSide.Sell));
            writer.Write(new Trade(3, Start.AddMinutes(2), 106m, 1m, TradeSide.Buy));
            writer.Write(new Trade(4, Start.AddMinutes(5), 104m, 1m, TradeSide.Sell));
        }
        try
        {
            var scope = new ExecutionScope("fixture", "sim", "paper", "test:" + symbol);
            var ledger = new LiveLedger(key, 10m);
            var entryFill = new LiveFill(Start, true, 2m, 100m)
            {
                Kind = LiveFillKind.Execution, OrderId = "entry", ExecutionId = new(scope, "entry"),
                Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
            };
            ledger.Apply(entryFill);
            ledger.Apply(new LiveFill(Start.AddMinutes(5), false, 2m, 104m)
            {
                Kind = LiveFillKind.Execution, OrderId = "exit", ExecutionId = new(scope, "exit"),
                Fees = new[] { new ExecutionFee(3m, "USD") }, FeesComplete = true, Currency = "USD",
            });
            var entry = new LiveEntry
            {
                Key = key, Source = "fixture", Ledger = ledger,
                Info = new LiveRunnerInfo { Key = key, FeedId = "test", Symbol = symbol, PointValue = 10m },
            };
            (decimal Mae, decimal Mfe, bool Exact) Read(ClosedTrade trade)
            {
                var pending = App.Services.Live.ExcursionsAsync(new[] { (entry, trade) });
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (!pending.IsCompleted && DateTime.UtcNow < deadline) pump(0.01);
                if (!pending.IsCompleted) throw new TimeoutException("The owned excursion consumer did not finish.");
                return pending.GetAwaiter().GetResult()[TradingHub.App.Services.LiveCenter.TradeId(key, trade)];
            }
            var before = Read(ledger.Trades.Single());
            if (before != (-60m, 120m, true)) throw new InvalidOperationException($"Initial excursion control failed: {before}.");
            ledger.Apply(entryFill with { Price = 102m, Fees = new[] { new ExecutionFee(4m, "USD") } });
            var after = Read(ledger.Trades.Single());
            bool correct = after == (-100m, 80m, true);
            string evidence = JsonSerializer.Serialize(new
            {
                before = new { before.Mae, before.Mfe, before.Exact },
                after = new { after.Mae, after.Mfe, after.Exact },
                expectedAfter = new { Mae = -100m, Mfe = 80m, Exact = true },
                passed = correct, authority = "Actual LiveCenter excursion consumer and owned raw history; same scoped execution corrected from100 to102",
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(output, "live-excursion-evidence.json"), evidence);
            Console.WriteLine(evidence);
            return correct ? 0 : 1;
        }
        finally { File.Delete(path); }
    }

    private sealed class EntryStrategy : Strategy
    {
        public void BuyAt(decimal price, decimal quantity) => BuyLimit(price, quantity);
        public void SellAt(decimal price, decimal quantity) => SellLimit(price, quantity);
    }

    private sealed class FixedFeed : IMarketDataFeed
    {
        private IMarketDataSink? _sink;
        public string Id => "test";
        public string DisplayName => "Isolated financial UI market";
        public bool SupportsHistory => true;
        public bool IsLive => false;
        public DateTime Now => Start.AddSeconds(1);
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Instrument>>(new[] { Contract });
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
            Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            onBatch(new[] { new Trade(1, Start, 110m, 1m, TradeSide.Buy) });
            return Task.CompletedTask;
        }
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            _sink = sink;
            return new Subscription(this);
        }
        public void Emit(Trade trade) => _sink!.OnTrades(Contract, new[] { trade });
        public void Dispose() => _sink = null;
        private sealed class Subscription(FixedFeed feed) : IDisposable
        {
            public void Dispose() => feed._sink = null;
        }
    }
}
