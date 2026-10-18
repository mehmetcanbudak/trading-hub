using System.Globalization;
using System.Text.RegularExpressions;
using TradingHub.Data;
using TradingHub.Feeds.Rithmic;

namespace TradingHub.App.Services;

/// <summary>Costos realistas para probar un mercado.</summary>
public sealed record MarketCosts(decimal CommissionPercent, decimal CommissionPerUnit, int SlippageTicks, string? TimeZoneId, double SessionStartHours);

/// <summary>Qué es cada mercado, cuánto cuesta operarlo y dónde se corre en vivo.</summary>
public static class MarketInfo
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>CFD importado → futuro de CME con el mismo subyacente (para correrlo en vivo).</summary>
    private static readonly Dictionary<string, string> CfdFutures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NAS100"] = "NQ", ["US100"] = "NQ", ["USTEC"] = "NQ", ["USTECH"] = "NQ", ["NDX"] = "NQ", ["NASDAQ"] = "NQ", ["USATECHIDXUSD"] = "NQ",
        ["US500"] = "ES", ["SPX500"] = "ES", ["SP500"] = "ES", ["USA500"] = "ES", ["SPX"] = "ES", ["USA500IDXUSD"] = "ES",
        ["US30"] = "YM", ["DJ30"] = "YM", ["WS30"] = "YM", ["USA30IDXUSD"] = "YM",
        ["US2000"] = "RTY", ["RUSSELL2000"] = "RTY",
        ["XAUUSD"] = "GC", ["GOLD"] = "GC",
        ["WTI"] = "CL", ["USOIL"] = "CL", ["XTIUSD"] = "CL", ["LIGHTCMDUSD"] = "CL",
    };

    /// <summary>Futuro grande → su micro (el mismo precio con un décimo del valor).</summary>
    private static readonly Dictionary<string, string> Micros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NQ"] = "MNQ", ["ES"] = "MES", ["YM"] = "MYM", ["RTY"] = "M2K", ["GC"] = "MGC", ["CL"] = "MCL",
        ["SI"] = "SIL", ["HG"] = "MHG", ["6E"] = "M6E", ["6B"] = "M6B", ["6A"] = "M6A", ["BTC"] = "MBT", ["ETH"] = "MET",
    };

    public static (string Feed, string Symbol) Split(string market)
    {
        int i = market.IndexOf(':');
        return i < 0 ? ("", market.Trim().ToUpperInvariant()) : (market[..i].Trim().ToLowerInvariant(), market[(i + 1)..].Trim().ToUpperInvariant());
    }

    public static string Key(string feed, string symbol) => $"{feed}:{symbol}";

    /// <summary>Normaliza lo que escribe un agente ("btcusdt", "Binance-Futures:BTCUSDT") contra una lista de mercados.</summary>
    public static string? Match(string written, IEnumerable<string> markets)
    {
        if (string.IsNullOrWhiteSpace(written)) return null;
        var list = markets.ToList();
        var exact = list.FirstOrDefault(m => m.Equals(written.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var sym = Split(written).Symbol;
        return list.FirstOrDefault(m => Split(m).Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCrypto(string feed) => feed is "binance-futures" or "binance-spot" or "bybit-linear";

    /// <summary>
    /// Futuros de CME (Rithmic o Tradovate: mismos contratos, misma historia continua; también la historia de Databento y
    /// la de solo una conexión, ver <see cref="HistorySources"/>).
    /// </summary>
    public static bool IsFutures(string feed) => HistorySources.Folder(feed) is RithmicFeed.FeedId or TradingHub.Feeds.Tradovate.TradovateFeed.FeedId or HistorySources.Databento;

    /// <summary>CFDs (archivos importados, OANDA o un broker de cTrader): sin operaciones reales, el spread va como deslizamiento.</summary>
    public static bool IsCfd(string feed) => feed == CsvImport.FeedId || feed == TradingHub.Feeds.Oanda.OandaFeed.FeedId || feed.StartsWith(TradingHub.Feeds.CTrader.CTraderFeed.FeedId, StringComparison.Ordinal);

    public static bool IsStocks(string feed) => feed == TradingHub.Feeds.Alpaca.AlpacaFeed.FeedId;

    /// <summary>Raíz de un símbolo de CFD para buscar su futuro (NAS100_USD → NAS100, US100.cash → US100).</summary>
    private static string CfdRoot(string symbol) => symbol.Split('_', '.', '-', ' ')[0].ToUpperInvariant();

    /// <summary>Descripción para los agentes.</summary>
    public static string Describe(string market)
    {
        var (feed, sym) = Split(market);
        if (IsCrypto(feed)) return feed == "binance-spot" ? "Binance spot crypto, 24/7" : feed == "bybit-linear" ? "Bybit perpetual futures, 24/7" : "Binance perpetual futures, 24/7";
        if (IsFutures(feed)) return (TradingHub.Market.FuturesCatalog.Find(sym) is { } p ? $"futures {p.Name} ({p.Exchange}, Globex hours)" : "CME Group futures") + FuturesSource(feed);
        if (feed == CsvImport.FeedId) return "Imported CFD history (1-minute bars converted to synthetic trades: no real traded volume or aggressor side; unsuitable for order flow)";
        if (feed == TradingHub.Feeds.Oanda.OandaFeed.FeedId) return "OANDA CFD/forex (5-second bars converted to synthetic trades: no real traded volume or aggressor side; unsuitable for order flow)";
        if (feed.StartsWith(TradingHub.Feeds.CTrader.CTraderFeed.FeedId, StringComparison.Ordinal)) return "cTrader broker CFD/forex (bid quotes: no real traded volume or aggressor side; unsuitable for order flow)";
        if (IsStocks(feed)) return "US stock/ETF via Alpaca (exchange trades, tick-rule side; New York 09:30–16:00 plus extended hours)";
        return feed;
    }

    private static string FuturesSource(string feed) => feed switch
    {
        RithmicFeed.FeedId => "",
        HistorySources.Databento => " (Databento history)",
        _ when feed.EndsWith(HistorySources.OnlySuffix, StringComparison.Ordinal) => $" (history only from {(HistorySources.Folder(feed) == RithmicFeed.FeedId ? "Rithmic" : "Tradovate")})",
        _ => " via Tradovate",
    };

    /// <summary>
    /// La ficha de un futuro con historia guardada sin depender de que su conexión esté abierta (Databento no da datos en
    /// vivo): tick, valor del punto y bolsa del catálogo de CME.
    /// </summary>
    public static TradingHub.Market.Instrument FuturesInstrument(string feed, string symbol) => new()
    {
        Symbol = symbol,
        FeedId = feed,
        Exchange = TradingHub.Market.FuturesCatalog.Find(symbol)?.Exchange ?? "CME",
        TickSize = RithmicFeed.SpecFor(symbol)?.Tick ?? HistorySources.StoredTickSize(AppPaths.Ticks, feed, symbol) ?? 0.25m,
        PointValue = PointValue(feed, symbol),
        VolumeStep = 1,
    };

    public static decimal PointValue(string feed, string symbol) =>
        IsFutures(feed) ? RithmicFeed.SpecFor(symbol)?.PointValue ?? 1 : 1;

    /// <summary>Paso de volumen del mercado (contratos enteros en futuros, acciones enteras).</summary>
    public static decimal VolumeStep(string feed, string symbol) => feed switch
    {
        _ when IsFutures(feed) || IsStocks(feed) || feed == TradingHub.Feeds.Oanda.OandaFeed.FeedId => 1,
        _ when IsCrypto(feed) => symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) || symbol.StartsWith("ETH", StringComparison.OrdinalIgnoreCase) ? 0.001m : 0.1m,
        _ => 0.01m,
    };

    /// <summary>Tick guardado en los archivos (o el de la especificación del futuro).</summary>
    public static decimal TickSize(string feed, string symbol)
    {
        if (IsFutures(feed) && RithmicFeed.SpecFor(symbol) is { } s) return s.Tick;
        return HistorySources.StoredTickSize(AppPaths.Ticks, feed, symbol) ?? 0.01m;
    }

    /// <summary>Comisión por lado que confirmó el trader para una raíz de futuros (null = la estimada).</summary>
    public static Func<string, decimal?> UserCommission { get; set; } = _ => null;

    /// <summary>Es la raíz de un futuro conocido (NQ, MNQ, ES, CL…).</summary>
    public static bool IsFuturesRoot(string root) => RithmicFeed.SpecFor(root.ToUpperInvariant()) != null;

    /// <summary>Micro de CME (MNQ, MES, MYM, M2K, MCL, MGC…).</summary>
    public static bool IsMicro(string root)
    {
        root = root.ToUpperInvariant();
        return root.Length > 2 && root[0] == 'M' && (root == "M2K" || RithmicFeed.SpecFor(root[1..]) != null);
    }

    /// <summary>Comisión por lado estimada, todo incluido, mientras el trader no confirme la suya: 2,5 en los grandes y 0,75 en los micro.</summary>
    public static decimal EstimatedCommission(string root) => IsMicro(root) ? 0.75m : 2.5m;

    /// <summary>Comisión por lado de un futuro y si es la que confirmó el trader.</summary>
    public static (decimal PerSide, bool Confirmed) Commission(string root) =>
        UserCommission(root.ToUpperInvariant()) is { } v ? (v, true) : (EstimatedCommission(root), false);

    /// <summary>
    /// Raíces de futuros escritas en mayúsculas en un texto («Historia de NQ y MES» → MES, NQ). Solo en mayúsculas: «es» o
    /// «si» en una frase no son el ES ni la plata.
    /// </summary>
    public static List<string> RootsIn(string text) =>
        Regex.Matches(text, @"(?<![A-Za-z0-9])[A-Z0-9]{2,4}(?![A-Za-z0-9])").Select(m => m.Value)
            .Where(t => t.Any(char.IsLetter) && RithmicFeed.SpecFor(t) != null).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>Costos para probar: comisión y deslizamiento realistas, y la sesión de cada mercado.</summary>
    public static MarketCosts Costs(string feed, string symbol)
    {
        if (IsCrypto(feed)) return new MarketCosts(0.04m, 0, 1, null, 0);
        if (IsFutures(feed)) return new MarketCosts(0, Commission(symbol).PerSide, 1, "America/New_York", 18);
        // acciones: sin comisión en Alpaca, un centavo de deslizamiento y la sesión de Nueva York desde el pre-mercado
        if (IsStocks(feed)) return new MarketCosts(0, 0, 1, "America/New_York", 4);
        // CFD: el spread se paga como deslizamiento (alrededor de un punto en índices); los de índices de EE.UU. con la
        // sesión de Nueva York, igual que su futuro (así una estrategia de horario corre igual en la simulada)
        decimal tick = TickSize(feed, symbol);
        int slip = tick > 0 ? (int)Math.Clamp(Math.Ceiling(1m / tick), 1, 200) : 1;
        return FutureForCfd(CfdRoot(symbol)) is "NQ" or "ES" or "YM" or "RTY" ? new MarketCosts(0, 0, slip, "America/New_York", 18) : new MarketCosts(0, 0, slip, null, 0);
    }

    public static string CostsText(string feed, string symbol)
    {
        var c = Costs(feed, symbol);
        var parts = new List<string>
        {
            $"commission_percent {c.CommissionPercent.ToString(Inv)}",
        };
        if (c.CommissionPerUnit > 0)
            parts.Add($"commission_per_unit {c.CommissionPerUnit.ToString(Inv)}" +
                      (!IsFutures(feed) ? "" : Commission(symbol).Confirmed ? " (actual: confirmed by trader)" : " (estimated: not yet confirmed by trader; do not ask)"));
        parts.Add($"slippage_ticks {c.SlippageTicks}");
        parts.Add(c.TimeZoneId != null ? $"timezone {c.TimeZoneId} · session_start_hour {c.SessionStartHours.ToString(Inv)}" : "timezone UTC");
        return string.Join(" · ", parts);
    }

    /// <summary>Futuro de CME para correr en vivo un CFD importado (null si no hay equivalente).</summary>
    public static string? FutureForCfd(string symbol) => CfdFutures.TryGetValue(symbol, out var f) || CfdFutures.TryGetValue(CfdRoot(symbol), out f) ? f : null;

    public static string? MicroOf(string root) => Micros.TryGetValue(root, out var m) ? m : null;

    /// <summary>El CFD más común de un futuro (NQ → NAS100, ES → US500), para conseguir historia gratis.</summary>
    public static string? CfdFor(string futureRoot) => CfdFutures.FirstOrDefault(kv => kv.Value.Equals(futureRoot, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>Subyacente de un mercado: el mini y el micro del mismo futuro dan lo mismo (rithmic:MNQ → rithmic:NQ).</summary>
    public static string Root(string market)
    {
        var (feed, symbol) = Split(market);
        return IsFutures(feed) ? Key(feed, MiniOf(symbol)) : market.ToLowerInvariant();
    }

    public static bool SameRoot(string a, string b) => Root(a).Equals(Root(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Como <see cref="Match"/>, pero si se pidió el micro y en la lista está el mini (o al revés) también lo encuentra:
    /// son el mismo precio (para elegir dónde validar, no para leer lo que escribió un agente).
    /// </summary>
    public static string? MatchUnderlying(string written, IEnumerable<string> markets)
    {
        var list = markets.ToList();
        if (Match(written, list) is { } m) return m;
        var (feed, symbol) = Split(written);
        string mini = MiniOf(symbol);
        return list.FirstOrDefault(x => Split(x) is var (f2, s2) && IsFutures(f2) && (feed.Length == 0 || IsFutures(feed)) && MiniOf(s2).Equals(mini, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>El mini de un micro (MNQ → NQ) o el mismo símbolo.</summary>
    public static string MiniOf(string root) => Micros.FirstOrDefault(kv => kv.Value.Equals(root, StringComparison.OrdinalIgnoreCase)).Key ?? root;

    /// <summary>
    /// Mercados que siguen al mismo subyacente, de entre <paramref name="available"/> (los que tienen historia o se pueden
    /// bajar): el mini y el micro del mismo futuro, los CFD importados de ese índice (NQ ↔ NAS100) y el mismo cripto en
    /// otros exchanges. Sirven para juntar muestra cuando un mercado tiene poca historia.
    /// </summary>
    public static List<string> Equivalents(string market, IEnumerable<string> available)
    {
        var (feed, symbol) = Split(market);
        var list = new List<string>();
        foreach (var other in available.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (other.Equals(market, StringComparison.OrdinalIgnoreCase)) continue;
            var (f2, s2) = Split(other);
            bool same;
            if (IsCrypto(feed)) same = IsCrypto(f2) && s2.Equals(symbol, StringComparison.OrdinalIgnoreCase);
            else
            {
                // futuro raíz de cada lado: el del contrato (sin micro) o el que sigue el CFD
                string? root = IsCfd(feed) ? FutureForCfd(symbol) : MiniOf(symbol);
                string? root2 = IsCfd(f2) ? FutureForCfd(s2) : IsFutures(f2) ? MiniOf(s2) : null;
                same = root != null && root2 != null && root.Equals(root2, StringComparison.OrdinalIgnoreCase);
            }

            if (same) list.Add(other);
        }

        return list;
    }

    /// <summary>Comisión por lado en una cuenta simulada (tasa sobre el nominal y fija por unidad).</summary>
    public static (decimal Rate, decimal PerUnit) PaperCommission(string feed, string symbol) =>
        IsCrypto(feed) ? (0.0004m, 0) : IsFutures(feed) ? (0, Commission(symbol).PerSide) : (0, 0);
}
