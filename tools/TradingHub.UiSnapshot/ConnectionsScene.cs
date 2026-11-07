// Escena "conexiones": Tradovate, cTrader, OANDA, Alpaca y Databento contra servidores DE MENTIRA (nunca cuentas reales):
// alta de cada conexión, prueba, descarga de historia en Datos, costo de Databento, cuentas de Tradovate en Cuenta y en el
// Chart Trader, y un gráfico con precios en vivo de OANDA.
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Tests;

internal static class ConnectionsScene
{
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "OK " : "FALLA")}  {what}");
        if (!ok) Failures.Add(what);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        void Await(Task t, double maxSeconds = 60)
        {
            var until = DateTime.UtcNow.AddSeconds(maxSeconds);
            while (!t.IsCompleted && DateTime.UtcNow < until) pump(0.05);
            if (t.IsFaulted) Console.WriteLine("Excepción: " + t.Exception?.GetBaseException().Message);
        }

        void Shot(string name)
        {
            pump(1);
            using (var capturedFrame = window.CaptureRenderedFrame()) capturedFrame?.Save(Path.Combine(outDir, name + ".png"));
        }

        using var oanda = FakeBrokers.Oanda();
        using var databento = FakeBrokers.Databento();
        using var alpaca = FakeBrokers.Alpaca();
        using var tradovate = new FakeTradovate();
        using var ctrader = new FakeCTrader();
        Environment.SetEnvironmentVariable("TRADINGHUB_OANDA_REST", oanda.Http);
        Environment.SetEnvironmentVariable("TRADINGHUB_OANDA_STREAM", oanda.Http);
        Environment.SetEnvironmentVariable("TRADINGHUB_DATABENTO_URL", databento.Http + "/v0");
        Environment.SetEnvironmentVariable("TRADINGHUB_ALPACA_DATA", alpaca.Http);
        Environment.SetEnvironmentVariable("TRADINGHUB_ALPACA_TRADING", alpaca.Http);
        Environment.SetEnvironmentVariable("TRADINGHUB_ALPACA_STREAM", alpaca.Ws + "/v2/iex");
        Environment.SetEnvironmentVariable("TRADINGHUB_TRADOVATE_REST", tradovate.Server.Http + "/v1");
        Environment.SetEnvironmentVariable("TRADINGHUB_TRADOVATE_WS", tradovate.Server.Ws + "/v1/websocket");
        Environment.SetEnvironmentVariable("TRADINGHUB_TRADOVATE_MD", tradovate.Server.Ws + "/md/v1/websocket");
        Environment.SetEnvironmentVariable("TRADINGHUB_CTRADER_URL", ctrader.Server.Ws + "/");

        var svc = App.Services;
        var conns = svc.Connections;
        Check(!AppPaths.Root.StartsWith(TradingHub.Common.Product.DefaultAppData, StringComparison.Ordinal), "datos en una carpeta temporal (nunca los reales)");
        conns.Profiles.RemoveAll(p => p.Kind is ConnectionKind.Tradovate or ConnectionKind.CTrader or ConnectionKind.Oanda or ConnectionKind.Alpaca or ConnectionKind.Databento);
        var pOanda = new ConnectionProfile { Id = "oanda", Kind = ConnectionKind.Oanda, Name = "OANDA", Testnet = true };
        var pDb = new ConnectionProfile { Id = "databento", Kind = ConnectionKind.Databento, Name = "Databento" };
        var pTv = new ConnectionProfile { Id = "tradovate", Kind = ConnectionKind.Tradovate, Name = "Tradovate", Testnet = true, TradovateUser = "demo", TradovateCid = "123" };
        var pCt = new ConnectionProfile { Id = "ctrader", Kind = ConnectionKind.CTrader, Name = "IC Markets", Testnet = true, CTraderClientId = "ID" };
        var pAl = new ConnectionProfile { Id = "alpaca", Kind = ConnectionKind.Alpaca, Name = "Alpaca", ApiKey = "KEY" };
        conns.Profiles.AddRange(new[] { pTv, pCt, pOanda, pAl, pDb });
        conns.SetSecret(pOanda, "TOKEN");
        conns.SetSecret(pDb, "db-KEY");
        conns.SetSecret(pTv, "pw");
        conns.SetSecret(pTv, "apisecret", "SEC");
        conns.SetSecret(pCt, "SECRET");
        conns.SetSecret(pCt, "token", "TOKEN");
        conns.SetSecret(pAl, "SECRET");
        conns.Save();
        Check(conns.GetSecret(pTv, "apisecret") == "SEC", "los secretos van al llavero de prueba (archivo aparte)");
        Check(pCt.MarketDataId == "ctrader" && !pDb.HasMarketData && pTv.HasMarketData, "ids de datos y conexiones sin gráficos");

        // ----- probar cada conexión
        foreach (var p in new[] { pTv, pCt, pOanda, pAl, pDb })
        {
            var t = conns.TestAsync(p, CancellationToken.None);
            Await(t);
            var (ok, msg) = t.IsCompletedSuccessfully ? t.Result : (false, "sin respuesta");
            Check(ok, $"Probar {p.Name}: {msg}");
        }

        // ----- pantalla de Conexiones con cada una
        window.Navigate("connections");
        pump(1);
        var cview = window.GetType().GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window) is Dictionary<string, Control> pages ? pages["connections"] : null;
        foreach (var (p, name) in new[] { (pTv, "tradovate"), (pCt, "ctrader"), (pOanda, "oanda"), (pAl, "alpaca"), (pDb, "databento") })
        {
            Call(cview!, "Select", p);
            Shot("conexion_" + name);
        }

        // ----- Datos: bajar un día de OANDA y de cTrader por su API, y el costo de Databento
        window.Navigate("data");
        pump(1);
        var data = ((Dictionary<string, Control>)window.GetType().GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!)["data"];
        var day = FakeBrokers.Day;
        foreach (var (profile, symbol) in new[] { ("oanda", "NAS100_USD"), ("ctrader", "US100"), ("alpaca", "SPY") })
        {
            var list = conns.GetFeed(profile).GetInstrumentsAsync(CancellationToken.None);
            Await(list);
            var inst = list.Result.First(i => i.Symbol == symbol);
            SetField(data, "_dlProfile", profile);
            SetField(data, "_dlInstrument", inst);
            Field<CalendarDatePicker>(data, "_from").SelectedDate = day.ToDateTime(TimeOnly.MinValue);
            Field<CalendarDatePicker>(data, "_to").SelectedDate = day.ToDateTime(TimeOnly.MinValue);
            Call(data, "UpdateDownloadHint");
            var dl = (Task)Call(data, "DownloadAsync")!;
            Await(dl, 120);
            var path = TickFile.DayPath(AppPaths.Ticks, inst.FeedId, symbol, day);
            var status = Field<TextBlock>(data, "_dlStatus").Text;
            Check(File.Exists(path), $"Datos bajó {symbol} de {profile} ({status})");
            if (File.Exists(path))
            {
                var trades = HistoricalData.Read(AppPaths.Ticks, inst.FeedId, symbol, day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1)).ToList();
                Check(trades.Count > 0, $"  {trades.Count} operaciones guardadas de {symbol}");
            }
        }

        Shot("datos_descargas");
        Field<CalendarDatePicker>(data, "_dbFrom").SelectedDate = day.ToDateTime(TimeOnly.MinValue);
        Field<CalendarDatePicker>(data, "_dbTo").SelectedDate = day.ToDateTime(TimeOnly.MinValue);
        pump(0.5); // los cambios de fecha (que vuelven a pedir el costo) se procesan antes
        Await((Task)Call(data, "DatabentoQuoteAsync")!);
        var dbStatus = Field<TextBlock>(data, "_dbStatus").Text ?? "";
        Check(dbStatus.Contains("US$ 1.26", StringComparison.Ordinal), $"Databento calcula el costo antes de bajar ({dbStatus})");
        Check(databento.Requests.All(r => r.Route != "/v0/timeseries.get_range"), "calcular el costo no baja ni cobra nada");
        Shot("datos_databento");

        // ----- Tradovate: la cuenta aparece en Cuenta y en el Chart Trader de un gráfico de futuros (aunque sea de Rithmic)
        var session = svc.TradovateSessionFor(pTv);
        var until = DateTime.UtcNow.AddSeconds(15);
        while (session.Accounts.Count == 0 && DateTime.UtcNow < until) pump(0.1);
        var accounts = svc.AccountCenter.Accounts();
        var tvAccount = accounts.FirstOrDefault(a => a.Kind == AccountKind.Tradovate);
        Check(tvAccount != null && tvAccount.Name.Contains("DEMO101", StringComparison.Ordinal), $"Cuenta lista la cuenta demo de Tradovate ({tvAccount?.Name})");
        var es = new Instrument { Symbol = "ES", FeedId = "rithmic", Exchange = "CME", TickSize = 0.25m, PointValue = 50 };
        var esData = new InstrumentData(es, conns.GetFeed("rithmic"));
        var options = svc.AccountsFor(esData).ToList();
        Check(options.Any(o => o.Id == "tradovate|tradovate|101" && !o.IsReal), $"el Chart Trader de ES (Rithmic) ofrece la cuenta de Tradovate ({string.Join(", ", options.Select(o => o.Name))})");
        var nas = new Instrument { Symbol = "NAS100_USD", FeedId = "oanda", TickSize = 0.1m };
        Check(!svc.AccountsFor(new InstrumentData(nas, conns.GetFeed("oanda"))).Any(o => o.Id.StartsWith("tradovate", StringComparison.Ordinal)), "un CFD no ofrece la cuenta de futuros");
        if (tvAccount != null)
        {
            window.Navigate("accounts");
            pump(1);
            var av = ((Dictionary<string, Control>)window.GetType().GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!)["accounts"];
            SetField(av, "_selected", tvAccount.Id);
            Call(av, "Refresh");
            pump(11); // el saldo llega cada 10 s
            Call(av, "Refresh");
            Shot("cuenta_tradovate");
            var snap = svc.AccountCenter.Snapshot(tvAccount);
            Check(snap.Connected && snap.Balance == 50120m, $"saldo de la cuenta de Tradovate ({snap.Balance}, {snap.Status})");
        }

        // ----- un gráfico con precios de OANDA
        window.Navigate("trading");
        var tab = window.Trading.ActiveTab;
        if (tab != null)
        {
            var open = window.Trading.OpenInTabAsync(tab, "oanda", "NAS100_USD", ChartPeriod.M1, tab.Model?.Settings.Clone());
            Await(open, 60);
            pump(4);
            var model = window.Trading.ActiveTab?.Model;
            Check(model?.Instrument.FeedId == "oanda" && model.Data.Trades.Count > 0, $"gráfico de NAS100_USD con datos de OANDA ({model?.Data.Trades.Count} operaciones, estado {model?.Data.Status})");
            Shot("grafico_oanda");
        }

        Console.WriteLine(Failures.Count == 0 ? "CONEXIONES: todo OK" : $"CONEXIONES: {Failures.Count} falla(s)");
        return Failures.Count;
    }
}
