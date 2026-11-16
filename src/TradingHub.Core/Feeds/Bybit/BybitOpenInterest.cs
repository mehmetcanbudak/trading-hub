using System.Globalization;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Bybit;

/// <summary>
/// Bybit linear-perpetual open interest at five-minute resolution from the public market API, in base-asset contracts.
/// A standalone <see cref="IOpenInterestProvider"/> so Bybit charts and the derivatives pane share the OI contract
/// without changing what the Bybit market-data feed polls.
/// </summary>
public sealed class BybitOpenInterest : IOpenInterestProvider, IDisposable
{
    private const long Step = 300_000;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _rest;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();

    public BybitOpenInterest(HttpClient? httpClient = null, Uri? restOrigin = null, bool ownsHttpClient = false,
        Func<DateTime>? utcNow = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _ownsHttp = httpClient == null || ownsHttpClient;
        _rest = (restOrigin?.GetLeftPart(UriPartial.Authority) ?? "https://api.bybit.com") + "/v5/market";
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
    }

    public TimeSpan OpenInterestRetention => TimeSpan.FromDays(3650);
    public TimeSpan OpenInterestMaxAge => TimeSpan.FromMinutes(10);
    public bool SupportsOpenInterest(Instrument instrument) => !string.IsNullOrWhiteSpace(instrument.Symbol);

    public async Task<OpenInterestHistoryResult> LoadOpenInterestAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        ct = linked.Token;
        DateTime end = to < _utcNow() ? to : _utcNow();
        if (from >= end) return new(Array.Empty<OpenInterestSample>(), null, null, OpenInterestAvailability.Unavailable, "The requested open-interest range is empty.");
        var samples = new SortedDictionary<DateTime, OpenInterestSample>();
        long startMs = Ms(from), endMs = Ms(end);
        string? cursor = null;
        for (int page = 0; page < 10_000; page++)
        {
            ct.ThrowIfCancellationRequested();
            string url = $"{_rest}/open-interest?category=linear&symbol={Uri.EscapeDataString(instrument.Symbol)}&intervalTime=5min&startTime={startMs}&endTime={endMs}&limit=200"
                + (string.IsNullOrEmpty(cursor) ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            using var doc = await GetAsync(url, ct).ConfigureAwait(false);
            var result = doc.RootElement.GetProperty("result");
            int rows = 0;
            foreach (var row in ParseRows(result))
            {
                rows++;
                if (row.Time >= from && row.Time < end) samples[row.Time] = row;
            }
            cursor = result.TryGetProperty("nextPageCursor", out var next) ? next.GetString() : null;
            if (rows == 0 || string.IsNullOrEmpty(cursor)) break;
        }
        var values = samples.Values.ToArray();
        if (values.Length == 0) return new(values, null, null, OpenInterestAvailability.Unavailable, "No Bybit open-interest observations are available in this range.");
        bool partial = values[0].Time - from > OpenInterestMaxAge || end - values[^1].Time > OpenInterestMaxAge;
        for (int i = 1; i < values.Length && !partial; i++) partial = values[i].Time - values[i - 1].Time > OpenInterestMaxAge;
        return new(values, values[0].Time, values[^1].Time, partial ? OpenInterestAvailability.Partial : OpenInterestAvailability.Available,
            partial ? "Partial Bybit open-interest coverage at five-minute resolution." : null);
    }

    /// <summary>Rows {openInterest, timestamp}; negative values are rejected.</summary>
    public static IEnumerable<OpenInterestSample> ParseRows(JsonElement result)
    {
        foreach (var row in result.GetProperty("list").EnumerateArray())
        {
            decimal quantity = decimal.Parse(row.GetProperty("openInterest").GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (quantity < 0) throw new InvalidDataException("Bybit returned negative open interest.");
            long time = long.Parse(row.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture);
            yield return new OpenInterestSample(DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime, quantity);
        }
    }

    public IDisposable SubscribeOpenInterest(Instrument instrument, IOpenInterestObserver observer)
    {
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
                        using var doc = await GetAsync($"{_rest}/open-interest?category=linear&symbol={Uri.EscapeDataString(instrument.Symbol)}&intervalTime=5min&limit=1", token).ConfigureAwait(false);
                        foreach (var sample in ParseRows(doc.RootElement.GetProperty("result")).Take(1)) observer.OnOpenInterest(sample);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception ex) { observer.OnOpenInterestStatus(OpenInterestAvailability.Error, ex.Message); }
                    await _delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { cts.Dispose(); }
        });
        return new Subscription(cts);
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("retCode", out var code) && code.GetInt32() != 0)
        {
            string message = doc.RootElement.TryGetProperty("retMsg", out var m) ? m.GetString() ?? "" : "";
            doc.Dispose();
            throw new InvalidOperationException("Bybit: " + message);
        }
        return doc;
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / Step * Step;

    private sealed class Subscription(CancellationTokenSource cts) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        if (_ownsHttp) _http.Dispose();
    }
}
