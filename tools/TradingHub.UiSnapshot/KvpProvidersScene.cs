using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Localization;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.Longbridge;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;

internal static class KvpProvidersScene
{
    public static int Run(MainWindow owner, Action<double> pump, string outDir) => new Scene(owner, pump, Path.GetFullPath(outDir)).Run();

    private sealed class Scene(MainWindow owner, Action<double> pump, string outDir)
    {
        private readonly List<object> _assertions = new();
        private readonly List<object> _observations = new();
        private readonly List<string> _captures = new();
        private readonly List<InstrumentData> _accountData = new();
        private readonly List<string> _secrets = new() { KvpProvidersOAuth.Access, KvpProvidersOAuth.Refresh, KvpProvidersOAuth.Renewed };
        private int _checks, _failures;
        private AppServices Services => App.Services;
        private ConnectionManager Connections => Services.Connections;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public int Run()
        {
            Directory.CreateDirectory(outDir);
            using var exchange = new KvpProvidersExchange();
            using var oauth = new KvpProvidersOAuth();
            using var endpoints = new Endpoints(new Dictionary<string, string>
            {
                ["TRADINGHUB_BYBIT_REST"] = exchange.Server.Http,
                ["TRADINGHUB_BYBIT_MARKET_REST"] = exchange.Server.Http,
                ["TRADINGHUB_BYBIT_MARKET_WS"] = exchange.Server.Ws + "/v5/public/linear",
            });
            try
            {
                Section("Longbridge data-only OAuth and market consumers", () => Longbridge(oauth));
                Section("Aggregate constituent/time coverage and non-executable routing", Aggregate);
                Section("Bybit actual account factory, snapshots, executions and ownership", () => Bybit(exchange));
                Check(exchange.UnexpectedRoutes.IsEmpty, "all actual Bybit HTTP calls reached explicitly implemented fake endpoints", new { routes = exchange.UnexpectedRoutes.ToArray() });
                Check(exchange.InvalidSignatures == 0 && exchange.SignedRequests > 0 && exchange.AdjustedRequests == exchange.SignedRequests,
                    "actual account requests verify HMAC and adjust to a fake server clock 90 seconds ahead", new { exchange.SignedRequests, exchange.InvalidSignatures, exchange.AdjustedRequests });
                CheckOrdinaryStores();
            }
            finally
            {
                foreach (var data in _accountData) data.Dispose();
                // App.Services and its provider contexts remain harness-owned. Root shutdown is a separate acceptance scene.
                string result = JsonSerializer.Serialize(new
                {
                    scene = "kvp-providers", checks = _checks, failures = _failures,
                    evidenceBoundary = "Actual Avalonia controls, AppServices, provider clients/sessions, account journal and strategy ownership against loopback HTTP and injected quote/constituent transports only. No real broker, OAuth browser, provider entitlement or production certification.",
                    assertions = _assertions, observations = _observations, captures = _captures,
                }, Json);
                if (_secrets.Any(value => value.Length > 0 && result.Contains(value, StringComparison.Ordinal)))
                {
                    Check(false, "provider evidence contains no credential material");
                    result = JsonSerializer.Serialize(new { scene = "kvp-providers", checks = _checks, failures = _failures, error = "Evidence publication refused because credential material was detected." }, Json);
                }
                File.WriteAllText(Path.Combine(outDir, "kvp-providers-results.json"), result);
            }
            return _failures;
        }

