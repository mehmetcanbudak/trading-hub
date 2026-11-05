using TradingHub.Data;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Rithmic;
using TradingHub.Feeds.Tradovate;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>Real broker adapters against owned fake transports, with explicit disposable pre-send identity storage.</summary>
internal sealed class NativeOrderTestFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TradingHub-native-order-test-" + Guid.NewGuid().ToString("N"));

    public BinanceBroker Binance(InstrumentData data, BinanceAccountSettings settings, string name, OrderContext? orderContext = null) =>
        orderContext == null
            ? Configure(new BinanceBroker(data, settings, name), "Binance", settings.Futures ? "usd-m-futures" : "spot")
            : new BinanceBroker(data, settings, name, orderContext);

    public TradovateBroker Tradovate(InstrumentData data, TradovateTradingSession session, TradovateAccount account, string name, OrderContext? orderContext = null) =>
        orderContext == null
            ? Configure(new TradovateBroker(data, session, account, name), "Tradovate", "futures")
            : new TradovateBroker(data, session, account, name, orderContext);

    public RithmicBroker Rithmic(InstrumentData data, RithmicTradingSession session, string account, string name,
        Func<CancellationToken, Task<(string Symbol, string Exchange)>> contract) =>
        Configure(new RithmicBroker(data, session, account, name, contract), "Rithmic", "futures");

    private T Configure<T>(T account, string provider, string product) where T : ITradingAccount
    {
        string id = Guid.NewGuid().ToString("N");
        account.OrderContext.Configure(new AccountContextDescriptor(id, provider, product, "owned-loopback-fixture", id, "fixture-generation"),
            Path.Combine(_root, id));
        return account;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
