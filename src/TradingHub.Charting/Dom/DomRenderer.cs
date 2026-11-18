using System.Globalization;
using SkiaSharp;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Rendering.Context;
using TradingHub.Trading;
using TradingHub.Rendering;

namespace TradingHub.Charting.Dom;

/// <summary>
/// Libro de órdenes vertical (DOM / escalera de precios) al estilo SmartDOM: órdenes propias,
/// bid, precio, ask y volumen operado en la sesión por precio. Se opera haciendo clic (if DOM trading is on).
/// Optional book-reading features: auto tick, quantity/notional unit, cumulative totals, recorded book changes,
/// recently traded volume, distance marks, a stats bar and a hover mirror.
/// </summary>
public sealed class DomModel
{
    // Traded volume per instrument tick: rows are built when drawing, so changing the step never rescans trades.
    private readonly Dictionary<long, (decimal Bid, decimal Ask)> _sessionVolume = new();
    private int _processed;
    private DateOnly _sessionDate;
    private DateTime _changesAt = DateTime.MinValue;
    private int _changesWindow;

    // Recently traded volume per tick: a window sliding over the trade store (each print enters and leaves it once).
    private readonly Dictionary<long, (decimal Bid, decimal Ask)> _recent = new();
    private int _recentFrom, _recentTo, _recentWindow;
    private Guid _recentStore;
    private long _recentCorrection = -1;
    private DateTime _recentNow = DateTime.MinValue;

    public DomModel(InstrumentData data, DomSettings? settings = null)
    {
        Data = data;
        Settings = settings ?? new DomSettings();
        AutoCenter = Settings.AutoCenter;
        Clock = () => data.Feed is TradingHub.Feeds.ReplayFeed replay ? replay.Clock : data.Feed.Now;
        data.TradesAppended += (_, _) => UpdateVolume();
        data.HistoryLoaded += () =>
        {
            _sessionVolume.Clear();
            _processed = 0;
            UpdateVolume();
        };
        UpdateVolume();
    }

    public InstrumentData Data { get; }
    public Instrument Instrument => Data.Instrument;
    public ITradingAccount? Broker { get; set; }

    /// <summary>Stop/objetivos de la cuenta (se pintan distinto).</summary>
    public BracketManager? Brackets { get; set; }

    /// <summary>Configuración visual y de comportamiento (compartida entre DOMs).</summary>
    public DomSettings Settings { get; set; }

    /// <summary>Ticks del instrumento por fila del DOM (chosen by the renderer with auto tick).</summary>
    public int Step { get; set; } = 1;

    /// <summary>Precio (en ticks) de la fila central cuando no está centrado automáticamente.</summary>
    public long CenterTicks { get; set; }

    /// <summary>Si sigue al precio (se apaga al desplazar con la rueda).</summary>
    public bool AutoCenter { get; set; }

    public float RowHeight => Math.Clamp(Settings.RowHeight, 10, 60);
    public decimal OrderQuantity { get; set; } = 1;

    /// <summary>Orden que se está arrastrando en el DOM y precio de destino.</summary>
    public Order? DragOrder { get; set; }
    public decimal DragPrice { get; set; }

    public SessionSettings Session { get; set; } = SessionSettings.CryptoUtc;

    /// <summary>Clock for the book-change and recent-trade windows (the feed's clock, so replay and fixtures stay causal).</summary>
    public Func<DateTime> Clock { get; set; }

    /// <summary>Volumen operado en la sesión por tick (bid = vendedores agresivos, ask = compradores agresivos).</summary>
    public IReadOnlyDictionary<long, (decimal Bid, decimal Ask)> SessionVolume => _sessionVolume;

    /// <summary>Recorded book changes in the window, at exact ticks (null before the first refresh).</summary>
    public BookChangesResult? RecentChanges { get; private set; }

    /// <summary>Short reason the book changes are unavailable or partial; null when complete.</summary>
    public string? RecentChangesStatus { get; private set; }

    public long RowOf(long ticks) => CandleSeries.FloorDiv(ticks, Step);

    private void UpdateVolume()
    {
        var store = Data.Trades;
        if (store.Count == 0) return;
        var today = Session.SessionDate(store.LastTime);
        if (today != _sessionDate)
        {
            _sessionDate = today;
            _sessionVolume.Clear();
            _processed = store.LowerBound(Session.SessionStartUtc(today));
        }

        for (int i = _processed; i < store.Count; i++)
        {
            if (store.GetId(i) < 0) continue; // historia en velas: no son operaciones reales
            long tick = store.GetPriceTicks(i);
            _sessionVolume.TryGetValue(tick, out var v);
            var vol = store.GetVolume(i);
            _sessionVolume[tick] = store.GetSide(i) == TradeSide.Sell ? (v.Bid + vol, v.Ask) : (v.Bid, v.Ask + vol);
        }

        _processed = store.Count;
    }

    public void ResetVolume()
    {
        _sessionVolume.Clear();
        _processed = 0;
        _sessionDate = default;
        UpdateVolume();
    }

    /// <summary>Clock skew allowed between trade and book timestamps when matching removed size to trades.</summary>
    public static readonly TimeSpan TradeSkew = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Book changes over the window from the opt-in depth recording (no second recorder): consecutive received
    /// snapshots inside the window are compared and removals are matched to observed trades. Refreshed at most once a second.
    /// </summary>
    public BookChangesResult RefreshChanges(DateTime now, bool force = false)
    {
        int seconds = Math.Clamp(Settings.ChangeWindowSeconds, 5, 3600);
        if (!force && RecentChanges != null && seconds == _changesWindow && now >= _changesAt && now - _changesAt < TimeSpan.FromSeconds(1))
            return RecentChanges;
        _changesAt = now;
        _changesWindow = seconds;
        var history = Data.DepthHistory;
        var from = now - TimeSpan.FromSeconds(seconds);
        var query = history.Query(from, now);
        if (query.Samples.Count == 0)
        {
            RecentChanges = BookChangesResult.Empty;
            RecentChangesStatus = history.Status.RecordingState == DepthRecordingState.Recording
                ? "No recorded depth in the window" : "Depth recording is off";
            return RecentChanges;
        }
        var trades = new List<BookTrade>();
        var store = Data.Trades;
        for (int i = store.LowerBound(from - TradeSkew); i < store.Count; i++)
        {
            var time = store.GetTime(i);
            if (time > now + TradeSkew) break;
            if (store.GetId(i) < 0) continue;
            trades.Add(new BookTrade(time, store.GetPriceTicks(i), store.GetVolume(i), store.GetSide(i)));
        }
        // Preceding is outside the window: only snapshots received inside it are compared.
        RecentChanges = BookChanges.Compute(query.Samples, null, new TimedBookTrades(trades, TradeSkew),
            new BookChangeOptions { MaxAge = history.Options.MaxAge });
        RecentChangesStatus = !RecentChanges.IsAvailable ? "No comparable snapshots"
            : RecentChanges.SkippedTransitions > 0 ? "Partial: gaps or stale intervals skipped" : null;
        return RecentChanges;
    }

