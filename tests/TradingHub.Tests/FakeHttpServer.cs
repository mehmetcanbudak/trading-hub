using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace TradingHub.Tests;

/// <summary>
/// Servidor HTTP/WebSocket de mentira en 127.0.0.1 para probar conexiones sin cuentas reales: cada prueba define qué
/// responde a cada pedido y qué hace con cada WebSocket. Guarda lo que recibió para verificarlo.
/// </summary>
internal sealed class FakeHttpServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public FakeHttpServer(int? firstPort = null)
    {
        var rnd = new Random();
        for (int i = 0; ; i++)
        {
            int port = i == 0 && firstPort.HasValue ? firstPort.Value : rnd.Next(45000, 60000);
            _listener = new HttpListener(); // A failed Start disposes the listener; each retry needs a fresh one.
            try
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Port = port;
                break;
            }
            catch (HttpListenerException)
            {
                _listener.Close();
                if (i >= 20) throw;
            }
        }

        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }
    public string Http => $"http://127.0.0.1:{Port}";
    public string Ws => $"ws://127.0.0.1:{Port}";

    /// <summary>Pedido HTTP: método, ruta con query, encabezados, cuerpo → (código, cuerpo, tipo).</summary>
    public Func<FakeRequest, (int Status, string Body)>? OnHttp { get; set; }

    /// <summary>Respuesta en streaming (por ejemplo precios de OANDA): escribe líneas mientras quiera.</summary>
    public Func<FakeRequest, Stream, CancellationToken, Task>? OnStream { get; set; }

    /// <summary>Qué rutas responden en streaming.</summary>
    public Func<string, bool> IsStream { get; set; } = _ => false;

    /// <summary>WebSocket: ruta y el socket ya aceptado.</summary>
    public Func<string, WebSocket, CancellationToken, Task>? OnSocket { get; set; }

    public List<FakeRequest> Requests { get; } = new();

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url!.PathAndQuery;
            if (ctx.Request.IsWebSocketRequest)
            {
                var wsc = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
                if (OnSocket != null) await OnSocket(path, wsc.WebSocket, _cts.Token).ConfigureAwait(false);
                return;
            }

            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync().ConfigureAwait(false);
            var headers = ctx.Request.Headers.AllKeys.Where(k => k != null).ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);
            var req = new FakeRequest(ctx.Request.HttpMethod, path, headers, body);
            lock (Requests) Requests.Add(req);
            if (IsStream(path) && OnStream != null)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.SendChunked = true;
                ctx.Response.ContentType = "application/octet-stream";
                await OnStream(req, ctx.Response.OutputStream, _cts.Token).ConfigureAwait(false);
                ctx.Response.Close();
                return;
            }

            var (status, text) = OnHttp?.Invoke(req) ?? (404, "{}");
            var bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = text.StartsWith('{') || text.StartsWith('[') ? "application/json" : "text/plain";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch
        {
            // el cliente se fue
        }
    }

    public static async Task<string?> ReceiveText(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    public static Task SendText(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // ya cerrado
        }
    }
}

internal sealed record FakeRequest(string Method, string Path, Dictionary<string, string> Headers, string Body)
{
    public string Query(string key)
    {
        int q = Path.IndexOf('?');
        if (q < 0) return "";
        foreach (var part in Path[(q + 1)..].Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && Uri.UnescapeDataString(part[..eq]) == key) return Uri.UnescapeDataString(part[(eq + 1)..]);
        }

        return "";
    }

    public string Route => Path.Split('?')[0];
}
