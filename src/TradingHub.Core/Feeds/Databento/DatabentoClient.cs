using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Databento;

/// <summary>
/// Historia de futuros de CME (Globex MDP 3.0, desde 2010) por Databento: cada operación con su lado agresor real.
/// Es pago por uso (las cuentas nuevas traen créditos gratis): por eso Trading Hub siempre calcula el costo antes y solo baja
/// cuando el usuario confirma. Usa el contrato continuo por volumen (NQ.v.0), que rola como los gráficos de Trading Hub.
/// </summary>
public sealed class DatabentoClient : IDisposable
{
    public const string Dataset = "GLBX.MDP3";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _base;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public DatabentoClient(string apiKey, string? baseUrl = null)
    {
        _base = (baseUrl ?? "https://hist.databento.com/v0").TrimEnd('/');
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(apiKey.Trim() + ":")));
    }

    /// <summary>Símbolo continuo por volumen (NQ → NQ.v.0).</summary>
    public static string Continuous(string root) => root.Trim().ToUpperInvariant() + ".v.0";

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    private string Query(string root, DateOnly from, DateOnly toExclusive, string schema) =>
        $"dataset={Dataset}&symbols={Uri.EscapeDataString(Continuous(root))}&stype_in=continuous&schema={schema}&start={Day(from)}&end={Day(toExclusive)}";

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException(Explain(resp.StatusCode, body));
        return body;
    }

    private static string Explain(System.Net.HttpStatusCode status, string body)
    {
        string detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            // «detail» viene como texto o como objeto {case, message, …}
            if (doc.RootElement.TryGetProperty("detail", out var d))
                detail = d.ValueKind == JsonValueKind.String ? d.GetString() ?? body
                    : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("message", out var m) ? m.GetString() ?? d.ToString()
                    : d.ToString();
        }
        catch (JsonException)
        {
        }

        return status switch
        {
            System.Net.HttpStatusCode.Unauthorized => "Databento rejected the API key.",
            System.Net.HttpStatusCode.PaymentRequired => "Databento: insufficient credits for this request.",
            _ => $"Databento returned {(int)status}: {detail}",
        };
    }

    /// <summary>Costo en dólares de bajar las operaciones de ese rango (no baja nada).</summary>
    public async Task<decimal> CostAsync(string root, DateOnly from, DateOnly toInclusive, CancellationToken ct)
    {
        var body = await GetStringAsync($"{_base}/metadata.get_cost?{Query(root, from, toInclusive.AddDays(1), "trades")}", ct).ConfigureAwait(false);
        return decimal.Parse(body.Trim().Trim('"'), NumberStyles.Float, Inv);
    }

    /// <summary>Desde y hasta cuándo hay datos en Globex.</summary>
    public async Task<(DateTime Start, DateTime End)> RangeAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetStringAsync($"{_base}/metadata.get_dataset_range?dataset={Dataset}", ct).ConfigureAwait(false));
        DateTime Read(string k) => doc.RootElement.TryGetProperty(k, out var v) && DateTime.TryParse(v.GetString(), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.MinValue;
        return (Read("start"), Read("end"));
    }

    /// <summary>
    /// Las operaciones de un día UTC (CSV): hora del evento, precio, tamaño y lado agresor (B = compró a mercado,
    /// A = vendió a mercado). null si ese día no hubo operaciones. Esto SÍ consume créditos.
    /// </summary>
    public async Task<List<Trade>?> DownloadDayAsync(Instrument instrument, DateOnly day, IProgress<double>? progress, CancellationToken ct)
    {
        var url = $"{_base}/timeseries.get_range?{Query(instrument.Symbol, day, day.AddDays(1), "trades")}&encoding=csv&compression=none&pretty_px=false&pretty_ts=false&map_symbols=false";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // un día sin sesión (feriado) puede venir como error de rango vacío
            if (resp.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity && body.Contains("no data", StringComparison.OrdinalIgnoreCase)) return null;
            throw new HttpRequestException(Explain(resp.StatusCode, body));
        }

        long length = resp.Content.Headers.ContentLength ?? 0;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var header = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (header == null) return null;
        var cols = header.Split(',');
        int iTs = Array.IndexOf(cols, "ts_event"), iPx = Array.IndexOf(cols, "price"), iSz = Array.IndexOf(cols, "size"), iSide = Array.IndexOf(cols, "side");
        if (iTs < 0 || iPx < 0 || iSz < 0) throw new InvalidDataException("Databento returned an unexpected format: " + header);
        var list = new List<Trade>();
        long lastId = 0, read = 0;
        decimal last = 0;
        var lastSide = TradeSide.Buy;
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            read += line.Length + 1;
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (f.Length <= Math.Max(iTs, Math.Max(iPx, iSz))) continue;
            var time = ParseTime(f[iTs]);
            decimal price = ParsePrice(f[iPx]);
            if (price <= 0 || !decimal.TryParse(f[iSz], NumberStyles.Float, Inv, out var size) || size <= 0) continue;
            price = instrument.RoundPrice(price);
            var side = iSide >= 0 ? f[iSide] switch { "B" => TradeSide.Buy, "A" => TradeSide.Sell, _ => TradeSide.None } : TradeSide.None;
            if (side == TradeSide.None) side = HistoryBuilder.TickRule(price, ref last, ref lastSide);
            else
            {
                last = price;
                lastSide = side;
            }

            list.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, price, size, side));
            if (length > 0 && list.Count % 20000 == 0) progress?.Report(Math.Min(1, read / (double)length));
        }

        progress?.Report(1);
        return list.Count > 0 ? list : null;
    }

    /// <summary>Hora en nanosegundos Unix (o ISO 8601 si viene «linda»).</summary>
    internal static DateTime ParseTime(string s)
    {
        if (long.TryParse(s, NumberStyles.Integer, Inv, out var ns)) return DateTime.UnixEpoch.AddTicks(ns / 100);
        return DateTime.Parse(s, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
    }

    /// <summary>Precio en punto fijo (1e-9) o decimal.</summary>
    internal static decimal ParsePrice(string s)
    {
        if (s.Contains('.') || s.Contains('e') || s.Contains('E')) return decimal.TryParse(s, NumberStyles.Float, Inv, out var d) ? d : 0;
        return long.TryParse(s, NumberStyles.Integer, Inv, out var fixedPx) && fixedPx != long.MaxValue ? fixedPx / 1_000_000_000m : 0;
    }

    public void Dispose() => _http.Dispose();
}
