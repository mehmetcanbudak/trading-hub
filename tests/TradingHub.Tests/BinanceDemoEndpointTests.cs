using System.Reflection;
using TradingHub.App.Services;
using TradingHub.Feeds.Binance;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>
/// Binance documents the USDⓈ-M testnet as its demo service at demo-fapi.binance.com; testnet.binancefuture.com, the
/// earlier default, is an alias of the same service. Moving the default must not orphan what was owned under the alias.
/// </summary>
public sealed class BinanceDemoEndpointTests
{
    /// <summary>The environment earlier releases persisted for the default USDⓈ-M testnet route.</summary>
    private const string PersistedTestnetEnvironment = "testnet|https://testnet.binancefuture.com/";

    [Fact]
    public void Futures_testnet_orders_go_to_the_documented_demo_host_and_spot_testnet_is_unchanged()
    {
        Assert.Equal("https://demo-fapi.binance.com", new BinanceAccountSettings { Futures = true, Testnet = true }.RestBase);
        Assert.Equal("https://testnet.binance.vision", new BinanceAccountSettings { Futures = false, Testnet = true }.RestBase);
        Assert.Equal("https://fapi.binance.com", new BinanceAccountSettings { Futures = true, Testnet = false }.RestBase);
        Assert.Equal("https://api.binance.com", new BinanceAccountSettings { Futures = false, Testnet = false }.RestBase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://demo-fapi.binance.com")]
    [InlineData("https://testnet.binancefuture.com")]
    public void Futures_demo_route_recovers_the_context_and_intents_owned_under_the_testnet_alias(string? baseUrl)
    {
        string root = Path.Combine(Path.GetTempPath(), "TradingHub-binance-demo-endpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            var services = SearchZoomFixture.Services();
            var profile = new ConnectionProfile
            {
                Id = "binance-demo-" + Guid.NewGuid().ToString("N"), Kind = ConnectionKind.BinanceFutures, Name = "Binance demo",
                Enabled = true, Testnet = true,
                TradingNamespace = Guid.NewGuid().ToString("N"), TradingGeneration = Guid.NewGuid().ToString("N"),
            };
            services.Connections.Profiles.Add(profile);
            Initialize(services.Connections, "_tradingRoutes");
            SearchZoomFixture.Set(services.Connections, "_filePath", Path.Combine(root, "connections.json"));
            Initialize(services, "_orderContexts");
            Initialize(services, "_wireAccounts");
            SearchZoomFixture.Set(services, "_executionRoot", root);

            // The context an earlier release built for this connection, with one owned intent persisted before sending.
            var persisted = (OrderContext)typeof(AppServices).GetMethod("TradingContext", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(services, new object?[] { profile, profile.Id, "Binance", "usd-m-futures", PersistedTestnetEnvironment, null, null })!;
            var intent = persisted.Prepare(persisted.ManualOwner,
                new OrderRequest("binance-futures:BTCUSDT", OrderDirections.Buy, OrderTypes.Limit, 0.001m, 70000m));
            Assert.Null(intent.Error);

            // Restart: the in-memory contexts are gone and only the ownership journal remains.
            ((Dictionary<string, OrderContext>)Field(services, "_orderContexts")).Clear();
            var recovered = services.BinanceContext(profile, profile.Id,
                new BinanceAccountSettings { Futures = true, Testnet = true, BaseUrl = baseUrl });

            Assert.NotSame(persisted, recovered);
            Assert.Equal(persisted.Descriptor, recovered.Descriptor);
            Assert.Equal(persisted.StorageRoot, recovered.StorageRoot);
            Assert.Equal(intent.Data, recovered.Find(intent.Data.Id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private static void Initialize(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(target, Activator.CreateInstance(field.FieldType,
            field.FieldType.GetConstructor(new[] { typeof(StringComparer) }) != null ? new object[] { StringComparer.Ordinal } : Array.Empty<object>()));
    }
}