        private void Check(bool condition, string description, object? evidence = null)
        {
            _checks++; if (!condition) _failures++;
            _assertions.Add(new { number = _checks, passed = condition, description, evidence });
            Console.WriteLine($"{(condition ? "OK" : "FAIL")} KVP providers: {description}");
        }
        private void Section(string description, Action action)
        {
            try { action(); }
            catch (Exception error) { Check(false, description + " completes without an unexpected exception", new { exceptionType = error.GetBaseException().GetType().Name, stack = error.GetBaseException().StackTrace }); }
        }
        private void Until(Func<bool> condition, string description)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            while (!condition() && DateTime.UtcNow < deadline) pump(0.02);
            bool met = condition(); Check(met, description);
            if (!met) throw new TimeoutException(description);
        }
        private void Await(Task task, string description)
        { Until(() => task.IsCompleted, description); task.GetAwaiter().GetResult(); }
        private T Await<T>(Task<T> task, string description)
        { Await((Task)task, description); return task.GetAwaiter().GetResult(); }
        private void Rejects<T>(Task task, string description) where T : Exception
        {
            Until(() => task.IsCompleted, description + " settles");
            try { task.GetAwaiter().GetResult(); Check(false, description); }
            catch (T) { Check(true, description); }
            catch (Exception error) { Check(false, description, new { exceptionType = error.GetType().Name }); }
        }
        private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        private static string Label(Button button) => button.Content is string text ? text : button.Content is Control control
            ? string.Join(" ", control.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)) : "";
        private static bool TextIs(string? actual, string expected) => actual == expected || actual == Lang.T(expected);
        private static Button Button(Control root, string text) => root.GetVisualDescendants().OfType<Button>().Single(button => TextIs(Label(button), text));
        private static T Input<T>(Control root, string label) where T : Control => root.GetVisualDescendants().OfType<StackPanel>()
            .Single(panel => panel.Children.OfType<TextBlock>().Any(text => TextIs(text.Text, label)) && panel.Children.OfType<T>().Any()).Children.OfType<T>().Single();
        private void Type(TextBox box, string value)
        {
            box.BringIntoView(); box.Focus(); box.SelectAll();
            box.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Source = box, Text = value });
            pump(0.02);
        }
        private Control Page(string name)
        {
            owner.Navigate(name); pump(0.08);
            return Field<Dictionary<string, Control>>(owner, "_pages")[name];
        }
        private Control SelectConnection(ConnectionProfile profile)
        {
            var page = Page("connections");
            var list = Field<ListBox>(page, "_list");
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => ReferenceEquals(item.Tag, profile));
            pump(0.05); return page;
        }
        private AccountsView SelectAccount(AccountEntry entry)
        {
            var page = (AccountsView)Page("accounts");
            Until(() => Field<IReadOnlyList<AccountEntry>>(page, "_accounts").Any(account =>
                account.Id == entry.Id && account.Name == entry.Name && account.BybitContextIdentity == entry.BybitContextIdentity),
                "actual account page publishes the selected credential context before its menu opens");
            var selector = Field<Button>(page, "_accountButton");
            var menu = (Flyout)selector.Flyout!;
            menu.ShowAt(selector); pump(0.05);
            var body = (Control)menu.Content!;
            Click(body.GetVisualDescendants().OfType<Button>().Single(button => TextIs(Label(button), entry.Name)));
            pump(1.05);
            Check(Field<string>(page, "_selected") == entry.Id, "actual account selector chooses " + entry.Name);
            return page;
        }
        private void Tab(AccountsView page, int index)
        { Field<TabControl>(page, "_tabs").SelectedIndex = index; pump(0.08); }
        private void RefreshUi() => pump(1.05);
        private void Shot(string name) => Shot(owner, name);
        private void Shot(Window target, string name)
        {
            pump(0.12);
            using var image = target.CaptureRenderedFrame();
            Check(image != null && image.PixelSize.Width > 0 && image.PixelSize.Height > 0, name + " captures an actual rendered window");
            if (image == null) return;
            string filename = name + ".png"; image.Save(Path.Combine(outDir, filename)); _captures.Add(filename);
        }
        private static string[] Texts(Control root) => root.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text ?? "").ToArray();
        private static string Card(AccountsView page, int index) => Field<List<(TextBlock Title, TextBlock Value, TextBlock Sub)>>(page, "_cards")[index].Value.Text ?? "";
        private void PrepareIdentity(LiveStrategyRunner runner, string description)
        {
            bool ready = runner.TryPrepareIdentity(out _);
            Check(ready, description);
            if (!ready) throw new InvalidOperationException("The fixture cannot access persistent runner state before verified account identity is available.");
        }
        private AccountEntry Account(ConnectionProfile profile, BybitTradingSession? context = null) => Services.AccountCenter.Accounts().Single(entry =>
            entry.Kind == AccountKind.Bybit && entry.Profile?.Id == profile.Id && (context == null ? entry.Id == profile.Id : entry.BybitContextIdentity == context.ContextIdentity));
        private void Snapshot(string name, AccountSnapshot snapshot) => _observations.Add(new
        {
            name, account = snapshot.Account.Name, snapshot.Account.IsReal, snapshot.Account.CanTrade,
            snapshot.Connected, snapshot.FinancialsKnown, snapshot.CostsComplete, snapshot.OpenPnlKnown,
            snapshot.RealizedPnlKnown, snapshot.AnalysisKnown, snapshot.PositionsKnown, snapshot.OrdersKnown,
            snapshot.Balance, snapshot.Available, snapshot.OpenPnl, snapshot.RealizedToday, snapshot.Status, snapshot.ExecutionRevision,
            positions = snapshot.Positions.Select(p => new { p.Symbol, p.Net, p.AvgPrice, p.OpenPnl, p.ClosedPnlKnown }),
            orders = snapshot.Orders.Select(o => new { o.Key, o.Symbol, o.Type, o.Quantity, o.Price, o.Filled, o.AvgFill, o.Status, o.Active }),
            fills = snapshot.Fills.Select(f => new { f.Id, f.Symbol, f.Quantity, f.Price, f.Commission, f.Buy, f.Time }),
        });
        private static string? FillCommissionText(AccountsView view, string executionSuffix)
        {
            var row = Field<ListBox>(view, "_fills").GetVisualDescendants().OfType<ListBoxItem>().SingleOrDefault(item =>
                (item.Content as AccountFill ?? item.DataContext as AccountFill)?.Id.EndsWith(executionSuffix, StringComparison.Ordinal) == true);
            return row?.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(text => Grid.GetColumn(text) == 5)?.Text;
        }

        private void Longbridge(KvpProvidersOAuth oauth)
        {
            var profile = new ConnectionProfile
            {
                Id = "kvp-ui-longbridge", Kind = ConnectionKind.Longbridge, Name = "Longbridge — FAKE data only",
                Longbridge = new LongbridgeSettings { BaseUrl = oauth.Server.Http, QuoteWsUrl = oauth.Server.Ws + "/v2" },
            };
            Connections.Profiles.Add(profile); Connections.Save();
            var page = SelectConnection(profile);
            Type(Input<TextBox>(page, "Public OAuth client ID"), "kvp-ui-public-client");
            Type(Input<TextBox>(page, "Symbols"), "AAPL.US, AAPL.US");
            Type(Input<TextBox>(page, "Exact market metadata (JSON)"), JsonSerializer.Serialize(new Dictionary<string, LongbridgeInstrumentMetadata>
            {
                ["AAPL.US"] = new() { TickSize = .01m, VolumeStep = 100, Exchange = "NASDAQ", Currency = "USD", TimeZoneId = "America/New_York", RegularSessions = new() { new(new(9, 30), new(16, 0)) } },
            }));
            Click(Button(page, "Save")); pump(0.05);
            Check(profile.Longbridge.ClientId == "kvp-ui-public-client" && profile.Longbridge.Symbols.SequenceEqual(new[] { "AAPL.US" }) && profile.Longbridge.Instruments["AAPL.US"].TickSize == .01m,
                "actual Longbridge settings save explicit public client, deduplicated symbol and exact tick/session metadata");
            Check(!profile.SupportsTrading && Texts(page).Any(text => text.Contains("Data only", StringComparison.OrdinalIgnoreCase)), "Longbridge connection panel explicitly limits authorization to market data");
            Shot("kvp_providers_longbridge_settings");
            var session = Connections.LongbridgeSessionFor(profile);
            var redirect = new Uri("http://127.0.0.1:60355/callback");
            Await(session.BeginAuthorizationAsync(redirect, default), "actual Longbridge session prepares authorization without a browser");
            Rejects<InvalidOperationException>(session.CompleteAuthorizationAsync(new Uri(redirect + "?state=wrong&code=fixture-code"), default), "wrong OAuth state cannot exchange or persist credentials");
            Check(oauth.TokenRequests == 0 && Connections.GetSecret(profile, "longbridge-oauth").Length == 0, "wrong state sends no fake token request and publishes no secret");
            using (var http = new HttpClient())
            {
                var clock = new KvpProvidersClock(DateTimeOffset.UtcNow);
                var expiring = new LongbridgeSession(profile.Longbridge, http, () => Connections.GetSecret(profile, "longbridge-oauth"), value => Connections.SetSecret(profile, "longbridge-oauth", value), clock);
                var attempt = Await(expiring.BeginAuthorizationAsync(redirect, default), "expiry fixture uses the actual OAuth session clock seam");
                clock.Now = clock.Now.AddMinutes(6);
                string state = KvpProvidersOAuth.Parse(attempt.AuthorizationUri.Query)["state"];
                Rejects<InvalidOperationException>(expiring.CompleteAuthorizationAsync(new Uri(redirect + "?state=" + Uri.EscapeDataString(state) + "&code=fixture-code"), default), "expired OAuth state cannot publish credentials");
                Check(oauth.TokenRequests == 0, "expired authorization never reaches even the fake token endpoint"); expiring.Retire();
            }
            using var callbackHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
            Task callbackOperation = Task.CompletedTask;
            async Task ReadCallback(Uri callback, CancellationToken ct)
            {
                using var response = await callbackHttp.GetAsync(callback, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
            }
            Task Browser(Uri authorization, CancellationToken ct)
            {
                oauth.ObserveAuthorization(authorization);
                var query = KvpProvidersOAuth.Parse(authorization.Query);
                var callback = new Uri(query["redirect_uri"] + "?state=" + Uri.EscapeDataString(query["state"]) + "&code=fixture-code");
                if (!callback.IsLoopback || callback.Scheme != "http") throw new InvalidOperationException("Only the owned fake callback is allowed");
                callbackOperation = ReadCallback(callback, ct);
                return Task.CompletedTask;
            }
            Await(LongbridgeAuthorizationFlow.AuthorizeAsync(Connections, profile, Browser), "actual app authorization flow completes through its owned loopback listener and fake browser callback");
            Await(callbackOperation, "fake browser observes the owned listener response after the launch returns");
            Check(oauth.PkceVerified && oauth.TokenRequests == 1 && Connections.GetSecret(profile, "longbridge-oauth").Contains(KvpProvidersOAuth.Refresh, StringComparison.Ordinal),
                "actual S256 verifier matches the authorization challenge and the opaque token persists only through the secret store");
            session = Connections.LongbridgeSessionFor(profile);
            string renewed = Await(session.AccessTokenAsync(default), "short-lived token refreshes through the actual configured session");
            Check(renewed == KvpProvidersOAuth.Renewed && oauth.RefreshRequests == 1 && Connections.GetSecret(profile, "longbridge-oauth").Contains(KvpProvidersOAuth.Refresh, StringComparison.Ordinal),
                "refresh replaces access token and preserves an omitted refresh token");
            string accepted = Connections.GetSecret(profile, "longbridge-oauth");
            oauth.HoldNextToken();
            using (var cancel = new CancellationTokenSource())
            {
                var operation = LongbridgeAuthorizationFlow.AuthorizeAsync(Connections, profile, Browser, cancel.Token);
                Until(() => oauth.TokenEntered.Task.IsCompleted, "fake token response is held inside the actual authorization flow");
                cancel.Cancel(); oauth.TokenRelease.TrySetResult();
                Rejects<OperationCanceledException>(operation, "canceling an active OAuth exchange discards its late response");
                Until(() => callbackOperation.IsCompleted, "canceled authorization drains its fake browser callback");
                if (!callbackOperation.IsCanceled) callbackOperation.GetAwaiter().GetResult();
                Check(Connections.GetSecret(profile, "longbridge-oauth") == accepted, "canceled reauthorization preserves the previously accepted private credentials");
            }
            DateTime start = DateTime.UtcNow.AddMinutes(-10); start = new DateTime(start.Ticks - start.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            var transport = new KvpProvidersLongbridgeTransport(start);
            var feed = new LongbridgeFeed(profile.Longbridge, session, transport, feedId: profile.MarketDataId);
            // Registration is a provider transport seam; all data consumers below remain the normal application ones.
            Connections.Profiles.Remove(profile); KvpSceneFeed.Register(Services, profile, feed);
            var instrument = Await(Connections.GetFeed(profile.Id).GetInstrumentsAsync(default), "actual configured Longbridge feed parses fake provider static metadata").Single();
            Check(instrument.Description == "Apple Inc." && instrument.Exchange == "NASDAQ" && instrument.QuoteAsset == "USD" && instrument.TickSize == .01m && instrument.VolumeStep == 100,
                "provider English name, currency, exchange, configured tick and observed board lot reach the real instrument");
            Check(feed.GetSessionSettings(instrument).SessionStartUtc(new(2026, 11, 4)) == new DateTime(2026, 11, 4, 14, 30, 0, DateTimeKind.Utc) &&
                feed.GetSessionSettings(instrument).SessionStartUtc(new(2026, 7, 1)) == new DateTime(2026, 7, 1, 13, 30, 0, DateTimeKind.Utc) && !feed.GetSessions(instrument, new(2026, 11, 4)).CalendarConfirmed,
                "actual market session honors DST without claiming a confirmed holiday calendar");
            var quote = Await(feed.GetQuoteAsync(instrument, default), "actual Longbridge quote parser consumes the fake wire report");
            Check(quote.LastPrice == 201.25m && quote.Volume == 1234 && quote.Turnover == null, "known quote price/volume do not turn absent turnover into zero");
            transport.UnknownQuote = true; var unknown = Await(feed.GetQuoteAsync(instrument, default), "actual quote consumer accepts absent financial measurements as unknown");
            Check(unknown.LastPrice == null && unknown.Timestamp == null && unknown.Volume == null && unknown.Turnover == null, "missing quote measurements stay nullable"); transport.UnknownQuote = false;
            var candles = new List<Kline>();
            Await(feed.LoadKlinesAsync(instrument, start, start.AddMinutes(3), 1, 0, batch => { candles.AddRange(batch); return true; }, null, default), "actual Longbridge history parses zero-volume and measured-volume candles");
            Check(candles.Count == 2 && candles[0].Volume == 0 && candles[1].Volume == 10 && !candles[1].HasBuyVolume && candles.All(bar => bar.Provenance.Quality == (DataQuality.Synthetic | DataQuality.Partial)),
                "zero remains zero and candle history is partial synthetic OHLC, not observed footprint");
            var tape = Await(feed.GetRecentTradesAsync(instrument, default), "actual recent-tape parser consumes the fake provider report");
            Check(tape.Trades.Select(t => t.Price).SequenceEqual(new[] { 200.5m, 201m }) && tape.Trades.Select(t => t.Side).SequenceEqual(new[] { TradeSide.None, TradeSide.Buy }) &&
                tape.Trades.All(t => t.Provenance.Quality == (DataQuality.Raw | DataQuality.Partial | DataQuality.InferredSide)) && !tape.Coverage.Complete && !tape.HasProviderTradeIds,
                "recent tape sorts exchange times while preserving partial/inferred-side coverage and unavailable stable provider IDs");
            Rejects<NotSupportedException>(feed.LoadTradesAsync(instrument, start.AddDays(-1), start, 5, _ => true, null, default), "old history cannot masquerade as ID-resumable observed tape");
            using (var data = new InstrumentData(instrument, feed) { RecordLive = false })
            {
                var depth = new List<DepthUpdate>(); data.DepthUpdated += depth.Add;
                data.Start(start); Until(() => { data.Pump(); return !data.IsLoadingHistory && depth.Any(update => update.IsFullRefresh); }, "real InstrumentData receives history and the initial queried provider depth baseline");
                var initial = depth.First(update => update.IsFullRefresh);
                Check(initial.ProviderId == profile.MarketDataId && initial.Venue == "NASDAQ" && initial.Sequence == null && initial.Coverage.HasFlag(DepthCoverage.SnapshotOnly) && initial.Coverage.HasFlag(DepthCoverage.PartialBook),
                    "actual initial depth is labelled partial provider snapshot with venue and no invented sequence");
                int before = data.Trades.Count; transport.QuotePush(); data.Pump();
                Check(data.Trades.Count == before, "a live quote with cumulative volume manufactures no trade through real InstrumentData");
                transport.DepthPush(100, (1, "202.10", 25), (2, "202.50", 30)); data.Pump();
                transport.DepthPush(101, (1, "202.20", 5)); data.Pump();
                Check(data.Depth.Asks.Select(level => level.PriceTicks).SequenceEqual(new[] { 20220L, 20250L }) && depth.Last().Changes.Any(change => change.PriceTicks == 20210 && change.Size == 0),
                    "real depth consumer applies changed provider positions and explicit derived removals without deleting unchanged levels");
                transport.DepthPush(102, (2, "", 0)); data.Pump();
                Check(data.Depth.Asks.Length == 1, "real depth consumer removes the provider's explicit zero-size position");
                transport.Disconnect(); data.Pump(); int commands = transport.Commands.Count;
                transport.Reconnect(); Until(() => { data.Pump(); return transport.Commands.Count > commands && depth.Count(update => update.IsFullRefresh) >= 2; }, "reconnect obtains a fresh full depth baseline before resuming changes");
                var sequence = transport.Commands.ToArray(); int subscribe = Array.IndexOf(sequence, LongbridgeCommand.Subscribe);
                Check(subscribe > 0 && sequence[subscribe - 1] == LongbridgeCommand.Depth && depth.Last(update => update.IsFullRefresh).GapBefore == DepthGapReason.Disconnected,
                    "actual subscription orders QueryDepth before Subscribe and retains the reconnect gap rather than pretending continuity");
                Check(!Services.AccountsFor(data).Any(option => option.Id != AccountCenter.SimId), "Longbridge data never advertises a broker account");
                try { Services.AccountFor(data, profile.Id); Check(false, "Longbridge account factory rejects order routing"); }
                catch (NotSupportedException) { Check(true, "Longbridge account factory rejects order routing"); }
            }
            page = SelectConnection(profile);
            var tested = Await(Connections.TestAsync(profile, default), "actual Longbridge connection consumer tests the registered fake quote transport");
            Check(tested.Ok && tested.Message.Contains("configured Longbridge instruments", StringComparison.Ordinal), "actual connection test reports data-only metadata availability", new { tested.Ok, tested.Message });
            Shot("kvp_providers_longbridge_authorized");
            _observations.Add(new { name = "Longbridge", quote.LastPrice, quote.Volume, quote.Turnover, candles = candles.Select(c => new { c.OpenTimeMs, c.Volume, c.HasBuyVolume, quality = c.Provenance.Quality.ToString() }), coverage = tape.Coverage.Message, commands = transport.Commands.Select(command => command.ToString()).ToArray() });
            Click(Button(page, "Forget authorization"));
            Check(Connections.GetSecret(profile, "longbridge-oauth").Length == 0 && Texts(page).Any(text => text.Contains("No trading account is created", StringComparison.Ordinal)), "actual Forget authorization removes the private token without creating an executable account");
            CheckOrdinaryStores();
        }

        private void Aggregate()
        {
            DateTime start = DateTime.UtcNow.AddMinutes(-5); start = new DateTime(start.Ticks - start.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            var firstInstrument = new Instrument { Symbol = "BTCUSDT", FeedId = BinanceFeed.FuturesId, TickSize = .1m, VolumeStep = .001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
            var secondInstrument = new Instrument { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .25m, VolumeStep = .001m, BaseAsset = "BTC", QuoteAsset = "USDT" };
            var firstRaw = new KvpCandleSceneFeed(firstInstrument.FeedId, firstInstrument) { Now = start.AddMinutes(3) };
            var secondRaw = new KvpCandleSceneFeed(secondInstrument.FeedId, secondInstrument) { Now = start.AddMinutes(3) };
            firstRaw.Trades.AddRange([new(1, start.AddSeconds(1), 100.1m, 2, TradeSide.Buy), new(2, start.AddSeconds(3), 100.2m, 3, TradeSide.Sell)]);
            secondRaw.Trades.Add(new(1, start.AddSeconds(2), 100.25m, 4, TradeSide.Buy));
            long ms = new DateTimeOffset(start).ToUnixTimeMilliseconds();
            firstRaw.Candles.AddRange([new(ms, 100, 110, 90, 105, 10, 6, true), new(ms + 60_000, 105, 115, 100, 110, 8, 5, true)]);
            secondRaw.Candles.Add(new(ms, 101, 112, 95, 106, 20, 0, false));
            var first = new KvpProvidersVenue(firstRaw); var second = new KvpProvidersVenue(secondRaw, recentOnly: true) { FailTrades = true };
            var feed = new AggregateFeed([first, second], ownsSources: true, timeProvider: new KvpProvidersClock(new DateTimeOffset(start.AddMinutes(3))));
            var profile = new ConnectionProfile { Id = "kvp-ui-aggregate", Kind = ConnectionKind.AggregateCrypto, Name = "Aggregate — FAKE constituents", HistoryHours = .5 };
            KvpSceneFeed.Register(Services, profile, feed); Connections.Save();
            var instrument = Await(Connections.GetFeed(profile.Id).GetInstrumentsAsync(default), "actual aggregate catalog combines the two injected venue instruments").Single();
            var trades = new List<Trade>();
            Await(feed.LoadTradesAsync(instrument, start, start.AddMinutes(2), null, batch => { trades.AddRange(batch); return true; }, null, default), "aggregate history retains successful venue trades while the other constituent fails");
            var coverage = feed.GetCoverage(instrument, AggregateOperation.Trades);
            Check(trades.Count == 2 && trades.All(t => t.Provenance.SourceIdentity == BinanceFeed.FuturesId && t.Provenance.IsPartial) && coverage.IsPartial &&
                coverage.RequestedFromUtc == start && coverage.RequestedToUtc == start.AddMinutes(2) && coverage.Constituents.Any(source => source.FeedId == BybitFeed.LinearId && source.Status == FeedStatus.Error),
                "actual aggregate reports failed constituent, exact requested time range and partial available prints");
            second.FailTrades = false; trades.Clear();
            Await(feed.LoadTradesAsync(instrument, start, start.AddMinutes(2), null, batch => { trades.AddRange(batch); return true; }, null, default), "aggregate reload recovers the second venue without claiming older full tape coverage");
            Check(trades.Select(t => t.Price).SequenceEqual(new[] { 100.1m, 100.25m, 100.2m }) && trades.Select(t => t.Provenance.SourceIdentity).SequenceEqual(new[] { BinanceFeed.FuturesId, BybitFeed.LinearId, BinanceFeed.FuturesId }) &&
                feed.GetCoverage(instrument, AggregateOperation.Trades).Constituents.Any(source => source.FeedId == BybitFeed.LinearId && source.Status == FeedStatus.Connected && source.IsPartial),
                "recovery preserves chronological venue identity and labels the constituent's recent-only time coverage");
            var candles = new List<Kline>();
            Await(feed.LoadKlinesAsync(instrument, start, start.AddMinutes(2), 1, 0, batch => { candles.AddRange(batch); return true; }, null, default), "actual aggregate merges candle rows with missing venue/time cells");
            Check(candles.Count == 2 && candles[0].Volume == 30 && candles[0].Open == 100 && candles[0].Close == 105 && candles[0].High == 112 && candles[0].Low == 90 && !candles[0].HasBuyVolume && candles[0].BuyVolume == 6 &&
                candles[1].Volume == 8 && candles[1].Provenance.IsPartial && feed.GetCoverage(instrument, AggregateOperation.Candles).IsPartial,
                "independent candle example sums volumes 10+20, preserves first-venue open/close, and never turns unknown buy side or a missing second bar into measured coverage");
            using (var data = new InstrumentData(instrument, feed) { RecordLive = false })
            {
                var depths = new List<DepthUpdate>(); data.DepthUpdated += depths.Add;
                data.Start(start); Until(() => { data.Pump(); return !data.IsLoadingHistory; }, "actual aggregate InstrumentData loads available history");
                firstRaw.PushDepth(firstInstrument, new DepthUpdate { Snapshot = new DepthSnapshot([new(1000, 7)], [new(1001, 8)], feed.Now), Sequence = 42, IsFullRefresh = true, Coverage = DepthCoverage.ReceivedChanges, ReceivedAt = feed.Now });
                secondRaw.PushDepth(secondInstrument, new DepthUpdate { Snapshot = new DepthSnapshot([new(400, 999)], [new(401, 999)], feed.Now), ReceivedAt = feed.Now }); data.Pump();
                var receivedBook = depths.LastOrDefault(update => update.Sequence == 42 && update.IsFullRefresh);
                Check(data.Depth.HasBid && instrument.FromTicks(data.Depth.BestBidTicks) == 100m && data.Depth.BestBidSize == 7 &&
                    receivedBook?.ProviderId == BinanceFeed.FuturesId && receivedBook.Venue == BinanceFeed.FuturesId && feed.BookFeedId == BinanceFeed.FuturesId,
                    "actual composite DOM preserves first-venue price100 and size7, ignores second-venue size999 and identifies its designated venue");
                first.ReportStatus(FeedStatus.Reconnecting, "First fake venue unavailable");
                secondRaw.PushDepth(secondInstrument, new DepthUpdate { Snapshot = new DepthSnapshot([new(400, 999)], [], feed.Now), ReceivedAt = feed.Now }); data.Pump();
                Check(depths.All(update => update.Snapshot?.BestBidSize != 999) && data.Depth.BestBidSize != 999 &&
                    feed.GetCoverage(instrument, AggregateOperation.Live).BookFeedId == BinanceFeed.FuturesId && !feed.IsExecutable && !feed.IsBookConsolidated,
                    "first-venue outage never imports the second book, changes designated venue or offers an executable composite price");
                Check(!Services.AccountsFor(data).Any(), "actual aggregate account selector offers no account, including simulation");
                try { Services.AccountFor(data, AccountCenter.SimId); Check(false, "actual aggregate account factory rejects execution even with a requested simulated account"); }
                catch (InvalidOperationException) { Check(true, "actual aggregate account factory rejects execution even with a requested simulated account"); }
            }
            var page = SelectConnection(profile);
            Check(Texts(page).Any(text => text.Contains("Missing constituents remain marked as partial", StringComparison.Ordinal)), "actual aggregate connection panel warns about partial constituents, non-executable prices and Binance-only DOM");
            var tested = Await(Connections.TestAsync(profile, default), "actual aggregate connection consumer tests only registered fake constituents");
            Check(tested.Ok && tested.Message.Contains("Non-executable", StringComparison.Ordinal), "actual aggregate test never offers executable composite prices", new { tested.Ok, tested.Message });
            Shot("kvp_providers_aggregate_partial");
            _observations.Add(new { name = "Aggregate", bookFeed = feed.BookFeedId, executable = feed.IsExecutable, requestedFrom = coverage.RequestedFromUtc, requestedTo = coverage.RequestedToUtc, constituents = coverage.Constituents, candles = candles.Select(c => new { c.OpenTimeMs, c.Volume, c.BuyVolume, c.HasBuyVolume, quality = c.Provenance.Quality.ToString(), c.Provenance.SourceIdentity }) });
        }

        private void Bybit(KvpProvidersExchange exchange)
        {
            var demoWire = exchange.Add("kvp-private-demo-key", "kvp-private-demo-secret", 900001, .75m);
            demoWire.ReportPriorClose(1m, 112.55m, .05m, 12.50m);
            var testWire = exchange.Add("kvp-private-testnet-key", "kvp-private-testnet-secret", 900002);
            var realWire = exchange.Add("kvp-private-real-key", "kvp-private-real-secret", 900003, 1m);
            var replacementWire = exchange.Add("kvp-private-replacement-key", "kvp-private-replacement-secret", 900004);
            foreach (var wire in exchange.Contexts.Values) { _secrets.Add(wire.Key); _secrets.Add(wire.Secret); }
            testWire.UtaStatus = 1;
            var demo = ConfigureBybit(BybitEnvironment.Demo, demoWire);
            var testnet = ConfigureBybit(BybitEnvironment.Testnet, testWire, expectedReady: false);
            var real = ConfigureBybit(BybitEnvironment.Real, realWire);
            CheckOrdinaryStores();
            var demoSession = Services.BybitSessionFor(demo); var testSession = Services.BybitSessionFor(testnet); var realSession = Services.BybitSessionFor(real);
            Await(demoSession.RefreshAsync(), "configured demo UTA becomes ready through actual account factory");
            Rejects<NotSupportedException>(testSession.RefreshAsync(), "actual testnet factory refuses a non-UTA account instead of declaring readiness");
            Check(!testSession.PositionReady && !testSession.OrdersReady, "non-UTA actual session exposes unknown/unready account state");
            testWire.UtaStatus = 5;
            Await(testSession.RefreshAsync(), "configured testnet recovers only after an actual supported UTA report");
            Await(realSession.RefreshAsync(), "configured read-only real UTA becomes ready through fake HTTP only");
            var demoData = AccountData(demo); var testData = AccountData(testnet); var realData = AccountData(real);
            var demoBroker = (BybitBroker)Services.AccountFor(demoData, demo.Id);
            var testBroker = (BybitBroker)Services.AccountFor(testData, testnet.Id);
            var realBroker = (BybitBroker)Services.AccountFor(realData, real.Id);
            Check(demoBroker.Session == demoSession && testBroker.Session == testSession && realBroker.Session == realSession && !demoBroker.IsReal && !testBroker.IsReal && realBroker.IsReal &&
                demoData.Instrument.FeedId == BybitFeed.LinearId && testData.Instrument.FeedId == BybitFeed.TestnetId,
                "actual account factory maps demo/main-market, testnet/separate-market and real UTA contexts truthfully");
            Check(Services.AccountsFor(demoData).Any(option => option.Id == demo.Id) && !Services.AccountsFor(demoData).Any(option => option.Id == testnet.Id || option.Id == real.Id) &&
                Services.AccountsFor(testData).Any(option => option.Id == testnet.Id) && !Services.AccountsFor(testData).Any(option => option.Id == demo.Id || option.Id == real.Id),
                "actual chart-account options reject cross-environment routing and omit unapproved real increases");
            try { Services.AccountFor(demoData, testnet.Id); Check(false, "actual testnet broker factory rejects main-market prices"); }
            catch (ArgumentException) { Check(true, "actual testnet broker factory rejects main-market prices"); }
            int realCreates = realWire.Count("/v5/order/create");
            Check(realBroker.Market(OrderDirections.Buy, .1m).Status == OrderStatus.Rejected && realWire.Count("/v5/order/create") == realCreates,
                "unapproved real exposure is rejected before any create request even against the fake endpoint");
            Check(realBroker.PositionReady && realBroker.OrdersReady && realBroker.Position.Volume == 1m,
                "real read-only fixture has an authoritative long1 position before testing opposite-side non-reduce-only requests");
            var ordinaryExits = new[]
            {
                realBroker.Limit(OrderDirections.Sell, .1m, 110, "Unapproved ordinary opposite limit"),
                realBroker.Stop(OrderDirections.Sell, .1m, 90, "Unapproved ordinary opposite stop"),
                realBroker.Market(OrderDirections.Sell, .1m, "Unapproved ordinary opposite market"),
            };
            Until(() => ordinaryExits.All(order => order.State == OrderStates.Failed),
                "actual root permission guard rejects all ordinary opposite-side orders while real permission is disabled");
            Check(ordinaryExits.All(order => order.Status == OrderStatus.Rejected && !order.ReduceOnly) && realWire.Count("/v5/order/create") == realCreates,
                "currently reducing plain Sell Limit/Stop/Market requests cannot bypass revoked exposure permission or send any create POST");
            var account = Account(demo); var view = SelectAccount(account); var snapshot = Services.AccountCenter.Snapshot(account);
            Check(snapshot.Balance == 1000 && snapshot.Available == 800 && snapshot.OpenPnlKnown && snapshot.OpenPnl == 3 && snapshot.RealizedPnlKnown && snapshot.RealizedToday == 12.50m &&
                !snapshot.AnalysisKnown && snapshot.Positions.Single().Net == .75m && !snapshot.Positions.Single().ClosedPnlKnown,
                "actual account center separates reported wallet/open/closed PnL from unknown historical cost basis and per-position realized result");
            Check(snapshot.Fills.Count == 1 && !snapshot.Fills[0].Buy && snapshot.Fills[0].Quantity == 1m && snapshot.Fills[0].Price == 112.55m && snapshot.Fills[0].Commission == .05m &&
                demoSession.Executions.Single().ClosedQuantity == 1m && demoSession.ExecutionsReady && snapshot.FinancialsKnown && snapshot.CostsComplete,
                "provider realized12.50 has its actual closing execution112.55/fee0.05/closedSize1, without inventing an old opening fill");
            Snapshot("Bybit known provider fields with unknown opening basis", snapshot);
            Check(Card(view, 0) != "—" && Card(view, 2) != "—" && Card(view, 3) != "—" && Field<StackPanel>(view, "_positions").GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "—"),
                "actual Account page displays known wallet/PnL but not an invented zero per-position realized result");
            Shot("kvp_providers_bybit_reported_financials");
            demoWire.FailPositions = true;
            Rejects<BybitUnknownException>(demoSession.RefreshAsync(), "failed position snapshot cannot claim a flat account");
            snapshot = Services.AccountCenter.Snapshot(account); RefreshUi();
            Check(!demoBroker.PositionReady && !demoBroker.OrdersReady && demoBroker.Position.Volume == .75m && !snapshot.PositionsKnown && !snapshot.OpenPnlKnown && !snapshot.OrdersKnown &&
                Card(view, 2) == "—" && Card(view, 5) == "—" && Texts(view).Any(text => TextIs(text, "Unknown")),
                "actual failed-snapshot UI retains previous exposure as unknown and hides false zero PnL/order counts");
            int before = demoWire.Count("/v5/order/create");
            Check(demoBroker.Market(OrderDirections.Buy, .1m).Status == OrderStatus.Rejected && demoWire.Count("/v5/order/create") == before, "unknown account readiness blocks a new actual broker order");
            Snapshot("Bybit failed position snapshot", snapshot); Shot("kvp_providers_bybit_unknown_position");
            demoWire.FailPositions = false; Await(demoSession.RefreshAsync(), "a complete authoritative snapshot restores account readiness");
            demoWire.WalletKnown = false; Await(demoSession.RefreshAsync(), "wallet fixture supplies absent rather than zero financial fields"); RefreshUi();
            snapshot = Services.AccountCenter.Snapshot(account);
            Check(snapshot.Balance == null && snapshot.Available == null && snapshot.OpenPnlKnown && snapshot.OpenPnl == 3 && Card(view, 0) == "—" && Card(view, 2) != "—",
                "actual account UI handles unknown wallet independently of known nonflat position PnL");
            Snapshot("Bybit missing wallet measurements", snapshot); Shot("kvp_providers_bybit_unknown_wallet");
            demoWire.WalletKnown = true; Await(demoSession.RefreshAsync(), "known wallet measurements recover without fabricated opening fills");
            testWire.HedgeMode = true; Rejects<NotSupportedException>(testSession.RefreshAsync(), "actual UTA client rejects hedge-mode positions without silently switching account mode");
            Check(!testBroker.PositionReady && !testBroker.OrdersReady, "unsupported one-way constraint leaves the actual testnet account unready");
            testWire.HedgeMode = false; Await(testSession.RefreshAsync(), "restored one-way fake report recovers testnet readiness");
            ExerciseExecutions(demo, demoWire, demoSession, demoBroker);
            ExerciseAmbiguity(demo, demoWire, demoSession, demoBroker);
            ExerciseRetirement(real, realWire, replacementWire, realSession, realBroker, realData);
            ExerciseImmutablePriceConflict(demo, demoWire, demoSession);
            ExercisePanelIdentity(exchange);
            ExerciseWorkspaceIdentity(exchange);
            ExerciseAccountContextCutover(exchange);
        }

        private ConnectionProfile ConfigureBybit(BybitEnvironment environment, KvpProvidersExchange.Context wire, bool expectedReady = true)
        {
            var profile = new ConnectionProfile { Id = "kvp-ui-bybit-" + environment.ToString().ToLowerInvariant(), Kind = ConnectionKind.Bybit, Name = "Bybit " + environment + " — FAKE endpoint" };
            Connections.Profiles.Add(profile); Connections.Save();
            var page = SelectConnection(profile);
            Input<ComboBox>(page, "Environment").SelectedItem = environment;
            Type(Input<TextBox>(page, "API key"), wire.Key); Type(Input<TextBox>(page, "API secret"), wire.Secret);
            var key = Input<TextBox>(page, "API key"); var secret = Input<TextBox>(page, "API secret");
            Check(key.PasswordChar != '\0' && secret.PasswordChar != '\0', "actual " + environment + " credential editors conceal both private values");
            Click(Button(page, "Save")); pump(0.04);
            Check(profile.BybitEnvironment == environment && profile.ApiKey.Length == 0 && Connections.HasTradingCredentials(profile) && key.Text == "" && secret.Text == "",
                "actual " + environment + " settings save both credentials privately and clear the editors");
            var results = Field<Dictionary<string, (bool Ok, string Message, DateTime When)>>(page, "_results");
            DateTime started = DateTime.UtcNow;
            Click(Button(page, "Test connection"));
            Until(() => results.TryGetValue(profile.Id, out var completed) && completed.When >= started, "actual " + environment + " Test connection completes fake catalog/UTA requests");
            Check(results.TryGetValue(profile.Id, out var tested) && tested.Ok == expectedReady,
                "actual " + environment + " Test connection reports " + (expectedReady ? "the configured UTA account" : "unsupported non-UTA state without a false ready result"),
                results.TryGetValue(profile.Id, out tested) ? new { tested.Ok, tested.Message } : null);
            Check(Texts(page).Any(text => text.Contains("UTA", StringComparison.Ordinal) && text.Contains("one-way", StringComparison.OrdinalIgnoreCase)), "actual " + environment + " panel exposes UTA/one-way limitations");
            Shot("kvp_providers_bybit_" + environment.ToString().ToLowerInvariant() + "_settings");
            return profile;
        }
        private InstrumentData AccountData(ConnectionProfile profile)
        {
            var feed = Connections.GetFeed(profile.Id);
            var instrument = Await(feed.GetInstrumentsAsync(default), "actual " + profile.BybitEnvironment + " public catalog parses the fake wire instrument").Single();
            var data = new InstrumentData(instrument, feed) { RecordLive = false };
            data.Trades.Add(new Trade(1, DateTime.UtcNow.AddMinutes(-1), 100, 1, TradeSide.Buy)); _accountData.Add(data); return data;
        }
        private void ExerciseExecutions(ConnectionProfile profile, KvpProvidersExchange.Context wire, BybitTradingSession session, BybitBroker broker)
        {
            var order = broker.Limit(OrderDirections.Buy, 1, 100, "UI fake execution fixture");
            Until(() => order.ExchangeOrderId.Length > 0, "actual broker opening request receives its exchange identity");
            Await(session.RefreshAsync(), "authoritative order report confirms the initial handle");
            string id = order.ExchangeOrderId;
            string clientIdentity = wire.Report(id)["orderLinkId"]!.GetValue<string>();
            Check(session.StableOrderIdentityOf(order) is { } stable && stable.StartsWith("bybit:", StringComparison.Ordinal) && stable.EndsWith(":" + clientIdentity, StringComparison.Ordinal) &&
                ReferenceEquals(session.Orders.Single(o => o.ExchangeOrderId == id), order),
                "actual broker/session preserve stable account-client identity and the same order handle");
            wire.Fill(id, "ui-partial", .3m, 101, .12m, "USDT");
            Await(session.RefreshAsync(), "actual partial execution and fee reach session and durable journal");
            var account = Account(profile); var snapshot = Services.AccountCenter.Snapshot(account);
            var partial = snapshot.Fills.Single(fill => fill.Id.EndsWith("ui-partial", StringComparison.Ordinal));
            Check(order.Filled == .3m && snapshot.Fills.Count == 2 && partial.Price == 101 && partial.Quantity == .3m && partial.Commission == .12m && snapshot.CostsComplete &&
                snapshot.Positions.Single().Net == 1.05m && snapshot.RealizedToday == 12.50m && !snapshot.AnalysisKnown,
                "actual execution rows use fill101/fee0.12, keep prior0.75 holdings separate from new0.3, and retain provider-reported realized12.50");
            var view = SelectAccount(account); Tab(view, 2);
            Check(Field<ListBox>(view, "_fills").Items.OfType<AccountFill>().Single(fill => fill.Id.EndsWith("ui-partial", StringComparison.Ordinal)).Commission == .12m && Texts(view).Any(text => text.Contains(.12m.ToString("0.00", TradingHub.Common.UiCulture.Current), StringComparison.Ordinal)),
                "actual Fills tab renders the nonzero provider commission rather than a guessed zero");
            Snapshot("Bybit partial execution", snapshot); Shot("kvp_providers_bybit_partial_fill_fee");
            long revision = snapshot.ExecutionRevision;
            wire.CorrectFee("ui-partial", .27m, "USDT");
            Await(session.RefreshAsync(), "same execution identity reports a supported known-to-known fee correction");
            var corrected = Services.AccountCenter.Snapshot(account);
            Check(corrected.ExecutionRevision > revision && corrected.Fills.Count == 2 && corrected.CostsComplete && corrected.FinancialsKnown &&
                corrected.OpenPnlKnown && corrected.RealizedPnlKnown && corrected.PositionsKnown && corrected.OrdersKnown && corrected.Connected &&
                !corrected.AnalysisKnown && corrected.Balance == 1000m && corrected.Available == 800m &&
                corrected.Fills.Single(fill => fill.Id.EndsWith("ui-partial", StringComparison.Ordinal)) is { Price: 101m, Quantity: .3m, Commission: .27m },
                "fee revision changes content without count/known-flag toggles, wallet changes or mutation of the actual execution price/quantity");
            string correctedFee = (-.27m).ToString("0.00", TradingHub.Common.UiCulture.Current);
            Until(() => FillCommissionText(view, "ui-partial") == correctedFee,
                "actual Account Fills row refreshes fee0.12 to fee0.27 in place through its normal timer, without account/tab reselection");
            Check(Card(view, 0) == 1000m.ToString("#,##0.00", TradingHub.Common.UiCulture.Current) &&
                Field<ListBox>(view, "_fills").Items.Count == 2 && Field<ListBox>(view, "_trades").Items.Count == 0,
                "same-count fee UI refresh preserves reported wallet and refuses invented round trips");
            Snapshot("Bybit same-count known-to-known fee correction", corrected); Shot("kvp_providers_bybit_same_count_fee_correction");
            Tab(view, 4);
            Check(Texts(view).Any(text => TextIs(text, "STATISTICS UNAVAILABLE") || text == Lang.T("Statistics unavailable").ToUpperInvariant()) &&
                Card(view, 0) == 1000m.ToString("#,##0.00", TradingHub.Common.UiCulture.Current),
                "actual Statistics consumer stays truthfully unavailable after a same-count correction because bounded Bybit fills still lack opening basis");
            Shot("kvp_providers_bybit_same_count_fee_statistics"); Tab(view, 2);
            wire.CorrectFee("ui-partial", .12m, "USDT");
            Await(session.RefreshAsync(), "original fixture fee is restored through the same supported producer correction path");
            Until(() => FillCommissionText(view, "ui-partial") == (-.12m).ToString("0.00", TradingHub.Common.UiCulture.Current),
                "actual Fills row observes another revision with unchanged execution count and known flags");
            long restoredRevision = session.ExecutionRevision;
            Await(session.RefreshAsync(), "unchanged execution reports pass another actual provider reconciliation");
            Check(session.ExecutionRevision == restoredRevision && Services.AccountCenter.Snapshot(account).ExecutionRevision == restoredRevision,
                "unchanged polling keeps a stable producer revision rather than fabricating content changes on every account refresh");
            wire.ConfirmAmendImmediately = false;
            broker.Replace(order, 99, .6m);
            Until(() => wire.Count("/v5/order/amend") == 1, "native amendment reaches the actual signed transport");
            Await(session.RefreshAsync(), "amend command acceptance is followed by the old authoritative order report");
            Check(broker.IsReplacementPending(order) && order.Price == 100 && order.QuantityToFill == 1, "actual cancel/replace consumer does not publish requested price or quantity before matching ACK");
            wire.ConfirmAmend(id); Await(session.RefreshAsync(), "matching provider amendment report is reconciled");
            Check(!broker.IsReplacementPending(order) && order.Price == 99 && order.QuantityToFill == .9m && order.Unfilled == .6m && wire.Count("/v5/order/create") == 1,
                "matching total0.9/unfilled0.6 amendment keeps one handle and causes no second create");
            wire.ConfirmCancelImmediately = false; view = SelectAccount(account); Tab(view, 1);
            int cancels = wire.Count("/v5/order/cancel");
            Click(Button(Field<StackPanel>(view, "_orders"), "Cancel"));
            Until(() => wire.Count("/v5/order/cancel") == cancels + 1, "actual Account Orders cancel button sends a signed cancellation");
            Await(session.RefreshAsync(), "cancel acceptance is reconciled against a still-working provider report");
            Check(order.State == OrderStates.Active && order.Status != OrderStatus.Canceled, "actual account cancellation remains active until authoritative terminal ACK");
            RefreshUi(); Shot("kvp_providers_bybit_cancel_awaiting_ack");
            wire.ConfirmCancel(id); Await(session.RefreshAsync(), "provider terminal report acknowledges cancellation");
            Check(order.Status == OrderStatus.Canceled, "actual order terminal state follows the provider cancellation report");
            wire.Fill(id, "ui-late", .2m, 102, .08m, "USDT", preserveCanceled: true);
            Await(session.RefreshAsync(), "late execution arrives after cancellation"); Await(session.RefreshAsync(), "repeated late execution report is reconciled idempotently");
            snapshot = Services.AccountCenter.Snapshot(account);
            Check(order.Filled == .5m && order.Status == OrderStatus.Canceled && session.AverageFillFor(order) == 101.4m && snapshot.Fills.Count == 3 && snapshot.Fills.Where(fill => fill.OrderId == id).Sum(fill => fill.Commission) == .20m &&
                snapshot.Positions.Single().Net == 1.25m && Services.Journal.FillsOf(AppServices.BybitJournalId(profile.Id, session)).Count == 3,
                "partial0.3@101 plus late0.2@102 gives actual average101.4 and fees0.20 exactly once without adopting unrelated0.75 holdings");
            view = SelectAccount(account); Tab(view, 2); Shot("kvp_providers_bybit_late_fills_fees"); Snapshot("Bybit late fill after terminal ACK", snapshot);
            wire.Fill(id, "ui-unknown-fee", .1m, 103, null, "", preserveCanceled: true, closedQuantity: null);
            Await(session.RefreshAsync(), "execution with absent fee/currency reaches real financial consumers");
            snapshot = Services.AccountCenter.Snapshot(account); RefreshUi();
            Until(() => Texts(view).Any(text => text.Contains("Known costs only", StringComparison.Ordinal)) && Card(view, 3) == "—",
                "actual financial header refresh publishes unknown costs and realized PnL");
            Check(snapshot.Fills.Count == 4 && !snapshot.CostsComplete && snapshot.Fills.Single(fill => fill.Id.EndsWith("ui-unknown-fee", StringComparison.Ordinal)).Commission == 0 &&
                Texts(view).Any(text => text.Contains("Known costs only", StringComparison.Ordinal)), "unknown fee is explicitly excluded with a visible known-costs warning, never certified as measured zero");
            Check(!snapshot.RealizedPnlKnown && Card(view, 3) == "—" && snapshot.OpenPnlKnown,
                "an execution with absent closedSize hides daily realized PnL while preserving independently known open PnL");
            Shot("kvp_providers_bybit_unknown_fee"); Snapshot("Bybit unavailable fee", snapshot);
            Tab(view, 4);
            Check(Texts(view).Any(text => TextIs(text, "STATISTICS UNAVAILABLE") || text == Lang.T("Statistics unavailable").ToUpperInvariant()) && Field<ListBox>(view, "_trades").Items.Count == 0,
                "actual Statistics/Trades consumers refuse to manufacture round trips from finite fills and preexisting holdings");
            Shot("kvp_providers_bybit_unknown_opening_basis");
        }
        private void ExerciseImmutablePriceConflict(ConnectionProfile profile, KvpProvidersExchange.Context wire, BybitTradingSession session)
        {
            var account = Account(profile); var view = SelectAccount(account); Tab(view, 2);
            wire.ReportExecutionPrice("ui-partial", 199m);
            try
            {
                Rejects<InvalidDataException>(session.RefreshAsync(), "conflicting price under an existing execution identity is rejected rather than silently corrected");
                Until(() => Texts(view).Any(text => text.Contains("changed an execution", StringComparison.OrdinalIgnoreCase)),
                    "actual Account page exposes the immutable execution conflict from the polling session");
                var snapshot = Services.AccountCenter.Snapshot(account);
                Check(!snapshot.PositionsKnown && !snapshot.OrdersKnown && !snapshot.FinancialsKnown && snapshot.Balance == 1000m &&
                    snapshot.Fills.Single(fill => fill.Id.EndsWith("ui-partial", StringComparison.Ordinal)).Price == 101m &&
                    Field<ListBox>(view, "_fills").Items.OfType<AccountFill>().Single(fill => fill.Id.EndsWith("ui-partial", StringComparison.Ordinal)).Price == 101m,
                    "actual account preserves last measured fill101 and wallet1000 while the conflicting price199 leaves financial/account readiness explicitly unknown");
                Snapshot("Bybit conflicting immutable execution price", snapshot); Shot("kvp_providers_bybit_execution_price_conflict");
            }
            finally
            {
                wire.ReportExecutionPrice("ui-partial", 101m);
                Await(session.RefreshAsync(), "returning the original immutable execution report recovers normal reconciliation");
            }
        }
        private void ExerciseAmbiguity(ConnectionProfile profile, KvpProvidersExchange.Context wire, BybitTradingSession session, BybitBroker broker)
        {
            Await(session.RefreshAsync(), "account is reconciled before a second independent order");
            var plan = new KvpProvidersExchange.CreatePlan(ambiguous: true); wire.NextCreate = plan;
            int creates = wire.Count("/v5/order/create"), cancels = wire.Count("/v5/order/cancel");
            var order = broker.Limit(OrderDirections.Buy, .2m, 98, "UI fake ambiguous outcome");
            string id = Await(plan.Received.Task, "fake exchange receives exactly one ambiguous placement");
            Until(() => session.LastError?.Contains("unknown", StringComparison.OrdinalIgnoreCase) == true, "actual session exposes the ambiguous create outcome");
            for (int pass = 0; pass < 3; pass++) Await(session.RefreshAsync(), "exact order lookup retains the unresolved outcome, pass " + (pass + 1));
            Check(order.State == OrderStates.Active && session.IsSubmissionUnknown(order) && !session.OrdersReady && order.ExchangeOrderId.Length == 0 && wire.Count("/v5/order/create") == creates + 1,
                "absent open/history lookups leave the same submission unknown and never invent cancellation or resubmit");
            var account = Account(profile); var view = SelectAccount(account); Tab(view, 1);
            Check(Texts(view).Any(text => text.Contains("Submission unknown", StringComparison.Ordinal)), "actual Account Orders table exposes the unresolved submission");
            Snapshot("Bybit ambiguous placement with absent final lookup", Services.AccountCenter.Snapshot(account)); Shot("kvp_providers_bybit_ambiguous_placement");
            var activeRow = Field<StackPanel>(view, "_orders").Children.OfType<Border>().Single(row => row.GetVisualDescendants().OfType<Button>().Any(button => TextIs(Label(button), "Cancel") && button.IsVisible));
            Click(Button(activeRow, "Cancel")); pump(.08);
            Check(wire.Count("/v5/order/cancel") == cancels, "canceling an unknown pre-ACK order queues intent without guessing an exchange identity");
            wire.ConfirmCancelImmediately = true; wire.Reveal(id);
            Await(session.RefreshAsync(), "exact identity reconciliation finds the previously hidden exchange order");
            Until(() => wire.Count("/v5/order/cancel") == cancels + 1, "queued cancellation drains through the recovered original client identity");
            Await(session.RefreshAsync(), "recovered order receives its terminal cancellation report");
            Check(order.Status == OrderStatus.Canceled && ReferenceEquals(session.Orders.Single(candidate => candidate.ExchangeOrderId == id), order) && wire.Count("/v5/order/create") == creates + 1,
                "ambiguous reconciliation/cancel preserves the original handle and creates no duplicate order");
        }
        private void ExerciseRetirement(ConnectionProfile profile, KvpProvidersExchange.Context wire, KvpProvidersExchange.Context replacementWire, BybitTradingSession session, BybitBroker broker, InstrumentData data)
        {
            var page = SelectConnection(profile);
            var allow = page.GetVisualDescendants().OfType<CheckBox>().Single(check => TextIs(check.Content as string, "Allow REAL orders from Trading Hub"));
            allow.IsChecked = true; Click(Button(page, "Save")); pump(.05);
            Check(Account(profile).CanTrade && Services.AccountsFor(data).Any(option => option.Id == profile.Id && option.IsReal), "actual explicit real permission changes account availability without changing credentials or prices");
            Await(session.RefreshAsync(), "real-context readiness is known before owned protection starts");
            var held = new KvpProvidersExchange.CreatePlan(hold: true); wire.NextCreate = held; wire.ConfirmCancelImmediately = false;
            using var runner = new LiveStrategyRunner(new OwnershipProbe { StopLossTicks = 50 }, data, broker, ChartPeriod.M1)
            { ProtectOnStart = true, AccountId = profile.Id, ProfileId = profile.Id };
            PrepareIdentity(runner, "actual runner verifies provider/environment/origin/UID before accessing its seeded persistent ledger");
            runner.SeedPosition(new LiveFill(DateTime.UtcNow.AddDays(-30), true, .25m, 100)); runner.Start();
            string id = Await(held.Received.Task, "actual live-runner protection reaches a held fake create ACK");
            var order = session.Orders.Single(candidate => candidate.State == OrderStates.Active);
            var report = wire.Report(id);
            Check(order.ExchangeOrderId.Length == 0 && session.IsSubmissionUnknown(order) && report["reduceOnly"]!.GetValue<bool>() && report["qty"]!.GetValue<string>() == "0.25" &&
                runner.Ledger?.Position == .25m && broker.Position.Volume == 1m && runner.ProtectionOrderIds.Single() == "client:" + session.StableOrderIdentityOf(order),
                "real runner protects only its seeded0.25, not the unrelated0.75 holding, and owns a durable pre-ACK client identity");
            page = SelectConnection(profile); allow = page.GetVisualDescendants().OfType<CheckBox>().Single(check => TextIs(check.Content as string, "Allow REAL orders from Trading Hub"));
            allow.IsChecked = false; Click(Button(page, "Save")); pump(.05);
            int creates = wire.Count("/v5/order/create");
            Check(broker.Market(OrderDirections.Buy, .1m).Status == OrderStatus.Rejected && wire.Count("/v5/order/create") == creates, "revoked actual exposure permission blocks increases while the owned protection ACK is held");
            runner.Stop(flatten: false, keepProtection: false);
            Check(runner.HasWorkingOrders, "stopped actual runner retains its owned cancellation until the held broker ACK resolves");
            page = SelectConnection(profile); Type(Input<TextBox>(page, "API key"), replacementWire.Key); Type(Input<TextBox>(page, "API secret"), replacementWire.Secret);
            Click(Button(page, "Save")); pump(.05);
            var replacement = Services.BybitSessionFor(profile); Await(replacement.RefreshAsync(), "replacement credential context validates its own distinct fake account");
            var retired = Account(profile, session); var view = SelectAccount(retired); Tab(view, 1);
            Check(session.IsRetired && !ReferenceEquals(replacement, session) && !retired.CanTrade && retired.Name.Contains("previous credentials", StringComparison.Ordinal) &&
                Services.Connections.SavedBybitContext(profile, session.ContextIdentity) != null && Services.AccountCenter.Snapshot(retired).Positions.Any(position => position.Net == 1m),
                "actual credential reconfigure retains old exposure/transport privately and shows a reduce/cancel-only account instead of assigning its holdings to the replacement");
            Snapshot("Retired Bybit credentials with owned held ACK", Services.AccountCenter.Snapshot(retired)); Shot("kvp_providers_bybit_retired_held_ack");
            int cancels = wire.Count("/v5/order/cancel");
            var cancelButton = Button(Field<StackPanel>(view, "_orders"), "Cancel");
            Check(cancelButton.IsVisible && cancelButton.Flyout is Flyout, "actual retired real-account row retains its safety-confirmed cancel action");
            var confirmation = (Flyout)cancelButton.Flyout!; confirmation.ShowAt(cancelButton); pump(.05);
            Click(Button((Control)confirmation.Content!, "Cancel order")); pump(.05);
            Check(wire.Count("/v5/order/cancel") == cancels && order.Status != OrderStatus.Canceled, "actual real-account cancel waits for held placement identity instead of publishing premature cancellation");
            held.Release.TrySetResult();
            Until(() => order.ExchangeOrderId == id && wire.Count("/v5/order/cancel") == cancels + 1, "retired credential transport drains the held create ACK and its queued owned cancellation");
            Await(session.RefreshAsync(), "retired context still reconciles authoritative working reports");
            Check(order.State == OrderStates.Active && wire.Count("/v5/order/create") == creates && replacementWire.Count("/v5/order/create") == 0,
                "held-ACK drain never duplicates the create or transfers owned protection to replacement credentials");
            wire.ConfirmCancel(id); Await(session.RefreshAsync(), "old private context receives authoritative canceled status");
            Until(() => !runner.HasWorkingOrders, "actual stopped runner releases owned cancellation only after the terminal broker ACK");
            Check(order.Status == OrderStatus.Canceled && runner.Ledger?.Position == .25m && broker.Position.Volume == 1m,
                "owned cancellation leaves runner0.25 and unrelated real holdings untouched");
            var reduction = broker.Exit(OrderTypes.Market, OrderDirections.Sell, .1m, 0, "UI explicit native reduction");
            Until(() => reduction.ExchangeOrderId.Length > 0, "revoked and retired context still sends an explicit native reducing exit");
            Await(session.RefreshAsync(), "native reducing exit is observed through the retired account session");
            var reducing = wire.Report(reduction.ExchangeOrderId);
            Check(reducing["reduceOnly"]!.GetValue<bool>() && reducing["orderType"]!.GetValue<string>() == "Market" && reducing["qty"]!.GetValue<string>() == "0.1" &&
                broker.Market(OrderDirections.Buy, .1m).Status == OrderStatus.Rejected && replacementWire.Count("/v5/order/create") == 0,
                "native reduction/cancel use the old immutable credentials while new increases and replacement-account adoption stay blocked");
            var line = Services.AccountCenter.Snapshot(retired).Orders.Single(candidate => candidate.Key == reduction.ExchangeOrderId);
            Await(Services.AccountCenter.CancelAsync(retired, line), "actual AccountCenter cancel remains available after permission/context revocation");
            Until(() => wire.Count("/v5/order/cancel") == cancels + 2, "explicit retired-context reduction cancellation reaches the original transport");
            wire.ConfirmCancel(reduction.ExchangeOrderId); Await(session.RefreshAsync(), "retired-context reduction drains its terminal ACK");
            Check(wire.Requests.Where(request => request.Route is "/v5/order/create" or "/v5/order/cancel").All(request => request.Headers["X-BAPI-API-KEY"] == wire.Key) &&
                Services.AccountCenter.Snapshot(Account(profile)).Positions.All(position => position.Net == 0), "all old owned mutation ACKs use original private credentials; replacement account remains independently flat");
            view = SelectAccount(retired); Tab(view, 1); Shot("kvp_providers_bybit_retired_ack_drained");
            Snapshot("Retired Bybit ACKs drained with holdings retained", Services.AccountCenter.Snapshot(retired));
        }
        private void ExercisePanelIdentity(KvpProvidersExchange exchange)
        {
            const string classPrefix = "KvpProviderUidFixture";
            var template = new StrategyTemplate("Verified provider identity fixture", "Passive fake-provider launch acceptance", classPrefix, false,
                name => "using TradingHub.Strategies; public sealed class " + name + " : Strategy { protected override void OnBar(Bar bar) { } }");
            var file = Services.Strategies.Create(classPrefix + Guid.NewGuid().ToString("N"), template);
            LiveStrategyRunner? initialRunner = null, resumedRunner = null;
            var gates = new List<KvpProvidersExchange.UidGate>();
            Task<(LiveStrategyRunner? Runner, string Message)>? initialLaunch = null;
            try
            {
                var compilation = Services.Strategies.Compile(file);
                Check(compilation.Success && file.Type != null, "actual strategy catalog compiles the owned passive provider-identity fixture");
                if (file.Type == null) throw new InvalidOperationException("Provider identity fixture did not compile.");
                var original = exchange.Add("kvp-private-held-uid-key", "kvp-private-held-uid-secret", 900011, .25m);
                _secrets.Add(original.Key); _secrets.Add(original.Secret);
                var firstGate = new KvpProvidersExchange.UidGate(); original.HoldUid = firstGate; gates.Add(firstGate);
                var profile = new ConnectionProfile { Id = "kvp-ui-bybit-uid", Kind = ConnectionKind.Bybit, Name = "Bybit held UID — FAKE endpoint", HistoryHours = .1 };
                Connections.SetSecret(profile, "apikey", original.Key); Connections.SetSecret(profile, original.Secret);
                var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, PointValue = 1, BaseAsset = "BTC", QuoteAsset = "USDT" };
                var raw = new KvpCandleSceneFeed(BybitFeed.LinearId, instrument) { Now = DateTime.UtcNow };
                raw.Trades.AddRange([new(1, raw.Now.AddMinutes(-2), 100, 1, TradeSide.Buy), new(2, raw.Now.AddMinutes(-1), 100, 1, TradeSide.Sell)]);
                var feed = new KvpProvidersVenue(raw);
                KvpSceneFeed.Register(Services, profile, feed); Connections.Save();
                var data = Services.Open(profile.Id, instrument);
                Until(() => !data.IsLoadingHistory, "actual panel launch uses preloaded deterministic venue data with no provider fallback");
                string[] LedgerFiles() => Directory.Exists(LiveStore.Root)
                    ? Directory.EnumerateFiles(LiveStore.Root, "*.jsonl", SearchOption.AllDirectories).Where(path => Path.GetFileName(path).StartsWith(file.Id + "_", StringComparison.Ordinal)).ToArray()
                    : Array.Empty<string>();
                Check(LedgerFiles().Length == 0, "owned panel strategy has no ledger before verified account identity");
                initialLaunch = Services.Live.LaunchAsync(new LiveLaunch
                {
                    File = file, ProfileId = profile.Id, Instrument = instrument, AccountId = profile.Id, Period = ChartPeriod.M1,
                    Parameters = new() { ["StopLossTicks"] = 0, ["TakeProfitTicks"] = 0 }, Paused = true,
                });
                try
                {
                    Until(() => firstGate.Entered.Task.IsCompleted, "actual root account context holds its fresh UID HTTP response while panel LaunchAsync is pending");
                    Check(!initialLaunch.IsCompleted && !Services.Runners.Any(runner => runner.ProfileId == profile.Id) && LedgerFiles().Length == 0,
                        "actual panel launch neither starts nor opens any strategy ledger while fresh UID verification is held");
                    var pendingAccount = SelectAccount(Account(profile));
                    var pendingSnapshot = Services.AccountCenter.Snapshot(Account(profile));
                    Check(!pendingSnapshot.FinancialsKnown && !pendingSnapshot.PositionsKnown && !pendingSnapshot.OrdersKnown &&
                        pendingSnapshot.Balance == null && pendingSnapshot.Fills.Count == 0 && Card(pendingAccount, 0) == "—",
                        "actual Account UI defers journal/financial publication rather than trusting an unverified cached or profile identity");
                    Snapshot("Held UID before initial actual panel launch", pendingSnapshot); Shot("kvp_providers_panel_held_uid_account");
                    var livePage = Page("live");
                    Until(() => Texts(livePage).Any(text => text.Contains("Awaiting verified trading account identity", StringComparison.Ordinal)),
                        "actual Live panel visibly reports that saved history and protection await verified account identity");
                    Services.SaveLiveState();
                    Check(LedgerFiles().Length == 0 && !initialLaunch.IsCompleted, "normal root save while launch is pending does not read/write or adopt an unverified runner ledger");
                    Shot("kvp_providers_panel_held_uid_waiting");
                }
                finally { firstGate.Release.TrySetResult(); }
                var started = Await(initialLaunch, "held UID release completes the actual owned panel launch");
                Check(started.Runner != null && started.Runner.IsRunning && started.Runner.Source == "panel",
                    "actual panel admits the runner only after the fake provider UID is freshly verified");
                initialRunner = started.Runner ?? throw new InvalidOperationException("Verified panel launch returned no runner.");
                var originalSession = Services.BybitSessionFor(profile);
                Await(originalSession.RefreshAsync(), "verified panel account obtains authoritative positions and orders");
                Check(initialRunner.OwnPosition == 0 && initialRunner.Account.Position.Volume == .25m && original.Count("/v5/order/create") == 0,
                    "successful initial launch never appropriates an unrelated preexisting0.25 holding or creates an order");
                initialRunner.Stop(flatten: false, keepProtection: false);
                initialRunner.SeedPosition(new LiveFill(DateTime.UtcNow.AddMinutes(-1), true, .25m, 100));
                Services.SaveLiveState();
                var saved = LiveStore.InfoFor(initialRunner.Key);
                Check(saved != null && saved.Key == initialRunner.Key && saved.Position == .25m && saved.Source == "panel",
                    "normal live-state save preserves the explicitly owned old-UID ledger marker for restart acceptance");
                if (saved == null) throw new InvalidOperationException("Saved panel runner metadata is unavailable.");
                byte[] ledgerBefore = File.ReadAllBytes(LiveStore.LedgerFile(saved.Key));
                var restored = Await(Services.Live.LaunchFromInfoAsync(saved), "actual saved-run reconstruction resolves its catalog and configured instrument");
                Check(restored.Spec?.ExpectedRunnerKey == saved.Key && restored.Error == null,
                    "actual LaunchFromInfo carries the exact saved verified key into the restart request");

                void SelectCredentials(KvpProvidersExchange.Context wire)
                {
                    _secrets.Add(wire.Key); _secrets.Add(wire.Secret);
                    var connections = SelectConnection(profile);
                    Type(Input<TextBox>(connections, "API key"), wire.Key); Type(Input<TextBox>(connections, "API secret"), wire.Secret);
                    Click(Button(connections, "Save")); pump(.04);
                    // Save invalidates the quote feed. Reattach the explicit deterministic transport before any consumer can open data.
                    Connections.Profiles.Remove(profile); KvpSceneFeed.Register(Services, profile, feed);
                }
                var changed = exchange.Add("kvp-private-different-uid-key", "kvp-private-different-uid-secret", 900012);
                var changedGate = new KvpProvidersExchange.UidGate(); changed.HoldUid = changedGate; gates.Add(changedGate);
                SelectCredentials(changed);
                var entry = Services.Live.Entries().Single(item => item.Key == saved.Key);
                var manage = new LiveStrategyWindow(Services, owner.SetStatus, entry);
                manage.Show(owner); pump(.1);
                try
                {
                    owner.SetStatus("KVP fake saved-run restart pending");
                    Click(Button(manage, "Restart"));
                    try
                    {
                        Until(() => changedGate.Entered.Task.IsCompleted, "actual saved-run Restart control reaches a held fresh UID response");
                        Check(!Services.Runners.Any(runner => runner.ProfileId == profile.Id && runner.IsRunning) &&
                            ledgerBefore.SequenceEqual(File.ReadAllBytes(LiveStore.LedgerFile(saved.Key))),
                            "actual Restart control does not start or touch the old ledger before a replacement UID is verified");
                        Shot(manage, "kvp_providers_saved_restart_held_uid");
                    }
                    finally { changedGate.Release.TrySetResult(); }
                    Until(() => Field<TextBlock>(owner, "_statusText").Text?.Contains("different account scope", StringComparison.Ordinal) == true,
                        "actual Restart control visibly refuses old-UID history after the connection verifies a different UID");
                    Check(!Services.Runners.Any(runner => runner.ProfileId == profile.Id && runner.IsRunning) &&
                        changed.Count("/v5/order/create") == 0 && changed.Count("/v5/order/cancel") == 0 &&
                        ledgerBefore.SequenceEqual(File.ReadAllBytes(LiveStore.LedgerFile(saved.Key))) && LedgerFiles().Length == 1 && LiveStore.LoadLedger(saved.Key, 1).Position == .25m,
                        "saved-key mismatch is rejected before old ledger/protection adoption, broker mutations or history rewriting");
                    Shot(manage, "kvp_providers_saved_restart_wrong_uid"); Shot("kvp_providers_saved_restart_wrong_uid_status");
                }
                finally { manage.Close(); }

                var same = exchange.Add("kvp-private-restored-uid-key", "kvp-private-restored-uid-secret", original.Uid, .25m);
                var sameGate = new KvpProvidersExchange.UidGate(); same.HoldUid = sameGate; gates.Add(sameGate);
                SelectCredentials(same);
                var sameEntry = Services.Live.Entries().Single(item => item.Key == saved.Key);
                var resume = new LiveStrategyWindow(Services, owner.SetStatus, sameEntry);
                resume.Show(owner); pump(.1);
                try
                {
                    owner.SetStatus("KVP fake same-account restart pending");
                    Click(Button(resume, "Restart"));
                    try
                    {
                        Until(() => sameGate.Entered.Task.IsCompleted, "same-UID rotated key also requires a fresh provider identity response before restart");
                        Check(!Services.Runners.Any(runner => runner.ProfileId == profile.Id && runner.IsRunning),
                            "matching expected identity is not guessed from profile/name or a previous credential context while UID is held");
                    }
                    finally { sameGate.Release.TrySetResult(); }
                    Until(() => Field<TextBlock>(owner, "_statusText").Text?.Contains("running again", StringComparison.Ordinal) == true,
                        "actual saved-run Restart control completes after rotated keys verify the original UID and origin");
                    resumedRunner = Services.Runners.Single(runner => runner.ProfileId == profile.Id && runner.IsRunning);
                    Check(resumedRunner.Key == saved.Key && resumedRunner.OwnPosition == .25m && resumedRunner.Ledger.Fills.Count == 1 &&
                        same.Count("/v5/order/create") == 0 && same.Count("/v5/order/cancel") == 0 &&
                        AppServices.BybitJournalId(profile.Id, Services.BybitSessionFor(profile)) == AppServices.BybitJournalId(profile.Id, originalSession),
                        "same verified UID/origin key rotation resumes the original ledger/journal exactly once without adopting another account or submitting orders");
                    Shot(resume, "kvp_providers_saved_restart_same_uid"); Shot("kvp_providers_saved_restart_same_uid_status");
                    _observations.Add(new { name = "Actual panel UID admission and saved-run scope",
                        sameUidKeyRotationResumed = resumedRunner.Key == saved.Key, ownedQuantity = resumedRunner.OwnPosition, ledgerFills = resumedRunner.Ledger.Fills.Count });
                }
                finally { resume.Close(); }
                resumedRunner.Stop(flatten: false, keepProtection: false); Services.SaveLiveState();
                byte[] authorityLedger = File.ReadAllBytes(LiveStore.LedgerFile(saved.Key));
                using var otherAuthority = new KvpProvidersExchange();
                var foreign = otherAuthority.Add(same.Key, same.Secret, same.Uid);
                var authorityGate = new KvpProvidersExchange.UidGate(); foreign.HoldUid = authorityGate; gates.Add(authorityGate);
                BybitTradingSession? foreignSession = null;
                try
                {
                    using (var authorityEndpoint = new Endpoints(new Dictionary<string, string> { ["TRADINGHUB_BYBIT_REST"] = otherAuthority.Server.Http }))
                    {
                        Connections.Invalidate(profile);
                        Connections.Profiles.Remove(profile); KvpSceneFeed.Register(Services, profile, feed);
                        foreignSession = Services.BybitSessionFor(profile);
                        var authorityEntry = Services.Live.Entries().Single(item => item.Key == saved.Key);
                        var authorityWindow = new LiveStrategyWindow(Services, owner.SetStatus, authorityEntry);
                        authorityWindow.Show(owner); pump(.1);
                        try
                        {
                            owner.SetStatus("KVP fake alternate-authority restart pending");
                            Click(Button(authorityWindow, "Restart"));
                            try
                            {
                                Until(() => authorityGate.Entered.Task.IsCompleted, "actual root account factory holds a fresh UID response from a different fake API authority");
                                Check(!Services.Runners.Any(runner => runner.ProfileId == profile.Id && runner.IsRunning) &&
                                    authorityLedger.SequenceEqual(File.ReadAllBytes(LiveStore.LedgerFile(saved.Key))) && LedgerFiles().Length == 1,
                                    "actual saved restart waits on authority verification without loading an alternate ledger or adopting the original one");
                            }
                            finally { authorityGate.Release.TrySetResult(); }
                            Until(() => Field<TextBlock>(owner, "_statusText").Text?.Contains("different account scope", StringComparison.Ordinal) == true,
                                "actual Restart control rejects the same numeric UID when its verified API authority differs");
                            Await(foreignSession.RefreshAsync(), "alternate authority supplies a complete independently flat account snapshot");
                            Check(!Services.Runners.Any(runner => runner.ProfileId == profile.Id && runner.IsRunning) &&
                                foreign.Count("/v5/order/create") == 0 && foreign.Count("/v5/order/cancel") == 0 &&
                                authorityLedger.SequenceEqual(File.ReadAllBytes(LiveStore.LedgerFile(saved.Key))) && LedgerFiles().Length == 1 &&
                                AppServices.BybitJournalId(profile.Id, foreignSession) != AppServices.BybitJournalId(profile.Id, originalSession),
                                "different originating authority cannot reuse saved ownership, open an empty replacement ledger or mutate either account");
                            Shot(authorityWindow, "kvp_providers_saved_restart_wrong_authority"); Shot("kvp_providers_saved_restart_wrong_authority_status");
                        }
                        finally { authorityGate.Release.TrySetResult(); authorityWindow.Close(); }
                    }
                }
                finally
                {
                    Connections.Invalidate(profile);
                    Connections.Profiles.Remove(profile); KvpSceneFeed.Register(Services, profile, feed);
                    Await(Services.BybitSessionFor(profile).RefreshAsync(), "original configured fake authority is restored after the negative resume case");
                    if (foreignSession != null)
                        Until(() =>
                        {
                            Services.AccountFor(data, profile.Id); // The normal factory sweeps confirmed-idle retired contexts.
                            return !Services.BybitContexts.Any(context => ReferenceEquals(context.Session, foreignSession));
                        }, "normal root credential retirement drains the flat alternate authority before its fixture transport closes");
                }
            }
            finally
            {
                foreach (var gate in gates) gate.Release.TrySetResult();
                if (initialLaunch is { IsCompleted: false }) Await(initialLaunch, "scene cleanup drains its released initial panel launch");
                foreach (var runner in Services.Runners.Where(runner => runner.StrategyInstance.GetType() == file.Type).ToArray())
                    runner.Stop(flatten: false, keepProtection: false);
                Services.SaveLiveState();
                Services.Strategies.Delete(file);
            }
        }
        private void ExerciseWorkspaceIdentity(KvpProvidersExchange exchange)
        {
            const string prefix = "KvpProviderChartUidFixture";
            var template = new StrategyTemplate("Restored chart account fixture", "Passive fake-provider workspace acceptance", prefix, false,
                name => "using TradingHub.Strategies; public sealed class " + name + " : Strategy { protected override void OnBar(Bar bar) { } }");
            var file = Services.Strategies.Create(prefix + Guid.NewGuid().ToString("N"), template);
            var trading = owner.Trading;
            var priorCharts = trading.AllCharts.ToHashSet();
            var priorActive = trading.ActiveTab;
            bool priorAutoResume = Services.Settings.AutoResumeLive;
            var priorAtClose = Services.RunningAtLastClose;
            var gates = new List<KvpProvidersExchange.UidGate>();
            var starts = new List<Task>();
            BybitTradingSession? originalSession = null, sameSession = null;
            try
            {
                var compilation = Services.Strategies.Compile(file);
                Check(compilation.Success && file.Type != null, "actual strategy catalog compiles the owned workspace/UID fixture");
                if (file.Type == null) throw new InvalidOperationException("Restored chart fixture did not compile.");
                var original = exchange.Add("kvp-private-chart-owner-key", "kvp-private-chart-owner-secret", 900041, .25m);
                _secrets.Add(original.Key); _secrets.Add(original.Secret);
                var profile = new ConnectionProfile
                {
                    Id = "kvp-ui-bybit-chart-uid", Kind = ConnectionKind.Bybit, BybitEnvironment = BybitEnvironment.Demo,
                    Name = "Bybit restored chart — FAKE endpoint", HistoryHours = .1,
                };
                Connections.SetSecret(profile, "apikey", original.Key); Connections.SetSecret(profile, original.Secret);
                var instrument = new Instrument { Symbol = "BTCUSDT", FeedId = BybitFeed.LinearId, TickSize = .1m, VolumeStep = .001m, PointValue = 1, BaseAsset = "BTC", QuoteAsset = "USDT" };
                var raw = new KvpCandleSceneFeed(BybitFeed.LinearId, instrument) { Now = DateTime.UtcNow };
                raw.Trades.AddRange([new(1, raw.Now.AddMinutes(-2), 100, 1, TradeSide.Buy), new(2, raw.Now.AddMinutes(-1), 100, 1, TradeSide.Sell)]);
                var feed = new KvpProvidersVenue(raw);
                KvpSceneFeed.Register(Services, profile, feed); Connections.Save();
                originalSession = Services.BybitSessionFor(profile);
                Await(originalSession.RefreshAsync(), "original chart credentials verify their fake native UID and position before ownership is seeded");
                owner.Navigate("trading");
                var chart = Await(trading.NewTabAsync(profile.Id, instrument.Symbol, ChartPeriod.M1, accountId: profile.Id),
                    "actual TradingView opens the explicitly scoped deterministic Bybit chart");
                Until(() => chart.Model is { Data.IsLoadingHistory: false }, "actual chart history finishes through the injected venue transport");
                Await(trading.AddStrategyAsync(chart, file, new Dictionary<string, object?> { ["StopLossTicks"] = 50, ["TakeProfitTicks"] = 0 }, openSettings: false),
                    "actual chart adds and runs the passive strategy in visual mode before live admission");
                var cs = chart.Strategies.Single(strategy => strategy.StrategyId == file.Id);
                var account = Services.AccountFor(chart.Model!.Data, profile.Id);
                using (var seed = new LiveStrategyRunner(file.Create(), chart.Model.Data, account, ChartPeriod.M1)
                    { Persist = true, Source = "chart", AccountId = profile.Id, ProfileId = profile.Id })
                {
                    PrepareIdentity(seed, "owned restored-chart ledger is seeded only after actual native UID verification");
                    seed.SeedPosition(new LiveFill(DateTime.UtcNow.AddMinutes(-1), true, .25m, 100));
                }
                cs.Live = true; cs.AccountId = profile.Id;
                Await(trading.RunStrategyAsync(chart, cs), "actual chart live consumer restores its deliberately owned0.25 ledger");
                var running = cs.Runner ?? throw new InvalidOperationException("Original chart did not create its live runner.");
                Until(() => running.IsRunning && running.ProtectionOrderIds.Count == 1 &&
                    originalSession.Orders.Any(order => order.Type == OrderTypes.Stop && order.ExchangeOrderId.Length > 0),
                    "actual chart runner arms one native protective stop for its owned quantity");
                Await(originalSession.RefreshAsync(), "native protective-stop acknowledgement is available before workspace save");
                string originalKey = running.Key;
                var protectionIds = running.ProtectionOrderIds.ToArray();
                var stop = originalSession.Orders.Single(order => order.Type == OrderTypes.Stop && order.State == OrderStates.Active);
                string stopId = stop.ExchangeOrderId;
                var atClose = running.Info();
                Check(running.Source == "chart" && cs.PersistentOwnerKey == originalKey && running.OwnPosition == .25m &&
                    original.Count("/v5/order/create") == 1 && original.Report(stopId)["qty"]!.GetValue<string>() == "0.25",
                    "actual chart admission anchors its verified account key and protects only the explicitly owned0.25");
                running.Stop(flatten: false, keepProtection: true);
                cs.Paused = true;
                Await(trading.RunStrategyAsync(chart, cs, keepProtection: true), "normal chart pause retains native protection and removes its stopped runner association");
                Check(cs.Runner == null && cs.PersistentOwnerKey == originalKey && stop.State == OrderStates.Active &&
                    original.Count("/v5/order/cancel") == 0 && LiveStore.InfoFor(originalKey)?.ProtectionOrderIds?.SequenceEqual(protectionIds) == true,
                    "paused chart keeps the original native stop and its saved owning-account identity without canceling or reassignment");
                Workspace.Save(trading);
                string savedWorkspace = File.ReadAllText(AppPaths.WorkspaceFile);
                using (var saved = JsonDocument.Parse(savedWorkspace))
                {
                    var dto = saved.RootElement.GetProperty("Pages").EnumerateArray()
                        .SelectMany(page => page.GetProperty("Charts").EnumerateArray())
                        .SelectMany(tab => tab.GetProperty("Strategies").EnumerateArray())
                        .Single(strategy => strategy.GetProperty("Id").GetString() == file.Id);
                    Check(dto.GetProperty("PersistentOwnerKey").GetString() == originalKey &&
                        dto.GetProperty("AccountId").GetString() == profile.Id && dto.GetProperty("Live").GetBoolean(),
                        "normal Workspace.Save persists the chart's verified owner anchor beside its actual strategy/account configuration");
                }
                byte[] ledgerBefore = File.ReadAllBytes(LiveStore.LedgerFile(originalKey));
                string[] LedgerFiles() => Directory.EnumerateFiles(LiveStore.Root, "*.jsonl", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path).StartsWith(file.Id + "_", StringComparison.Ordinal)).ToArray();
                trading.RemoveStrategy(chart, cs);
                trading.CloseChart(chart);

                void Rotate(KvpProvidersExchange.Context wire)
                {
                    _secrets.Add(wire.Key); _secrets.Add(wire.Secret);
                    var connections = SelectConnection(profile);
                    Type(Input<TextBox>(connections, "API key"), wire.Key); Type(Input<TextBox>(connections, "API secret"), wire.Secret);
                    Click(Button(connections, "Save")); pump(.04);
                    Connections.Profiles.Remove(profile); KvpSceneFeed.Register(Services, profile, feed);
                }
                var changed = exchange.Add("kvp-private-chart-other-key", "kvp-private-chart-other-secret", 900042);
                var changedGate = new KvpProvidersExchange.UidGate(); changed.HoldUid = changedGate; gates.Add(changedGate);
                Rotate(changed);
                // Restore bytes produced by the real Save above, not a hand-authored DTO. Closing the old chart
                // and saving credentials may normally save the current layout, just as preparation for reopening does.
                File.WriteAllText(AppPaths.WorkspaceFile, savedWorkspace);
                Check(Await(Workspace.RestoreAsync(trading), "actual Workspace.RestoreAsync rebuilds the saved live chart and strategy"),
                    "actual workspace restore recognizes the normal saved chart document");
                var restoredChart = trading.AllCharts.Single(tab => tab.Strategies.Any(strategy => strategy.StrategyId == file.Id));
                var restored = restoredChart.Strategies.Single(strategy => strategy.StrategyId == file.Id);
                Until(() => !restored.Busy, "restored chart completes its normal paused history calculation without starting live");
                Until(() => changedGate.Entered.Task.IsCompleted, "restored chart/current account consumer reaches the held replacement UID HTTP response");
                var pending = Services.Live.Entries().Single(entry => ReferenceEquals(entry.ChartStrategy, restored));
                Check(restored.Live && restored.Paused && restored.Runner == null && restored.PersistentOwnerKey == originalKey &&
                    pending.Key == originalKey && pending.Source == "chart" && ReferenceEquals(pending.Chart, restoredChart) &&
                    pending.Ledger == null && pending.AwaitingAccountIdentity && restored.SavedLedger == null &&
                    Services.Live.Entries().Count(entry => entry.Key == originalKey) == 1,
                    "held-UID workspace restore retains one original-key chart/ChartStrategy association, never a panel fallback or prematurely adopted saved ledger");
                var live = Page("live");
                Until(() => Texts(live).Any(text => text.Contains("Awaiting verified trading account identity", StringComparison.Ordinal)),
                    "actual Live UI visibly identifies the restored chart's pending account/ledger verification");
                Shot("kvp_providers_workspace_held_uid");
                Services.Settings.AutoResumeLive = true;
                Services.RunningAtLastClose = new() { atClose };
                Task differentResume = Services.Live.ResumeAtStartAsync(); starts.Add(differentResume);
                Check(!differentResume.IsCompleted && restored.Runner == null && restored.PersistentOwnerKey == originalKey &&
                    Services.Live.Entries().Single(entry => entry.Key == originalKey).ChartStrategy == restored &&
                    LedgerFiles().Length == 1 && ledgerBefore.SequenceEqual(File.ReadAllBytes(LiveStore.LedgerFile(originalKey))) &&
                    changed.Count("/v5/order/create") == 0 && changed.Count("/v5/order/cancel") == 0,
                    "actual auto-resume remains attached to the chart while UID is held, without Start, ledger I/O adoption, alternate ledger or broker mutation");
                changedGate.Release.TrySetResult();
                Await(differentResume, "actual chart auto-resume settles after the replacement UID verifies as a different account");
                Until(() => !restored.Busy, "different-UID chart attempt leaves its actual overlay in a settled refusal state");
                var refused = Services.Live.Entries().Single(entry => entry.Key == originalKey);
                Check(restored.Runner == null && restored.PersistentOwnerKey == originalKey &&
                    restored.Overlay.Error?.Contains("another verified account", StringComparison.Ordinal) == true &&
                    ReferenceEquals(refused.ChartStrategy, restored) && refused.Ledger == null && refused.AwaitingAccountIdentity &&
                    LedgerFiles().Length == 1 && ledgerBefore.SequenceEqual(File.ReadAllBytes(LiveStore.LedgerFile(originalKey))) &&
                    !Services.Runners.Any(runner => runner.StrategyInstance.GetType() == file.Type && runner.IsRunning) &&
                    changed.Count("/v5/order/create") == 0 && changed.Count("/v5/order/cancel") == 0 &&
                    original.Count("/v5/order/create") == 1 && original.Count("/v5/order/cancel") == 0 && stop.State == OrderStates.Active,
                    "different verified UID refuses the saved chart before Start/adoption, keeps its original native protection and ledger unchanged, and creates no alternate run/account orders");
                owner.Navigate("trading"); trading.Activate(restoredChart); pump(.1);
                Shot("kvp_providers_workspace_wrong_uid_chart");
                Page("live"); RefreshUi(); Shot("kvp_providers_workspace_wrong_uid_live");

                var same = exchange.Add("kvp-private-chart-rotated-key", "kvp-private-chart-rotated-secret", original.Uid);
                same.ShareAccountReports(original);
                var sameGate = new KvpProvidersExchange.UidGate(); same.HoldUid = sameGate; gates.Add(sameGate);
                Rotate(same); sameSession = Services.BybitSessionFor(profile);
                Services.RunningAtLastClose = new() { atClose };
                Task sameResume = Services.Live.ResumeAtStartAsync(); starts.Add(sameResume);
                Until(() => sameGate.Entered.Task.IsCompleted, "same-UID key rotation still performs a fresh native UID request for the restored chart");
                var samePending = Services.Live.Entries().Single(entry => entry.Key == originalKey);
                Check(!sameResume.IsCompleted && restored.Runner == null && samePending.ChartStrategy == restored &&
                    samePending.Ledger == null && restored.PersistentOwnerKey == originalKey &&
                    same.Count("/v5/order/create") == 0 && same.Count("/v5/order/cancel") == 0,
                    "same-account key rotation does not infer saved-chart ownership before its new credential context verifies");
                sameGate.Release.TrySetResult();
                Await(sameResume, "actual chart auto-resume accepts freshly verified rotated keys for the original native UID");
                Until(() => restored.Runner is { IsRunning: true } runner && runner.ProtectionOrderIds.SequenceEqual(protectionIds),
                    "same-UID restored chart adopts its original native protective stop rather than replacing it");
                var resumed = restored.Runner!;
                Check(resumed.Source == "chart" && resumed.Key == originalKey && resumed.AccountId == profile.Id &&
                    restored.PersistentOwnerKey == originalKey && resumed.OwnPosition == .25m && resumed.Ledger.Fills.Count == 1 &&
                    ReferenceEquals(Services.Live.Entries().Single(entry => entry.Key == originalKey).ChartStrategy, restored) &&
                    same.Count("/v5/order/create") == 0 && same.Count("/v5/order/cancel") == 0 &&
                    original.Count("/v5/order/create") == 1 && original.Count("/v5/order/cancel") == 0 &&
                    same.Report(stopId)["orderStatus"]!.GetValue<string>() == "Untriggered",
                    "verified same-UID rotation resumes the original ledger on the restored chart and adopts the exact original stop with no duplicate create/cancel");
                owner.Navigate("trading"); trading.Activate(restoredChart); pump(.1);
                Shot("kvp_providers_workspace_same_uid_chart");
                trading.SetChartAccount(restoredChart, "sim");
                Check(restoredChart.AccountId == "sim" && restored.AccountId == profile.Id &&
                    restored.PersistentOwnerKey == originalKey && ReferenceEquals(restored.Runner, resumed) &&
                    resumed.ProtectionOrderIds.SequenceEqual(protectionIds) && same.Count("/v5/order/cancel") == 0,
                    "changing only the actual chart ticket account does not clear a strategy's owner anchor, adopt its ledger elsewhere or move its original protection");
                var settings = new StrategyChartWindow(trading, restoredChart, restored);
                settings.Show(owner); pump(.1);
                try
                {
                    var selector = Field<ComboBox>(settings, "_account");
                    Check(selector.SelectedItem is ComboBoxItem { Tag: AccountOption selected } && selected.Id == profile.Id,
                        "actual strategy settings keep the native strategy route distinct from the chart ticket's simulated account");
                    selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(item => item.Tag is AccountOption option && option.Id == "sim");
                    Shot(settings, "kvp_providers_workspace_explicit_strategy_account");
                    Click(Button(settings, "Apply"));
                    Until(() => restored.Runner is { IsRunning: true, AccountId: "sim" } && !restored.Busy,
                        "actual strategy account dropdown and Apply explicitly create a separate simulated chart run");
                    var separate = restored.Runner!;
                    Check(separate.Key != originalKey && restored.PersistentOwnerKey == separate.Key && separate.Source == "chart" &&
                        separate.OwnPosition == 0 && separate.Ledger.Fills.Count == 0 &&
                        LiveStore.LoadLedger(originalKey, 1).Position == .25m &&
                        LiveStore.InfoFor(originalKey)?.ProtectionOrderIds?.SequenceEqual(protectionIds) == true &&
                        same.Count("/v5/order/create") == 0 && same.Count("/v5/order/cancel") == 0 &&
                        same.Report(stopId)["orderStatus"]!.GetValue<string>() == "Untriggered" && same.NetPosition == .25m,
                        "only the explicit strategy account choice releases the old anchor for a new empty ledger; old native holdings/protection remain with their original saved owner and are never adopted into simulation");
                    _observations.Add(new { name = "Actual Workspace/UID chart ownership",
                        savedOwnerKey = originalKey, restoredSource = resumed.Source, savedOwnedQuantity = resumed.OwnPosition,
                        originalNativeProtection = protectionIds, nativeCreateCount = original.Count("/v5/order/create") + same.Count("/v5/order/create"),
                        explicitSeparateAccount = separate.AccountId, explicitSeparateOwnedQuantity = separate.OwnPosition,
                        fakeNetworkOnly = true });
                    Shot(settings, "kvp_providers_workspace_explicit_strategy_account_applied");
                }
                finally { settings.Close(); }
            }
            finally
            {
                foreach (var gate in gates) gate.Release.TrySetResult();
                try
                {
                    foreach (var start in starts.Where(start => !start.IsCompleted))
                        Await(start, "owned workspace launch drains after its fake UID response is released");
                    foreach (var chart in trading.AllCharts.Where(chart => !priorCharts.Contains(chart)).ToArray())
                    {
                        foreach (var cs in chart.Strategies.Where(cs => cs.StrategyId == file.Id).ToArray()) trading.RemoveStrategy(chart, cs);
                        trading.CloseChart(chart);
                    }
                    var native = sameSession ?? originalSession;
                    if (native != null)
                    {
                        native.CancelAll("BTCUSDT");
                        Await(native.RefreshAsync(), "owned workspace fixture protection receives its normal cleanup cancellation report");
                        Until(() => native.Orders.All(order => order.State != OrderStates.Active),
                            "workspace fixture native protection drains authoritative cancellation without closing or appropriating holdings");
                    }
                }
                finally
                {
                    Services.Settings.AutoResumeLive = priorAutoResume; Services.RunningAtLastClose = priorAtClose;
                    Services.SaveLiveState();
                    if (priorActive != null && trading.AllCharts.Contains(priorActive)) trading.Activate(priorActive);
                    Workspace.Save(trading);
                    if (File.Exists(file.Path)) File.Delete(file.Path);
                    Services.Strategies.Refresh();
                }
            }
        }
        private void ExerciseAccountContextCutover(KvpProvidersExchange exchange)
        {
            var oldWire = exchange.Add("kvp-private-ui-context-old-key", "kvp-private-ui-context-old-secret", 900031, .7m);
            var newWire = exchange.Add("kvp-private-ui-context-new-key", "kvp-private-ui-context-new-secret", 900032, .4m);
            oldWire.ReportPriorClose(1, 112.6m, .1m, 12.50m);
            newWire.ReportPriorClose(1, 212.6m, .2m, 22.50m);
            foreach (var wire in new[] { oldWire, newWire }) { _secrets.Add(wire.Key); _secrets.Add(wire.Secret); }
            var profile = new ConnectionProfile { Id = "kvp-ui-bybit-context", Kind = ConnectionKind.Bybit, Name = "Bybit credential context — FAKE endpoint" };
            Connections.Profiles.Add(profile); Connections.SetSecret(profile, "apikey", oldWire.Key); Connections.SetSecret(profile, oldWire.Secret); Connections.Save();
            BybitTradingSession? oldSession = null, newSession = null;
            try
            {
                oldSession = Services.BybitSessionFor(profile);
                Await(oldSession.RefreshAsync(), "first credential context supplies actual known account snapshots for UI cutover");
                var oldData = AccountData(profile);
                var oldBroker = (BybitBroker)Services.AccountFor(oldData, profile.Id);
                var oldOrder = oldBroker.Limit(OrderDirections.Buy, .1m, 99, "Old-context UI action fixture");
                Until(() => oldOrder.ExchangeOrderId.Length > 0, "old context's independent working order receives its native identity");
                Await(oldSession.RefreshAsync(), "old working order and actual execution are fully known before account rendering");
                Until(() => oldSession.SnapshotReady && oldSession.ClosedPnlReady, "old context has complete authoritative readiness before caching its account UI");
                var oldEntry = Account(profile);
                var oldSnapshot = Services.AccountCenter.Snapshot(oldEntry);
                var view = SelectAccount(oldEntry); Tab(view, 2);
                Until(() => FillCommissionText(view, "ui-prior-close") == (-.1m).ToString("0.00", TradingHub.Common.UiCulture.Current),
                    "actual old-context Fills row renders its measured0.10 fee");
                Check(Field<ListBox>(view, "_fills").Items.OfType<AccountFill>().Single().Price == 112.6m, "old-context rendered fill has its own actual price112.6");
                Tab(view, 4); Shot("kvp_providers_context_before_statistics");

                // A normal factory creates a different verified session. Its producer-global revision
                // necessarily advances; do not invent an unreachable equal-revision collision.
                var connections = SelectConnection(profile);
                Type(Input<TextBox>(connections, "API key"), newWire.Key); Type(Input<TextBox>(connections, "API secret"), newWire.Secret);
                Click(Button(connections, "Save")); pump(.05);
                newSession = Services.BybitSessionFor(profile);
                Await(newSession.RefreshAsync(), "new credential context verifies its different UID before the Account page is shown again");
                Until(() => newSession.SnapshotReady && newSession.ClosedPnlReady, "new context is fully known before the Account page can observe the cutover");
                var newData = AccountData(profile);
                var newBroker = (BybitBroker)Services.AccountFor(newData, profile.Id);
                var newEntry = Account(profile);
                var newSnapshot = Services.AccountCenter.Snapshot(newEntry);
                Check(oldEntry.Id == newEntry.Id && oldEntry.Name == newEntry.Name && oldEntry.BybitContextIdentity != newEntry.BybitContextIdentity &&
                    oldSnapshot.Fills.Count == 1 && newSnapshot.Fills.Count == 1 && oldSnapshot.FinancialsKnown && newSnapshot.FinancialsKnown &&
                    oldSnapshot.CostsComplete && newSnapshot.CostsComplete && !oldSnapshot.AnalysisKnown && !newSnapshot.AnalysisKnown &&
                    oldSnapshot.PositionsKnown && newSnapshot.PositionsKnown && oldSnapshot.OrdersKnown && newSnapshot.OrdersKnown &&
                    newSnapshot.ExecutionRevision > oldSnapshot.ExecutionRevision && oldSnapshot.Balance == 1000 && newSnapshot.Balance == 1000,
                    "natural same-profile/name UID rotation keeps fill count, known flags and wallet unchanged while its guaranteed global producer revision advances",
                    new { oldRevision = oldSnapshot.ExecutionRevision, newRevision = newSnapshot.ExecutionRevision, oldCount = oldSnapshot.Fills.Count, newCount = newSnapshot.Fills.Count });
                view = (AccountsView)Page("accounts"); // Keep the selected profile; do not reset account selection or UI cache fields.
                Check(Card(view, 0) == 1000m.ToString("#,##0.00", TradingHub.Common.UiCulture.Current) &&
                    Texts(view).Any(text => TextIs(text, "STATISTICS UNAVAILABLE") || text == Lang.T("Statistics unavailable").ToUpperInvariant()) &&
                    Field<ListBox>(view, "_trades").Items.Count == 0,
                    "actual Statistics page preserves known wallet and unavailable opening-basis analysis across native account cutover");
                Shot("kvp_providers_context_after_statistics");
                Tab(view, 2);
                Until(() => FillCommissionText(view, "ui-prior-close") == (-.2m).ToString("0.00", TradingHub.Common.UiCulture.Current) &&
                    Texts(view).Any(text => text == 212.6m.ToString("#,##0.#####", TradingHub.Common.UiCulture.Current)),
                    "same selected profile redraws actual new-UID fill price212.6/fee0.20 instead of retaining prior account rows");
                Check(Services.Journal.FillsOf(AppServices.BybitJournalId(profile.Id, oldSession)).Single().Price == 112.6m &&
                    Services.Journal.FillsOf(AppServices.BybitJournalId(profile.Id, newSession)).Single().Price == 212.6m,
                    "same native execution IDs in two UIDs remain distinct in the real scoped account journals");
                Shot("kvp_providers_context_after_fills");
                Tab(view, 1);
                Check(!Field<StackPanel>(view, "_orders").GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && TextIs(Label(button), "Cancel")) &&
                    Texts(view).Any(text => TextIs(text, "No orders yet")) && oldOrder.State == OrderStates.Active && oldWire.Count("/v5/order/cancel") == 0,
                    "new UID's empty order snapshot removes the old captured Cancel action without canceling the previous account's still-working order");
                Shot("kvp_providers_context_old_actions_removed");

                var newOrder = newBroker.Limit(OrderDirections.Buy, .2m, 199, "New-context UI action fixture");
                Until(() => newOrder.ExchangeOrderId.Length > 0, "current UID creates its own independently tracked working order");
                Await(newSession.RefreshAsync(), "current UID confirms the new working order for its real UI row");
                RefreshUi();
                Click(Button(Field<StackPanel>(view, "_orders"), "Cancel"));
                Until(() => newWire.Count("/v5/order/cancel") == 1, "actual current-account Cancel control signs with the new credential context");
                Await(newSession.RefreshAsync(), "new-context UI cancellation receives an authoritative terminal report");
                Until(() => newOrder.Status == OrderStatus.Canceled, "new owning session confirms the UI-requested cancellation before terminal state is asserted");
                Check(newOrder.Status == OrderStatus.Canceled && oldOrder.State == OrderStates.Active && oldWire.Count("/v5/order/cancel") == 0,
                    "current-account Cancel affects only the new native order, never the old account with an overlapping native order ID");
                Tab(view, 0); RefreshUi();
                Click(Button(Field<StackPanel>(view, "_positions"), "Close position"));
                Until(() => newWire.Count("/v5/order/create") == 2 && newSession.Orders.Any(order => order.Type == OrderTypes.Market && order.ExchangeOrderId.Length > 0),
                    "actual current-account Close control submits through the new session after refreshing its own position");
                var close = newSession.Orders.Single(order => order.Type == OrderTypes.Market);
                var closeReport = newWire.Report(close.ExchangeOrderId);
                Check(closeReport["qty"]!.GetValue<string>() == "0.4" && closeReport["reduceOnly"]!.GetValue<bool>() &&
                    closeReport["side"]!.GetValue<string>() == "Sell" && oldWire.Count("/v5/order/create") == 1 && oldWire.NetPosition == .7m &&
                    newWire.Requests.Where(request => request.Route is "/v5/order/create" or "/v5/order/cancel").All(request => request.Headers["X-BAPI-API-KEY"] == newWire.Key),
                    "rebuilt Close callback uses new owned account snapshot0.4 and new signed keys, not the prior context's0.7 holding or credentials");
                Await(newSession.RefreshAsync(), "new reducing-close acknowledgement is reconciled for account display");
                RefreshUi(); Shot("kvp_providers_context_new_close_action");
                Snapshot("Credential context cutover old account", Services.AccountCenter.Snapshot(Account(profile, oldSession)));
                Snapshot("Credential context cutover current account", Services.AccountCenter.Snapshot(newEntry));
            }
            finally
            {
                foreach (var session in new[] { oldSession, newSession })
                {
                    if (session == null) continue;
                    session.CancelAll("BTCUSDT");
                    Until(() => session.Orders.All(order => order.State != OrderStates.Active), "fixture-created context orders drain through their owning native cancellation acknowledgements");
                }
            }
        }
        private void CheckOrdinaryStores()
        {
            string[] files = new[] { Path.Combine(AppPaths.Root, "connections.json"), Path.Combine(AppPaths.Root, "settings.json"), AppPaths.WorkspaceFile }
                .Concat(Directory.Exists(Path.Combine(AppPaths.Accounts, "Bybit")) ? Directory.EnumerateFiles(Path.Combine(AppPaths.Accounts, "Bybit"), "*.json", SearchOption.AllDirectories) : Array.Empty<string>()).Where(File.Exists).ToArray();
            Check(files.Length > 0 && files.All(file => { string text = File.ReadAllText(file); return _secrets.All(secret => !text.Contains(secret, StringComparison.Ordinal)); }),
                "ordinary settings and provider intent state contain no API keys, signing secrets or OAuth tokens", new { filesInspected = files.Length });
        }
    }
    private sealed class OwnershipProbe : Strategy { protected override void OnBar(Bar bar) { } }
    private sealed class Endpoints : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new();
        public Endpoints(IReadOnlyDictionary<string, string> values)
        { foreach (var pair in values) { _previous.Add(pair.Key, Environment.GetEnvironmentVariable(pair.Key)); Environment.SetEnvironmentVariable(pair.Key, pair.Value); } }
        public void Dispose() { foreach (var pair in _previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }
}
