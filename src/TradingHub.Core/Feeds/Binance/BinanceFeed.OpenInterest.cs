using TradingHub.Market;

namespace TradingHub.Feeds.Binance;

public sealed partial class BinanceFeed
{
    public TimeSpan OpenInterestRetention => TimeSpan.FromDays(30);
    public TimeSpan OpenInterestMaxAge => TimeSpan.FromMinutes(10);
    public bool SupportsOpenInterest(Instrument instrument) => _futures && instrument.FeedId == FuturesId && !string.IsNullOrWhiteSpace(instrument.Symbol);

    public async Task<OpenInterestSample> GetOpenInterestAsync(Instrument instrument, CancellationToken ct)
    {
        if (!SupportsOpenInterest(instrument)) throw new NotSupportedException("Open interest requires a Binance USD-M Futures instrument.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        using var doc = await GetAsync($"/openInterest?symbol={Uri.EscapeDataString(instrument.Symbol)}", 1, linked.Token).ConfigureAwait(false);
        var root = doc.RootElement;
        decimal quantity = Dec(root.GetProperty("openInterest"));
        if (quantity < 0) throw new InvalidDataException("Binance returned negative open interest.");
        return new OpenInterestSample(DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("time").GetInt64()).UtcDateTime, quantity);
    }

    public async Task<OpenInterestHistoryResult> LoadOpenInterestAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
    {
        if (!SupportsOpenInterest(instrument)) return new(Array.Empty<OpenInterestSample>(), null, null,
            OpenInterestAvailability.Unsupported, "Open interest is available only for Binance USD-M Futures instruments.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        DateTime now = _utcNow();
        DateTime earliest = now - OpenInterestRetention;
        DateTime start = from > earliest ? from : earliest;
        DateTime end = to < now ? to : now;
        if (start >= end) return new(Array.Empty<OpenInterestSample>(), null, null,
            OpenInterestAvailability.Unavailable, "Binance open-interest history retains only the latest month at five-minute resolution.");
        var samples = new SortedDictionary<DateTime, OpenInterestSample>();
        long cursor = Ms(start), endMs = Ms(end);
        const long step = 300_000;
        while (cursor < endMs)
        {
            ct.ThrowIfCancellationRequested();
            // This endpoint is at the origin root, NOT under /fapi/v1.
            long pageEnd = Math.Min(endMs - 1, cursor + 500 * step - 1);
            using var doc = await GetAsync($"/futures/data/openInterestHist?symbol={Uri.EscapeDataString(instrument.Symbol)}&period=5m&limit=500&startTime={cursor}&endTime={pageEnd}",
                1, ct, rootPath: true).ConfigureAwait(false);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                long time = row.GetProperty("timestamp").GetInt64();
                var sample = new OpenInterestSample(DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime, Dec(row.GetProperty("sumOpenInterest")));
                if (sample.Quantity < 0) throw new InvalidDataException("Binance returned negative open interest.");
                if (time >= cursor && time <= pageEnd && sample.Time >= start && sample.Time < end) samples[sample.Time] = sample;
            }
            // Advance the covered request window even when a listing has no observations.
            cursor = pageEnd + 1;
        }
        var values = samples.Values.ToArray();
        if (values.Length == 0) return new(values, null, null, OpenInterestAvailability.Unavailable, "No open-interest observations are available in this range.");
        bool isPartial = from < earliest || values[0].Time - start > OpenInterestMaxAge || end - values[^1].Time > OpenInterestMaxAge;
        for (int i = 1; i < values.Length && !isPartial; i++)
            isPartial = values[i].Time - values[i - 1].Time > OpenInterestMaxAge;
        return new(values, values[0].Time, values[^1].Time, isPartial ? OpenInterestAvailability.Partial : OpenInterestAvailability.Available,
            isPartial ? "Partial open-interest coverage; Binance retains only the latest month at five-minute resolution." : null);
    }

    public IDisposable SubscribeOpenInterest(Instrument instrument, IOpenInterestObserver observer)
    {
        if (!SupportsOpenInterest(instrument)) throw new NotSupportedException("Open interest requires a Binance USD-M Futures instrument.");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var sample = await GetOpenInterestAsync(instrument, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        observer.OnOpenInterest(sample);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception ex) { observer.OnOpenInterestStatus(OpenInterestAvailability.Error, ex.Message); }
                    await _delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { cts.Dispose(); }
        });
        return new OpenInterestSubscription(cts);
    }

    private sealed class OpenInterestSubscription(CancellationTokenSource cts) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { } // The owned polling task already completed and disposed it.
        }
    }
}
