using System.Reflection;
using System.Runtime.CompilerServices;
using SkiaSharp;
using TradingHub.App.Services;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;
// Shared with the actual headless scene; no production control inspection API is needed.
public static class SearchZoomFixture
{
    public static readonly DateTime Start = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    public static Instrument Instrument(string symbol) => new() { Symbol = symbol, Description = symbol + " fixture", FeedId = "search-zoom-fixture", TickSize = 1, VolumeStep = 1 };

    public static ChartModel Model(int count, Func<int, (decimal Open, decimal High, decimal Low, decimal Close)>? prices = null)
    {
        var instrument = Instrument("ZOOM");
        var data = new InstrumentData(instrument, new SimulatedFeed());
        long id = 0;
        for (int i = 0; i < count; i++)
        {
            decimal baseline = 1000 + i % 43;
            var p = prices?.Invoke(i) ?? (baseline, baseline + (i % 47 == 0 ? 100 : 8), baseline - (i % 53 == 0 ? 100 : 8), baseline + i % 5 - 2);
            data.Trades.Add(new Trade(++id, Start.AddMinutes(i), p.Item1, 1, TradeSide.Buy));
            data.Trades.Add(new Trade(++id, Start.AddMinutes(i).AddSeconds(10), p.Item2, 1, TradeSide.Buy));
            data.Trades.Add(new Trade(++id, Start.AddMinutes(i).AddSeconds(20), p.Item3, 1, TradeSide.Sell));
            data.Trades.Add(new Trade(++id, Start.AddMinutes(i).AddSeconds(30), p.Item4, 1, TradeSide.Sell));
        }
        return new ChartModel(data, ChartPeriod.M1, new ChartSettings());
    }

    public static AppServices Services(params (ConnectionProfile Profile, SearchZoomFeed Feed)[] sources)
    {
        // Bypass application startup, storage, global live-runner subscriptions and real feeds.
        var manager = (ConnectionManager)RuntimeHelpers.GetUninitializedObject(typeof(ConnectionManager));
        Set(manager, "<Profiles>k__BackingField", sources.Select(s => s.Profile).ToList());
        Set(manager, "_feeds", sources.ToDictionary(s => s.Profile.Id, s => (IMarketDataFeed)s.Feed));
        var services = (AppServices)RuntimeHelpers.GetUninitializedObject(typeof(AppServices));
        Set(services, "<Connections>k__BackingField", manager);
        Set(services, "<Settings>k__BackingField", new AppSettings());
        Set(services, "<Work>k__BackingField", new OwnedOperationRegistry());
        return services;
    }

    public static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    public static void RenderCandles(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartStyle style, Dictionary<int, SKColor>? paint = null)
    {
        var type = typeof(ChartRenderer).Assembly.GetType("TradingHub.Charting.Rendering.CandleRenderer")!;
        var renderer = Activator.CreateInstance(type, nonPublic: true)!;
        type.GetMethod("Render")!.Invoke(renderer, new object?[] { canvas, model, layout, layout.Main, style, paint });
    }
}

public sealed class SearchZoomFeed(params Instrument[] instruments) : IMarketDataFeed
{
    public string Id => "search-zoom-fixture";
    public string DisplayName => "Fake instrument source";
    public bool SupportsHistory => false;
    public bool IsLive => false;
    public int Requests { get; private set; }
    public CancellationToken LastToken { get; private set; }
    public Func<CancellationToken, Task<IReadOnlyList<Instrument>>>? Response { get; set; }
    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        Requests++;
        LastToken = ct;
        return Response?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<Instrument>>(instruments);
    }
    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException("Instrument-list fixture has no history transport.");
    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => throw new NotSupportedException("Instrument-list fixture has no live transport.");
    public void Dispose() { }
}