    /// <summary>
    /// Recently traded volume per tick in the window (observed prints only). The window slides over the trade store: each
    /// call only adds the prints that entered it and removes those that left, never rescanning the whole window.
    /// </summary>
    public IReadOnlyDictionary<long, (decimal Bid, decimal Ask)> RecentTraded(DateTime now)
    {
        var store = Data.Trades;
        int seconds = Math.Clamp(Settings.ChangeWindowSeconds, 5, 3600);
        var since = now - TimeSpan.FromSeconds(seconds);
        if (seconds != _recentWindow || now < _recentNow || store.Identity != _recentStore || store.CorrectionRevision != _recentCorrection || store.Count < _recentTo)
        {
            _recent.Clear();
            (_recentWindow, _recentStore, _recentCorrection) = (seconds, store.Identity, store.CorrectionRevision);
            _recentFrom = _recentTo = store.LowerBound(since);
        }

        _recentNow = now;
        for (; _recentTo < store.Count && store.GetTime(_recentTo) <= now; _recentTo++) AddRecent(store, _recentTo, 1);
        for (; _recentFrom < _recentTo && store.GetTime(_recentFrom) < since; _recentFrom++) AddRecent(store, _recentFrom, -1);
        return _recent;
    }

    private void AddRecent(TradeStore store, int i, int sign)
    {
        if (store.GetId(i) < 0) return; // candle-derived history: not observed prints
        long tick = store.GetPriceTicks(i);
        _recent.TryGetValue(tick, out var v);
        decimal vol = store.GetVolume(i) * sign;
        v = store.GetSide(i) == TradeSide.Sell ? (v.Bid + vol, v.Ask) : (v.Bid, v.Ask + vol);
        if (v.Bid == 0 && v.Ask == 0) _recent.Remove(tick);
        else _recent[tick] = v;
    }

    /// <summary>
    /// Auto tick: the visible rows span ± the chosen range around the price, but never more than the received book
    /// (<paramref name="bookHalfSpan"/>: price distance to the farthest level on the shorter side; 0 = unknown).
    /// </summary>
    public static int AutoStepFor(decimal price, decimal tick, int rows, double rangePercent, decimal bookHalfSpan = 0)
    {
        if (price <= 0 || tick <= 0 || rows <= 0) return 1;
        double half = (double)price * Math.Clamp(rangePercent, 0.01, 50) / 100;
        if (bookHalfSpan > 0) half = Math.Min(half, (double)bookHalfSpan);
        long raw = (long)Math.Ceiling(half * 2 / rows / (double)tick);
        return Math.Max(1, CandleSeries.NiceStep(Math.Max(1, raw)));
    }
}

/// <summary>Dibuja el DOM y resuelve los clics.</summary>
public sealed class DomRenderer
{
    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly List<(DomColumn Col, float X, float W)> _cols = new();

    private float _width, _height, _headerH, _statsH, _rowH = 18;
    private long _topRow;
    private int _rows;

    private ChartTheme? _theme;

    /// <summary>Colores (por defecto, la base del modo oscuro/claro de la app).</summary>
    public ChartTheme Theme
    {
        get => _theme ?? ChartTheme.Base;
        set => _theme = value;
    }

    /// <summary>Alto del encabezado (stats bar + column labels) en el último dibujo.</summary>
    public float HeaderHeight => _headerH;

    /// <summary>Fila de arriba en el último dibujo (si cambia, el DOM se movió).</summary>
    public long TopRow => _topRow;

    /// <summary>Rótulos de cada columna, del más largo al más corto.</summary>
    public static string[] HeaderLabels(DomColumn c) => c switch
    {
        DomColumn.BuyOrders or DomColumn.SellOrders => new[] { "Orders", "Ord.", "O" },
        DomColumn.Bid => new[] { "Bid", "B" },
        DomColumn.Price => new[] { "Price", "Price", "$" },
        DomColumn.Ask => new[] { "Ask", "A" },
        DomColumn.Volume => new[] { "Volume", "Vol.", "V" },
        DomColumn.Delta => new[] { "Delta", "Δ" },
        DomColumn.TradedBid => new[] { "Vol. bid", "V.bid", "Vb" },
        DomColumn.TradedAsk => new[] { "Vol. ask", "V.ask", "Va" },
        DomColumn.Size => new[] { "Size", "Sz.", "#" },
        DomColumn.Total => new[] { "Total", "Tot.", "Σ" },
        DomColumn.Changes => new[] { "Changes", "± book", "±" },
        DomColumn.Recent => new[] { "Recent", "Rec.", "R" },
        _ => new[] { c.ToString() },
    };

    /// <summary>El rótulo más largo que entra en el ancho (translated), o null si no entra ninguno.</summary>
    public static string? FitLabel(DomColumn c, float width, Func<string, float> measure)
    {
        foreach (var s in HeaderLabels(c))
        {
            string label = UiText.Tr(s);
            if (measure(label) <= width - 6) return label;
        }
        return null;
    }

    /// <summary>Short text of a size in the chosen unit (notional = size × price × point value).</summary>
    public static string Amount(decimal size, decimal price, DomUnit unit, int decimals, decimal pointValue = 1)
    {
        if (unit != DomUnit.Notional) return VolumeFormat.Compact(size, decimals);
        decimal notional = size * price * pointValue, a = Math.Abs(notional);
        var c = CultureInfo.InvariantCulture;
        string s = a >= 1_000_000_000 ? (a / 1_000_000_000).ToString("0.##", c) + "B"
            : a >= 1_000_000 ? (a / 1_000_000).ToString("0.##", c) + "M"
            : a >= 1_000 ? (a / 1_000).ToString("0.#", c) + "K"
            : a.ToString("0", c);
        return (notional < 0 ? "-" : "") + s;
    }

