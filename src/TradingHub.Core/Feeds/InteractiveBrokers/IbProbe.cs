using System.Net.Sockets;
using System.Text;

namespace TradingHub.Feeds.InteractiveBrokers;

/// <summary>
/// Prueba de conexión con TWS / IB Gateway usando el protocolo de la TWS API
/// (saludo "API" + rango de versiones; el servidor responde su versión y hora).
/// </summary>
public static class IbProbe
{
    public static async Task<(bool Ok, string Message)> TestAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(host, port, t.Token).ConfigureAwait(false);
            }

            var stream = tcp.GetStream();
            var payload = Encoding.ASCII.GetBytes("v100..187");
            var msg = new List<byte>();
            msg.AddRange(Encoding.ASCII.GetBytes("API\0"));
            var len = BitConverter.GetBytes(payload.Length);
            if (BitConverter.IsLittleEndian) Array.Reverse(len);
            msg.AddRange(len);
            msg.AddRange(payload);
            await stream.WriteAsync(msg.ToArray(), ct).ConfigureAwait(false);

            var lenBuf = new byte[4];
            using var rt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rt.CancelAfter(TimeSpan.FromSeconds(5));
            await stream.ReadExactlyAsync(lenBuf, rt.Token).ConfigureAwait(false);
            if (BitConverter.IsLittleEndian) Array.Reverse(lenBuf);
            int n = BitConverter.ToInt32(lenBuf);
            var body = new byte[n];
            await stream.ReadExactlyAsync(body, rt.Token).ConfigureAwait(false);
            var fields = Encoding.ASCII.GetString(body).Split('\0');
            return (true, $"TWS responded: server version {fields.ElementAtOrDefault(0)}, time {fields.ElementAtOrDefault(1)}");
        }
        catch (Exception ex)
        {
            return (false, $"Could not connect to {host}:{port} ({ex.Message}). Is TWS or IB Gateway running with API access enabled?");
        }
    }
}
