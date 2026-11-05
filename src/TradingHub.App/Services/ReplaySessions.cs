using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Replay;
using TradingHub.Trading;

namespace TradingHub.App.Services;

/// <summary>Practice accounts are registered by owned feed/data, never by the current live/simulation account selection.</summary>
public sealed class ReplaySessions : IDisposable
{
    private readonly MarketDataHub _hub;
    private readonly string _historyRoot, _resultRoot;
    private readonly AlertCenter _alerts;
    private readonly List<ReplaySession> _runs = new();
    private bool _disposed;
    public ReplaySessions(MarketDataHub hub, string historyRoot, string resultRoot, AlertCenter alerts)
    { _hub = hub; _historyRoot = historyRoot; _resultRoot = resultRoot; _alerts = alerts; }

    public IReadOnlyList<ReplaySession> All => _runs.ToArray();
    public IReadOnlyList<AccountEntry> Entries => _runs.Select(r => new AccountEntry(r.AccountId, r.Account.Name, AccountKind.Replay, false, !r.Ended)).ToArray();
    public ReplaySession? TryGet(InstrumentData data) => data.Feed is ReplayFeed replay ? _runs.FirstOrDefault(r => r.RunId == replay.RunId && ReferenceEquals(r.Data, data)) : null;
    public ReplaySession? Find(string accountId) => _runs.FirstOrDefault(r => r.AccountId == accountId);

    public ReplaySession Open(ReplaySelection selection, decimal balance, ReplayFees fees, TimeSpan preload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var run = new ReplaySession(selection, balance, fees);
        try
        {
            var feed = run.CreateFeed(_historyRoot);
            var data = _hub.GetOrStart(feed, feed.Instrument, preload);
            run.Bind(data);
            run.Account.NewMyTrade += fill => _alerts.Raise(AlertKind.Execution, run.IsBlind ? "Practice" : selection.Source.Symbol,
                "Replay practice", $"{fill.OrderDirection} {fill.Volume} @ {fill.Price} · fee {fill.Commission}", timeUtc: fill.Time, allowExternal: false);
            _runs.Add(run);
            return run;
        }
        catch { run.Dispose(); throw; }
    }

    public ReplayAccount Resolve(InstrumentData data, string accountId)
    {
        var run = TryGet(data) ?? throw new InvalidOperationException("Replay data is not registered to an owned practice session.");
        if (accountId != run.AccountId)
            throw new InvalidOperationException("Replay cannot select another practice, simulated or real account.");
        return run.Account;
    }

    public AccountSnapshot Snapshot(string accountId)
    {
        var run = Find(accountId) ?? throw new KeyNotFoundException("Replay account not found.");
        var account = run.Account;
        var portfolio = account.Portfolio!;
        var fills = run.IsBlind ? Array.Empty<AccountFill>() : account.Journal.ToArray();
        var analysis = RoundTrips.Analyze(fills);
        var snapshot = new AccountSnapshot { Account = Entries.Single(a => a.Id == accountId), StartBalance = portfolio.BeginBalance,
            Balance = portfolio.BeginBalance + portfolio.RealizedPnL + portfolio.UnrealizedPnL, OpenPnl = portfolio.UnrealizedPnL, Fills = fills, Analysis = analysis,
            RealizedToday = analysis.Realized.Where(r => r.Time.Date == run.ClockUtc.Date).Sum(r => r.Net),
            TradesToday = analysis.Trades.Count(t => t.CloseTime.Date == run.ClockUtc.Date),
            ExecutionRevision = account.ExecutionRevision,
            Status = (run.Ended ? "Ended practice run" : "Isolated practice account") + " · fills are deterministic estimates, not exchange executions" };
        if (run.DataWarning != null) snapshot.Status += " · " + run.DataWarning;
        snapshot.DayPnl = snapshot.RealizedToday + snapshot.OpenPnl;
        var position = account.Position!;
        snapshot.Positions.Add(new(run.AccountId, run.IsBlind ? "Practice" : account.Instrument.Symbol, position.Volume,
            position.AveragePrice, position.UnrealizedPnL, position.RealizedPnL, account, ClosedPnlKnown: !run.IsBlind));
        if (run.IsBlind)
        {
            snapshot.Status += " · dated fills, orders and analysis are concealed until Reveal; use the practice chart for order management";
            snapshot.Balance = null;
            snapshot.FinancialsKnown = false;
            snapshot.OpenPnlKnown = snapshot.RealizedPnlKnown = snapshot.AnalysisKnown = false;
            snapshot.OrdersKnown = false;
            snapshot.PositionsKnown = true; // Owned exposure and average remain visible; only dated activity and totals are concealed.
            snapshot.OpenPnl = snapshot.RealizedToday = snapshot.DayPnl = 0;
            return snapshot;
        }
        foreach (var order in account.Orders)
        {
            var executions = account.MyTrades.Where(t => ReferenceEquals(t.Order, order)).ToArray();
            decimal quantity = executions.Sum(t => t.Volume);
            snapshot.Orders.Add(new(run.AccountId + ":" + order.Id, order.Time, run.IsBlind ? "Practice" : account.Instrument.Symbol,
                order.Direction == OrderDirections.Buy, order.Type.ToString(), order.QuantityToFill,
                order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price, order.Filled,
                quantity > 0 ? executions.Sum(t => t.Price * t.Volume) / quantity : 0, order.Status.ToString(),
                order.State == OrderStates.Active, ((ITradingAccount)account, order)));
        }
        return snapshot;
    }

    public ReplayResult End(ReplaySession run, bool closePosition = true)
    {
        if (!_runs.Contains(run)) throw new InvalidOperationException("Replay run is not registered here.");
        if (run.Ended) return run.Result();
        var result = run.End(closePosition);
        SaveResult(result);
        return result;
    }
    private void SaveResult(ReplayResult result)
    {
        string temporary = Path.Combine(_resultRoot, result.RunId.ToString("N") + ".pending");
        try
        {
            Directory.CreateDirectory(_resultRoot);
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, result); file.Flush(flushToDisk: true); }
            File.Move(temporary, Path.Combine(_resultRoot, result.RunId.ToString("N") + ".json"), overwrite: true);
        }
        catch
        {
            _alerts.Raise(AlertKind.System, "Practice", "Replay results", "The practice result could not be saved. It remains visible in this session.", allowExternal: false);
            try { File.Delete(temporary); } catch { }
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var run in _runs) { End(run); run.Dispose(); }
    }
}
