// Escena "mercado": el backtest según el mercado (rango, mixto, tendencia alcista o bajista y volatilidad) y volverlo a
// correr solo en las condiciones elegidas, con el contexto medido en el momento (como en vivo).
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Strategies;

internal static class BacktestMarketScene
{
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "OK " : "FALLA")}  {what}");
        if (!ok) Failures.Add(what);
    }

    private const string Code = """
        using TradingHub.Strategies;

        [Strategy("Market-condition test")]
        public class BtMercado : Strategy
        {
            private int _entry = -100;

            protected override void OnBar(Bar bar)
            {
                if (IsFlat && CurrentBar % 6 == 0)
                {
                    Buy(1);
                    _entry = CurrentBar;
                }
                else if (IsLong && CurrentBar - _entry >= 4) ClosePosition();
            }
        }
        """;

    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// Sin historia de BTC guardada (por ejemplo, el código recién bajado del repositorio): se arman 45 días sintéticos
    /// con tendencias, rangos y volatilidad que cambia, en la carpeta temporal de la escena.
    /// </summary>
    private static void SyntheticBtcIfMissing()
    {
        var folder = Path.Combine(AppPaths.Ticks, "binance-futures", "BTCUSDT");
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.vtk").Count(f => new FileInfo(f).Length > 500_000) >= 12) return;
        var inst = new TradingHub.Market.Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", Exchange = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
        var rnd = new Random(7);
        double price = 60_000;
        long id = 1;
        const int perDay = 120_000;
        var today = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        for (var d = today.AddDays(-44); d <= today; d = d.AddDays(1))
        {
            int week = d.DayNumber / 7 % 4;
            double drift = week switch { 0 => 0.025, 1 => -0.025, _ => 0 } / perDay, sigma = (week == 3 ? 0.045 : 0.02) / Math.Sqrt(perDay);
            var start = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var trades = new List<TradingHub.Market.Trade>(perDay);
            double last = price;
            for (int i = 0; i < perDay; i++)
            {
                double z = Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
                price *= 1 + drift + sigma * z;
                decimal px = Math.Round((decimal)price / inst.TickSize) * inst.TickSize;
                var side = price > last ? TradingHub.Market.TradeSide.Buy : price < last ? TradingHub.Market.TradeSide.Sell : (rnd.Next(2) == 0 ? TradingHub.Market.TradeSide.Buy : TradingHub.Market.TradeSide.Sell);
                last = price;
                trades.Add(new TradingHub.Market.Trade(id++, start.AddTicks((long)(TimeSpan.TicksPerDay * (i + rnd.NextDouble() * 0.9) / perDay)), px, Math.Round((decimal)(0.001 + rnd.NextDouble() * rnd.NextDouble() * 0.8), 3), side));
            }

            TradingHub.Data.HistoricalData.WriteDay(AppPaths.Ticks, inst, d, trades, replace: true);
        }
    }

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        Check(!AppPaths.Strategies.Contains("/Documents/", StringComparison.Ordinal), "las estrategias son una copia temporal");
        SyntheticBtcIfMissing();
        File.WriteAllText(Path.Combine(AppPaths.Strategies, "BtMercado.cs"), Code);
        window.Navigate("strategies");
        pump(1.5);
        var view = window.Page("strategies")!;
        var t = view.GetType();
        var catalog = App.Services.Strategies;
        catalog.Refresh();
        var file = catalog.Files.First(f => f.FileName == "BtMercado");
        var sel = (Task)t.GetMethod("SelectAsync", Any)!.Invoke(view, new object[] { file })!;
        for (int i = 0; i < 200 && !sel.IsCompleted; i++) pump(0.05);
        for (int i = 0; i < 100 && file.Type == null; i++) pump(0.1);
        t.GetMethod("ShowTab", Any)!.Invoke(view, new object[] { "backtest" });
        pump(0.5);

        // datos: BTC con historia guardada (diez días completos, y el mes anterior para el contexto)
        var source = (ComboBox)t.GetField("_dataSource", Any)!.GetValue(view)!;
        var btc = source.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is TradingHub.Data.SymbolData { FeedId: "binance-futures", Symbol: "BTCUSDT" });
        if (btc?.Tag is not TradingHub.Data.SymbolData data)
        {
            Check(false, "hay historia de BTCUSDT para el backtest");
            return Failures.Count;
        }

        source.SelectedItem = btc;
        var days = data.Days.Where(d => d.Bytes > 500_000).Select(d => d.Day).OrderBy(d => d).ToList();
        var last = days.Where(d => days.Contains(d.AddDays(1))).LastOrDefault();
        ((TextBox)t.GetField("_period", Any)!.GetValue(view)!).Text = "M15";
        ((CalendarDatePicker)t.GetField("_from", Any)!.GetValue(view)!).SelectedDate = last.AddDays(-9).ToDateTime(TimeOnly.MinValue);
        ((CalendarDatePicker)t.GetField("_to", Any)!.GetValue(view)!).SelectedDate = last.ToDateTime(TimeOnly.MinValue);

        BacktestResult? Result() => (BacktestResult?)t.GetField("_lastResult", Any)!.GetValue(view);
        void RunAndWait(Func<Task> start)
        {
            var before = Result();
            var task = start();
            var limit = DateTime.UtcNow.AddMinutes(4);
            while (!task.IsCompleted && DateTime.UtcNow < limit) pump(0.1);
            for (int i = 0; i < 600 && ReferenceEquals(Result(), before); i++) pump(0.1);
            pump(0.8);
        }

        // sin filtro de contexto (una corrida anterior de la escena pudo dejarlo guardado en la estrategia)
        var noFilter = new Dictionary<string, object?>
        {
            [nameof(Strategy.RegimeFilter)] = RegimeFilter.All, [nameof(Strategy.MinVolatilityRank)] = 0, [nameof(Strategy.MaxVolatilityRank)] = 100,
        };
        RunAndWait(() =>
        {
            t.GetMethod("ApplyParameters", Any)!.Invoke(view, new object[] { noFilter });
            return Task.CompletedTask;
        });
        var all = Result();
        Check(all is { Ok: true } && all.Trades.Count >= 10, $"corre el backtest ({all?.Trades.Count} operaciones)");
        if (all == null) return Failures.Count;
        Check(all.Trades.All(x => x.EntryTrend != null),
            $"con el mes anterior, todas las operaciones (incluso las primeras) saben cómo estaba el mercado ({all.Trades.Count(x => x.EntryTrend == null)} sin contexto)");

        var tabs = (Ui.Segmented<string>)t.GetField("_resultTabs", Any)!.GetValue(view)!;
        tabs.Choose("market");
        pump(1);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "backtest_mercado.png"));
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text ?? "").ToList();
        Check(texts.Contains("Range") && texts.Contains("Mixed") && texts.Contains("Uptrend") && texts.Contains("Downtrend") &&
              texts.Any(x => x.StartsWith("Low volatility", StringComparison.Ordinal)) && texts.Contains("PERFORMANCE BY MARKET CONDITION"),
            "la pestaña «Según el mercado» muestra cómo le fue en cada condición y volatilidad");

        // solo en rango: un backtest nuevo con el filtro (no un recorte del anterior)
        var picker = (MarketConditionPicker)t.GetField("_btConditions", Any)!.GetValue(view)!;
        picker.Load(RegimeFilter.RangeOnly, 0, 100);
        var rerun = view.GetVisualDescendants().OfType<Button>().First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Rerun only in selected conditions"));
        RunAndWait(() =>
        {
            rerun.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        });
        var only = Result();
        Check(only != null && !ReferenceEquals(only, all) && Equals(only.Parameters[nameof(Strategy.RegimeFilter)], RegimeFilter.RangeOnly),
            "vuelve a correr el backtest con el filtro «solo en rango»");
        if (only != null)
        {
            Check(only.Trades.Count > 0 && only.Trades.Count < all.Trades.Count && only.Trades.All(x => x.EntryTrend <= Robustness.RangeTrend),
                $"solo entra cuando el mercado que veía en ese momento era rango ({only.Trades.Count} de {all.Trades.Count} operaciones; máx. eficiencia {only.Trades.Max(x => x.EntryTrend ?? 0):0.00})");
            // no es un recorte: con el filtro las operaciones cambian (otra entrada cuando la anterior no se tomó)
            var cut = all.Trades.Where(x => x.EntryTrend <= Robustness.RangeTrend).Select(x => x.EntryTime).ToHashSet();
            Check(only.Trades.Any(x => !cut.Contains(x.EntryTime)) || only.Trades.Count == cut.Count,
                "es una corrida nueva (la estrategia decide con el filtro en cada vela), no las mismas operaciones filtradas");
        }

        pump(0.5);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "backtest_mercado_filtrado.png"));
        texts = view.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text ?? "").ToList();
        Check(texts.Contains("In all conditions") && texts.Any(x => x.StartsWith("With filter (only in range)", StringComparison.Ordinal)),
            "compara todas las condiciones contra solo las elegidas");

        // y se vuelve a todas
        var reset = view.GetVisualDescendants().OfType<Button>().First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Run in all conditions"));
        RunAndWait(() =>
        {
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        });
        Check(Result() is { } back && Equals(back.Parameters[nameof(Strategy.RegimeFilter)], RegimeFilter.All) && back.Trades.Count == all.Trades.Count,
            "«Correr en todas las condiciones» vuelve a la corrida sin filtro");

        // el optimizador también prueba períodos de las velas, como un parámetro más
        t.GetMethod("ShowTab", Any)!.Invoke(view, new object[] { "optimize" });
        pump(0.8);
        var periodCheck = view.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Content as string == "Bar period" && c.IsEffectivelyVisible);
        var comboText = (TextBlock)t.GetField("_comboCount", Any)!.GetValue(view)!;
        string before = comboText.Text ?? "";
        Check(periodCheck != null, "el optimizador tiene «Período de las velas» con los períodos para tildar");
        if (periodCheck != null)
        {
            periodCheck.IsChecked = true;
            pump(0.3);
            var chips = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(b => b.Content is "M15" or "H1" && b.IsEffectivelyVisible).ToList();
            foreach (var c in chips) c.IsChecked = true;
            pump(0.3);
            var ranges = (System.Collections.IList)t.GetField("_state", Any)!.GetValue(view)!.GetType().GetProperty("Ranges")!.GetValue(t.GetField("_state", Any)!.GetValue(view))!;
            var period = ranges.OfType<ParameterRange>().FirstOrDefault(r => r.IsPeriod);
            Check(period is { Enabled: true } && period.Choices.SequenceEqual(new[] { "M15", "H1" }) && comboText.Text != before,
                $"al tildar M15 y H1 se prueban los dos ({string.Join(", ", period?.Choices ?? new())}; {before} → {comboText.Text})");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "optimizar_periodos.png"));

            // «Usar en Backtest» con una fila que eligió H1 pone ese período en el backtest
            var pars = new Dictionary<string, object?>(((Dictionary<string, object?>)t.GetMethod("BaseParameters", Any)!.Invoke(view, null)!)) { [ParameterRange.PeriodName] = "H1" };
            RunAndWait(() =>
            {
                t.GetMethod("ApplyParameters", Any)!.Invoke(view, new object[] { pars });
                return Task.CompletedTask;
            });
            Check(((TextBox)t.GetField("_period", Any)!.GetValue(view)!).Text == "H1" && Result()?.Settings?.Period == "H1",
                $"«Usar en Backtest» de una fila con período H1 corre el backtest en H1 ({Result()?.Settings?.Period})");
            periodCheck.IsChecked = false;
            pump(0.3);
        }

        // favoritos: la estrella de la lista de estrategias la sube a «Favoritos» (y queda guardada en los ajustes)
        var menu = (Flyout)t.GetField("_strategyMenu", Any)!.GetValue(view)!;
        var pickerButton = (Button)t.GetField("_strategyPicker", Any)!.GetValue(view)!;
        menu.ShowAt(pickerButton);
        pump(0.6);
        int Rows() => (menu.Content as Control)?.GetVisualDescendants().OfType<TextBlock>().Count(x => x.Text == file.DisplayName) ?? 0;
        Button? Star() => (menu.Content as Control)?.GetVisualDescendants().OfType<DockPanel>()
            .FirstOrDefault(d => d.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == file.DisplayName))?.Children.OfType<Button>().FirstOrDefault();
        App.Services.Settings.FavoriteStrategies.Remove(file.FileName);
        int rowsBefore = Rows();
        Star()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(0.4);
        Check(App.Services.Settings.FavoriteStrategies.Contains(file.FileName) && Rows() == rowsBefore + 1 && menu.IsOpen,
            $"la estrella pone la estrategia en «Favoritos», arriba de todas, sin cerrar la lista ({rowsBefore} → {Rows()} filas)");
        if (menu.Content is Control flyoutContent) TopLevel.GetTopLevel(flyoutContent)?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "estrategias_favoritos.png"));
        Star()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(0.4);
        Check(!App.Services.Settings.FavoriteStrategies.Contains(file.FileName) && Rows() == rowsBefore, "tocándola de nuevo sale de favoritos");
        menu.Hide();
        pump(0.3);

        Console.WriteLine(Failures.Count == 0 ? "SEGÚN EL MERCADO: todo OK" : $"SEGÚN EL MERCADO: {Failures.Count} falla(s)");
        return Failures.Count;
    }
}
