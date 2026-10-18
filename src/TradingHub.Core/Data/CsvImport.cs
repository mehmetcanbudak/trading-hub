using System.Globalization;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Lo que dejó una importación.</summary>
public sealed record ImportResult(string Kind, long Rows, long Trades, int Days, DateTime? From, DateTime? To, int BadLines, decimal TickSize);

/// <summary>
/// Importa historia de archivos CSV/TXT bajados a mano (CFDs como el US100, forex, metales…): Dukascopy (ticks o velas,
/// "Gmt time,Ask,Bid…"), HistData (ticks "20250901 140000123,bid,ask" y velas "20250901 140000;o;h;l;c;v"), MetaTrader
/// (&lt;DATE&gt; &lt;TIME&gt; &lt;BID&gt; &lt;ASK&gt;… o velas) y cualquier CSV con fecha y precio u OHLC. El formato se
/// reconoce solo. Los ticks se guardan tal cual (precio bid, una operación por cotización, lado por la regla del tick);
/// las velas se convierten en operaciones sintéticas como la historia larga de los exchanges.
/// </summary>
public static class CsvImport
{
    public const string FeedId = "importado";

    private static readonly string[] DateFormats =
    {
        "dd.MM.yyyy HH:mm:ss.fff", "dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy HH:mm",
        "yyyy.MM.dd HH:mm:ss.fff", "yyyy.MM.dd HH:mm:ss", "yyyy.MM.dd HH:mm",
        "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss",
        "yyyyMMdd HHmmssfff", "yyyyMMdd HHmmss", "yyyyMMdd HH:mm:ss", "yyyyMMdd",
        "yyyy.MM.dd", "yyyy-MM-dd", "dd.MM.yyyy", "MM/dd/yyyy HH:mm:ss", "MM/dd/yyyy HH:mm", "MM/dd/yyyy",
    };

    private sealed class Layout
    {
        public char Delimiter;
        public bool Header;
        public int Date = -1, Time = -1, Bid = -1, Ask = -1, Last = -1, Open = -1, High = -1, Low = -1, Close = -1, Volume = -1;
        public string? DateFormat;
        public bool Unix, UnixMs;
        public bool IsBars => Open >= 0 && High >= 0 && Low >= 0 && Close >= 0;
        public int Price => Bid >= 0 ? Bid : Last >= 0 ? Last : Ask;
    }

    private static string Clean(string s) => s.Trim().Trim('<', '>', '"', '\'').ToLowerInvariant().Replace(" ", "").Replace("_", "");

