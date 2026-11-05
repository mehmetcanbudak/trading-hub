using System.Globalization;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Imported source rows and the quality of the generated price events.</summary>
public sealed record ImportResult(string Kind, long Rows, long Trades, int Days, DateTime? From, DateTime? To, int BadLines, decimal TickSize)
{
    public long MissingVolumeRows { get; init; }
    public string QualitySummary { get; init; } = "";
}

/// <summary>
/// Imports Dukascopy, HistData, MetaTrader and generic date/price or OHLC CSV/TXT history.
/// Quote observations retain their prices and tick-rule side estimates, but never claim executed
/// volume or observed order flow. OHLC rows become explicitly synthetic price paths.
/// </summary>
public static class CsvImport
{
    public const string FeedId = "importado";
    private const string BarEventPrefix = "csv-ohlc:";

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
    private static bool TryTime(string text, Layout l, TimeSpan offset, out DateTime utc, out TimeSpan sourceOffset)
    {
        utc = default;
        sourceOffset = l.Unix ? TimeSpan.Zero : offset;
        text = text.Trim().Trim('"');
        if (l.Unix)
        {
            if (!long.TryParse(text.Split('.')[0], out var n)) return false;
            utc = l.UnixMs ? DateTimeOffset.FromUnixTimeMilliseconds(n).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime;
            return true;
        }

        int gmt = text.IndexOf(" GMT", StringComparison.OrdinalIgnoreCase);
        if (gmt > 0)
        {
            var z = text[(gmt + 4)..].Trim();
            text = text[..gmt];
            if (z.Length >= 5 && int.TryParse(z[1..3], out var hh) && int.TryParse(z[3..5], out var mm))
                sourceOffset = TimeSpan.FromMinutes((hh * 60 + mm) * (z[0] == '-' ? -1 : 1));
        }

        if (l.DateFormat == null || !DateTime.TryParseExact(text, l.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local)) return false;
        utc = DateTime.SpecifyKind(local - sourceOffset, DateTimeKind.Utc);
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
        if (!TryNumber(s, out var value)) return 0;
        value /= 1.0000000000000000000000000000m;
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        return (bits[3] >> 16) & 0xFF;
    }

    private readonly record struct FileShape(int PriceDecimals, long BarTicks, int CalendarMonths)
    {
        public TimeSpan BarLength(DateTime open, TimeSpan offset)
        {
            if (CalendarMonths <= 0) return TimeSpan.FromTicks(BarTicks > 0 ? BarTicks : 4);
            var local = open + offset;
            return local.AddMonths(CalendarMonths) - local;
        }
    }

    private static bool TryBar(string[] columns, Layout layout, DateTime utc, out Kline bar, out bool hasVolume)
    {
        bar = default;
        hasVolume = false;
        if (!TryNumber(columns[layout.Open], out var open) || !TryNumber(columns[layout.High], out var high) ||
            !TryNumber(columns[layout.Low], out var low) || !TryNumber(columns[layout.Close], out var close)) return false;
        decimal volume = 0;
        hasVolume = layout.Volume >= 0 && layout.Volume < columns.Length && TryNumber(columns[layout.Volume], out volume);
        if (hasVolume && volume < 0) return false;
        bar = new Kline(new DateTimeOffset(utc).ToUnixTimeMilliseconds(), open, high, low, close, volume, 0, false);
        return true;
    }

