using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Feeds;

/// <summary>Bar-derived history, never genuine ticks or recorder input. Negative IDs identify synthetic trades.</summary>
public interface IBarHistoryFeed
{
    Task<IReadOnlyList<Trade>> LoadBarHistoryAsync(Instrument instrument, DateTime from, DateTime to, CancellationToken ct);
}

/// <summary>Immutable identity of a configured data source. Different scopes must not share active data.</summary>
public interface IDataScopeFeed
{
    string DataScopeKey { get; }
}

public interface ISessionSettingsFeed
{
    SessionSettings GetSessionSettings(Instrument instrument);
}

/// <summary>Invalidates retained instrument history after configuration or committed imports; null targets all instruments.</summary>
public interface IHistoryRefreshFeed
{
    event Action<string?>? HistoryInvalidated;
}
