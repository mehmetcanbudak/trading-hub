using System.Collections.Concurrent;
using TradingHub.Feeds;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Todos los datos de un instrumento: operaciones (historia + vivo), libro y estado de la conexión.
/// Los feeds escriben desde sus hilos en colas; la UI llama a <see cref="Pump"/> en su hilo para
/// aplicar los cambios y disparar los eventos. Varios gráficos pueden compartir la misma instancia.
/// </summary>
public sealed class InstrumentData : IMarketDataSink, IDisposable
{
    private readonly ConcurrentQueue<Trade> _incoming = new();
    private readonly string _sourceIdentity;
    private readonly ConcurrentQueue<DepthUpdate> _depthUpdates = new();
    private long _depthUpdateBytes;
    private DateTime _lastReplayAvailableAt;
    private long _depthHistoryVersion = -1;
    private long _depthLoadRevision;
    private bool _depthUnavailable;
    private bool _depthOverflow;
    private volatile bool _historyMerged;
    private List<Trade>? _historyToMerge;
    private IDisposable? _subscription;
    private CancellationTokenSource? _cts;
    private TickRecorder? _recorder;
    private FeedStatus _status = FeedStatus.Disconnected;
    private string? _statusMessage;
    private volatile bool _statusDirty;
    private readonly object _lifecycle = new();
    private readonly ConcurrentQueue<(long Generation, Action Publish)> _publications = new();
    private long _generation;
    private bool _disposed;
    private IDisposable? _openInterestSubscription;
    private GenerationSink? _sink;
    private long _openInterestClockMinute = -1;
    public OpenInterestStore OpenInterest { get; } = new();
    private bool _openInterestDirty;
    private bool _refreshRequested;
    public event Action? OpenInterestChanged;

    private void Publish(long generation, Action action)
    {
        lock (_lifecycle)
            if (_cts != null && generation == _generation && !_disposed)
                _publications.Enqueue((generation, action));
    }

    public InstrumentData(Instrument instrument, IMarketDataFeed feed)
    {
        Instrument = instrument;
        _sourceIdentity = HistorySources.DefaultSourceIdentity(instrument.FeedId, instrument.Symbol);
        Feed = feed;
        Trades = new TradeStore(instrument);
        DepthHistory = new DepthHistory(instrument);
        if (feed is IHistoryRefreshFeed refresh) refresh.HistoryInvalidated += OnHistoryInvalidated;
    }

    private void OnHistoryInvalidated(string? symbol)
    {
        if (symbol != null && !string.Equals(symbol, Instrument.Symbol, StringComparison.OrdinalIgnoreCase)) return;
        Publish(_generation, () => _refreshRequested = true);
    }

    public Instrument Instrument { get; }
    public IMarketDataFeed Feed { get; }
    public TradeStore Trades { get; }
    public DepthSnapshot Depth { get; private set; } = DepthSnapshot.Empty;
    public DepthHistory DepthHistory { get; }
    public DepthRecordingOptions DepthRecording { get; set; } = new();
    public event Action? DepthHistoryChanged;
    public long QueuedDepthBytes { get { lock (_lifecycle) return _depthUpdateBytes; } }
    public long DepthQueueByteLimit => DepthHistory.Options.MaxQueuedBytes;
    private DateTime DepthNow => Feed is ReplayFeed replay ? replay.Clock : Feed.Now;

    public void ConfigureDepthRecording(DepthRecordingOptions options)
    {
        lock (_lifecycle)
        {
            DepthRecording = options;
            if (_cts != null && !_disposed) DepthHistory.Configure(RecordingRoot, options, DepthNow);
            TrimDepthQueue(enforceSampleLimit: true);
        }
    }

    public FeedStatus Status => _status;
    public bool ReplayInputDrained
    {
        get
        {
            lock (_lifecycle)
                return Feed is ReplayFeed && _cts != null && !_disposed && _historyMerged && !IsLoadingHistory && !IsExtending &&
                    _historyToMerge == null && _olderToMerge == null && _incoming.IsEmpty && _publications.IsEmpty && !_refreshRequested;
        }
    }
    public string? StatusMessage => _statusMessage;
    public bool IsLoadingHistory { get; private set; }
    public double HistoryProgress { get; private set; }
    public string? HistoryError { get; private set; }
    public string? HistoryWarning { get; private set; }
    public string? RecordingWarning { get; private set; }

    /// <summary>Desde cuándo hay historia pedida/cargada (UTC).</summary>
    public DateTime HistoryFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>Si está trayendo historia más vieja en segundo plano.</summary>
    public bool IsExtending { get; private set; }

    private List<Trade>? _olderToMerge;
    private DateTime _pendingFrom;

    /// <summary>Última operación del mercado cuando está cerrado (la historia se arma hasta ahí).</summary>
    private DateTime? _anchor;

    public decimal LastPrice => Trades.Count > 0 ? Trades.GetPrice(Trades.Count - 1) : 0m;
    public decimal BestBid => Depth.HasBid ? Instrument.FromTicks(Depth.BestBidTicks) : 0m;
    public decimal BestAsk => Depth.HasAsk ? Instrument.FromTicks(Depth.BestAskTicks) : 0m;

    /// <summary>Operaciones nuevas agregadas al almacén (índice inicial, cantidad). Hilo de UI.</summary>
    public event Action<int, int>? TradesAppended;

    /// <summary>La historia terminó de cargarse (los gráficos deben reconstruirse). Hilo de UI.</summary>
    public event Action? HistoryLoaded;

    /// <summary>Cambió el libro. Hilo de UI.</summary>
    public event Action<DepthUpdate>? DepthUpdated;

    public event Action? StatusChanged;

    /// <summary>Carpeta donde se graban los ticks (null = no grabar).</summary>
    public string? RecordingRoot { get; set; }

    /// <summary>Si graba lo recibido en vivo (la carpeta se sigue leyendo aunque no grabe).</summary>
    public bool RecordLive { get; set; } = true;

    // ----------------------------------------------------------------- arranque

    /// <summary>Se suscribe al vivo y carga historia desde <paramref name="historyFrom"/>.</summary>
    public void Start(DateTime historyFrom)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        long generation = _generation;
        _sink = new GenerationSink(this, generation);
        _historyMerged = false;
        _anchor = null;
        _tradedSinceAnchor = 0;
        RealTicksFrom = DateTime.MaxValue;
        IsLoadingHistory = true;
        HistoryFrom = historyFrom;
        HistoryProgress = 0;
        HistoryError = null;
        HistoryWarning = null;
        Depth = DepthSnapshot.Empty;
        DepthHistory.Start(RecordingRoot, DepthRecording, Feed.Now);
        LoadDepthHistory(historyFrom, Feed.Now, generation, ct);
        OpenInterest.Clear();
        OpenInterest.Availability = OpenInterestAvailability.Unsupported;
        _openInterestDirty = true;
        ReceiveStatus(generation, FeedStatus.Connecting, "Connecting…");

