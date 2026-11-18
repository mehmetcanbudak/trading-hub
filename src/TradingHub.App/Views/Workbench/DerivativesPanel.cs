using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using SkiaSharp;
using TradingHub.App.Services.Workbench;
using TradingHub.Feeds;
using TradingHub.Feeds.Derivatives;

namespace TradingHub.App.Views.Workbench;

/// <summary>
/// Derivatives dashboard for the selected coin across the public perpetual venues Trading Hub reads (Binance USD-M,
/// Bybit linear, OKX swaps, Bitget USDT-M and Hyperliquid): funding (per period and normalized to 8 hours), open
/// interest and its change, perpetual premium over the index, long/short account ratio and observed liquidations, as a
/// table plus small charts; a cross-coin funding board; price and CVD from the instrument's own trades; and a live
/// liquidation feed. Missing data is a dash or a hatched gap, never a zero; metrics a venue does not publish say so.
/// </summary>
internal sealed class DerivativesPanel : WorkbenchPanel
{
    private static readonly TimeSpan Reload = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BoardReload = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan BoardMinimumInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FlowReload = TimeSpan.FromSeconds(5);
    private const int FeedRows = 50;
    private static readonly string[] Columns =
    {
        "Venue", "Funding", "Funding 8h", "Next funding", "Open interest", "OI (USD)", "OI 1h", "OI 24h", "Premium", "Long/short", "Long liqs 24h", "Short liqs 24h",
    };
    private readonly Grid _table = new() { Name = "DerivativesTable" };
    private readonly DerivativesChartsCanvas _charts = new() { Name = "DerivativesCharts", Height = 400 };
    private readonly TextBlock _status = new() { Name = "DerivativesStatus", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _board = new() { Name = "DerivativesFundingBoard", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _boardStatus = new() { Name = "DerivativesBoardStatus", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _flowNote = new() { Name = "DerivativesFlowNote", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _feed = new() { Name = "DerivativesLiquidationFeed", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _feedCoverage = new() { Name = "DerivativesLiquidationCoverage", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, DerivativesVenueInputs> _inputs = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, FundingSnapshot>?> _boards = new();
    private readonly List<string> _boardErrors = new();
    private IReadOnlyList<(string Coin, string Symbol)> _boardSymbols = Array.Empty<(string, string)>();
    private IDisposable? _watch;
    private string? _watchSymbol;
    private DateTime _loadedAt = DateTime.MinValue, _lastTable, _boardLoadedAt = DateTime.MinValue, _flowAt = DateTime.MinValue;
    private PriceCvdSeries? _flow;
    // Price/CVD re-reads the trade store in the background, one read at a time; the UI thread only snapshots the row range.
    private readonly SingleFlightRefresher<PriceCvdSeries> _flowRefresh = new(work => Task.Run(work), work => Dispatcher.UIThread.Post(work));
    private (long Generation, decimal Minimum, long Minute, string? Symbol) _feedKey;
    private (int Inputs, long Liquidations, long Minute) _tableKey;
    private int _generation, _inputsVersion;
    private bool _loading, _boardLoading, _boardDirty = true;

    public DerivativesPanel(WorkbenchContext context) : base(context)
    {
        var s = context.Settings;
        var range = new Ui.Segmented<int>(new (int, string, string?)[] { (24, "24 hours", null), (72, "3 days", null), (168, "7 days", null) }) { Name = "DerivativesRange" };
        range.Select(s.DerivativesHours is 24 or 72 or 168 ? s.DerivativesHours : 24);
        range.SelectionChanged += h => { s.DerivativesHours = h; context.Save(); _loadedAt = DateTime.MinValue; CancelFlow(); Load(); };
        var reload = Ui.Button("Reload", MaterialIconKind.Refresh, () => { _loadedAt = DateTime.MinValue; Load(); });
        reload.Name = "DerivativesReload";
        for (int c = 0; c < Columns.Length; c++) _table.ColumnDefinitions.Add(new ColumnDefinition(c == 0 ? new GridLength(130) : GridLength.Auto));
        var minimum = Number("DerivativesLiquidationMinimum", s.DerivativesLiquidationMinNotional, 0, 100_000_000, 1_000, "#,0", v =>
        {
            s.DerivativesLiquidationMinNotional = v;
            context.Save();
            RenderFeed(Context.Clock(), force: true);
        });
        var content = Ui.V(10,
            Ui.H(12, range, reload),
            Note("Public USDT-perpetual data from Binance USD-M, Bybit linear, OKX, Bitget USDT-M and Hyperliquid (USD-settled). Funding is shown per settlement and normalized to 8 hours; premium = (mark − index) / index, with Hyperliquid's oracle price as its index; long/short is each venue's account ratio. \"not published\" marks data the venue does not offer; liquidations come from the Binance and Bybit live streams only while observed (Binance publishes a one-per-second sample)."),
            new ScrollViewer { Content = _table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            Heading("Funding across venues (8-hour basis)"),
            Note("Each venue's current funding normalized to 8 hours. Max spread = (highest − lowest) × 3 × 365, from the venues that list the coin. \"not listed\" means the venue has no USDT perpetual for the coin; \"failed\" means its request failed. Refreshed every 3 minutes while this panel is open."),
            new ScrollViewer { Content = _board, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            _boardStatus,
            Heading("Charts"),
            _flowNote,
            _charts,
            Heading("Liquidation feed"),
            Ui.H(8, new TextBlock { Text = "Minimum notional (USD)", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, minimum),
            _feedCoverage,
            Note("OKX, Bitget and Hyperliquid liquidations are not streamed by Trading Hub; their columns say \"not streamed\"."),
            _feed);
        content.Margin = new Thickness(0, 0, 8, 10);
        var root = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = root;
        RenderTable(Array.Empty<DerivativesVenueRow>());
        RenderBoard();
    }

    public override string Key => "derivatives";
    public override string Title => "Derivatives";
    public override MaterialIconKind Icon => MaterialIconKind.ChartTimelineVariant;
    internal IReadOnlyDictionary<string, DerivativesVenueInputs> Inputs => _inputs;
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, FundingSnapshot>?> Boards => _boards;
    internal PriceCvdSeries? Flow => _flow;

    /// <summary>Whether the panel currently holds the liquidation streams (scenes check that hidden panels release them).</summary>
    internal bool WatchingLiquidations => _watch != null;

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    protected override void OnSelectionChanged()
    {
        _generation++;
        _inputs.Clear();
        _inputsVersion++;
        _loading = false;
        _boardLoading = false;
        _loadedAt = DateTime.MinValue;
        CancelFlow();
        _flow = null;
        _feedKey = default;
        _boardDirty = true;
        Release();
        if (IsActive) OnActivated();
        Refresh();
    }

    protected override void OnActivated()
    {
        if (Selection is { IsCryptoUsd: true } sel && Context.Derivatives() is { } service && _watchSymbol != sel.DerivativesSymbol)
        {
            Release();
            try
            {
                _watch = service.WatchLiquidations(sel.DerivativesSymbol);
                _watchSymbol = sel.DerivativesSymbol;
            }
            catch (ObjectDisposedException) { }
        }
        Load();
        LoadBoard();
    }

    protected override void OnDeactivated()
    {
        Release();
        CancelFlow();
    }

    protected override void OnDispose()
    {
        Release();
        _flowRefresh.Dispose();
    }

    /// <summary>Drops the running and waiting price/CVD reads; the next refresh while active reads again.</summary>
    private void CancelFlow()
    {
        _flowRefresh.Cancel();
        _flowAt = DateTime.MinValue;
    }

    private void Release()
    {
        _watch?.Dispose();
        _watch = null;
        _watchSymbol = null;
    }

    public override void Tick(DateTime now)
    {
        if (now - _loadedAt >= Reload) Load();
        LoadBoard();
        if (Selection is { } sel && now - _flowAt >= FlowReload) RequestFlow(sel, now);
        // The table and charts are rebuilt only when an input changed: venue data, a liquidation or stream window, or the minute.
        if (now - _lastTable >= TimeSpan.FromSeconds(2) && TableKey(now) != _tableKey) Refresh();
    }

    private (int Inputs, long Liquidations, long Minute) TableKey(DateTime now) =>
        (_inputsVersion, Selection is { IsCryptoUsd: true } sel && Context.Derivatives() is { } service ? service.LiquidationGeneration(sel.DerivativesSymbol) : 0,
            now.Ticks / TimeSpan.TicksPerMinute);

    private void Load()
    {
        if (_loading || !IsActive || Selection is not { } sel) return;
        _loadedAt = Context.Clock();
        if (!sel.IsCryptoUsd || Context.Derivatives() is not { } service)
        {
            _status.Text = !sel.IsCryptoUsd ? "The derivatives dashboard needs a USD-quoted crypto coin (its USDT perpetual is read on each venue)." : "Derivatives data is unavailable in this session.";
            Refresh();
            return;
        }
        _loading = true;
        int generation = _generation;
        string symbol = sel.DerivativesSymbol;
        var now = _loadedAt;
        var from = now.AddHours(-Math.Clamp(Context.Settings.DerivativesHours, 1, 24 * 30));
        var ct = ActivityToken;
        _status.Text = $"Loading {symbol} from Binance, Bybit, OKX, Bitget and Hyperliquid…";
        _ = Task.Run(async () =>
        {
            // Venues are separate hosts and load side by side; each venue's requests stay sequential.
            var results = await Task.WhenAll(DerivativesData.PerpetualVenues.Select(venue => LoadVenueAsync(service, venue, symbol, from, now, ct))).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _generation) return;
                _loading = false;
                // Hidden meanwhile: the requests were cancelled, so load again once the panel is visible.
                if (ct.IsCancellationRequested)
                {
                    _loadedAt = DateTime.MinValue;
                    return;
                }
                foreach (var r in results) _inputs[r.Venue] = r;
                _inputsVersion++;
                Refresh();
            });
        });
    }

    private static async Task<DerivativesVenueInputs> LoadVenueAsync(DerivativesData service, string venue, string symbol, DateTime from, DateTime now, CancellationToken ct)
    {
        var errors = new List<string>();
        // One complete template per metric: the venue and the venue's message are the only data holes.
        async Task<T?> Try<T>(Func<string, string> failed, Func<Task<T>> load) where T : class
        {
            if (ct.IsCancellationRequested) return null;
            try { return await load().ConfigureAwait(false); }
            catch (ObjectDisposedException) { return null; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
            catch (Exception ex)
            {
                errors.Add(failed(ex.Message));
                return null;
            }
        }
        string name = WorkbenchFormat.Venue(venue);
        var funding = await Try(m => $"{name} funding failed: {m}", () => service.LoadFundingAsync(venue, symbol, from.AddHours(-8), now, ct)).ConfigureAwait(false);
        var oi = await Try(m => $"{name} open interest failed: {m}", () => service.LoadOpenInterestAsync(venue, symbol, from.AddHours(-1), now, ct)).ConfigureAwait(false);
        var stats = await Try(m => $"{name} premium failed: {m}", () => service.LoadMarketStatsAsync(venue, symbol, ct)).ConfigureAwait(false);
        var ls = await Try(m => $"{name} long/short ratio failed: {m}", () => service.LoadLongShortAsync(venue, symbol, from, now, ct)).ConfigureAwait(false);
        return new DerivativesVenueInputs(venue, funding, oi, stats, ls, null, errors);
    }

    /// <summary>Loads every venue's bulk funding for the board coins: every three minutes, or after a minute when the coin list changed.</summary>
    private void LoadBoard()
    {
        if (_boardLoading || !IsActive || Selection is not { IsCryptoUsd: true } sel || Context.Derivatives() is not { } service) return;
        var now = Context.Clock();
        var symbols = DerivativesDashboard.BoardSymbols(DerivativesDashboard.BoardCoins, sel.DerivativesSymbol);
        bool changed = !symbols.SequenceEqual(_boardSymbols);
        if (now - _boardLoadedAt < (changed ? BoardMinimumInterval : BoardReload)) return;
        _boardLoading = true;
        _boardLoadedAt = now;
        int generation = _generation;
        var ct = ActivityToken;
        _ = Task.Run(async () =>
        {
            var wanted = symbols.Select(s => s.Symbol).ToArray();
            var results = await Task.WhenAll(DerivativesData.PerpetualVenues.Select(async venue =>
            {
                try { return (venue, Board: await service.LoadFundingBoardAsync(venue, wanted, ct).ConfigureAwait(false), Error: (string?)null); }
                catch (ObjectDisposedException) { return (venue, Board: (IReadOnlyDictionary<string, FundingSnapshot>?)null, Error: (string?)null); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (venue, Board: null, Error: null); }
                catch (Exception ex) { return (venue, Board: null, Error: $"{WorkbenchFormat.Venue(venue)}: {ex.Message}"); }
            })).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                _boardLoading = false;
                if (generation != _generation) return;
                if (ct.IsCancellationRequested)
                {
                    _boardLoadedAt = DateTime.MinValue;
                    return;
                }
                _boardSymbols = symbols;
                _boards.Clear();
                _boardErrors.Clear();
                foreach (var (venue, board, error) in results)
                {
                    _boards[venue] = board;
                    if (error != null) _boardErrors.Add(error);
                }
                _boardStatus.Text = _boardErrors.Count == 0
                    ? $"Updated {now.ToLocalTime():HH:mm:ss}"
                    : $"Updated {now.ToLocalTime():HH:mm:ss}; failed: {string.Join(" · ", _boardErrors)}";
                _boardDirty = true;
                RenderBoard();
            });
        });
    }

    /// <summary>Rebuilds rows (with the latest observed liquidations), the charts, the price/CVD panes and the liquidation feed.</summary>
    private void Refresh()
    {
        _lastTable = Context.Clock();
        var now = _lastTable;
        _tableKey = TableKey(now);
        if (_boardDirty) RenderBoard();
        if (Selection is not { } sel)
        {
            RenderTable(Array.Empty<DerivativesVenueRow>());
            _charts.Set(Array.Empty<DerivativesChart>());
            _flowNote.Text = "";
            RenderFeed(now, force: false);
            return;
        }
        var hours = Math.Clamp(Context.Settings.DerivativesHours, 1, 24 * 30);
        var from = now.AddHours(-hours);
        var step = TimeSpan.FromHours(hours) / 96;
        var charts = new List<DerivativesChart>();
        AddFlowCharts(sel, charts);
        RenderFeed(now, force: false);
        if (_inputs.Count == 0)
        {
            RenderTable(Array.Empty<DerivativesVenueRow>());
            _charts.Set(charts);
            return;
        }
        var liquidations = sel.IsCryptoUsd ? Context.Derivatives()?.Liquidations(sel.DerivativesSymbol, from < now.AddHours(-24) ? from : now.AddHours(-24), now) : null;
        var rows = new List<DerivativesVenueRow>();
        foreach (var venue in DerivativesData.PerpetualVenues)
        {
            if (!_inputs.TryGetValue(venue, out var input)) continue;
            var coverage = DerivativesData.Coverage(venue);
            input = input with { Liquidations = coverage.LiquidationStream ? liquidations : null };
            var row = DerivativesDashboard.BuildRow(input, now);
            rows.Add(row);
            string name = WorkbenchFormat.Venue(venue);
            double? interval = row.FundingIntervalHours;
            var funding = (input.Funding?.Samples ?? Array.Empty<FundingSample>())
                .Select(f => new SeriesPoint(f.Time, (double)(interval is { } h ? DerivativesDashboard.NormalizeTo8Hours(f.RatePercent, h) : f.RatePercent))).ToArray();
            charts.Add(new($"{name} · funding (8h %)", DerivativesDashboard.Resample(funding, from, now, step, TimeSpan.FromHours(Math.Max(9, (interval ?? 8) + 1))), true, v => v.ToString("0.####", CultureInfo.InvariantCulture) + "%"));
            var oi = (input.OpenInterest?.Samples ?? Array.Empty<OpenInterestSample>()).Select(o => new SeriesPoint(o.Time, (double)o.Quantity)).ToArray();
            charts.Add(new(coverage.OpenInterestHistory ? $"{name} · open interest" : $"{name} · open interest (current only)",
                DerivativesDashboard.Resample(oi, from, now, step, TimeSpan.FromHours(2)), false, v => WorkbenchFormat.Compact(v)));
            if (coverage.LongShortRatio)
            {
                var ls = (input.LongShort?.Samples ?? Array.Empty<LongShortSample>()).Where(x => x.Ratio != null).Select(x => new SeriesPoint(x.Time, (double)x.Ratio!.Value)).ToArray();
                charts.Add(new($"{name} · long/short ratio", DerivativesDashboard.Resample(ls, from, now, step, TimeSpan.FromHours(Math.Max(2, step.TotalHours * 2))), false, v => v.ToString("0.00", CultureInfo.InvariantCulture)));
            }
            if (coverage.LiquidationStream)
            {
                var liqStep = TimeSpan.FromHours(Math.Max(1, hours / 48));
                var longs = liquidations == null ? Array.Empty<double?>() : DerivativesDashboard.LiquidationBuckets(liquidations.Events, liquidations.Windows, venue, from, now, liqStep, true);
                var shorts = liquidations == null ? Array.Empty<double?>() : DerivativesDashboard.LiquidationBuckets(liquidations.Events, liquidations.Windows, venue, from, now, liqStep, false);
                charts.Add(new($"{name} · liquidations (longs − / shorts +)", longs.Zip(shorts, (l, s) => l is { } a && s is { } b ? b - a : (double?)null).ToArray(), true, v => WorkbenchFormat.Compact(v), Warn: true));
            }
        }
        RenderTable(rows);
        _charts.Set(charts);
        var notes = rows.SelectMany(r => r.Notes).Distinct().ToArray();
        if (!_loading) _status.Text = notes.Length == 0 ? $"{sel.DerivativesSymbol} · updated {_loadedAt.ToLocalTime():HH:mm:ss}" : string.Join(" · ", notes);
    }

    /// <summary>Price and CVD panes from the instrument's own trades (re-read in the background by <see cref="RequestFlow"/>).</summary>
    private void AddFlowCharts(WorkbenchSelection sel, List<DerivativesChart> charts)
    {
        var flow = _flow;
        charts.Add(new($"{sel.Instrument.Symbol} · price", flow?.Price ?? Array.Empty<double?>(), false, v => WorkbenchFormat.Price(v)));
        charts.Add(new($"{sel.Instrument.Symbol} · CVD (measured aggressor side)", flow?.Cvd ?? Array.Empty<double?>(), true, v => WorkbenchFormat.Compact(v), Flagged: flow?.Flagged));
    }

    /// <summary>
    /// Snapshots the store's published row count and the range's first row on the UI thread (a binary search), reads those
    /// rows in the background and applies the result only if the selection is unchanged and no correction moved rows meanwhile.
    /// A request while a read runs waits for it (coalesced), so reads never overlap. Called at most every few seconds while visible.
    /// </summary>
    private void RequestFlow(WorkbenchSelection sel, DateTime now)
    {
        _flowAt = now;
        var hours = Math.Clamp(Context.Settings.DerivativesHours, 1, 24 * 30);
        var from = now.AddHours(-hours);
        var step = TimeSpan.FromHours(hours) / 96;
        var store = sel.Data.Trades;
        var view = TradingHub.Data.TradeStoreView.Capture(store);
        int first = store.LowerBound(from);
        int generation = _generation;
        _flowRefresh.Request(ct => DerivativesDashboard.PriceAndCvd(Trades(store, first, view.Count, ct), from, now, step), flow =>
        {
            // A correction or replacement while the rows were read makes them inconsistent: drop and read again next time.
            if (generation != _generation || !view.IsCurrent(store)) return;
            _flow = flow;
            _flowNote.Text = FlowNote(sel, flow);
            Refresh();
        });
    }

    /// <summary>Rows [<paramref name="first"/>, <paramref name="count"/>) published before the snapshot; cancellation is checked every 4096 rows.</summary>
    private static IEnumerable<FlowTrade> Trades(TradingHub.Data.TradeStore store, int first, int count, CancellationToken ct)
    {
        for (int i = first; i < count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            yield return new FlowTrade(store.GetTime(i), store.GetPrice(i), store.GetVolume(i), store.GetSide(i), store.GetProvenance(i));
        }
    }

    private static string FlowNote(WorkbenchSelection sel, PriceCvdSeries flow)
    {
        if (flow.FirstTrade is not { } first) return $"Price and CVD: no trades of {sel.Instrument.Symbol} are recorded in this range; the panes stay hatched.";
        string text = $"Price and CVD from {sel.Display} trades recorded since {first.ToLocalTime():yyyy-MM-dd HH:mm}; hatched steps had no trades. CVD counts measured aggressor sides only and starts at zero at the range start.";
        decimal total = flow.MeasuredVolume + flow.UnmeasuredVolume;
        if (flow.UnmeasuredVolume > 0 && total > 0)
            return string.Join("\n", text, $"{(flow.UnmeasuredVolume / total * 100).ToString("0.#", CultureInfo.InvariantCulture)}% of the volume had an inferred or unknown side and is left out; amber marks show the affected steps.");
        // Separate sentences are separate lines so each translates on its own.
        return flow.FlaggedSteps > 0 ? string.Join("\n", text, "Amber marks show steps built from compact or partial history.") : text;
    }

    private void RenderTable(IReadOnlyList<DerivativesVenueRow> rows)
    {
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < Columns.Length; c++) Cell(Columns[c], 0, c, header: true);
        int r = 1;
        foreach (var row in rows)
        {
            var coverage = DerivativesData.Coverage(row.Venue);
            _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell(WorkbenchFormat.Venue(row.Venue), r, 0, bold: true);
            Cell(row.FundingIntervalHours is { } h ? $"{WorkbenchFormat.Percent(row.FundingPercent, 4)} / {h.ToString("0.##", CultureInfo.InvariantCulture)}h" : WorkbenchFormat.Percent(row.FundingPercent, 4),
                r, 1, color: Sign(row.FundingPercent));
            Cell(WorkbenchFormat.Percent(row.Funding8hPercent, 4), r, 2, color: Sign(row.Funding8hPercent));
            Cell(row.NextFunding is { } next ? next.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : WorkbenchFormat.Missing, r, 3);
            Cell(WorkbenchFormat.Compact(row.OpenInterest), r, 4);
            Cell(WorkbenchFormat.Compact(row.OpenInterestUsd), r, 5);
            if (coverage.OpenInterestHistory)
            {
                Cell(WorkbenchFormat.Percent(row.OpenInterestChange1hPercent), r, 6, color: Sign(row.OpenInterestChange1hPercent));
                Cell(WorkbenchFormat.Percent(row.OpenInterestChange24hPercent), r, 7, color: Sign(row.OpenInterestChange24hPercent));
            }
            else
            {
                Cell("not published", r, 6, color: Ui.Text2);
                Cell("not published", r, 7, color: Ui.Text2);
            }
            Cell(WorkbenchFormat.Percent(row.PremiumPercent, 4), r, 8, color: Sign(row.PremiumPercent));
            if (coverage.LongShortRatio) Cell(row.LongShortRatio is { } ls ? ls.ToString("0.00", CultureInfo.InvariantCulture) : WorkbenchFormat.Missing, r, 9);
            else Cell("not published", r, 9, color: Ui.Text2);
            if (coverage.LiquidationStream)
            {
                bool partial = row.Liquidations24hCoverage == LiquidationCoverage.Partial;
                Cell(LiquidationText(row.LongLiquidations24h, partial), r, 10, color: row.LongLiquidations24h != null ? Ui.Down : Ui.Text2);
                Cell(LiquidationText(row.ShortLiquidations24h, partial), r, 11, color: row.ShortLiquidations24h != null ? Ui.Up : Ui.Text2);
            }
            else
            {
                Cell("not streamed", r, 10, color: Ui.Text2);
                Cell("not streamed", r, 11, color: Ui.Text2);
            }
            r++;
        }
        if (rows.Count == 0)
        {
            _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var empty = new TextBlock { Text = _loading ? "Loading…" : "No derivatives data yet", Classes = { "muted" }, Margin = new Thickness(4, 6) };
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, Columns.Length);
            _table.Children.Add(empty);
        }
    }

    private static string LiquidationText(decimal? value, bool partial) => value is not { } v ? "not observed"
        : partial ? $"{WorkbenchFormat.Compact(v)} (partial)" : WorkbenchFormat.Compact(v);

    private void RenderBoard()
    {
        _boardDirty = false;
        _board.Children.Clear();
        _board.RowDefinitions.Clear();
        _board.ColumnDefinitions.Clear();
        var venues = DerivativesData.PerpetualVenues;
        for (int c = 0; c < venues.Count + 2; c++) _board.ColumnDefinitions.Add(new ColumnDefinition(c == 0 ? new GridLength(90) : GridLength.Auto));
        _board.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        BoardCell(new TextBlock { Text = "Coin", Classes = { "muted" }, FontWeight = FontWeight.SemiBold }, 0, 0, null);
        for (int v = 0; v < venues.Count; v++)
            BoardCell(new TextBlock { Text = WorkbenchFormat.Venue(venues[v]), Classes = { "muted" }, FontWeight = FontWeight.SemiBold }, 0, v + 1, null);
        BoardCell(new TextBlock { Text = "Max spread (annualized)", Classes = { "muted" }, FontWeight = FontWeight.SemiBold }, 0, venues.Count + 1, null);
        if (_boardSymbols.Count == 0)
        {
            _board.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var empty = new TextBlock { Text = _boardLoading ? "Loading…" : "The funding board loads while this panel is open for a USD-quoted crypto coin.", Classes = { "muted" }, Margin = new Thickness(4, 6) };
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, venues.Count + 2);
            _board.Children.Add(empty);
            return;
        }
        int r = 1;
        foreach (var (coin, symbol) in _boardSymbols)
        {
            var row = DerivativesDashboard.BuildBoardRow(coin, symbol, venues, _boards);
            _board.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            BoardCell(new TextBlock { Text = coin, FontWeight = FontWeight.SemiBold }, r, 0, null);
            for (int v = 0; v < row.Cells.Count; v++)
            {
                var cell = row.Cells[v];
                var text = new TextBlock
                {
                    Text = cell.State switch
                    {
                        FundingBoardCellState.Value => WorkbenchFormat.Percent(cell.Rate8hPercent, 4),
                        FundingBoardCellState.NotListed => "not listed",
                        FundingBoardCellState.Failed => "failed",
                        _ => WorkbenchFormat.Missing,
                    },
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                if (cell.State != FundingBoardCellState.Value) text.Foreground = Ui.Text2;
                IBrush? background = null;
                if (cell.Rate8hPercent is { } rate && rate != 0)
                {
                    var c = Ui.Color(rate > 0 ? "Warn" : "Accent");
                    background = new SolidColorBrush(Color.FromArgb((byte)(255 * (0.10 + 0.55 * DerivativesDashboard.Heat(rate))), c.R, c.G, c.B));
                }
                var border = BoardCell(text, r, v + 1, background);
                if (cell.State == FundingBoardCellState.Value && cell.IntervalHours is { } h && cell.RatePercent is { } raw)
                    ToolTip.SetTip(border, $"{WorkbenchFormat.Percent(raw, 5)} per settlement every {h.ToString("0.##", CultureInfo.InvariantCulture)} h");
            }
            BoardCell(new TextBlock
            {
                Text = row.SpreadAnnualizedPercent is { } spread ? spread.ToString("0.0", CultureInfo.InvariantCulture) + "%" : WorkbenchFormat.Missing,
                HorizontalAlignment = HorizontalAlignment.Right,
            }, r, venues.Count + 1, null);
            r++;
        }
    }

    private Border BoardCell(TextBlock text, int row, int column, IBrush? background)
    {
        var border = new Border { Child = text, Padding = new Thickness(8, 3), Margin = new Thickness(1), CornerRadius = new CornerRadius(3), Background = background };
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        _board.Children.Add(border);
        return border;
    }

    /// <summary>Newest observed liquidations at or above the minimum, rebuilt only when new events or windows arrive.</summary>
    private void RenderFeed(DateTime now, bool force)
    {
        var sel = Selection;
        var service = Context.Derivatives();
        string? symbol = sel is { IsCryptoUsd: true } ? sel.DerivativesSymbol : null;
        decimal minimum = Context.Settings.DerivativesLiquidationMinNotional;
        long generation = symbol != null && service != null ? service.LiquidationGeneration(symbol) : 0;
        var key = (generation, minimum, now.Ticks / TimeSpan.TicksPerMinute, symbol);
        if (!force && key == _feedKey) return;
        _feedKey = key;
        _feed.Children.Clear();
        _feed.RowDefinitions.Clear();
        _feed.ColumnDefinitions.Clear();
        if (symbol == null || service == null)
        {
            _feedCoverage.Text = symbol == null ? "Liquidations: available only for USD-quoted crypto perpetuals (Binance and Bybit streams)." : "Liquidations: derivatives data is unavailable in this session.";
            return;
        }
        var snapshot = service.Liquidations(symbol, now.AddHours(-24), now.AddSeconds(5));
        var coverage = DerivativesDashboard.StreamCoverage(snapshot.Windows, DerivativesData.Venues, now, TimeSpan.FromMinutes(2));
        // One line per venue, each translated on its own. The literal comes first: a venue-first "{0}: …" template would be
        // captured by venue-prefixed catalog templates such as "Binance {0}: {1}".
        var lines = coverage.Select(c => Localization.Lang.T(c.Observing ? $"Liquidation stream observed: {WorkbenchFormat.Venue(c.Venue)}" : $"Liquidation stream not connected: {WorkbenchFormat.Venue(c.Venue)}"));
        if (snapshot.Message is { } message) lines = lines.Append(Localization.Lang.T(message));
        _feedCoverage.Text = string.Join("\n", lines);
        var rows = DerivativesDashboard.LiquidationFeed(snapshot.Events, minimum, FeedRows);
        string[] headers = { "Time", "Venue", "Side", "Size", "Price", "Notional" };
        for (int c = 0; c < headers.Length; c++) _feed.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _feed.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < headers.Length; c++) FeedCell(headers[c], 0, c, Ui.Text2);
        if (rows.Count == 0)
        {
            _feed.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var empty = new TextBlock
            {
                Text = snapshot.Windows.Count == 0 ? "No liquidation stream has been observed for this coin yet." : "No liquidations at or above this size were observed in the last 24 hours.",
                Classes = { "muted" }, Margin = new Thickness(4, 6),
            };
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, headers.Length);
            _feed.Children.Add(empty);
            return;
        }
        int r = 1;
        foreach (var e in rows)
        {
            _feed.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var color = e.LongLiquidated ? Ui.Down : Ui.Up;
            FeedCell(e.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), r, 0, null);
            FeedCell(WorkbenchFormat.Venue(e.VenueId), r, 1, null);
            FeedCell(e.LongLiquidated ? "Long liquidated" : "Short liquidated", r, 2, color);
            FeedCell(WorkbenchFormat.Compact(e.Quantity), r, 3, null);
            FeedCell(WorkbenchFormat.Price(e.Price), r, 4, null);
            FeedCell(WorkbenchFormat.Compact(e.Notional), r, 5, color);
            r++;
        }
    }

    private void FeedCell(string text, int row, int column, IBrush? color)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(column == 0 ? 4 : 14, 2, 4, 2), HorizontalAlignment = column >= 3 ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        if (row == 0) t.Classes.Add("muted");
        if (color != null) t.Foreground = color;
        Grid.SetRow(t, row);
        Grid.SetColumn(t, column);
        _feed.Children.Add(t);
    }

    private static IBrush? Sign(decimal? value) => value is > 0 ? Ui.Up : value is < 0 ? Ui.Down : null;

    private void Cell(string text, int row, int column, bool header = false, bool bold = false, IBrush? color = null)
    {
        var t = new TextBlock
        {
            Text = text, Margin = new Thickness(column == 0 ? 4 : 14, 4, 4, 4), HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            FontWeight = header || bold ? FontWeight.SemiBold : FontWeight.Normal,
        };
        if (header) t.Classes.Add("muted");
        if (color != null) t.Foreground = color;
        Grid.SetRow(t, row);
        Grid.SetColumn(t, column);
        _table.Children.Add(t);
    }
}

/// <summary>A small chart; <paramref name="Flagged"/> marks steps whose data has reduced provenance (amber ticks).</summary>
internal sealed record DerivativesChart(string Title, IReadOnlyList<double?> Values, bool ZeroLine, Func<double, string> Format,
    IReadOnlyList<bool>? Flagged = null, bool Warn = false);

/// <summary>Grid of small gap-aware charts, four per row.</summary>
internal sealed class DerivativesChartsCanvas : WorkbenchCanvas
{
    private const int Columns = 4;
    private const double RowHeight = 120;
    private IReadOnlyList<DerivativesChart> _charts = Array.Empty<DerivativesChart>();

