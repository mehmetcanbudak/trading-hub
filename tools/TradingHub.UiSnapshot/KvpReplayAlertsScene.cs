using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Replay;
using TradingHub.Trading;

internal static class KvpReplayAlertsScene
{
    private static readonly DateTime Start = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
    private const int Seed = 743;
    private const string WebhookSecret = "Bearer kvp-ui-private-authorization";
    private const string ReplacementSecret = "Bearer kvp-ui-unconfirmed-authorization";
    private const string TelegramSecret = "12345:kvp_ui_private_token";
    private const string WebhookHostSecret = "kvp-ui-private-host";
    private const string WebhookOrigin = "https://" + WebhookHostSecret + ".fixture.invalid";
    private const string WebhookDisplay = "Private webhook";
    private const string WebhookCapability = "kvp-ui-private-capability";
    private const string WebhookQuerySecret = "kvp-ui-private-query";
    private const string WebhookPath = "/hooks/" + WebhookCapability;
    private const string WebhookEndpoint = WebhookOrigin + WebhookPath + "?capability=" + WebhookQuerySecret;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    public static int Run(MainWindow owner, Action<double> pump, string outDir)
    {
        var proof = new Proof(owner, pump, Path.GetFullPath(outDir));
        proof.Exercise("practice", () => ExercisePractice(proof));
        proof.Exercise("source availability", () => ExerciseAvailability(proof));
        proof.Exercise("practice strategy lifecycle", () => ExerciseStrategyLifecycle(proof));
        proof.Exercise("known negative and zero open links", () => ExerciseKnownOpenLinkMarks(proof));
        proof.Exercise("natural replay completion", () => ExerciseNaturalCompletion(proof));
        proof.Exercise("notification delivery", () => ExerciseNotifications(proof));
        foreach (var fault in Enum.GetValues<UncheckedSaveFault>())
            proof.Exercise("unchecked Save revocation with " + fault, () => ExerciseUncheckedSaveRevocation(proof, fault));
        proof.Exercise("unavailable notification storage", () => ExerciseUnavailableStorage(proof));
        proof.Exercise("encrypted persistence boundary", () => ExerciseNotificationDurability(proof));
        proof.Exercise("practice result-save privacy", () => ExercisePracticeSaveFailure(proof));
        foreach (var scenario in Enum.GetValues<PrivateMaterialCase>())
            proof.Exercise("blocked private material " + scenario, () => ExercisePrivateMaterial(proof, scenario));
        foreach (var scenario in Enum.GetValues<EncryptedOutboxCase>())
            proof.Exercise("encrypted notification outbox " + scenario, () => ExerciseEncryptedOutbox(proof, scenario));
        proof.Save();
        return proof.Failures;
    }

