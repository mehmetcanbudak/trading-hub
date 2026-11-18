using System.Net;
using TradingHub.App.Services;
using TradingHub.Feeds;
using TradingHub.Feeds.External;
using TradingHub.Market;
using static TradingHub.Tests.VenueFixtures;

namespace TradingHub.Tests;

/// <summary>Options, comparison candles and custom-API rules of the external data hubs, against in-process fakes.</summary>
public sealed class FeedReviewExternalTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_retired_providers_failure_after_a_swap_does_not_reach_the_new_providers_cache()
    {
        var data = new OptionsData(null, () => Now);
        var old = new GatedProvider("Old");
        data.Provider = old;
        var refresh = data.RefreshChainAsync("BTC", CancellationToken.None);
        await old.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        data.Provider = new GatedProvider("New");
        // Disposing the retired provider's client cancels its request without the caller's token.
        old.Gate.SetException(new TaskCanceledException("retired client"));
        Assert.Null(await refresh);
        Assert.Null(data.ChainError("BTC"));
        Assert.Null(data.LastError);

        var oldTrades = new GatedProvider("Old trades") { GateTrades = true };
        data.Provider = oldTrades;
        var load = data.LoadTradesAsync("BTC", Now.AddHours(-1), Now, CancellationToken.None);
        await oldTrades.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = new GatedProvider("New trades");
        data.Provider = replacement;
        oldTrades.Gate.SetException(new TaskCanceledException("retired client"));
        await load;
        Assert.Null(data.LastError);

        // No retry delay was left behind: the new provider's trades load at once.
        data.TradesBetween("BTC", Now.AddHours(-1), Now);
        await data.WhenIdleAsync();
        Assert.True(Volatile.Read(ref replacement.TradeCalls) > 0);
    }

    [Fact]
    public async Task Identical_option_fills_in_one_fetch_are_all_kept_and_a_re_read_adds_none()
    {
        var clock = Now;
        var time = Now.AddMinutes(-2);
        OptionTrade Fill(string id = "") => new(time, "BTC-17NOV26-100000-C", Now.AddDays(7), 100_000, OptionRight.Call, 2_000, 1, 1, 0.5, 90_000, 1, "Deribit", id);
        var provider = new FixedTrades();
        var data = new OptionsData(null, () => clock) { Provider = provider };

        // One aggressive order filling three resting orders: same millisecond, contract, price, size and side.
        provider.Trades = new[] { Fill(), Fill(), Fill() };
        await data.LoadTradesAsync("BTC", Now.AddHours(-1), Now, CancellationToken.None);
        Assert.Equal(3, data.TradesBetween("BTC", Now.AddHours(-1), Now).Count);

        // The tail refresh re-reads the last five minutes: the held fills are matched, not added again.
        clock = Now.AddMinutes(2);
        await data.LoadTradesAsync("BTC", Now.AddHours(-1), clock, CancellationToken.None);
        Assert.Equal(3, data.TradesBetween("BTC", Now.AddHours(-1), clock).Count);

        // With provider ids, a repeated id is one trade and distinct ids are distinct trades.
        var withIds = new OptionsData(null, () => Now) { Provider = new FixedTrades { Trades = new[] { Fill("a"), Fill("a"), Fill("b") } } };
        await withIds.LoadTradesAsync("BTC", Now.AddHours(-1), Now, CancellationToken.None);
        Assert.Equal(new[] { "a", "b" }, withIds.TradesBetween("BTC", Now.AddHours(-1), Now).Select(t => t.TradeId).Order());
    }

    [Fact]
    public async Task Comparison_candles_of_a_symbol_listed_after_the_start_reload_only_the_tail()
    {
        var clock = Now;
        var listing = Now.AddDays(-2);
        var source = new ListedLater(listing);
        var hub = new ComparisonKlines(_ => source, () => clock);
        var from = Now.AddDays(-10);

        var (klines, _) = hub.Get("NEWUSDT", true, from, 1);
        await hub.WhenIdleAsync();
        Assert.Equal(from, Assert.Single(source.Starts));

        clock = Now.AddSeconds(30);
        hub.Get("NEWUSDT", true, from, 1);
        await hub.WhenIdleAsync();

        Assert.Equal(2, source.Starts.Count);
        Assert.True(source.Starts[1] >= listing, $"the refresh restarted at {source.Starts[1]:O}");
        (klines, _) = hub.Get("NEWUSDT", true, from, 1);
        Assert.Equal(new DateTimeOffset(listing).ToUnixTimeMilliseconds(), klines[0].OpenTimeMs);
    }

    [Fact]
    public async Task Deribit_reports_a_rate_limit_page_as_its_http_status()
    {
        using var handler = new Http((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("<html>Too Many Requests</html>") });
        using var http = new HttpClient(handler);
        var deribit = new DeribitOptions(http, new Uri("https://deribit.fixture/api/v2/public/"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => deribit.ChainAsync("BTC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Contains("429", error.Message);
    }

    [Fact]
    public void A_custom_api_key_is_only_sent_over_https_or_to_this_machine()
    {
        Assert.Contains("https://", ConnectionManager.ExternalUrlProblem("http://provider.example/api/options"));
        Assert.Equal("Enter the API base URL.", ConnectionManager.ExternalUrlProblem("provider.example/api"));
        Assert.Null(ConnectionManager.ExternalUrlProblem("https://provider.example/api/options"));
        Assert.Null(ConnectionManager.ExternalUrlProblem("http://127.0.0.1:8080/v1"));
        Assert.Null(ConnectionManager.ExternalUrlProblem("http://localhost:5000"));
    }

    private sealed class GatedProvider(string name) : IOptionsProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GateTrades { get; init; }
        public int TradeCalls;
        public string Name => name;
        public bool Supports(string underlying) => underlying == "BTC";

        public async Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct)
        {
            Started.TrySetResult();
            await Gate.Task;
            return null;
        }

        public async Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct)
        {
            Interlocked.Increment(ref TradeCalls);
            if (GateTrades)
            {
                Started.TrySetResult();
                await Gate.Task;
            }
            return Array.Empty<OptionTrade>();
        }

        public Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OptionDaily>>(Array.Empty<OptionDaily>());
    }

    /// <summary>Answers every trade request with the same trades (the provider clips nothing; the hub does).</summary>
    private sealed class FixedTrades : IOptionsProvider
    {
        public IReadOnlyList<OptionTrade> Trades { get; set; } = Array.Empty<OptionTrade>();
        public string Name => "Fixed";
        public bool Supports(string underlying) => underlying == "BTC";
        public Task<OptionsChain?> ChainAsync(string underlying, CancellationToken ct) => Task.FromResult<OptionsChain?>(null);

        public Task<IReadOnlyList<OptionTrade>> TradesAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OptionTrade>>(Trades.Where(t => t.Time >= from && t.Time < to).ToArray());

        public Task<IReadOnlyList<OptionDaily>> DailyAsync(string underlying, DateTime from, DateTime to, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OptionDaily>>(Array.Empty<OptionDaily>());
    }

    /// <summary>A symbol whose candles start at its listing: earlier requests return nothing before it.</summary>
    private sealed class ListedLater(DateTime listing) : IKlineHistory
    {
        public List<DateTime> Starts { get; } = new();

        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(listing);

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
            Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            lock (Starts) Starts.Add(fromUtc);
            var first = fromUtc > listing ? fromUtc : listing;
            var bars = Enumerable.Range(0, 3).Select(i => first.AddMinutes(i)).Where(t => t < toUtc)
                .Select(t => new Kline(new DateTimeOffset(t).ToUnixTimeMilliseconds(), 1, 1, 1, 1, 1, 0, false)).ToArray();
            if (bars.Length > 0) onBatch(bars);
            return Task.CompletedTask;
        }
    }
}