    /// <summary>Reparte el ancho; la columna de precio nunca queda más angosta que el precio (si no, se corta).</summary>
    private void ComputeColumns(DomSettings s, float width, float priceMin = 0)
    {
        _cols.Clear();
        var visible = s.Columns.Where(c => c.Visible).ToList();
        if (visible.Count == 0) visible.Add(new DomColumnSetting { Column = DomColumn.Price });
        double total = visible.Sum(c => Math.Max(0.1, c.Width));
        var price = visible.FirstOrDefault(c => c.Column == DomColumn.Price);
        float priceW = price != null ? (float)(width * Math.Max(0.1, price.Width) / total) : 0;
        bool reserve = price != null && visible.Count > 1 && priceW < priceMin && width > priceMin * 1.3f;
        double others = total - (price != null ? Math.Max(0.1, price.Width) : 0);
        float x = 0;
        foreach (var c in visible)
        {
            float w = !reserve
                ? (float)(width * Math.Max(0.1, c.Width) / total)
                : c.Column == DomColumn.Price ? priceMin : (float)((width - priceMin) * Math.Max(0.1, c.Width) / Math.Max(0.1, others));
            _cols.Add((c.Column, x, w));
            x += w;
        }
    }

    private bool TryColumn(DomColumn c, out float x, out float w)
    {
        foreach (var (col, cx, cw) in _cols)
        {
            if (col != c) continue;
            x = cx;
            w = cw;
            return true;
        }

        x = w = 0;
        return false;
    }

    private bool Has(DomColumn c) => _cols.Any(x => x.Col == c);

    /// <summary>Columna donde se dibujan las órdenes propias de un lado (si su columna está oculta, usa otra).</summary>
    public DomColumn OrdersColumn(OrderDirections dir)
    {
        var order = dir == OrderDirections.Buy
            ? new[] { DomColumn.BuyOrders, DomColumn.Bid, DomColumn.Price }
            : new[] { DomColumn.SellOrders, DomColumn.Ask, DomColumn.Price };
        foreach (var c in order)
            if (Has(c)) return c;
        return _cols.Count > 0 ? _cols[0].Col : DomColumn.Price;
    }

    private static SKColor C(System.Drawing.Color c) => new(c.R, c.G, c.B, c.A);

