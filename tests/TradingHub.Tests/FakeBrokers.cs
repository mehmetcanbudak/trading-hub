using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Feeds.CTrader;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Servidores de mentira de OANDA, Alpaca, Databento, cTrader y Tradovate (los usan las pruebas y la herramienta de capturas).</summary>
internal static class FakeBrokers
{
    public static readonly DateOnly Day = new(2026, 10, 12); // lunes
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

public static FakeHttpServer Oanda(string token = "TOKEN")
{
    var s = new FakeHttpServer();
    var start = FakeBrokers.Day.ToDateTime(new TimeOnly(13, 0), DateTimeKind.Utc);
    s.OnHttp = req =>
    {
        if (req.Headers.GetValueOrDefault("Authorization") != "Bearer " + token) return (401, "{\"errorMessage\":\"Insufficient authorization to perform request.\"}");
        if (req.Route == "/v3/accounts") return (200, "{\"accounts\":[{\"id\":\"101-001-1\",\"tags\":[]}]}");
        if (req.Route == "/v3/accounts/101-001-1/instruments")
            return (200, "{\"instruments\":[{\"name\":\"EUR_USD\",\"type\":\"CURRENCY\",\"displayName\":\"EUR/USD\",\"displayPrecision\":5,\"tradeUnitsPrecision\":0}," +
                         "{\"name\":\"NAS100_USD\",\"type\":\"CFD\",\"displayName\":\"US Nas 100\",\"displayPrecision\":1,\"tradeUnitsPrecision\":0}]}");
        if (req.Route == "/v3/instruments/NAS100_USD/candles")
        {
            var from = DateTime.Parse(req.Query("from"), Inv, DateTimeStyles.AdjustToUniversal);
            var to = DateTime.Parse(req.Query("to"), Inv, DateTimeStyles.AdjustToUniversal);
            int step = req.Query("granularity") switch { "S5" => 5, "M1" => 60, "M5" => 300, _ => 900 };
            var candles = new List<string>();
            // una hora de datos: de 13:00 a 14:00 UTC
            for (var t = start; t < start.AddHours(1); t = t.AddSeconds(step))
            {
                if (t < from || t >= to) continue;
                double o = 20000 + (t - start).TotalSeconds / 10, c = o + ((t.Second / step) % 2 == 0 ? 2 : -1);
                candles.Add(string.Create(Inv, $"{{\"complete\":true,\"volume\":8,\"time\":\"{t:yyyy-MM-ddTHH:mm:ss}.000000000Z\",\"mid\":{{\"o\":\"{o:0.0}\",\"h\":\"{Math.Max(o, c) + 1:0.0}\",\"l\":\"{Math.Min(o, c) - 1:0.0}\",\"c\":\"{c:0.0}\"}}}}"));
            }

            return (200, $"{{\"instrument\":\"NAS100_USD\",\"granularity\":\"{req.Query("granularity")}\",\"candles\":[{string.Join(",", candles)}]}}");
        }

        return (404, "{}");
    };
    s.IsStream = path => path.Contains("/pricing/stream");
    s.OnStream = async (req, stream, ct) =>
    {
        async Task Line(string text)
        {
            var b = Encoding.UTF8.GetBytes(text + "\n");
            await stream.WriteAsync(b, ct);
            await stream.FlushAsync(ct);
        }

        await Line("{\"type\":\"HEARTBEAT\",\"time\":\"2026-10-14T13:00:00.000000000Z\"}");
        for (int i = 0; i < 5; i++)
        {
            await Line(string.Create(Inv, $"{{\"type\":\"PRICE\",\"instrument\":\"NAS100_USD\",\"time\":\"2026-10-14T13:00:0{i}.000000000Z\",\"bids\":[{{\"price\":\"{20000 + i:0.0}\",\"liquidity\":10}}],\"asks\":[{{\"price\":\"{20001 + i:0.0}\",\"liquidity\":10}}],\"status\":\"tradeable\"}}"));
            await Task.Delay(30, ct);
        }

        await Task.Delay(3000, ct).ContinueWith(_ => { });
    };
    return s;
}


public static FakeHttpServer Alpaca()
{
    var s = new FakeHttpServer();
    s.OnHttp = req =>
    {
        if (req.Headers.GetValueOrDefault("APCA-API-KEY-ID") != "KEY" || req.Headers.GetValueOrDefault("APCA-API-SECRET-KEY") != "SECRET")
            return (403, "{\"message\":\"forbidden\"}");
        if (req.Route == "/v2/assets")
            return (200, "[{\"symbol\":\"AAPL\",\"name\":\"Apple\",\"exchange\":\"NASDAQ\",\"tradable\":true},{\"symbol\":\"QQQ\",\"name\":\"Invesco QQQ\",\"exchange\":\"NASDAQ\",\"tradable\":true}," +
                         "{\"symbol\":\"SPY\",\"name\":\"SPDR S&P 500\",\"exchange\":\"ARCA\",\"tradable\":true},{\"symbol\":\"XYZ\",\"name\":\"no\",\"tradable\":false}]");
        if (req.Route == "/v2/stocks/SPY/trades")
        {
            if (req.Query("feed") != "sip") return (400, "{\"message\":\"feed\"}");
            return req.Query("page_token") == ""
                ? (200, "{\"trades\":[{\"t\":\"2026-10-12T13:30:00.1Z\",\"x\":\"P\",\"p\":580.10,\"s\":100,\"c\":[\"@\"],\"i\":1,\"z\":\"B\"},{\"t\":\"2026-10-12T13:30:00.2Z\",\"x\":\"P\",\"p\":580.11,\"s\":50,\"i\":2,\"z\":\"B\"},{\"t\":\"2026-10-12T13:30:00.3Z\",\"x\":\"Q\",\"p\":580.11,\"s\":20,\"i\":3,\"z\":\"B\"}],\"symbol\":\"SPY\",\"next_page_token\":\"p2\"}")
                : (200, "{\"trades\":[{\"t\":\"2026-10-12T13:30:01Z\",\"x\":\"P\",\"p\":580.05,\"s\":10,\"i\":4,\"z\":\"B\"},{\"t\":\"2026-10-12T13:30:02Z\",\"x\":\"P\",\"p\":580.07,\"s\":5,\"i\":5,\"z\":\"B\"}],\"symbol\":\"SPY\",\"next_page_token\":null}");
        }

        if (req.Route == "/v2/stocks/SPY/bars")
            return (200, "{\"bars\":[{\"t\":\"2026-10-12T13:30:00Z\",\"o\":580,\"h\":581,\"l\":579.5,\"c\":580.5,\"v\":120000,\"n\":900,\"vw\":580.2},{\"t\":\"2026-10-12T13:31:00Z\",\"o\":580.5,\"h\":580.9,\"l\":580.1,\"c\":580.3,\"v\":90000}],\"symbol\":\"SPY\",\"next_page_token\":null}");
        return (404, "{}");
    };
    s.OnSocket = async (path, ws, ct) =>
    {
        await FakeHttpServer.SendText(ws, "[{\"T\":\"success\",\"msg\":\"connected\"}]", ct);
        while (ws.State == WebSocketState.Open)
        {
            var text = await FakeHttpServer.ReceiveText(ws, ct);
            if (text == null) return;
            using var doc = JsonDocument.Parse(text);
            string action = doc.RootElement.GetProperty("action").GetString()!;
            if (action == "auth")
            {
                bool ok = doc.RootElement.GetProperty("key").GetString() == "KEY";
                await FakeHttpServer.SendText(ws, ok ? "[{\"T\":\"success\",\"msg\":\"authenticated\"}]" : "[{\"T\":\"error\",\"code\":402,\"msg\":\"auth failed\"}]", ct);
            }
            else if (action == "subscribe")
            {
                await FakeHttpServer.SendText(ws, "[{\"T\":\"subscription\",\"trades\":[\"SPY\"],\"quotes\":[\"SPY\"]}]", ct);
                await FakeHttpServer.SendText(ws, "[{\"T\":\"q\",\"S\":\"SPY\",\"bx\":\"V\",\"bp\":580.1,\"bs\":3,\"ax\":\"V\",\"ap\":580.12,\"as\":2,\"t\":\"2026-10-14T14:00:00Z\"}," +
                                                  "{\"T\":\"t\",\"S\":\"SPY\",\"i\":9,\"x\":\"V\",\"p\":580.12,\"s\":100,\"t\":\"2026-10-14T14:00:00.5Z\",\"z\":\"B\"}]", ct);
                await FakeHttpServer.SendText(ws, "[{\"T\":\"t\",\"S\":\"SPY\",\"i\":10,\"x\":\"V\",\"p\":580.10,\"s\":40,\"t\":\"2026-10-14T14:00:01Z\",\"z\":\"B\"}]", ct);
            }
        }
    };
    return s;
}


public static FakeHttpServer Databento()
{
    var s = new FakeHttpServer();
    string auth = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("db-KEY:"));
    s.OnHttp = req =>
    {
        if (req.Headers.GetValueOrDefault("Authorization") != auth) return (401, "{\"detail\":\"Authentication failed.\"}");
        if (req.Route == "/v0/metadata.get_cost")
        {
            if (!req.Query("symbols").EndsWith(".v.0", StringComparison.Ordinal)) return (422, "{\"detail\":\"symbols\"}");
            if (req.Query("stype_in") != "continuous") return (422, "{\"detail\":\"stype\"}");
            if (req.Query("schema") != "trades") return (422, "{\"detail\":\"schema\"}");
            return (200, "1.2575");
        }

        if (req.Route == "/v0/metadata.get_dataset_range") return (200, "{\"start\":\"2010-06-06T00:00:00.000000000Z\",\"end\":\"2026-10-13T00:00:00.000000000Z\"}");
        if (req.Route == "/v0/timeseries.get_range")
        {
            if (req.Query("encoding") != "csv") return (422, "{\"detail\":\"encoding\"}");
            long baseNs = new DateTimeOffset(FakeBrokers.Day.ToDateTime(new TimeOnly(14, 30), DateTimeKind.Utc)).ToUnixTimeMilliseconds() * 1_000_000;
            var sb = new StringBuilder("ts_recv,ts_event,rtype,publisher_id,instrument_id,action,side,depth,price,size,flags,ts_in_delta,sequence\n");
            // compra agresiva, venta agresiva, sin lado (regla del tick: bajó → venta) y una con precio raro que se redondea al tick
            sb.Append($"{baseNs + 10},{baseNs},0,1,42,T,B,0,20000250000000,2,0,0,1\n");
            sb.Append($"{baseNs + 1_000_010},{baseNs + 1_000_000},0,1,42,T,A,0,20000000000000,1,0,0,2\n");
            sb.Append($"{baseNs + 2_000_010},{baseNs + 2_000_000},0,1,42,T,N,0,19999750000000,3,0,0,3\n");
            sb.Append($"{baseNs + 3_000_010},{baseNs + 3_000_000},0,1,42,T,B,0,20000260000000,1,0,0,4\n");
            return (200, sb.ToString());
        }

        return (404, "{}");
    };
    return s;
}

}

