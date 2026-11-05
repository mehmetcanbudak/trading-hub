using System.Net;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.Tests;

[Collection("LiveStore")]
public sealed class BybitAccountIdentityTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, PointValue = 1 };
    private sealed class Passive : Strategy { protected override void OnBar(Bar bar) { } }

    [Fact]
    public async Task Actual_runner_and_account_history_share_same_UID_key_rotation_but_never_an_unrelated_UID_or_authority()
    {
        string previous = LiveStore.Root;
        string root = Path.Combine(Path.GetTempPath(), "tradinghub-bybit-identity-" + Guid.NewGuid().ToString("N"));
        LiveStore.Root = Path.Combine(root, "live");
        try
        {
            using var data = new InstrumentData(Btc, new SimulatedFeed());
            data.Trades.Add(new Trade(1, FakeBybitProtocol.Now.UtcDateTime.AddMinutes(-1), 101, 1, TradeSide.Buy));
            const string displayName = "Same configured Bybit account";
            string legacyNameKey = $"{typeof(Passive).FullName}|{Btc.FeedId}:{Btc.Symbol}|{displayName}";
            Assert.Null(LiveStore.AppendFill(legacyNameKey, new LiveFill(FakeBybitProtocol.Now.UtcDateTime.AddDays(-1), true, 777, 2)));

            using var originalProtocol = new FakeBybitProtocol { NetPosition = 1, AveragePositionPrice = 101 };
            using var originalHttp = new HttpClient(originalProtocol);
            await using var original = new BybitTradingSession(originalProtocol.Settings, httpClient: originalHttp,
                clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var originalBroker = new BybitBroker(data, original, displayName);
            using var first = new LiveStrategyRunner(new Passive(), data, originalBroker, ChartPeriod.M1) { AccountId = "same-profile", ProfileId = "same-profile" };
            Assert.Null(((IAccountExecutionSource)originalBroker).PersistentAccountIdentity);
            Assert.False(first.TryPrepareIdentity(out var waiting));
            Assert.False(string.IsNullOrWhiteSpace(waiting));
            Assert.Throws<InvalidOperationException>(() => AppServices.BybitJournalId("same-profile", original));
            await original.RefreshAsync();
            Assert.True(first.TryPrepareIdentity(out _));
            string originalKey = first.Key;
            string originalJournal = AppServices.BybitJournalId("same-profile", original);
            Assert.NotEqual(legacyNameKey, originalKey);
            Assert.Null(LiveStore.AppendFill(originalKey, new LiveFill(FakeBybitProtocol.Now.UtcDateTime.AddDays(-1), true, 1, 101,
                Btc.Symbol, Btc.PointValue, "bybit:demo-1234567:BTCUSDT:old-order:old-fill", 0, "USDT", 0)));
            first.Start();
            Assert.Equal(1m, first.Ledger.Position);
            first.Stop(flatten: false, keepProtection: true);

            using var rotatedProtocol = new FakeBybitProtocol { NetPosition = 1, AveragePositionPrice = 101 };
            using var rotatedHttp = new HttpClient(rotatedProtocol);
            await using var rotated = new BybitTradingSession(rotatedProtocol.Settings with { ApiKey = "rotated-key", ApiSecret = "rotated-secret" },
                httpClient: rotatedHttp, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var rotatedBroker = new BybitBroker(data, rotated, displayName);
            using var sameAccount = new LiveStrategyRunner(new Passive(), data, rotatedBroker, ChartPeriod.M1) { AccountId = "same-profile", ProfileId = "same-profile" };
            Assert.False(sameAccount.TryPrepareIdentity(out _));
            await rotated.RefreshAsync();
            Assert.NotEqual(original.ContextIdentity, rotated.ContextIdentity);
            Assert.Equal(((IAccountExecutionSource)originalBroker).PersistentAccountIdentity, ((IAccountExecutionSource)rotatedBroker).PersistentAccountIdentity);
            Assert.True(sameAccount.TryPrepareIdentity(out _));
            Assert.Equal(originalKey, sameAccount.Key);
            Assert.Equal(originalJournal, AppServices.BybitJournalId("same-profile", rotated));
            Assert.Equal(displayName, rotatedBroker.Name);
            sameAccount.Start();
            Assert.Equal(1m, sameAccount.Ledger.Position);
            sameAccount.Stop(flatten: false, keepProtection: true);

            using var unrelatedProtocol = new FakeBybitProtocol { NetPosition = 1, AveragePositionPrice = 202 };
            unrelatedProtocol.ResponseFor = request => request.Path == "/v5/user/query-api" ?
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"retCode\":0,\"result\":{\"userID\":9876543}}") } : null;
            using var unrelatedHttp = new HttpClient(unrelatedProtocol);
            await using var unrelated = new BybitTradingSession(unrelatedProtocol.Settings with { ApiKey = "other-user-key", ApiSecret = "other-user-secret" },
                httpClient: unrelatedHttp, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var unrelatedBroker = new BybitBroker(data, unrelated, displayName);
            using var otherAccount = new LiveStrategyRunner(new Passive(), data, unrelatedBroker, ChartPeriod.M1) { AccountId = "same-profile", ProfileId = "same-profile" };
            await unrelated.RefreshAsync();
            Assert.True(otherAccount.TryPrepareIdentity(out _));
            Assert.NotEqual(originalKey, otherAccount.Key);
            Assert.NotEqual(originalJournal, AppServices.BybitJournalId("same-profile", unrelated));
            Assert.Equal(displayName, unrelatedBroker.Name);
            otherAccount.Start();
            Assert.Equal(0m, otherAccount.Ledger.Position); // Same display name and net account size do not transfer ownership.
            otherAccount.Stop(flatten: false, keepProtection: true);

            using var authorityProtocol = new FakeBybitProtocol { NetPosition = 1, AveragePositionPrice = 303 };
            using var authorityHttp = new HttpClient(authorityProtocol);
            await using var authority = new BybitTradingSession(authorityProtocol.Settings with { BaseUrl = "http://127.0.0.1:2" },
                httpClient: authorityHttp, clock: new BybitProviderTests.FixedClock(FakeBybitProtocol.Now));
            using var authorityBroker = new BybitBroker(data, authority, displayName);
            using var otherAuthority = new LiveStrategyRunner(new Passive(), data, authorityBroker, ChartPeriod.M1) { AccountId = "same-profile", ProfileId = "same-profile" };
            await authority.RefreshAsync();
            Assert.Equal(original.AccountIdentity, authority.AccountIdentity);
            Assert.True(otherAuthority.TryPrepareIdentity(out _));
            Assert.NotEqual(originalKey, otherAuthority.Key);
            Assert.NotEqual(originalJournal, AppServices.BybitJournalId("same-profile", authority));
            otherAuthority.Start();
            Assert.Equal(0m, otherAuthority.Ledger.Position);
            otherAuthority.Stop(flatten: false, keepProtection: true);
            Assert.DoesNotContain(originalProtocol.Requests, request => request.Path == "/v5/order/create");
            Assert.DoesNotContain(rotatedProtocol.Requests, request => request.Path == "/v5/order/create");
            Assert.DoesNotContain(unrelatedProtocol.Requests, request => request.Path == "/v5/order/create");
            Assert.DoesNotContain(authorityProtocol.Requests, request => request.Path == "/v5/order/create");
        }
        finally
        {
            LiveStore.Root = previous;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