    public void Render(SKCanvas canvas, DomModel dom, float width, float height, float? mouseY = null)
    {
        var s = dom.Settings;
        _width = width;
        _height = height;
        _rowH = dom.RowHeight;
        var t = Theme;
        var inst = dom.Instrument;
        var book = dom.Data.Depth;
        var unit = s.Unit;
        decimal pointValue = inst.PointValue > 0 ? inst.PointValue : 1;

        // la letra nunca más alta que la fila
        float size = Math.Min(Math.Clamp(s.FontSize, 7, 24), _rowH - 3);
        var font = RenderContext.GetFont(t.FontFamily, size, false, false);
        var bold = RenderContext.GetFont(t.FontFamily, size, true, false);
        decimal last = dom.Data.LastPrice > 0 ? dom.Data.LastPrice : book.HasBid ? inst.FromTicks(book.BestBidTicks) : 0;
        decimal sample = last > 0 ? last : inst.TickSize * 100_000;
        ComputeColumns(s, width, RenderContext.MeasureWidth(bold, inst.FormatPrice(sample)) + 10);
        var small = RenderContext.GetFont(t.FontFamily, Math.Max(7, size - 1.5f), false, false);
        var header = RenderContext.GetFont(t.FontFamily, Math.Clamp(s.FontSize - 1.5f, 8, 14), false, false);
        _statsH = s.ShowStats ? MathF.Ceiling(header.Spacing * 2 + 10) : 0;
        _headerH = _statsH + (s.ShowHeader ? MathF.Ceiling(header.Spacing + 9) : 0);

        _fill.Color = t.BackgroundTop;
        canvas.DrawRect(0, 0, width, height, _fill);

        _rows = Math.Max(1, (int)((height - _headerH) / _rowH));
        int drawRows = _rows + 1; // fila parcial abajo

        // auto tick: rows span ± the chosen range, never wider than the received book's shorter side
        if (s.AutoStep && last > 0)
        {
            decimal reach = 0;
            if (book.HasBid && book.HasAsk)
                reach = Math.Min(last - inst.FromTicks(book.Bids[^1].PriceTicks), inst.FromTicks(book.Asks[^1].PriceTicks) - last);
            dom.Step = DomModel.AutoStepFor(last, inst.TickSize, _rows, s.AutoRangePercent, Math.Max(reach, inst.TickSize * 10));
        }

        long lastTicks = dom.Data.Trades.Count > 0 ? dom.Data.Trades.GetPriceTicks(dom.Data.Trades.Count - 1) : (book.HasBid ? book.BestBidTicks : 0);
        long lastRow = dom.RowOf(lastTicks);
        long centerRow = dom.RowOf(dom.CenterTicks);
        // recentra si el precio se va del tercio central
        if (dom.CenterTicks == 0 || (dom.AutoCenter && Math.Abs(lastRow - centerRow) > _rows / 3)) dom.CenterTicks = lastRow * dom.Step;

        long center = dom.RowOf(dom.CenterTicks);
        _topRow = center + _rows / 2;
        long bottomRow = _topRow - drawRows + 1;

        // libro por fila
        var bids = new Dictionary<long, decimal>();
        var asks = new Dictionary<long, decimal>();
        foreach (var l in book.Bids) bids[dom.RowOf(l.PriceTicks)] = bids.GetValueOrDefault(dom.RowOf(l.PriceTicks)) + l.Size;
        foreach (var l in book.Asks) asks[dom.RowOf(l.PriceTicks)] = asks.GetValueOrDefault(dom.RowOf(l.PriceTicks)) + l.Size;
        long bestBidRow = book.HasBid ? dom.RowOf(book.BestBidTicks) : long.MinValue;
        long bestAskRow = book.HasAsk ? dom.RowOf(book.BestAskTicks) : long.MaxValue;
        decimal mid = book.HasBid && book.HasAsk ? (inst.FromTicks(book.BestBidTicks) + inst.FromTicks(book.BestAskTicks)) / 2 : last;

        // session volume per row (stored per tick): the visible ticks are looked up, unless the session traded fewer ticks
        var session = new Dictionary<long, (decimal Bid, decimal Ask)>();
        var traded = dom.SessionVolume;
        void AddSession(long tick, (decimal Bid, decimal Ask) v)
        {
            long r = dom.RowOf(tick);
            if (r < bottomRow || r > _topRow) return;
            var cur = session.GetValueOrDefault(r);
            session[r] = (cur.Bid + v.Bid, cur.Ask + v.Ask);
        }

        if ((_topRow - bottomRow + 1) * dom.Step <= traded.Count)
        {
            for (long tick = bottomRow * dom.Step, end = (_topRow + 1) * dom.Step; tick < end; tick++)
                if (traded.TryGetValue(tick, out var v)) AddSession(tick, v);
        }
        else
        {
            foreach (var (tick, v) in traded) AddSession(tick, v);
        }

        // optional columns: computed only when shown
        var cumBid = new Dictionary<long, decimal>();
        var cumAsk = new Dictionary<long, decimal>();
        if (Has(DomColumn.Total))
        {
            decimal acc = 0;
            foreach (var r in bids.Keys.Where(r => r <= bestBidRow).OrderByDescending(r => r)) cumBid[r] = acc += bids[r];
            acc = 0;
            foreach (var r in asks.Keys.Where(r => r >= bestAskRow).OrderBy(r => r)) cumAsk[r] = acc += asks[r];
        }

        var now = dom.Clock();
        var change = new Dictionary<long, decimal>();
        if (Has(DomColumn.Changes))
        {
            var changes = dom.RefreshChanges(now);
            foreach (var (tick, c) in changes.Levels)
            {
                long r = dom.RowOf(tick);
                change[r] = change.GetValueOrDefault(r) + c.Net;
            }
            foreach (var r in change.Where(p => p.Value == 0).Select(p => p.Key).ToList()) change.Remove(r);
        }

        var recent = new Dictionary<long, (decimal Bid, decimal Ask)>();
        if (Has(DomColumn.Recent))
            foreach (var (tick, v) in dom.RecentTraded(now))
            {
                long r = dom.RowOf(tick);
                var cur = recent.GetValueOrDefault(r);
                recent[r] = (cur.Bid + v.Bid, cur.Ask + v.Ask);
            }

        decimal PriceOf(long r) => r * dom.Step * inst.TickSize;
        decimal U(decimal amount, long r) => unit == DomUnit.Notional ? amount * PriceOf(r) * pointValue : amount;
        decimal BookAt(long r) => r >= bestAskRow ? asks.GetValueOrDefault(r) : r <= bestBidRow ? bids.GetValueOrDefault(r) : 0;

        decimal maxBook = 0, maxVol = 0, maxDelta = 0, maxSide = 0, maxCum = 0, maxChange = 0, maxRecent = 0;
        decimal rawBook = 0, rawVol = 0;
        for (long r = bottomRow; r <= _topRow; r++)
        {
            decimal side = Math.Max(bids.GetValueOrDefault(r), asks.GetValueOrDefault(r));
            rawBook = Math.Max(rawBook, side);
            maxBook = Math.Max(maxBook, U(side, r));
            maxCum = Math.Max(maxCum, U(Math.Max(cumBid.GetValueOrDefault(r), cumAsk.GetValueOrDefault(r)), r));
            if (change.TryGetValue(r, out var ch)) maxChange = Math.Max(maxChange, Math.Abs(U(ch, r)));
            if (recent.TryGetValue(r, out var rc)) maxRecent = Math.Max(maxRecent, U(rc.Bid + rc.Ask, r));
            if (!session.TryGetValue(r, out var sv)) continue;
            rawVol = Math.Max(rawVol, sv.Bid + sv.Ask);
            maxVol = Math.Max(maxVol, U(sv.Bid + sv.Ask, r));
            maxDelta = Math.Max(maxDelta, Math.Abs(U(sv.Ask - sv.Bid, r)));
            maxSide = Math.Max(maxSide, U(Math.Max(sv.Bid, sv.Ask), r));
        }

        // highlight: a fixed size (in the chosen unit), optionally the largest visible sizes, otherwise none
        decimal largeAt = s.LargeSize > 0 ? s.LargeSize : decimal.MaxValue;
        if (s.LargeSize <= 0 && s.AutoLargeSize)
        {
            var sizes = new List<decimal>();
            for (long r = bottomRow; r <= _topRow; r++)
            {
                decimal v = U(BookAt(r), r);
                if (v > 0) sizes.Add(v);
            }
            sizes.Sort();
            if (sizes.Count >= 5) largeAt = Math.Max(sizes[^1] * 0.85m, sizes[sizes.Count / 2] * 3);
        }

        int bookDec = FootprintRendererShim.Decimals(rawBook, inst.VolumeDecimals);
        int volDec = FootprintRendererShim.Decimals(rawVol, inst.VolumeDecimals);
        int hoverRow = mouseY is float my && my > _headerH ? (int)((my - _headerH) / _rowH) : -1;

        // distance marks: the row nearest each percentage of the mid price, on both sides
        var marks = new Dictionary<long, string>();
        if (mid > 0)
            foreach (var pct in s.Marks())
            {
                string label = pct.ToString("0.##", CultureInfo.InvariantCulture) + "%";
                marks.TryAdd(dom.RowOf(inst.ToTicks(mid * (1 + (decimal)pct / 100))), label);
                marks.TryAdd(dom.RowOf(inst.ToTicks(mid * (1 - (decimal)pct / 100))), label);
            }

        // fondos de fila
        var bidBg = C(s.BidBackground);
        var askBg = C(s.AskBackground);
        var spreadBg = C(s.SpreadBackground);
        for (int i = 0; i < drawRows; i++)
        {
            long r = _topRow - i;
            float y = _headerH + i * _rowH;
            _fill.Color = r >= bestAskRow ? askBg : r <= bestBidRow ? bidBg : spreadBg;
            canvas.DrawRect(0, y, width, _rowH, _fill);
            if (i != hoverRow) continue;
            _fill.Color = new SKColor(255, 255, 255, 18);
            canvas.DrawRect(0, y, width, _rowH, _fill);
        }

        var bidColor = C(s.BidColor);
        var askColor = C(s.AskColor);
        var buyVol = C(s.BuyVolumeColor);
        var sellVol = C(s.SellVolumeColor);
        var lastColor = C(s.LastPriceColor);
        var priceText = C(s.PriceText);
        var pos = dom.Broker?.Position;
        long posRow = pos != null && pos.Volume != 0 ? dom.RowOf(inst.ToTicks(pos.AveragePrice)) : long.MinValue;
        var cell = new CellStyle(s, bookDec, font, bold, lastColor, largeAt, unit, pointValue);

        foreach (var (col, x, w) in _cols)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(x, _headerH, x + w, height));
            for (int i = 0; i < drawRows; i++)
            {
                long r = _topRow - i;
                float y = _headerH + i * _rowH;
                decimal px = PriceOf(r);
                switch (col)
                {
                    case DomColumn.Bid when r <= bestBidRow && bids.TryGetValue(r, out var bsz) && bsz > 0:
                        BookCell(canvas, x, y, w, bsz, px, maxBook, bidColor, r == bestBidRow, rightAligned: true, cell, null);
                        break;
                    case DomColumn.Ask when r >= bestAskRow && asks.TryGetValue(r, out var asz) && asz > 0:
                        BookCell(canvas, x, y, w, asz, px, maxBook, askColor, r == bestAskRow, rightAligned: false, cell, null);
                        break;
                    case DomColumn.Size:
                    {
                        decimal sz = BookAt(r);
                        if (sz > 0)
                            BookCell(canvas, x, y, w, sz, px, maxBook, r >= bestAskRow ? askColor : bidColor, r == bestAskRow || r == bestBidRow, rightAligned: true, cell, marks.GetValueOrDefault(r));
                        else if (marks.TryGetValue(r, out var mark)) Text(canvas, mark, x, y, w, small, t.TextDim, -1);
                        break;
                    }
                    case DomColumn.Total:
                    {
                        decimal cum = r >= bestAskRow ? cumAsk.GetValueOrDefault(r) : r <= bestBidRow ? cumBid.GetValueOrDefault(r) : 0;
                        if (cum <= 0 || maxCum <= 0) break;
                        float bw = (float)(U(cum, r) / maxCum) * (w - 4);
                        _fill.Color = (r >= bestAskRow ? askColor : bidColor).WithAlpha(60);
                        canvas.DrawRect(x + w - 2 - bw, y + 2, bw, _rowH - 4, _fill);
                        Text(canvas, Amount(cum, px, unit, bookDec, pointValue), x, y, w, small, t.Text, 1);
                        break;
                    }
                    case DomColumn.Changes when change.TryGetValue(r, out var ch) && maxChange > 0:
                    {
                        float bw = (float)(Math.Abs(U(ch, r)) / maxChange) * (w / 2 - 2);
                        float cx = x + w / 2;
                        _fill.Color = (ch > 0 ? buyVol : sellVol).WithAlpha(170);
                        canvas.DrawRect(ch > 0 ? cx : cx - bw, y + 2, bw, _rowH - 4, _fill);
                        Text(canvas, (ch > 0 ? "+" : "−") + Amount(Math.Abs(ch), px, unit, bookDec, pointValue), x, y, w, small, t.Text, ch > 0 ? -1 : 1);
                        break;
                    }
                    case DomColumn.Recent when recent.TryGetValue(r, out var rc) && maxRecent > 0:
                    {
                        decimal tot = rc.Bid + rc.Ask;
                        float vw = (float)(U(tot, r) / maxRecent) * (w - 4);
                        float bidW = tot > 0 ? vw * (float)(rc.Bid / tot) : 0;
                        _fill.Color = sellVol;
                        canvas.DrawRect(x + 2, y + 2, bidW, _rowH - 4, _fill);
                        _fill.Color = buyVol;
                        canvas.DrawRect(x + 2 + bidW, y + 2, vw - bidW, _rowH - 4, _fill);
                        Text(canvas, Amount(tot, px, unit, volDec, pointValue), x, y, w, small, t.Text, -1);
                        break;
                    }
                    case DomColumn.Price:
                    {
                        bool isLast = r == lastRow;
                        if (isLast)
                        {
                            _fill.Color = lastColor;
                            canvas.DrawRect(x + 1, y + 1, w - 2, _rowH - 2, _fill);
                        }

                        bool round = px % (inst.TickSize * dom.Step * 10) == 0;
                        Text(canvas, inst.FormatPrice(px), x, y, w, isLast ? bold : font, isLast ? SKColors.Black : round ? t.Text : priceText, 0);
                        if (!isLast && marks.ContainsKey(r))
                        {
                            _fill.Color = t.TextDim.WithAlpha(160);
                            canvas.DrawRect(x + 1, y + _rowH - 2, w - 2, 1, _fill);
                        }
                        break;
                    }
                    case DomColumn.Volume when session.TryGetValue(r, out var sv) && maxVol > 0:
                    {
                        decimal tot = sv.Bid + sv.Ask;
                        float vw = (float)(U(tot, r) / maxVol) * (w - 4);
                        float bidW = tot > 0 ? vw * (float)(sv.Bid / tot) : 0;
                        _fill.Color = sellVol;
                        canvas.DrawRect(x + 2, y + 2, bidW, _rowH - 4, _fill);
                        _fill.Color = buyVol;
                        canvas.DrawRect(x + 2 + bidW, y + 2, vw - bidW, _rowH - 4, _fill);
                        Text(canvas, Amount(tot, px, unit, volDec, pointValue), x, y, w, small, t.Text, -1);
                        break;
                    }
                    case DomColumn.Delta when session.TryGetValue(r, out var sv) && maxDelta > 0:
                    {
                        decimal d = sv.Ask - sv.Bid;
                        if (d == 0) break;
                        float dw = (float)(Math.Abs(U(d, r)) / maxDelta) * (w - 4);
                        _fill.Color = d > 0 ? buyVol : sellVol;
                        canvas.DrawRect(x + 2, y + 2, dw, _rowH - 4, _fill);
                        Text(canvas, (d > 0 ? "+" : "") + Amount(d, px, unit, volDec, pointValue), x, y, w, small, t.Text, -1);
                        break;
                    }
                    case DomColumn.TradedBid when session.TryGetValue(r, out var sv) && sv.Bid > 0 && maxSide > 0:
                    {
                        float bw = (float)(U(sv.Bid, r) / maxSide) * (w - 4);
                        _fill.Color = sellVol;
                        canvas.DrawRect(x + w - 2 - bw, y + 2, bw, _rowH - 4, _fill);
                        Text(canvas, Amount(sv.Bid, px, unit, volDec, pointValue), x, y, w, small, t.Text, 1);
                        break;
                    }
                    case DomColumn.TradedAsk when session.TryGetValue(r, out var sv) && sv.Ask > 0 && maxSide > 0:
                    {
                        float aw = (float)(U(sv.Ask, r) / maxSide) * (w - 4);
                        _fill.Color = buyVol;
                        canvas.DrawRect(x + 2, y + 2, aw, _rowH - 4, _fill);
                        Text(canvas, Amount(sv.Ask, px, unit, volDec, pointValue), x, y, w, small, t.Text, -1);
                        break;
                    }
                }
            }

