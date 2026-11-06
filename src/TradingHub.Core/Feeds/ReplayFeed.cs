using System.Diagnostics;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Feeds;

/// <summary>
/// Reproduce operaciones grabadas o descargadas como si fueran en vivo, con velocidad ajustable,
/// pausa y salto hacia adelante. La historia previa al inicio se carga de los mismos archivos.
/// </summary>
public sealed class ReplayFeed : IMarketDataFeed
{
    public const string FeedPrefix = "replay";
    private readonly string _root;
    private readonly Instrument _source;
    private Thread? _thread;
    private volatile bool _stop;
    private long _clockTicks;
    private long _seekTicks = -1;
    private readonly object _lifecycle = new();
    private bool _disposed;

    private readonly CancellationTokenSource _readCancellation = new();
    public ReplayFeed(string root, Instrument source, DateTime startUtc, DateTime endUtc)
        : this(root, source, startUtc, endUtc, Guid.NewGuid()) { }

    public ReplayFeed(string root, Instrument source, DateTime startUtc, DateTime endUtc, Guid runId)
    {
        _root = root;
        if (startUtc.Kind != DateTimeKind.Utc || endUtc.Kind != DateTimeKind.Utc || endUtc <= startUtc)
            throw new ArgumentException("Replay needs an increasing UTC interval.");
        RunId = runId;
        _source = source;
        Start = startUtc;
        End = endUtc;
        _clockTicks = startUtc.Ticks;
        Instrument = new Instrument
        {
            Symbol = source.Symbol,
            FeedId = $"{FeedPrefix}-{runId:N}",
            Exchange = "Replay · " + source.Exchange,
            TickSize = source.TickSize,
            VolumeStep = source.VolumeStep,
            PointValue = source.PointValue,
            BaseAsset = source.BaseAsset,
            QuoteAsset = source.QuoteAsset,
            Description = source.Description,
        };
    }

    public string Id => Instrument.FeedId;
    public string DisplayName => $"Replay {_source.Symbol}";
    public bool SupportsHistory => true;
    public bool IsLive => false;
    public Instrument Instrument { get; }
    public Instrument Source => _source;
    public Guid RunId { get; }
    public DateTime Start { get; }
    public DateTime End { get; }

    /// <summary>Multiplicador del tiempo real (1 = tiempo real).</summary>
    public double Speed { get; set; } = 1;

    public bool Paused { get; set; }
    private volatile bool _finished;
    public bool IsFinished { get => _finished; private set => _finished = value; }

    /// <summary>Instante del mercado que se está reproduciendo.</summary>
    public DateTime Clock => new(Interlocked.Read(ref _clockTicks), DateTimeKind.Utc);

    public DateTime Now => Start;

    public double Progress => End > Start ? Math.Clamp((Clock - Start).TotalSeconds / (End - Start).TotalSeconds, 0, 1) : 1;

    /// <summary>Salta hacia adelante (las operaciones intermedias se entregan de una).</summary>
    public void SeekForward(DateTime target)
    {
        if (target > Clock) Interlocked.Exchange(ref _seekTicks, Math.Min(target.Ticks, End.Ticks));
    }

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Instrument>>(new[] { Instrument });

    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var to = toUtc > Start ? Start : toUtc;
            var batch = new List<Trade>(10_000);
            foreach (var t in RequireKnownAvailability(HistoricalData.ReadAvailable(_root, _source.FeedId, _source.Symbol, fromUtc, to, ct)))
            {
                ct.ThrowIfCancellationRequested();
                if (fromId.HasValue && t.IsObservedTrade && t.Id < fromId.Value) continue;
                batch.Add(t);
                if (batch.Count < 10_000) continue;
                if (!onBatch(batch)) return;
                batch = new List<Trade>(10_000);
            }

            if (batch.Count > 0) onBatch(batch);
            progress?.Report(1);
        }, ct);
    }

    private static List<Trade> RequireKnownAvailability(List<Trade> events)
    {
        if (events.Any(trade => !trade.Provenance.HasKnownAvailability))
            throw new InvalidDataException("Replay history contains events with unknown source availability.");
        return events;
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread != null) throw new InvalidOperationException("A replay feed supports one owned subscription.");
            _stop = false;
            _thread = new Thread(() => Run(sink)) { IsBackground = true, Name = "replay-" + RunId.ToString("N")[..8] };
            _thread.Start();
            return new Unsub(this);
        }
    }

    private void Run(IMarketDataSink sink)
    {
        try
        {
            sink.OnStatus(Instrument, FeedStatus.Connected, $"Replay from {Start:yyyy-MM-dd HH:mm} UTC");
            using var e = RequireKnownAvailability(HistoricalData.ReadAvailable(_root, _source.FeedId, _source.Symbol, Start, End, _readCancellation.Token)).GetEnumerator();
            bool has = e.MoveNext();
            var sw = Stopwatch.StartNew();
            double lastMs = 0;
            var batch = new List<Trade>(4096);
            while (!_stop && has)
            {
                double nowMs = sw.Elapsed.TotalMilliseconds;
                double dtMs = nowMs - lastMs;
                lastMs = nowMs;
                long seek = Interlocked.Exchange(ref _seekTicks, -1);
                if (Paused && seek < 0) { Thread.Sleep(15); continue; }
                long clock = Interlocked.Read(ref _clockTicks);
                if (seek > 0) clock = seek;
                else if (double.IsFinite(Speed) && Speed > 0)
                    clock += (long)Math.Min(End.Ticks - clock, dtMs * Math.Min(Speed, 100_000) * TimeSpan.TicksPerMillisecond);
                clock = Math.Clamp(clock, Start.Ticks, End.Ticks);
                Interlocked.Exchange(ref _clockTicks, clock);
                batch.Clear();
                while (has && e.Current.AvailableAt.Ticks <= clock && batch.Count < 20_000 && !_stop)
                {
                    batch.Add(e.Current);
                    has = e.MoveNext();
                }
                if (batch.Count > 0 && !_stop) sink.OnTrades(Instrument, batch.ToArray());
                if (batch.Count < 20_000) Thread.Sleep(15);
            }
            if (!has && !_stop)
            {
                IsFinished = true;
                Interlocked.Exchange(ref _clockTicks, End.Ticks);
                sink.OnStatus(Instrument, FeedStatus.Disconnected, "Replay finished");
            }
        }
        catch (Exception ex)
        {
            if (!_stop) sink.OnStatus(Instrument, FeedStatus.Error, "Replay history could not be read: " + ex.Message);
            IsFinished = true;
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lifecycle)
        {
            _stop = true;
            try { _readCancellation.Cancel(); } catch (ObjectDisposedException) { }
            thread = _thread;
        }
        if (thread != null && thread != Thread.CurrentThread) thread.Join();
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _readCancellation.Dispose();
    }

    private sealed class Unsub(ReplayFeed feed) : IDisposable
    {
        public void Dispose() => feed.Stop();
    }
}