        RecordingWarning = null;
        if (Feed.IsLive && RecordingRoot != null && RecordLive)
        {
            _recorder = new TickRecorder(RecordingRoot, Instrument);
            if (!_recorder.IsActive)
                RecordingWarning = "Live recording unavailable: another history writer owns this instrument. Market data remains readable, but new ticks are not being saved.";
        }

        // 1) vivo primero (se acumula mientras carga la historia)
        try
        {
            _subscription = Feed.Subscribe(Instrument, _sink);
        }
        catch (Exception ex)
        {
            ReceiveStatus(generation, FeedStatus.Error, ex.Message);
        }
        if (Feed is IOpenInterestProvider oi && oi.SupportsOpenInterest(Instrument))
        {
            OpenInterest.MaxAge = oi.OpenInterestMaxAge;
            OpenInterest.Availability = OpenInterestAvailability.Loading;
            try { _openInterestSubscription = oi.SubscribeOpenInterest(Instrument, _sink); }
            catch (Exception ex) { Publish(generation, () => { OpenInterest.Availability = OpenInterestAvailability.Error; OpenInterest.Message = ex.Message; _openInterestDirty = true; }); }
            LoadOpenInterest(oi, historyFrom, Feed.Now, generation, ct);
        }

        // 2) historia: archivos locales + los huecos que falten desde el feed
        _ = Task.Run(async () =>
        {
            List<Trade> all;
            try
            {
                var from = historyFrom;
                DateTime? anchor = null;
                if (Feed is ISessionAwareFeed session)
                {
                    // mercado cerrado (noche, fin de semana): el mismo largo de historia, terminando en la última operación
                    try
                    {
                        var last = await session.LastTradeTimeAsync(Instrument, ct).ConfigureAwait(false);
                        if (last is { } lt && Feed.Now - lt > TimeSpan.FromMinutes(10))
                        {
                            anchor = lt;
                            from = lt - (Feed.Now - historyFrom);
                            var loadedFrom = from;
                            Publish(generation, () => { _anchor = lt; HistoryFrom = loadedFrom; });
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // sin la hora de la última operación se carga como siempre
                    }
                }

                all = await LoadRangeAsync(from, Feed.Now, generation, ct, anchor).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Publish(generation, () => HistoryError = ex.Message);
                all = new List<Trade>();
            }

            Publish(generation, () => _historyToMerge = all);
        }, ct);
    }

    /// <summary>
    /// Hasta qué antigüedad se piden ticks al exchange cuando faltan. Lo anterior (y sin ticks en disco)
    /// se completa con velas del exchange: así se cargan meses o años en segundos, como en cualquier plataforma.
    /// </summary>
    private TimeSpan _tickWindow = TimeSpan.FromDays(7);
    public TimeSpan TickWindow
    {
        get => _tickWindow;
        set => _tickWindow = value < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : value > TimeSpan.FromDays(90) ? TimeSpan.FromDays(90) : value;
    }
    public static int MaxDiskTrades { get; set; } = 2_000_000;
    public int RawTradeLimit { get; set; } = MaxDiskTrades;
    public string? CompactRoot { get; set; }
    public bool UseCompactHistory { get; set; }
    public int CompactMinutes { get; set; } = 5;
    public int CompactTradeLimit { get; set; } = 500_000;