    private static void ExercisePractice(Proof proof)
    {
        var services = App.Services;
        var trading = proof.Owner.Trading;
        var original = trading.ActiveTab;
        var tabs = new List<ChartTab>();
        var windows = new List<Window>();
        decimal oldBalance = services.Settings.ReplayStartBalance;
        decimal oldPerUnit = services.Settings.ReplayFeePerUnit;
        decimal oldRate = services.Settings.ReplayNotionalFeeRate;
        int oldSeed = services.Settings.ReplayRandomSeed;
        int oldDays = services.Settings.ReplayLengthDays;
        bool oldSound = services.Settings.SoundAlerts;
        string sourceId = "kvp-replay-ui-" + Guid.NewGuid().ToString("N");
        var source = new Instrument { FeedId = sourceId, Symbol = "KVP-PRACTICE", Description = "Isolated recorded practice fixture",
            Exchange = "Fixture", TickSize = 1, VolumeStep = 0.5m, PointValue = 10, QuoteAsset = "USD" };
        var feed = new KvpSceneFeed(sourceId, source) { Now = Start.AddMilliseconds(500) };
        var tape = new[] {
            new Trade(1, Start.AddMinutes(-2), 97, 10, TradeSide.Buy),
            new Trade(2, Start.AddMinutes(-1), 99, 10, TradeSide.Sell),
            new Trade(21, Start, 100, 10, TradeSide.Buy),
            new Trade(22, Start.AddSeconds(1), 110, 10, TradeSide.Buy),
            new Trade(23, Start.AddSeconds(2), 120, 10, TradeSide.Sell),
            new Trade(24, Start.AddSeconds(3), 90, 10, TradeSide.Sell),
            new Trade(25, Start.AddSeconds(4), 105, 10, TradeSide.Buy),
            new Trade(26, Start.AddSeconds(29), 100, 10, TradeSide.Buy) };
        feed.Trades.AddRange(tape.Take(3));
        KvpSceneFeed.Register(services, new ConnectionProfile { Id = sourceId, Name = "KVP isolated practice source", Kind = ConnectionKind.Simulator, HistoryHours = 3 }, feed);
        string tapePath = TickFile.DayPath(AppPaths.Ticks, sourceId, source.Symbol, DateOnly.FromDateTime(Start));
        using (var writer = new TickFile.Writer(tapePath, source)) foreach (var trade in tape) writer.Write(trade);
        string strategyPath = Path.Combine(AppPaths.Strategies, "KvpUiCausalReplay" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(strategyPath, StrategySource);
        SimBroker? normal = null;
        decimal oldNormalFee = 0, oldNormalRate = 0;
        try
        {
            services.Settings.ReplayStartBalance = 1000;
            services.Settings.ReplayFeePerUnit = 1;
            services.Settings.ReplayNotionalFeeRate = 0.001m;
            services.Settings.SoundAlerts = false;
            proof.Owner.Navigate("trading");
            var normalTab = proof.Await(trading.NewTabAsync(sourceId, source.Symbol, ChartPeriod.M1), "normal simulation opens against only the deterministic fake feed");
            tabs.Add(normalTab);
            proof.Until(() => normalTab.Model is { Series.Count: > 0, LastLayout: not null } && !normalTab.Model.Data.IsLoadingHistory,
                "recorded source renders in the actual normal chart");
            var normalModel = normalTab.Model ?? throw new InvalidOperationException("Normal chart has no model.");
            normal = services.SimBrokerFor(normalModel.Data);
            oldNormalFee = normal.CommissionPerUnit;
            oldNormalRate = normal.CommissionRate;
            normal.CommissionPerUnit = 0.5m;
            normal.CommissionRate = 0;
            var normalBefore = services.Journal.FillsOf(AccountCenter.SimId).ToArray();
            var normalEntry = new AccountEntry(AccountCenter.SimId, "Simulated account", AccountKind.Sim, false, true);
            var normalAccountBefore = services.AccountCenter.Snapshot(normalEntry);
            decimal normalNetBefore = services.Portfolio.RealizedPnL;
            decimal normalCommissionBefore = services.Portfolio.Commission;
            normal.Market(OrderDirections.Buy, 1, "Isolated normal simulation, not a practice fill");
            proof.Check(normal.Position!.Volume == 1 && normal.MyTrades.Last().Price == 101,
                "normal simulated entry uses its normal one-tick spread at 101, not replay's print-fill policy");

            var selected = ReplaySelection.Selected(source, Start, Start.AddSeconds(30), Seed, blind: true);
            ChartTab Open(ReplaySelection selection)
            {
                proof.Await(trading.OpenPracticeAsync(selection, ChartPeriod.M1), "selected seeded practice opens through the normal chart workflow");
                var tab = trading.ActiveTab ?? throw new InvalidOperationException("Practice did not select a chart.");
                tabs.Add(tab);
                proof.Until(() => tab.Model is { LastLayout: not null } && !tab.Model.Data.IsLoadingHistory,
                    "practice warm-up completes and actual chart layout is rendered");
                return tab;
            }
            var firstTab = Open(selected);
            var secondTab = Open(selected);
            var first = services.ReplaySessions.TryGet(firstTab.Model!.Data)!;
            var second = services.ReplaySessions.TryGet(secondTab.Model!.Data)!;
            var firstFeed = first.Feed ?? throw new InvalidOperationException("First practice has no owned feed.");
            var secondFeed = second.Feed ?? throw new InvalidOperationException("Second practice has no owned feed.");
            proof.Check(first.RunId != second.RunId && first.Data.Instrument.Key != second.Data.Instrument.Key
                && !ReferenceEquals(first.Account.Portfolio, second.Account.Portfolio) && !ReferenceEquals(first.Account.Portfolio, services.Portfolio),
                "concurrent practices own independent feed identities and portfolios, separate from normal simulation");
            proof.Check(firstFeed.Paused && secondFeed.Paused && first.ClockUtc == Start && second.ClockUtc == Start,
                "each seeded run starts paused at its own selected market clock");
            proof.Check(first.Account.Journal.Count == 0 && second.Account.Journal.Count == 0,
                "historical warm-up does not manufacture practice executions");
            var days = new[] { new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 15) };
            var random = ReplaySelection.Random([new(source, days)], Seed, true, 3);
            var reordered = ReplaySelection.Random([new(source, days.Reverse().ToArray())], Seed, true, 3);
            proof.Check(random == reordered && random.EndUtc == new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
                "seeded random selection is input-order independent and never treats the isolated recorded day as a consecutive window");

            trading.Activate(firstTab);
            trading.SetTraderVisible(firstTab, true);
            proof.Pump(0.15);
            var accountBox = (ComboBox)typeof(ChartTraderPanel).GetField("_account", Fields)!.GetValue(firstTab.Trader)!;
            var cross = (Button)typeof(ChartTraderPanel).GetField("_cross", Fields)!.GetValue(firstTab.Trader)!;
            firstTab.Trader!.Refresh();
            var accountRows = accountBox.Items.OfType<ComboBoxItem>().Select(item => item.Tag).OfType<AccountOption>().ToArray();
            proof.Check(accountRows.Length == 1 && accountRows[0].Id == first.AccountId && !accountRows[0].IsReal && !cross.IsEnabled,
                "actual practice account selector contains only its owned paper account and disables cross trading");
            trading.SetChartAccount(firstTab, "scene-real-route");
            proof.Await(trading.SetCrossTradingAsync(firstTab, "UNROUTABLE"), "normal cross-selection method rejects practice routing");
            proof.Check(firstTab.AccountId == first.AccountId && firstTab.CrossData == null && firstTab.CrossSymbol == null
                && ReferenceEquals(trading.AccountOf(firstTab), first.Account), "external account and cross-instrument attempts cannot alter the practice order route");
            foreach (string id in new[] { "scene-real-route", "sim", second.AccountId })
                proof.Rejects<InvalidOperationException>(() => services.AccountFor(first.Data, id), "practice rejects foreign account resolution: " + (id == second.AccountId ? "another run" : id));
            proof.Rejects<InvalidOperationException>(() => services.SimBrokerFor(first.Data), "practice cannot obtain the shared normal simulator");

            services.Strategies.Refresh();
            var file = services.Strategies.Files.Single(item => item.Path == strategyPath);
            ChartStrategy Launch(ChartTab tab, string prefix, bool sell, decimal quantity, int exitId)
            {
                var parameters = new Dictionary<string, object?> { ["EventPrefix"] = prefix, ["SellFirst"] = sell,
                    ["Quantity"] = quantity, ["ExitId"] = exitId };
                proof.Await(trading.AddStrategyAsync(tab, file, parameters, openSettings: false), "real strategy source compiles and attaches through the chart consumer");
                var strategy = tab.Strategies.Single();
                strategy.Live = true;
                proof.Await(trading.RunStrategyAsync(tab, strategy), "normal chart execution starts a run-owned compiled strategy");
                proof.Check(file.LastCompile?.Success == true && strategy.Runner is { IsRunning: true, Persist: false }
                    && ReferenceEquals(strategy.Runner.Account, services.ReplaySessions.TryGet(tab.Model!.Data)!.Account),
                    "compiled strategy has no shared-simulator, real-broker or persistent live-ledger route");
                return strategy;
            }
            var firstStrategy = Launch(firstTab, "kvp-ui-first-", false, 1, 23);
            var secondStrategy = Launch(secondTab, "kvp-ui-second-", true, 2, 999);
            proof.Await(services.Alerts.FlushAsync(), "normal alert center drains strategy initialization and warm-up");
            proof.Check(!services.Alerts.Snapshot().Recent.Any(alert => alert.Id.StartsWith("kvp-ui-first-", StringComparison.Ordinal)
                || alert.Id.StartsWith("kvp-ui-second-", StringComparison.Ordinal)), "compiled OnInit and historical warm-up remain silent instead of flooding Strategy.Alert");

            trading.Activate(firstTab);
            var play = Named<ToggleButton>(trading, "ReplayPlay");
            proof.PointerClick(play);
            proof.Check(!firstFeed.Paused && secondFeed.Paused, "actual Play control starts only the active run");
            proof.PointerClick(play);
            proof.Check(firstFeed.Paused && secondFeed.Paused, "actual Pause control leaves both independent clocks paused");
            // Seek is the normal recorded-feed consumer. Both prints deliberately enter one batch;
            // using the final batch price for an earlier strategy order would fail the literal fill assertions.
            firstFeed.SeekForward(Start.AddSeconds(2));
            proof.Until(() => first.ClockUtc == Start.AddSeconds(2) && first.Account.Journal.Count == 2,
                "batched replay delivers both causal strategy executions");
            secondFeed.SeekForward(Start.AddSeconds(1));
            proof.Until(() => second.ClockUtc == Start.AddSeconds(1) && second.Account.Position!.Volume == -2,
                "second practice independently opens its short without advancing the first clock");
            var firstFills = first.Account.Journal.ToArray();
            proof.Check(firstFills.Length == 2 && firstFills[0].Price == 110 && firstFills[0].Time == Start.AddSeconds(1)
                && firstFills[1].Price == 120 && firstFills[1].Time == Start.AddSeconds(2),
                "compiled replay strategy fills at each delivered print and market timestamp, not the later batch value");
            proof.Check(first.Result().RealizedNet == 95.7m && first.Result().Commission == 4.3m && first.Result().Equity == 1095.7m,
                "independent long example: 100 gross minus 2.10 entry fee and 2.20 exit fee gives 95.70 net");
            proof.Check(second.ClockUtc == Start.AddSeconds(1) && second.Account.Journal.Single().Price == 110
                && second.Result().Commission == 4.2m && normal.Position!.Volume == 1,
                "second run's short quantity, fee and clock do not inherit the first run or normal account state");
            proof.Await(services.Alerts.FlushAsync(), "actual strategy alerts reach the app-owned local history");
            var strategyAlerts = services.Alerts.Snapshot().Recent.Where(alert => alert.Kind == AlertKind.Strategy
                && (alert.Id.StartsWith("kvp-ui-first-tick-", StringComparison.Ordinal) || alert.Id.StartsWith("kvp-ui-second-tick-", StringComparison.Ordinal))).ToArray();
            proof.Check(strategyAlerts.Count(alert => alert.Id.StartsWith("kvp-ui-first-", StringComparison.Ordinal)) == 3
                && strategyAlerts.Count(alert => alert.Id.StartsWith("kvp-ui-second-", StringComparison.Ordinal)) == 2
                && strategyAlerts.All(alert => !alert.AllowExternal && alert.Instrument == "Practice"),
                "real compiled Strategy.Alert emits only delivered ticks, stays local and conceals a blind run");
            proof.Check(strategyAlerts.Any(alert => alert.Id == "kvp-ui-first-tick-22" && alert.TimeUtc == Start.AddSeconds(1)),
                "strategy notification availability uses the causal replay clock");
            proof.Until(() => trading.GetVisualDescendants().OfType<Border>().Any(border => border.Name == "UnifiedAlertToast"),
                "actual practice notifications render through the unified chart toast consumer");

            var concealed = services.AccountCenter.Snapshot(services.ReplaySessions.Entries.Single(entry => entry.Id == first.AccountId));
            proof.Check(firstTab.DisplaySymbol == "Practice" && firstTab.Model!.Settings.BlindReplay && first.IsBlind
                && !concealed.FinancialsKnown && !concealed.AnalysisKnown && concealed.Fills.Count == 0 && concealed.Orders.Count == 0
                && concealed.Positions.Single().Symbol == "Practice" && first.Account.Journal.Count == 2,
                "blind mode conceals the real UI/account snapshot, not its underlying run-owned fills or data");
            proof.Check(!Named<TextBlock>(trading, "ReplayClock").Text!.Contains("2026-09-07", StringComparison.Ordinal),
                "actual blind replay clock shows relative practice time rather than historical calendar time");
            proof.Pump(0.3);
            proof.Check(!firstStrategy.Overlay.Status.Contains(source.Symbol, StringComparison.Ordinal),
                "periodic live strategy legend refresh keeps the blind run's execution instrument concealed");
            proof.Capture(proof.Owner, "kvp_replay_blind");
            Click(Named<Button>(trading, "ReplayResult"));
            var resultWindow = proof.Owner.OwnedWindows.Single(window => window.GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "ReplayJournal"));
            windows.Add(resultWindow);
            proof.Check(!Named<TextBox>(resultWindow, "ReplayJournal").Text!.Contains("2026-09-07", StringComparison.Ordinal),
                "actual run-owned result journal conceals dated fills before Reveal");
            proof.Capture(resultWindow, "kvp_replay_blind_journal");
            resultWindow.Close();
            var beforeReveal = first.Result();
            Click(Named<Button>(trading, "ReplayReveal"));
            proof.Pump(0.1);
            var revealed = services.AccountCenter.Snapshot(services.ReplaySessions.Entries.Single(entry => entry.Id == first.AccountId));
            proof.Check(!first.IsBlind && !firstTab.Model!.Settings.BlindReplay && firstTab.DisplaySymbol == source.Symbol
                && revealed.FinancialsKnown && revealed.Analysis.RealizedNet == 95.7m && revealed.Fills.Count == 2,
                "actual Reveal control exposes symbol, dated fills and fee-aware account analysis");
            proof.Until(() => firstStrategy.Overlay.Status.Contains(source.Symbol, StringComparison.Ordinal),
                "the live strategy legend exposes its execution instrument only after explicit Reveal");
            proof.Check(first.Result().ClockUtc == beforeReveal.ClockUtc && first.Result().RealizedNet == beforeReveal.RealizedNet
                && first.Result().Fills.SequenceEqual(beforeReveal.Fills), "Reveal changes presentation only, never accounting or the replay clock");
            var idsBeforeRecalculation = services.Alerts.Snapshot().Recent.Select(alert => alert.Id).ToHashSet(StringComparer.Ordinal);
            proof.Await(trading.RunStrategyAsync(firstTab, firstStrategy, historyOnly: true), "normal chart historical recalculation completes while the actual strategy stays owned");
            proof.Await(services.Alerts.FlushAsync(), "historical recalculation drains without new historical alerts");
            proof.Check(!services.Alerts.Snapshot().Recent.Any(alert => !idsBeforeRecalculation.Contains(alert.Id)
                && alert.Id.StartsWith("kvp-ui-first-", StringComparison.Ordinal)) && first.Account.Journal.SequenceEqual(firstFills),
                "historical strategy recalculation neither floods alerts nor executes old signals in the practice account");
            proof.Capture(proof.Owner, "kvp_replay_revealed");

            var firstPending = first.Account.Stop(OrderDirections.Buy, 1, 200);
            var secondPending = second.Account.Stop(OrderDirections.Buy, 2, 200);
            var normalPending = normal.Stop(OrderDirections.Sell, 1, 80);
            second.Account.Cancel(firstPending);
            proof.Check(firstPending.State == OrderStates.Active, "another run cannot cancel a foreign owned order handle");
            first.Account.Cancel(firstPending);
            proof.Check(secondPending.State == OrderStates.Active && normalPending.State == OrderStates.Active,
                "cancelling one practice leaves the other practice and normal simulation protection untouched");
            var orderIds = first.Account.Orders.Concat(second.Account.Orders).Concat(normal.Orders).Select(order => order.Id).ToArray();
            proof.Check(orderIds.Distinct().Count() == orderIds.Length && firstPending.ExchangeOrderId != secondPending.ExchangeOrderId,
                "SDK order IDs remain process-unique while broker-style replay IDs are run-qualified");

            secondFeed.SeekForward(Start.AddSeconds(3));
            proof.Until(() => second.ClockUtc == Start.AddSeconds(3), "second practice advances to its own closing mark");
            trading.Activate(secondTab);
            Click(Named<Button>(trading, "ReplayEnd"));
            proof.Check(second.Ended && second.Account.Position!.Volume == 0 && second.Account.Journal.Count == 2
                && secondStrategy.Runner?.IsRunning != true && !second.Account.ActiveOrders.Any(),
                "actual End stops the compiled strategy, cancels pending orders and closes only that practice position");
            proof.Check(second.Result().RealizedNet == 392m && second.Result().Commission == 8m && second.Result().Equity == 1392m,
                "independent short example: (110 - 90) × 2 × 10 minus 4.20 and 3.80 fees gives 392.00 net");
            proof.Check(first.ClockUtc == Start.AddSeconds(2) && !first.Ended && normal.Position!.Volume == 1,
                "ending the second practice leaves the first clock/run and normal position unchanged");
            secondFeed.SeekForward(Start.AddSeconds(29));
            proof.Pump(0.1);
            proof.Check(second.ClockUtc == Start.AddSeconds(3) && second.Account.Market(OrderDirections.Buy, 1).Status == OrderStatus.Rejected
                && !Named<Button>(trading, "ReplayEnd").IsEnabled && !Named<ToggleButton>(trading, "ReplayPlay").IsEnabled,
                "ended practice refuses more data/orders and disables its actual end/play controls");
            trading.Activate(firstTab);
            Click(Named<Button>(trading, "ReplayEnd"));
            proof.Check(first.Ended && first.Account.Journal.Count == 2 && first.Result().RealizedNet == 95.7m,
                "ending a flat first practice does not add a spurious fill or fee");
            foreach (var run in new[] { first, second })
            {
                string saved = Path.Combine(AppPaths.Root, "Practice", run.RunId.ToString("N") + ".json");
                proof.Check(File.Exists(saved), "normal practice result consumer persists its own run result");
                if (File.Exists(saved))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(saved));
                    proof.Check(json.RootElement.GetProperty("RunId").GetGuid() == run.RunId
                        && json.RootElement.GetProperty("RealizedNet").GetDecimal() == (ReferenceEquals(run, first) ? 95.7m : 392m),
                        "persisted practice result retains run identity and independently expected fee-aware result");
                }
                proof.Replay.Add(run.Result());
            }
            Click(Named<Button>(trading, "ReplayResult"));
            resultWindow = proof.Owner.OwnedWindows.Single(window => window.GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "ReplayJournal"));
            windows.Add(resultWindow);
            proof.Check(Named<TextBox>(resultWindow, "ReplayJournal").Text!.Contains("2026-09-07 12:00:01", StringComparison.Ordinal),
                "ended/revealed actual journal displays the run's dated causal fills");
            proof.Capture(resultWindow, "kvp_replay_ended_journal");
            resultWindow.Close();

            var repeatedTab = Open(selected with { Blind = false });
            var repeated = services.ReplaySessions.TryGet(repeatedTab.Model!.Data)!;
            Launch(repeatedTab, "kvp-ui-repeat-", false, 1, 23);
            repeated.Feed!.SeekForward(Start.AddSeconds(2));
            proof.Until(() => repeated.Account.Journal.Count == 2, "same selected interval/seed repeats actual compiled-strategy executions");
            Click(Named<Button>(trading, "ReplayEnd"));
            proof.Check(repeated.Result().Seed == Seed && repeated.Result().RealizedNet == 95.7m
                && repeated.Account.Journal.Select(fill => (fill.Time, fill.Buy, fill.Quantity, fill.Price, fill.Commission))
                    .SequenceEqual(firstFills.Select(fill => (fill.Time, fill.Buy, fill.Quantity, fill.Price, fill.Commission))),
                "repeated selected seed/tape reproduces every fill and fee despite a separate run ID and different blind presentation");
            proof.Replay.Add(repeated.Result());

            var runsBeforeDialog = services.ReplaySessions.All.Select(run => run.RunId).ToHashSet();
            trading.OpenPracticeDialog();
            var dialog = proof.Owner.OwnedWindows.Single(window => window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "PracticeStart"));
            windows.Add(dialog);
            Named<NumericUpDown>(dialog, "PracticeSeed").Value = Seed;
            Named<NumericUpDown>(dialog, "PracticeDays").Value = 1;
            Named<NumericUpDown>(dialog, "PracticeBalance").Value = 1000;
            Named<NumericUpDown>(dialog, "PracticeFeePerUnit").Value = 1;
            Named<NumericUpDown>(dialog, "PracticeFeeRate").Value = 0.001m;
            Named<CheckBox>(dialog, "PracticeBlind").IsChecked = true;
            proof.Capture(dialog, "kvp_replay_random_configuration");
            Click(Named<Button>(dialog, "PracticeStart"));
            proof.Until(() => services.ReplaySessions.All.Any(run => !runsBeforeDialog.Contains(run.RunId)),
                "actual random-practice Start control creates a fresh run from recorded catalogs, without requesting a provider");
            var randomRun = services.ReplaySessions.All.Single(run => !runsBeforeDialog.Contains(run.RunId));
            var randomTab = trading.ActiveTab ?? throw new InvalidOperationException("Random practice did not select a chart.");
            tabs.Add(randomTab);
            proof.Until(() => randomTab.Model is { LastLayout: not null } && !randomTab.Model.Data.IsLoadingHistory,
                "actual random-practice chart renders its available recorded history");
            proof.Check(randomRun.Selection.Seed == Seed && randomRun.Selection.Blind && randomRun.Fees == new ReplayFees(1, 0.001m)
                && randomRun.Account.Portfolio!.BeginBalance == 1000 && randomTab.AccountId == randomRun.AccountId
                && services.Settings.ReplayRandomSeed == Seed && services.Settings.ReplayLengthDays == 1,
                "actual seed, length, balance, fees and blind controls reach the normal owned practice consumer");
            proof.Capture(proof.Owner, "kvp_replay_random_started");
            if (!randomRun.Ended) Click(Named<Button>(trading, "ReplayEnd"));
            proof.Replay.Add(randomRun.Result());

            feed.PushTrades(source, new Trade(100, Start.AddMinutes(1), 120, 10, TradeSide.Buy));
            proof.Until(() => normalModel.Data.LastPrice == 120, "normal simulation consumes only its separate fake live print");
            normal.Flatten();
            var normalAfter = services.Journal.FillsOf(AccountCenter.SimId);
            var added = normalAfter.Where(fill => !normalBefore.Any(old => old.Id == fill.Id)).ToArray();
            proof.Check(normal.Position!.Volume == 0 && services.Portfolio.RealizedPnL - normalNetBefore == 179m
                && services.Portfolio.Commission - normalCommissionBefore == 1m,
                "normal simulation remains independent: (119 - 101) × 10 minus two 0.50 fees gives 179.00 net");
            proof.Check(added.Length == 2 && added[0].Price == 101 && added[1].Price == 119
                && RoundTrips.Analyze(added).RealizedNet == 179m
                && normalAfter.All(fill => !fill.Id.StartsWith("REPLAY-", StringComparison.Ordinal)
                    && !fill.OrderId.StartsWith("REPLAY-", StringComparison.Ordinal)),
                "actual normal account journal contains only its two fills, never practice fills or replay order IDs");
            var normalAccountAfter = services.AccountCenter.Snapshot(normalEntry);
            proof.Check(normalAccountAfter.Analysis.RealizedNet - normalAccountBefore.Analysis.RealizedNet == 179m
                && normalAccountAfter.Analysis.Commission - normalAccountBefore.Analysis.Commission == 1m
                && normalAccountAfter.Fills.Count - normalAccountBefore.Fills.Count == 2,
                "actual account-center analysis independently consumes only the normal journal's fee-aware round trip");
            proof.Check(first.Account.Journal.All(fill => fill.Id.StartsWith(first.AccountId + ":", StringComparison.Ordinal))
                && second.Account.Journal.All(fill => fill.Id.StartsWith(second.AccountId + ":", StringComparison.Ordinal))
                && first.Account.Journal.All(fill => !second.Account.Journal.Any(other => other.Id == fill.Id)),
                "each practice journal retains disjoint run-owned fill identities");
            proof.NormalJournal.AddRange(added);
            trading.Activate(normalTab);
            proof.Capture(proof.Owner, "kvp_replay_normal_simulation");
        }
        finally
        {
            foreach (var window in windows.Where(window => window.IsVisible)) window.Close();
            foreach (var tab in tabs.AsEnumerable().Reverse())
            {
                if (tab.Model != null && services.ReplaySessions.TryGet(tab.Model.Data) is { } run
                    && proof.Replay.All(result => result.RunId != run.RunId)) proof.Replay.Add(run.Result());
                foreach (var strategy in tab.Strategies.ToArray()) trading.RemoveStrategy(tab, strategy);
                trading.CloseChart(tab);
            }
            if (original != null) trading.Activate(original);
            if (normal != null)
            {
                normal.CancelAll();
                if (normal.Position!.Volume != 0) normal.Flatten();
                normal.CommissionPerUnit = oldNormalFee;
                normal.CommissionRate = oldNormalRate;
            }
            services.Settings.ReplayStartBalance = oldBalance;
            services.Settings.ReplayFeePerUnit = oldPerUnit;
            services.Settings.ReplayNotionalFeeRate = oldRate;
            services.Settings.ReplayRandomSeed = oldSeed;
            services.Settings.ReplayLengthDays = oldDays;
            services.Settings.SoundAlerts = oldSound;
            services.Settings.Save();
            File.Delete(strategyPath);
            services.Strategies.Refresh();
        }
    }

    private static void ExerciseStrategyLifecycle(Proof proof)
    {
        var services = App.Services;
        var trading = proof.Owner.Trading;
        var original = trading.ActiveTab;
        decimal oldBalance = services.Settings.ReplayStartBalance, oldFee = services.Settings.ReplayFeePerUnit, oldRate = services.Settings.ReplayNotionalFeeRate;
        int normalFillsBefore = services.Journal.CountOf(AccountCenter.SimId);
        string id = "kvp-replay-lifecycle-ui-" + Guid.NewGuid().ToString("N");
        var source = new Instrument { FeedId = id, Symbol = "KVP-LEDGER-GAP", TickSize = 1, VolumeStep = 1, PointValue = 10, QuoteAsset = "USD" };
        using (var writer = new TickFile.Writer(TickFile.DayPath(AppPaths.Ticks, id, source.Symbol, DateOnly.FromDateTime(Start)), source))
        {
            writer.Write(new Trade(1, Start.AddMinutes(-2), 97, 10, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddMinutes(-1), 99, 10, TradeSide.Sell));
            writer.Write(new Trade(3, Start.AddSeconds(-30), 100, 10, TradeSide.Buy));
            writer.Write(new Trade(21, Start, 100, 10, TradeSide.Buy));
            writer.Write(new Trade(22, Start.AddSeconds(1), 110, 10, TradeSide.Buy));
            writer.Write(new Trade(23, Start.AddMilliseconds(1250), 110, 10, TradeSide.Buy));
            writer.Write(new Trade(24, Start.AddMilliseconds(1500), 110, 10, TradeSide.Buy));
            writer.Write(new Trade(25, Start.AddSeconds(2), 104, 10, TradeSide.Sell));
            writer.Write(new Trade(26, Start.AddSeconds(29), 104, 10, TradeSide.Sell));
        }
        string scriptPath = Path.Combine(AppPaths.Strategies, "KvpUiProtectedGap" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(scriptPath, ProtectedGapStrategySource);
        ChartTab? tab = null;
        var windows = new List<Window>();
        try
        {
            services.Settings.ReplayStartBalance = 1000;
            services.Settings.ReplayFeePerUnit = 1;
            services.Settings.ReplayNotionalFeeRate = 0.001m;
            proof.Owner.Navigate("trading");
            proof.Await(trading.OpenPracticeAsync(ReplaySelection.Selected(source, Start, Start.AddSeconds(30), Seed, blind: true), ChartPeriod.M1),
                "actual chart opens a blind protected-strategy practice run");
            tab = trading.ActiveTab ?? throw new InvalidOperationException("Protected practice did not select a chart.");
            var chart = tab;
            var run = services.ReplaySessions.TryGet(chart.Model!.Data)!;
            var feed = run.Feed!;
            proof.Until(() => !run.Data.IsLoadingHistory && chart.Model!.LastLayout != null, "protected practice warm-up reaches actual chart rendering");
            services.Strategies.Refresh();
            var file = services.Strategies.Files.Single(item => item.Path == scriptPath);
            proof.Await(trading.AddStrategyAsync(chart, file, openSettings: false), "protected gap strategy compiles through the normal source/catalog consumer");
            var strategy = chart.Strategies.Single();
            strategy.Live = true;
            proof.Await(trading.RunStrategyAsync(chart, strategy), "normal chart execution starts the owned protected strategy");
            feed.SeekForward(Start.AddTicks(1));
            proof.Until(() => run.Account.ActiveOrders.Any(order => order.Type == OrderTypes.Stop && !order.ReduceOnly),
                "compiled strategy places its real owned stop-entry order from the delivered 100 print");
            feed.SeekForward(Start.AddSeconds(1));
            proof.Until(() => run.Account.Position!.Volume == 1 && strategy.Runner is { OwnPosition: 1 } && run.Account.ActiveOrders.Count() == 2,
                "actual gap execution is booked and protected before any settings or period restart");
            var stop = run.Account.ActiveOrders.Single(order => order.Type == OrderTypes.Stop);
            var target = run.Account.ActiveOrders.Single(order => order.Type == OrderTypes.Limit);
            var protectionIds = strategy.Runner!.ProtectionOrderIds.Order(StringComparer.Ordinal).ToArray();
            proof.Check(run.Account.Journal.Single().Price == 110 && run.Account.Journal.Single().Time == Start.AddSeconds(1)
                && run.Account.Journal.Single().Commission == 2.1m && stop.TriggerPrice == 105 && target.Price == 120,
                "confirmed 110 gap fill, actual market time and 2.10 fee set the real stop105/target120 basis, never stale signal100");
            var entryOrder = run.Account.Orders.Single(order => order.ExchangeOrderId == run.Account.Journal.Single().OrderId);
            var confirmed = ((IAccountExecutionSource)run.Account).GetExecutions(entryOrder);
            proof.Check(confirmed.CompleteCoverage && confirmed.Executions.Count == 1 && confirmed.Executions[0].Price == 110
                && confirmed.Executions[0].TimeUtc == Start.AddSeconds(1) && confirmed.Executions[0].AccountFee == 2.1m
                && confirmed.Executions[0].FeeCurrency == "USD",
                "actual owned gap order supplies complete confirmed execution facts, including real price/time and account-denominated fee");
            void CheckOwnedState(string stage)
            {
                var runner = strategy.Runner ?? throw new InvalidOperationException(stage + ": owned runner is unavailable.");
                proof.Check(runner.IsRunning && !runner.Persist && ReferenceEquals(runner.Account, run.Account)
                    && runner.OwnPosition == 1 && runner.Ledger.AveragePrice == 110 && runner.Ledger.Fills.Count == 1
                    && runner.Ledger.Fills[0].Price == 110 && runner.Ledger.Fills[0].Time == Start.AddSeconds(1)
                    && runner.Ledger.Fills[0].AccountFee == 2.1m && runner.Ledger.Fees == 2.1m
                    && runner.Ledger.Fills[0].ExecutionId == confirmed.Executions[0].Id
                    && runner.Ledger.Realized == -2.1m && runner.Ledger.CostsComplete && run.Account.Journal.Count == 1,
                    stage + ": actual strategy ledger retains its owned position, confirmed fill price/time and single booked execution");
                proof.Check(runner.ProtectionOrderIds.Order(StringComparer.Ordinal).SequenceEqual(protectionIds)
                    && run.Account.ActiveOrders.Count() == 2 && stop.State == OrderStates.Active && target.State == OrderStates.Active
                    && stop.TriggerPrice == 105 && target.Price == 120,
                    stage + ": original owned stop/target handles remain effective without a cancel/recreate protection gap");
                proof.StrategyLedgers.Add(new { stage, run.RunId, period = chart.Model!.Period.Code, runner.OwnPosition,
                    runner.Ledger.AveragePrice, runner.Ledger.Realized, fills = runner.Ledger.Fills.ToArray(), protection = runner.ProtectionOrderIds.ToArray() });
            }
            CheckOwnedState("After confirmed gap entry");
            proof.Check(strategy.HistoryMetrics is { TotalTrades: > 0 }, "real historical strategy consumer has a dated completed trade to conceal in blind settings");
            trading.OpenStrategySettings(chart, strategy);
            var blindSettings = proof.Owner.OwnedWindows.OfType<StrategyChartWindow>().Single();
            windows.Add(blindSettings);
            proof.Pump(0.05);
            string blindText = VisibleText(blindSettings);
            proof.Check(!blindText.Contains(source.Symbol, StringComparison.Ordinal) && !HasAbsoluteFixtureDate(blindText)
                && blindSettings.GetVisualDescendants().OfType<TradingHub.App.Controls.EquityChart>().All(curve => !curve.IsEffectivelyVisible),
                "actual blind StrategyChartWindow conceals instrument, absolute historical dates and the date-bearing historical equity renderer");
            proof.Capture(blindSettings, "kvp_replay_blind_strategy_settings");
            Click(LabeledButton(blindSettings, "Apply"));
            proof.Until(() => !strategy.Busy && strategy.Runner is { IsRunning: true }, "actual strategy Apply finishes its owned restart");
            CheckOwnedState("After actual settings Apply");
            blindSettings.Close();
            proof.Owner.Activate();
            trading.Activate(chart);
            var periodMenu = (Button)typeof(ChartToolbar).GetField("_periodMenu", Fields)!.GetValue(trading.Toolbar)!;
            proof.PointerClick(periodMenu);
            proof.Pump(0.05);
            var periodChoices = ((Flyout)periodMenu.Flyout!).Content as Control ?? throw new InvalidOperationException("Actual period menu did not open.");
            Click(LabeledButton(periodChoices, ChartPeriod.M5.DisplayName));
            proof.Until(() => chart.Model!.Period == ChartPeriod.M5 && !strategy.Busy && strategy.Runner is { IsRunning: true },
                "actual period-menu selection changes M1 to M5 and completes the normal strategy restart");
            CheckOwnedState("After actual M5 period selection");
            proof.Await(trading.RunStrategyAsync(chart, strategy, keepProtection: true), "explicit normal chart strategy restart reuses run-owned state");
            CheckOwnedState("After explicit chart restart");
            var ownedBeforeVisual = strategy.Runner!;
            void CheckVisualIsolation(string stage)
            {
                var runner = strategy.Runner ?? throw new InvalidOperationException(stage + ": visual runner is unavailable.");
                proof.Check(!strategy.Live && runner.IsRunning && !runner.Persist && runner.Account is SimBroker shadow
                    && !ReferenceEquals(runner.Account, run.Account) && !runner.Account.IsReal
                    && runner.OwnPosition == 1 && runner.Account.Position!.Volume == 1 && runner.Account.Position.AveragePrice == 111
                    && runner.Ledger.Fills.Count == 1 && runner.Ledger.Realized == 0
                    && shadow.MyTrades.Count == 1 && shadow.MyTrades[0].Price == 111 && shadow.MyTrades[0].Commission == 0,
                    stage + ": actual visual strategy trades its distinct zero-fee private simulator at synthetic ask111, not the owned replay account");
                proof.Check(runner.Ledger.Fills[0].ExecutionId != confirmed.Executions[0].Id && runner.Ledger.Fees == 0,
                    stage + ": private shadow ledger never adopts the authoritative owned replay execution ID or its 2.10 fee");
                proof.Check(run.Account.Position!.Volume == 1 && run.Account.Journal.Count == 1
                    && run.Account.Journal[0].Price == 110 && run.Account.Journal[0].Commission == 2.1m
                    && ownedBeforeVisual.OwnPosition == 1 && ownedBeforeVisual.Ledger.Fills.Count == 1
                    && ownedBeforeVisual.Ledger.Fills[0].ExecutionId == confirmed.Executions[0].Id
                    && ownedBeforeVisual.Ledger.Realized == -2.1m && ownedBeforeVisual.Ledger.AveragePrice == 110
                    && run.Account.ActiveOrders.Count() == 2 && run.Account.ActiveOrders.Contains(stop) && run.Account.ActiveOrders.Contains(target)
                    && stop.State == OrderStates.Active && target.State == OrderStates.Active
                    && stop.TriggerPrice == 105 && target.Price == 120
                    && !runner.ProtectionOrderIds.Intersect(protectionIds, StringComparer.Ordinal).Any()
                    && services.Journal.CountOf(AccountCenter.SimId) == normalFillsBefore,
                    stage + ": real shadow fills cannot mutate owned replay execution history, fees, original native protection handles or normal simulation journal");
                proof.StrategyLedgers.Add(new { stage, run.RunId, mode = "Visual only", period = chart.Model!.Period.Code,
                    runner.OwnPosition, runner.Ledger.Realized, runner.Ledger.CostsComplete, fills = runner.Ledger.Fills.ToArray(),
                    actualSimulatorTrades = ((SimBroker)runner.Account).MyTrades.ToArray(),
                    protection = runner.ProtectionOrderIds.ToArray(), replayPosition = run.Account.Position!.Volume,
                    replayJournal = run.Account.Journal.ToArray(), replayProtection = protectionIds });
            }
            trading.OpenStrategySettings(chart, strategy);
            var visualSettings = proof.Owner.OwnedWindows.OfType<StrategyChartWindow>().Single();
            windows.Add(visualSettings);
            var visualMode = (RadioButton)typeof(StrategyChartWindow).GetField("_visual", Fields)!.GetValue(visualSettings)!;
            var visualAccount = (ComboBox)typeof(StrategyChartWindow).GetField("_account", Fields)!.GetValue(visualSettings)!;
            proof.PointerClick(visualMode);
            proof.Check(visualMode.IsChecked == true && !visualAccount.IsEnabled,
                "actual Visual only radio changes the execution mode and disables account routing before Apply");
            Click(LabeledButton(visualSettings, "Apply"));
            proof.Until(() => !strategy.Busy && !strategy.Live && strategy.Runner is { IsRunning: true }
                && !ReferenceEquals(strategy.Runner.Account, run.Account),
                "actual practice-to-visual Apply starts a distinct live shadow without restoring an account-bound replay memento onto it");
            var firstShadow = strategy.Runner!.Account;
            feed.SeekForward(Start.AddMilliseconds(1250));
            proof.Until(() => strategy.Runner is { OwnPosition: 1 } && strategy.Runner.Ledger.Fills.Count == 1,
                "real compiled strategy consumes a new replay print and actually fills its first private visual order");
            CheckVisualIsolation("After actual practice-to-visual Apply");
            proof.Check(!VisibleText(visualSettings).Contains(source.Symbol, StringComparison.Ordinal) && !HasAbsoluteFixtureDate(VisibleText(visualSettings)),
                "switching to visual mode cannot reveal the blind practice instrument or historical dates");
            proof.Capture(visualSettings, "kvp_replay_visual_shadow_mode");
            visualSettings.Close();
            proof.Owner.Activate();
            trading.Activate(chart);
            proof.PointerClick(periodMenu);
            proof.Pump(0.05);
            var visualPeriods = ((Flyout)periodMenu.Flyout!).Content as Control ?? throw new InvalidOperationException("Visual-mode period menu did not open.");
            Click(LabeledButton(visualPeriods, ChartPeriod.M1.DisplayName));
            proof.Until(() => chart.Model!.Period == ChartPeriod.M1 && !strategy.Busy && !strategy.Live
                && strategy.Runner is { IsRunning: true } && !ReferenceEquals(strategy.Runner.Account, firstShadow)
                && !ReferenceEquals(strategy.Runner.Account, run.Account),
                "actual M5-to-M1 period change stops the first visual consumer and starts another distinct shadow without overwriting replay state");
            feed.SeekForward(Start.AddMilliseconds(1500));
            proof.Until(() => strategy.Runner is { OwnPosition: 1 } && strategy.Runner.Ledger.Fills.Count == 1,
                "restarted visual consumer independently executes the next compiled strategy signal on its new private account");
            CheckVisualIsolation("After actual visual-mode period restart");
            proof.Capture(proof.Owner, "kvp_replay_visual_preserves_owned_protection");

            trading.OpenStrategySettings(chart, strategy);
            var returnSettings = proof.Owner.OwnedWindows.OfType<StrategyChartWindow>().Single();
            windows.Add(returnSettings);
            var executeMode = (RadioButton)typeof(StrategyChartWindow).GetField("_live", Fields)!.GetValue(returnSettings)!;
            var returnAccount = (ComboBox)typeof(StrategyChartWindow).GetField("_account", Fields)!.GetValue(returnSettings)!;
            proof.PointerClick(executeMode);
            var routes = returnAccount.Items.OfType<ComboBoxItem>().Select(item => item.Tag).OfType<AccountOption>().ToArray();
            proof.Check(executeMode.IsChecked == true && returnAccount.IsEnabled && routes.Length == 1
                && routes[0].Id == run.AccountId && !routes[0].IsReal,
                "actual execute-on-account radio offers only this practice owner, never normal simulation, a real account or the visual shadow");
            Click(LabeledButton(returnSettings, "Apply"));
            proof.Until(() => !strategy.Busy && strategy.Live && strategy.Runner is { IsRunning: true }
                && ReferenceEquals(strategy.Runner.Account, run.Account),
                "actual visual-to-practice Apply restores the original run-owned strategy after two real private shadow executions");
            CheckOwnedState("After actual visual-to-practice Apply");
            proof.Capture(returnSettings, "kvp_replay_returned_owned_strategy");
            returnSettings.Close();
            proof.Owner.Activate();
            trading.Activate(chart);
            Click(Named<Button>(trading, "ReplayReveal"));
            trading.OpenStrategySettings(chart, strategy);
            var revealedSettings = proof.Owner.OwnedWindows.OfType<StrategyChartWindow>().Single();
            windows.Add(revealedSettings);
            proof.Pump(0.05);
            proof.Check(VisibleText(revealedSettings).Contains(source.Symbol, StringComparison.Ordinal)
                && revealedSettings.GetVisualDescendants().OfType<TradingHub.App.Controls.EquityChart>().Any(curve => curve.IsEffectivelyVisible),
                "after actual Reveal the strategy settings deliberately expose instrument and real historical result rendering again");
            revealedSettings.GetVisualDescendants().OfType<TradingHub.App.Controls.EquityChart>().Single().BringIntoView();
            proof.Capture(revealedSettings, "kvp_replay_revealed_strategy_settings");
            revealedSettings.Close();
            feed.SeekForward(Start.AddSeconds(2));
            proof.Until(() => run.Account.Position!.Volume == 0 && strategy.Runner is { OwnPosition: 0 } && run.Account.Journal.Count == 2,
                "retained strategy protection consumes the next observed stop-gap print after all real UI restarts");
            var closedRunner = strategy.Runner!;
            var closed = closedRunner.Ledger.Trades.Single();
            proof.Check(run.Account.Journal[1].Price == 104 && run.Account.Journal[1].Time == Start.AddSeconds(2)
                && run.Account.Journal[1].Commission == 2.04m && closedRunner.Ledger.Fills.Count == 2
                && closedRunner.Ledger.Fills[1].Price == 104 && closedRunner.Ledger.Fills[1].Time == Start.AddSeconds(2)
                && closed.Commission == 4.14m && closed.NetPnl == -64.14m && closedRunner.Ledger.Realized == -64.14m
                && run.Result().RealizedNet == -64.14m && !run.Account.ActiveOrders.Any(),
                "actual confirmed stop104/time/fee2.04 closes the retained ledger: gross-60 less fees4.14 is net-64.14, with no stale target left working");
            proof.StrategyLedgers.Add(new { stage = "After retained protection closes", run.RunId, closedRunner.OwnPosition,
                closedRunner.Ledger.Realized, fills = closedRunner.Ledger.Fills.ToArray(), trade = closed });
            proof.Check(services.Journal.CountOf(AccountCenter.SimId) == normalFillsBefore,
                "actual strategy Apply/period/restart and protective fills never enter the normal simulation journal");
            proof.Capture(proof.Owner, "kvp_replay_retained_protection_closed");
            Click(Named<Button>(trading, "ReplayEnd"));
            proof.Replay.Add(run.Result());
        }
        finally
        {
            foreach (var window in windows.Where(window => window.IsVisible)) window.Close();
            if (tab != null)
            {
                foreach (var strategy in tab.Strategies.ToArray()) trading.RemoveStrategy(tab, strategy);
                trading.CloseChart(tab);
            }
            if (original != null) trading.Activate(original);
            services.Settings.ReplayStartBalance = oldBalance;
            services.Settings.ReplayFeePerUnit = oldFee;
            services.Settings.ReplayNotionalFeeRate = oldRate;
            services.Settings.Save();
            File.Delete(scriptPath);
            services.Strategies.Refresh();
        }
    }

    private static void ExerciseKnownOpenLinkMarks(Proof proof)
    {
        var services = App.Services;
        var trading = proof.Owner.Trading;
        var original = trading.ActiveTab;
        decimal oldBalance = services.Settings.ReplayStartBalance, oldFee = services.Settings.ReplayFeePerUnit, oldRate = services.Settings.ReplayNotionalFeeRate;
        int normalFillsBefore = services.Journal.CountOf(AccountCenter.SimId);
        string id = "kvp-replay-open-link-ui-" + Guid.NewGuid().ToString("N");
        DateTime entryTime = Start.AddSeconds(1);
        var source = new Instrument { FeedId = id, Symbol = "KVP-OPEN-LINK-MARKS", TickSize = 1, VolumeStep = 1, PointValue = 10, QuoteAsset = "USD" };
        using (var writer = new TickFile.Writer(TickFile.DayPath(AppPaths.Ticks, id, source.Symbol, DateOnly.FromDateTime(Start)), source, includeProvenance: true))
        {
            writer.Write(new Trade(1, Start.AddMinutes(-2), 97, 10, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddMinutes(-1), 99, 10, TradeSide.Buy));
            writer.Write(new Trade(21, entryTime, 100, 10, TradeSide.Buy));
            writer.Write(new Trade(22, Start.AddSeconds(2), -5, 10, TradeSide.Sell));
            writer.Write(new Trade(23, Start.AddSeconds(3), 0, 10, TradeSide.Buy));
            writer.Write(new Trade(24, Start.AddSeconds(29), 0, 10, TradeSide.Buy));
        }
        string scriptPath = Path.Combine(AppPaths.Strategies, "KvpUiOpenLinkMarks" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(scriptPath, OpenLinkStrategySource);
        ChartTab? tab = null;
        try
        {
            services.Settings.ReplayStartBalance = 10_000;
            services.Settings.ReplayFeePerUnit = 0;
            services.Settings.ReplayNotionalFeeRate = 0;
            proof.Owner.Navigate("trading");
            proof.Await(trading.OpenPracticeAsync(ReplaySelection.Selected(source, Start, Start.AddSeconds(30), Seed, blind: false), ChartPeriod.M1),
                "actual chart opens the recorded negative/zero mark practice with sufficient balance and zero fixture fees");
            tab = trading.ActiveTab ?? throw new InvalidOperationException("Known-mark practice did not select its actual chart.");
            var chart = tab;
            var run = services.ReplaySessions.TryGet(chart.Model!.Data)
                ?? throw new InvalidOperationException("Known-mark chart has no owned replay session.");
            var feed = run.Feed ?? throw new InvalidOperationException("Known-mark practice has no recorded replay producer.");
            proof.Until(() => !run.Data.IsLoadingHistory && chart.Model!.LastLayout != null,
                "known-mark practice reaches actual chart rendering after its historical warm-up");
            services.Strategies.Refresh();
            var file = services.Strategies.Files.Single(item => item.Path == scriptPath);
            proof.Await(trading.AddStrategyAsync(chart, file, openSettings: false),
                "actual catalog compiles the unprotected open-link strategy");
            var strategy = chart.Strategies.Single();
            strategy.Live = true;
            proof.Await(trading.RunStrategyAsync(chart, strategy), "normal chart consumer starts the owned unprotected strategy");
            feed.SeekForward(entryTime);
            proof.Until(() => run.Account.Position!.Volume == 2 && run.Account.Journal.Count == 1
                && strategy.Runner is { IsRunning: true, OwnPosition: 2 } && strategy.Runner.Ledger.Fills.Count == 1,
                "compiled strategy genuinely buys two owned units on the delivered 100 print before negative or zero marks");
            proof.Check(run.Account.Journal[0].Price == 100 && run.Account.Journal[0].Time == entryTime
                && run.Account.Journal[0].Commission == 0 && strategy.Runner!.Ledger.AveragePrice == 100
                && strategy.Runner.Ledger.Fees == 0 && strategy.Runner.Ledger.CostsComplete
                && !run.Account.ActiveOrders.Any(),
                "known entry100, quantity2, point-value10 and zero confirmed fees establish an open position without stops that could mask mark-to-market");

            void CheckRenderedMark(DateTime time, decimal mark, decimal expectedPnl, string name)
            {
                feed.SeekForward(time);
                proof.Until(() => {
                    trading.Tick();
                    return run.Data.Trades.Count > 0 && run.Data.LastPrice == mark && run.Data.Trades.LastTime == time
                        && strategy.Overlay.Links.Any(link => link.OpenTime == entryTime && link.CloseTime == time && link.ClosePrice == mark);
                }, "normal chart Tick/pump updates the actual strategy open link at the delivered " + name + " mark");
                var runner = strategy.Runner ?? throw new InvalidOperationException("Known-mark strategy runner disappeared.");
                var link = strategy.Overlay.Links.Last();
                proof.Check(link.OpenTime == entryTime && link.OpenPrice == 100 && link.CloseTime == time
                    && link.ClosePrice == mark && link.IsLong && link.Pnl == expectedPnl,
                    "actual " + name + " TradeLink keeps causal entry/mark coordinates and PnL " + expectedPnl + " = (" + mark + " - 100) × 2 × 10",
                    JsonSerializer.Serialize(link));
                proof.Check(runner.IsRunning && ReferenceEquals(runner.Account, run.Account) && runner.OwnPosition == 2
                    && run.Account.Position!.Volume == 2 && runner.Ledger.AveragePrice == 100
                    && runner.Ledger.Fills.Count == 1 && run.Account.Journal.Count == 1
                    && runner.Ledger.Realized == 0 && runner.Ledger.Fees == 0 && !run.Account.ActiveOrders.Any()
                    && services.Journal.CountOf(AccountCenter.SimId) == normalFillsBefore,
                    "delivered " + name + " price is a known open-position mark, not an empty-price fallback, forced exit or normal-account fill");
                proof.StrategyLedgers.Add(new { stage = "Actual known " + name + " open link", run.RunId,
                    expectedEntryTime = entryTime, expectedEntry = 100m, expectedQuantity = 2m, expectedPointValue = 10m, expectedMark = mark, expectedPnl,
                    actualLink = link, deliveredTime = run.Data.Trades.LastTime, deliveredPrice = run.Data.LastPrice,
                    deliveredTrades = run.Data.Trades.Count, runner.OwnPosition, runner.Ledger.Realized,
                    runner.Ledger.CostsComplete, fills = runner.Ledger.Fills.ToArray() });
                proof.Capture(proof.Owner, "kvp_replay_open_link_" + name);
            }

            CheckRenderedMark(Start.AddSeconds(2), -5, -2100, "negative");
            CheckRenderedMark(Start.AddSeconds(3), 0, -2000, "zero");
            Click(Named<Button>(trading, "ReplayEnd"));
            proof.Replay.Add(run.Result());
        }
        finally
        {
            if (tab != null)
            {
                foreach (var strategy in tab.Strategies.ToArray()) trading.RemoveStrategy(tab, strategy);
                trading.CloseChart(tab);
            }
            if (original != null) trading.Activate(original);
            services.Settings.ReplayStartBalance = oldBalance;
            services.Settings.ReplayFeePerUnit = oldFee;
            services.Settings.ReplayNotionalFeeRate = oldRate;
            services.Settings.Save();
            File.Delete(scriptPath);
            services.Strategies.Refresh();
        }
    }

    private static void ExerciseNaturalCompletion(Proof proof)
    {
        var services = App.Services;
        var trading = proof.Owner.Trading;
        var original = trading.ActiveTab;
        decimal oldBalance = services.Settings.ReplayStartBalance, oldFee = services.Settings.ReplayFeePerUnit, oldRate = services.Settings.ReplayNotionalFeeRate;
        string id = "kvp-replay-drain-ui-" + Guid.NewGuid().ToString("N");
        var source = new Instrument { FeedId = id, Symbol = "KVP-FINAL-BATCH", TickSize = 1, VolumeStep = 1, PointValue = 10, QuoteAsset = "USD" };
        using (var writer = new TickFile.Writer(TickFile.DayPath(AppPaths.Ticks, id, source.Symbol, DateOnly.FromDateTime(Start)), source))
        {
            writer.Write(new Trade(1, Start.AddMinutes(-2), 97, 10, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddMinutes(-1), 99, 10, TradeSide.Buy));
            writer.Write(new Trade(21, Start, 100, 10, TradeSide.Buy));
            writer.Write(new Trade(22, Start.AddSeconds(1), 110, 10, TradeSide.Buy));
            writer.Write(new Trade(23, Start.AddSeconds(2), 120, 10, TradeSide.Sell));
        }
        string scriptPath = Path.Combine(AppPaths.Strategies, "KvpUiFinalBatch" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(scriptPath, StrategySource);
        ChartTab? tab = null;
        Window? resultWindow = null;
        try
        {
            services.Settings.ReplayStartBalance = 1000;
            services.Settings.ReplayFeePerUnit = 1;
            services.Settings.ReplayNotionalFeeRate = 0.001m;
            proof.Owner.Navigate("trading");
            proof.Await(trading.OpenPracticeAsync(ReplaySelection.Selected(source, Start, Start.AddSeconds(3), Seed), ChartPeriod.M1),
                "actual short practice opens for final-batch completion acceptance");
            tab = trading.ActiveTab ?? throw new InvalidOperationException("Final-batch practice did not select a chart.");
            var chart = tab;
            var run = services.ReplaySessions.TryGet(chart.Model!.Data)!;
            proof.Until(() => !run.Data.IsLoadingHistory, "final-batch practice completes its warm-up before the producer is released");
            services.Strategies.Refresh();
            var file = services.Strategies.Files.Single(item => item.Path == scriptPath);
            proof.Await(trading.AddStrategyAsync(chart, file, new Dictionary<string, object?> { ["EventPrefix"] = "kvp-ui-drain-" }, openSettings: false),
                "real compiled strategy attaches before the final recorded batch");
            var strategy = chart.Strategies.Single();
            strategy.Live = true;
            proof.Await(trading.RunStrategyAsync(chart, strategy), "normal owned strategy execution starts before final-batch production");
            var runner = strategy.Runner!;
            var feed = run.Feed!;
            feed.SeekForward(run.Selection.EndUtc);
            // Deliberately hold the UI consumer while its real producer finishes. This creates the actual queue race,
            // rather than pumping the final batch before asking the view's natural-completion consumer to run.
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!feed.IsFinished && DateTime.UtcNow < deadline) Thread.Sleep(5);
            proof.Check(feed.IsFinished && run.Account.Journal.Count == 0,
                "real replay producer is finished while its final strategy-bearing batch is still queued for the UI consumer");
            trading.Tick();
            proof.Until(() => run.Ended && run.Account.Journal.Count == 2, "actual view natural-finish path drains the queued final batch before stopping the run");
            proof.Check(run.ClockUtc == Start.AddSeconds(2) && run.Account.Journal[0].Price == 110 && run.Account.Journal[1].Price == 120
                && run.Account.Journal[0].Time == Start.AddSeconds(1) && run.Account.Journal[1].Time == Start.AddSeconds(2)
                && run.Result().RealizedNet == 95.7m && run.Result().Commission == 4.3m
                && runner.Ledger.Fills.Count == 2 && runner.Ledger.Realized == 95.7m
                && run.Data.Trades.GetId(run.Data.Trades.Count - 1) == 23,
                "natural completion retains both actual causal executions, fees, strategy ledger and last source print without an early flatten or dropped batch");
            proof.Await(services.Alerts.FlushAsync(), "naturally completed compiled strategy alerts reach the actual app-owned local consumer");
            proof.Check(services.Alerts.Snapshot().Recent.Count(alert => alert.Id.StartsWith("kvp-ui-drain-tick-", StringComparison.Ordinal)) == 3,
                "the final queued batch delivers every actual Strategy.Alert tick once in this finite run, not historical warm-up alerts");
            proof.StrategyLedgers.Add(new { stage = "Natural completion after queued final batch", run.RunId, runner.Ledger.Realized, fills = runner.Ledger.Fills.ToArray() });
            proof.Replay.Add(run.Result());
            Click(Named<Button>(trading, "ReplayResult"));
            resultWindow = proof.Owner.OwnedWindows.Single(window => window.GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "ReplayJournal"));
            proof.Check(Named<TextBox>(resultWindow, "ReplayJournal").Text!.Contains("2026-09-07 12:00:02", StringComparison.Ordinal),
                "actual completed result journal renders the drained final execution");
            proof.Capture(resultWindow, "kvp_replay_natural_final_batch_journal");
        }
        finally
        {
            resultWindow?.Close();
            if (tab != null)
            {
                foreach (var strategy in tab.Strategies.ToArray()) trading.RemoveStrategy(tab, strategy);
                trading.CloseChart(tab);
            }
            if (original != null) trading.Activate(original);
            services.Settings.ReplayStartBalance = oldBalance;
            services.Settings.ReplayFeePerUnit = oldFee;
            services.Settings.ReplayNotionalFeeRate = oldRate;
            services.Settings.Save();
            File.Delete(scriptPath);
            services.Strategies.Refresh();
        }
    }


    private static void ExerciseAvailability(Proof proof)
    {
        var services = App.Services;
        var trading = proof.Owner.Trading;
        var original = trading.ActiveTab;
        var tabs = new List<ChartTab>();
        var windows = new List<Window>();
        decimal oldBalance = services.Settings.ReplayStartBalance;
        decimal oldFee = services.Settings.ReplayFeePerUnit;
        decimal oldRate = services.Settings.ReplayNotionalFeeRate;
        string id = "kvp-replay-availability-ui-" + Guid.NewGuid().ToString("N");
        var source = new Instrument { FeedId = id, Symbol = "KVP-AVAILABILITY", TickSize = 1, VolumeStep = 1, PointValue = 10, QuoteAsset = "USD" };
        using (var writer = new TickFile.Writer(TickFile.DayPath(AppPaths.Ticks, id, source.Symbol, DateOnly.FromDateTime(Start)),
            source, includeProvenance: true))
        {
            writer.Write(new Trade(-1, Start, 400, 20, TradeSide.None, new TradeProvenance(DataQuality.Compact, 60, id + "-compact")));
            writer.Write(new Trade(1, Start.AddSeconds(61), 100, 10, TradeSide.Buy));
            writer.Write(new Trade(2, Start.AddSeconds(62), 110, 10, TradeSide.Buy));
            writer.Write(new Trade(3, Start.AddSeconds(88), 110, 10, TradeSide.Buy));
        }
        try
        {
            services.Settings.ReplayStartBalance = 1000;
            services.Settings.ReplayFeePerUnit = 0;
            services.Settings.ReplayNotionalFeeRate = 0;
            proof.Owner.Navigate("trading");
            proof.Await(trading.OpenPracticeAsync(ReplaySelection.Selected(source, Start.AddSeconds(30), Start.AddSeconds(90), Seed), ChartPeriod.M1),
                "actual chart practice opens an interval straddled by a recorded compact bucket");
            var tab = trading.ActiveTab ?? throw new InvalidOperationException("Availability practice did not select a chart.");
            tabs.Add(tab);
            var model = tab.Model ?? throw new InvalidOperationException("Availability practice has no chart model.");
            var run = services.ReplaySessions.TryGet(model.Data) ?? throw new InvalidOperationException("Availability chart has no owned run.");
            var replay = run.Feed ?? throw new InvalidOperationException("Availability run has no feed.");
            proof.Until(() => !run.Data.IsLoadingHistory, "actual straddling-bucket warm-up finishes without future source values");
            var waiting = run.Account.Limit(OrderDirections.Buy, 1, 500);
            replay.SeekForward(Start.AddSeconds(40));
            proof.Until(() => replay.Clock >= Start.AddSeconds(40), "normal replay seek reaches a time before the compact source closes");
            proof.Pump(0.1);
            proof.Check(run.Data.Trades.Count == 0 && model.Series.Count == 0 && !run.Account.HasPrice
                && run.ClockUtc == Start.AddSeconds(30) && waiting.Filled == 0 && run.Account.Journal.Count == 0,
                "a bucket located before the selected start is neither preloaded nor executed before its later availability");
            proof.Capture(proof.Owner, "kvp_replay_before_bucket_available");
            replay.SeekForward(Start.AddSeconds(60));
            proof.Until(() => run.ClockUtc == Start.AddSeconds(60) && model.Series.Count > 0 && model.LastLayout != null,
                "actual chart publishes the straddling bucket only at its source-close clock");
            proof.Check(run.Data.Trades.GetTime(0) == Start && run.Account.LastPrice == 400 && !run.Account.HasObservedPrice
                && waiting.State == OrderStates.Active && waiting.Filled == 0 && run.Account.Journal.Count == 0
                && run.DataWarning?.Contains("Aggregated", StringComparison.Ordinal) == true
                && run.Account.Market(OrderDirections.Buy, 1).Status == OrderStatus.Rejected,
                "available compact value retains its old chart location and an explicit warning, but cannot fill a resting or market order");
            proof.Capture(proof.Owner, "kvp_replay_bucket_available_not_executable");
            run.Account.Cancel(waiting);
            replay.SeekForward(Start.AddSeconds(61));
            proof.Until(() => run.ClockUtc == Start.AddSeconds(61) && run.Account.HasObservedPrice,
                "a later observed raw print restores the actual practice execution price");
            run.Account.Market(OrderDirections.Buy, 1);
            replay.SeekForward(Start.AddSeconds(62));
            proof.Until(() => run.ClockUtc == Start.AddSeconds(62), "raw closing print advances the owned practice clock");
            run.Account.Flatten();
            proof.Check(run.Result().RealizedNet == 100m && run.Account.Journal.Count == 2
                && run.Account.Journal[0].Time == Start.AddSeconds(61) && run.Account.Journal[0].Price == 100
                && run.Account.Journal[1].Time == Start.AddSeconds(62) && run.Account.Journal[1].Price == 110
                && run.Result().HasKnownAvailability && (run.Result().DataQuality & DataQuality.Compact) != 0,
                "only the two available raw prints execute: (110 - 100) × 1 × 10 gives 100 with explicit zero fixture fees");
            Click(Named<Button>(trading, "ReplayEnd"));
            proof.Replay.Add(run.Result());
            proof.Capture(proof.Owner, "kvp_replay_available_raw_fills");

            var unknownSource = new Instrument { FeedId = id, Symbol = "KVP-UNKNOWN-AVAILABILITY", TickSize = 1, VolumeStep = 1, PointValue = 10 };
            using (var writer = new TickFile.Writer(TickFile.DayPath(AppPaths.Ticks, id, unknownSource.Symbol, DateOnly.FromDateTime(Start)),
                unknownSource, includeProvenance: true))
                writer.Write(new Trade(1, Start, 1000, 10, TradeSide.Buy,
                    new TradeProvenance(DataQuality.Raw, SourceIdentity: id + "-unknown", HasKnownAvailability: false)));
            proof.Await(trading.OpenPracticeAsync(ReplaySelection.Selected(unknownSource, Start, Start.AddMinutes(1), Seed), ChartPeriod.M1),
                "actual practice workflow opens a recorded source with explicitly unknown availability");
            var unknownTab = trading.ActiveTab ?? throw new InvalidOperationException("Unknown-availability practice did not select a chart.");
            tabs.Add(unknownTab);
            var unknown = services.ReplaySessions.TryGet(unknownTab.Model!.Data)!;
            proof.Until(() => !unknown.Data.IsLoadingHistory && unknown.Data.Status == FeedStatus.Error,
                "real replay reader exposes unknown availability as an actual feed error");
            proof.Check(!unknown.Account.HasPrice && !unknown.Result().HasKnownAvailability && unknown.Data.Trades.Count == 0
                && unknown.Account.Market(OrderDirections.Buy, 1).Status == OrderStatus.Rejected && unknown.Account.Journal.Count == 0,
                "unknown-availability raw flags cannot release a price, mark equity or execute practice orders");
            Click(Named<Button>(trading, "ReplayResult"));
            var result = proof.Owner.OwnedWindows.Single(window => window.GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "ReplayJournal"));
            windows.Add(result);
            proof.Check(result.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("availability", StringComparison.OrdinalIgnoreCase) == true),
                "actual run result renders the source-availability failure instead of a fabricated complete-data result");
            proof.Capture(result, "kvp_replay_unknown_availability_warning");
            result.Close();
            Click(Named<Button>(trading, "ReplayEnd"));
            proof.Replay.Add(unknown.Result());
        }
        finally
        {
            foreach (var window in windows.Where(window => window.IsVisible)) window.Close();
            foreach (var tab in tabs.AsEnumerable().Reverse()) trading.CloseChart(tab);
            if (original != null) trading.Activate(original);
            services.Settings.ReplayStartBalance = oldBalance;
            services.Settings.ReplayFeePerUnit = oldFee;
            services.Settings.ReplayNotionalFeeRate = oldRate;
            services.Settings.Save();
        }
    }

    private static void ExerciseNotifications(Proof proof)
    {
        string root = Path.Combine(proof.OutDir, "kvp-notification-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string statePath = Path.Combine(root, "notifications.json");
        string privatePath = Path.Combine(root, "private-fake-secret-store.json");
        var clock = new FixtureClock(Start);
        var secrets = new FixtureSecrets(privatePath);
        using var endpoint = new NotificationEndpoint();
        using var http = new HttpClient(endpoint, disposeHandler: false);
        AlertCenter? center = null;
        AlertsWindow? window = null;
        try
        {
            center = new AlertCenter(statePath, secrets, http, () => clock.Now);
            window = new AlertsWindow(center);
            window.Show(proof.Owner);
            proof.Pump(0.1);
            proof.Check(Named<CheckBox>(window, "AlertExternalConsent").IsChecked == false && center.Snapshot().Destinations.Count == 0,
                "actual Alerts window starts without external consent or an enabled destination");
            Click(Named<Button>(window, "AlertSendTest"));
            proof.Await(center.FlushAsync(), "explicit UI test alert is durable locally without permission");
            proof.Check(endpoint.Requests.IsEmpty && center.Snapshot().Recent.Any(alert => alert.Source == "Notification test"),
                "actual Queue test action stays local before a destination is approved");
            SelectKinds(window, AlertKind.Price);
            Enter(Named<TextBox>(window, "AlertWebhookEndpoint"), WebhookEndpoint);
            Enter(Named<TextBox>(window, "AlertSecret"), WebhookSecret);
            secrets.DropWrites = true;
            Named<CheckBox>(window, "AlertExternalConsent").IsChecked = true;
            Save(window, proof);
            proof.Check(center.Snapshot().Destinations.Count == 0 && endpoint.Requests.IsEmpty
                && Named<TextBlock>(window, "AlertDeliveryStatus").Text!.Contains("did not confirm", StringComparison.Ordinal),
                "actual Save refuses silent secret-store write loss without publishing external permission");
            proof.Check(Named<TextBox>(window, "AlertWebhookEndpoint").Text == "" && Named<TextBox>(window, "AlertSecret").Text == ""
                && Named<CheckBox>(window, "AlertExternalConsent").IsChecked == false,
                "even a failed enabled Save clears private target, authorization and explicit-consent controls");
            secrets.DropWrites = false;
            Enter(Named<TextBox>(window, "AlertWebhookEndpoint"), WebhookEndpoint);
            Enter(Named<TextBox>(window, "AlertSecret"), WebhookSecret);
            Named<CheckBox>(window, "AlertExternalConsent").IsChecked = false;
            Save(window, proof);
            var disabled = center.Snapshot().Destinations.Single();
            proof.Check(!disabled.Enabled && Named<TextBox>(window, "AlertSecret").Text == ""
                && Named<CheckBox>(window, "AlertExternalConsent").IsChecked == false,
                "saving without approval stores a disabled destination and clears secret/consent controls");
            proof.Check(disabled.Endpoint == WebhookDisplay && !ContainsSecret(JsonSerializer.Serialize(center.Snapshot()))
                && Named<TextBox>(window, "AlertWebhookEndpoint").Text == "",
                "ordinary destination state exposes only a generic private-webhook label, never its capability hostname/path/query or an automatically read-back target");
            center.Raise(AlertKind.Price, "UI-TEST", "Price scene", "Review this local payload; " + WebhookSecret + " · " + WebhookEndpoint, "kvp-ui-preview");
            proof.Await(center.FlushAsync(), "disabled destination retains its redacted local preview event");
            Click(Named<Button>(window, "AlertPreviewPayload"));
            string preview = Named<TextBox>(window, "AlertPayloadPreview").Text!;
            proof.Check(endpoint.Requests.IsEmpty && preview.Contains("[redacted]", StringComparison.Ordinal) && !ContainsSecret(preview),
                "actual Preview displays redacted payload/authorization and sends nothing");
            Click(Named<Button>(window, "AlertReviewPrivateTarget"));
            proof.Await(window.PendingOperation!, "explicit private-target Review action completes in the actual window");
            proof.Check(Named<TextBox>(window, "AlertWebhookEndpoint").Text == WebhookEndpoint
                && Named<TextBox>(window, "AlertSecret").Text == "" && endpoint.Requests.IsEmpty
                && !ContainsSecret(File.ReadAllText(statePath)) && !ContainsSecret(JsonSerializer.Serialize(center.Snapshot())),
                "only explicit Review reveals the full host/path/query in window memory, without reading back authorization or changing ordinary state");
            Click(Named<Button>(window, "AlertPreviewPayload"));
            proof.Check(!ContainsSecret(Named<TextBox>(window, "AlertPayloadPreview").Text!)
                && Named<TextBox>(window, "AlertPayloadPreview").Text!.StartsWith(WebhookDisplay + "\n", StringComparison.Ordinal),
                "default payload preview stays generic even while the user explicitly reviews the private capability hostname/path/query");
            Click(Named<Button>(window, "AlertHidePrivateTarget"));
            proof.Check(Named<TextBox>(window, "AlertWebhookEndpoint").Text == "" && endpoint.Requests.IsEmpty,
                "actual Hide action clears the reviewed target without an external send");
            proof.Capture(window, "kvp_alert_consent_preview");
            Named<TextBox>(window, "AlertPayloadPreview").BringIntoView();
            proof.Capture(window, "kvp_alert_reviewed_payload");
            Named<CheckBox>(window, "AlertExternalConsent").IsChecked = true;
            Save(window, proof);
            var webhook = center.Snapshot().Destinations.Single();
            proof.Check(webhook.Enabled && webhook.Revision != disabled.Revision
                && secrets.Get(AlertCenter.SecretAccount(disabled.Id, disabled.Revision)) == "",
                "explicit fresh approval publishes a new secret-backed permission revision and retires the disabled credential");
            proof.Check(Named<TextBox>(window, "AlertSecret").Text == "" && Named<CheckBox>(window, "AlertExternalConsent").IsChecked == false,
                "saved permission never reads the credential back into actual UI controls");

            endpoint.WebhookMode = ResponseMode.Unavailable;
            center.Raise(AlertKind.Price, "UI-TEST", "Approved price scene", "Approved payload; " + WebhookSecret,
                "kvp-ui-stable-event", clock.Now);
            proof.Await(center.FlushAsync(), "approved price alert reaches durable pending storage before delivery");
            proof.Await(center.DeliverPendingAsync(), "unavailable fake webhook completes its first actual HTTP attempt");
            var pending = center.Snapshot().Deliveries.Single();
            proof.Check(pending.State == NotificationDeliveryState.Pending && pending.Attempts == 1 && pending.Alert.Id == "kvp-ui-stable-event",
                "HTTP 503 leaves a visible durable pending message with one bounded attempt");
            var firstRequest = endpoint.Requests.Single();
            proof.Check(firstRequest.WebhookTargetMatched && firstRequest.AuthorizationMatched && firstRequest.Method == "POST" && firstRequest.MediaType == "application/json"
                && firstRequest.DeliveryId == pending.Id && firstRequest.EventId == pending.Alert.Id && !ContainsSecret(firstRequest.Payload),
                "real webhook request builder sends protected authorization only to fake HTTP and stable event/delivery IDs in headers");
            CheckWebhookPayload(proof, firstRequest.Payload, pending.Alert);
            Click(Named<Button>(window, "AlertPreviewPayload"));
            proof.Check(Named<TextBox>(window, "AlertPayloadPreview").Text!.Contains(firstRequest.Payload, StringComparison.Ordinal),
                "actual approved payload preview exactly matches redacted webhook JSON");
            proof.CaptureDelivery(window, pending.Id, "kvp_alert_pending");
            proof.Check(DeliveryText(window, pending.Id).Contains("Pending", StringComparison.Ordinal)
                && DeliveryText(window, pending.Id).Contains("attempt 1/5", StringComparison.Ordinal),
                "actual delivery row renders pending state and finite attempt count");
            proof.Notifications.Add(center.Snapshot());

            secrets.DropWrites = true;
            Enter(Named<TextBox>(window, "AlertSecret"), ReplacementSecret);
            Named<CheckBox>(window, "AlertExternalConsent").IsChecked = true;
            Save(window, proof);
            proof.Check(center.Snapshot().Destinations.Single().Revision == webhook.Revision && center.Snapshot().Destinations.Single().Enabled
                && center.Snapshot().Deliveries.Single().Id == pending.Id
                && center.Snapshot().Deliveries.Single().State == NotificationDeliveryState.Pending && endpoint.Requests.Count == 1,
                "failed enabled replacement preserves the old grant, original pending identity and approved destination without an extra send");
            proof.Check(Named<TextBox>(window, "AlertWebhookEndpoint").Text == "" && Named<TextBox>(window, "AlertSecret").Text == ""
                && Named<CheckBox>(window, "AlertExternalConsent").IsChecked == false,
                "failed enabled replacement does not leave private target or replacement authorization in actual controls");
            secrets.DropWrites = false;
            window.Close();
            window = null;
            proof.Await(center.DisposeAsync().AsTask(), "scene-owned notification service drains and releases its state lease for restart");
            center = null;
            clock.Advance(TimeSpan.FromHours(1));
            endpoint.WebhookMode = ResponseMode.Accepted;
            center = new AlertCenter(statePath, new FixtureSecrets(privatePath), http, () => clock.Now);
            window = new AlertsWindow(center);
            window.Show(proof.Owner);
            proof.Await(center.DeliverPendingAsync(), "restarted real notification consumer recovers the due durable outbox");
            proof.Until(() => center.Snapshot().Deliveries.Single().State == NotificationDeliveryState.Delivered,
                "recovered fake endpoint acknowledgment publishes delivered status");
            var sent = center.Snapshot().Deliveries.Single();
            proof.Check(sent.Id == pending.Id && sent.Attempts == 2 && endpoint.Requests.Count == 2
                && endpoint.Requests.All(request => request.WebhookTargetMatched && request.EventId == "kvp-ui-stable-event" && request.DeliveryId == pending.Id),
                "restart recovery preserves event and delivery IDs rather than creating a new notification");
            proof.Check(sent.Status.Contains("Human receipt is not confirmed", StringComparison.Ordinal),
                "successful endpoint acknowledgment does not claim human receipt");
            proof.CaptureDelivery(window, sent.Id, "kvp_alert_restart_delivered");
            int beforeDuplicate = endpoint.Requests.Count;
            center.Raise(AlertKind.Price, "UI-TEST", "Duplicate scene", "Same recently retained event ID", "kvp-ui-stable-event");
            proof.Await(center.FlushAsync(), "repeated retained event ID passes through normal deduplication");
            proof.Await(center.DeliverPendingAsync(), "deduplicated event creates no additional delivery");
            proof.Check(endpoint.Requests.Count == beforeDuplicate && center.Snapshot().Recent.Count(alert => alert.Id == "kvp-ui-stable-event") == 1,
                "finite retained-ID deduplication survives restart, without asserting universal exactly-once delivery");

            endpoint.WebhookMode = ResponseMode.Unavailable;
            center.Raise(AlertKind.Price, "UI-TEST", "Bounded retry scene", "Unavailable external endpoint", "kvp-ui-bounded");
            proof.Await(center.FlushAsync(), "retry example is durably queued");
            proof.Await(center.DeliverPendingAsync(), "first bounded retry attempt completes");
            for (int attempt = 2; attempt <= 5; attempt++)
            {
                clock.Advance(TimeSpan.FromHours(1));
                proof.Await(center.DeliverPendingAsync(), "fake clock makes bounded retry attempt " + attempt + " due");
            }
            var exhausted = center.Snapshot().Deliveries.Single(delivery => delivery.Alert.Id == "kvp-ui-bounded");
            proof.Check(exhausted.State == NotificationDeliveryState.Failed && exhausted.Attempts == 5
                && endpoint.Requests.Count(request => request.EventId == "kvp-ui-bounded") == 5,
                "five failed HTTP attempts terminate in a visible error instead of retrying indefinitely");
            clock.Advance(TimeSpan.FromDays(1));
            proof.Await(center.DeliverPendingAsync(), "exhausted notification remains bounded after additional time");
            proof.Check(endpoint.Requests.Count(request => request.EventId == "kvp-ui-bounded") == 5,
                "exhausted automatic retries do not resume by themselves");
            proof.CaptureDelivery(window, exhausted.Id, "kvp_alert_retry_exhausted");
            proof.Check(DeliveryText(window, exhausted.Id).Contains("Failed", StringComparison.Ordinal)
                && DeliveryText(window, exhausted.Id).Contains("attempt 5/5", StringComparison.Ordinal),
                "actual error row exposes failed state and exhausted retry count");
            proof.Notifications.Add(center.Snapshot());
            endpoint.WebhookMode = ResponseMode.Accepted;
            Click(DeliveryButton(window, exhausted.Id, "Retry"));
            proof.Await(center.DeliverPendingAsync(), "actual Retry control submits the original approved message");
            var retried = center.Snapshot().Deliveries.Single(delivery => delivery.Id == exhausted.Id);
            proof.Check(retried.State == NotificationDeliveryState.Delivered && retried.Attempts == 1
                && endpoint.Requests.Count(request => request.EventId == "kvp-ui-bounded") == 6
                && endpoint.Requests.Where(request => request.EventId == "kvp-ui-bounded").All(request => request.DeliveryId == exhausted.Id),
                "explicit UI Retry resets its finite attempt cycle but preserves the original delivery identity");

            endpoint.WebhookMode = ResponseMode.Held;
            center.Raise(AlertKind.Price, "UI-TEST", "Held external scene", "In-flight acceptance is not known", "kvp-ui-revoke-held");
            proof.Until(() => endpoint.Requests.Any(request => request.EventId == "kvp-ui-revoke-held")
                && center.Snapshot().Deliveries.Any(delivery => delivery.Alert.Id == "kvp-ui-revoke-held" && delivery.State == NotificationDeliveryState.Delivering),
                "actual HTTP request is held at the fake endpoint with visible unconfirmed state");
            foreach (var kind in new[] { AlertKind.Price, AlertKind.Indicator, AlertKind.Strategy, AlertKind.Risk, AlertKind.Execution })
                center.Raise(kind, "UI-TEST", "Local while external is held", "Local notification survives external delay: " + kind,
                    "kvp-ui-held-local-" + kind, allowExternal: false);
            proof.Await(center.FlushAsync(), "a held HTTP delivery does not block normal local intake or persistence");
            proof.Until(() => Named<StackPanel>(window, "AlertRecentHistory").Children.OfType<TextBlock>().Count(block => block.Text!.Contains("kvp-ui-held-local-", StringComparison.Ordinal)) == 5,
                "actual local history renders price, indicator, strategy, risk and execution alerts while external delivery is held");
            int cancellationsBeforeRevoke = endpoint.Cancellations;
            SelectDestination(window, webhook.Id);
            proof.Check(Named<TextBox>(window, "AlertSecret").Text == "" && Named<CheckBox>(window, "AlertExternalConsent").IsChecked == false,
                "selecting a saved destination neither discloses its secret nor silently rechecks consent");
            Click(Named<Button>(window, "AlertRevokeDestination"));
            proof.Await(window.PendingOperation!, "actual Revoke control cancels pending and in-flight work");
            proof.Until(() => endpoint.Cancellations > cancellationsBeforeRevoke, "held fake HTTP observes cancellation after revocation");
            proof.Check(center.Snapshot().Deliveries.Single(delivery => delivery.Alert.Id == "kvp-ui-revoke-held").State == NotificationDeliveryState.Cancelled
                && !center.Snapshot().Destinations.Single().Enabled && secrets.Get(AlertCenter.SecretAccount(webhook.Id, webhook.Revision)) == "",
                "revocation disables the actual destination and removes its private grant, without claiming remote recall");
            int afterRevoke = endpoint.Requests.Count;
            center.Raise(AlertKind.Price, "UI-TEST", "Revoked scene", "Must stay local after revocation", "kvp-ui-after-revoke");
            proof.Await(center.FlushAsync(), "post-revocation price alert stays visible locally");
            proof.Await(center.DeliverPendingAsync(), "revoked destination cannot send another queued event");
            proof.Check(endpoint.Requests.Count == afterRevoke && center.Snapshot().Recent.Any(alert => alert.Id == "kvp-ui-after-revoke"),
                "revocation prevents further sends without suppressing the local alert");
            proof.CaptureDelivery(window, pending.Id, "kvp_alert_webhook_revoked");
            Click(Named<Button>(window, "AlertReviewPrivateTarget"));
            proof.Await(window.PendingOperation!, "actual Review action refuses a revoked private target");
            proof.Check(Named<TextBox>(window, "AlertWebhookEndpoint").Text == "" && endpoint.Requests.Count == afterRevoke,
                "revoked private target cannot be read back by the actual review control");
            Named<CheckBox>(window, "AlertExternalConsent").IsChecked = true;
            Save(window, proof);
            proof.Check(!center.Snapshot().Destinations.Single().Enabled && endpoint.Requests.Count == afterRevoke
                && Named<TextBox>(window, "AlertWebhookEndpoint").Text == "" && Named<TextBox>(window, "AlertSecret").Text == "",
                "after revocation, blank re-enable cannot fall back to the public origin or reuse deleted private capability credentials");

            Click(Named<Button>(window, "AlertNewDestination"));
            Named<ComboBox>(window, "AlertDestinationKind").SelectedItem = NotificationDestinationKind.Telegram;
            Enter(Named<TextBox>(window, "AlertTelegramChat"), "-9988");
            Enter(Named<TextBox>(window, "AlertSecret"), TelegramSecret);
            SelectKinds(window, AlertKind.System);
            Save(window, proof);
            int beforeTelegramConsent = endpoint.Requests.Count;
            Click(Named<Button>(window, "AlertSendTest"));
            proof.Await(center.FlushAsync(), "actual Telegram test remains local while destination is disabled");
            Click(Named<Button>(window, "AlertPreviewPayload"));
            preview = Named<TextBox>(window, "AlertPayloadPreview").Text!;
            proof.Check(endpoint.Requests.Count == beforeTelegramConsent && preview.Contains("chat_id", StringComparison.Ordinal)
                && preview.Contains("-9988", StringComparison.Ordinal) && !ContainsSecret(preview),
                "Telegram UI previews its exact chat payload without a token or pre-consent send");
            proof.Capture(window, "kvp_alert_telegram_preview");
            Named<CheckBox>(window, "AlertExternalConsent").IsChecked = true;
            Save(window, proof);
            var telegram = center.Snapshot().Destinations.Single(destination => destination.Kind == NotificationDestinationKind.Telegram);
            endpoint.TelegramMode = ResponseMode.RejectedAcknowledgment;
            Click(Named<Button>(window, "AlertSendTest"));
            proof.Await(center.FlushAsync(), "explicit approved Telegram test enters the normal durable outbox");
            proof.Await(center.DeliverPendingAsync(), "fake Telegram returns HTTP 200 with a rejected application acknowledgment");
            var rejected = center.Snapshot().Deliveries.Single(delivery => delivery.DestinationId == telegram.Id);
            proof.Check(rejected.State == NotificationDeliveryState.Failed && rejected.Attempts == 1 && rejected.Status.Contains("403", StringComparison.Ordinal),
                "Telegram HTTP 200 with ok:false is an error, not a falsely successful notification");
            var telegramRequest = endpoint.Requests.Last();
            using (var payload = JsonDocument.Parse(telegramRequest.Payload))
                proof.Check(telegramRequest.TelegramTokenMatched && !telegramRequest.HasAuthorization
                    && telegramRequest.Method == "POST" && telegramRequest.MediaType == "application/json"
                    && payload.RootElement.GetProperty("chat_id").GetString() == "-9988"
                    && payload.RootElement.GetProperty("text").GetString()!.Contains("Alert ID: " + rejected.Alert.Id, StringComparison.Ordinal)
                    && !ContainsSecret(telegramRequest.Payload), "real Telegram request builder isolates the protected token and sends the approved chat, text and stable event ID");
            Click(Named<Button>(window, "AlertPreviewPayload"));
            proof.Check(Named<TextBox>(window, "AlertPayloadPreview").Text!.Contains(telegramRequest.Payload, StringComparison.Ordinal),
                "actual Telegram preview equals the exact HTTP chat payload");
            proof.CaptureDelivery(window, rejected.Id, "kvp_alert_telegram_rejected");
            endpoint.TelegramMode = ResponseMode.Accepted;
            Click(DeliveryButton(window, rejected.Id, "Retry"));
            proof.Await(center.DeliverPendingAsync(), "actual Telegram Retry consumes an ok:true fake acknowledgment");
            var accepted = center.Snapshot().Deliveries.Single(delivery => delivery.Id == rejected.Id);
            proof.Check(accepted.State == NotificationDeliveryState.Delivered && accepted.Status.Contains("Human receipt is not confirmed", StringComparison.Ordinal)
                && endpoint.Requests.Count(request => request.EventId == rejected.Alert.Id) == 2
                && endpoint.Requests.Where(request => request.EventId == rejected.Alert.Id).All(request => request.DeliveryId == rejected.Id),
                "Telegram becomes delivered only after its own acknowledgment, with stable retry identity and no receipt guarantee");
            proof.CaptureDelivery(window, accepted.Id, "kvp_alert_telegram_delivered");
            Click(Named<Button>(window, "AlertRevokeDestination"));
            proof.Await(window.PendingOperation!, "actual Telegram revoke removes external permission");
            int beforeFinalLocal = endpoint.Requests.Count;
            Click(Named<Button>(window, "AlertSendTest"));
            proof.Await(center.FlushAsync(), "test after revoking both destinations remains local");
            proof.Await(center.DeliverPendingAsync(), "neither revoked destination receives another test");
            proof.Check(endpoint.Requests.Count == beforeFinalLocal
                && center.Snapshot().Destinations.All(destination => !destination.Enabled)
                && secrets.Get(AlertCenter.SecretAccount(telegram.Id, telegram.Revision)) == "",
                "both destination kinds prevent new sends after revoke and clear their protected credentials");
            proof.Check(!ContainsSecret(File.ReadAllText(statePath)) && !ContainsSecret(JsonSerializer.Serialize(center.Snapshot()))
                && !ContainsSecret(JsonSerializer.Serialize(App.Services.Settings))
                && !File.ReadAllText(statePath).Contains("private-response-detail", StringComparison.Ordinal),
                "ordinary notification state, snapshots and app settings contain neither secrets nor private endpoint response details");
            string settingsPath = Path.Combine(AppPaths.Root, "settings.json");
            proof.Check(File.Exists(settingsPath) && !ContainsSecret(File.ReadAllText(settingsPath))
                && !Directory.EnumerateFiles(AppPaths.Root, "*.log", SearchOption.AllDirectories).Any(path => ContainsSecret(File.ReadAllText(path)))
                && !Directory.EnumerateFiles(AppPaths.Root, "*.jsonl", SearchOption.AllDirectories).Any(path => ContainsSecret(File.ReadAllText(path))),
                "actual saved settings, fixture logs and ordinary account event files never contain notification authorization or bot tokens");
            proof.Notifications.Add(center.Snapshot());
            window.Close();
            window = null;
            proof.Await(center.DisposeAsync().AsTask(), "scene-owned alert center releases the recovered state after revocation");
            center = null;
            clock.Advance(TimeSpan.FromDays(1));
            center = new AlertCenter(statePath, new FixtureSecrets(privatePath), http, () => clock.Now);
            proof.Await(center.DeliverPendingAsync(), "revocation remains authoritative after a second service restart");
            proof.Check(endpoint.Requests.Count == beforeFinalLocal && center.Snapshot().Destinations.All(destination => !destination.Enabled),
                "restart never resurrects revoked webhook or Telegram consent");
            proof.Notifications.Add(center.Snapshot());
        }
        finally
        {
            window?.Close();
            proof.Requests.AddRange(endpoint.Requests);
            if (center != null) proof.Notifications.Add(center.Snapshot());
            if (center != null) proof.Await(center.DisposeAsync().AsTask(), "scene-owned notification worker drains without shutting down App.Services");
        }
    }

    private static void ExerciseUncheckedSaveRevocation(Proof proof, UncheckedSaveFault fault)
    {
        string root = Path.Combine(proof.OutDir, "kvp-unchecked-save-" + fault + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string statePath = Path.Combine(root, "notifications.json");
        string privatePath = Path.Combine(root, "private-fake-secret-store.json");
        var secrets = new FixtureSecrets(privatePath);
        var clock = new FixtureClock(Start);
        using var endpoint = new NotificationEndpoint { WebhookMode = ResponseMode.Held };
        using var http = new HttpClient(endpoint, disposeHandler: false);
        AlertCenter? center = null;
        AlertsWindow? window = null;
        try
        {
            var current = center = new AlertCenter(statePath, secrets, http, () => clock.Now);
            var ui = window = new AlertsWindow(current);
            ui.Show(proof.Owner);
            proof.Pump(0.05);
            Enter(Named<TextBox>(ui, "AlertWebhookEndpoint"), WebhookEndpoint);
            Enter(Named<TextBox>(ui, "AlertSecret"), WebhookSecret);
            SelectKinds(ui, AlertKind.Price);
            Named<CheckBox>(ui, "AlertExternalConsent").IsChecked = true;
            Save(ui, proof);
            var destination = current.Snapshot().Destinations.Single();
            proof.Check(destination.Enabled && destination.Endpoint == WebhookDisplay && !ContainsSecret(File.ReadAllText(statePath)),
                fault + ": actual enabled Save publishes only a generic label while the entire capability URI remains private");
            string activeId = "kvp-ui-unchecked-" + fault + "-active";
            string queuedId = "kvp-ui-unchecked-" + fault + "-queued";
            current.Raise(AlertKind.Price, "UI-TEST", "Unchecked Save active", "A held external request must be retired", activeId);
            proof.Until(() => endpoint.Requests.Any(request => request.EventId == activeId)
                && current.Snapshot().Deliveries.Any(delivery => delivery.Alert.Id == activeId && delivery.State == NotificationDeliveryState.Delivering),
                fault + ": actual HTTP is held before permission is unchecked");
            current.Raise(AlertKind.Price, "UI-TEST", "Unchecked Save queued", "Pending delivery must also be cancelled", queuedId);
            proof.Await(current.FlushAsync(), fault + ": both active and queued work reach the actual durable outbox before the injected fault");
            SelectDestination(ui, destination.Id);
            proof.Check(Named<TextBox>(ui, "AlertSecret").Text == "" && !ContainsSecret(Named<TextBox>(ui, "AlertWebhookEndpoint").Text ?? "")
                && Named<CheckBox>(ui, "AlertExternalConsent").IsChecked == false,
                fault + ": selecting the saved grant exposes no private target and leaves explicit consent unchecked");
            int cancellationsBeforeSave = endpoint.Cancellations;
            switch (fault)
            {
                case UncheckedSaveFault.SecretRead:
                    secrets.FailReads = true;
                    break;
                case UncheckedSaveFault.SecretWrite:
                    secrets.FailWrites = true;
                    break;
                case UncheckedSaveFault.SecretDelete:
                    secrets.DropDeletes = true;
                    break;
                case UncheckedSaveFault.Disk:
                    File.Delete(statePath);
                    Directory.CreateDirectory(statePath);
                    break;
            }
            Click(Named<Button>(ui, "AlertSaveDestination"));
            proof.Await(ui.PendingOperation ?? throw new InvalidOperationException("Unchecked Save did not start its actual operation."),
                fault + ": unchecked Save returns through the actual UI, including safe storage-error presentation");
            proof.Check(Named<TextBox>(ui, "AlertWebhookEndpoint").Text == "" && Named<TextBox>(ui, "AlertSecret").Text == ""
                && Named<CheckBox>(ui, "AlertExternalConsent").IsChecked == false
                && Named<TextBlock>(ui, "AlertDeliveryStatus").Text?.Contains("not confirmed", StringComparison.OrdinalIgnoreCase) == true,
                fault + ": actual failed withdrawal clears private controls and visibly reports that durable revocation is unconfirmed");
            if (fault == UncheckedSaveFault.SecretDelete)
                proof.Check(secrets.Get(AlertCenter.SecretAccount(destination.Id, destination.Revision)).Length > 0,
                    "silent protected deletion really retains the old credential, but its readback cannot be falsely reported as successful");
            proof.Until(() => endpoint.Cancellations > cancellationsBeforeSave,
                fault + ": unchecked Save cancels the actual active HTTP request even when a private or disk operation is unavailable");
            var revoked = current.Snapshot();
            proof.Check(revoked.Destinations.Count == 1 && !revoked.Destinations[0].Enabled
                && revoked.Deliveries.Count == 2 && revoked.Deliveries.All(delivery => delivery.State == NotificationDeliveryState.Cancelled),
                fault + ": unchecked Save immediately revokes local permission and cancels active/pending deliveries instead of preserving old consent");
            int requestsAfterDisable = endpoint.Requests.Count;
            string localId = "kvp-ui-unchecked-" + fault + "-local";
            current.Raise(AlertKind.Price, "UI-TEST", "After unchecked Save", "Local notification remains visible while external permission is off", localId);
            proof.Until(() => Named<StackPanel>(ui, "AlertRecentHistory").Children.OfType<TextBlock>().Any(block => block.Text?.Contains(localId, StringComparison.Ordinal) == true),
                fault + ": actual local history remains usable after the failed or completed private/disk revocation operation");
            clock.Advance(TimeSpan.FromHours(1));
            proof.Await(current.DeliverPendingAsync(), fault + ": due-delivery consumer observes the local revocation");
            proof.Check(endpoint.Requests.Count == requestsAfterDisable && current.Snapshot().Deliveries.All(delivery => delivery.State == NotificationDeliveryState.Cancelled)
                && !ContainsSecret(JsonSerializer.Serialize(current.Snapshot())),
                fault + ": later alerts and elapsed retry time cannot cause further HTTP under the retired permission");
            var savedRow = Named<ComboBox>(ui, "AlertDestinations").Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, destination.Id));
            string savedCaption = savedRow.Content?.ToString() ?? "";
            proof.Check(savedCaption.Contains("off", StringComparison.Ordinal) && savedCaption.Contains(WebhookDisplay, StringComparison.Ordinal)
                && !ContainsSecret(savedCaption), fault + ": actual saved-destination row visibly shows the private-target grant is off");
            proof.CaptureDelivery(ui, revoked.Deliveries[0].Id, "kvp_alert_unchecked_save_" + fault.ToString().ToLowerInvariant());
            proof.Notifications.Add(current.Snapshot());

            // Durable recovery is proved only after storage is restored and the user explicitly saves the off state again.
            secrets.FailReads = false;
            secrets.FailWrites = false;
            secrets.DropDeletes = false;
            if (Directory.Exists(statePath)) Directory.Delete(statePath);
            Named<CheckBox>(ui, "AlertExternalConsent").IsChecked = false;
            Save(ui, proof);
            proof.Await(current.FlushAsync(), fault + ": explicit off-state Save becomes durable after private/disk recovery");
            proof.Check(!current.Snapshot().Destinations.Single().Enabled && !ContainsSecret(File.ReadAllText(statePath)),
                fault + ": recovered ordinary state retains revocation and no webhook capability or authorization");
            ui.Close();
            window = null;
            proof.Await(current.DisposeAsync().AsTask(), fault + ": scene-owned revoked center drains before restart");
            center = null;
            clock.Advance(TimeSpan.FromDays(1));
            var restarted = center = new AlertCenter(statePath, new FixtureSecrets(privatePath), http, () => clock.Now);
            proof.Await(restarted.DeliverPendingAsync(), fault + ": restart consumes the explicitly recovered durable off state");
            proof.Check(endpoint.Requests.Count == requestsAfterDisable && restarted.Snapshot().Destinations.All(item => !item.Enabled)
                && restarted.Snapshot().Deliveries.All(delivery => delivery.State == NotificationDeliveryState.Cancelled),
                fault + ": restart after recovered revocation cannot resurrect old active or pending sends");
            proof.Notifications.Add(restarted.Snapshot());
        }
        finally
        {
            secrets.FailReads = false;
            secrets.FailWrites = false;
            secrets.DropDeletes = false;
            if (Directory.Exists(statePath)) Directory.Delete(statePath);
            window?.Close();
            proof.Requests.AddRange(endpoint.Requests);
            if (center != null)
            {
                proof.Notifications.Add(center.Snapshot());
                proof.Await(center.DisposeAsync().AsTask(), fault + ": scene-owned fault fixture drains without shutting down App.Services");
            }
        }
    }

    private static void ExercisePrivateMaterial(Proof proof, PrivateMaterialCase scenario)
    {
        string root = Path.Combine(proof.OutDir, "kvp-private-material-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string statePath = Path.Combine(root, "notifications.json");
        var privateStore = new FixtureSecrets(Path.Combine(root, "private-fake-secret-store.json"));
        using var barrier = new AsyncBarrierSecrets(privateStore);
        using var endpoint = new NotificationEndpoint { WebhookMode = ResponseMode.Unavailable };
        using var http = new HttpClient(endpoint, disposeHandler: false);
        var clock = new FixtureClock(Start);
        AlertCenter? center = null;
        AlertsWindow? window = null;
        try
        {
            var initial = center = new AlertCenter(statePath, privateStore, http, () => clock.Now);
            var setup = window = new AlertsWindow(initial);
            setup.Show(proof.Owner);
            proof.Pump(0.05);
            Enter(Named<TextBox>(setup, "AlertWebhookEndpoint"), WebhookEndpoint);
            Enter(Named<TextBox>(setup, "AlertSecret"), WebhookSecret);
            SelectKinds(setup, AlertKind.Price, AlertKind.Risk);
            Named<CheckBox>(setup, "AlertExternalConsent").IsChecked = true;
            Save(setup, proof);
            proof.Check(proof.Await(initial.RefreshPrivateMaterialAsync(), scenario + ": initial real private-material cache becomes available"),
                scenario + ": initial grant has confirmed private target/authorization material");
            var destination = initial.Snapshot().Destinations.Single();
            string account = AlertCenter.SecretAccount(destination.Id, destination.Revision);
            string protectedValue = privateStore.Get(account);
            string pendingId = "kvp-ui-material-" + scenario + "-pending";
            initial.Raise(AlertKind.Price, "UI-TEST", "Private cache restart", "Already redacted pending event; " + WebhookSecret + " " + WebhookEndpoint, pendingId);
            proof.Await(initial.FlushAsync(), scenario + ": initial unavailable delivery reaches durable outbox");
            proof.Await(initial.DeliverPendingAsync(), scenario + ": initial fake endpoint returns unavailable without exposing private response details");
            proof.Check(initial.Snapshot().Deliveries.Single().State == NotificationDeliveryState.Pending && endpoint.Requests.Count == 1,
                scenario + ": restart fixture begins with a real pending external event and stable identity");
            string originalDeliveryId = initial.Snapshot().Deliveries.Single().Id;
            setup.Close();
            window = null;
            proof.Await(initial.DisposeAsync().AsTask(), scenario + ": original notification service drains before the private read is blocked");
            center = null;

            clock.Advance(TimeSpan.FromHours(1));
            barrier.HoldReads();
            var restarted = center = new AlertCenter(statePath, barrier, http, () => clock.Now);
            var ui = window = new AlertsWindow(restarted);
            ui.Show(proof.Owner);
            proof.Until(() => barrier.Entered.Task.IsCompleted, scenario + ": restarted real notification consumer reaches the CT-aware blocked private-store read");
            var refresh = restarted.RefreshPrivateMaterialAsync();
            string priceId = "kvp-ui-material-" + scenario + "-price";
            string riskId = "kvp-ui-material-" + scenario + "-risk";
            string sensitive = "Protected producer content " + WebhookEndpoint + " " + WebhookSecret;
            var producers = Task.WhenAll(
                Task.Run(() => restarted.Raise(AlertKind.Price, "UI-TEST", "Concurrent price producer", sensitive, priceId)),
                Task.Run(() => restarted.Raise(AlertKind.Risk, "UI-TEST", "Concurrent risk producer", sensitive, riskId)));
            proof.Check(proof.Await(producers, scenario + ": concurrent actual local producers complete while protected reads remain blocked").All(accepted => accepted),
                scenario + ": blocked private-store IO cannot block or reject normal bounded local alert intake");
            proof.Until(() => Named<StackPanel>(ui, "AlertRecentHistory").Children.OfType<TextBlock>().Any(block => block.Text?.Contains(priceId, StringComparison.Ordinal) == true)
                && Named<StackPanel>(ui, "AlertRecentHistory").Children.OfType<TextBlock>().Any(block => block.Text?.Contains(riskId, StringComparison.Ordinal) == true),
                scenario + ": actual AlertsWindow renders local event identities while restart redaction material is unavailable");
            proof.Check(barrier.IsHolding && barrier.TimedOutReads == 0 && barrier.SynchronousReads == 0
                && endpoint.Requests.Count == 1 && restarted.Snapshot().Deliveries.Count == 1
                && !ContainsSecret(JsonSerializer.Serialize(restarted.Snapshot())) && !ContainsSecret(File.ReadAllText(statePath))
                && !ContainsSecret(VisibleText(ui)),
                scenario + ": unavailable restart redactors withhold sensitive content from UI/state/new delivery queues without blocking local history");
            proof.Until(() => VisibleText(ui).Contains("not yet durable", StringComparison.OrdinalIgnoreCase),
                scenario + ": actual UI explicitly distinguishes new memory-held intake from the already durable encrypted outbox");
            proof.Await(restarted.FlushAsync(), scenario + ": Flush persists safe metadata and existing ciphertext without claiming held bodies are durable");
            proof.Check(restarted.Snapshot().Deliveries.Count == 1 && restarted.Snapshot().NotDurableAlerts == 2
                && !File.ReadAllText(statePath).Contains("Protected producer content", StringComparison.Ordinal),
                scenario + ": flushing unavailable-key intake does not persist a secret-bearing body or invent a durable external delivery");
            proof.Capture(ui, "kvp_alert_private_material_blocked_" + scenario.ToString().ToLowerInvariant());
            proof.Notifications.Add(restarted.Snapshot());

            if (scenario == PrivateMaterialCase.Revoke)
            {
                SelectDestination(ui, destination.Id);
                int cancellationsBeforeRevoke = barrier.CancelledReads;
                Click(Named<Button>(ui, "AlertRevokeDestination"));
                proof.Until(() => restarted.Snapshot().Destinations.All(item => !item.Enabled) && barrier.CancelledReads > cancellationsBeforeRevoke,
                    "actual Revoke withdraws local permission and cancels the active asynchronous private read before that provider is released");
                string afterRevokeId = "kvp-ui-material-after-revoke";
                proof.Check(proof.Await(Task.Run(() => restarted.Raise(AlertKind.Risk, "UI-TEST", "Revoked private read", "Local after revoke remains available", afterRevokeId)),
                    "another actual producer completes while revoked private-store verification is still held"),
                    "local alert intake remains responsive during asynchronous revocation verification");
                proof.Until(() => Named<StackPanel>(ui, "AlertRecentHistory").Children.OfType<TextBlock>().Any(block => block.Text?.Contains(afterRevokeId, StringComparison.Ordinal) == true),
                    "actual UI history remains responsive while revocation retires blocked private material");
                proof.Check(endpoint.Requests.Count == 1 && !ContainsSecret(File.ReadAllText(statePath)) && !ContainsSecret(JsonSerializer.Serialize(restarted.Snapshot())),
                    "revocation cannot release deferred secret-bearing content or authorize an origin-only fallback send");
                barrier.ReleaseReads();
                proof.Await(ui.PendingOperation!, "actual Revoke finishes private deletion verification after the fake provider is available");
                proof.Until(() => refresh.IsCompleted, "revocation drains the concurrently requested private-material refresh");
                proof.Await(restarted.RefreshPrivateMaterialAsync(), "explicit refresh settles revoked startup material without unsafe deferred publication");
                proof.Await(restarted.FlushAsync(), "revoked placeholder/local history reaches ordinary storage safely");
                proof.Check(restarted.Snapshot().Destinations.All(item => !item.Enabled) && endpoint.Requests.Count == 1
                    && !ContainsSecret(File.ReadAllText(statePath)) && !ContainsSecret(JsonSerializer.Serialize(restarted.Snapshot())),
                    "deleted/unavailable restart material never becomes permission to publish the original deferred private content");
                proof.Capture(ui, "kvp_alert_private_material_revoked");
                ui.Close();
                window = null;
                proof.Await(restarted.DisposeAsync().AsTask(), "revoked blocked-read service drains before another restart");
                center = null;
                var off = center = new AlertCenter(statePath, new FixtureSecrets(Path.Combine(root, "private-fake-secret-store.json")), http, () => clock.Now);
                proof.Await(off.DeliverPendingAsync(), "next restart evaluates the persisted revoked permission and cancelled outbox");
                proof.Check(endpoint.Requests.Count == 1 && off.Snapshot().Destinations.All(item => !item.Enabled)
                    && !ContainsSecret(JsonSerializer.Serialize(off.Snapshot())), "restart cannot resurrect revoked private-material sends or deferred secrets");
            }
            else
            {
                if (scenario == PrivateMaterialCase.Missing)
                    privateStore.Set(account, null);
                endpoint.WebhookMode = ResponseMode.Accepted;
                barrier.ReleaseReads();
                proof.Until(() => refresh.IsCompleted, scenario + ": released background private-material operation completes");
                if (scenario == PrivateMaterialCase.Missing)
                {
                    proof.Check(!proof.Await(restarted.RefreshPrivateMaterialAsync(), "missing protected credential is reported through the actual refresh barrier")
                        && endpoint.Requests.Count == 1 && !ContainsSecret(File.ReadAllText(statePath))
                        && !ContainsSecret(JsonSerializer.Serialize(restarted.Snapshot())),
                        "deleted startup material fails closed: no original secret content, no HTTP and no origin-only endpoint fallback");
                    proof.Capture(ui, "kvp_alert_private_material_missing");
                    privateStore.Set(account, protectedValue);
                }
                proof.Check(proof.Await(restarted.RefreshPrivateMaterialAsync(), scenario + ": real private-material recovery finishes with verified redactors"),
                    scenario + ": available protected target/authentication enables safe recovery");
                proof.Await(restarted.DeliverPendingAsync(), scenario + ": actual delivery consumer processes recovered sanitized events");
                bool missingConsent = scenario == PrivateMaterialCase.Missing;
                proof.Until(() => restarted.Snapshot().Deliveries.Count == 3
                    && restarted.Snapshot().Deliveries.All(delivery => delivery.State ==
                        (missingConsent && delivery.Id == originalDeliveryId ? NotificationDeliveryState.Failed : NotificationDeliveryState.Delivered)),
                    scenario + ": recovered producers are acknowledged while definitively missing-consent work remains failed");
                proof.Check(endpoint.Requests.Count == (missingConsent ? 3 : 4) && endpoint.Requests.All(request => request.WebhookTargetMatched && request.AuthorizationMatched && !ContainsSecret(request.Payload))
                    && endpoint.Requests.Count(request => request.EventId == pendingId) == (missingConsent ? 1 : 2)
                    && endpoint.Requests.Where(request => request.EventId == pendingId).Select(request => request.DeliveryId).Distinct().Count() == 1
                    && !ContainsSecret(File.ReadAllText(statePath)) && !ContainsSecret(JsonSerializer.Serialize(restarted.Snapshot())),
                    scenario + ": recovery preserves the stable delivery and private target, never automatically resending a definitively unauthorized event");
                proof.Until(() => Named<StackPanel>(ui, "AlertRecentHistory").Children.OfType<TextBlock>().Any(block =>
                    block.Text?.Contains("Protected producer content", StringComparison.Ordinal) == true && block.Text.Contains("[redacted]", StringComparison.Ordinal)),
                    scenario + ": actual UI replaces withheld content only after real redaction material safely sanitizes it");
                proof.Check(restarted.Snapshot().NotDurableAlerts == 0,
                    scenario + ": successfully persisted recovered ciphertext clears the structured not-yet-durable intake count");
                proof.Capture(ui, "kvp_alert_private_material_recovered_" + scenario.ToString().ToLowerInvariant());
            }
            proof.Check(barrier.TimedOutReads == 0, scenario + ": blocked private material was cancelled/released causally, never by the fixture safety deadline");
            if (center != null) proof.Notifications.Add(center.Snapshot());
        }
        finally
        {
            barrier.ReleaseReads();
            window?.Close();
            proof.Requests.AddRange(endpoint.Requests);
            if (center != null)
            {
                proof.Notifications.Add(center.Snapshot());
                proof.Await(center.DisposeAsync().AsTask(), scenario + ": scene-owned private-material work cancels/drains without native stores or App.Services shutdown");
            }
        }
    }

    private static void ExerciseEncryptedOutbox(Proof proof, EncryptedOutboxCase scenario)
    {
        string root = Path.Combine(proof.OutDir, "kvp-encrypted-outbox-" + scenario + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string statePath = Path.Combine(root, "notifications.json");
        var privateStore = new FixtureSecrets(Path.Combine(root, "private-fake-secret-store.json"));
        using var barrier = new AsyncBarrierSecrets(privateStore);
        using var endpoint = new NotificationEndpoint { WebhookMode = ResponseMode.Unavailable };
        using var http = new HttpClient(endpoint, disposeHandler: false);
        var clock = new FixtureClock(Start);
        AlertCenter? center = null;
        AlertsWindow? window = null;
        string eventId = "kvp-ui-encrypted-" + scenario;
        string instrument = "KVP-PRIVATE-INSTRUMENT-" + scenario;
        string source = "KVP-PRIVATE-SOURCE-" + scenario;
        string body = "KVP-PRIVATE-BODY-" + scenario + ": approved execution details, quantity 1.";
        try
        {
            var initial = center = new AlertCenter(statePath, privateStore, http, () => clock.Now);
            var setup = window = new AlertsWindow(initial);
            setup.Show(proof.Owner);
            proof.Pump(0.05);
            Enter(Named<TextBox>(setup, "AlertWebhookEndpoint"), WebhookEndpoint);
            Enter(Named<TextBox>(setup, "AlertSecret"), WebhookSecret);
            SelectKinds(setup, AlertKind.Execution);
            Named<CheckBox>(setup, "AlertExternalConsent").IsChecked = true;
            Save(setup, proof);
            var destination = initial.Snapshot().Destinations.Single();
            string protectedValue = privateStore.Get(AlertCenter.SecretAccount(destination.Id, destination.Revision));
            using var credential = JsonDocument.Parse(protectedValue);
            string deliveryKey = credential.RootElement.GetProperty("DeliveryKey").GetString()
                ?? throw new InvalidDataException("Confirmed fake private grant has no outbox encryption key.");
            proof.Check(Convert.FromBase64String(deliveryKey).Length == 32,
                scenario + ": actual consent readback includes its private 256-bit per-revision encryption key");
            proof.Check(initial.Raise(AlertKind.Execution, instrument, source, body, eventId),
                scenario + ": real local producer accepts the approved private business body");
            proof.Await(initial.FlushAsync(), scenario + ": approved body reaches the real encrypted durable outbox");
            proof.Await(initial.DeliverPendingAsync(), scenario + ": unavailable fake HTTP response leaves an encrypted pending delivery");
            var pending = initial.Snapshot().Deliveries.Single();
            proof.Check(pending.State == NotificationDeliveryState.Pending && pending.Attempts == 1 && endpoint.Requests.Count == 1,
                scenario + ": encryption fixture starts from a real request and visible pending retry, not a manufactured state row");
            var original = CheckEncryptedState(proof, statePath, pending.Id, deliveryKey, scenario + " initial", eventId, instrument, source, body);
            setup.Close();
            window = null;
            proof.Await(initial.DisposeAsync().AsTask(), scenario + ": original encrypted consumer releases its durable state lease");
            center = null;
            proof.Check(ReadEncryptedEnvelope(statePath, pending.Id) == original,
                scenario + ": normal service shutdown preserves the original authenticated ciphertext verbatim");
            clock.Advance(TimeSpan.FromHours(1));
            endpoint.WebhookMode = ResponseMode.Accepted;

            if (scenario == EncryptedOutboxCase.BlockedRestart)
            {
                barrier.HoldReads();
                var blocked = center = new AlertCenter(statePath, barrier, http, () => clock.Now);
                var blockedUi = window = new AlertsWindow(blocked);
                blockedUi.Show(proof.Owner);
                proof.Until(() => barrier.Entered.Task.IsCompleted,
                    "encrypted pending restart reaches the actual CT-aware unavailable private-key consumer");
                proof.Await(blocked.FlushAsync(),
                    "unavailable-key Flush writes only safe metadata while preserving the original encrypted body");
                proof.Check(endpoint.Requests.Count == 1 && barrier.SynchronousReads == 0 && barrier.IsHolding
                    && blocked.Snapshot().Deliveries.Single().State == NotificationDeliveryState.Pending
                    && !VisibleText(blockedUi).Contains(body, StringComparison.Ordinal),
                    "blocked restart neither sends a placeholder nor exposes a body without its original private key");
                CheckEncryptedState(proof, statePath, pending.Id, deliveryKey, "blocked restart", eventId, instrument, source, body);
                proof.Check(ReadEncryptedEnvelope(statePath, pending.Id) == original,
                    "unavailable-key metadata Flush retains nonce, authenticated event identity, ciphertext and tag unchanged");
                proof.CaptureDelivery(blockedUi, pending.Id, "kvp_alert_encrypted_blocked_restart");
                blockedUi.Close();
                window = null;
                proof.Await(blocked.DisposeAsync().AsTask(),
                    "scene-owned encrypted service can shut down while its original private-key read remains held");
                center = null;
                proof.Check(barrier.CancelledReads > 0 && barrier.TimedOutReads == 0
                    && endpoint.Requests.Count == 1 && ReadEncryptedEnvelope(statePath, pending.Id) == original,
                    "cancellable blocked shutdown preserves the same encrypted pending body instead of overwriting it with a placeholder");
                barrier.ReleaseReads();
            }
            else
            {
                var state = JsonNode.Parse(File.ReadAllText(statePath))?.AsObject()
                    ?? throw new InvalidDataException("Encrypted fixture state is missing.");
                var delivery = state["Deliveries"]!.AsArray().Single(item => item!["Id"]!.GetValue<string>() == pending.Id)!;
                if (scenario == EncryptedOutboxCase.Revision)
                {
                    string changedRevision = destination.Revision + "-changed";
                    state["Destinations"]!.AsArray().Single()!["Revision"] = changedRevision;
                    delivery["Revision"] = changedRevision;
                    // Preserve the entire private credential, including its original DeliveryKey. Only AAD consent identity changes.
                    privateStore.Set(AlertCenter.SecretAccount(destination.Id, changedRevision), protectedValue);
                }
                else
                {
                    var envelope = delivery["EncryptedAlert"]!;
                    string field = scenario == EncryptedOutboxCase.Tag ? "Tag" : "Ciphertext";
                    byte[] bytes = Convert.FromBase64String(envelope[field]!.GetValue<string>());
                    bytes[bytes.Length / 2] ^= 1;
                    envelope[field] = Convert.ToBase64String(bytes);
                }
                File.WriteAllText(statePath, state.ToJsonString());
                proof.Check(!ContainsSecret(File.ReadAllText(statePath)) && !File.ReadAllText(statePath).Contains(body, StringComparison.Ordinal),
                    scenario + ": bounded on-disk tampering modifies only encrypted authentication or bound consent metadata, never plaintext Alert.Message");
            }

            var recovered = center = new AlertCenter(statePath, privateStore, http, () => clock.Now);
            var ui = window = new AlertsWindow(recovered);
            ui.Show(proof.Owner);
            proof.Check(proof.Await(recovered.RefreshPrivateMaterialAsync(), scenario + ": original fake protected credential becomes available"),
                scenario + ": private-material readiness is evaluated separately from ciphertext authentication and persistence");
            proof.Await(recovered.DeliverPendingAsync(), scenario + ": normal HTTP consumer evaluates recovered authenticated content");
            if (scenario == EncryptedOutboxCase.BlockedRestart)
            {
                proof.Until(() => recovered.Snapshot().Deliveries.Single().State == NotificationDeliveryState.Delivered,
                    "second actual restart decrypts the retained pending event and publishes endpoint acceptance");
                var sent = recovered.Snapshot().Deliveries.Single();
                var requests = endpoint.Requests.ToArray();
                using var payload = JsonDocument.Parse(requests.Last().Payload);
                proof.Check(requests.Length == 2 && requests.All(request => request.EventId == eventId && request.DeliveryId == pending.Id
                        && request.AuthorizationMatched && request.WebhookTargetMatched && !ContainsSecret(request.Payload))
                    && payload.RootElement.GetProperty("id").GetString() == eventId
                    && payload.RootElement.GetProperty("message").GetString() == body
                    && payload.RootElement.GetProperty("instrument").GetString() == instrument
                    && payload.RootElement.GetProperty("source").GetString() == source
                    && sent.Alert.Id == eventId && sent.Alert.OutboxEventId == original.EventId,
                    "recovery sends the original approved body and original event headers, never generic metadata, with the same opaque delivery identity");
                proof.Until(() => Named<StackPanel>(ui, "AlertRecentHistory").Children.OfType<TextBlock>().Any(block =>
                    block.Text?.Contains(body, StringComparison.Ordinal) == true && block.Text.Contains(eventId, StringComparison.Ordinal)),
                    "actual UI restores the original sanitized local body only after successful private-key authentication");
                proof.Check(sent.Status.Contains("Human receipt is not confirmed", StringComparison.Ordinal),
                    "authenticated ciphertext recovery still does not claim exactly-once delivery or human receipt");
                proof.CaptureDelivery(ui, sent.Id, "kvp_alert_encrypted_authenticated_recovery");
            }
            else
            {
                proof.Until(() => recovered.Snapshot().Deliveries.Single().State == NotificationDeliveryState.Failed,
                    scenario + ": actual encrypted consumer exposes authentication failure instead of sending forged or wrong-consent content");
                proof.Check(endpoint.Requests.Count == 1 && DeliveryText(ui, pending.Id).Contains("Failed", StringComparison.Ordinal)
                    && DeliveryText(ui, pending.Id).Contains("Nothing was sent", StringComparison.Ordinal),
                    scenario + ": actual AlertsWindow renders controlled authentication failure and no new HTTP request");
                Click(DeliveryButton(ui, pending.Id, "Retry"));
                proof.Await(recovered.DeliverPendingAsync(), scenario + ": actual Retry control re-enters normal protected delivery validation");
                proof.Until(() => recovered.Snapshot().Deliveries.Single().State == NotificationDeliveryState.Failed,
                    scenario + ": explicit retry cannot bypass ciphertext or consent-bound authentication");
                string localId = "kvp-ui-encrypted-local-" + scenario;
                proof.Check(recovered.Raise(AlertKind.Risk, "UI-TEST", "Authenticated outbox failure", "Local protection remains available", localId, allowExternal: false),
                    scenario + ": failed ciphertext cannot block an independent actual local alert producer");
                proof.Await(recovered.FlushAsync(), scenario + ": independent local alert metadata survives failed ciphertext validation");
                proof.Until(() => Named<StackPanel>(ui, "AlertRecentHistory").Children.OfType<TextBlock>().Any(block =>
                    block.Text?.Contains(localId, StringComparison.Ordinal) == true && block.Text.Contains("Local protection remains available", StringComparison.Ordinal)),
                    scenario + ": local alert remains rendered despite external encrypted-body failure");
                proof.Check(endpoint.Requests.Count == 1 && recovered.Snapshot().Deliveries.Single().Id == pending.Id,
                    scenario + ": rejected ciphertext never reaches HTTP and retry does not invent a fresh delivery identity");
                proof.CaptureDelivery(ui, pending.Id, "kvp_alert_encrypted_tampered_" + scenario.ToString().ToLowerInvariant());
            }
            proof.Await(recovered.FlushAsync(), scenario + ": final metadata-only encrypted state is durably retained");
            CheckEncryptedState(proof, statePath, pending.Id, deliveryKey, scenario + " final", eventId, instrument, source, body);
            using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(recovered.Snapshot()));
            bool privateKeyInPayload = endpoint.Requests.Any(request => {
                using var payload = JsonDocument.Parse(request.Payload);
                return JsonContainsValue(payload.RootElement, deliveryKey);
            });
            proof.Check(!privateKeyInPayload && !JsonContainsValue(snapshot.RootElement, deliveryKey)
                && !VisibleText(ui).Contains(deliveryKey, StringComparison.Ordinal),
                scenario + ": the actual private encryption key never enters approved HTTP payloads, public snapshot bodies or rendered controls");
            if (scenario == EncryptedOutboxCase.BlockedRestart)
                proof.Check(ReadEncryptedEnvelope(statePath, pending.Id) == original,
                    "successful recovery keeps its original ciphertext authoritative rather than re-encrypting metadata placeholders");
            proof.Notifications.Add(recovered.Snapshot());
        }
        finally
        {
            barrier.ReleaseReads();
            window?.Close();
            proof.Requests.AddRange(endpoint.Requests);
            if (center != null)
            {
                proof.Notifications.Add(center.Snapshot());
                proof.Await(center.DisposeAsync().AsTask(), scenario + ": scene-owned encrypted consumer drains without global App.Services shutdown");
            }
        }
    }

    private static NotificationCiphertext ReadEncryptedEnvelope(string path, string deliveryId)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(path));
        var delivery = state.RootElement.GetProperty("Deliveries").EnumerateArray().Single(item => item.GetProperty("Id").GetString() == deliveryId);
        return delivery.GetProperty("EncryptedAlert").Deserialize<NotificationCiphertext>()
            ?? throw new InvalidDataException("Real pending delivery has no encrypted envelope.");
    }

    private static bool JsonContainsValue(JsonElement value, string fragment)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString()?.Contains(fragment, StringComparison.Ordinal) == true;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                if (JsonContainsValue(property.Value, fragment)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (JsonContainsValue(item, fragment)) return true;
        return false;
    }

    private static NotificationCiphertext CheckEncryptedState(Proof proof, string path, string deliveryId, string deliveryKey, string phase, params string[] plaintext)
    {
        string raw = File.ReadAllText(path);
        using var state = JsonDocument.Parse(raw);
        var root = state.RootElement;
        static bool Opaque(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);
        static bool Metadata(JsonElement alert) => Opaque(alert.GetProperty("Id").GetString())
            && alert.GetProperty("OutboxEventId").GetString() == alert.GetProperty("Id").GetString()
            && alert.GetProperty("Instrument").GetString() == ""
            && alert.GetProperty("Source").GetString() == "Alert center"
            && alert.GetProperty("Message").GetString()?.Contains("not stored", StringComparison.Ordinal) == true
            && !alert.GetProperty("AllowExternal").GetBoolean();
        bool privateState = root.GetProperty("Version").GetInt32() == 3
            && root.GetProperty("Recent").EnumerateArray().All(Metadata)
            && root.GetProperty("Deliveries").EnumerateArray().All(item => Metadata(item.GetProperty("Alert")))
            && root.GetProperty("SeenIds").EnumerateArray().All(item => Opaque(item.GetString()))
            && plaintext.All(value => !JsonContainsValue(root, value))
            && !JsonContainsValue(root, deliveryKey) && !ContainsSecret(raw);
        proof.Check(privateState, phase + ": schema3 ordinary state contains only opaque identities/generic local metadata, never original body fields, event IDs, URI secrets or private encryption keys");
        var envelope = ReadEncryptedEnvelope(path, deliveryId);
        int nonceBytes = Convert.FromBase64String(envelope.Nonce).Length;
        int tagBytes = Convert.FromBase64String(envelope.Tag).Length;
        int ciphertextBytes = Convert.FromBase64String(envelope.Ciphertext).Length;
        bool bounded = envelope.Schema == 1 && Opaque(envelope.EventId) && Opaque(deliveryId)
            && nonceBytes == 12 && tagBytes == 16 && ciphertextBytes is > 0 and <= 16 * 1024;
        proof.Check(bounded, phase + ": actual persisted authenticated envelope has bounded schema/event identity, 96-bit nonce, 128-bit tag and at most 16KiB ciphertext");
        proof.EncryptedOutbox.Add(new { phase, deliveryId, outboxEventId = envelope.EventId, envelope.Schema,
            nonceBytes, tagBytes, ciphertextBytes, metadataPrivate = privateState, envelopeBounded = bounded });
        return envelope;
    }

    private static void ExercisePracticeSaveFailure(Proof proof)
    {
        var services = App.Services;
        var trading = proof.Owner.Trading;
        var original = trading.ActiveTab;
        string root = Path.Combine(proof.OutDir, "kvp-practice-save-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var endpoint = new NotificationEndpoint();
        using var http = new HttpClient(endpoint, disposeHandler: false);
        var external = new AlertCenter(Path.Combine(root, "notifications.json"), new FixtureSecrets(Path.Combine(root, "private-fake-secret-store.json")), http);
        var alerts = new AlertsWindow(external);
        var observed = new ConcurrentQueue<AlertEvent>();
        void ForwardActualProducer(AlertEvent alert)
        {
            if (alert.Source != "Replay results") return;
            observed.Enqueue(alert);
            external.Raise(alert.Kind, alert.Instrument, alert.Source, alert.Message, alert.Id, alert.TimeUtc, alert.AllowExternal);
        }
        ChartTab? tab = null;
        string? resultBlocker = null;
        services.Alerts.Raised += ForwardActualProducer;
        try
        {
            alerts.Show(proof.Owner);
            proof.Pump(0.05);
            Enter(Named<TextBox>(alerts, "AlertWebhookEndpoint"), WebhookEndpoint);
            Enter(Named<TextBox>(alerts, "AlertSecret"), WebhookSecret);
            SelectKinds(alerts, AlertKind.System);
            Named<CheckBox>(alerts, "AlertExternalConsent").IsChecked = true;
            Save(alerts, proof);
            string id = "kvp-result-fault-ui-" + Guid.NewGuid().ToString("N");
            var source = new Instrument { FeedId = id, Symbol = "KVP-RESULT-SAVE-FAULT", TickSize = 1, VolumeStep = 1, PointValue = 1 };
            using (var writer = new TickFile.Writer(TickFile.DayPath(AppPaths.Ticks, id, source.Symbol, DateOnly.FromDateTime(Start)), source))
            {
                writer.Write(new Trade(1, Start.AddMinutes(-1), 99, 10, TradeSide.Buy));
                writer.Write(new Trade(2, Start, 100, 10, TradeSide.Buy));
                writer.Write(new Trade(3, Start.AddSeconds(29), 101, 10, TradeSide.Buy));
            }
            proof.Owner.Navigate("trading");
            proof.Await(trading.OpenPracticeAsync(ReplaySelection.Selected(source, Start, Start.AddSeconds(30), Seed), ChartPeriod.M1),
                "actual chart opens the run whose result publication will fail");
            tab = trading.ActiveTab ?? throw new InvalidOperationException("Result-failure practice did not select a chart.");
            var run = services.ReplaySessions.TryGet(tab.Model!.Data)!;
            proof.Until(() => !run.Data.IsLoadingHistory, "result-failure practice reaches its actual End control");
            resultBlocker = Path.Combine(AppPaths.Root, "Practice", run.RunId.ToString("N") + ".json");
            Directory.CreateDirectory(resultBlocker);
            Click(Named<Button>(trading, "ReplayEnd"));
            proof.Until(() => run.Ended && observed.Any(alert => alert.Message.Contains("could not be saved", StringComparison.Ordinal)),
                "actual ReplayEnd result writer exposes its owned publication failure through the normal app alert producer");
            proof.Await(services.Alerts.FlushAsync(), "actual app-owned practice failure alert drains locally");
            proof.Await(external.FlushAsync(), "unchanged actual producer event reaches the same real notification consumer with a fake external transport");
            proof.Await(external.DeliverPendingAsync(), "external delivery consumer evaluates the actual practice-failure event's permission");
            var failure = observed.Single(alert => alert.Message.Contains("could not be saved", StringComparison.Ordinal));
            proof.Check(!failure.AllowExternal && external.Snapshot().Recent.Any(alert => alert.Id == failure.Id)
                && external.Snapshot().Deliveries.Count == 0 && endpoint.Requests.IsEmpty,
                "actual practice-result save failure stays visible but cannot egress even to an explicitly approved System destination");
            proof.Until(() => Named<StackPanel>(alerts, "AlertRecentHistory").Children.OfType<TextBlock>().Any(block => block.Text?.Contains(failure.Id, StringComparison.Ordinal) == true),
                "actual AlertsWindow renders the unchanged practice save-failure event locally");
            proof.Capture(alerts, "kvp_alert_practice_result_save_failure_local_only");
            proof.Notifications.Add(external.Snapshot());
            proof.Replay.Add(run.Result());
        }
        finally
        {
            services.Alerts.Raised -= ForwardActualProducer;
            alerts.Close();
            if (tab != null) trading.CloseChart(tab);
            if (original != null) trading.Activate(original);
            if (resultBlocker != null && Directory.Exists(resultBlocker)) Directory.Delete(resultBlocker);
            proof.Requests.AddRange(endpoint.Requests);
            proof.Await(external.DisposeAsync().AsTask(), "scene-owned practice-failure notification consumer drains without disposing App.Services");
        }
    }

    private static void ExerciseNotificationDurability(Proof proof)
    {
        string root = Path.Combine(proof.OutDir, "kvp-alert-durability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string statePath = Path.Combine(root, "notifications.json");
        var privateStore = new FixtureSecrets(Path.Combine(root, "private-fake-secret-store.json"));
        using var endpoint = new NotificationEndpoint();
        using var http = new HttpClient(endpoint, disposeHandler: false);
        var center = new AlertCenter(statePath, privateStore, http, () => Start);
        var window = new AlertsWindow(center);
        const string retainedEventId = "kvp-ui-undurable-retained";
        const string cancelledEventId = "kvp-ui-undurable-cancelled";
        const string localBody = "Storage retains local history; ";
        try
        {
            window.Show(proof.Owner);
            proof.Pump(0.05);
            Enter(Named<TextBox>(window, "AlertWebhookEndpoint"), WebhookEndpoint);
            Enter(Named<TextBox>(window, "AlertSecret"), WebhookSecret);
            SelectKinds(window, AlertKind.Price, AlertKind.Risk);
            Named<CheckBox>(window, "AlertExternalConsent").IsChecked = true;
            Save(window, proof);
            var destination = center.Snapshot().Destinations.Single();
            using var credential = JsonDocument.Parse(privateStore.Get(AlertCenter.SecretAccount(destination.Id, destination.Revision)));
            string deliveryKey = credential.RootElement.GetProperty("DeliveryKey").GetString()
                ?? throw new InvalidDataException("Durability fixture has no confirmed private encryption key.");
            File.Delete(statePath);
            Directory.CreateDirectory(statePath);
            proof.Check(center.Raise(AlertKind.Price, "UI-TEST", "Disk durability", localBody + WebhookSecret + " " + WebhookEndpoint, retainedEventId)
                && center.Raise(AlertKind.Risk, "UI-TEST", "Disk durability", localBody + "cancel this one", cancelledEventId),
                "approved actual local producers remain available when only their ordinary state commit fails");
            proof.Until(() => center.Snapshot().Deliveries.Count == 2
                && center.Snapshot().Deliveries.All(delivery => delivery.State == NotificationDeliveryState.AwaitingPersistence),
                "real encrypted outbox exposes both failed commits as AwaitingPersistence, never durable Pending");
            proof.Rejects<InvalidOperationException>(() => proof.Await(center.FlushAsync(), "real failed commit returns without blocking local UI"),
                "encrypted intake cannot claim durability when its atomic state-file replacement fails");
            var retained = center.Snapshot().Deliveries.Single(delivery => delivery.Alert.Id == retainedEventId);
            var cancelled = center.Snapshot().Deliveries.Single(delivery => delivery.Alert.Id == cancelledEventId);
            var originalEnvelope = retained.EncryptedAlert ?? throw new InvalidDataException("Uncommitted approved event has no bounded encrypted body.");
            proof.Until(() => Named<StackPanel>(window, "AlertRecentHistory").Children.OfType<TextBlock>().Count(block =>
                    block.Text?.Contains(localBody, StringComparison.Ordinal) == true) == 2
                && VisibleText(window).Contains("not yet durable", StringComparison.OrdinalIgnoreCase),
                "actual local history and persistent not-yet-durable status remain rendered through disk failure");
            proof.Check(center.Snapshot().NotDurableAlerts == 2 && endpoint.Requests.IsEmpty
                && retained.Attempts == 0 && cancelled.Attempts == 0
                && DeliveryText(window, retained.Id).Contains("AwaitingPersistence", StringComparison.Ordinal)
                && !DeliveryButton(window, retained.Id, "Retry").IsEnabled
                && DeliveryButton(window, retained.Id, "Cancel").IsEnabled
                && !center.Retry(retained.Id),
                "actual undurable row permits cancellation, disables Retry, rejects consumer retry and never increments HTTP attempts");
            proof.Await(center.DeliverPendingAsync(), "normal delivery consumer evaluates the still-unavailable persistence boundary");
            Click(Named<Button>(window, "AlertPreviewPayload"));
            proof.Check(endpoint.Requests.IsEmpty
                && !ContainsSecret(JsonSerializer.Serialize(center.Snapshot()))
                && !ContainsSecret(Named<TextBox>(window, "AlertPayloadPreview").Text ?? "")
                && Named<TextBox>(window, "AlertPayloadPreview").Text?.Contains(localBody, StringComparison.Ordinal) == true,
                "failed storage blocks actual HTTP while approved local preview and secret-redacted history continue");
            proof.Notifications.Add(center.Snapshot());
            proof.CaptureDelivery(window, retained.Id, "kvp_alert_awaiting_persistence");

            Click(DeliveryButton(window, cancelled.Id, "Cancel"));
            proof.Until(() => center.Snapshot().Deliveries.Single(delivery => delivery.Id == cancelled.Id).State == NotificationDeliveryState.Cancelled,
                "actual Cancel control withdraws the second event before any durable outbox or HTTP send exists");
            proof.Check(center.Snapshot().NotDurableAlerts == 1 && endpoint.Requests.IsEmpty
                && center.Snapshot().Recent.Any(alert => alert.Id == cancelledEventId && alert.Message.Contains(localBody, StringComparison.Ordinal)),
                "cancelling undurable external work does not erase its local alert or release the remaining unsafe send");
            proof.CaptureDelivery(window, cancelled.Id, "kvp_alert_undurable_cancelled");

            endpoint.WebhookMode = ResponseMode.Unavailable;
            Directory.Delete(statePath);
            proof.Await(center.FlushAsync(), "restored owned disk path permits the first actual durable encrypted commit");
            proof.Await(center.DeliverPendingAsync(), "normal delivery consumer may first attempt HTTP only after successful encrypted persistence");
            proof.Until(() => endpoint.Requests.Count == 1
                && center.Snapshot().Deliveries.Single(delivery => delivery.Id == retained.Id).State == NotificationDeliveryState.Pending,
                "successful persistence promotes the original ciphertext to durable Pending after a real unavailable HTTP response");
            var pending = center.Snapshot().Deliveries.Single(delivery => delivery.Id == retained.Id);
            proof.Check(center.Snapshot().NotDurableAlerts == 0 && pending.Attempts == 1
                && center.Snapshot().Deliveries.Single(delivery => delivery.Id == cancelled.Id).State == NotificationDeliveryState.Cancelled
                && ReadEncryptedEnvelope(statePath, retained.Id) == originalEnvelope
                && endpoint.Requests.Single().EventId == retainedEventId && endpoint.Requests.Single().DeliveryId == retained.Id,
                "atomic recovery preserves original encrypted body and event/delivery IDs, clears undurable count, and never resurrects cancelled intake");
            using (var persisted = JsonDocument.Parse(File.ReadAllText(statePath)))
                proof.Check(persisted.RootElement.GetProperty("Deliveries").EnumerateArray()
                        .All(delivery => delivery.GetProperty("State").GetInt32() != (int)NotificationDeliveryState.AwaitingPersistence),
                    "committed ordinary state contains durable Pending/Cancelled projections, not false persisted AwaitingPersistence rows");
            CheckEncryptedState(proof, statePath, retained.Id, deliveryKey, "restored disk commit", retainedEventId, cancelledEventId, localBody);
            proof.CaptureDelivery(window, retained.Id, "kvp_alert_durable_after_storage_recovery");

            endpoint.WebhookMode = ResponseMode.Accepted;
            Click(DeliveryButton(window, retained.Id, "Retry"));
            proof.Await(center.DeliverPendingAsync(), "actual Retry reuses the now-durable original delivery after fake endpoint recovery");
            proof.Until(() => center.Snapshot().Deliveries.Single(delivery => delivery.Id == retained.Id).State == NotificationDeliveryState.Delivered,
                "real fake-endpoint acknowledgment publishes Delivered only for the retained, durably committed event");
            proof.Check(endpoint.Requests.Count == 2 && endpoint.Requests.All(request => request.EventId == retainedEventId
                    && request.DeliveryId == retained.Id && request.AuthorizationMatched && request.WebhookTargetMatched && !ContainsSecret(request.Payload))
                && center.Snapshot().NotDurableAlerts == 0
                && DeliveryText(window, retained.Id).Contains("Human receipt is not confirmed", StringComparison.Ordinal),
                "disk recovery and actual retry preserve approved redacted payload/identity without claiming human receipt or delivering cancelled work");
            proof.Notifications.Add(center.Snapshot());
            proof.CaptureDelivery(window, retained.Id, "kvp_alert_storage_recovered_delivered");
        }
        finally
        {
            window.Close();
            proof.Requests.AddRange(endpoint.Requests);
            proof.Notifications.Add(center.Snapshot());
            proof.Await(center.DisposeAsync().AsTask(), "scene-owned durability consumer drains without touching App.Services lifecycle");
            if (Directory.Exists(statePath)) Directory.Delete(statePath);
        }
    }

    private static void ExerciseUnavailableStorage(Proof proof)
    {
        string root = Path.Combine(proof.OutDir, "kvp-alert-storage-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string blocker = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocker, "Owned UI fixture blocks directory creation.");
        using var endpoint = new NotificationEndpoint();
        using var http = new HttpClient(endpoint, disposeHandler: false);
        var center = new AlertCenter(Path.Combine(blocker, "notifications.json"), new FixtureSecrets(Path.Combine(root, "private-fake-secret-store.json")), http);
        var window = new AlertsWindow(center);
        int observed = 0;
        center.Raised += _ => throw new InvalidOperationException("Owned failed notification listener");
        center.Raised += _ => Interlocked.Increment(ref observed);
        try
        {
            window.Show(proof.Owner);
            foreach (var kind in new[] { AlertKind.Price, AlertKind.Indicator, AlertKind.Strategy, AlertKind.Risk, AlertKind.Execution })
                proof.Check(center.Raise(kind, "UI-TEST", "Local storage failure", "Local alert survives unavailable disk: " + kind, "kvp-ui-disk-" + kind),
                    "normal local alert intake accepts " + kind + " despite unavailable storage");
            proof.Until(() => center.Snapshot().Recent.Count == 5 && Volatile.Read(ref observed) == 5,
                "throwing notification listener and disk failure cannot suppress later local subscribers");
            proof.Rejects<InvalidOperationException>(() => proof.Await(center.FlushAsync(), "failed storage operation returns without blocking local UI"),
                "durable flush exposes unavailable storage instead of claiming persistence");
            proof.Until(() => Named<StackPanel>(window, "AlertRecentHistory").Children.Count == 5,
                "actual Alerts window renders all five alert kinds' local entries during disk failure");
            proof.Check(endpoint.Requests.IsEmpty && center.Snapshot().Status.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
                "unavailable disk is visible and blocks external sends, not local notifications");
            Click(Named<Button>(window, "AlertPreviewPayload"));
            proof.Check(Named<TextBox>(window, "AlertPayloadPreview").Text!.Contains("Local alert survives", StringComparison.Ordinal),
                "actual local payload review remains available even when notification storage is unavailable");
            proof.Capture(window, "kvp_alert_disk_failure");
            Named<StackPanel>(window, "AlertRecentHistory").BringIntoView();
            proof.Capture(window, "kvp_alert_disk_failure_local_history");
            proof.Notifications.Add(center.Snapshot());
        }
        finally
        {
            window.Close();
            proof.Await(center.DisposeAsync().AsTask(), "failed scene-owned notification service drains without disposing app composition");
        }
    }

    private static void CheckWebhookPayload(Proof proof, string payload, AlertEvent alert)
    {
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        string[] approved = ["app", "id", "time", "kind", "instrument", "source", "message", "text"];
        proof.Check(root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(approved.Order(StringComparer.Ordinal))
            && root.GetProperty("app").GetString() == "Trading Hub" && root.GetProperty("id").GetString() == alert.Id
            && root.GetProperty("time").GetDateTime() == alert.TimeUtc && root.GetProperty("kind").GetString() == "price"
            && root.GetProperty("instrument").GetString() == "UI-TEST" && root.GetProperty("source").GetString() == "Approved price scene"
            && root.GetProperty("message").GetString() == "Approved payload; [redacted]",
            "webhook payload contains only the approved finite fields, causal event identity and redacted message, not accounts or credentials");
    }

    private static T Named<T>(Control root, string name) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Enter(TextBox box, string text)
    {
        box.BringIntoView();
        box.Focus();
        box.SelectAll();
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Back, Source = box });
        box.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text, Source = box });
    }
    private static void SelectKinds(AlertsWindow window, params AlertKind[] kinds)
    {
        foreach (var kind in Enum.GetValues<AlertKind>()) Named<CheckBox>(window, "AlertKind_" + kind).IsChecked = kinds.Contains(kind);
    }
    private static void Save(AlertsWindow window, Proof proof)
    {
        Click(Named<Button>(window, "AlertSaveDestination"));
        proof.Await(window.PendingOperation ?? throw new InvalidOperationException("Save did not start its normal operation."), "actual destination Save operation completes");
    }
    private static void SelectDestination(AlertsWindow window, string id)
    {
        var saved = Named<ComboBox>(window, "AlertDestinations");
        saved.SelectedItem = saved.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, id));
    }
    private static StackPanel DeliveryRow(AlertsWindow window, string id) => Named<StackPanel>(window, "AlertDeliveryHistory").Children.OfType<StackPanel>()
        .Single(row => row.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains("Delivery ID: " + id, StringComparison.Ordinal) == true));
    private static string DeliveryText(AlertsWindow window, string id) => string.Join("\n", DeliveryRow(window, id).GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
    private static Button DeliveryButton(AlertsWindow window, string id, string action) => DeliveryRow(window, id).GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(button.Content, action) || Equals(button.Content, Lang.T(action)));
    private static bool ContainsSecret(string value) => value.Contains(WebhookSecret, StringComparison.Ordinal)
        || value.Contains(WebhookSecret[7..], StringComparison.Ordinal) || value.Contains(ReplacementSecret, StringComparison.Ordinal)
        || value.Contains(ReplacementSecret[7..], StringComparison.Ordinal) || value.Contains(TelegramSecret, StringComparison.Ordinal)
        || value.Contains(WebhookCapability, StringComparison.Ordinal) || value.Contains(WebhookQuerySecret, StringComparison.Ordinal)
        || value.Contains(WebhookHostSecret, StringComparison.Ordinal);
    private static string Redact(string text) => text.Replace(WebhookSecret, "[redacted]", StringComparison.Ordinal)
        .Replace(WebhookSecret[7..], "[redacted]", StringComparison.Ordinal).Replace(ReplacementSecret, "[redacted]", StringComparison.Ordinal)
        .Replace(ReplacementSecret[7..], "[redacted]", StringComparison.Ordinal).Replace(TelegramSecret, "[redacted]", StringComparison.Ordinal)
        .Replace(WebhookEndpoint, WebhookDisplay, StringComparison.Ordinal)
        .Replace(WebhookCapability, "[redacted]", StringComparison.Ordinal)
        .Replace(WebhookQuerySecret, "[redacted]", StringComparison.Ordinal)
        .Replace(WebhookHostSecret, "[redacted]", StringComparison.Ordinal);

    private static Button LabeledButton(Control root, string label) => root.GetVisualDescendants().OfType<Button>().Single(button =>
        Equals(button.Content, label) || Equals(button.Content, Lang.T(label))
        || button.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == label || block.Text == Lang.T(label)));
    private static string VisibleText(Window window) => (window.Title ?? "") + "\n" + string.Join("\n",
        window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text));
    private static bool HasAbsoluteFixtureDate(string text) => text.Contains("2026-09-07", StringComparison.Ordinal)
        || text.Contains("07/09/2026", StringComparison.Ordinal) || text.Contains("09/07/2026", StringComparison.Ordinal);

    private const string OpenLinkStrategySource = """
        using TradingHub.Strategies;
        using TradingHub.Market;
        public sealed class KvpUiOpenLinkMarks : Strategy
        {
            public KvpUiOpenLinkMarks() { StopLossTicks = 0; TakeProfitTicks = 0; }
            protected override void OnTick(Trade tick)
            {
                if (tick.Id == 21 && IsFlat) Buy(2, "Known-mark owned entry");
            }
        }
        """;

    private const string ProtectedGapStrategySource = """
        using TradingHub.Strategies;
        using TradingHub.Market;
        public sealed class KvpUiProtectedGap : Strategy
        {
            public KvpUiProtectedGap() { StopLossTicks = 5; TakeProfitTicks = 10; }
            protected override void OnTick(Trade tick)
            {
                if (tick.Id == 1) Buy(1, "Historical entry");
                if (tick.Id == 2) ClosePosition("Historical exit");
                if (tick.Id == 21 && IsFlat) BuyStop(105, 1, "Owned gap entry");
                if ((tick.Id == 23 || tick.Id == 24) && IsFlat) Buy(1, "Private visual sample");
            }
        }
        """;


    private const string StrategySource = """
        using TradingHub.Strategies;
        using TradingHub.Market;
        public sealed class KvpUiCausalReplay : Strategy
        {
            [Parameter("Event prefix")]
            public string EventPrefix { get; set; } = "kvp-ui-";
            [Parameter("Sell first")]
            public bool SellFirst { get; set; }
            [Parameter("Exit print ID")]
            public int ExitId { get; set; } = 23;
            protected override void OnInit() { Alert("Historical initialization must stay silent", EventPrefix + "init"); }
            protected override void OnBar(Bar bar) { Alert("Observed closed bar", EventPrefix + "bar-" + bar.Time.Ticks); }
            protected override void OnTick(Trade tick)
            {
                Alert("Observed causal replay print", EventPrefix + "tick-" + tick.Id);
                if (tick.Id == 22) { if (SellFirst) Sell(Quantity); else Buy(Quantity); }
                if (tick.Id == ExitId) ClosePosition("Owned replay close");
            }
        }
        """;

    private enum ResponseMode { Accepted, Unavailable, Held, RejectedAcknowledgment }
    private enum UncheckedSaveFault { SecretRead, SecretWrite, SecretDelete, Disk }
    private enum PrivateMaterialCase { Recover, Missing, Revoke }
    private enum EncryptedOutboxCase { BlockedRestart, Tag, Ciphertext, Revision }
    private sealed record HttpObservation(string Uri, string Method, string? MediaType, string EventId, string DeliveryId,
        bool HasAuthorization, bool AuthorizationMatched, bool TelegramTokenMatched, bool WebhookTargetMatched, string Payload, string Response);
    private sealed class NotificationEndpoint : HttpMessageHandler
    {
        private int _webhookMode, _telegramMode, _cancellations;
        public ConcurrentQueue<HttpObservation> Requests { get; } = new();
        public int Cancellations => Volatile.Read(ref _cancellations);
        public ResponseMode WebhookMode { get => (ResponseMode)Volatile.Read(ref _webhookMode); set => Volatile.Write(ref _webhookMode, (int)value); }
        public ResponseMode TelegramMode { get => (ResponseMode)Volatile.Read(ref _telegramMode); set => Volatile.Write(ref _telegramMode, (int)value); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Fake HTTP request needs a URI.");
            bool telegram = uri.Host == "api.telegram.org";
            if (!(telegram && uri.AbsolutePath == "/bot" + TelegramSecret + "/sendMessage")
                && uri.AbsoluteUri != WebhookEndpoint)
                throw new InvalidOperationException("The isolated notification handler refuses every unrecognized destination; it has no network transport.");
            var mode = telegram ? TelegramMode : WebhookMode;
            string payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            bool hasAuthorization = request.Headers.TryGetValues("Authorization", out var values);
            bool authorization = hasAuthorization && values!.Single() == WebhookSecret;
            Requests.Enqueue(new(telegram ? "https://api.telegram.org/bot[redacted]/sendMessage" : WebhookDisplay,
                request.Method.Method, request.Content.Headers.ContentType?.MediaType,
                request.Headers.GetValues("X-TradingHub-Alert-Id").Single(), request.Headers.GetValues("Idempotency-Key").Single(),
                hasAuthorization, authorization, telegram && uri.AbsolutePath.Contains(TelegramSecret, StringComparison.Ordinal),
                !telegram && uri.AbsoluteUri == WebhookEndpoint, payload, mode.ToString()));
            if (mode == ResponseMode.Held)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Interlocked.Increment(ref _cancellations); throw; }
            }
            string body = telegram ? mode == ResponseMode.RejectedAcknowledgment
                ? "{\"ok\":false,\"error_code\":403,\"description\":\"private-response-detail " + TelegramSecret + "\"}"
                : "{\"ok\":true,\"result\":{\"message_id\":123}}" : "private-response-detail " + WebhookSecret;
            return new HttpResponseMessage(mode == ResponseMode.Unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(body) };
        }
    }
    private sealed class FixtureClock(DateTime initial)
    {
        private long _ticks = initial.Ticks;
        public DateTime Now => new(Interlocked.Read(ref _ticks), DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
    private sealed class FixtureSecrets(string path) : ISecretStore
    {
        private static readonly object Gate = new();
        public bool DropWrites { get; set; }
        public bool DropDeletes { get; set; }
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public string Get(string account)
        {
            lock (Gate)
            {
                if (FailReads) throw new IOException("Owned fixture credential reads are unavailable.");
                return Read().GetValueOrDefault(account, "");
            }
        }
        public void Set(string account, string? secret)
        {
            lock (Gate)
            {
                if (FailWrites) throw new IOException("Owned fixture credential writes are unavailable.");
                if (DropWrites && !string.IsNullOrEmpty(secret)) return;
                if (DropDeletes && string.IsNullOrEmpty(secret)) return;
                var values = Read();
                if (string.IsNullOrEmpty(secret)) values.Remove(account); else values[account] = secret;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(values));
            }
        }
        private Dictionary<string, string> Read() => File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))! : new();
    }

    private sealed class AsyncBarrierSecrets(FixtureSecrets inner) : IAsyncSecretStore, IDisposable
    {
        private int _hold, _cancelledReads, _timedOutReads, _synchronousReads;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsHolding => Volatile.Read(ref _hold) != 0;
        public int CancelledReads => Volatile.Read(ref _cancelledReads);
        public int TimedOutReads => Volatile.Read(ref _timedOutReads);
        public int SynchronousReads => Volatile.Read(ref _synchronousReads);
        public void HoldReads() => Volatile.Write(ref _hold, 1);
        public void ReleaseReads() { Volatile.Write(ref _hold, 0); _release.TrySetResult(); }
        public string Get(string account) { Interlocked.Increment(ref _synchronousReads); return inner.Get(account); }
        public void Set(string account, string? secret) => inner.Set(account, secret);
        public async Task<string> GetAsync(string account, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsHolding)
            {
                Entered.TrySetResult();
                try { await _release.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { Interlocked.Increment(ref _cancelledReads); throw; }
                catch (TimeoutException) { Interlocked.Increment(ref _timedOutReads); throw new IOException("Owned fake private-read safety deadline elapsed."); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return inner.Get(account);
        }
        public Task SetAsync(string account, string? secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inner.Set(account, secret);
            return Task.CompletedTask;
        }
        public void Dispose() => ReleaseReads();
    }

    private sealed record Assertion(string Description, bool Passed, string? Detail);
    private sealed class Proof(MainWindow owner, Action<double> pump, string outDir)
    {
        public MainWindow Owner => owner;
        public string OutDir => outDir;
        public int Failures { get; private set; }
        private readonly List<Assertion> _assertions = new();
        private readonly List<string> _captures = new();
        public List<ReplayResult> Replay { get; } = new();
        public List<object> StrategyLedgers { get; } = new();
        public List<AccountFill> NormalJournal { get; } = new();
        public List<NotificationSnapshot> Notifications { get; } = new();
        public List<object> EncryptedOutbox { get; } = new();
        public List<HttpObservation> Requests { get; } = new();
        public void Check(bool condition, string description, string? detail = null)
        {
            _assertions.Add(new(description, condition, detail == null ? null : Redact(detail)));
            if (!condition) Failures++;
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} KVP replay/alerts: {description}" + (detail == null ? "" : " · " + Redact(detail)));
        }
        public void Exercise(string name, Action action)
        {
            Directory.CreateDirectory(outDir);
            try { action(); }
            catch (Exception error) { Check(false, name + " completes through actual consumers", error.ToString()); }
        }
        public void Pump(double seconds) => pump(seconds);
        public void Until(Func<bool> condition, string description)
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (!condition() && DateTime.UtcNow < deadline) pump(0.02);
            bool ready = condition();
            Check(ready, description);
            if (!ready) throw new TimeoutException(description);
        }
        public void Await(Task task, string description)
        {
            Until(() => task.IsCompleted, description);
            task.GetAwaiter().GetResult();
            pump(0.02);
        }
        public T Await<T>(Task<T> task, string description) { Await((Task)task, description); return task.GetAwaiter().GetResult(); }
        public void Rejects<T>(Action action, string description) where T : Exception
        {
            try { action(); Check(false, description, "Expected " + typeof(T).Name + " was not raised."); }
            catch (T) { Check(true, description); }
        }
        public void PointerClick(Control control)
        {
            control.BringIntoView();
            pump(0.02);
            var top = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("Actual control has no visual top level.");
            var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)
                ?? throw new InvalidOperationException("Actual control has no pointer coordinates.");
            top.MouseDown(at, MouseButton.Left);
            top.MouseUp(at, MouseButton.Left);
        }
        public void Capture(Window window, string name)
        {
            window.Activate();
            pump(0.15);
            using var frame = window.CaptureRenderedFrame();
            Check(frame != null, "actual rendered PNG is available: " + name);
            if (frame == null) return;
            string path = Path.Combine(outDir, name + ".png");
            frame.Save(path);
            _captures.Add(path);
        }
        public void CaptureDelivery(AlertsWindow window, string id, string name)
        {
            pump(0.05);
            DeliveryRow(window, id).BringIntoView();
            Capture(window, name);
        }
        public void Save() => File.WriteAllText(Path.Combine(outDir, "kvp-replay-alerts-results.json"), Redact(JsonSerializer.Serialize(new {
            scene = "kvp-replay-alerts", checks = _assertions.Count, failures = Failures, assertions = _assertions, captures = _captures,
            practice = Replay, strategyLedgers = StrategyLedgers, normalSimulationJournal = NormalJournal, notifications = Notifications,
            encryptedOutbox = EncryptedOutbox, fakeHttpRequests = Requests,
            boundaries = new[] { "Actual Avalonia controls, chart strategies, run-owned accounts and normal simulation consumers.",
                "All HTTP is intercepted by a fixture-only HttpMessageHandler with no network transport; no provider, OAuth or model requests.",
                "Notification restart is an AlertCenter service restart with an independently retained fake secret store, not a process-crash or power-loss proof.",
                "HTTP/Telegram acknowledgment means endpoint acceptance only. Retries can duplicate and do not establish exactly-once delivery or human receipt.",
                "Blind practice is presentation concealment, not a security boundary. App.Services remains alive for the ordinary harness lifecycle." }
        }, new JsonSerializerOptions { WriteIndented = true })));
    }
}
