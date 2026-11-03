using Avalonia.Controls;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Controls;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Strategies;

internal static partial class SafetyScene
{
    private static void LedgerRecovery(MainWindow window, Action<double> pump, Action<Window, string> shot, Action<bool, string> check)
    {
        var isolated = Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR");
        if (string.IsNullOrWhiteSpace(isolated) || Path.GetFullPath(isolated) != Path.GetFullPath(LiveStore.Root))
            throw new InvalidOperationException("The ledger scene requires its explicit isolated live-store directory.");

        var svc = App.Services;
        var instruments = InstrumentPicker.InstrumentsAsync(svc, "sim");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!instruments.IsCompleted && DateTime.UtcNow < deadline) pump(0.02);
        if (!instruments.IsCompleted) throw new TimeoutException("The isolated simulator instrument list did not complete.");
        var instrument = instruments.GetAwaiter().GetResult().First(i => i.Symbol == "SIM-ES");
        var data = svc.Open("sim", instrument);
        var account = svc.AccountFor(data, "sim");
        var strategyFile = new StrategyFile { Path = Path.Combine(AppPaths.Strategies, nameof(LedgerRecoveryFixtureStrategy) + ".cs"), Type = typeof(LedgerRecoveryFixtureStrategy) };
        string key = $"{typeof(LedgerRecoveryFixtureStrategy).FullName}|{instrument.FeedId}:{instrument.Symbol}|{account.Name}";
        string file = LiveStore.LedgerFile(key);
        var time = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Window? details = null;
        LiveEntry Entry() => svc.Live.Entries().Single(e => e.Key == key);
        void Append(bool buy, decimal price, DateTime at)
        {
            if (LiveStore.AppendFill(key, new LiveFill(at, buy, 1, price, "SIM-ES", 1)) is { } error)
                throw new IOException(error);
        }

        try
        {
            Append(true, 100, time);
            Append(false, 110, time.AddMinutes(1));
            File.SetLastWriteTimeUtc(file, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            LiveStore.SaveRunners(new[]
            {
                new LiveRunnerInfo
                {
                    Key = key, Source = "panel", Strategy = "Ledger recovery fixture", StrategyId = strategyFile.Id,
                    FeedId = instrument.FeedId, ProfileId = "sim", Symbol = instrument.Symbol, AccountId = "sim", Account = account.Name, Period = "M5", PointValue = 1,
                },
            });
            var known = Entry();
            check(known.Ledger is { Position: 0, Realized: 10 } && known.Net == 10,
                "live history starts with its actual closed-trade PnL");
            byte[] committed = File.ReadAllBytes(file);

            // Deliberately damage only this fixture's previously absent intent; never do this to a user's ledger.
            File.WriteAllText(file + ".wal", "{\"Version\":1");
            var unavailable = Entry();
            check(unavailable.Ledger == null && unavailable.Net == null && unavailable.OpenPnL == null && unavailable.LedgerError != null,
                "an unreadable intent invalidates a cached ledger without claiming zero PnL or a flat position");
            check(svc.Live.PlanOf(unavailable).Units == 0,
                "unavailable live history has no new-exposure order plan");
            bool analysisBlocked = false;
            try
            {
                LiveCenter.Analyze(new LivePortfolioView
                {
                    Def = new LivePortfolioDef { Id = "ledger-recovery-fixture", Name = "Ledger recovery fixture" },
                    Members = new List<LiveEntry> { unavailable },
                });
            }
            catch (IOException) { analysisBlocked = true; }
            check(analysisBlocked, "portfolio analysis refuses incomplete live history instead of fabricating zero metrics");

            int ordersBefore = account.ActiveOrders.Count();
            var launch = svc.Live.LaunchAsync(new LiveLaunch { File = strategyFile, ProfileId = "sim", Instrument = instrument, AccountId = "sim", Period = ChartPeriod.M5 });
            deadline = DateTime.UtcNow.AddSeconds(120);
            while (!launch.IsCompleted && DateTime.UtcNow < deadline) pump(0.02);
            if (!launch.IsCompleted) throw new TimeoutException("The isolated launch did not return its history error.");
            var launchResult = launch.GetAwaiter().GetResult();
            check(launchResult.Runner == null && launchResult.Message.Contains(file, StringComparison.Ordinal) &&
                  !svc.Runners.Any(r => r.Key == key) && account.ActiveOrders.Count() == ordersBefore,
                "new live launch reports unreadable history without starting a runner or submitting an order");

            window.Navigate("live");
            pump(0.4);
            shot(window, "safety_ledger_unavailable_live");
            details = new LiveStrategyWindow(svc, _ => { }, unavailable);
            details.Show(window);
            pump(0.3);
            var labels = details.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            check(labels.Contains("Unavailable") && !labels.Contains("Flat"),
                "strategy details distinguish an unknown position from a confirmed flat position");
            shot(details, "safety_ledger_unavailable_details");
            details.Close();
            details = null;

            // Restore the fixture's original state, not a repair procedure for a real damaged intent.
            File.Delete(file + ".wal");
            var restored = Entry();
            check(restored.Ledger is { Position: 0, Realized: 10 } && committed.SequenceEqual(File.ReadAllBytes(file)),
                "rendering an unavailable ledger leaves its committed history unchanged");

            LiveStore.DeleteFills(key);
            Append(true, 100, time);
            Append(false, 120, time.AddMinutes(1));
            File.SetLastWriteTimeUtc(file, new DateTime(2002, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var replaced = Entry();
            check(new FileInfo(file).Length == committed.Length && replaced.Ledger is { Position: 0, Realized: 20 } && replaced.Net == 20,
                "same-length replacement of stopped history invalidates the GUI cache");
            details = new LiveStrategyWindow(svc, _ => { }, replaced);
            details.Show(window);
            shot(details, "safety_ledger_restored_details");
        }
        finally
        {
            details?.Close();
            LiveStore.RemoveRunner(key, deleteFills: true);
            window.Navigate("trading");
            pump(0.2);
        }
    }
}

public sealed class LedgerRecoveryFixtureStrategy : Strategy
{
}
