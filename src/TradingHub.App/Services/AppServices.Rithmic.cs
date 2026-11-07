using TradingHub.Data;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.App.Services;

public sealed record RithmicPositionTarget(RithmicBroker Broker, RithmicCloseConfirmation Confirmation);
public sealed record RithmicOrderTarget(RithmicTradingSession Session, string Account, string Basket);

public sealed partial class AppServices
{
    private const string RithmicAdmissionClosed = "Trading Hub is shutting down; new Rithmic account contexts are not admitted.";
    private System.Runtime.CompilerServices.ConditionalWeakTable<RithmicTradingSession, RithmicFeed>? _rithmicContractFeeds;
    private List<Task>? _rithmicRetirementTasks;

    internal RithmicTradingSession? DiscoverRithmicSession(ConnectionProfile profile)
    {
        lock (_shutdownGate)
            return IsShuttingDown ? null : RithmicSessionFor(profile);
    }

    private void RetireRithmicSession(RithmicTradingSession session)
    {
        var drain = session.StopAsync();
        var work = _rithmicRetirementTasks ??= new();
        work.RemoveAll(static task => task.IsCompletedSuccessfully);
        if (!drain.IsCompletedSuccessfully) work.Add(drain);
    }

    private async Task DrainRithmicSessionsAsync()
    {
        Task[] work;
        lock (_shutdownGate)
        {
            if (_rithmicRetirementTasks == null && _rithmicSessions.Count == 0 && _retiredRithmicSessions.Count == 0) return;
            var draining = _rithmicRetirementTasks ??= new();
            foreach (var session in _rithmicSessions.Values) draining.Add(session.StopAsync());
            foreach (var session in _retiredRithmicSessions) draining.Add(session.StopAsync());
            work = draining.ToArray();
        }
        try { await Task.WhenAll(work).ConfigureAwait(false); }
        finally
        {
            lock (_shutdownGate) _rithmicRetirementTasks?.RemoveAll(static task => task.IsCompleted);
        }
    }

    internal OrderContext RithmicContextFor(ConnectionProfile profile, RithmicTradingSession session, string account)
    {
        var origin = _rithmicOrigins.GetValue(session, _ => new(profile.Id, profile.TradingNamespace, profile.TradingGeneration,
            PublicEndpoint(profile.RithmicUri) + "|" + profile.RithmicSystem));
        return TradingContext(profile, RithmicAccountId(profile, account), "Rithmic", "futures", origin.Environment, null, origin);
    }

    private string? CheckRithmicNativePermission(ConnectionProfile profile, string generation, string contextId,
        OrderRequest request, decimal position)
    {
        if (!_wireAccounts.TryGetValue((contextId, request.Instrument), out var weak) || !weak.TryGetTarget(out var broker))
            return "The prepared account route is no longer available.";
        decimal signed = request.Direction == OrderDirections.Buy ? request.Quantity : -request.Quantity;
        bool increases = RealOrderPermission.IncreasesExposure(position, signed);
        if (request.ReduceOnly && increases)
            return "The requested reduction exceeds the currently reconciled account position.";
        if (increases && !Connections.IsTradingGenerationCurrent(profile, generation))
            return "The connection's trading namespace changed: new exposure is disabled; original-context reductions/cancels remain allowed.";
        return CheckRealOrderForProfile(Connections.Profiles.Contains(profile) ? profile : null, broker, position, signed);
    }

    internal RithmicBroker RithmicBrokerForPosition(ConnectionProfile profile, RithmicTradingSession session,
        string account, string symbol, string exchange)
    {
        var context = RithmicContextFor(profile, session, account);
        string key = "rithmic-position|" + context.Descriptor.Id + "|" + symbol + "|" + exchange;
        if (_realBrokers.TryGetValue(key, out var existing)) return (RithmicBroker)existing;
        string root = System.Text.RegularExpressions.Regex.Replace(symbol, "[FGHJKMNQUVXZ][0-9]{1,2}$", "");
        var instrument = new Instrument { Symbol = symbol, FeedId = RithmicFeed.FeedId, Exchange = exchange,
            TickSize = RithmicFeed.SpecFor(root)?.Tick ?? 0, VolumeStep = 1, PointValue = RithmicFeed.PointValueOfContract(symbol) };
        return (RithmicBroker)Cache(key, profile, new RithmicBroker(instrument, session, account,
            account + " · Rithmic", symbol, exchange, context), rithmicSession: session);
    }

    private Func<CancellationToken, Task<(string Symbol, string Exchange)>> RithmicContractResolver(
        ConnectionProfile profile, RithmicTradingSession session, InstrumentData data, string account)
    {
        _rithmicContractFeeds ??= new();
        if (!_rithmicContractFeeds.TryGetValue(session, out var feed))
        {
            if (_rithmicOrigins.TryGetValue(session, out var origin) &&
                !Connections.IsTradingGenerationCurrent(profile, origin.Generation))
            {
                return _ =>
                {
                    var exact = session.PositionsOf(account).FirstOrDefault(position => position.Symbol == data.Instrument.Symbol &&
                        (data.Instrument.Exchange.Length == 0 || position.Exchange == data.Instrument.Exchange));
                    return exact.Symbol is { Length: > 0 } ? Task.FromResult((exact.Symbol, exact.Exchange)) :
                        Task.FromException<(string, string)>(new InvalidOperationException("The original Rithmic session cannot resolve this chart contract after reconfiguration; use its captured Accounts position."));
                };
            }
            feed = (RithmicFeed)Connections.GetFeed(profile.Id);
            _rithmicContractFeeds.Add(session, feed);
        }
        var captured = feed;
        return ct => captured.ContractAsync(data.Instrument, ct);
    }
}
