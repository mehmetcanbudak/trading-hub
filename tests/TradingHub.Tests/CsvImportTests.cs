using TradingHub.Data;

namespace TradingHub.Tests;

/// <summary>Importar historia de archivos bajados a mano (CFDs como el US100, forex…), reconociendo el formato solo.</summary>
public class CsvImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tradinghub-import-" + Guid.NewGuid().ToString("N"));

    public CsvImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string File(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return path;
    }

    private List<TradingHub.Market.Trade> Day(string symbol, DateOnly day) => TickFile.Read(TickFile.DayPath(Path.Combine(_dir, "ticks"), CsvImport.FeedId, symbol, day));

    [Fact]
    public void Ticks_de_Dukascopy_con_hora_UTC_y_dos_dias()
    {
        var path = File("USATECHIDXUSD_Ticks.csv", """
            Gmt time,Ask,Bid,AskVolume,BidVolume
            01.09.2025 23:59:58.120,23450.520,23449.320,0.0015,0.0012
            01.09.2025 23:59:59.500,23451.020,23449.820,0.0015,0.0012
            02.09.2025 00:00:00.010,23450.020,23448.820,0.0010,0.0010
            """);
        var r = CsvImport.Import(path, "US100", Path.Combine(_dir, "ticks"), TimeSpan.Zero, null, null, CancellationToken.None);
        Assert.Equal("ticks", r.Kind);
        Assert.Equal(3, r.Rows);
        Assert.Equal(2, r.Days);
        Assert.Equal(0.01m, r.TickSize); // 23449.32: dos decimales significativos
        var d1 = Day("US100", new DateOnly(2025, 9, 1));
        Assert.Equal(new[] { 23449.32m, 23449.82m }, d1.Select(t => t.Price));
        Assert.Equal(TradingHub.Market.TradeSide.Buy, d1[1].Side); // subió: compra por la regla del tick
        var d2 = Day("US100", new DateOnly(2025, 9, 2));
        Assert.Equal(TradingHub.Market.TradeSide.Sell, Assert.Single(d2).Side);
    }

    [Fact]
    public void Velas_de_Dukascopy_con_zona_GMT_en_la_hora()
    {
        var path = File("USA500_M1.csv", """
            Local time,Open,High,Low,Close,Volume
            01.09.2025 16:30:00.000 GMT+0200,6450.1,6452.3,6449.8,6451.0,120.5
            01.09.2025 16:31:00.000 GMT+0200,6451.0,6451.9,6450.2,6450.4,98.1
            """);
        var r = CsvImport.Import(path, "US500", Path.Combine(_dir, "ticks"), TimeSpan.Zero, null, null, CancellationToken.None);
        Assert.Equal("1-minute bars", r.Kind);
        Assert.Equal(new DateTime(2025, 9, 1, 14, 30, 0, DateTimeKind.Utc), r.From);
        var trades = Day("US500", new DateOnly(2025, 9, 1));
        Assert.Equal(8, trades.Count); // 4 por vela
        Assert.Equal(6452.3m, trades.Max(t => t.Price));
        Assert.Equal(6449.8m, trades.Min(t => t.Price));
    }

    [Fact]
    public void HistData_ticks_y_velas_en_hora_de_Nueva_York()
    {
        var ticks = File("DAT_ASCII_NSXUSD_T_202509.csv", "20250901 093000123,23450.25,23451.00,0\n20250901 093000456,23450.50,23451.25,0\n");
        var r = CsvImport.Import(ticks, "NSX", Path.Combine(_dir, "ticks"), TimeSpan.FromHours(-5), null, null, CancellationToken.None);
        Assert.Equal(new DateTime(2025, 9, 1, 14, 30, 0, 123, DateTimeKind.Utc), r.From);
        Assert.Equal(2, r.Trades);

        var bars = File("DAT_ASCII_NSXUSD_M1_2025.csv", "20250901 093000;23450.25;23460.00;23440.50;23455.75;0\n20250901 093100;23455.75;23458.00;23452.00;23453.25;0\n");
        var b = CsvImport.Import(bars, "NSX1", Path.Combine(_dir, "ticks"), TimeSpan.FromHours(-5), null, null, CancellationToken.None);
        Assert.Equal("1-minute bars", b.Kind);
        Assert.Equal(8, b.Trades); // sin volumen igual se arman
    }

    [Fact]
    public void MetaTrader_con_fecha_y_hora_separadas_y_bid_vacio()
    {
        var path = File("US100.cash_ticks.csv", "<DATE>\t<TIME>\t<BID>\t<ASK>\t<LAST>\t<VOLUME>\t<FLAGS>\n2025.09.01\t14:00:00.100\t23450.10\t23451.30\t\t\t6\n2025.09.01\t14:00:00.200\t\t23451.50\t\t\t4\n2025.09.01\t14:00:00.300\t23449.90\t23451.10\t\t\t6\n");
        var r = CsvImport.Import(path, "US100MT", Path.Combine(_dir, "ticks"), TimeSpan.Zero, null, null, CancellationToken.None);
        Assert.Equal(0, r.BadLines);
        Assert.Equal(2, r.Trades); // la fila con bid vacío no es una cotización nueva
        Assert.Equal(new[] { 23450.1m, 23449.9m }, Day("US100MT", new DateOnly(2025, 9, 1)).Select(t => t.Price));
    }

    [Fact]
    public void TradingView_con_hora_unix()
    {
        long t0 = new DateTimeOffset(2025, 9, 1, 13, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var path = File("NAS100.csv", $"time,open,high,low,close,Volume\n{t0},23450,23460,23440,23455,1500\n{t0 + 300},23455,23458,23452,23453,900\n");
        var r = CsvImport.Import(path, "NAS100", Path.Combine(_dir, "ticks"), TimeSpan.Zero, null, null, CancellationToken.None);
        Assert.Equal("5-minute bars", r.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(t0).UtcDateTime, r.From);
        Assert.Equal(2400m, Day("NAS100", new DateOnly(2025, 9, 1)).Sum(t => t.Volume));
    }

    [Fact]
    public void Un_archivo_que_no_es_historia_se_rechaza_con_un_mensaje_claro()
    {
        var path = File("notas.txt", "hola\nesto no es un csv de precios\n");
        var ex = Assert.Throws<InvalidDataException>(() => CsvImport.Import(path, "X", Path.Combine(_dir, "ticks"), TimeSpan.Zero, null, null, CancellationToken.None));
        Assert.Contains("format", ex.Message);
    }
}