    /// <summary>Desde cuándo hay ticks reales (antes, la historia viene de velas del exchange).</summary>
    public DateTime RealTicksFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>
    /// Lee lo grabado/descargado entre <paramref name="from"/> y <paramref name="to"/> y completa los huecos:
    /// lo reciente con ticks del exchange (se graban para no volver a pedirlos) y lo viejo con velas.
    /// </summary>
    private async Task<List<Trade>> LoadRangeAsync(DateTime from, DateTime to, long generation, CancellationToken ct, DateTime? anchor = null)
    {
        if (Feed is IbFeed ib)
        {
            // Fetch/publish first. Recorded and synthetic rows are then captured together, never across a network await.
            int configuration = await ib.EnsureBarHistoryAsync(Instrument, from, to, ct).ConfigureAwait(false);
            var recorded = new List<Trade>();
            var ibSynthetic = new List<Trade>();
            int recordedCount = 0;
            DateTime rawCut = (anchor ?? _anchor ?? Feed.Now) - TickWindow;
            using (var snapshot = IbBarStore.OpenSnapshot(ib.HistoryRoot, Instrument.Symbol, ct))
            {
                foreach (var day in EachDay(from, to).Reverse())
                {
                    ct.ThrowIfCancellationRequested();
                    var window = new HistoryWindow(Math.Max(0, RawTradeLimit - recordedCount));
                    foreach (var row in snapshot.ReadRecordedDay(day, Instrument.TickSize))
                    {
                        ct.ThrowIfCancellationRequested();
                        var trade = HistorySources.AnnotateSource(row, Instrument.FeedId, _sourceIdentity);
                        window.Observe(trade, trade.Time >= from && trade.Time < to &&
                            (!trade.Provenance.IsMeasured || trade.Time >= rawCut));
                    }
                    var retained = window.Finish();
                    if (window.Truncated)
                    {
                        for (int index = 0; index < retained.Count; index++)
                        {
                            var trade = retained[index];
                            retained[index] = new Trade(trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side,
                                trade.Provenance with { Quality = trade.Provenance.Quality | DataQuality.Partial });
                        }
                        Publish(generation, () => HistoryWarning = "The recent-history event limit retained the newest source events. Earlier coverage may be compact or unavailable.");
                    }
                    recordedCount += retained.Count;
                    recorded.AddRange(retained);
                    ibSynthetic.AddRange(snapshot.ReadBarDay(day, Instrument.TickSize).Where(t => t.Time >= from && t.Time < to));
                }
            }
            ib.EnsureCurrent(configuration, ct);
            recorded = TickFile.SortAndDedupe(recorded);
            var measuredTimes = recorded.Where(t => t.Provenance.IsMeasured).Select(t => t.Time.Ticks).Order().ToList();
            recorded.RemoveAll(t => HistoricalData.OverlapsMeasured(t, measuredTimes));
            if (measuredTimes.Count > 0)
            {
                DateTime firstRecorded = new(measuredTimes[0], DateTimeKind.Utc);
                Publish(generation, () => RealTicksFrom = firstRecorded < RealTicksFrom ? firstRecorded : RealTicksFrom);
            }
            var legacyMinutes = recorded.Where(t => !t.Provenance.IsMeasured).Select(t => t.Time.Ticks / TimeSpan.TicksPerMinute).ToHashSet();
            ibSynthetic.RemoveAll(t => HistoricalData.OverlapsMeasured(t, measuredTimes) || legacyMinutes.Contains(t.Time.Ticks / TimeSpan.TicksPerMinute));
            ibSynthetic.Sort(static (a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Id.CompareTo(b.Id));
            return KlineHistory.MergeByTime(ibSynthetic, recorded);
        }
        var real = new List<Trade>();
        var compact = new List<Trade>();
        if (RecordingRoot != null && (Feed.IsLive || Feed is IBarHistoryFeed))
        {
            DateTime rawCut = (anchor ?? _anchor ?? Feed.Now) - TickWindow;
            var rawChunks = new List<List<Trade>>();
            var compactChunks = new List<List<Trade>>();
            int rawCount = 0, compactCount = 0;
            var sourceIdentity = _sourceIdentity;
            foreach (var day in EachDay(from, to).Reverse())
            {
                ct.ThrowIfCancellationRequested();
                var path = TickFile.ExistingDayPath(RecordingRoot, Instrument.FeedId, Instrument.Symbol, day);
                if (path == null) continue;
                List<Trade> list;
                try
                {
                    IEnumerable<Trade> recorded = Feed is IBarHistoryFeed && Instrument.FeedId == IbBarImport.FeedId
                        ? IbBarStore.ReadRecordedDay(RecordingRoot, Instrument.Symbol, day, Instrument.TickSize)
                        : TickFile.Enumerate(path, allowIncompleteTail: !path.EndsWith(".br", StringComparison.Ordinal) && TickFile.HasActiveRecordingWriter(RecordingRoot, Instrument.FeedId, Instrument.Symbol), requireOrdered: false);
                    var window = new HistoryWindow(Math.Max(0, RawTradeLimit - rawCount));
                    foreach (var recordedTrade in recorded)
                    {
                        ct.ThrowIfCancellationRequested();
                        var trade = HistorySources.AnnotateSource(recordedTrade, Instrument.FeedId, sourceIdentity);
                        bool include = trade.Time >= from && trade.Time < to &&
                            (!trade.Provenance.IsMeasured || trade.Time >= rawCut);
                        window.Observe(trade, include);
                    }
                    list = window.Finish();
                    if (window.Truncated)
                    {
                        for (int index = 0; index < list.Count; index++)
                        {
                            var trade = list[index];
                            list[index] = new Trade(trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side,
                                trade.Provenance with { Quality = trade.Provenance.Quality | DataQuality.Partial });
                        }
                        Publish(generation, () => HistoryWarning = "The recent-history event limit retained the newest source events. Earlier coverage may be compact or unavailable.");
                    }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
                {
                    // A small append file may end mid-record. Keep its established codec's recoverable prefix;
                    // large/unordered/corrupt days remain unavailable rather than allocating an unbounded retry.
                    if (Instrument.FeedId != IbBarImport.FeedId && new FileInfo(path).Length <= 16 * 1024 * 1024)
                        list = HistorySources.ReadSourceFile(path, Instrument.FeedId, Instrument.Symbol)
                            .Where(t => t.Time >= from && t.Time < to && (!t.Provenance.IsMeasured || t.Time >= rawCut))
                            .TakeLast(Math.Max(0, RawTradeLimit - rawCount)).ToList();
                    else list = new List<Trade>();
                    Publish(generation, () => HistoryWarning = "Some recorded history is incomplete or requires correction: " + error.Message);
                }
                rawCount += list.Count;
                if (list.Count > 0) rawChunks.Add(list);
                if (UseCompactHistory && CompactRoot != null && Instrument.FeedId != IbBarImport.FeedId && compactCount < CompactTradeLimit)
                {
                    try
                    {
                        var summary = CompactHistory.LoadDay(RecordingRoot, CompactRoot, Instrument, day, CompactMinutes, ct);
                        var rawBuckets = new HashSet<long>(list.Select(t => t.Time.Ticks / (CompactMinutes * TimeSpan.TicksPerMinute)));
                        summary = summary.Where(t => t.Time >= from && t.Time < to && !rawBuckets.Contains(t.Time.Ticks / (CompactMinutes * TimeSpan.TicksPerMinute))).ToList();
                        int available = Math.Max(0, CompactTradeLimit - compactCount);
                        if (summary.Count > available)
                        {
                            summary = summary.TakeLast(available).Select(t => new Trade(t.Id, t.Time, t.Price, t.Volume, t.Side, t.Provenance with { Quality = t.Provenance.Quality | DataQuality.Partial })).ToList();
                            Publish(generation, () => HistoryWarning = "The compact memory limit retained only recent summaries. The oldest retained bucket may be partial; raw files remain intact.");
                        }
                        compactCount += summary.Count;
                        if (summary.Count > 0) compactChunks.Add(summary);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
                    { Publish(generation, () => HistoryWarning = "Compact history is unavailable; intact raw files are retained: " + error.Message); }
                }
            }
            rawChunks.Reverse(); compactChunks.Reverse();
            if (rawChunks.Count > 0)
            {
                real = rawChunks[0];
                for (int index = 1; index < rawChunks.Count; index++) real.AddRange(rawChunks[index]);
                rawChunks.Clear();
            }
            if (compactChunks.Count > 0)
            {
                compact = compactChunks[0];
                for (int index = 1; index < compactChunks.Count; index++) compact.AddRange(compactChunks[index]);
                compactChunks.Clear();
            }
            real = TickFile.SortAndDedupe(real);
            for (int i = 0; i < real.Count; i++)
            {
                var trade = real[i];
                if (trade.Provenance.SourceIdentity == null)
                    real[i] = new Trade(trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side, trade.Provenance with { SourceIdentity = sourceIdentity });
            }
        }
        if (real.Count > 0)
        {
            DateTime first = DateTime.MaxValue;
            foreach (var trade in real) if (trade.Provenance.IsMeasured && trade.Time < first) first = trade.Time;
            if (first != DateTime.MaxValue) Publish(generation, () => RealTicksFrom = first < RealTicksFrom ? first : RealTicksFrom);
        }
        if (Feed is IBarHistoryFeed bars && Instrument.FeedId == IbBarImport.FeedId)
        {
            var syntheticBars = await bars.LoadBarHistoryAsync(Instrument, from, to, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var measured = KlineHistory.MergeByTime(compact, real);
            var recordedTimes = measured.Where(t => t.Provenance.IsMeasured).Select(t => t.Time.Ticks).ToList();
            var filtered = syntheticBars.Where(t => (t.Provenance.Quality & DataQuality.Synthetic) != 0 && t.Time >= from && t.Time < to &&
                !HistoricalData.OverlapsMeasured(t, recordedTimes)).OrderBy(t => t.Time).ToList();
            return KlineHistory.MergeByTime(filtered, measured);
        }

        if (!Feed.SupportsHistory) return KlineHistory.MergeByTime(compact, real);
        var measuredHistory = KlineHistory.MergeByTime(compact, real);
        var gaps = FindGaps(measuredHistory, from, to, tailById: false);
        if (gaps.Count == 0) return measuredHistory;

        // lo reciente en ticks; lo viejo en velas (si el feed las tiene)
        var klineSource = Feed as IKlineHistory;
        // los ticks cubren las últimas horas de mercado (con el mercado cerrado, antes de la última operación)
        DateTime tickCut = klineSource != null || Feed.IsLive
            ? (anchor ?? _anchor ?? Feed.Now) - (klineSource != null && TickWindow > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : TickWindow)
            : DateTime.MinValue;
        var tickGaps = new List<(DateTime From, DateTime To, long? FromId)>();
        var klineGaps = new List<(DateTime From, DateTime To)>();
        foreach (var (gFrom, gTo, fromId) in gaps)
        {
            if (gTo <= tickCut) klineGaps.Add((gFrom, gTo));
            else if (gFrom >= tickCut) tickGaps.Add((gFrom, gTo, fromId));
            else
            {
                klineGaps.Add((gFrom, tickCut));
                tickGaps.Add((tickCut, gTo, null));
            }
        }

        int parts = tickGaps.Count + (klineGaps.Count > 0 ? 1 : 0);
        int part = 0;
        var fetched = new List<Trade>();
        foreach (var (gFrom, gTo, fromId) in tickGaps)
        {
            int index = part++;
            var progress = new InlineProgress(p => Publish(generation, () => HistoryProgress = (index + Math.Clamp(p, 0, 1)) / parts));
            try
            {
                await Feed.LoadTradesAsync(Instrument, gFrom, gTo, fromId, batch =>
                {
                    foreach (var incoming in batch)
                    {
                        var trade = WithSource(incoming);
                        DateTime available = Feed is ReplayFeed ? trade.AvailableAt : trade.Time;
                        if (available < from || available >= to) continue;
                        if (fetched.Count + real.Count >= RawTradeLimit)
                        {
                            Publish(generation, () => HistoryWarning = "The bounded raw window is full. Remaining coverage uses labelled candle history when available.");
                            return false;
                        }
                        fetched.Add(trade);
                    }
                    return !ct.IsCancellationRequested;
                }, progress, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { Publish(generation, () => HistoryWarning = "Some recent trade history is unavailable; recorded data is retained: " + error.Message); }
            // Also cover unavailable/capacity-limited portions, without replacing any finer observations.
            if (klineSource != null) klineGaps.Add((gFrom, gTo));
        }

        if (fetched.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            lock (_lifecycle)
                if (generation == _generation && _cts != null && !ct.IsCancellationRequested)
                    _recorder?.WriteHistory(fetched);
            if (real.Count == 0) real = fetched;
            else real.AddRange(fetched);
            real = TickFile.SortAndDedupe(real);
        }

        if (real.Count > 0)
        {
            DateTime first = DateTime.MaxValue;
            foreach (var trade in real) if (trade.Provenance.IsMeasured && trade.Time < first) first = trade.Time;
            if (first != DateTime.MaxValue) Publish(generation, () => RealTicksFrom = first < RealTicksFrom ? first : RealTicksFrom);
        }
        if (klineGaps.Count == 0 || klineSource == null) return KlineHistory.MergeByTime(compact, real);

        // velas solo donde no hay ticks reales
        measuredHistory = KlineHistory.MergeByTime(compact, real);
        var windows = KlineHistory.Subtract(klineGaps, DisplayCoverage(measuredHistory));
        if (windows.Count == 0) return measuredHistory;
        var kFrom = windows[0].From;
        var kTo = windows[^1].To;
        int minutes = KlineHistory.MinutesFor(kTo - kFrom);
        decimal endPrice = real.Count > 0 ? real[0].Price : Trades.Count > 0 ? Trades.GetPrice(0) : 0;
        int kIndex = part;
        var kProgress = new InlineProgress(p => Publish(generation, () => HistoryProgress = (kIndex + Math.Clamp(p, 0, 1)) / parts));
        List<Kline> klines;
        try { klines = await KlineHistory.LoadAsync(klineSource, Instrument, kFrom, kTo, minutes, endPrice, kProgress, ct,
            warning => Publish(generation, () => HistoryWarning = warning)).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Publish(generation, () => HistoryWarning = "Candle fallback is unavailable; finer recorded history is retained: " + error.Message);
            return measuredHistory;
        }
        var synthetic = new List<Trade>(klines.Count * 4);
        KlineHistory.Synthesize(klines, Instrument, minutes, windows, synthetic);
        return KlineHistory.MergeByTime(synthetic, measuredHistory);
    }

    /// <summary>Rangos que faltan en <paramref name="local"/> (ordenado) para cubrir [from, to).</summary>
    public static List<(DateTime From, DateTime To, long? FromId)> FindGaps(IReadOnlyList<Trade> local, DateTime from, DateTime to, bool tailById)
    {
        var gaps = new List<(DateTime, DateTime, long?)>();
        if (local.Count == 0)
        {
            gaps.Add((from, to, null));
            return gaps;
        }

        if (local[0].Time - from > TimeSpan.FromMinutes(1)) gaps.Add((from, local[0].Time, null));
        for (int i = 1; i < local.Count; i++)
        {
            if (local[i].Time - local[i - 1].Time > TimeSpan.FromMinutes(3))
                gaps.Add((local[i - 1].Time, local[i].Time, null));
        }

        // demasiados cortes (instrumento poco líquido): conviene pedir todo de una
        if (gaps.Count > 20)
        {
            gaps.Clear();
            gaps.Add((from, to, null));
            return gaps;
        }

        if (to - local[^1].Time > TimeSpan.FromSeconds(1))
            gaps.Add((local[^1].Time, to, tailById ? local[^1].Id + 1 : null));
        return gaps;
    }

    /// <summary>
    /// Trae historia más vieja hasta <paramref name="from"/> (si hace falta): primero de los archivos
    /// grabados/descargados y lo que falte desde el feed. Al terminar se reconstruyen los gráficos.
    /// </summary>
    public bool ExtendHistory(DateTime from)
    {
        if (_cts == null || _disposed || from >= HistoryFrom || IsLoadingHistory || IsExtending) return false;
        var ct = _cts.Token;
        long generation = _generation;
        DateTime until = Trades.Count > 0 ? Trades.FirstTime : Feed.Now;
        if (Feed is ReplayFeed && Trades.Count > 0)
        {
            until = DateTime.MaxValue;
            for (int index = 0; index < Trades.Count; index++)
            {
                var trade = Trades.Get(index);
                if (trade.AvailableAt < until) until = trade.AvailableAt;
            }
        }
        IsExtending = true;
        HistoryProgress = 0;
        _pendingFrom = from;
        _ = Task.Run(async () =>
        {
            var older = new List<Trade>();
            try
            {
                older = await LoadRangeAsync(from, until, generation, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Publish(generation, () => HistoryError = ex.Message);
            }

            Publish(generation, () => _olderToMerge = older);
        }, ct);
        if (Feed is IOpenInterestProvider oi && oi.SupportsOpenInterest(Instrument))
            LoadOpenInterest(oi, from, until, generation, ct);
        LoadDepthHistory(from, DepthNow, generation, ct);
        return true;
    }

    private long _lastDataAt;
    private int _tradedSinceAnchor;

    /// <summary>Cuándo llegó el último dato en vivo (operación o libro), en hora real UTC.</summary>
    public DateTime LastDataAt => new(Interlocked.Read(ref _lastDataAt), DateTimeKind.Utc);

    /// <summary>Al abrir, el mercado estaba cerrado (la última operación era vieja) y todavía no volvió a operar.</summary>
    public bool MarketClosed => _anchor != null && Volatile.Read(ref _tradedSinceAnchor) == 0;

    /// <summary>Última operación conocida cuando el mercado estaba cerrado al abrir.</summary>
    public DateTime? ClosedAt => _anchor;

    /// <summary>
    /// Fuerza la reconexión al instante (por ejemplo al despertar la Mac). Los feeds que lo soportan cortan
    /// la conexión y rellenan lo perdido; los demás se vuelven a suscribir.
    /// </summary>
    public void Reconnect()
    {
        if (_subscription is IReconnectable r)
        {
            r.Reconnect();
            return;
        }

        if (_subscription == null || !Feed.IsLive) return;
        _subscription.Dispose();
        try
        {
            _subscription = Feed.Subscribe(Instrument, _sink!);
        }
        catch (Exception ex)
        {
            ReceiveStatus(_generation, FeedStatus.Error, ex.Message);
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        IDisposable? subscription, openInterestSubscription;
        TickRecorder? recorder;
        lock (_lifecycle)
        {
            _generation++;
            cts = _cts;
            _cts = null;
            subscription = _subscription;
            _subscription = null;
            openInterestSubscription = _openInterestSubscription;
            _openInterestSubscription = null;
            recorder = _recorder;
            _recorder = null;
            _historyToMerge = _olderToMerge = null;
            _incoming.Clear();
            _depthUpdates.Clear();
            _depthUpdateBytes = 0;
            _depthOverflow = false;
            _lastReplayAvailableAt = DateTime.MinValue;
            _depthUnavailable = true;
            _statusDirty = false;
            _openInterestDirty = false;
            _refreshRequested = false;
            IsLoadingHistory = IsExtending = false;
            OpenInterest.Clear();
            OpenInterest.Availability = OpenInterestAvailability.Unavailable;
        }
        cts?.Cancel();
        cts?.Dispose();
        subscription?.Dispose();
        openInterestSubscription?.Dispose();
        recorder?.Dispose();
        DepthHistory.Stop(Feed.Now);
    }

    private static IEnumerable<DateOnly> EachDay(DateTime fromUtc, DateTime toUtc)
    {
        for (var d = DateOnly.FromDateTime(fromUtc); d <= DateOnly.FromDateTime(toUtc); d = d.AddDays(1))
            yield return d;
    }

    // ----------------------------------------------------------------- IMarketDataSink (hilos del feed)

    void IMarketDataSink.OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) => ReceiveTrades(_generation, trades);
    void IMarketDataSink.OnDepth(Instrument instrument, DepthUpdate update) => ReceiveDepth(_generation, update);
    void IMarketDataSink.OnStatus(Instrument instrument, FeedStatus status, string? message) =>
        ReceiveStatus(_generation, status, message);

    private void ReceiveStatus(long generation, FeedStatus status, string? message)
    {
        lock (_lifecycle)
        {
            if (_cts == null || generation != _generation || _disposed) return;
            if (status is FeedStatus.Disconnected or FeedStatus.Error or FeedStatus.Connecting or FeedStatus.Reconnecting)
            {
                DepthHistory.MarkGap(DepthNow, DepthGapReason.Disconnected, message);
                EnqueueDepth(new DepthUpdate { Snapshot = DepthSnapshot.Empty, ProviderId = Feed.Id, ReceivedAt = DepthNow, GapBefore = DepthGapReason.Disconnected, Coverage = DepthCoverage.Disconnected | DepthCoverage.Missing, Message = message });
            }
            Publish(generation, () => SetStatus(status, message));
        }
    }

    private void ReceiveTrades(long generation, IReadOnlyList<Trade> trades)
    {
        lock (_lifecycle)
        {
            if (_cts == null || generation != _generation || _disposed) return;
            foreach (var trade in trades) _incoming.Enqueue(WithSource(trade));
            _recorder?.WriteLive(trades);
            _lastDataAt = DateTime.UtcNow.Ticks;
            if (trades.Count > 0 && _anchor != null) Volatile.Write(ref _tradedSinceAnchor, 1);
        }
    }

    private void ReceiveDepth(long generation, DepthUpdate update)
    {
        lock (_lifecycle)
        {
            if (_cts == null || generation != _generation || _disposed) return;
            _lastDataAt = DateTime.UtcNow.Ticks;
            var normalized = DepthHistory.Receive(update, DepthNow);
            EnqueueDepth(normalized);
        }
    }

    private void EnqueueDepth(DepthUpdate update)
    {
        _depthUpdates.Enqueue(update);
        _depthUpdateBytes += DepthSample.EstimateMemoryBytes(update);
        TrimDepthQueue();
    }

    private void TrimDepthQueue(bool enforceSampleLimit = false)
    {
        if (enforceSampleLimit)
        {
            long maxSample = DepthHistory.Options.MaxSampleBytes;
            int pending = _depthUpdates.Count;
            for (int index = 0; index < pending && _depthUpdates.TryDequeue(out var update); index++)
            {
                long bytes = DepthSample.EstimateMemoryBytes(update);
                if (bytes <= maxSample) _depthUpdates.Enqueue(update);
                else { _depthUpdateBytes -= bytes; _depthOverflow = true; }
            }
        }
        while ((_depthUpdates.Count > 2_048 || _depthUpdateBytes > DepthQueueByteLimit) && _depthUpdates.TryDequeue(out var removed))
        {
            _depthUpdateBytes -= DepthSample.EstimateMemoryBytes(removed);
            _depthOverflow = true;
        }
    }

    private void LoadDepthHistory(DateTime from, DateTime to, long generation, CancellationToken ct)
    {
        long request = Interlocked.Increment(ref _depthLoadRevision);
        _ = Task.Run(async () =>
        {
            try
            {
                var loaded = await DepthHistory.LoadAsync(from, to, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                Publish(generation, () => { if (request == _depthLoadRevision) DepthHistory.Apply(loaded); });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error)
            { Publish(generation, () => DepthHistory.MarkGap(Feed.Now, DepthGapReason.StorageFailure, error.Message)); }
        }, ct);
    }

    private void LoadOpenInterest(IOpenInterestProvider provider, DateTime from, DateTime to, long generation, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await provider.LoadOpenInterestAsync(Instrument, from, to, ct).ConfigureAwait(false);
                if (result.Samples.Any(s => s.Time.Kind != DateTimeKind.Utc || s.Quantity < 0))
                    throw new InvalidDataException("The provider returned invalid open-interest observations.");
                Publish(generation, () =>
                {
                    foreach (var sample in result.Samples) OpenInterest.Add(sample);
                    OpenInterest.Availability = result.Availability;
                    OpenInterest.Message = result.Message;
                    _openInterestDirty = true;
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Publish(generation, () =>
                {
                    OpenInterest.Availability = OpenInterestAvailability.Error;
                    OpenInterest.Message = ex.Message;
                    _openInterestDirty = true;
                });
            }
        }, ct);
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class GenerationSink(InstrumentData owner, long generation) : IMarketDataSink, IOpenInterestObserver
    {
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) => owner.ReceiveTrades(generation, trades);
        public void OnDepth(Instrument instrument, DepthUpdate update) => owner.ReceiveDepth(generation, update);
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) => owner.ReceiveStatus(generation, status, message);
        public void OnOpenInterest(OpenInterestSample sample)
        {
            if (sample.Time.Kind != DateTimeKind.Utc || sample.Quantity < 0)
            {
                OnOpenInterestStatus(OpenInterestAvailability.Error, "The provider returned an invalid open-interest observation.");
                return;
            }
            owner.Publish(generation, () =>
            {
                owner.OpenInterest.Add(sample);
                owner.OpenInterest.Availability = OpenInterestAvailability.Available;
                owner.OpenInterest.Message = null;
                owner._openInterestDirty = true;
            });
        }
        public void OnOpenInterestStatus(OpenInterestAvailability availability, string? message) => owner.Publish(generation, () =>
        {
            owner.OpenInterest.Availability = availability;
            owner.OpenInterest.Message = message;
            owner._openInterestDirty = true;
        });
    }

    private void SetStatus(FeedStatus status, string? message)
    {
        _status = status;
        _statusMessage = message;
        _statusDirty = true;
    }

    // ----------------------------------------------------------------- hilo de UI

    /// <summary>Aplica lo recibido. Llamar periódicamente desde el hilo de UI.</summary>
    public void Pump()
    {
        DateTime? refreshFrom = null;
        long generation;
        lock (_lifecycle)
        {
            generation = _generation;
            PumpCore();
            if (_refreshRequested && _cts != null && !_disposed)
            {
                _refreshRequested = false;
                refreshFrom = HistoryFrom;
            }
        }
        // Restart on the owning UI thread, but dispose old subscriptions outside the Pump gate.
        if (refreshFrom is { } from && _cts != null && !_disposed && generation == _generation) Start(from);
    }

    private void PumpCore()
    {
        if (_cts == null || _disposed) return;
        if (_recorder?.Error is { } recordingError) RecordingWarning = recordingError;
        long generation = _generation;
        while (_publications.TryDequeue(out var publication))
        {
            if (publication.Generation == _generation) publication.Publish();
            if (_cts == null || _disposed || generation != _generation) return;
            if (_refreshRequested) return;
        }
        if (Feed is IOpenInterestProvider provider && OpenInterest.Samples.Count > 0)
        {
            var now = Feed.Now;
            long minute = now.Ticks / TimeSpan.TicksPerMinute;
            if (_openInterestClockMinute != minute)
            {
                _openInterestClockMinute = minute;
                OpenInterest.TrimBefore(now - provider.OpenInterestRetention);
                _openInterestDirty = true;
            }
        }
        if (_openInterestDirty)
        {
            _openInterestDirty = false;
            OpenInterestChanged?.Invoke();
            if (_cts == null || _disposed || generation != _generation) return;
            OpenInterest.AcceptChanges();
        }
        if (_statusDirty)
        {
            _statusDirty = false;
            StatusChanged?.Invoke();
            if (_cts == null || _disposed || generation != _generation) return;
        }

        if (!_historyMerged)
        {
            var hist = _historyToMerge;
            _historyToMerge = null;

            if (hist != null)
            {
                Trades.Clear();
                foreach (var t in hist) Trades.Add(t);
                _historyMerged = true;
                IsLoadingHistory = false;
                HistoryProgress = 1;
                if (Feed is not ReplayFeed) DrainIncoming(skipKnown: true, raise: false);
                HistoryLoaded?.Invoke();
                if (_cts == null || _disposed || generation != _generation) return;
                StatusChanged?.Invoke();
                if (_cts == null || _disposed || generation != _generation) return;
                if (Feed is ReplayFeed) DrainIncoming(skipKnown: true, raise: true);
                if (_cts == null || _disposed || generation != _generation) return;
            }
        }
        else
        {
            var older = _olderToMerge;
            _olderToMerge = null;

            if (older != null)
            {
                // reconstruye el almacén: historia vieja + lo que ya había
                if (older.Count > 0)
                {
                    var current = new List<Trade>(Trades.Count);
                    for (int i = 0; i < Trades.Count; i++) current.Add(Trades.Get(i));
                    Trades.Clear();
                    foreach (var t in older) Trades.Add(t);
                    foreach (var t in current) Trades.Add(t);
                }

                HistoryFrom = _pendingFrom;
                IsExtending = false;
                HistoryProgress = 1;
                if (older.Count > 0) HistoryLoaded?.Invoke();
                if (_cts == null || _disposed || generation != _generation) return;
                StatusChanged?.Invoke();
                if (_cts == null || _disposed || generation != _generation) return;
            }

            DrainIncoming(skipKnown: true, raise: true);
            if (_cts == null || _disposed || generation != _generation) return;
        }

        if (_depthUnavailable)
        {
            _depthUnavailable = false;
            Depth = DepthSnapshot.Empty;
            DepthUpdated?.Invoke(new DepthUpdate { Snapshot = Depth, Coverage = DepthCoverage.Disconnected | DepthCoverage.Missing, GapBefore = DepthGapReason.Disconnected, ReceivedAt = Feed.Now, ProviderId = Feed.Id });
            if (_cts == null || _disposed || generation != _generation) return;
        }
        while (_depthUpdates.TryDequeue(out var depthUpdate))
        {
            _depthUpdateBytes -= DepthSample.EstimateMemoryBytes(depthUpdate);
            if (_depthOverflow)
            {
                _depthOverflow = false;
                depthUpdate = new DepthUpdate
                {
                    Snapshot = depthUpdate.Snapshot, Changes = depthUpdate.Changes, IsFullRefresh = depthUpdate.IsFullRefresh,
                    ProviderId = depthUpdate.ProviderId, Venue = depthUpdate.Venue, ReceivedAt = depthUpdate.ReceivedAt,
                    Sequence = depthUpdate.Sequence, FirstSequence = depthUpdate.FirstSequence, PreviousSequence = depthUpdate.PreviousSequence,
                    Coverage = depthUpdate.Coverage | DepthCoverage.Truncated, GapBefore = DepthGapReason.Overflow, Message = "Received depth updates exceeded the bounded UI queue.",
                };
            }
            Depth = depthUpdate.HasAvailableLevels ? depthUpdate.Snapshot : DepthSnapshot.Empty;
            DepthUpdated?.Invoke(depthUpdate);
            if (_cts == null || _disposed || generation != _generation) return;
        }
        DepthHistory.Refresh(DepthNow);
        if (!DepthHistory.HasFreshLevels && (Depth.HasBid || Depth.HasAsk))
        {
            Depth = DepthSnapshot.Empty;
            DepthUpdated?.Invoke(new DepthUpdate
            {
                Snapshot = Depth, ProviderId = Feed.Id, ReceivedAt = DepthNow,
                Coverage = DepthHistory.Status.Coverage | DepthCoverage.Missing,
                GapBefore = DepthGapReason.Missing, Message = "Current depth is unavailable or stale.",
            });
            if (_cts == null || _disposed || generation != _generation) return;
        }
        if (_depthHistoryVersion != DepthHistory.Version)
        {
            _depthHistoryVersion = DepthHistory.Version;
            DepthHistoryChanged?.Invoke();
        }
    }

    // Display coverage prevents duplicate candle fallbacks; it never grants observed-volume/footprint coverage.
    private static List<(DateTime From, DateTime To)> DisplayCoverage(IReadOnlyList<Trade> history)
    {
        var coverage = KlineHistory.Coverage(history);
        foreach (var trade in history)
        {
            var provenance = trade.Provenance;
            if (provenance.IsMeasured || !provenance.HasKnownAvailability) continue;
            if (provenance.ResolutionSeconds > 0) coverage.Add(provenance.SourceWindow(trade.Time));
            else
            {
                long start = trade.Time.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute;
                coverage.Add((new DateTime(start, DateTimeKind.Utc),
                    new DateTime(Math.Min(DateTime.MaxValue.Ticks, start + TimeSpan.TicksPerMinute), DateTimeKind.Utc)));
            }
        }
        coverage.Sort(static (left, right) => left.From.CompareTo(right.From));
        return coverage;
    }

    private (List<TradeIdentity> Ordered, HashSet<TradeIdentity> Seen) CaptureNewArrivals(int start)
    {
        var ordered = new List<TradeIdentity>();
        var seen = new HashSet<TradeIdentity>();
        for (int index = start; index < Trades.Count; index++)
        {
            var identity = Trades.GetIdentity(index);
            if (seen.Add(identity)) ordered.Add(identity);
        }
        return (ordered, seen);
    }

    private Trade WithSource(in Trade trade) => trade.Provenance.SourceIdentity != null ? trade :
        new Trade(trade.Id, trade.Time, trade.Price, trade.Volume, trade.Side, trade.Provenance with { SourceIdentity = _sourceIdentity });

    private void DrainIncoming(bool skipKnown, bool raise)
    {
        if (_incoming.IsEmpty) return;
        if (raise && Feed is ReplayFeed) { DrainReplayIncoming(skipKnown); return; }
        int start = Trades.Count;
        long generation = _generation;
        long lastTicks = Trades.Count > 0 ? Trades.LastTime.Ticks : long.MinValue;
        bool corrected = false;
        List<Trade>? outOfOrder = null;
        List<TradeIdentity>? arriving = null;
        HashSet<TradeIdentity>? arrivalKeys = null;
        while (_incoming.TryDequeue(out var trade))
        {
            var identity = TradeIdentity.From(trade);
            int known = skipKnown ? Trades.FindIdentity(identity) : -1;
            if (known >= 0)
            {
                var previous = Trades.Get(known);
                if (trade.Id == previous.Id && trade.Time == previous.Time && trade.Price == previous.Price &&
                    trade.Volume == previous.Volume && trade.Side == previous.Side && trade.Provenance == previous.Provenance) continue;
                if (trade.Time == previous.Time) Trades.Update(known, trade);
                else
                {
                    if (arriving == null) (arriving, arrivalKeys) = CaptureNewArrivals(start);
                    (outOfOrder ??= []).Add(trade);
                }
                corrected = true;
                continue;
            }
            if (trade.Time.Ticks < lastTicks)
            {
                if (arriving == null) (arriving, arrivalKeys) = CaptureNewArrivals(start);
                if (arrivalKeys!.Add(identity)) arriving.Add(identity);
                (outOfOrder ??= []).Add(trade);
                continue;
            }
            Trades.Add(trade);
            if (arriving != null && arrivalKeys!.Add(identity)) arriving.Add(identity);
            lastTicks = trade.Time.Ticks;
        }
        if (outOfOrder != null)
        {
            var merged = new List<Trade>(Trades.Count + outOfOrder.Count);
            for (int index = 0; index < Trades.Count; index++) merged.Add(Trades.Get(index));
            merged.AddRange(outOfOrder);
            Trades.Replace(TickFile.SortAndDedupe(merged));
            corrected = true;
        }
        if (!raise) return;
        if (corrected) HistoryLoaded?.Invoke();
        if (_cts == null || _disposed || generation != _generation) return;
        if (arriving != null)
        {
            foreach (var identity in arriving)
            {
                int index = Trades.FindIdentity(identity);
                if (index >= 0) TradesAppended?.Invoke(index, 1);
                if (_cts == null || _disposed || generation != _generation) return;
            }
        }
        else if (Trades.Count > start) TradesAppended?.Invoke(start, Trades.Count - start);
    }

    private void DrainReplayIncoming(bool skipKnown)
    {
        long generation = _generation;
        if (HistoryError != null) { _incoming.Clear(); return; }
        while (_incoming.TryDequeue(out var trade))
        {
            if (skipKnown && Trades.FindIdentity(TradeIdentity.From(trade)) >= 0) continue;
            if (!trade.HasKnownAvailability || (_lastReplayAvailableAt != DateTime.MinValue && trade.AvailableAt < _lastReplayAvailableAt))
            {
                HistoryError = "Replay event availability is unknown or out of order. Playback stopped before the event could reach the practice account.";
                if (Feed is ReplayFeed replay) replay.Paused = true;
                SetStatus(FeedStatus.Error, HistoryError);
                return;
            }
            _lastReplayAvailableAt = trade.AvailableAt;
            int start = Trades.InsertChronological(trade);
            TradesAppended?.Invoke(start, 1);
            if (_cts == null || _disposed || generation != _generation) return;
        }
    }

    public void Dispose()
    {
        if (Feed is IHistoryRefreshFeed refresh) refresh.HistoryInvalidated -= OnHistoryInvalidated;
        Stop();
        _disposed = true;
        DepthHistory.Dispose();
    }
}

/// <summary>Graba ticks en disco en un hilo aparte (no traba la UI).</summary>
public sealed class TickRecorder : IDisposable
{
    private readonly string _root;
    private readonly Instrument _instrument;
    private readonly BlockingCollection<Trade> _queue = new(new ConcurrentQueue<Trade>(), 1_000_000);
    private readonly Thread? _thread;
    private readonly FileStream? _lock;
    private TickFile.Writer? _writer;
    private DateOnly _day;
    private readonly object _writeGate = new();
    private bool _closed;
    private volatile bool _leaseReleased;
    private volatile string? _error;
    public string? Error { get => _error; private set => _error = value; }

    public TickRecorder(string root, Instrument instrument)
    {
        _root = root;
        _instrument = instrument;
        if (instrument.FeedId == IbBarImport.FeedId)
        {
            // Recover before taking append ownership; the recorder does not retain the snapshot coordinator.
            using var ownership = IbHistoryCoordinator.Enter(root, instrument.Symbol);
            _lock = ownership.StartRecorder();
        }
        else _lock = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol, recordingWriter: true);
        if (_lock == null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "recorder-" + instrument.Symbol };
        try { _thread.Start(); }
        catch
        {
            _lock.Dispose();
            _leaseReleased = true;
            throw;
        }
    }

    /// <summary>Owns the writer lease, including any asynchronous drain after a bounded Dispose.</summary>
    public bool IsActive => _lock != null && !_leaseReleased;

    public void WriteLive(IReadOnlyList<Trade> trades)
    {
        lock (_writeGate)
        {
            if (_lock == null || _closed) return;
            foreach (var trade in trades)
                if (trade.Id >= 0 && !_queue.TryAdd(trade))
                    Error = "Tick recording queue is full; some live observations were not saved.";
        }
    }

    public void WriteHistory(IReadOnlyList<Trade> trades) => WriteLive(trades);

    private void Run()
    {
        try
        {
            foreach (var t in _queue.GetConsumingEnumerable())
            {
                var day = DateOnly.FromDateTime(t.Time);
                if (_writer == null || day != _day)
                {
                    _writer?.Dispose();
                    _day = day;
                    var path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
                    TickArchive.EnsureRawDay(path);
                    if (TickFile.RequiresExtended(t)) TickFile.EnsureProvenanceFormat(path, _instrument);
                    _writer = new TickFile.Writer(path, _instrument, includeProvenance: !File.Exists(path));
                }
                if (!_writer.IncludesProvenance && TickFile.RequiresExtended(t))
                {
                    _writer.Dispose();
                    var path = TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day);
                    TickFile.EnsureProvenanceFormat(path, _instrument);
                    _writer = new TickFile.Writer(path, _instrument);
                }

                _writer.Write(t);
                if (_queue.Count == 0) _writer.Flush();
            }
        }
        catch (Exception error)
        {
            Error = "Tick recording stopped; incoming market data remains available: " + error.Message;
        }
        finally
        {
            lock (_writeGate)
            {
                _closed = true;
                if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
            }
            try { _writer?.Dispose(); }
            catch (Exception error) { Error = "Tick recording could not finish saving: " + error.Message; }
            finally
            {
                _lock?.Dispose();
                _leaseReleased = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_closed) return;
            _closed = true;
            _queue.CompleteAdding();
        }
        _thread?.Join(2000);
    }
}

/// <summary>Administra los <see cref="InstrumentData"/> abiertos (uno por feed+símbolo).</summary>
public sealed class MarketDataHub : IDisposable
{
    private readonly Dictionary<string, InstrumentData> _items = new();

    public string? RecordingRoot { get; set; }
    public DepthRecordingOptions DepthRecording { get; set; } = new();
    public TimeSpan TickWindow { get; set; } = TimeSpan.FromDays(7);
    public int MaxDiskTrades { get; set; } = 2_000_000;
    public string? CompactRoot { get; set; }
    public bool UseCompactHistory { get; set; }
    public int CompactMinutes { get; set; } = 5;
    public int MaxCompactTrades { get; set; } = 500_000;

    /// <summary>Si los instrumentos nuevos graban el vivo.</summary>
    public bool RecordLive { get; set; } = true;

    public IReadOnlyCollection<InstrumentData> Items => _items.Values;

    public InstrumentData GetOrStart(IMarketDataFeed feed, Instrument instrument, TimeSpan history)
    {
        string key = feed is IDataScopeFeed scope ? instrument.Key + "\u001f" + scope.DataScopeKey : instrument.Key;
        if (_items.TryGetValue(key, out var d))
        {
            if (feed is IDataScopeFeed && !ReferenceEquals(d.Feed, feed))
                throw new InvalidOperationException("An active data scope is already bound to a different feed instance. Use the stable scoped feed or a new configuration scope.");
            return d;
        }
        d = new InstrumentData(instrument, feed)
        {
            RecordingRoot = RecordingRoot, RecordLive = RecordLive, TickWindow = TickWindow,
            RawTradeLimit = Math.Clamp(MaxDiskTrades, 1_000, 30_000_000), CompactRoot = CompactRoot,
            UseCompactHistory = UseCompactHistory, CompactMinutes = CompactMinutes,
            CompactTradeLimit = Math.Clamp(MaxCompactTrades, 1_000, 5_000_000),
            DepthRecording = DepthRecording,
        };
        _items[key] = d;
        d.Start(feed.Now - history);
        return d;
    }

    public void Release(InstrumentData data)
    {
        // los datos se mantienen mientras la app esté abierta (cambiar de gráfico es instantáneo)
    }

    public void PumpAll()
    {
        foreach (var d in _items.Values) d.Pump();
    }

    /// <summary>Reconecta todos los instrumentos en vivo (despertó la Mac, volvió internet...).</summary>
    public void ReconnectAll()
    {
        foreach (var d in _items.Values) d.Reconnect();
    }

    public void Dispose()
    {
        foreach (var d in _items.Values) d.Dispose();
        _items.Clear();
    }
}