    private static bool TryNumber(string s, out decimal value)
    {
        s = s.Trim().Trim('"');
        if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        return decimal.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static char Delimiter(string line)
    {
        int tabs = line.Count(c => c == '\t'), semis = line.Count(c => c == ';'), commas = line.Count(c => c == ',');
        return tabs >= semis && tabs >= commas && tabs > 0 ? '\t' : semis >= commas && semis > 0 ? ';' : ',';
    }

    /// <summary>Hora en UTC de un texto de fecha ("… GMT+0200" al final corrige la zona).</summary>
    private static bool TryTime(string text, Layout l, TimeSpan offset, out DateTime utc)
    {
        utc = default;
        text = text.Trim().Trim('"');
        if (l.Unix)
        {
            if (!long.TryParse(text.Split('.')[0], out var n)) return false;
            utc = l.UnixMs ? DateTimeOffset.FromUnixTimeMilliseconds(n).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime;
            return true;
        }

        var own = offset;
        int gmt = text.IndexOf(" GMT", StringComparison.OrdinalIgnoreCase);
        if (gmt > 0)
        {
            var z = text[(gmt + 4)..].Trim();
            text = text[..gmt];
            if (z.Length >= 5 && int.TryParse(z[1..3], out var hh) && int.TryParse(z[3..5], out var mm))
                own = TimeSpan.FromMinutes((hh * 60 + mm) * (z[0] == '-' ? -1 : 1));
        }

        if (l.DateFormat == null || !DateTime.TryParseExact(text, l.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)) return false;
        utc = DateTime.SpecifyKind(local - own, DateTimeKind.Utc);
        return true;
    }

    private static string? Detect(string text, out bool unix, out bool unixMs)
    {
        text = text.Trim().Trim('"');
        unix = unixMs = false;
        int gmt = text.IndexOf(" GMT", StringComparison.OrdinalIgnoreCase);
        if (gmt > 0) text = text[..gmt];
        if (long.TryParse(text.Split('.')[0], out var n) && text.Length >= 9 && !text.Contains(' '))
        {
            unix = true;
            unixMs = n > 100_000_000_000;
            return null;
        }

        foreach (var f in DateFormats)
            if (DateTime.TryParseExact(text, f, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _)) return f;
        return null;
    }

    /// <summary>Reconoce columnas por el encabezado o, si no hay, por la forma de la primera fila.</summary>
    private static Layout? Recognize(string first, string? second)
    {
        var l = new Layout { Delimiter = Delimiter(first) };
        var cols = first.Split(l.Delimiter);
        bool firstIsDate = Detect(cols[0], out var u0, out _) != null || u0 || (cols.Length > 1 && Detect(cols[0] + " " + cols[1], out _, out _) != null);
        bool header = !firstIsDate && first.Any(char.IsLetter);
        if (header)
        {
            l.Header = true;
            for (int i = 0; i < cols.Length; i++)
            {
                string c = Clean(cols[i]);
                if (c is "date" or "day" or "fecha") l.Date = i;
                else if (c is "time" or "hora" or "gmttime" or "localtime" or "time(utc)" or "time(eet)" or "datetime" or "timestamp" or "fechayhora" or "(utc)") l.Time = i;
                else if (c.StartsWith("gmttime") || c.StartsWith("localtime") || c.StartsWith("time(")) l.Time = i;
                else if (c is "bid") l.Bid = i;
                else if (c is "ask") l.Ask = i;
                else if (c is "last" or "price" or "precio") l.Last = i;
                else if (c is "open" or "apertura") l.Open = i;
                else if (c is "high" or "máximo" or "maximo") l.High = i;
                else if (c is "low" or "mínimo" or "minimo") l.Low = i;
                else if (c is "close" or "cierre") l.Close = i;
                else if (c is "volume" or "vol" or "tickvol" or "tickvolume" or "volumen" && l.Volume < 0) l.Volume = i;
            }

            // MetaTrader separa fecha y hora; si solo hay "date" con la hora adentro, va como tiempo
            if (l.Time < 0 && l.Date >= 0)
            {
                l.Time = l.Date;
                l.Date = -1;
            }

            var sample = second?.Split(l.Delimiter);
            if (sample == null || l.Time < 0 || l.Time >= sample.Length) return null;
            string when = l.Date >= 0 ? sample[l.Date].Trim() + " " + sample[l.Time].Trim() : sample[l.Time];
            l.DateFormat = Detect(when, out l.Unix, out l.UnixMs);
            if (l.DateFormat == null && !l.Unix) return null;
            return l.IsBars || l.Price >= 0 ? l : null;
        }

        // sin encabezado: fecha (y hora) al principio y después precios
        int t = 0;
        string time = cols[0];
        string? fmt = Detect(time, out l.Unix, out l.UnixMs);
        if (fmt == null && !l.Unix && cols.Length > 1)
        {
            fmt = Detect(cols[0] + " " + cols[1], out l.Unix, out l.UnixMs);
            if (fmt != null)
            {
                l.Date = 0;
                t = 1;
            }
        }

        if (fmt == null && !l.Unix) return null;
        l.DateFormat = fmt;
        l.Time = t;
        int rest = cols.Length - t - 1;
        if (rest >= 4)
        {
            // fecha;apertura;máximo;mínimo;cierre[;volumen]
            l.Open = t + 1;
            l.High = t + 2;
            l.Low = t + 3;
            l.Close = t + 4;
            if (rest >= 5) l.Volume = t + 5;
        }
        else if (rest >= 2)
        {
            // HistData ticks: fecha,bid,ask[,volumen]
            l.Bid = t + 1;
            l.Ask = t + 2;
        }
        else if (rest == 1)
        {
            l.Last = t + 1;
        }
        else return null;

        return l;
    }

    private static int Decimals(string s)
    {
        s = s.Trim().Trim('"').Replace(',', '.');
        int dot = s.IndexOf('.');
        if (dot < 0 || s.Contains('e') || s.Contains('E')) return 0;
        return s.Length - dot - 1 - (s.Length - s.TrimEnd('0').Length);
    }

    /// <summary>Lee el archivo y guarda cada día (UTC) en la historia de Trading Hub.</summary>
    /// <param name="offset">Zona horaria del archivo (UTC = cero; HistData es Nueva York fija, −5).</param>
    /// <param name="tickSize">Paso de precio; null = el que se deduce de los decimales del archivo.</param>
    public static ImportResult Import(string path, string symbol, string root, TimeSpan offset, decimal? tickSize, IProgress<double>? progress, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        string? first = reader.ReadLine();
        while (first != null && first.Trim().Length == 0) first = reader.ReadLine();
        string? second = reader.ReadLine();
        if (first == null) throw new InvalidDataException("The file is empty");
        var l = Recognize(first, second) ?? throw new InvalidDataException("Unrecognized format: date and price columns are required (bid/ask, last or OHLC)");

        // paso de precio: los decimales de las primeras filas
        decimal tick = tickSize ?? 0;
        var pending = new List<string>();
        if (!l.Header) pending.Add(first);
        if (second != null) pending.Add(second);
        if (tick <= 0)
        {
            int dec = 0;
            var probe = new List<string>(pending);
            for (int i = 0; i < 500 && reader.Peek() >= 0; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                probe.Add(line);
                pending.Add(line);
            }

            foreach (var line in probe)
            {
                var c = line.Split(l.Delimiter);
                foreach (int idx in new[] { l.Price, l.Open, l.High, l.Low, l.Close })
                    if (idx >= 0 && idx < c.Length) dec = Math.Max(dec, Decimals(c[idx]));
            }

            tick = dec <= 0 ? 1 : (decimal)Math.Pow(10, -Math.Min(dec, 6));
        }

        var inst = new Instrument { Symbol = symbol, FeedId = FeedId, Exchange = FeedId, TickSize = tick, VolumeStep = 0.001m, PointValue = 1 };
        long rows = 0, trades = 0;
        int bad = 0, days = 0;
        DateTime? from = null, to = null;
        long lastId = 0;
        decimal lastPrice = 0;
        TradeSide lastSide = TradeSide.Buy;
        var dayTrades = new List<Trade>();
        var dayBars = new List<Kline>();
        DateOnly? current = null;
        long barMs = 0, prevBarMs = 0;

        void Flush()
        {
            if (current is not { } day) return;
            if (l.IsBars && dayBars.Count > 0)
            {
                int minutes = (int)Math.Max(1, barMs / 60_000);
                var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                KlineHistory.Synthesize(dayBars, inst, minutes, new[] { (start, start.AddDays(1).AddMinutes(minutes)) }, dayTrades);
                dayBars.Clear();
            }

            if (dayTrades.Count > 0)
            {
                HistoricalData.WriteDay(root, inst, day, dayTrades);
                trades += dayTrades.Count;
                days++;
                dayTrades.Clear();
            }
        }

        IEnumerable<string> Lines()
        {
            foreach (var p in pending) yield return p;
            string? line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }

        long total = Math.Max(1, stream.Length);
        int need = new[] { l.Time, l.Date, l.Price, l.Open, l.High, l.Low, l.Close }.Max();
        foreach (var line in Lines())
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0) continue;
            var c = line.Split(l.Delimiter);
            if (c.Length <= need)
            {
                bad++;
                continue;
            }

            string when = l.Date >= 0 && l.Date != l.Time ? c[l.Date].Trim() + " " + c[l.Time].Trim() : c[l.Time];
            if (!TryTime(when, l, offset, out var utc))
            {
                bad++;
                continue;
            }

            var day = DateOnly.FromDateTime(utc);
            if (current != day)
            {
                Flush();
                current = day;
            }

            rows++;
            from ??= utc;
            to = utc;
            if (l.IsBars)
            {
                if (!TryNumber(c[l.Open], out var o) || !TryNumber(c[l.High], out var h) || !TryNumber(c[l.Low], out var lo) || !TryNumber(c[l.Close], out var cl))
                {
                    bad++;
                    continue;
                }

                decimal vol = l.Volume >= 0 && l.Volume < c.Length && TryNumber(c[l.Volume], out var v) && v > 0 ? v : 4; // sin volumen: 1 por punto
                long ms = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
                if (prevBarMs > 0 && ms > prevBarMs && (barMs == 0 || ms - prevBarMs < barMs)) barMs = ms - prevBarMs;
                prevBarMs = ms;
                dayBars.Add(new Kline(ms, o, h, lo, cl, vol, 0, false));
            }
            else
            {
                // MetaTrader deja vacío el bid cuando solo cambió el ask: no es un error, no hay cotización nueva
                if (c[l.Price].Trim().Length == 0) continue;
                if (!TryNumber(c[l.Price], out var price) || price <= 0)
                {
                    bad++;
                    continue;
                }

                price = inst.RoundPrice(price);
                // una operación por cotización; el lado sale de si el precio subió o bajó (regla del tick)
                var side = price > lastPrice ? TradeSide.Buy : price < lastPrice ? TradeSide.Sell : lastSide;
                lastPrice = price;
                lastSide = side;
                long id = Math.Max(lastId + 1, new DateTimeOffset(utc).ToUnixTimeMilliseconds() * 8);
                lastId = id;
                dayTrades.Add(new Trade(id, utc, price, 1, side));
            }

            if ((rows & 0x3FFF) == 0) progress?.Report(Math.Clamp(stream.Position / (double)total, 0, 1));
        }

        Flush();
        progress?.Report(1);
        return new ImportResult(l.IsBars ? $"{Math.Max(1, barMs / 60_000)}-minute bars" : "ticks", rows, trades, days, from, to, bad, tick);
    }
}