            // an unavailable recorded-changes column says so instead of looking like "no changes" (full reason: stats bar)
            if (col == DomColumn.Changes && dom.RecentChanges is { IsAvailable: false })
                Text(canvas, dom.Data.DepthHistory.Status.RecordingState == DepthRecordingState.Recording ? "No data" : "Recording off", x, _headerH, w, small, t.TextDim, -1);
            canvas.Restore();
        }

        // posición: recuadro en el precio promedio
        if (posRow != long.MinValue)
        {
            int i = (int)(_topRow - posRow);
            if (i >= 0 && i < drawRows)
            {
                float y = _headerH + i * _rowH;
                float px = 0, pw = width;
                if (TryColumn(DomColumn.Price, out var cx, out var cw))
                {
                    px = cx;
                    pw = cw;
                }

                _stroke.Color = pos!.Volume > 0 ? t.PositionLong : t.PositionShort;
                _stroke.StrokeWidth = 2;
                canvas.DrawRect(px + 1.5f, y + 1.5f, pw - 3, _rowH - 3, _stroke);
                _stroke.StrokeWidth = 1;
            }
        }

        // órdenes propias
        var orders = dom.Broker?.ActiveOrders.ToList() ?? new List<Order>();
        foreach (var o in orders)
        {
            bool dragging = ReferenceEquals(o, dom.DragOrder);
            long orow = dom.RowOf(inst.ToTicks(dragging ? dom.DragPrice : o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price));
            int i = (int)(_topRow - orow);
            if (i < 0 || i >= drawRows) continue;
            float y = _headerH + i * _rowH;
            if (!TryColumn(OrdersColumn(o.Direction), out var x, out var w)) continue;
            canvas.Save();
            canvas.ClipRect(new SKRect(x, _headerH, x + w, height));
            var role = dom.Brackets?.RoleOf(o) ?? BracketRole.None;
            var color = role switch
            {
                BracketRole.StopLoss => new SKColor(240, 82, 90),
                BracketRole.Target => new SKColor(38, 194, 129),
                _ => o.Direction == OrderDirections.Buy ? t.BuyOrder : t.SellOrder,
            };
            _fill.Color = color.WithAlpha(dragging ? (byte)150 : (byte)255);
            canvas.DrawRoundRect(new SKRect(x + 2, y + 2, x + w - 2, y + _rowH - 2), 3, 3, _fill);
            string prefix = role switch { BracketRole.StopLoss => "SL ", BracketRole.Target => "TP ", _ => o.Type == OrderTypes.Stop ? "S " : "L " };
            string label = prefix + VolumeFormat.Compact(o.Unfilled, inst.VolumeDecimals);
            Text(canvas, label, x, y, w, small, SKColors.Black, 0);
            canvas.Restore();
        }

        // líneas de columnas
        _stroke.Color = new SKColor(40, 46, 60);
        foreach (var (_, x, _) in _cols.Skip(1))
        {
            float xx = MathF.Floor(x) + 0.5f;
            canvas.DrawLine(xx, _statsH, xx, height, _stroke);
        }

        if (s.HoverMirror && hoverRow >= 0 && hoverRow < drawRows && mid > 0)
            DrawMirror(canvas, dom, _topRow - hoverRow, mid, bids, asks, bestBidRow, bestAskRow, unit, pointValue, small, bookDec);
        if (s.ShowStats) DrawStats(canvas, dom, book, mid, unit, pointValue, header);
        if (s.ShowHeader) DrawHeader(canvas, header);
    }

    private readonly record struct CellStyle(DomSettings Settings, int Decimals, SKFont Font, SKFont Bold, SKColor Large, decimal LargeAt, DomUnit Unit, decimal PointValue);

    private void BookCell(SKCanvas canvas, float x, float y, float w, decimal size, decimal price, decimal max, SKColor color, bool best, bool rightAligned,
        CellStyle style, string? mark)
    {
        decimal value = style.Unit == DomUnit.Notional ? size * price * style.PointValue : size;
        if (style.Settings.BookBars)
        {
            _fill.Color = color.WithAlpha(best ? (byte)230 : (byte)150);
            float bw = max > 0 ? (float)(value / max) * (w - 2) : 0;
            canvas.DrawRect(rightAligned ? x + w - 1 - bw : x + 1, y + 1, bw, _rowH - 2, _fill);
        }
        else if (best)
        {
            _fill.Color = color.WithAlpha(120);
            canvas.DrawRect(x + 1, y + 1, w - 2, _rowH - 2, _fill);
        }

        bool isLarge = value >= style.LargeAt;
        if (isLarge)
        {
            _stroke.Color = style.Large;
            _stroke.StrokeWidth = 1.5f;
            canvas.DrawRect(x + 1.5f, y + 1.5f, w - 3, _rowH - 3, _stroke);
            _stroke.StrokeWidth = 1;
        }

        if (mark != null) Text(canvas, mark, x, y, w, RenderContext.GetFont(Theme.FontFamily, Math.Max(7, style.Font.Size - 2), false, false), Theme.TextDim, rightAligned ? -1 : 1);
        Text(canvas, Amount(size, price, style.Unit, style.Decimals, style.PointValue), x, y, w, best || isLarge ? style.Bold : style.Font, SKColors.White, rightAligned ? 1 : -1);
    }

    /// <summary>
    /// Stats bar: spread and, within 0.5 / 2 / 10 % of the mid, each side's resting size with a bid/ask split bar.
    /// "≥" marks a band deeper than the received book (only received levels are summed).
    /// </summary>
    private void DrawStats(SKCanvas canvas, DomModel dom, DepthSnapshot book, decimal mid, DomUnit unit, decimal pointValue, SKFont font)
    {
        var t = Theme;
        var inst = dom.Instrument;
        _fill.Color = t.AxisBackground;
        canvas.DrawRect(0, 0, _width, _statsH, _fill);
        float y1 = font.Spacing + 2, y2 = font.Spacing * 2 + 6;
        if (mid <= 0 || !book.HasBid || !book.HasAsk)
        {
            _text.Color = t.TextDim;
            canvas.DrawUiText(UiText.Tr("No two-sided book"), 6, y1, font, _text);
            return;
        }

        (decimal Sum, bool Partial) Band(BookLevel[] side, decimal pct, bool bidSide)
        {
            decimal limit = bidSide ? mid * (1 - pct / 100) : mid * (1 + pct / 100), sum = 0;
            foreach (var l in side)
            {
                decimal p = inst.FromTicks(l.PriceTicks);
                if (bidSide ? p < limit : p > limit) return (sum, false);
                sum += unit == DomUnit.Notional ? l.Size * p * pointValue : l.Size;
            }
            return (sum, true);
        }

        decimal spread = inst.FromTicks(book.BestAskTicks) - inst.FromTicks(book.BestBidTicks);
        string F(decimal v) => unit == DomUnit.Notional ? Amount(v, 1, DomUnit.Notional, 0) : VolumeFormat.Compact(v, FootprintRendererShim.Decimals(v, inst.VolumeDecimals));
        string bp = ((double)(spread / mid) * 1e4).ToString("0.0", CultureInfo.InvariantCulture);
        string head = UiText.Tr($"Spread {inst.FormatPrice(spread)} ({bp} bp) · ×{dom.Step} tick");
        if (Has(DomColumn.Changes) && dom.RecentChanges is { IsAvailable: true } changes)
            head += " · " + UiText.Tr($"changes: {changes.Comparisons} comparisons, {changes.SkippedTransitions} skipped");
        else if (Has(DomColumn.Changes) && dom.RecentChangesStatus is { } why)
            head += " · " + UiText.Tr(why);
        _text.Color = t.TextDim;
        canvas.DrawUiText(head, 6, y1, font, _text);
        float x = 6;
        var bidC = C(dom.Settings.BidColor).WithAlpha(200);
        var askC = C(dom.Settings.AskColor).WithAlpha(200);
        foreach (var pct in new[] { 0.5m, 2m, 10m })
        {
            var b = Band(book.Bids, pct, true);
            var a = Band(book.Asks, pct, false);
            string label = pct.ToString("0.#", CultureInfo.InvariantCulture) + "%";
            _text.Color = t.TextDim;
            canvas.DrawUiText(label, x, y2, font, _text);
            x += RenderContext.MeasureWidth(font, label) + 3;
            float bw = 28, h = font.Size * 0.7f;
            decimal total = a.Sum + b.Sum;
            float split = total > 0 ? (float)(b.Sum / total) * bw : bw / 2;
            _fill.Color = bidC;
            canvas.DrawRect(x, y2 - h, split, h, _fill);
            _fill.Color = askC;
            canvas.DrawRect(x + split, y2 - h, bw - split, h, _fill);
            x += bw + 3;
            string v = (b.Partial ? "≥" : "") + F(b.Sum) + "/" + (a.Partial ? "≥" : "") + F(a.Sum);
            _text.Color = t.Text;
            canvas.DrawUiText(v, x, y2, font, _text);
            x += RenderContext.MeasureWidth(font, v) + 10;
            if (x > _width - 40) break;
        }

        _stroke.Color = new SKColor(40, 46, 60);
        canvas.DrawLine(0, _statsH - 0.5f, _width, _statsH - 0.5f, _stroke);
    }

    /// <summary>Hover mirror: resting size from the price to the hovered row against the same distance on the other side.</summary>
    private void DrawMirror(SKCanvas canvas, DomModel dom, long hover, decimal mid, Dictionary<long, decimal> bids, Dictionary<long, decimal> asks,
        long bestBidRow, long bestAskRow, DomUnit unit, decimal pointValue, SKFont font, int dec)
    {
        var inst = dom.Instrument;
        long midRow = dom.RowOf(inst.ToTicks(mid));
        long distance = Math.Abs(hover - midRow);
        if (distance == 0) return;
        bool above = hover > midRow;
        decimal near = 0, far = 0;
        for (long k = 0; k <= distance; k++)
        {
            long ra = midRow + k, rb = midRow - k;
            decimal va = ra >= bestAskRow ? asks.GetValueOrDefault(ra) : 0, vb = rb <= bestBidRow ? bids.GetValueOrDefault(rb) : 0;
            if (unit == DomUnit.Notional)
            {
                va *= ra * dom.Step * inst.TickSize * pointValue;
                vb *= rb * dom.Step * inst.TickSize * pointValue;
            }
            if (above) { near += va; far += vb; }
            else { near += vb; far += va; }
        }

        long mirror = midRow - (hover - midRow);
        int iMirror = (int)(_topRow - mirror);
        if (iMirror >= 0 && iMirror <= _rows)
        {
            _stroke.Color = new SKColor(242, 181, 58, 200);
            canvas.DrawRect(0.5f, _headerH + iMirror * _rowH + 0.5f, _width - 1, _rowH - 1, _stroke);
        }

        decimal away = Math.Abs(hover * dom.Step * inst.TickSize - mid) / mid * 100;
        string nearSide = UiText.Tr(above ? "asks" : "bids"), farSide = UiText.Tr(above ? "bids" : "asks");
        string ratio = near > 0 && far > 0 ? (far >= near ? $"{farSide} ×{(far / near).ToString("0.00", CultureInfo.InvariantCulture)}" : $"{nearSide} ×{(near / far).ToString("0.00", CultureInfo.InvariantCulture)}") : "";
        string text = $"±{away.ToString("0.##", CultureInfo.InvariantCulture)}% · {nearSide} {Amount(near, 1, unit, dec)} · {farSide} {Amount(far, 1, unit, dec)}" + (ratio.Length > 0 ? " · " + ratio : "");
        float tw = RenderContext.MeasureWidth(font, text) + 10;
        int iHover = (int)(_topRow - hover);
        float y = _headerH + iHover * _rowH + (above ? _rowH + 2 : -font.Spacing - 6);
        y = Math.Clamp(y, _headerH, Math.Max(_headerH, _height - font.Spacing - 6));
        float x = Math.Max(2, _width - tw - 4);
        _fill.Color = new SKColor(20, 24, 32, 235);
        canvas.DrawRoundRect(new SKRect(x, y, x + tw, y + font.Spacing + 4), 4, 4, _fill);
        _text.Color = new SKColor(235, 235, 240);
        canvas.DrawUiText(text, x + 5, y + font.Spacing, font, _text);
    }

    private void Text(SKCanvas canvas, string s, float x, float y, float w, SKFont f, SKColor color, int align)
    {
        // measuring and drawing translate it (once each)
        float tw = RenderContext.MeasureWidth(f, s);
        float tx = align < 0 ? x + 4 : align > 0 ? x + w - 4 - tw : x + (w - tw) / 2;
        float ty = y + _rowH / 2 - (f.Metrics.Ascent + f.Metrics.Descent) / 2;
        _text.Color = color;
        canvas.DrawUiText(s, tx, ty, f, _text);
    }

    private void DrawHeader(SKCanvas canvas, SKFont font)
    {
        var t = Theme;
        float top = _statsH, h = _headerH - _statsH;
        _fill.Color = t.AxisBackground;
        canvas.DrawRect(0, top, _width, h, _fill);
        float baseline = top + h / 2 - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
        foreach (var (col, x, w) in _cols)
        {
            // el rótulo se abrevia según el ancho y nunca se sale de su columna
            var label = FitLabel(col, w, s => RenderContext.MeasureWidth(font, s));
            if (label == null) continue;
            canvas.Save();
            canvas.ClipRect(new SKRect(x, top, x + w, _headerH));
            _text.Color = t.TextDim;
            canvas.DrawUiText(label, x + w / 2 - RenderContext.MeasureWidth(font, label) / 2, baseline, font, _text);
            canvas.Restore();
        }

        _stroke.Color = new SKColor(40, 46, 60);
        canvas.DrawLine(0, _headerH - 0.5f, _width, _headerH - 0.5f, _stroke);
    }

    /// <summary>Qué hay bajo el mouse: columna y precio.</summary>
    public (DomColumn Column, decimal Price)? HitTest(DomModel dom, float x, float y)
    {
        if (y < _headerH || _width <= 0) return null;
        int i = (int)((y - _headerH) / _rowH);
        long r = _topRow - i;
        decimal price = r * dom.Step * dom.Instrument.TickSize;
        foreach (var (c, cx, cw) in _cols)
            if (x >= cx && x < cx + cw) return (c, price);
        return null;
    }

    /// <summary>Orden propia dibujada en la celda (columna de órdenes de su lado, mismo precio).</summary>
    public Order? OrderAt(DomModel dom, float x, float y)
    {
        var hit = HitTest(dom, x, y);
        if (hit == null || dom.Broker == null) return null;
        long row = dom.RowOf(dom.Instrument.ToTicks(hit.Value.Price));
        foreach (var dir in new[] { OrderDirections.Buy, OrderDirections.Sell })
        {
            if (OrdersColumn(dir) != hit.Value.Column) continue;
            var o = dom.Broker.ActiveOrders.FirstOrDefault(o => o.Direction == dir && dom.RowOf(dom.Instrument.ToTicks(o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price)) == row);
            if (o != null) return o;
        }

        return null;
    }

    /// <summary>Clic en el DOM: opera directo en el broker (sin confirmación). Only reached through <see cref="DomOrderGestures"/>.</summary>
    public string? Click(DomModel dom, float x, float y, bool right, bool cancelModifier)
    {
        var hit = HitTest(dom, x, y);
        var broker = dom.Broker;
        if (hit == null || broker == null) return null;
        var (col, price) = hit.Value;
        var book = dom.Data.Depth;
        decimal bestBid = book.HasBid ? dom.Instrument.FromTicks(book.BestBidTicks) : dom.Data.LastPrice;
        decimal bestAsk = book.HasAsk ? dom.Instrument.FromTicks(book.BestAskTicks) : dom.Data.LastPrice;
        decimal qty = dom.OrderQuantity;

        if (right || cancelModifier)
        {
            broker.CancelAt(price);
            return $"Orders canceled on {dom.Instrument.FormatPrice(price)}";
        }

        switch (col)
        {
            case DomColumn.Bid:
            case DomColumn.BuyOrders:
                if (price < bestAsk) OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Buy, qty, price);
                else OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Buy, qty, price);
                return $"Buy {(price < bestAsk ? "limit" : "stop")} {qty} @ {dom.Instrument.FormatPrice(price)}";
            case DomColumn.Ask:
            case DomColumn.SellOrders:
                if (price > bestBid) OrderActions.Manual(broker, OrderTypes.Limit, OrderDirections.Sell, qty, price);
                else OrderActions.Manual(broker, OrderTypes.Stop, OrderDirections.Sell, qty, price);
                return $"Sell {(price > bestBid ? "limit" : "stop")} {qty} @ {dom.Instrument.FormatPrice(price)}";
        }

        return null;
    }

    public void Scroll(DomModel dom, int rows)
    {
        dom.AutoCenter = false;
        long center = dom.RowOf(dom.CenterTicks) + rows;
        dom.CenterTicks = center * dom.Step;
    }
}

internal static class FootprintRendererShim
{
    public static int Decimals(decimal max, int instrumentDecimals) => Rendering.FootprintRenderer.ChooseDecimals(max, instrumentDecimals);
}
