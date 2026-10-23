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
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Tests;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

internal static partial class SafetyScene
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
                // moving an order that is already working sends nothing new: allowed, behind the REAL confirmation
                decimal movedEntry = inst.RoundPrice(last * 0.71m);
                asked = Act(() => Move(entry, movedEntry), confirm: true);
                Check(asked && entry.Price == movedEntry && Pos() == 2, $"revoked: moving a working entry order is still allowed, behind the REAL confirmation (asked {asked}, price {entry.Price}, position {Pos()})");
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

        Part("cached real brokers follow connection changes", () =>
        {
            // a fake Binance REST server for an idle account (no position, no open orders) that refuses anything else, such
            // as an order, so nothing can fill; TRADINGHUB_BINANCE_REST points the account there, never at Binance
            using var binance = new FakeHttpServer();
            binance.OnHttp = r => r.Route switch
            {
                "/fapi/v1/time" => (200, $"{{\"serverTime\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}"),
                "/fapi/v1/openOrders" or "/fapi/v2/positionRisk" => (200, "[]"),
                _ => (400, "{\"code\":-1000,\"msg\":\"safety fixture: orders are refused\"}"),
            };
            string? previousRest = Environment.GetEnvironmentVariable("TRADINGHUB_BINANCE_REST");
            Environment.SetEnvironmentVariable("TRADINGHUB_BINANCE_REST", binance.Http);
            var bp = new ConnectionProfile { Id = "safety-binance", Kind = ConnectionKind.BinanceFutures, Name = "Fixture Binance", Testnet = false, AllowRealOrders = true, ApiKey = "fixture-key-1" };
            svc.Connections.Profiles.Add(bp);
            svc.Connections.SetSecret(bp, "fixture-secret");
            LiveStrategyRunner? runner = null;
            async Task DeleteRunner(LiveStrategyRunner r)
            {
                if (svc.Live.Entries().FirstOrDefault(e => e.Key == r.Key) is { } e) await svc.Live.DeleteAsync(e, history: true);
            }

            try
            {
                bool fake = svc.Connections.BinanceSettingsOf(bp).RestBase == binance.Http;
                Check(fake, "the Binance fixture account points at the local fake server");
                if (!fake) return;
                int Polls(string key)
                {
                    lock (binance.Requests) return binance.Requests.Count(r => r.Headers.GetValueOrDefault("X-MBX-APIKEY") == key);
                }

                static bool Disposed(ITradingAccount broker) => broker is BinanceBroker && Field<CancellationTokenSource>(broker, "_cts").IsCancellationRequested;

                tv.SetChartAccount(chart, bp.Id);
                pump(0.3);
                var first = tv.AccountOf(chart)!;
                Until(() => Polls("fixture-key-1") > 0, 10);
                Check(first is BinanceBroker { IsReal: true } && ReferenceEquals(chart.Model!.Account, first) && Polls("fixture-key-1") > 0,
                    $"the chart trades the fake REAL Binance account, which polls the fake server ({first.Name})");

                // a live strategy runs on the same account (paused: it opens nothing)
                if (svc.Strategies.Files.Count == 0) svc.Strategies.Refresh();
                var launch = svc.Live.LaunchAsync(new LiveLaunch { File = svc.Strategies.Files.First(), ProfileId = "sim", Instrument = inst, AccountId = bp.Id, Period = ChartPeriod.H1, Paused = true });
                Wait(launch, 120);
                runner = launch.IsCompletedSuccessfully ? launch.Result.Runner : null;
                Check(runner != null && ReferenceEquals(runner.Account, first),
                    $"a paused live strategy runs on the same broker ({(launch.IsCompletedSuccessfully ? launch.Result.Message : launch.Exception?.GetBaseException().Message)})");

                // the connection is saved as testnet (the Connections page saves, then invalidates the connection)
                bp.Testnet = true;
                svc.Connections.Invalidate(bp);
                pump(0.3);
                var second = svc.AccountFor(model.Data, bp.Id);
                Check(!ReferenceEquals(second, first) && !second.IsReal && second.Name.Contains("testnet", StringComparison.Ordinal),
                    $"switching the connection to testnet replaces the cached REAL broker ({second.Name}, REAL {second.IsReal})");
                Check(ReferenceEquals(chart.Model!.Account, second) && ReferenceEquals(tv.AccountOf(chart), second), "the chart now shows and routes to the testnet account");
                string? why = svc.CheckRealOrder(bp.Id, first, 0, 1);
                Check(why?.Contains("opened as REAL", StringComparison.Ordinal) == true, $"the replaced REAL broker can no longer add exposure («{why}»)");
                Check(svc.CheckRealOrder(bp.Id, first, 1, -1) == null && svc.CheckRealOrder(bp.Id, first, 1, 0) == null, "the replaced REAL broker can still reduce and cancel");
                if (runner != null)
                {
                    why = runner.Guard?.Check(runner, OrderSide.Buy, 1, 1);
                    Check(why?.Contains("opened as REAL", StringComparison.Ordinal) == true && runner.Guard!.Check(runner, OrderSide.Sell, 1, 0) == null,
                        $"its strategy keeps the REAL broker but can only reduce or cancel through it («{why}»)");
                    Check(!Disposed(first), "the replaced broker keeps working while the strategy runs on it");
                    Wait(DeleteRunner(runner), 10);
                    runner = null;
                    svc.AccountFor(model.Data, bp.Id); // the next use disposes the replaced brokers nothing works through
                    Check(Disposed(first), "once the strategy is gone, the replaced REAL broker is disposed");
                }

                bp.MaxOrderQuantity = 5;
                svc.Connections.Invalidate(bp);
                Check(ReferenceEquals(svc.AccountFor(model.Data, bp.Id), second), "a change that keeps mode, key and server keeps the cached broker");

                bp.ApiKey = "fixture-key-2";
                svc.Connections.Invalidate(bp);
                var third = svc.AccountFor(model.Data, bp.Id);
                Until(() => Polls("fixture-key-2") > 0, 10);
                Check(!ReferenceEquals(third, second) && Polls("fixture-key-2") > 0 && Disposed(second),
                    "a new API key replaces the broker: the new one signs with the new key and the old, unused one is disposed");
                why = svc.CheckRealOrder(bp.Id, second, 0, 1);
                Check(why?.Contains("changed its keys", StringComparison.Ordinal) == true, $"a broker replaced for another API key can no longer add exposure («{why}»)");
            }
            finally
            {
                if (runner != null) Wait(DeleteRunner(runner), 10);
                tv.SetChartAccount(chart, "sim");
                foreach (var key in cache.Keys.Where(k => k.StartsWith(bp.Id + "|", StringComparison.Ordinal)).ToList())
                {
                    (cache[key] as IDisposable)?.Dispose();
                    cache.Remove(key);
                }

                svc.Connections.SetSecret(bp, null);
                svc.Connections.Profiles.Remove(bp);
                Environment.SetEnvironmentVariable("TRADINGHUB_BINANCE_REST", previousRest);
                lock (binance.Requests)
                    Console.WriteLine("  fake Binance requests: " + string.Join(", ", binance.Requests.GroupBy(r => r.Method + " " + r.Route).Select(g => $"{g.Key} ×{g.Count()}")));
                pump(0.2);
            }
        });

        Part("Account page on a REAL account with real orders disabled", () =>
        {
            var rp = svc.Connections.Profiles.FirstOrDefault(p => p.IsRithmic && p.Enabled && p.RithmicUser == "demo");
            Check(rp != null, "the fixture has its fake Rithmic profile");
            if (rp == null) return;
            string oldSystem = rp.RithmicSystem;
            bool oldAllow = rp.AllowRealOrders;
            var session = svc.RithmicSessionFor(rp);
            // signed in again after Reconfigure: each sign-in sets a new account list, so the session still connected with the
            // old settings for a moment never counts
            bool SignedInAgain(object before) => session.IsConnected && !ReferenceEquals(session.Accounts, before) && session.Accounts.Count > 0;
            Until(() => session.IsConnected && session.Accounts.Count > 0, 30);
            string? brokerKey = null; // the cache entry of the broker this part builds
            try
            {
                string acct = session.Accounts.FirstOrDefault()?.AccountId ?? "ACC1";
                string id = AppServices.RithmicAccountId(rp, acct);
                // activity from elsewhere (placed straight through the session) while the account is still practice: a filled
                // position and a working order. Earlier scenes can leave a position or orders on the same fake account, so the
                // checks start from the position found here and follow this scene's own working order by its tag.
                string tag = "safety-" + Guid.NewGuid().ToString("N")[..8];
                decimal Net() => session.PositionFor(acct, "ESZ6", "CME")?.Net ?? 0;
                RithmicOrderState? Work() => session.OrderByTag(tag + "-work");
                decimal startNet = Net();
                bool Placed() => Net() == startNet + 2 && Work() is { Done: false, BasketId.Length: > 0 };
                Wait(session.PlaceAsync(acct, "ESZ6", "CME", true, 2, 2, 0, 0, tag + "-fill", CancellationToken.None), 15);
                Wait(session.PlaceAsync(acct, "ESZ6", "CME", true, 1, 1, 4000m, 0, tag + "-work", CancellationToken.None), 15);
                Until(Placed, 15);

                window.Navigate("accounts");
                pump(0.5);
                var page = window.Page("accounts")!;
                void Show()
                {
                    Set(page, "_selected", id);
                    Call(page, "Refresh");
                    pump(1);
                    Call(page, "Refresh");
                    pump(0.3);
                }

                Button? RowButton(string rows, string button, Func<object?, bool> wanted) =>
                    Field<IDictionary>(page, rows).Values.Cast<object>().Where(r => wanted(r.GetType().GetField("Line")!.GetValue(r)))
                        .Select(r => r.GetType().GetField(button)!.GetValue(r) as Button).FirstOrDefault();
                // this scene's position row and its own working order (rows from earlier activity may also be listed)
                Button? CloseButton() => RowButton("_positionRows", "Close", l => l is PositionLine { Key: "ESZ6|CME", Net: not 0 });
                Button? CancelButton() => RowButton("_orderRows", "Cancel", l => l is OrderLine { Active: true } o && o.Key == Work()?.BasketId);
                // the Account page shows the account while it is practice: its Close and Cancel need no REAL confirmation
                Show();
                Check(CloseButton() is { IsVisible: true, Flyout: null } && CancelButton() is { IsVisible: true, Flyout: null },
                    "while the account is practice, Account shows its Close and Cancel without the REAL confirmation");

                // a cached broker for the account while it is practice (as Chart Trader builds one)
                var esData = new InstrumentData(new Instrument { FeedId = rp.MarketDataId, Symbol = "ESZ6", TickSize = 0.25m, PointValue = 50 }, svc.Connections.GetFeed(rp.Id));
                brokerKey = id + "|" + esData.Instrument.Key;
                var practiceBroker = svc.AccountFor(esData, id);
                Check(!practiceBroker.IsReal, $"the account's cached broker is practice ({practiceBroker.Name})");

                // then its connection is saved with a REAL system (the Connections page saves, then invalidates the connection):
                // the same fake server under a non-practice system name
                rp.RithmicSystem = "Rithmic 01";
                rp.AllowRealOrders = false;
                var practiceLogin = session.Accounts;
                svc.Connections.Invalidate(rp);
                Until(() => SignedInAgain(practiceLogin), 30);
                Check(SignedInAgain(practiceLogin) && !session.IsPractice, $"fake Rithmic account reports a REAL system ({session.StatusText})");
                Until(Placed, 15);
                Check(Placed(), $"fake REAL account has a position and a working order (net {startNet} → {Net()}, working order {Work()?.BasketId})");
                var account = svc.AccountCenter.Accounts().FirstOrDefault(a => a.Id == id);
                Check(account is { IsReal: true, CanTrade: false }, "Account lists it as REAL with real orders disabled");
                var realBroker = svc.AccountFor(esData, id);
                Check(!ReferenceEquals(realBroker, practiceBroker) && realBroker.IsReal && realBroker.Name.Contains("REAL", StringComparison.Ordinal),
                    $"the system change replaces the cached practice broker with a REAL one ({realBroker.Name})");
                // the replaced broker was built for practice (nothing that uses it confirmed REAL): even with real orders allowed it
                // adds no exposure, while reductions and cancels still pass
                rp.AllowRealOrders = true;
                string? why = svc.CheckRealOrder(id, practiceBroker, 0, 1);
                bool reducesAndCancels = svc.CheckRealOrder(id, practiceBroker, 1, -1) == null && svc.CheckRealOrder(id, practiceBroker, 1, 0) == null;
                rp.AllowRealOrders = false;
                Check(why?.Contains("opened as practice", StringComparison.Ordinal) == true && reducesAndCancels,
                    $"the replaced practice broker cannot add REAL exposure, even with real orders allowed («{why}»)");

                // the page still shows the same account, now REAL: its rows must ask for the REAL confirmation
                Show();
                string status = Field<TextBlock>(page, "_accountStatus").Text ?? "";
                Check(status.Contains("REAL ACCOUNT", StringComparison.Ordinal) && status.Contains("cancel/close allowed", StringComparison.Ordinal),
                    $"Account status says new orders are disabled but cancel/close are allowed («{status}»)");
                var close = CloseButton();
                var cancel = CancelButton();
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
                // the fake server acknowledges over its order plant; under heavy machine load that has taken longer than 15 s
                Until(() => Work() is { Done: true }, 45);
                var worked = Work();
                Check(worked is { Done: true, Cancelled: true },
                    $"confirmed Cancel cancels the working REAL order with real orders disabled (done {worked?.Done}, cancelled {worked?.Cancelled})");
                var positionRow = Field<IDictionary>(page, "_positionRows")["ESZ6|CME"];
                var openLine = positionRow?.GetType().GetField("Line")!.GetValue(positionRow);
                Confirm(close);
                Until(() => Net() == 0, 45);
                Check(Net() == 0, $"confirmed Close flattens the REAL position with real orders disabled (net {Net()})");

                // the same Close confirmed on its row as it was a moment before (the position is already flat): nothing is sent,
                // and the status says so instead of staying on «Closing position at market…». The close order's own completion
                // may arrive after the flat position: wait for it, or that Close would try to cancel it.
                Until(() => !session.OrdersOf(acct).Any(o => !o.Done), 15);
                bool sentNothing = false;
                if (positionRow != null && openLine is PositionLine { Net: not 0 })
                {
                    positionRow.GetType().GetField("Line")!.SetValue(positionRow, openLine);
                    int orders = session.OrdersOf(acct).Count;
                    Confirm(close);
                    Until(() => Status().Contains("nothing needed closing", StringComparison.Ordinal), 10);
                    sentNothing = session.OrdersOf(acct).Count == orders && Net() == 0;
                }

                Check(sentNothing && Status().Contains("nothing needed closing", StringComparison.Ordinal),
                    $"Close on a position that is already flat sends nothing and says so («{Status()}»)");
            }
            finally
            {
                rp.RithmicSystem = oldSystem;
                rp.AllowRealOrders = oldAllow;
                var realLogin = session.Accounts;
                svc.Connections.Invalidate(rp);
                Until(() => SignedInAgain(realLogin), 30); // back on the practice system before anything else uses the session
                if (brokerKey != null && cache.Remove(brokerKey, out var leftover)) (leftover as IDisposable)?.Dispose();
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

// ======================================================================== S1: one Quit path
// The menu bar's «Quit Trading Hub» and ⌘Q (the platform's shutdown request) take the same path: while live strategies
// hold positions it first asks what to do with their orders, then saves and stops them that way and exits once.
// Independent of earlier scenes: other scenes' strategies are set aside while it runs; its own strategy (it never trades
// by itself), its own simulated accounts on SIM-ES, ledger keys and order comments; and a desktop lifetime that records
// the app's exit instead of ending this process.
internal static partial class SafetyScene
{
    private const string QuitStrategy = "SafetyQuitFixture";
    private const string QuitAccountId = "safety-quit";
    private const string QuitTitle = "Quit Trading Hub";
    private const string NotClosedTitle = "Positions not closed";
    private const string KeepText = "Keep stops at broker and quit";
    private const string CancelStopsText = "Cancel stops and quit";
    private const string CloseText = "Close positions and quit";
    private const string StayText = "Don't quit";

    public static int RunQuit(MainWindow window, Action<double> pump, string outDir)
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

        // a visible window owned by the main window with that title (null if none shows up in time)
        Window? Find(string title, double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            do
            {
                pump(0.02);
                if (window.OwnedWindows.FirstOrDefault(w => w.Title == title && w.IsVisible) is { } found) return found;
            }
            while (DateTime.UtcNow < until);

            return null;
        }

        void Shot(Window target, string name)
        {
            pump(0.2);
            target.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
        }

        // the strategies a dialog lists, and all its text
        List<string> Listed(Window dialog) =>
            dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.StartsWith("• ", StringComparison.Ordinal)).ToList();
        string Text(Window dialog) => string.Join(" ", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        var svc = App.Services;
        var tv = window.Trading;
        var app = Avalonia.Application.Current!;
        // what Avalonia 11.3.22 keeps as Application.ApplicationLifetime (its setter refuses changes after startup)
        var lifetimeField = typeof(Avalonia.Application).GetField("_applicationLifetime", Private)!;
        var previousLifetime = lifetimeField.GetValue(app);
        bool autoResume = svc.Settings.AutoResumeLive;
        var parked = svc.Runners.ToList();
        svc.Runners.Clear();
        var cache = Field<Dictionary<string, ITradingAccount>>(svc, "_realBrokers");
        string fixturePath = Path.Combine(AppPaths.Strategies, QuitStrategy + ".cs");
        var keys = new List<string>();
        TradingHub.App.Views.ChartTab? chartTab = null, windowTab = null;
        FixtureSlowCloseAccount? slow = null;

        // after a quit that only recorded the exit: the services were never shut down, only the live saves stopped
        void Reopen() => Set(svc, "_closingDown", false);

        try
        {
            var list = TradingHub.App.Controls.InstrumentPicker.InstrumentsAsync(svc, "sim");
            Wait(list, 30);
            var inst = list.Result.First(i => i.Symbol == "SIM-ES");
            var data = svc.Open("sim", inst);
            Until(() => !data.IsLoadingHistory && data.LastPrice > 0, 60);
            var quitSim = new SimBroker(data, new Portfolio { AccountID = "SAFETY-QUIT", Name = "Fixture quit", BeginBalance = 100_000m, Balance = 100_000m }, new TradingStatistics()) { Name = "Fixture quit" };
            cache[QuitAccountId + "|" + data.Instrument.Key] = quitSim; // what AccountFor returns for this account id
            string key = $"{QuitStrategy}|{inst.FeedId}:{inst.Symbol}|{quitSim.Name}";
            keys.Add(key);
            LiveStore.RemoveRunner(key, deleteFills: true);
            File.WriteAllText(fixturePath, """
                using TradingHub.Strategies;

                [Strategy("Safety quit fixture", Description = "UI fixture for the Quit checks: it never trades by itself.")]
                public class SafetyQuitFixture : Strategy
                {
                }
                """);
            svc.Strategies.Refresh();
            var file = svc.Strategies.Files.First(f => f.FileName == QuitStrategy);
            // stop and target far from the market: they stay working for the whole scene
            int ticks = (int)Math.Max(20, Math.Round(data.LastPrice * 0.1m / inst.TickSize));
            var parameters = new Dictionary<string, object?> { ["StopLossTicks"] = ticks, ["TakeProfitTicks"] = ticks };

            // a long position on the account and in the strategy's ledger: the strategy inherits it and protects it at start
            void Hold(SimBroker account, string ledgerKey)
            {
                account.Market(OrderDirections.Buy, 1, "safety-quit: fixture position");
                LiveStore.AppendFill(ledgerKey, new LiveFill(DateTime.UtcNow, true, 1, data.LastPrice, inst.Symbol, inst.PointValue));
            }

            LiveStrategyRunner? Launch(string accountId)
            {
                var launch = svc.Live.LaunchAsync(new LiveLaunch { File = file, ProfileId = "sim", Instrument = inst, AccountId = accountId, Period = ChartPeriod.H1, Parameters = new(parameters) });
                Wait(launch, 120);
                if (!launch.IsCompletedSuccessfully) return null;
                if (launch.Result.Runner == null) Console.WriteLine("  launch: " + launch.Result.Message);
                return launch.Result.Runner;
            }

            // its position and its working stop loss and take profit (the sell stop and sell limit placed or kept for it)
            decimal Pos(ITradingAccount account) => account.Position.Volume;
            List<Order> Protection(ITradingAccount account) =>
                account.ActiveOrders.Where(o => o.Direction == OrderDirections.Sell && o.Type is OrderTypes.Stop or OrderTypes.Limit).ToList();

            // the app's own quit wiring for a desktop lifetime (⌘Q's shutdown request and the exit), fresh for each case
            FixtureDesktopLifetime Desktop()
            {
                var desktop = FixtureDesktopLifetime.For(window);
                App.UseQuit(desktop.Lifetime);
                return desktop;
            }

            // what the next start reads as running when Trading Hub closed (read before the live saves resume)
            List<LiveRunnerInfo> RunningAtClose() => LiveStore.LoadRunners().Where(i => i.Key == key && i.Running).ToList();
            LiveRunnerInfo? Saved() => LiveStore.InfoFor(key);
            LiveEntry Entry() => svc.Live.Entries().First(e => e.Key == key);
            // the ids name exactly those orders: by exchange ID or by their id in this process ("local:<session>:<id>")
            bool Names(IReadOnlyCollection<string>? ids, List<Order> orders) => ids != null && ids.Count == orders.Count &&
                orders.All(o => ids.Contains(o.ExchangeOrderId) || ids.Any(id => id.EndsWith(":" + o.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)));

            LiveStrategyRunner? Resume()
            {
                var resume = svc.Live.ResumeAsync(Entry());
                Wait(resume, 120);
                if (resume.IsCompletedSuccessfully && resume.Result != null) Console.WriteLine("  resume: " + resume.Result);
                return svc.Live.Running(key);
            }

            LiveStrategyRunner? runner = null;
            var kept = new List<Order>();
            var menuBarListed = new List<string>();
            var atClose = new List<LiveRunnerInfo>();

            Part("menu-bar Quit with a strategy holding a position (characterization)", () =>
            {
                Hold(quitSim, key);
                runner = Launch(QuitAccountId);
                Check(runner is { IsRunning: true, OwnPosition: 1 } && Pos(quitSim) == 1 && Protection(quitSim).Count == 2,
                    $"fixture: a live strategy holds a position with its stop loss and take profit working on its own simulated account ({runner?.OwnPosition}, {Protection(quitSim).Count} protective orders)");
                if (runner == null) return;
                var started = runner;
                var desktop = Desktop();
                bool runningAtExit = false;
                desktop.Exiting = () => runningAtExit = started.IsRunning;
                lifetimeField.SetValue(app, desktop.Lifetime); // the lifetime the menu bar's Quit used to close directly
                try
                {
                    MenuBar.Quit();
                    var dialog = Find(QuitTitle, 3);
                    Check(dialog != null, "menu-bar Quit asks first while a live strategy holds a position (Quit Trading Hub dialog)");
                    if (dialog != null)
                    {
                        menuBarListed = Listed(dialog);
                        Shot(dialog, "safety_quit_dialog");
                        Press(dialog, Key.Enter);
                    }

                    Until(() => desktop.Shutdowns > 0, 5);
                    atClose = RunningAtClose();
                    Check(desktop.Shutdowns == 1 && !runningAtExit,
                        $"menu-bar Quit exits only after saving and stopping the live strategies, never through a forced Shutdown() while one still runs (exits {desktop.Shutdowns}, still running at exit: {runningAtExit})");
                }
                finally
                {
                    lifetimeField.SetValue(app, previousLifetime);
                }
            });

            Part("Keep stops at broker and quit", () =>
            {
                if (runner == null) return;
                kept = Protection(quitSim);
                var saved = Saved();
                Check(!runner.IsRunning && kept.Count == 2 && Pos(quitSim) == 1,
                    $"Keep (Enter, the default): the strategy stopped, its position stays open and its stop loss and take profit stay working on the simulated account ({kept.Count} working, position {Pos(quitSim)})");
                Check(Names(runner.ProtectionOrderIds, kept) && Names(saved?.ProtectionOrderIds, kept),
                    $"Keep: their identities are recorded in the strategy's saved state ({string.Join(", ", saved?.ProtectionOrderIds ?? new List<string>())})");
                Check(atClose.Count == 1 && saved?.Running == true, "Keep: the saved state still says it was running, so auto-resume restarts it");
            });

            Part("auto-resume adopts the kept orders", () =>
            {
                Reopen();
                svc.Settings.AutoResumeLive = true;
                svc.RunningAtLastClose = atClose;
                Wait(svc.Live.ResumeAtStartAsync(), 120);
                runner = svc.Live.Running(key);
                var now = Protection(quitSim);
                Check(runner != null && now.Count == 2 && now.All(kept.Contains) && Names(runner.ProtectionOrderIds, now),
                    $"auto-resume: the resumed strategy adopts the stop loss and take profit kept at the broker and places none ({now.Count} working, the kept ones: {now.All(kept.Contains)})");
                Check(runner?.LogLines.Any(l => l.Contains("Adopted the stop loss and take profit kept at the broker", StringComparison.Ordinal)) == true,
                    "auto-resume: its log says it adopted them");
            });

            Part("⌘Q asks the same question; Don't quit changes nothing", () =>
            {
                if (runner == null) return;
                var desktop = Desktop();
                bool allowed = desktop.RequestShutdown();
                var dialog = Find(QuitTitle, 3);
                Check(!allowed && dialog != null, $"⌘Q while a strategy holds a position: the first shutdown request is canceled and the Quit Trading Hub dialog asks (allowed: {allowed})");
                if (dialog == null) return;
                var listed = Listed(dialog);
                Check(listed.Count == 1 && listed.SequenceEqual(menuBarListed) && listed[0].Contains("«Safety quit fixture»", StringComparison.Ordinal)
                      && listed[0].Contains("long 1 SIM-ES", StringComparison.Ordinal) && listed[0].Contains("2 protective orders working", StringComparison.Ordinal),
                    $"⌘Q and the menu bar reach the same question about the same strategies («{string.Join(" | ", listed)}»)");
                Shot(dialog, "safety_quit_dialog_cmd_q");
                var keep = ButtonOf(dialog, s => s == KeepText);
                var cancelStops = ButtonOf(dialog, s => s == CancelStopsText);
                var close = ButtonOf(dialog, s => s == CloseText);
                var stay = ButtonOf(dialog, s => s == StayText);
                Check(keep != null && cancelStops != null && close != null && stay != null,
                    "Quit dialog: its four choices are in English (Keep stops at broker and quit, Cancel stops and quit, Close positions and quit, Don't quit)");
                Check(keep is { IsDefault: true } && keep.Classes.Contains("primary") && cancelStops is { IsDefault: false } && stay is { IsDefault: false },
                    "Quit dialog: Keep stops at broker and quit is the default button");
                Check(close is { IsDefault: false } && close.Classes.Contains("danger"), "Quit dialog: Close positions and quit has danger styling and is never the default");
                Check(stay is { IsCancel: true } && keep is { IsCancel: false } && cancelStops is { IsCancel: false } && close is { IsCancel: false },
                    "Quit dialog: Don't quit is the Esc (cancel) button");
                if (close != null)
                {
                    close.Focus();
                    Press(close, Key.Enter);
                    pump(0.3);
                }

                Check(dialog.IsVisible && runner.IsRunning && Pos(quitSim) == 1, "Quit dialog: Enter with Close positions and quit focused does nothing (closing the positions needs a click)");
                Press(dialog, Key.Escape);
                Until(() => !dialog.IsVisible, 3);
                pump(0.5);
                Check(!dialog.IsVisible && runner.IsRunning && Protection(quitSim).Count == 2 && desktop.Shutdowns == 0 && !Field<bool>(svc, "_closingDown"),
                    "Don't quit (Esc): nothing changes; the strategy keeps running with its stop loss and take profit, the app does not exit and live saves continue");
            });

            Part("Cancel stops and quit", () =>
            {
                if (runner == null) return;
                var desktop = Desktop();
                var working = Protection(quitSim);
                MenuBar.Quit();
                var dialog = Find(QuitTitle, 3);
                Click(dialog == null ? null : ButtonOf(dialog, s => s == CancelStopsText));
                Until(() => desktop.Shutdowns > 0, 5);
                atClose = RunningAtClose();
                var saved = Saved();
                Check(dialog != null && desktop.Shutdowns == 1 && !runner.IsRunning && working.Count == 2 && working.All(o => o.State != OrderStates.Active) && Pos(quitSim) == 1,
                    $"Cancel stops: the stop loss and take profit are canceled, the position stays open and the app exits once (exits {desktop.Shutdowns}, working {Protection(quitSim).Count}, position {Pos(quitSim)})");
                Check(atClose.Count == 1 && saved?.ProtectionOrderIds is not { Count: > 0 }, "Cancel stops: the saved state still says it was running and names no orders to adopt");
            });

            Part("resume after Cancel stops", () =>
            {
                Reopen();
                runner = Resume();
                var placed = Protection(quitSim);
                Check(runner is { OwnPosition: 1 } && placed.Count == 2 && placed.Count(o => o.Type == OrderTypes.Stop) == 1 && !placed.Any(kept.Contains),
                    $"resumed after Cancel stops: nothing to adopt, so it places one new stop loss and one new take profit ({placed.Count} working)");
            });

            Part("Close positions and quit", () =>
            {
                if (runner == null) return;
                var desktop = Desktop();
                bool allowed = desktop.RequestShutdown();
                var dialog = Find(QuitTitle, 3);
                Click(dialog == null ? null : ButtonOf(dialog, s => s == CloseText));
                Until(() => desktop.Shutdowns > 0, 15);
                Check(!allowed && dialog != null && desktop.Shutdowns == 1 && Pos(quitSim) == 0 && runner is { OwnPosition: 0, IsClosing: false } && Protection(quitSim).Count == 0 && Find(NotClosedTitle, 0.2) == null,
                    $"Close positions (from ⌘Q): the position is closed at market on the account and in its ledger, its orders canceled, then the app exits once (position {Pos(quitSim)}, ledger {runner.OwnPosition}, exits {desktop.Shutdowns})");
                Check(desktop.Requests == 1 && desktop.RequestShutdown() && Find(QuitTitle, 0.5) == null && desktop.Shutdowns == 1,
                    "⌘Q: once decided, the exit raises no new shutdown request, and a later request goes through without asking again (no loop)");
            });

            Part("no positions: no question", () =>
            {
                Reopen();
                var flat = runner = Resume();
                var desktop = Desktop();
                MenuBar.Quit();
                var dialog = Find(QuitTitle, 1.5);
                Until(() => desktop.Shutdowns > 0, 5);
                Check(flat is { OwnPosition: 0 } && !flat.IsRunning && dialog == null && desktop.Shutdowns == 1,
                    $"no positions: the menu bar's Quit asks nothing, stops the strategies and exits (dialog shown: {dialog != null}, exits {desktop.Shutdowns})");
                Reopen();
                flat = runner = Resume();
                desktop = Desktop();
                bool allowed = desktop.RequestShutdown();
                dialog = Find(QuitTitle, 1.5);
                Check(flat is { OwnPosition: 0 } && !flat.IsRunning && allowed && dialog == null && desktop.Shutdowns == 0,
                    "no positions: ⌘Q is not canceled; it stops the strategies within the request and lets the app close itself, as before");
            });

            Part("restart with new parameters keeps and adopts", () =>
            {
                Reopen();
                Hold(quitSim, key);
                runner = Resume();
                var placed = Protection(quitSim);
                Check(runner is { OwnPosition: 1 } && Pos(quitSim) == 1 && placed.Count == 2, $"fixture: resumed with a position again, protected by a new stop loss and take profit ({placed.Count} working)");
                if (runner == null) return;
                var old = runner;
                var restart = svc.Live.RestartWithParametersAsync(Entry(), old.StrategyInstance.GetParameters());
                Wait(restart, 120);
                runner = svc.Live.Running(key);
                var now = Protection(quitSim);
                Check(restart.IsCompletedSuccessfully && restart.Result == null && runner != null && !ReferenceEquals(runner, old) && now.Count == 2 && now.All(placed.Contains) && Names(runner.ProtectionOrderIds, now),
                    $"restart with new parameters keeps the stop loss and take profit working and the restarted strategy adopts them, placing none ({now.Count} working, the same ones: {now.All(placed.Contains)})");
                kept = now;
            });

            Part("a chart strategy restored from saved state adopts the kept orders", () =>
            {
                if (runner == null) return;
                var desktop = Desktop();
                MenuBar.Quit();
                var dialog = Find(QuitTitle, 3);
                Click(dialog == null ? null : ButtonOf(dialog, s => s == KeepText));
                Until(() => desktop.Shutdowns > 0, 5);
                atClose = RunningAtClose();
                Check(desktop.Shutdowns == 1 && !runner.IsRunning && Protection(quitSim).Count == 2 && atClose.Count == 1,
                    "Keep (clicked): the strategy stops, recorded as running, and its stop loss and take profit stay working");
                Reopen();
                // its chart as the workspace restores it at startup: set to execute on that account, waiting to be resumed
                var open = tv.NewTabAsync("sim", inst.Symbol, ChartPeriod.M5, activate: false);
                Wait(open, 60);
                var tab = chartTab = open.Result;
                Until(() => tab.Model is { Data.IsLoadingHistory: false }, 60);
                Wait(tv.AddStrategyAsync(tab, file, parameters, openSettings: false), 120);
                var cs = tab.Strategies.Last();
                cs.Live = true;
                cs.Paused = true;
                cs.AccountId = QuitAccountId;
                Wait(tv.RunStrategyAsync(tab, cs), 120);
                Check(cs.Runner == null && svc.Live.Entries().Any(e => e.Key == key && e.WaitingResume), "fixture: a chart strategy on that account waits to be resumed, as restored from the saved workspace");
                svc.RunningAtLastClose = atClose;
                Wait(svc.Live.ResumeAtStartAsync(), 120);
                var now = Protection(quitSim);
                Check(cs.Runner is { IsRunning: true, Source: "chart" } chartRunner && now.Count == 2 && now.All(kept.Contains) && Names(chartRunner.ProtectionOrderIds, now),
                    $"a chart strategy restored from saved state (auto-resumed on its chart) adopts the kept stop loss and take profit and places none ({now.Count} working, the kept ones: {now.All(kept.Contains)})");
                if (cs.Runner is not { } adopted) return;
                // restart with new parameters from its chart: the same keep-and-adopt as on the panel
                var restart = svc.Live.RestartWithParametersAsync(Entry(), adopted.StrategyInstance.GetParameters());
                Wait(restart, 120);
                var after = Protection(quitSim);
                Check(restart.IsCompletedSuccessfully && restart.Result == null && cs.Runner is { IsRunning: true } restarted && !ReferenceEquals(restarted, adopted)
                      && after.Count == 2 && after.All(kept.Contains) && Names(restarted.ProtectionOrderIds, after),
                    $"restart with new parameters on its chart keeps the stop loss and take profit working and the restarted chart strategy adopts them ({after.Count} working, the kept ones: {after.All(kept.Contains)})");
                tv.RemoveStrategy(tab, cs);
            });

            Part("Close positions that the broker does not confirm in time", () =>
            {
                Reopen();
                var inner = new SimBroker(data, new Portfolio { AccountID = "SAFETY-SLOW", Name = "Fixture slow close", BeginBalance = 100_000m, Balance = 100_000m }, new TradingStatistics()) { Name = "Fixture slow close" };
                slow = new FixtureSlowCloseAccount(inner);
                cache[QuitAccountId + "-slow|" + data.Instrument.Key] = slow;
                string slowKey = $"{QuitStrategy}|{inst.FeedId}:{inst.Symbol}|{slow.Name}";
                keys.Add(slowKey);
                LiveStore.RemoveRunner(slowKey, deleteFills: true);
                Hold(inner, slowKey);
                var slowRunner = Launch(QuitAccountId + "-slow");
                Check(slowRunner is { IsRunning: true, OwnPosition: 1 } && Protection(slow).Count == 2, "fixture: a strategy holds a protected position on an account whose market orders the broker never confirms");
                if (slowRunner == null) return;
                var desktop = Desktop();
                App.Quit!.CloseWait = TimeSpan.FromSeconds(2);
                MenuBar.Quit();
                var dialog = Find(QuitTitle, 3);
                Click(dialog == null ? null : ButtonOf(dialog, s => s == CloseText));
                var notice = Find(NotClosedTitle, 8);
                string text = notice == null ? "" : Text(notice);
                Check(notice != null && desktop.Shutdowns == 0 && text.Contains("did not confirm them within 2 seconds", StringComparison.Ordinal)
                      && text.Contains("«Safety quit fixture»", StringComparison.Ordinal) && text.Contains("Check them at the broker", StringComparison.Ordinal),
                    $"Close positions: when the broker does not confirm a close in time, an English message names the strategy before the app exits («{text}»)");
                if (notice != null)
                {
                    Shot(notice, "safety_quit_not_closed");
                    Click(ButtonOf(notice, s => s == "Quit"));
                }

                Until(() => desktop.Shutdowns > 0, 5);
                Check(desktop.Shutdowns == 1, "Close positions: after that message the app exits once");
            });

            Part("closing the main window quits through the same path (Keep in menu bar off)", () =>
            {
                Reopen();
                bool keepInMenuBar = svc.Settings.KeepInMenuBar;
                svc.Settings.KeepInMenuBar = false;
                // the scene's safety net runs after the window's own handler: it records what that handler decided, then
                // keeps the window open whatever it decided, so this process never loses its main window
                bool? appCanceled = null;
                void Net(object? sender, WindowClosingEventArgs e)
                {
                    appCanceled = e.Cancel;
                    e.Cancel = true;
                }

                window.Closing += Net;
                try
                {
                    // a live chart strategy in a floating chart window, holding a position with its stop loss and take profit
                    if (Pos(quitSim) == 0) Hold(quitSim, key);
                    var open = tv.NewTabAsync("sim", inst.Symbol, ChartPeriod.M5, activate: false);
                    Wait(open, 60);
                    var tab = windowTab = open.Result;
                    Until(() => tab.Model is { Data.IsLoadingHistory: false }, 60);
                    Wait(tv.AddStrategyAsync(tab, file, parameters, openSettings: false), 120);
                    var cs = tab.Strategies.Last();
                    cs.Live = true;
                    cs.AccountId = QuitAccountId;
                    Wait(tv.RunStrategyAsync(tab, cs), 120);
                    var floating = tv.Detach(tab);
                    pump(0.3);
                    var working = Protection(quitSim);
                    Check(cs.Runner is { IsRunning: true, OwnPosition: 1 } && working.Count == 2 && floating != null && tv.FloatingWindows.Contains(floating),
                        $"fixture: a live chart strategy in a floating chart window holds a position with its stop loss and take profit working ({working.Count} working)");
                    if (cs.Runner is not { } chartRunner || floating is not { } chartWindow) return;
                    bool Untouched() => chartRunner.IsRunning && working.All(o => o.State == OrderStates.Active) && Protection(quitSim).Count == 2 && tv.FloatingWindows.Contains(chartWindow);

                    var desktop = Desktop();
                    window.Close(); // what the red close button does: a WindowClosing close
                    var dialog = Find(QuitTitle, 3);
                    Check(dialog != null && appCanceled == true,
                        $"closing the main window with Keep in menu bar off cancels the close and asks first (Quit Trading Hub dialog shown: {dialog != null}, close canceled by the window: {appCanceled})");
                    Check(Untouched(), "closing the main window stops nothing before the decision: the floating chart's strategy keeps running with its stop loss and take profit working");
                    if (dialog == null) return;
                    Press(dialog, Key.Escape);
                    Until(() => !dialog.IsVisible, 3);
                    pump(0.5);
                    Check(window.IsVisible && Untouched() && desktop.Shutdowns == 0, "Don't quit after closing the main window: the window stays open and everything keeps running");

                    window.Close();
                    dialog = Find(QuitTitle, 3);
                    if (dialog != null) Press(dialog, Key.Enter); // Keep, the default
                    Until(() => desktop.Shutdowns > 0, 5);
                    var saved = Saved();
                    Check(dialog != null && desktop.Shutdowns == 1 && !chartRunner.IsRunning && working.All(o => o.State == OrderStates.Active)
                          && Names(chartRunner.ProtectionOrderIds, working) && Names(saved?.ProtectionOrderIds, working) && saved?.Running == true,
                        "Keep after closing the main window works as from ⌘Q: the strategy stops, its stop loss and take profit stay working and are recorded, and the app exits once");

                    // the quit's own exit then closes the windows (Avalonia closes them for an application shutdown)
                    appCanceled = null;
                    typeof(Window).GetMethod("CloseCore", Private)!.Invoke(window, new object[] { WindowCloseReason.ApplicationShutdown, true, false });
                    Check(appCanceled == false && Find(QuitTitle, 0.5) == null && !tv.FloatingWindows.Contains(chartWindow),
                        "the quit's own exit closes the windows normally: an application-shutdown close is not intercepted and asks nothing (the floating chart window closes)");
                    appCanceled = null;
                    window.Close();
                    Check(appCanceled == false && Find(QuitTitle, 0.5) == null && desktop.Shutdowns == 1, "once decided, closing the main window again is not intercepted either (no second question)");
                }
                finally
                {
                    window.Closing -= Net;
                    svc.Settings.KeepInMenuBar = keepInMenuBar;
                }
            });
        }
        finally
        {
            Reopen();
            lifetimeField.SetValue(app, previousLifetime);
            svc.Settings.AutoResumeLive = autoResume;
            svc.RunningAtLastClose = new();
            slow?.CancelHeld();
            if (chartTab != null)
            {
                foreach (var cs in chartTab.Strategies.ToList()) tv.RemoveStrategy(chartTab, cs);
                tv.CloseChart(chartTab);
            }

            if (windowTab != null)
            {
                foreach (var cs in windowTab.Strategies.ToList()) tv.RemoveStrategy(windowTab, cs);
                tv.CloseChart(windowTab);
            }

            foreach (var r in svc.Runners.ToList()) r.Stop(flatten: false);
            svc.Runners.Clear();
            svc.Runners.AddRange(parked);
            foreach (var k in keys) LiveStore.RemoveRunner(k, deleteFills: true);
            foreach (var k in cache.Keys.Where(k => k.StartsWith(QuitAccountId, StringComparison.Ordinal)).ToList()) cache.Remove(k);
            if (File.Exists(fixturePath)) File.Delete(fixturePath);
            svc.Strategies.Refresh();
        }

        Console.WriteLine($"Safety quit scenario: {checks} checks, {failures} failures.");
        return failures;
    }
}

/// <summary>
/// A desktop lifetime that records how the app asks to exit instead of ending this process. Avalonia's lifetime
/// interfaces cannot be implemented in source (its reference assemblies forbid it), so <see cref="Lifetime"/> is generated
/// at run time. Its Shutdown() is Avalonia's forced exit (in 11.3.22 it raises no ShutdownRequested);
/// <see cref="RequestShutdown"/> is the platform's request on ⌘Q. Its Exit is never raised: the app's services stay up.
/// </summary>
public class FixtureDesktopLifetime : DispatchProxy
{
    private EventHandler<Avalonia.Controls.ApplicationLifetimes.ShutdownRequestedEventArgs>? _shutdownRequested;
    private Window? _main;

    public Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime Lifetime { get; private set; } = null!;
    public int Shutdowns { get; private set; }
    public int Requests { get; private set; }

    /// <summary>Runs when the app exits (Shutdown), before it is counted.</summary>
    public Action? Exiting { get; set; }

    public static FixtureDesktopLifetime For(Window main)
    {
        var lifetime = Create<Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime, FixtureDesktopLifetime>();
        var fixture = (FixtureDesktopLifetime)(object)lifetime;
        fixture.Lifetime = lifetime;
        fixture._main = main;
        return fixture;
    }

    /// <summary>⌘Q: the platform asks whether the app may close; true unless a handler canceled the request.</summary>
    public bool RequestShutdown()
    {
        Requests++;
        var e = new Avalonia.Controls.ApplicationLifetimes.ShutdownRequestedEventArgs();
        _shutdownRequested?.Invoke(Lifetime, e);
        return !e.Cancel;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case "Shutdown":
                Exiting?.Invoke();
                Shutdowns++;
                return null;
            case "TryShutdown":
                return RequestShutdown();
            case "add_ShutdownRequested":
                _shutdownRequested += (EventHandler<Avalonia.Controls.ApplicationLifetimes.ShutdownRequestedEventArgs>?)args![0];
                return null;
            case "remove_ShutdownRequested":
                _shutdownRequested -= (EventHandler<Avalonia.Controls.ApplicationLifetimes.ShutdownRequestedEventArgs>?)args![0];
                return null;
            case "get_MainWindow":
                return _main;
            case "set_MainWindow":
                _main = (Window?)args![0];
                return null;
            case "get_Windows":
                return _main is { } main ? new[] { main } : Array.Empty<Window>();
            case "get_ShutdownMode":
                return Avalonia.Controls.ShutdownMode.OnLastWindowClose;
            default:
                return null; // Args, setting ShutdownMode, subscribing to Startup or Exit (never raised)
        }
    }
}

/// <summary>
/// A simulated account whose market orders the broker accepts but never confirms (they stay working, unfilled): a close
/// sent while quitting is not confirmed in time. Everything else fills in memory, as on the simulated account.
/// </summary>
internal sealed class FixtureSlowCloseAccount(SimBroker inner) : ITradingAccount
{
    private readonly List<Order> _held = new();

    public string Name => "Fixture slow close";
    public bool IsReal => false;
    public Instrument Instrument => inner.Instrument;
    public Position Position => ((ITradingAccount)inner).Position;
    public IEnumerable<Order> ActiveOrders => inner.ActiveOrders.Concat(_held.Where(o => o.State == OrderStates.Active));
    public string? LastError => inner.LastError;
    public decimal LastPrice => inner.LastPrice;

    public event Action? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public Order Market(OrderDirections dir, decimal qty, string comment = "")
    {
        var order = new Order { Direction = dir, Type = OrderTypes.Market, QuantityToFill = qty, Unfilled = qty, Comment = comment, State = OrderStates.Active, Status = OrderStatus.Placed };
        _held.Add(order);
        return order;
    }

    /// <summary>Cancels the market orders it never confirmed (fixture cleanup).</summary>
    public void CancelHeld()
    {
        foreach (var order in _held.Where(o => o.State == OrderStates.Active)) Cancel(order);
    }

    public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => inner.Limit(dir, qty, price, comment);
    public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => inner.Stop(dir, qty, trigger, comment);
    public Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "") => inner.Exit(type, dir, qty, price, comment);
    public Order Replace(Order order, decimal newPrice, decimal newQuantity) => inner.Replace(order, newPrice, newQuantity);

    public void Cancel(Order order)
    {
        if (!_held.Contains(order))
        {
            inner.Cancel(order);
            return;
        }

        order.State = OrderStates.Done;
        order.Status = OrderStatus.Canceled;
    }

    public void CancelAt(decimal price) => inner.CancelAt(price);

    public void CancelAll()
    {
        CancelHeld();
        inner.CancelAll();
    }

    public void Modify(Order order, decimal newPrice) => inner.Modify(order, newPrice);
    public void Flatten() => inner.Flatten();
    public void Reverse() => inner.Reverse();
}
