using System.Text.Json.Serialization;
using TradingHub.Data;

namespace TradingHub.Feeds.Longbridge;

/// <summary>Nonsecret connection configuration. OAuth credentials belong only in the injected secret store.</summary>
public sealed class LongbridgeSettings
{
    public string ClientId { get; set; } = "";
    public string Region { get; set; } = "global";
    public string? BaseUrl { get; set; }
    public string? QuoteWsUrl { get; set; }
    public List<string> Symbols { get; set; } = new();
    public Dictionary<string, LongbridgeInstrumentMetadata> Instruments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public Uri HttpEndpoint => Endpoint(BaseUrl ?? (Region switch
    {
        "global" => "https://openapi.longbridge.com",
        "cn" => "https://openapi.longbridge.cn",
        _ => throw new InvalidOperationException("Unknown Longbridge region; configure an explicit endpoint."),
    }), socket: false);

    [JsonIgnore]
    public Uri QuoteEndpoint => Endpoint(QuoteWsUrl ?? (Region switch
    {
        "global" => "wss://openapi-quote.longbridge.com/v2",
        "cn" => "wss://openapi-quote.longbridge.cn/v2",
        _ => throw new InvalidOperationException("Unknown Longbridge region; configure an explicit endpoint."),
    }), socket: true);

    private static Uri Endpoint(string text, bool socket)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != (socket ? "wss" : "https") && !(uri.IsLoopback && uri.Scheme == (socket ? "ws" : "http"))))
            throw new ArgumentException("Longbridge endpoints must use TLS, except isolated loopback fixtures.");
        return uri;
    }
}

public readonly record struct LongbridgeTradingPeriod(TimeOnly Start, TimeOnly End);

/// <summary>
/// Explicit security metadata, not an estimate from its last price or suffix. TickSize is the configured fixed
/// analysis grid, not a claim that a variable exchange tick table is constant. VolumeStep is shares per board lot;
/// when omitted the provider's static lot_size is required. No trading permission follows from this metadata.
/// </summary>
public sealed class LongbridgeInstrumentMetadata
{
    public decimal TickSize { get; set; }
    public decimal? VolumeStep { get; set; }
    public string Exchange { get; set; } = "";
    public string Currency { get; set; } = "";
    public string TimeZoneId { get; set; } = "";
    public List<LongbridgeTradingPeriod> RegularSessions { get; set; } = new();
    public List<DayOfWeek> TradingWeekdays { get; set; } = new() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
    /// <summary>Exact supplied calendar overrides; an empty list denotes a closed day. Unlisted holidays are unknown.</summary>
    public Dictionary<DateOnly, List<LongbridgeTradingPeriod>> SessionOverrides { get; set; } = new();
    /// <summary>Inclusive bounds of an authoritative supplied holiday/half-day calendar, if available.</summary>
    public DateOnly? CalendarFrom { get; set; }
    public DateOnly? CalendarTo { get; set; }
    public int TradeSession { get; set; }

    internal LongbridgeInstrumentMetadata Copy()
    {
        if (TickSize <= 0 || VolumeStep is <= 0 || string.IsNullOrWhiteSpace(TimeZoneId) || RegularSessions.Count == 0)
            throw new InvalidOperationException("Longbridge requires a positive configured price grid, known lot step, time zone and regular sessions; missing metadata is not inferred.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        if (CalendarFrom.HasValue != CalendarTo.HasValue || CalendarFrom > CalendarTo)
            throw new InvalidOperationException("Longbridge calendar coverage requires an ordered inclusive date range.");
        if (TradeSession is not (0 or 1 or 2 or 3 or 100)) throw new ArgumentOutOfRangeException(nameof(TradeSession));
        static List<LongbridgeTradingPeriod> Periods(IEnumerable<LongbridgeTradingPeriod> values)
        {
            var result = values.OrderBy(p => p.Start).ToList();
            for (int i = 0; i < result.Count; i++)
                if (result[i].Start >= result[i].End || (i > 0 && result[i].Start < result[i - 1].End))
                    throw new InvalidOperationException("Longbridge sessions must be nonoverlapping same-day exchange-local periods.");
            return result;
        }
        return new()
        {
            TickSize = TickSize, VolumeStep = VolumeStep, Exchange = Exchange.Trim(), Currency = Currency.Trim(),
            TimeZoneId = TimeZoneId, RegularSessions = Periods(RegularSessions), TradingWeekdays = TradingWeekdays.Distinct().ToList(),
            SessionOverrides = SessionOverrides.ToDictionary(p => p.Key, p => Periods(p.Value)), CalendarFrom = CalendarFrom,
            CalendarTo = CalendarTo, TradeSession = TradeSession,
        };
    }

    [JsonIgnore]
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    public SessionSettings ToSessionSettings() => new() { TimeZone = TimeZone, Start = RegularSessions[0].Start.ToTimeSpan() };
    public LongbridgeSessionDay GetSessions(DateOnly date)
    {
        var periods = SessionOverrides.TryGetValue(date, out var special) ? special :
            TradingWeekdays.Contains(date.DayOfWeek) ? RegularSessions : new List<LongbridgeTradingPeriod>();
        bool known = SessionOverrides.ContainsKey(date) ||
            (CalendarFrom.HasValue && date >= CalendarFrom.Value && date <= CalendarTo!.Value);
        var zone = TimeZone;
        var windows = periods.Select(p => new LongbridgeSessionWindow(
            TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(p.Start, DateTimeKind.Unspecified), zone),
            TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(p.End, DateTimeKind.Unspecified), zone))).ToArray();
        return new(date, windows, known);
    }
}

public sealed record LongbridgeSessionDay(DateOnly Date, IReadOnlyList<LongbridgeSessionWindow> Windows, bool CalendarConfirmed);
public readonly record struct LongbridgeSessionWindow(DateTime StartUtc, DateTime EndUtc);
public sealed record LongbridgeAuthorization(Uri AuthorizationUri, Uri RedirectUri, DateTimeOffset ExpiresAt, string ClientId);
public readonly record struct LongbridgeSocketCredential(string Otp, long Generation);