/// <summary>Open API de mentira: autenticación, cuentas, símbolos, precios en vivo, ticks con páginas y velas.</summary>
internal sealed class FakeCTrader : IDisposable
{
    public readonly FakeHttpServer Server = new();
    public readonly List<int> Received = new();
    public int TickPage = 40;
    public int TickRequests;
    public long DayStartMs = new DateTimeOffset(FakeBrokers.Day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>100 ticks del día: uno por segundo desde las 10:00, precio 20000.00 + i*0.01 (en 1/100000).</summary>
    public (long Ms, long Price) Tick(int i) => (DayStartMs + i * 1000, 2_000_000_000 + i * 1000);

    public FakeCTrader()
    {
        Server.OnSocket = async (path, ws, ct) =>
        {
            while (ws.State == WebSocketState.Open)
            {
                var text = await FakeHttpServer.ReceiveText(ws, ct);
                if (text == null) return;
                var msg = JsonNode.Parse(text)!.AsObject();
                int type = msg["payloadType"]!.GetValue<int>();
                string id = msg["clientMsgId"]?.GetValue<string>() ?? "";
                var p = msg["payload"] as JsonObject ?? new JsonObject();
                lock (Received) Received.Add(type);
                async Task Reply(int t, JsonObject payload) =>
                    await FakeHttpServer.SendText(ws, new JsonObject { ["clientMsgId"] = id, ["payloadType"] = t, ["payload"] = payload }.ToJsonString(), ct);
                switch (type)
                {
                    case 51:
                        break;
                    case 2100:
                        if (p["clientSecret"]?.GetValue<string>() != "SECRET") await Reply(2142, new JsonObject { ["errorCode"] = "CH_CLIENT_AUTH_FAILURE", ["description"] = "clientId or clientSecret is incorrect" });
                        else await Reply(2101, new JsonObject());
                        break;
                    case 2149:
                        await Reply(2150, new JsonObject
                        {
                            ["accessToken"] = p["accessToken"]!.GetValue<string>(),
                            ["ctidTraderAccount"] = new JsonArray(
                                new JsonObject { ["ctidTraderAccountId"] = 77, ["isLive"] = true, ["traderLogin"] = 1001, ["brokerTitleShort"] = "FakeBroker" },
                                new JsonObject { ["ctidTraderAccountId"] = 99, ["isLive"] = false, ["traderLogin"] = 5012345, ["brokerTitleShort"] = "FakeBroker" }),
                        });
                        break;
                    case 2102:
                        await Reply(2103, new JsonObject { ["ctidTraderAccountId"] = p["ctidTraderAccountId"]!.GetValue<long>() });
                        break;
                    case 2114:
                        await Reply(2115, new JsonObject
                        {
                            ["ctidTraderAccountId"] = 99,
                            ["symbol"] = new JsonArray(
                                new JsonObject { ["symbolId"] = 1, ["symbolName"] = "US100", ["enabled"] = true, ["description"] = "US Tech 100 Index" },
                                new JsonObject { ["symbolId"] = 2, ["symbolName"] = "EURUSD", ["enabled"] = true, ["description"] = "Euro vs US Dollar" }),
                        });
                        break;
                    case 2116:
                        await Reply(2117, new JsonObject
                        {
                            ["ctidTraderAccountId"] = 99,
                            ["symbol"] = new JsonArray(
                                new JsonObject { ["symbolId"] = 1, ["digits"] = 2, ["pipPosition"] = 1, ["lotSize"] = 100, ["stepVolume"] = 100, ["minVolume"] = 100 },
                                new JsonObject { ["symbolId"] = 2, ["digits"] = 5, ["pipPosition"] = 4, ["lotSize"] = 10000000, ["stepVolume"] = 100000, ["minVolume"] = 100000 }),
                        });
                        break;
                    case 2127:
                        await Reply(2128, new JsonObject { ["ctidTraderAccountId"] = 99 });
                        for (int i = 0; i < 3; i++)
                        {
                            await Task.Delay(40, ct);
                            await FakeHttpServer.SendText(ws, new JsonObject
                            {
                                ["payloadType"] = 2131,
                                ["payload"] = new JsonObject { ["ctidTraderAccountId"] = 99, ["symbolId"] = 1, ["bid"] = 2_000_100_000 + i * 1000, ["ask"] = 2_000_200_000 + i * 1000, ["timestamp"] = DayStartMs + i },
                            }.ToJsonString(), ct);
                        }

                        break;
                    case 2129:
                        await Reply(2130, new JsonObject { ["ctidTraderAccountId"] = 99 });
                        break;
                    case 2145:
                    {
                        Interlocked.Increment(ref TickRequests);
                        long from = p["fromTimestamp"]!.GetValue<long>(), to = p["toTimestamp"]!.GetValue<long>();
                        // de la más nueva a la más vieja, la primera absoluta y las demás como diferencia
                        var inRange = Enumerable.Range(0, 100).Select(Tick).Where(t => t.Ms >= from && t.Ms <= to).OrderByDescending(t => t.Ms).ToList();
                        var page = inRange.Take(TickPage).ToList();
                        var arr = new JsonArray();
                        for (int i = 0; i < page.Count; i++)
                            arr.Add(i == 0
                                ? new JsonObject { ["timestamp"] = page[0].Ms, ["tick"] = page[0].Price }
                                : new JsonObject { ["timestamp"] = page[i].Ms - page[i - 1].Ms, ["tick"] = page[i].Price - page[i - 1].Price });
                        await Reply(2146, new JsonObject { ["ctidTraderAccountId"] = 99, ["tickData"] = arr, ["hasMore"] = inRange.Count > TickPage });
                        break;
                    }

                    case 2137:
                    {
                        var bars = new JsonArray();
                        long baseMin = DayStartMs / 60000;
                        for (int i = 0; i < 3; i++)
                            bars.Add(new JsonObject { ["volume"] = 50 + i, ["period"] = 1, ["low"] = 2_000_000_000 + i * 100000, ["deltaOpen"] = 20000, ["deltaHigh"] = 90000, ["deltaClose"] = 50000, ["utcTimestampInMinutes"] = baseMin + i });
                        await Reply(2138, new JsonObject { ["ctidTraderAccountId"] = 99, ["period"] = 1, ["trendbar"] = bars, ["symbolId"] = 1 });
                        break;
                    }
                }
            }
        };
    }

    public CTraderSettings Settings(string secret = "SECRET") => new() { ClientId = "ID", ClientSecret = secret, AccessToken = "TOKEN", Url = Server.Ws + "/" };

    public void Dispose() => Server.Dispose();
}


/// <summary>Tradovate de mentira: login con CID/secret, cuentas, contratos, órdenes con ejecuciones y los dos sockets.</summary>
internal sealed class FakeTradovate : IDisposable
{
    public readonly FakeHttpServer Server = new();
    public readonly List<JsonObject> Orders = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> MdRequests = new();
    public string? MdToken, UserToken;
    public int Logins, ChartRequests;
    public long BaseMs = new DateTimeOffset(FakeBrokers.Day.ToDateTime(new TimeOnly(14, 30), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    public readonly string Contract = FuturesRolls.On("ES", DateTime.UtcNow).Symbol;
    private WebSocket? _user;
    private long _nextOrder = 1000, _nextFill = 5000;
    private int _position;

    public FakeTradovate()
    {
        Server.OnHttp = req =>
        {
            if (req.Route == "/v1/auth/accesstokenrequest")
            {
                var b = JsonNode.Parse(req.Body)!.AsObject();
                if (b["password"]?.GetValue<string>() != "pw" || b["sec"]?.GetValue<string>() != "SEC" || b["cid"]?.GetValue<string>() != "123")
                    return (200, "{\"errorText\":\"Incorrect username or password. Please try again.\"}");
                Logins++;
                return (200, $"{{\"accessToken\":\"AT{Logins}\",\"mdAccessToken\":\"MD{Logins}\",\"expirationTime\":\"{DateTime.UtcNow.AddMinutes(80):O}\",\"userStatus\":\"Active\",\"userId\":7,\"name\":\"demo\",\"hasLive\":false}}");
            }

            if (!req.Headers.GetValueOrDefault("Authorization", "").StartsWith("Bearer AT", StringComparison.Ordinal)) return (401, "{}");
            if (req.Route == "/v1/account/list") return (200, "[{\"id\":101,\"name\":\"DEMO101\",\"userId\":7,\"active\":true}]");
            if (req.Route == "/v1/contract/find") return (200, $"{{\"id\":555,\"name\":\"{req.Query("name")}\",\"contractMaturityId\":1}}");
            if (req.Route == "/v1/cashBalance/getcashbalancesnapshot") return (200, "{\"totalCashValue\":50000,\"netLiq\":50120,\"openPnL\":120,\"realizedPnL\":25,\"weekRealizedPnL\":25,\"initialMargin\":1200}");
            if (req.Route == "/v1/order/placeorder")
            {
                var o = JsonNode.Parse(req.Body)!.AsObject();
                long id = Interlocked.Increment(ref _nextOrder);
                lock (Orders) Orders.Add(o);
                _ = Task.Run(() => OrderLifecycle(id, o));
                return (200, $"{{\"failureReason\":\"Success\",\"failureText\":\"\",\"orderId\":{id}}}");
            }

            if (req.Route == "/v1/order/cancelorder")
            {
                long id = JsonNode.Parse(req.Body)!["orderId"]!.GetValue<long>();
                _ = Push("order", "Updated", $"{{\"id\":{id},\"accountId\":101,\"contractId\":555,\"action\":\"Buy\",\"ordStatus\":\"Canceled\",\"timestamp\":\"{DateTime.UtcNow:O}\"}}");
                return (200, $"{{\"orderId\":{id}}}");
            }

            if (req.Route == "/v1/order/modifyorder")
            {
                var b = JsonNode.Parse(req.Body)!.AsObject();
                lock (Orders) Orders.Add(b);
                _ = Push("orderVersion", "Created", $"{{\"id\":9,\"orderId\":{b["orderId"]},\"orderQty\":{b["orderQty"]},\"orderType\":\"{b["orderType"]}\",\"price\":{b["price"]?.ToJsonString() ?? "0"}}}");
                return (200, $"{{\"orderId\":{b["orderId"]}}}");
            }

            return (404, "{}");
        };
        Server.OnSocket = async (path, ws, ct) =>
        {
            bool md = path.Contains("md", StringComparison.Ordinal);
            await FakeHttpServer.SendText(ws, "o", ct);
            while (ws.State == WebSocketState.Open)
            {
                var text = await FakeHttpServer.ReceiveText(ws, ct);
                if (text == null) return;
                if (text == "[]") continue;
                var parts = text.Split('\n');
                string endpoint = parts[0];
                int id = int.Parse(parts[1], CultureInfo.InvariantCulture);
                string body = parts.Length > 3 ? string.Join("\n", parts.Skip(3)) : "";
                if (endpoint == "authorize")
                {
                    if (md) MdToken = body;
                    else
                    {
                        UserToken = body;
                        _user = ws;
                    }

                    await FakeHttpServer.SendText(ws, "a[{\"s\":200,\"i\":0}]", ct);
                    continue;
                }

                if (md) MdRequests.Enqueue(endpoint);
                switch (endpoint)
                {
                    case "user/syncrequest":
                        await FakeHttpServer.SendText(ws, $"a[{{\"s\":200,\"i\":{id},\"d\":{{\"accounts\":[{{\"id\":101,\"name\":\"DEMO101\",\"active\":true}}],\"contracts\":[{{\"id\":555,\"name\":\"{Contract}\"}}]," +
                                                          "\"positions\":[],\"orders\":[],\"orderVersions\":[],\"fills\":[],\"cashBalances\":[{\"accountId\":101,\"amount\":50000,\"realizedPnL\":0}]}}]", ct);
                        break;
                    case "md/getChart":
                    {
                        int seq = Interlocked.Increment(ref ChartRequests);
                        long hid = 100 + seq * 2, rid = hid + 1; // cada gráfico con sus propios ids
                        var req = JsonNode.Parse(body)!.AsObject();
                        var range = req["timeRange"]!.AsObject();
                        bool live = range["closestTimestamp"] == null;
                        // la respuesta y los primeros paquetes van pegados (como el servidor real)
                        await FakeHttpServer.SendText(ws, $"a[{{\"s\":200,\"i\":{id},\"d\":{{\"historicalId\":{hid},\"realtimeId\":{rid}}}}}]", ct);
                        if (live)
                        {
                            // un tick de historia, fin de historia y después ticks en vivo
                            await FakeHttpServer.SendText(ws, Packet(hid, BaseMs, new[] { (0, 0, 1, -1, 0, 1L) }), ct);
                            await FakeHttpServer.SendText(ws, $"a[{{\"e\":\"chart\",\"d\":{{\"charts\":[{{\"id\":{hid},\"eoh\":true}}]}}}}]", ct);
                            await Task.Delay(50, ct);
                            await FakeHttpServer.SendText(ws, Packet(rid, BaseMs + 60000, new[] { (0, 1, 2, 0, 1, 50L), (10, 0, 3, 0, 1, 51L) }), ct);
                        }
                        else
                        {
                            // historia: 12 ticks por segundo de las 14:30 hacia atrás, de a 5 por pedido (el cliente pagina)
                            var closest = DateTime.Parse(range["closestTimestamp"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                            long closestMs = new DateTimeOffset(closest).ToUnixTimeMilliseconds();
                            var ticks = Enumerable.Range(0, 12).Select(i => (Ms: BaseMs + i * 1000, Id: 100L + i)).Where(t => t.Ms <= closestMs).OrderByDescending(t => t.Ms).Take(5).ToList();
                            if (ticks.Count > 0)
                            {
                                long bt = ticks.Min(t => t.Ms);
                                // precio que sube un tick cada segundo; compra si es par (al ask), venta si es impar (al bid)
                                var tks = ticks.Select(t => ((int)(t.Ms - bt), (int)(t.Id - 100), 1, (int)(t.Id - 100) - ((t.Id % 2 == 0) ? 1 : 0), (int)(t.Id - 100) + ((t.Id % 2 == 0) ? 0 : 1), t.Id)).ToArray();
                                await FakeHttpServer.SendText(ws, Packet(hid, bt, tks), ct);
                            }

                            await FakeHttpServer.SendText(ws, $"a[{{\"e\":\"chart\",\"d\":{{\"charts\":[{{\"id\":{hid},\"eoh\":true}}]}}}}]", ct);
                        }

                        break;
                    }

                    case "md/subscribeDOM":
                        await FakeHttpServer.SendText(ws, $"a[{{\"s\":200,\"i\":{id}}}]", ct);
                        await FakeHttpServer.SendText(ws, "a[{\"e\":\"md\",\"d\":{\"doms\":[{\"contractId\":555,\"timestamp\":\"2026-10-14T14:00:00Z\",\"bids\":[{\"price\":5000,\"size\":12},{\"price\":4999.75,\"size\":30}],\"offers\":[{\"price\":5000.25,\"size\":9}]}]}}]", ct);
                        break;
                    default:
                        await FakeHttpServer.SendText(ws, $"a[{{\"s\":200,\"i\":{id}}}]", ct);
                        break;
                }
            }
        };
    }

    /// <summary>Paquete de ticks: tiempo y precios relativos a la base (precio base 20000 ticks de 0.25 = 5000).</summary>
    private static string Packet(long id, long bt, IEnumerable<(int T, int P, int S, int B, int A, long Id)> ticks)
    {
        var tks = string.Join(",", ticks.Select(t => $"{{\"t\":{t.T},\"p\":{t.P},\"s\":{t.S},\"b\":{t.B},\"a\":{t.A},\"bs\":4,\"as\":5,\"id\":{t.Id}}}"));
        return $"a[{{\"e\":\"chart\",\"d\":{{\"charts\":[{{\"id\":{id},\"td\":20261014,\"bp\":20000,\"bt\":{bt},\"ts\":0.25,\"tks\":[{tks}]}}]}}}}]";
    }

    private async Task Push(string entityType, string eventType, string entity)
    {
        for (int i = 0; i < 50 && _user == null; i++) await Task.Delay(20);
        if (_user == null) return;
        await FakeHttpServer.SendText(_user, $"a[{{\"e\":\"props\",\"d\":{{\"entityType\":\"{entityType}\",\"eventType\":\"{eventType}\",\"entity\":{entity}}}}}]", CancellationToken.None);
    }

    /// <summary>Orden creada; las de mercado se ejecutan (ejecución, posición y orden completa).</summary>
    private async Task OrderLifecycle(long id, JsonObject o)
    {
        string action = o["action"]!.GetValue<string>();
        string type = o["orderType"]!.GetValue<string>();
        int qty = o["orderQty"]!.GetValue<int>();
        string now = DateTime.UtcNow.ToString("O");
        await Push("orderVersion", "Created", $"{{\"id\":{id + 1},\"orderId\":{id},\"orderQty\":{qty},\"orderType\":\"{type}\",\"price\":{o["price"]?.ToJsonString() ?? "0"},\"stopPrice\":{o["stopPrice"]?.ToJsonString() ?? "0"}}}");
        await Push("order", "Created", $"{{\"id\":{id},\"accountId\":101,\"contractId\":555,\"action\":\"{action}\",\"ordStatus\":\"Working\",\"timestamp\":\"{now}\"}}");
        if (type != "Market") return;
        await Task.Delay(30);
        _position += action == "Buy" ? qty : -qty;
        long fill = Interlocked.Increment(ref _nextFill);
        await Push("fill", "Created", $"{{\"id\":{fill},\"orderId\":{id},\"contractId\":555,\"timestamp\":\"{now}\",\"action\":\"{action}\",\"qty\":{qty},\"price\":5000.25,\"active\":true}}");
        await Push("position", "Updated", string.Create(CultureInfo.InvariantCulture, $"{{\"id\":1,\"accountId\":101,\"contractId\":555,\"netPos\":{_position},\"netPrice\":5000.25,\"bought\":{(action == "Buy" ? qty : 0)},\"boughtValue\":{(action == "Buy" ? qty * 5000.25 : 0)},\"sold\":0,\"soldValue\":0}}"));
        await Push("order", "Updated", $"{{\"id\":{id},\"accountId\":101,\"contractId\":555,\"action\":\"{action}\",\"ordStatus\":\"Filled\",\"timestamp\":\"{now}\"}}");
    }

    public TradovateSettings Settings(string password = "pw") => new()
    {
        User = "demo",
        Password = password,
        Cid = "123",
        Sec = "SEC",
        DeviceId = "test-device",
        RestUrl = Server.Http + "/v1",
        UserSocketUrl = Server.Ws + "/v1/websocket",
        MdSocketUrl = Server.Ws + "/md/v1/websocket",
    };

    public void Dispose() => Server.Dispose();
}

