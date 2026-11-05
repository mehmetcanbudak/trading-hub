using System.Net;
using System.Net.Sockets;
using System.Text;
using TradingHub.App.Windows;

namespace TradingHub.App.Services;

/// <summary>Explicit browser authorization with an already-bound ephemeral loopback redirect. No token/code paste or URL logging.</summary>
public static class LongbridgeAuthorizationFlow
{
    public static Task AuthorizeAsync(ConnectionManager connections, ConnectionProfile profile,
        Func<Uri, CancellationToken, Task>? openBrowser = null, CancellationToken cancellationToken = default) =>
        connections.RunLongbridgeAuthorizationAsync(token => AuthorizeCoreAsync(connections, profile, openBrowser, token), cancellationToken);

    private static async Task AuthorizeCoreAsync(ConnectionManager connections, ConnectionProfile profile,
        Func<Uri, CancellationToken, Task>? openBrowser, CancellationToken cancellationToken)
    {
        if (profile.Kind != ConnectionKind.Longbridge) throw new ArgumentException("A Longbridge data-only connection is required.", nameof(profile));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/longbridge-callback/");
        var session = connections.LongbridgeSessionFor(profile);
        using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.LifetimeToken);
        var authorization = await session.BeginAuthorizationAsync(redirect, sessionLifetime.Token);
        profile.Longbridge.ClientId = authorization.ClientId; // Dynamic registration ID is public, unlike the opaque token secret.
        connections.Save();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sessionLifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        if (openBrowser != null) await openBrowser(authorization.AuthorizationUri, deadline.Token);
        else Shell.Open(authorization.AuthorizationUri.AbsoluteUri);
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            requestDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            byte[] bytes = new byte[16 * 1024];
            int count = 0, newline = -1;
            while (count < bytes.Length && newline < 0)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(count), requestDeadline.Token);
                if (read == 0) break;
                newline = Array.IndexOf(bytes, (byte)'\n', count, read);
                count += read;
            }
            string[] request = newline > 0 ? Encoding.ASCII.GetString(bytes, 0, newline).TrimEnd('\r').Split(' ') : [];
            bool valid = request.Length == 3 && request[0] == "GET" && request[2].StartsWith("HTTP/1.", StringComparison.Ordinal) &&
                request[1].StartsWith("/longbridge-callback/?", StringComparison.Ordinal) &&
                Uri.TryCreate(redirect, request[1], out var callback) && callback.AbsolutePath == redirect.AbsolutePath;
            byte[] body = Encoding.UTF8.GetBytes(valid ? "Return to Trading Hub to check authorization status. This connection provides market data only." : "Invalid authorization callback.");
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, deadline.Token);
            await stream.WriteAsync(body, deadline.Token);
            if (!valid) continue;
            // Exact redirect, expiry, state, PKCE and credential generation are validated before publishing secrets.
            await session.CompleteAuthorizationAsync(new Uri(redirect, request[1]), deadline.Token);
            connections.Invalidate(profile);
            return;
        }
    }
}