    // Inspect the complete source before writing. A later finer price or shorter interval must not
    // change already-written days, and calendar months cannot be represented by a fixed day count.
    private static FileShape Inspect(string path, Layout layout, TimeSpan offset, bool inferTick, CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        string? line;
        bool first = true, calendar = true;
        DateTime? previousUtc = null, previousLocal = null;
        int decimals = 0, calendarMonths = 0;
        long barTicks = 0;
        int need = new[] { layout.Time, layout.Date, layout.Price, layout.Open, layout.High, layout.Low, layout.Close }.Max();
        int[] prices = layout.IsBars ? new[] { layout.Open, layout.High, layout.Low, layout.Close } : new[] { layout.Price };
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (first) { first = false; if (layout.Header) continue; }
            var columns = line.Split(layout.Delimiter);
            if (columns.Length <= need) continue;
            string when = layout.Date >= 0 && layout.Date != layout.Time
                ? columns[layout.Date].Trim() + " " + columns[layout.Time].Trim() : columns[layout.Time];
            if (!TryTime(when, layout, offset, out var utc, out var sourceOffset)) continue;
            if (layout.IsBars && !TryBar(columns, layout, utc, out _, out _)) continue;
            if (inferTick)
                foreach (int index in prices) decimals = Math.Max(decimals, Decimals(columns[index]));
            if (!layout.IsBars) continue;
            var local = utc + sourceOffset;
            if (previousUtc is { } before && previousLocal is { } beforeLocal)
            {
                long difference = (utc - before).Ticks;
                if (difference > 0 && (barTicks == 0 || difference < barTicks)) barTicks = difference;
                int months = (local.Year - beforeLocal.Year) * 12 + local.Month - beforeLocal.Month;
                bool monthOpening = months > 0 && beforeLocal.Day == 1 && local.Day == 1 && beforeLocal.TimeOfDay == local.TimeOfDay;
                if (!monthOpening || (calendarMonths > 0 && calendarMonths != months)) calendar = false;
                if (monthOpening && calendarMonths == 0) calendarMonths = months;
            }
            previousUtc = utc;
            previousLocal = local;
        }
        return new FileShape(decimals, barTicks, calendar ? calendarMonths : 0);
    }

    private readonly record struct BarPlacement(long OpenTicks, long LengthTicks)
    {
        public long PointTime(int index) => OpenTicks + LengthTicks / 4 * index;
    }

    private static Instrument StorageInstrument(Instrument instrument, string path)
    {
        return TickFile.ReadTickSize(path) is { } storedTick && storedTick > 0 && storedTick < instrument.TickSize
            ? new Instrument { Symbol = instrument.Symbol, FeedId = instrument.FeedId, Exchange = instrument.Exchange,
                TickSize = storedTick, VolumeStep = instrument.VolumeStep, PointValue = instrument.PointValue }
            : instrument;
    }

    // Corrections can move a source point to another UTC day. Day-local merging cannot retire its old
    // location, so reconcile only the bars present in this import against the pre-existing source days.
    private static decimal RemoveObsoleteBarPoints(string root, Instrument instrument, IReadOnlyList<DateOnly> previousDays,
        Dictionary<long, BarPlacement> placements, HashSet<DateOnly> archiveRefresh, CancellationToken ct)
    {
        decimal tick = instrument.TickSize;
        string sourceIdentity = instrument.Key;
        Predicate<Trade> isObsolete = trade =>
        {
            if (trade.Id >= 0 || trade.Provenance.SourceIdentity != sourceIdentity ||
                (trade.Provenance.Quality & DataQuality.Synthetic) == 0) return false;
            long encoded = trade.Id - KlineHistory.SyntheticIdBase;
            if (encoded < 0 || !placements.TryGetValue(encoded / 4, out var placement)) return false;
            var eventId = trade.Provenance.SourceEventId;
            bool legacy = string.IsNullOrEmpty(eventId);
            if (!legacy && (!eventId!.StartsWith(BarEventPrefix, StringComparison.Ordinal) ||
                !long.TryParse(eventId.AsSpan(BarEventPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long eventNumber) ||
                eventNumber != trade.Id)) return false;
            return legacy || trade.Time.Ticks != placement.PointTime((int)(encoded % 4));
        };
        foreach (var day in previousDays)
        {
            ct.ThrowIfCancellationRequested();
            var existing = TickFile.ExistingDayPath(root, instrument.FeedId, instrument.Symbol, day);
            if (existing == null) continue;
            var rows = HistorySources.ReadSourceFile(existing, instrument.FeedId, instrument.Symbol, requireComplete: true);
            int removed = rows.RemoveAll(isObsolete);
            if (removed == 0) continue;
            if (rows.Count == 0)
            {
                HistoricalData.DeleteDays(root, instrument.FeedId, instrument.Symbol, new[] { day });
                continue;
            }
            var storedInstrument = StorageInstrument(instrument, existing);
            tick = Math.Min(tick, storedInstrument.TickSize);
            var path = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, day);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new TickFile.Writer(temporary, storedInstrument, includeProvenance: true))
                    foreach (var trade in rows) writer.Write(trade);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            if (File.Exists(TickArchive.PathFor(path))) archiveRefresh.Add(day);
        }
        return tick;
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

        decimal tick = tickSize ?? 0;
        var pending = new List<string>();
        if (!l.Header) pending.Add(first);
        if (second != null) pending.Add(second);
        var shape = tick <= 0 || l.IsBars ? Inspect(path, l, offset, tick <= 0, ct) : default;
        if (tick <= 0)
        {
            tick = 1m;
            for (int i = 0; i < shape.PriceDecimals; i++) tick /= 10m;
        }
        using var lease = TickFile.TryLock(root, FeedId, symbol, failOnNonSharingError: true)
            ?? throw new IOException("Another writer owns this imported instrument.");
        IReadOnlyList<DateOnly> previousDays = l.IsBars ? HistorySources.AvailableDays(root, FeedId, symbol) : Array.Empty<DateOnly>();
        var placements = previousDays.Count > 0 ? new Dictionary<long, BarPlacement>() : null;
        var archiveRefresh = new HashSet<DateOnly>();

        var inst = new Instrument { Symbol = symbol, FeedId = FeedId, Exchange = FeedId, TickSize = tick, VolumeStep = 0.001m, PointValue = 1 };
        string sourceIdentity = inst.Key;
        var quoteProvenance = new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial,
            SourceIdentity: sourceIdentity, ObservedTradeCount: 0);
        long rows = 0, trades = 0, missingVolume = 0;
        int bad = 0;
        var writtenDays = new HashSet<DateOnly>();
        DateTime? from = null, to = null;
        long lastId = 0;
        decimal lastPrice = 0;
        TradeSide lastSide = TradeSide.Buy;
        var dayTrades = new List<Trade>();
        var barPoints = new List<Trade>(4);
        Kline? pendingBar = null;
        DateTime pendingOpen = default;
        TimeSpan pendingOffset = default;
        DateOnly? current = null;

        void Flush()
        {
            if (current is not { } day || dayTrades.Count == 0) return;
            var existing = TickFile.ExistingDayPath(root, inst.FeedId, inst.Symbol, day);
            var storedInstrument = existing == null ? inst : StorageInstrument(inst, existing);
            tick = Math.Min(tick, storedInstrument.TickSize);
            HistoricalData.WriteDay(root, storedInstrument, day, dayTrades);
            if (File.Exists(TickArchive.PathFor(TickFile.DayPath(root, inst.FeedId, inst.Symbol, day)))) archiveRefresh.Add(day);
            trades += dayTrades.Count;
            writtenDays.Add(day);
            dayTrades.Clear();
        }

        void AddPoint(Trade trade)
        {
            var day = DateOnly.FromDateTime(trade.Time);
            if (current != day)
            {
                Flush();
                current = day;
            }
            dayTrades.Add(trade);
        }

        void EmitBar(Kline bar, DateTime open, TimeSpan sourceOffset, DateTime? nextOpen = null)
        {
            var length = shape.CalendarMonths > 0 && nextOpen is { } end && end > open ? end - open : shape.BarLength(open, sourceOffset);
            var provenance = new TradeProvenance(DataQuality.Synthetic | DataQuality.InferredSide | DataQuality.Partial,
                SourceIdentity: sourceIdentity, ObservedTradeCount: 0, HasKnownAvailability: false);
            barPoints.Clear();
            long generatedId = 0;
            HistoryBuilder.Synthesize(open, length, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, inst, ref generatedId, barPoints, provenance);
            long baseId = KlineHistory.SyntheticIdBase + bar.OpenTimeMs * 4;
            if (placements != null) placements[bar.OpenTimeMs] = new BarPlacement(open.Ticks, shape.BarTicks > 0 ? length.Ticks : 0);
            decimal distributed = 0;
            for (int i = 0; i < barPoints.Count; i++)
            {
                var point = barPoints[i];
                decimal volume = i == barPoints.Count - 1 ? bar.Volume - distributed : point.Volume;
                distributed += volume;
                // A lone OHLC row has no demonstrated duration. Keep its path at the source timestamp
                // with unknown resolution/availability instead of inventing a one-minute source bar.
                var pointProvenance = shape.BarTicks > 0 ? point.Provenance
                    : point.Provenance with { ResolutionSeconds = 0, ResolutionOffsetSeconds = 0 };
                long id = baseId + i;
                pointProvenance = pointProvenance with { SourceEventId = BarEventPrefix + id.ToString(CultureInfo.InvariantCulture) };
                AddPoint(new Trade(id, shape.BarTicks > 0 ? point.Time : open, point.Price, volume, point.Side, pointProvenance));
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
            if (!TryTime(when, l, offset, out var utc, out var sourceOffset))
            {
                bad++;
                continue;
            }


            rows++;
            from ??= utc;
            to = utc;
            if (l.IsBars)
            {
                if (!TryBar(c, l, utc, out var bar, out bool hasVolume))
                {
                    bad++;
                    continue;
                }
                if (!hasVolume) missingVolume++;
                if (pendingBar is { } previous) EmitBar(previous, pendingOpen, pendingOffset, utc);
                pendingBar = bar;
                pendingOpen = utc;
                pendingOffset = sourceOffset;
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
                var side = HistoryBuilder.TickRule(price, ref lastPrice, ref lastSide);
                long id = Math.Max(lastId + 1, new DateTimeOffset(utc).ToUnixTimeMilliseconds() * 8);
                lastId = id;
                AddPoint(new Trade(id, utc, price, 0, side, quoteProvenance));
            }

            if ((rows & 0x3FFF) == 0) progress?.Report(Math.Clamp(stream.Position / (double)total, 0, 1));
        }
        if (pendingBar is { } lastBar) EmitBar(lastBar, pendingOpen, pendingOffset);

        Flush();
        if (placements is { Count: > 0 })
            tick = Math.Min(tick, RemoveObsoleteBarPoints(root, inst, previousDays, placements, archiveRefresh, ct));
        foreach (var day in archiveRefresh)
        {
            ct.ThrowIfCancellationRequested();
            var archiveRawPath = TickFile.DayPath(root, inst.FeedId, inst.Symbol, day);
            if (File.Exists(archiveRawPath)) TickArchive.ArchiveDay(root, StorageInstrument(inst, archiveRawPath), day, ct);
        }
        progress?.Report(1);
        return new ImportResult(l.IsBars ? $"{Math.Max(1, shape.BarTicks / TimeSpan.TicksPerMinute)}-minute bars" : "ticks",
            rows, trades, writtenDays.Count, from, to, bad, tick)
        {
            MissingVolumeRows = missingVolume,
            QualitySummary = l.IsBars
                ? "Synthetic OHLC paths and inferred sides; no observed trades or footprint; " +
                    (shape.BarTicks == 0 ? "interval unknown" : shape.CalendarMonths > 0 ? "calendar-month spacing inferred" : "interval inferred") +
                    ", source availability unknown" +
                    (missingVolume > 0 ? $"; {missingVolume} row(s) without reported volume stored as zero" : "; reported volume retained, including zero")
                : "Quote observations; executed volume unknown (stored as zero), inferred side, no observed trades or footprint",
        };
    }
}
