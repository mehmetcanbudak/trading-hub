// "safety" scene: dangerous confirmations default to Cancel (Enter cancels, ⌘Enter or a click confirms) and real-order
// permission is checked where orders are sent (live strategies, Chart Trader and the Account page).
// Fake accounts only: a local simulated account that reports itself as REAL, the fake Rithmic server under a non-practice
// system name and the fake Databento server. Nothing reaches a real broker, a paid service or personal data.
using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Tests;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

internal static class SafetyScene
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);

    /// <summary>A key press delivered to a dialog element: the window itself or a focused control inside it.</summary>
    private static void Press(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = target, Key = key, KeyModifiers = modifiers });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Source = target, Key = key, KeyModifiers = modifiers });
    }

    private static Button? ButtonOf(Window dialog, Func<string, bool> text) =>
        dialog.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content is string s && text(s));

    private static void Click(Button? button) => button?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0, checks = 0;
        void Check(bool ok, string message)
        {
            checks++;
            Console.WriteLine((ok ? "OK " : "FAIL ") + message);
            if (!ok) failures++;
        }

        void Part(string name, Action body)
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Check(false, $"{name} ran without an exception: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
            }
        }

        void Wait(Task task, double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted && DateTime.UtcNow < until) pump(0.02);
        }

        void Until(Func<bool> done, double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < until) pump(0.05);
        }

        // the dialog titled so while the task waits for it (null if the task finishes without one or none shows up in time)
        Window? Dialog(string title, Task task, double seconds = 3, bool keepLooking = false)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                pump(0.02);
                if (window.OwnedWindows.FirstOrDefault(w => w.Title == title && w.IsVisible) is { } found) return found;
                if (task.IsCompleted && !keepLooking) return null;
            }

            return null;
        }

        void Shot(Window target, string name)
        {
            pump(0.2);
            target.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
        }

        var svc = App.Services;
        Check(!Path.GetFullPath(AppPaths.Root).StartsWith(Path.GetFullPath(TradingHub.Common.Product.AppData), StringComparison.Ordinal), "uses isolated application data");
        Check(Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE") is { Length: > 0 }, "uses the fake keychain file");

        // ================================================================ S7: dangerous dialogs default to Cancel
        // answers a dialog opened by show (key or click) and returns its result (null: it never opened or did not close)
        bool? Answer(Func<Task<bool>> show, string title, Action<Window> respond)
        {
            var task = show();
            var dialog = Dialog(title, task);
            if (dialog == null) return null;
            respond(dialog);
            Wait(task, 5);
            if (task.IsCompleted) return task.Result;
            dialog.Close(false);
            Wait(task, 2);
            return null;
        }

        Part("danger dialog keys", () =>
        {
            const string title = "Safety fixture: danger";
            Task<bool> Show() => ConfirmDialog.ShowAsync(window, title, "Fixture only: nothing is deleted or sent.", "Delete", danger: true);
            var task = Show();
            var dialog = Dialog(title, task);
            Check(dialog != null, "danger dialog opens");
            if (dialog == null) return;
            Shot(dialog, "safety_dialog_danger");
            var cancel = ButtonOf(dialog, s => s == "Cancel");
            var action = ButtonOf(dialog, s => s == "Delete");
            Check(cancel?.IsDefault == true && action?.IsDefault == false, "danger dialog: Cancel is the default button");
            Check(action?.Classes.Contains("danger") == true, "danger dialog: the action keeps its danger styling");
            dialog.Close(false);
            Wait(task, 2);

            Check(Answer(Show, title, d => Press(d, Key.Enter)) == false, "danger dialog: Enter returns false (cancels)");
            Check(Answer(Show, title, d =>
            {
                var a = ButtonOf(d, s => s == "Delete");
                a?.Focus();
                if (a != null) Press(a, Key.Enter);
            }) == false, "danger dialog: Enter with the action button focused still cancels");
            Check(Answer(Show, title, d => Press(d, Key.Enter, KeyModifiers.Meta)) == true, "danger dialog: ⌘Enter returns true (confirms)");
            Check(Answer(Show, title, d => Press(d, Key.Escape)) == false, "danger dialog: Esc cancels");
            Check(Answer(Show, title, d => Click(ButtonOf(d, s => s == "Delete"))) == true, "danger dialog: clicking the action confirms");
        });

        Part("ordinary dialog keys", () =>
        {
            const string title = "Safety fixture: ordinary";
            Task<bool> Show() => ConfirmDialog.ShowAsync(window, title, "Fixture only: nothing is applied.", "Apply");
            var task = Show();
            var dialog = Dialog(title, task);
            Check(dialog != null, "ordinary dialog opens");
            if (dialog == null) return;
            var cancel = ButtonOf(dialog, s => s == "Cancel");
            var action = ButtonOf(dialog, s => s == "Apply");
            Check(action?.IsDefault == true && cancel?.IsDefault == false && cancel.IsCancel, "ordinary dialog unchanged: the action is the default button and Cancel the cancel button");
            Check(action?.Classes.Contains("primary") == true, "ordinary dialog unchanged: primary styling");
            dialog.Close(false);
            Wait(task, 2);
            Check(Answer(Show, title, d => Press(d, Key.Enter)) == true, "ordinary dialog unchanged: Enter returns true");
            Check(Answer(Show, title, d => Press(d, Key.Escape)) == false, "ordinary dialog unchanged: Esc returns false");
        });

        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        while (day.DayOfWeek == DayOfWeek.Saturday) day = day.AddDays(-1);

        Part("Delete data (danger call site)", () =>
        {
            window.Navigate("data");
            pump(0.3);
            var data = window.Page("data")!;
            var fixture = new Instrument { FeedId = CsvImport.FeedId, Symbol = "SAFETY", TickSize = 1, VolumeStep = 1 };
            string history = TickFile.DayPath(AppPaths.Ticks, fixture.FeedId, fixture.Symbol, day);
            // (re)writes the fixture day and selects it: each attempt starts from the same history file
            void Select()
            {
                HistoricalData.WriteDay(AppPaths.Ticks, fixture, day, new[] { new Trade(1, day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), 100, 1, TradeSide.Buy) }, replace: true);
                data.GetType().GetMethod("Refresh", BindingFlags.Public | Private)!.Invoke(data, null);
                var symbols = Field<ListBox>(data, "_symbols");
                symbols.SelectedItem = symbols.Items.OfType<ListBoxItem>().First(i => i.Tag is SymbolData s && s.FeedId == fixture.FeedId && s.Symbol == fixture.Symbol);
                pump(0.1);
                var days = Field<ListBox>(data, "_days");
                days.SelectedItem = days.Items[0];
                pump(0.1);
            }

            Select();
            var task = (Task)Call(data, "DeleteSelectedAsync")!;
            var dialog = Dialog("Delete data", task);
            Check(dialog != null, "Delete data opens its danger confirmation");
            if (dialog == null) return;
            Check(ButtonOf(dialog, s => s == "Cancel")?.IsDefault == true && ButtonOf(dialog, s => s == "Delete")?.IsDefault == false, "Delete data: Cancel is the default button");
            Press(dialog, Key.Enter);
            Wait(task, 5);
            if (!task.IsCompleted) dialog.Close(false);
            Wait(task, 2);
            Check(File.Exists(history), "Delete data: Enter keeps the history file");

            Select();
            task = (Task)Call(data, "DeleteSelectedAsync")!;
            dialog = Dialog("Delete data", task);
            if (dialog != null) Press(dialog, Key.Enter, KeyModifiers.Meta);
            Wait(task, 5);
            if (!task.IsCompleted) dialog?.Close(false);
            Check(dialog != null && !File.Exists(history), "Delete data: ⌘Enter confirms and deletes the fixture day");
        });

        Part("Databento paid download", () =>
        {
            using var db = FakeBrokers.Databento();
            string? previousDb = Environment.GetEnvironmentVariable("TRADINGHUB_DATABENTO_URL");
            Environment.SetEnvironmentVariable("TRADINGHUB_DATABENTO_URL", db.Http + "/v0");
            var profile = new ConnectionProfile { Id = "safety-databento", Kind = ConnectionKind.Databento, Name = "Fixture Databento" };
            svc.Connections.Profiles.Add(profile);
            svc.Connections.SetSecret(profile, "db-KEY");
            try
            {
                window.Navigate("data");
                pump(0.3);
                var data = window.Page("data")!;
                Field<CalendarDatePicker>(data, "_dbFrom").SelectedDate = day.ToDateTime(TimeOnly.MinValue);
                Field<CalendarDatePicker>(data, "_dbTo").SelectedDate = day.ToDateTime(TimeOnly.MinValue);
                Field<CheckBox>(data, "_dbAlsoRithmic").IsChecked = true;
                pump(0.1);
                string root = (string)data.GetType().GetProperty("DbRoot", Private)!.GetValue(data)!;
                void Quote()
                {
                    Set(data, "_dbQuote", 1.26m);
                    Set(data, "_dbQuoteKey", $"{root}|{day}|{day}|1");
                }

                int Charges()
                {
                    lock (db.Requests) return db.Requests.Count(r => r.Route == "/v0/timeseries.get_range");
                }

                Quote();
                var task = (Task)Call(data, "DatabentoDownloadAsync")!;
                var dialog = Dialog("Download Databento history", task);
                Check(dialog != null, "Databento download opens its paid-download confirmation");
                if (dialog == null) return;
                Shot(dialog, "safety_dialog_databento");
                var cancel = ButtonOf(dialog, s => s == "Cancel");
                var action = ButtonOf(dialog, s => s.StartsWith("Download (", StringComparison.Ordinal));
                Check(cancel?.IsDefault == true && action?.IsDefault == false, "paid Databento download: Cancel is the default button");
                Check(action?.Classes.Contains("primary") == true && !action.Classes.Contains("danger"), "paid Databento download keeps its ordinary (not danger) styling");
                Press(dialog, Key.Enter);
                Wait(task, 10);
                if (!task.IsCompleted) dialog.Close(false);
                Wait(task, 10);
                Check(Charges() == 0, "paid Databento download: Enter returns false (no data request, no charge)");

                int charges = Charges();
                // the same quote again (a download the Enter attempt might have made would mark the day complete)
                string downloaded = TickFile.DayPath(AppPaths.Ticks, HistorySources.Databento, root, day);
                if (File.Exists(downloaded)) File.Delete(downloaded);
                Quote();
                task = (Task)Call(data, "DatabentoDownloadAsync")!;
                dialog = Dialog("Download Databento history", task);
                if (dialog != null) Press(dialog, Key.Enter, KeyModifiers.Meta);
                Wait(task, 60);
                if (!task.IsCompleted) dialog?.Close(false);
                Check(dialog != null && Charges() > charges, "paid Databento download: ⌘Enter returns true (download requested from the fake server)");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TRADINGHUB_DATABENTO_URL", previousDb);
                svc.Connections.SetSecret(profile, null);
                svc.Connections.Profiles.Remove(profile);
            }
        });

        // ================================================================ S4: real-order permission where orders are sent
        window.Navigate("trading");
        pump(0.3);
        var tv = window.Trading;
        var chart = tv.ActiveTab!;
        var model = chart.Model!;
        var inst = model.Instrument;
        // the connection of the fake REAL account: an inert kind (no market data, no network) with real orders allowed
        var realProfile = new ConnectionProfile { Id = "safety-real", Kind = ConnectionKind.InteractiveBrokers, Name = "Fixture REAL", AllowRealOrders = true, Testnet = false };
        svc.Connections.Profiles.Add(realProfile);
        var inner = new SimBroker(model.Data, new Portfolio { AccountID = "SAFETY-REAL", Name = "Fixture REAL", BeginBalance = 10_000m, Balance = 10_000m }, new TradingStatistics()) { Name = "Fixture REAL" };
        var real = new FixtureRealAccount(inner);
        var cache = Field<Dictionary<string, ITradingAccount>>(svc, "_realBrokers");
        string cacheKey = "safety-real|" + model.Data.Instrument.Key;
        cache[cacheKey] = real; // what AccountFor returns from its cache for this account and instrument
        string Status() => Field<TextBlock>(window, "_statusText").Text ?? "";
        decimal Pos() => real.Position.Volume;

        // a Chart Trader action: its «Confirm REAL order» (if any) is confirmed with ⌘Enter or canceled with Cancel; true if it asked
        bool Act(Func<Task> start, bool confirm, bool hotkey = false)
        {
            var task = start();
            var dialog = Dialog("Confirm REAL order", task, hotkey ? 1 : 3, keepLooking: hotkey);
            if (dialog != null)
            {
                if (confirm) Press(dialog, Key.Enter, KeyModifiers.Meta);
                else Click(ButtonOf(dialog, s => s == "Cancel"));
                Wait(task, 5);
                if (dialog.IsVisible) dialog.Close(false);
            }

            Wait(task, 10);
            pump(0.1);
            return dialog != null;
        }

        Task Hotkey(string id)
        {
            tv.ExecuteHotkey(id, chart);
            return Task.CompletedTask;
        }

        Task Move(Order order, decimal price) => (Task)Call(tv, "MoveOrderAsync", chart, order, price)!;

        Part("Chart Trader on a REAL account after revocation", () =>
        {
            tv.SetChartAccount(chart, "safety-real");
            pump(0.3);
            Check(ReferenceEquals(tv.AccountOf(chart), real) && real.IsReal, "the chart routes to the cached fake REAL account (AccountFor cache)");
            decimal last = model.Data.LastPrice;
            Check(last > 0, $"the simulated market has a price ({last})");
            Check(Act(() => tv.TradeAsync(chart, OrderTypes.Market, OrderDirections.Buy, 0, 2), confirm: true) && Pos() == 2,
                $"allowed: a REAL buy asks for confirmation and fills (position {Pos()})");
            decimal entryPrice = inst.RoundPrice(last * 0.7m), stopPrice = inst.RoundPrice(last * 0.8m);
            Act(() => tv.TradeAsync(chart, OrderTypes.Limit, OrderDirections.Buy, entryPrice, 1), confirm: true);
            Act(() => tv.TradeAsync(chart, OrderTypes.Stop, OrderDirections.Sell, stopPrice, 1), confirm: true);
            var entry = real.ActiveOrders.FirstOrDefault(o => o.Type == OrderTypes.Limit && o.Direction == OrderDirections.Buy);
            var stop = real.ActiveOrders.FirstOrDefault(o => o.Type == OrderTypes.Stop && o.Direction == OrderDirections.Sell);
            Check(entry != null && stop != null, "allowed: a working entry limit and a protective sell stop are placed");

            realProfile.AllowRealOrders = false; // revoked while the tab keeps its cached REAL account
            bool asked = Act(() => tv.TradeAsync(chart, OrderTypes.Market, OrderDirections.Buy, 0, 1), confirm: false);
            Check(!asked && Pos() == 2 && Status().Contains("new orders are disabled", StringComparison.Ordinal),
                $"revoked: a buy that adds exposure is blocked before any confirmation (asked {asked}, position {Pos()}, status «{Status()}»)");
            asked = Act(() => tv.TradeAsync(chart, OrderTypes.Market, OrderDirections.Sell, 0, 3), confirm: false);
            Check(!asked && Pos() == 2, $"revoked: a sell that reverses through flat is blocked (asked {asked}, position {Pos()})");
            asked = Act(() => Hotkey("reverse"), confirm: false, hotkey: true);
            Check(!asked && Pos() == 2, $"revoked: Reverse is blocked (asked {asked}, position {Pos()})");
            if (entry != null)
            {
                asked = Act(() => Move(entry, inst.RoundPrice(last * 0.71m)), confirm: false);
                Check(!asked && entry.Price == entryPrice, $"revoked: moving a working entry order is blocked (asked {asked}, price {entry.Price})");
            }

            asked = Act(() => tv.TradeAsync(chart, OrderTypes.Market, OrderDirections.Sell, 0, 1), confirm: true);
            Check(asked && Pos() == 1, $"revoked: a reduction is still sent, behind the REAL confirmation (asked {asked}, position {Pos()})");
            if (stop != null)
            {
                decimal moved = inst.RoundPrice(last * 0.81m);
                asked = Act(() => Move(stop, moved), confirm: true);
                Check(asked && stop.TriggerPrice == moved, $"revoked: moving a protective stop is still allowed (asked {asked}, trigger {stop.TriggerPrice})");
            }

            asked = Act(() => Hotkey("cancel-all"), confirm: true, hotkey: true);
            Check(asked && !real.ActiveOrders.Any(), $"revoked: Cancel all is still allowed (asked {asked}, active {real.ActiveOrders.Count()})");
            asked = Act(() => Hotkey("flatten"), confirm: true, hotkey: true);
            Check(asked && Pos() == 0, $"revoked: closing the position is still allowed (asked {asked}, position {Pos()})");

            realProfile.AllowRealOrders = true;
            realProfile.Enabled = false;
            asked = Act(() => tv.TradeAsync(chart, OrderTypes.Market, OrderDirections.Buy, 0, 1), confirm: false);
            Check(!asked && Pos() == 0 && Status().Contains("is disabled", StringComparison.Ordinal), $"disabled connection: a new buy is blocked (asked {asked}, status «{Status()}»)");
            realProfile.Enabled = true;
            svc.Connections.Profiles.Remove(realProfile);
            asked = Act(() => tv.TradeAsync(chart, OrderTypes.Market, OrderDirections.Buy, 0, 1), confirm: false);
            Check(!asked && Pos() == 0 && Status().Contains("no longer exists", StringComparison.Ordinal), $"removed connection: a new buy is blocked (asked {asked}, status «{Status()}»)");
            svc.Connections.Profiles.Add(realProfile);
            Shot(window, "safety_chart_trader_blocked");

            tv.SetChartAccount(chart, "sim");
            pump(0.2);
            var sim = tv.AccountOf(chart)!;
            decimal before = sim.Position.Volume;
            asked = Act(() => tv.TradeAsync(chart, OrderTypes.Market, OrderDirections.Buy, 0, 1), confirm: false);
            Check(!sim.IsReal && !asked && sim.Position.Volume == before + 1, "the simulated account is unaffected: no dialog and the order fills");
            Act(() => Hotkey("flatten"), confirm: false, hotkey: true);
            Check(sim.Position.Volume == 0, "the simulated position closes without a dialog");
        });

        Part("live strategy guards", () =>
        {
            realProfile.AllowRealOrders = true;
            realProfile.Enabled = true;
            if (svc.Strategies.Files.Count == 0) svc.Strategies.Refresh();
            var file = svc.Strategies.Files.First();
            LiveStrategyRunner? Launch(string accountId)
            {
                var launch = svc.Live.LaunchAsync(new LiveLaunch { File = file, ProfileId = "sim", Instrument = inst, AccountId = accountId, Period = ChartPeriod.H1 });
                Wait(launch, 120);
                if (!launch.IsCompletedSuccessfully) return null;
                if (launch.Result.Runner == null) Console.WriteLine("  launch: " + launch.Result.Message);
                return launch.Result.Runner;
            }

            async Task Delete(LiveStrategyRunner r)
            {
                if (svc.Live.Entries().FirstOrDefault(e => e.Key == r.Key) is { } e) await svc.Live.DeleteAsync(e, history: true);
            }

            var runner = Launch("safety-real");
            Check(runner != null && ReferenceEquals(runner.Account, real), "a panel strategy launches on the cached fake REAL account");
            if (runner == null) return;
            Check(runner.Guard != null, "launched panel strategy has the production real-order guard");
            Check(runner.Guard != null && runner.Guard.Check(runner, OrderSide.Buy, 1, 1) == null, "allowed: its guard lets an entry through");
            realProfile.AllowRealOrders = false;
            string? why = runner.Guard?.Check(runner, OrderSide.Buy, 1, 1);
            Check(why?.Contains("not allowed", StringComparison.Ordinal) == true, $"revoked: its guard blocks an entry that adds exposure («{why}»)");
            Check(runner.Guard != null && runner.Guard.Check(runner, OrderSide.Sell, 1, 0) == null, "revoked: its guard never blocks a reduction");
            realProfile.AllowRealOrders = true;
            realProfile.Enabled = false;
            why = runner.Guard?.Check(runner, OrderSide.Sell, 1, -1);
            Check(why?.Contains("is disabled", StringComparison.Ordinal) == true, $"disabled connection: its guard blocks a short entry («{why}»)");
            realProfile.Enabled = true;
            Check(runner.Guard != null && runner.Guard.Check(runner, OrderSide.Buy, 1, 1) == null, "permission restored: entries pass again");

            // restart with parameters (same path as resume and auto-resume: LaunchFromInfoAsync → LaunchAsync)
            if (svc.Live.Entries().FirstOrDefault(e => e.Key == runner.Key) is { } entry)
            {
                var restart = svc.Live.RestartWithParametersAsync(entry, runner.StrategyInstance.GetParameters());
                Wait(restart, 120);
                var restarted = svc.Live.Running(runner.Key);
                Check(restart.IsCompletedSuccessfully && restart.Result == null && restarted != null && !ReferenceEquals(restarted, runner), $"restart with parameters relaunches the strategy ({(restart.IsCompletedSuccessfully ? restart.Result : restart.Exception?.GetBaseException().Message)})");
                realProfile.AllowRealOrders = false;
                Check(restarted is { Guard: { } restartedGuard } && restartedGuard.Check(restarted, OrderSide.Buy, 1, 1) != null, "the restarted strategy also has the guard and it blocks after revocation");
                realProfile.AllowRealOrders = true;
                if (restarted != null) Wait(Delete(restarted), 10);
            }

            Wait(Delete(runner), 10);
            var simRunner = Launch("sim");
            realProfile.AllowRealOrders = false;
            Check(simRunner?.Guard != null && simRunner.Guard.Check(simRunner, OrderSide.Buy, 1, 1) == null, "a simulated panel strategy has the guard and is never blocked");
            realProfile.AllowRealOrders = true;
            if (simRunner != null) Wait(Delete(simRunner), 10);
        });

        Part("chart strategy guard", () =>
        {
            realProfile.AllowRealOrders = true;
            var file = svc.Strategies.Files.First();
            tv.SetChartAccount(chart, "sim");
            Wait(tv.AddStrategyAsync(chart, file, openSettings: false), 120);
            var cs = chart.Strategies.LastOrDefault();
            Check(cs != null, "a strategy is added to the chart (visual)");
            if (cs == null) return;
            cs.Live = true;
            cs.AccountId = "safety-real";
            var run = tv.RunStrategyAsync(chart, cs);
            var dialog = Dialog("Execute on REAL account", run, 120);
            Check(dialog != null, "executing a chart strategy on a REAL account asks first");
            if (dialog != null) Press(dialog, Key.Enter, KeyModifiers.Meta);
            Wait(run, 60);
            var runner = cs.Runner;
            Check(runner != null && ReferenceEquals(runner.Account, real) && runner.Guard != null, "the chart strategy executing on the fake REAL account has the production real-order guard");
            realProfile.AllowRealOrders = false;
            string? why = runner is { Guard: { } chartGuard } ? chartGuard.Check(runner, OrderSide.Sell, 1, -1) : null;
            Check(why?.Contains("not allowed", StringComparison.Ordinal) == true, $"revoked: the chart strategy's guard blocks a short entry («{why}»)");
            Check(runner?.Guard != null && runner.Guard.Check(runner, OrderSide.Buy, 1, 0) == null, "revoked: the chart strategy's guard never blocks a reduction");
            realProfile.AllowRealOrders = true;
            tv.RemoveStrategy(chart, cs);
            pump(0.2);
        });

        Part("Account page on a REAL account with real orders disabled", () =>
        {
            var rp = svc.Connections.Profiles.FirstOrDefault(p => p.IsRithmic && p.Enabled && p.RithmicUser == "demo");
            Check(rp != null, "the fixture has its fake Rithmic profile");
            if (rp == null) return;
            string oldSystem = rp.RithmicSystem;
            bool oldAllow = rp.AllowRealOrders;
            var session = svc.RithmicSessionFor(rp);
            try
            {
                // the same fake server under a non-practice system name: Trading Hub treats the account as REAL
                rp.RithmicSystem = "Rithmic 01";
                rp.AllowRealOrders = false;
                session.Reconfigure(svc.Connections.RithmicSettingsOf(rp));
                Until(() => session.IsConnected && session.Accounts.Count > 0, 30);
                Check(session.IsConnected && !session.IsPractice, $"fake Rithmic account reports a REAL system ({session.StatusText})");
                string acct = session.Accounts.FirstOrDefault()?.AccountId ?? "ACC1";
                // activity from elsewhere (placed straight through the session): a filled position and a working order
                Wait(session.PlaceAsync(acct, "ESZ6", "CME", true, 2, 2, 0, 0, "safety-fill", CancellationToken.None), 15);
                Wait(session.PlaceAsync(acct, "ESZ6", "CME", true, 1, 1, 4000m, 0, "safety-work", CancellationToken.None), 15);
                Until(() => session.PositionsOf(acct).Any(p => p.Position.Net == 2) && session.OrdersOf(acct).Any(o => !o.Done), 15);
                Check(session.PositionsOf(acct).Any(p => p.Position.Net == 2) && session.OrdersOf(acct).Any(o => !o.Done), "fake REAL account has a position and a working order");
                string id = AppServices.RithmicAccountId(rp, acct);
                var account = svc.AccountCenter.Accounts().FirstOrDefault(a => a.Id == id);
                Check(account is { IsReal: true, CanTrade: false }, "Account lists it as REAL with real orders disabled");

                window.Navigate("accounts");
                pump(0.5);
                var page = window.Page("accounts")!;
                Set(page, "_selected", id);
                Call(page, "Refresh");
                pump(1);
                Call(page, "Refresh");
                pump(0.3);
                string status = Field<TextBlock>(page, "_accountStatus").Text ?? "";
                Check(status.Contains("REAL ACCOUNT", StringComparison.Ordinal) && status.Contains("cancel/close allowed", StringComparison.Ordinal),
                    $"Account status says new orders are disabled but cancel/close are allowed («{status}»)");
                Button? RowButton(string rows, string button, Func<object?, bool> wanted) =>
                    Field<IDictionary>(page, rows).Values.Cast<object>().Where(r => wanted(r.GetType().GetField("Line")!.GetValue(r)))
                        .Select(r => r.GetType().GetField(button)!.GetValue(r) as Button).FirstOrDefault();
                var close = RowButton("_positionRows", "Close", l => l is PositionLine { Net: not 0 });
                var cancel = RowButton("_orderRows", "Cancel", l => l is OrderLine { Active: true });
                Check(close is { IsVisible: true, Flyout: Flyout }, "Close is available on the REAL position, behind its confirmation");
                Check(cancel is { IsVisible: true, Flyout: Flyout }, "Cancel is available on the working REAL order, behind its confirmation");
                Shot(window, "safety_account_real_orders_disabled");

                // confirm through the confirmation the page shows (its action button)
                static void Confirm(Button? b)
                {
                    if (b?.Flyout is Flyout { Content: StackPanel body } && body.Children.OfType<StackPanel>().LastOrDefault() is { } bar && bar.Children.Count > 1)
                        Click(bar.Children[1] as Button);
                }

                Confirm(cancel);
                Until(() => !session.OrdersOf(acct).Any(o => !o.Done), 15);
                Check(!session.OrdersOf(acct).Any(o => !o.Done), "confirmed Cancel cancels the working REAL order with real orders disabled");
                Confirm(close);
                Until(() => session.PositionsOf(acct).All(p => p.Position.Net == 0), 15);
                Check(session.PositionsOf(acct).All(p => p.Position.Net == 0), "confirmed Close flattens the REAL position with real orders disabled");
            }
            finally
            {
                rp.RithmicSystem = oldSystem;
                rp.AllowRealOrders = oldAllow;
                session.Reconfigure(svc.Connections.RithmicSettingsOf(rp));
                window.Navigate("trading");
                pump(0.3);
            }
        });

        cache.Remove(cacheKey);
        svc.Connections.Profiles.Remove(realProfile);
        Console.WriteLine($"Safety scenario: {checks} checks, {failures} failures.");
        return failures;
    }
}

