using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Controls;

/// <summary>
/// Selector de instrumentos: muestra el ticker actual y abre un menú con todos los instrumentos
/// de cada conexión, con buscador, favoritos, filtro por moneda y orden por volumen de 24 h.
/// </summary>
public sealed class InstrumentPicker : Button
{
    private readonly AppServices _svc;
    private readonly TextBlock _symbol = new() { FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _exchange = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    // anchos mínimos: si el texto cambiara de ancho con cada tick, la barra se reacomodaría y movería todo
    private readonly TextBlock _price = new() { FontSize = 13, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, FontFamily = Ui.MonoFont, MinWidth = 88, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _change = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 54 };

    // menú
    private readonly TextBox _search = new() { Watermark = "Search (BTC, ETH, NQ…)", MinWidth = 360 };
    private readonly StackPanel _connTabs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly ListBox _list = new() { Height = 430 };
    private readonly TextBlock _hint = new() { Classes = { "faint", "small" } };
    private readonly StackPanel _elsewhere = new() { Spacing = 5, IsVisible = false };
    private readonly Flyout _flyout;

    private static readonly Dictionary<string, IReadOnlyList<Instrument>> InstrumentCache = new();
    private static readonly Dictionary<string, (DateTime At, InstrumentCatalogSnapshot Snapshot)> CatalogStatus = new();
    private static readonly Dictionary<string, (DateTime At, IReadOnlyDictionary<string, TickerStats> Stats)> StatsCache = new();
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, long> CacheVersions = new();
    private static long _cacheEpoch;
    private static long _cacheRevision;
    private static long CacheRevision { get { lock (CacheLock) return _cacheRevision; } }
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _suggestionCancellation;
    private int _loadVersion, _suggestionVersion;
    private bool _open, _loading;
    private string? _loadError;
    private string _profileId = "";
    private string _filterQuote = "";
    private bool _favoritesOnly;
    private List<Row> _rows = new();

    private sealed record Row(Instrument Instrument, TickerStats? Stats, bool Favorite, string ProfileId);

    public InstrumentPicker(AppServices svc)
    {
        _svc = svc;
        Padding = new Thickness(10, 4);
        MinHeight = 34;
        Classes.Add("ghost");
        var chevron = Ui.Icon(MaterialIconKind.ChevronDown, 16, Ui.Text2);
        Content = Ui.H(10, Ui.Icon(MaterialIconKind.ChartBoxOutline, 18, Ui.Accent), Ui.V(0, _symbol, _exchange), _price, _change, chevron);
        _exchange.Foreground = Ui.Text2;
        ToolTip.SetTip(this, $"Choose instrument ({Keys.Label("K")})");

        // al reciclar filas el contenido puede llegar vacío: se dibuja una fila vacía
        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row == null ? new Panel() : BuildRow(row), supportsRecycling: false);
        _list.DoubleTapped += (_, _) => PickSelected(newTab: false);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) PickSelected(e.KeyModifiers.HasFlag(Keys.Command));
        };
        _search.TextChanged += (_, _) => Refill();
        _search.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down:
                    _list.SelectedIndex = Math.Min(_list.ItemCount - 1, _list.SelectedIndex + 1);
                    _list.ScrollIntoView(_list.SelectedIndex);
                    e.Handled = true;
                    break;
                case Key.Up:
                    _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                    _list.ScrollIntoView(_list.SelectedIndex);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    PickSelected(e.KeyModifiers.HasFlag(Keys.Command));
                    e.Handled = true;
                    break;
            }
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("30,150,*,110,80,90"), Margin = new Thickness(12, 0, 22, 0) };
        void H(string text, int col, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var t = new TextBlock { Text = text, Classes = { "h3" }, HorizontalAlignment = align };
            Grid.SetColumn(t, col);
            header.Children.Add(t);
        }

        H("SYMBOL", 1);
        H("DESCRIPTION", 2);
        H("PRICE", 3, HorizontalAlignment.Right);
        H("24 H", 4, HorizontalAlignment.Right);
        H("VOLUME", 5, HorizontalAlignment.Right);

        var panel = new StackPanel
        {
            Width = 720,
            Spacing = 10,
            Margin = new Thickness(14),
            Children =
            {
                _search,
                _connTabs,
                _chips,
                new Border { Height = 1, Background = Ui.Line },
                header,
                _elsewhere,
                _list,
                _hint,
            },
        };
        _flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
        _symbol.Text = "Choose instrument…";
        _flyout.Opened += async (_, _) =>
        {
            _open = true;
            _search.Text = "";
            BuildConnectionTabs();
            await LoadAsync(_profileId);
            if (_open) Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Input);
        };
        _flyout.Closed += (_, _) => CancelPending();
        AttachedToVisualTree += (_, _) => _svc.Connections.Changed += ConnectionsChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            _svc.Connections.Changed -= ConnectionsChanged;
            CancelPending();
            _flyout.Hide();
        };
        Flyout = _flyout;
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Se eligió un instrumento (conexión, instrumento, abrir en pestaña nueva).</summary>
    public event Action<string, Instrument, bool>? Picked;

    public void SetCurrent(string profileId, Instrument? instrument)
    {
        if (_profileId != profileId)
        {
            CancelRequests();
            if (_open) _ = LoadAsync(profileId);
        }
        _profileId = profileId;
        _symbol.Text = instrument?.Symbol ?? "Choose…";
        // el ancho mínimo se fija al cambiar de instrumento (no en cada tick)
        _price.MinWidth = Math.Max(60, 9 * Math.Max(6, (instrument?.FormatPrice(instrument.TickSize * 10_000_000) ?? "").Length));
        _exchange.Text = _svc.Connections.Find(profileId)?.Name ?? instrument?.Exchange ?? "";
    }

    public void SetQuote(decimal last, decimal changePct, Instrument? instrument)
    {
        _price.Text = instrument != null && last > 0 ? instrument.FormatPrice(last) : "";
        if (changePct == 0)
        {
            _change.Text = "";
            return;
        }

        _change.Text = $"{(changePct > 0 ? "+" : "")}{changePct.ToString("0.00", CultureInfo.InvariantCulture)}%";
        _change.Foreground = changePct >= 0 ? Ui.Up : Ui.Down;
    }

    public void Open() => _flyout.ShowAt(this);

    private void ConnectionsChanged()
    {
        ClearCache();
        Dispatcher.UIThread.Post(async () =>
        {
            if (!_open) return;
            BuildConnectionTabs();
            await LoadAsync(_profileId);
        });
    }

    private void CancelSuggestions()
    {
        _suggestionVersion++;
        _suggestionCancellation?.Cancel();
        _suggestionCancellation?.Dispose();
        _suggestionCancellation = null;
        _elsewhere.Children.Clear();
        _elsewhere.IsVisible = false;
    }

    private void CancelRequests()
    {
        _loadVersion++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        CancelSuggestions();
    }

    private void CancelPending()
    {
        _open = false;
        _loading = false;
        CancelRequests();
    }

    private void BuildConnectionTabs()
    {
        _connTabs.Children.Clear();
        var fav = new ToggleButton { Content = Ui.H(5, Ui.Icon(MaterialIconKind.Star, 14), new TextBlock { Text = "Favorites" }), IsChecked = _favoritesOnly }.Classes("seg");
        fav.Click += async (_, _) =>
        {
            _favoritesOnly = fav.IsChecked == true;
            await LoadAsync(_profileId);
        };
        _connTabs.Children.Add(fav);
        foreach (var p in _svc.Connections.Enabled.Where(p => p.HasMarketData))
        {
            var b = new ToggleButton { Content = p.Name, IsChecked = p.Id == _profileId && !_favoritesOnly, Tag = p.Id }.Classes("seg");
            b.Click += async (_, _) =>
            {
                _favoritesOnly = false;
                _filterQuote = "";
                await LoadAsync(p.Id);
            };
            _connTabs.Children.Add(b);
        }
    }

    private async Task LoadAsync(string profileId)
    {
        CancelRequests();
        int version = _loadVersion;
        var cancellation = _loadCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        long revision = CacheRevision;
        if (!_favoritesOnly) _profileId = profileId;
        foreach (var b in _connTabs.Children.OfType<ToggleButton>())
            b.IsChecked = b.Tag == null ? _favoritesOnly : (!_favoritesOnly && Equals(b.Tag, _profileId));
        _loading = true;
        _loadError = null;
        _rows.Clear();
        _list.ItemsSource = null;
        _hint.Text = "Loading instruments…";
        var rows = new List<Row>();
        var errors = new List<string>();
        var profiles = (_favoritesOnly
            ? _svc.Connections.Enabled.Where(p => p.HasMarketData && _svc.Settings.Favorites.Any(f => f.StartsWith(p.Id + ":", StringComparison.Ordinal)))
            : _svc.Connections.Enabled.Where(p => p.HasMarketData && p.Id == _profileId)).ToArray();

        foreach (var p in profiles)
        {
            try
            {
                var instruments = await CachedInstrumentsAsync(_svc, p.Id, token, TimeSpan.FromSeconds(10));
                if (token.IsCancellationRequested || version != _loadVersion || !_open) return;
                InstrumentCatalogSnapshot? catalog;
                lock (CacheLock) catalog = CatalogStatus.GetValueOrDefault(p.Id).Snapshot;
                if (catalog is { IsOffline: true }) errors.Add(catalog.StatusText);
                else if (catalog?.Warning is { Length: > 0 } warning) errors.Add(warning);
                IReadOnlyDictionary<string, TickerStats>? stats = null;
                if (catalog is not { IsOffline: true } && _svc.Connections.GetFeed(p.Id) is ITickerStatsProvider sp)
                {
                    try { stats = await CachedStatsAsync(p.Id, sp, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch { /* Optional statistics must not hide a usable instrument list. */ }
                }
                foreach (var i in instruments)
                {
                    bool favorite = _svc.Settings.Favorites.Contains($"{p.Id}:{i.Symbol}");
                    if (_favoritesOnly && !favorite) continue;
                    rows.Add(new Row(i, stats != null && stats.TryGetValue(i.Symbol, out var st) ? st : null, favorite, p.Id));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) { errors.Add($"Could not load instruments from {p.Name}: {ex.Message}"); }
        }
        if (token.IsCancellationRequested || version != _loadVersion || !_open) return;
        if (revision != CacheRevision)
        {
            rows.Clear();
            errors.Add("Connections changed while loading; reopen the picker to refresh.");
        }
        _loading = false;
        _loadError = errors.Count == 0 ? null : string.Join(" · ", errors);
        _rows = rows.OrderByDescending(r => r.Favorite).ThenByDescending(r => r.Stats?.QuoteVolume ?? 0).ThenBy(r => r.Instrument.Symbol).ToList();
        BuildQuoteChips();
        Refill();
    }

    private void BuildQuoteChips()
    {
        _chips.Children.Clear();
        var quotes = _rows.Select(r => r.Instrument.QuoteAsset).Where(q => !string.IsNullOrEmpty(q))
            .GroupBy(q => q).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key).ToList();
        if (quotes.Count < 2) return;
        foreach (var q in new[] { "" }.Concat(quotes))
        {
            var b = new ToggleButton { Content = q == "" ? "All" : q, IsChecked = q == _filterQuote }.Classes("seg");
            b.Click += (_, _) =>
            {
                _filterQuote = q;
                foreach (var x in _chips.Children.OfType<ToggleButton>()) x.IsChecked = ReferenceEquals(x, b);
                Refill();
            };
            _chips.Children.Add(b);
        }
    }

    private void Refill()
    {
        string q = (_search.Text ?? "").Trim();
        var filtered = _rows.Where(r =>
            (_filterQuote == "" || r.Instrument.QuoteAsset == _filterQuote) &&
            (q.Length == 0 || Matches(r.Instrument, q)))
            .ToList();
        // coincidencias que empiezan con lo buscado, primero
        if (q.Length > 0)
            filtered = filtered.OrderByDescending(r => r.Instrument.Symbol.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenByDescending(r => r.Stats?.QuoteVolume ?? 0).ToList();
        _list.ItemsSource = filtered;
        _list.SelectedIndex = filtered.Count > 0 ? 0 : -1;
        CancelSuggestions();
        _hint.Text = $"{filtered.Count} instruments · Enter opens here · {Keys.Label("Enter")} opens a new tab · ★ marks favorites";
        if (_loadError != null) _hint.Text = _loadError;
        if (_open && !_loading && q.Length > 0 && filtered.Count == 0 && !_favoritesOnly)
        {
            var cancellation = _suggestionCancellation = new CancellationTokenSource();
            _ = FindElsewhereAsync(q, _suggestionVersion, cancellation.Token);
        }
    }

    public static bool Matches(Instrument instrument, string query) =>
        instrument.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        instrument.Description.Contains(query, StringComparison.OrdinalIgnoreCase);

    private async Task FindElsewhereAsync(string query, int version, CancellationToken token)
    {
        try
        {
            // A short debounce prevents every keystroke from starting another source request.
            await Task.Delay(180, token);
            var profiles = _svc.Connections.Enabled.Where(p => p.HasMarketData && p.Id != _profileId).ToArray();
            long revision = CacheRevision;
            var found = new List<(ConnectionProfile Profile, string Symbols)>();
            var unavailable = new List<string>();
            foreach (var profile in profiles)
            {
                try
                {
                    var instruments = await CachedInstrumentsAsync(_svc, profile.Id, token, TimeSpan.FromSeconds(3));
                    var hits = instruments.Where(i => Matches(i, query))
                        .OrderByDescending(i => i.Symbol.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                        .ThenBy(i => i.Symbol.Length).Take(3).Select(i => i.Symbol).ToArray();
                    if (hits.Length > 0) found.Add((profile, string.Join(", ", hits)));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (TimeoutException) { unavailable.Add($"{profile.Name}: timed out"); }
                catch (Exception ex) { unavailable.Add($"{profile.Name}: {ex.Message}"); }
                if (token.IsCancellationRequested || version != _suggestionVersion || !_open) return;
            }
            if (token.IsCancellationRequested || version != _suggestionVersion || !_open) return;
            if (revision != CacheRevision)
            {
                found.Clear();
                unavailable.Add("Connections changed while searching; enter the query again to refresh.");
            }
            _elsewhere.Children.Clear();
            _elsewhere.Children.Add(new TextBlock
            {
                Text = found.Count > 0 ? $"Matches for “{query}” on other connections:" :
                    profiles.Length == 0 ? "No other enabled market-data connections to search." :
                    unavailable.Count == 0 ? $"No matches for “{query}” on other enabled connections." :
                    $"No matches for “{query}” on the connections that could be searched.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ui.Text2,
            });
            foreach (var (profile, symbols) in found)
            {
                var button = new Button { Content = $"{profile.Name} · {symbols}", HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += async (_, _) =>
                {
                    if (!_open || version != _suggestionVersion) return;
                    _favoritesOnly = false;
                    _filterQuote = "";
                    await LoadAsync(profile.Id);
                };
                _elsewhere.Children.Add(button);
            }
            if (unavailable.Count > 0) _elsewhere.Children.Add(new TextBlock
            {
                Text = "Search incomplete · " + string.Join(" · ", unavailable),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Ui.Text2,
            });
            _elsewhere.IsVisible = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private Control BuildRow(Row r)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("30,150,*,110,80,90"), Height = 26 };
        var star = new Button { Content = Ui.Icon(r.Favorite ? MaterialIconKind.Star : MaterialIconKind.StarOutline, 15, r.Favorite ? Ui.Accent : Ui.Text2), Padding = new Thickness(0), Width = 24, Height = 24, MinHeight = 0 }.Classes("icon");
        star.Click += (_, e) =>
        {
            string key = $"{r.ProfileId}:{r.Instrument.Symbol}";
            if (!_svc.Settings.Favorites.Remove(key)) _svc.Settings.Favorites.Add(key);
            _svc.Settings.Save();
            int idx = _rows.IndexOf(r);
            if (idx >= 0) _rows[idx] = r with { Favorite = !r.Favorite };
            Refill();
            e.Handled = true;
        };
        g.Children.Add(star);
        var sym = new TextBlock { Text = r.Instrument.Symbol, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sym, 1);
        g.Children.Add(sym);
        var desc = new TextBlock { Text = r.Instrument.Description, Foreground = Ui.Text2, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(desc, 2);
        g.Children.Add(desc);
        if (r.Stats != null)
        {
            var price = new TextBlock { Text = r.Instrument.FormatPrice(r.Stats.LastPrice), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = Ui.MonoFont, FontSize = 12 };
            Grid.SetColumn(price, 3);
            g.Children.Add(price);
            var ch = new TextBlock
            {
                Text = $"{(r.Stats.ChangePercent > 0 ? "+" : "")}{r.Stats.ChangePercent.ToString("0.00", CultureInfo.InvariantCulture)}%",
                Foreground = r.Stats.ChangePercent >= 0 ? Ui.Up : Ui.Down,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            Grid.SetColumn(ch, 4);
            g.Children.Add(ch);
            var vol = new TextBlock { Text = TradingHub.Indicators.VolumeFormat.Compact(r.Stats.QuoteVolume), Foreground = Ui.Text1, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            Grid.SetColumn(vol, 5);
            g.Children.Add(vol);
        }

        return g;
    }

    private void PickSelected(bool newTab)
    {
        if (_list.SelectedItem is not Row r) return;
        _flyout.Hide();
        Picked?.Invoke(r.ProfileId, r.Instrument, newTab);
    }

    /// <summary>Invalidates cached rows and rejects results from requests started before the invalidation.</summary>
    public static void ClearCache(string? profileId = null)
    {
        lock (CacheLock)
        {
            _cacheRevision++;
            if (profileId == null)
            {
                _cacheEpoch++;
                InstrumentCache.Clear();
                CatalogStatus.Clear();
                StatsCache.Clear();
                CacheVersions.Clear();
            }
            else
            {
                CacheVersions[profileId] = CacheVersions.GetValueOrDefault(profileId) + 1;
                InstrumentCache.Remove(profileId);
                CatalogStatus.Remove(profileId);
                StatsCache.Remove(profileId);
            }
        }
    }

    /// <summary>Instruments for a connection, sharing the picker's invalidation-safe cache.</summary>
    public static Task<IReadOnlyList<Instrument>> InstrumentsAsync(AppServices svc, string profileId) =>
        CachedInstrumentsAsync(svc, profileId, CancellationToken.None, TimeSpan.FromSeconds(10));

    /// <summary>Cancellation follows quick-entry text, tab and window ownership into discovery.</summary>
    public static Task<IReadOnlyList<Instrument>> InstrumentsAsync(AppServices svc, string profileId, CancellationToken token) =>
        CachedInstrumentsAsync(svc, profileId, token, TimeSpan.FromSeconds(10));

    /// <summary>The same provider-labelled discovery status for quick entry and the full picker.</summary>
    public static InstrumentCatalogSnapshot? CatalogSnapshot(string profileId)
    {
        lock (CacheLock) return CatalogStatus.GetValueOrDefault(profileId).Snapshot;
    }

    private static async Task<IReadOnlyList<Instrument>> CachedInstrumentsAsync(AppServices svc, string profileId, CancellationToken token, TimeSpan timeout)
    {
        long epoch, version;
        lock (CacheLock)
        {
            if (InstrumentCache.TryGetValue(profileId, out var cached) && CatalogStatus.TryGetValue(profileId, out var status) &&
                DateTime.UtcNow - status.At < (status.Snapshot.IsOffline ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(15))) return cached;
            epoch = _cacheEpoch;
            version = CacheVersions.GetValueOrDefault(profileId);
        }
        var profile = svc.Connections.Find(profileId) ?? throw new KeyNotFoundException("The selected connection no longer exists.");
        var snapshot = await svc.InstrumentCatalog.LoadAsync(profileId, InstrumentCatalog.IdentityFor(profile),
            svc.Connections.GetFeed(profileId), token, allowNetwork: !svc.Settings.OfflineCatalogOnly, requestTimeout: timeout);
        var list = snapshot.Instruments;
        token.ThrowIfCancellationRequested();
        lock (CacheLock)
        {
            if (epoch != _cacheEpoch || version != CacheVersions.GetValueOrDefault(profileId))
                throw new InvalidOperationException("Connection changed during instrument loading; reopen the picker to refresh.");
            InstrumentCache[profileId] = list;
            CatalogStatus[profileId] = (DateTime.UtcNow, snapshot);
        }
        return list;
    }

    private static async Task<IReadOnlyDictionary<string, TickerStats>> CachedStatsAsync(string profileId, ITickerStatsProvider provider, CancellationToken token)
    {
        long epoch, version;
        lock (CacheLock)
        {
            if (StatsCache.TryGetValue(profileId, out var cached) && (DateTime.UtcNow - cached.At).TotalSeconds <= 30) return cached.Stats;
            epoch = _cacheEpoch;
            version = CacheVersions.GetValueOrDefault(profileId);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(3));
        IReadOnlyDictionary<string, TickerStats> stats;
        try { stats = await provider.GetTickerStatsAsync(bounded.Token).WaitAsync(TimeSpan.FromSeconds(3), token); }
        catch (OperationCanceledException)
        {
            bounded.Cancel();
            throw;
        }
        catch (TimeoutException)
        {
            bounded.Cancel();
            throw;
        }
        token.ThrowIfCancellationRequested();
        lock (CacheLock)
        {
            if (epoch != _cacheEpoch || version != CacheVersions.GetValueOrDefault(profileId))
                throw new InvalidOperationException("Connection changed during statistics loading.");
            StatsCache[profileId] = (DateTime.UtcNow, stats);
        }
        return stats;
    }
}
