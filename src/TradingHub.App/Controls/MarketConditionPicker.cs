using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradingHub.Strategies;

namespace TradingHub.App.Controls;

/// <summary>
/// En qué condiciones de mercado abre operaciones una estrategia: rango, mixto, tendencia alcista o bajista (cualquier
/// combinación) y con qué volatilidad (baja, normal o alta respecto del último mes). Es el filtro de contexto de la
/// estrategia: se mide con las velas ya cerradas, igual en vivo que en un backtest, y las salidas siempre pasan.
/// </summary>
internal sealed class MarketConditionPicker : UserControl
{
    private readonly CheckBox _range = Box("Range"), _mixed = Box("Mixed"), _up = Box("Uptrend"), _down = Box("Downtrend");
    private readonly CheckBox _low = Box("Low"), _normal = Box("Normal"), _high = Box("High");
    private readonly TextBlock _summary = new() { FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _now = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private (int Min, int Max)? _custom; // un rango de percentiles que no son las tres franjas (por ejemplo, el de una variante)
    private bool _loading;
    private (MarketRegime Regime, int? Direction, double? Rank)? _context;

    public MarketConditionPicker()
    {
        foreach (var b in new[] { _range, _mixed, _up, _down }) b.IsCheckedChanged += (_, _) => Changed_();
        foreach (var b in new[] { _low, _normal, _high })
            b.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                _custom = null;
                // la volatilidad va de corrido: baja y alta incluyen la normal
                if (_low.IsChecked == true && _high.IsChecked == true && _normal.IsChecked != true)
                {
                    _loading = true;
                    _normal.IsChecked = true;
                    _loading = false;
                }

                Changed_();
            };
        var regimes = new WrapPanel { ItemSpacing = 14, LineSpacing = 6, Children = { _range, _mixed, _up, _down } };
        var vols = new WrapPanel { ItemSpacing = 14, LineSpacing = 6, Children = { _low, _normal, _high } };
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Label("Trade when the market is"), regimes,
                Label("With volatility (relative to the last month)"), vols,
                _summary, _now,
            },
        };
        Load(RegimeFilter.All, 0, 100);
    }

    /// <summary>Cambió lo elegido.</summary>
    public event Action? Changed;

    private static CheckBox Box(string text) => new() { Content = text };

    private static TextBlock Label(string text) => new() { Text = text, Classes = { "faint", "small" } };

    public void Load(RegimeFilter filter, int minRank, int maxRank)
    {
        _loading = true;
        var set = MarketContextTracker.SetOf(filter);
        _range.IsChecked = set.HasFlag(RegimeSet.Range);
        _mixed.IsChecked = set.HasFlag(RegimeSet.Mixed);
        _up.IsChecked = set.HasFlag(RegimeSet.UpTrend);
        _down.IsChecked = set.HasFlag(RegimeSet.DownTrend);
        minRank = Math.Clamp(minRank, 0, 100);
        maxRank = Math.Clamp(maxRank, 0, 100);
        _low.IsChecked = minRank <= MarketContextTracker.LowVolatility;
        _normal.IsChecked = minRank <= MarketContextTracker.HighVolatility - 1 && maxRank >= MarketContextTracker.LowVolatility + 1;
        _high.IsChecked = maxRank >= MarketContextTracker.HighVolatility;
        _custom = Bands() == (minRank, maxRank) ? null : (minRank, maxRank);
        _loading = false;
        Changed_(raise: false);
    }

    /// <summary>Los percentiles de las franjas tildadas (baja 0-33, normal 34-66, alta 67-100).</summary>
    private (int Min, int Max) Bands()
    {
        bool low = _low.IsChecked == true, normal = _normal.IsChecked == true, high = _high.IsChecked == true;
        int min = low ? 0 : normal ? 34 : high ? 67 : 0;
        int max = high ? 100 : normal ? 66 : low ? 33 : 100;
        return (min, max);
    }

    private RegimeSet Set =>
        (_range.IsChecked == true ? RegimeSet.Range : 0) | (_mixed.IsChecked == true ? RegimeSet.Mixed : 0) |
        (_up.IsChecked == true ? RegimeSet.UpTrend : 0) | (_down.IsChecked == true ? RegimeSet.DownTrend : 0);

    /// <summary>Hay al menos una condición y una franja de volatilidad tildadas.</summary>
    public bool IsValid => Set != RegimeSet.None && (_low.IsChecked == true || _normal.IsChecked == true || _high.IsChecked == true);

    /// <summary>Lo elegido como los parámetros del filtro de contexto de la estrategia.</summary>
    public (RegimeFilter Filter, int Min, int Max) Value
    {
        get
        {
            var (min, max) = _custom ?? Bands();
            return (MarketContextTracker.FilterOf(Set), min, max);
        }
    }

    /// <summary>Sin filtro: todas las condiciones y cualquier volatilidad.</summary>
    public bool IsAll => Value is (RegimeFilter.All, 0, 100);

    public bool Editable
    {
        set
        {
            foreach (var b in new[] { _range, _mixed, _up, _down, _low, _normal, _high }) b.IsEnabled = value;
        }
    }

    /// <summary>Cómo está el mercado ahora (lo que ve la estrategia): dice si con lo elegido abriría operaciones.</summary>
    public void SetContext(MarketRegime regime, int? direction, double? rank)
    {
        _context = (regime, direction, rank);
        Changed_(raise: false);
    }

    /// <summary>Lo que se le dice a la estrategia y a vos: «solo en rango, volatilidad baja».</summary>
    public string Describe()
    {
        var (f, min, max) = Value;
        string text = Robustness.DescribeFilter(f, min, max);
        return text.Length == 0 ? "in any market condition" : text;
    }

    private void Changed_(bool raise = true)
    {
        if (_loading) return;
        _summary.Text = IsValid ? $"Trades {Describe()}." : "Select at least one condition and volatility category.";
        _summary.Foreground = IsValid ? Ui.Text0 : Ui.Warn;
        if (_context is { } c && IsValid)
        {
            var (f, min, max) = Value;
            bool regimeOk = c.Regime == MarketRegime.Unknown || MarketContextTracker.RegimeAllows(f, c.Regime, c.Direction);
            bool volOk = c.Rank is not { } r || (r >= min && r <= max);
            string now = c.Regime == MarketRegime.Unknown ? "no market context yet"
                : MarketContextTracker.Label(c.Regime, c.Direction).ToLowerInvariant() + (c.Rank is { } rk ? $", volatility {MarketContextTracker.VolatilityLabel(rk)} (percentile {rk:0})" : "");
            _now.Text = regimeOk && volOk ? $"Currently {now}: entries allowed." : $"Currently {now}: no new entries (existing positions remain managed).";
            _now.Foreground = regimeOk && volOk ? Ui.Up : Ui.Warn;
            _now.IsVisible = true;
        }
        else _now.IsVisible = false;

        if (raise) Changed?.Invoke();
    }
}