/// <summary>A local simulated account that reports itself as REAL: its orders fill in memory and never leave the process.</summary>
internal sealed class FixtureRealAccount(SimBroker inner) : ITradingAccount
{
    public string Name => "Fixture REAL";
    public bool IsReal => true;
    public Instrument Instrument => inner.Instrument;
    public Position Position => ((ITradingAccount)inner).Position;
    public IEnumerable<Order> ActiveOrders => inner.ActiveOrders;
    public string? LastError => inner.LastError;
    public decimal LastPrice => inner.LastPrice;

    public event Action? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public Order Market(OrderDirections dir, decimal qty, string comment = "") => inner.Market(dir, qty, comment);
    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => inner.Limit(dir, qty, price, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => inner.Stop(dir, qty, trigger, comment);
    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") => inner.Exit(type, dir, qty, price, comment);
    public Order Replace(Order order, decimal newPrice, decimal newQuantity) => inner.Replace(order, newPrice, newQuantity);
    public void Cancel(Order order) => inner.Cancel(order);
    public void CancelAt(decimal price) => inner.CancelAt(price);
    public void CancelAll() => inner.CancelAll();
    public void Modify(Order order, decimal newPrice) => inner.Modify(order, newPrice);
    public void Flatten() => inner.Flatten();
    public void Reverse() => inner.Reverse();
}
