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
    private readonly TextBlock _price = new() { FontSize = 13, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Menlo, SF Mono"), MinWidth = 88, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _change = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 54 };

    // menú
    private readonly TextBox _search = new() { Watermark = "Search (BTC, ETH, NQ…)", MinWidth = 360 };
    private readonly StackPanel _connTabs = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly ListBox _list = new() { Height = 430 };
    private readonly TextBlock _hint = new() { Classes = { "faint", "small" } };
    private readonly Flyout _flyout;

    private static readonly Dictionary<string, IReadOnlyList<Instrument>> InstrumentCache = new();
    private static readonly Dictionary<string, (DateTime At, IReadOnlyDictionary<string, TickerStats> Stats)> StatsCache = new();
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
        ToolTip.SetTip(this, "Choose instrument (⌘K)");

        // al reciclar filas el contenido puede llegar vacío: se dibuja una fila vacía
        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row == null ? new Panel() : BuildRow(row), supportsRecycling: false);
        _list.DoubleTapped += (_, _) => PickSelected(newTab: false);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) PickSelected(e.KeyModifiers.HasFlag(KeyModifiers.Meta));
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
                    PickSelected(e.KeyModifiers.HasFlag(KeyModifiers.Meta));
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
                _list,
                _hint,
            },
        };
        _flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
        _symbol.Text = "Choose instrument…";
        _flyout.Opened += (_, _) =>
        {
            _search.Text = "";
            Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Input);
        };
        Flyout = _flyout;
    }

    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>Se eligió un instrumento (conexión, instrumento, abrir en pestaña nueva).</summary>
    public event Action<string, Instrument, bool>? Picked;

    public void SetCurrent(string profileId, Instrument? instrument)
    {
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

    protected override async void OnClick()
    {
        base.OnClick();
        try
        {
            BuildConnectionTabs();
            await LoadAsync(_profileId);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            _hint.Text = "Could not load the list: " + ex.Message;
        }
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
        if (!_favoritesOnly) _profileId = profileId;
        foreach (var b in _connTabs.Children.OfType<ToggleButton>())
            b.IsChecked = b.Tag == null ? _favoritesOnly : (!_favoritesOnly && Equals(b.Tag, _profileId));
        _hint.Text = "Loading instruments…";
        var rows = new List<Row>();
        var profiles = _favoritesOnly
            ? _svc.Connections.Enabled.Where(p => p.HasMarketData && _svc.Settings.Favorites.Any(f => f.StartsWith(p.Id + ":", StringComparison.Ordinal)))
            : _svc.Connections.Enabled.Where(p => p.HasMarketData && p.Id == _profileId);

        foreach (var p in profiles)
        {
            try
            {
                var feed = _svc.Connections.GetFeed(p.Id);
                if (!InstrumentCache.TryGetValue(p.Id, out var instruments))
                {
                    instruments = await feed.GetInstrumentsAsync(CancellationToken.None);
                    InstrumentCache[p.Id] = instruments;
                }

                IReadOnlyDictionary<string, TickerStats>? stats = null;
                if (feed is ITickerStatsProvider sp)
                {
                    if (!StatsCache.TryGetValue(p.Id, out var cached) || (DateTime.UtcNow - cached.At).TotalSeconds > 30)
                    {
                        try
                        {
                            cached = (DateTime.UtcNow, await sp.GetTickerStatsAsync(CancellationToken.None));
                            StatsCache[p.Id] = cached;
                        }
                        catch
                        {
                            // sin estadísticas: se ordena por nombre
                        }
                    }

                    stats = StatsCache.TryGetValue(p.Id, out var c2) ? c2.Stats : null;
                }

                foreach (var i in instruments)
                {
                    string key = $"{p.Id}:{i.Symbol}";
                    bool fav = _svc.Settings.Favorites.Contains(key);
                    if (_favoritesOnly && !fav) continue;
                    rows.Add(new Row(i, stats != null && stats.TryGetValue(i.Symbol, out var st) ? st : null, fav, p.Id));
                }
            }
            catch (Exception ex)
            {
                _hint.Text = $"Could not load instruments from {p.Name}: {ex.Message}";
            }
        }

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
            (q.Length == 0 || r.Instrument.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Instrument.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        // coincidencias que empiezan con lo buscado, primero
        if (q.Length > 0)
            filtered = filtered.OrderByDescending(r => r.Instrument.Symbol.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenByDescending(r => r.Stats?.QuoteVolume ?? 0).ToList();
        _list.ItemsSource = filtered;
        _list.SelectedIndex = filtered.Count > 0 ? 0 : -1;
        _hint.Text = $"{filtered.Count} instruments · Enter opens here · ⌘Enter opens a new tab · ★ marks favorites";
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
            var price = new TextBlock { Text = r.Instrument.FormatPrice(r.Stats.LastPrice), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Menlo, SF Mono"), FontSize = 12 };
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

    /// <summary>Olvida el caché (por ejemplo, tras cambiar una conexión).</summary>
    public static void ClearCache(string? profileId = null)
    {
        if (profileId == null)
        {
            InstrumentCache.Clear();
            StatsCache.Clear();
        }
        else
        {
            InstrumentCache.Remove(profileId);
            StatsCache.Remove(profileId);
        }
    }

    /// <summary>Instrumentos de una conexión (con caché).</summary>
    public static async Task<IReadOnlyList<Instrument>> InstrumentsAsync(AppServices svc, string profileId)
    {
        if (InstrumentCache.TryGetValue(profileId, out var list)) return list;
        list = await svc.Connections.GetFeed(profileId).GetInstrumentsAsync(CancellationToken.None);
        InstrumentCache[profileId] = list;
        return list;
    }
}