    public IReadOnlyList<DerivativesChart> Charts => _charts;

    public void Set(IReadOnlyList<DerivativesChart> charts)
    {
        _charts = charts;
        Height = Math.Max(160, Math.Ceiling(charts.Count / (double)Columns) * RowHeight + 8);
        InvalidateVisual();
    }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        LoadColors();
        canvas.Clear(Bg);
        if (_charts.Count == 0)
        {
            Label(canvas, "Charts appear once the venues answer", 10, 22, Text);
            return;
        }
        int rows = (int)Math.Ceiling(_charts.Count / (double)Columns);
        float cw = (width - 8) / Columns, ch = (height - 8) / Math.Max(1, rows);
        using var tick = new SKPaint { Color = Warn, IsAntialias = false };
        for (int i = 0; i < _charts.Count; i++)
        {
            var c = _charts[i];
            var rect = new SKRect(4 + (i % Columns) * cw + 3, 4 + (i / Columns) * ch + 3, 4 + (i % Columns + 1) * cw - 3, 4 + (i / Columns + 1) * ch - 3);
            Sparkline(canvas, rect, c.Title, c.Values, c.Warn ? Warn : Accent, c.Format, c.ZeroLine);
            if (c.Flagged is not { } flagged || c.Values.Count == 0) continue;
            // Same horizontal mapping as the sparkline's plot area.
            var plot = new SKRect(rect.Left + 6, rect.Top + 22, rect.Right - 6, rect.Bottom - 6);
            for (int k = 0; k < flagged.Count && k < c.Values.Count; k++)
                if (flagged[k])
                {
                    float x = plot.Left + (c.Values.Count <= 1 ? plot.Width : plot.Width * k / (c.Values.Count - 1));
                    canvas.DrawRect(x - 1.5f, plot.Bottom - 3, 3, 3, tick);
                }
        }
    }
}
