using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TradingHub.Tests;

public sealed record FakeProxyRequest(string Method, Uri Target, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A finite fixture proxy for actual HTTP and WebSocket consumers. It forwards only to literal-loopback
/// origins, never resolves remote names, and owns/drains every socket. HTTP requests use fresh origin
/// connections; CONNECT and Upgrade preserve the origin's real WebSocket protocol implementation.
/// </summary>
public sealed class FakeProxyServer : IDisposable, IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentBag<Task> _connections = new();
    private readonly Task _accept;
    private Task? _dispose;
    private readonly object _sync = new();
    public string Address { get; }
    public ConcurrentQueue<FakeProxyRequest> Requests { get; } = new();
    public ConcurrentQueue<Exception> Errors { get; } = new();

    public FakeProxyServer()
    {
        _listener.Start();
        Address = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _clients.TryAdd(client, 0);
                _connections.Add(ForwardAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        using (client)
        using (var origin = new TcpClient())
        {
            try
            {
                var input = client.GetStream();
                var (header, bufferedBody) = await ReadHeadersAsync(input, _stop.Token).ConfigureAwait(false);
                var lines = header.Split("\r\n", StringSplitOptions.None);
                var first = lines[0].Split(' ', 3);
                if (first.Length != 3) throw new InvalidDataException("Malformed fixture proxy request.");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
                }
                bool tunnel = first[0] == "CONNECT";
                var target = new Uri(tunnel ? "https://" + first[1] + "/" : first[1], UriKind.Absolute);
                if (!IPAddress.TryParse(target.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
                    throw new InvalidDataException("The fixture proxy refuses non-loopback origins.");
                Requests.Enqueue(new(first[0], target, headers));
                await origin.ConnectAsync(address, target.Port, _stop.Token).ConfigureAwait(false);
                var output = origin.GetStream();
                if (tunnel)
                {
                    await input.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), _stop.Token).ConfigureAwait(false);
                }
                else
                {
                    bool upgrade = headers.ContainsKey("Upgrade");
                    var rewritten = new StringBuilder().Append(first[0]).Append(' ').Append(target.PathAndQuery.Length == 0 ? "/" : target.PathAndQuery)
                        .Append(' ').Append(first[2]).Append("\r\n");
                    foreach (var pair in headers)
                    {
                        if (pair.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                            pair.Key.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                            (!upgrade && pair.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))) continue;
                        rewritten.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
                    }
                    if (!upgrade) rewritten.Append("Connection: close\r\n");
                    rewritten.Append("\r\n");
                    await output.WriteAsync(Encoding.ASCII.GetBytes(rewritten.ToString()), _stop.Token).ConfigureAwait(false);
                }
                if (bufferedBody.Length > 0) await output.WriteAsync(bufferedBody, _stop.Token).ConfigureAwait(false);
                var request = input.CopyToAsync(output, _stop.Token);
                var response = output.CopyToAsync(input, _stop.Token);
                await Task.WhenAny(request, response).ConfigureAwait(false);
                client.Dispose();
                origin.Dispose();
                try { await Task.WhenAll(request, response).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                if (ex is InvalidDataException) Errors.Enqueue(ex);
            }
            catch (Exception ex) { Errors.Enqueue(ex); }
            finally { _clients.TryRemove(client, out _); }
        }
    }

    private static async Task<(string Header, byte[] Body)> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var received = new MemoryStream();
        int searched = 0;
        while (received.Length <= 65536)
        {
            int count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) throw new IOException("Fixture proxy request ended before its headers.");
            received.Write(buffer, 0, count);
            var bytes = received.GetBuffer();
            for (int i = Math.Max(0, searched - 3); i + 3 < received.Length; i++)
            {
                if (bytes[i] != '\r' || bytes[i + 1] != '\n' || bytes[i + 2] != '\r' || bytes[i + 3] != '\n') continue;
                int body = i + 4;
                return (Encoding.ASCII.GetString(bytes, 0, i), bytes.AsSpan(body, (int)received.Length - body).ToArray());
            }
            searched = (int)received.Length;
        }
        throw new InvalidDataException("Fixture proxy request headers exceed their bounded size.");
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_dispose ??= DrainAsync());
    }
    private async Task DrainAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        foreach (var client in _clients.Keys) client.Dispose();
        await _accept.ConfigureAwait(false);
        await Task.WhenAll(_connections).ConfigureAwait(false);
        _stop.Dispose();
    }
}
