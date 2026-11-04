using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using TradingHub.Feeds.InteractiveBrokers;

namespace TradingHub.Tests;

/// <summary>Loopback-only framed gateway. Every connection is retained, requests are gated and background failures are surfaced.</summary>
internal sealed class FakeIbGateway : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly ConcurrentBag<Task> _workers = new();
    private readonly ConcurrentBag<Session> _sessions = new();
    private readonly Channel<Session> _accepted = Channel.CreateUnbounded<Session>();
    public int ServerVersion { get; set; } = 187;
    public int? StartupError { get; set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public FakeIbGateway() { _listener.Start(); _accept = AcceptAsync(); }
    public async Task<Session> NextSessionAsync() => await _accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                var session = new Session(tcp, _stop.Token);
                _sessions.Add(session);
                _workers.Add(ServeAsync(session));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    private async Task ServeAsync(Session session)
    {
        try
        {
            byte[] preamble = new byte[4];
            await session.Stream.ReadExactlyAsync(preamble, _stop.Token);
            if (!preamble.SequenceEqual("API\0"u8.ToArray())) throw new InvalidDataException("Client omitted API preamble.");
            var hello = await session.ReadAsync();
            if (hello[0] != "v187..187") throw new InvalidDataException("Client advertised an unevidenced protocol range.");
            await session.SendAsync(true, ServerVersion.ToString(), "20261103 12:00:00 UTC");
            if (ServerVersion != 187) return;
            var start = await session.ReadAsync();
            if (start[0] != "71" || start[1] != "2") throw new InvalidDataException("START_API missing.");
            session.ClientId = int.Parse(start[2]);
            if (StartupError is { } code) { await session.SendAsync(false, "4", "2", "-1", code.ToString(), "Fixture startup denied"); return; }
            await session.SendAsync(false, "9", "1", "2000");
            await _accepted.Writer.WriteAsync(session, _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                var request = await session.ReadAsync();
                session.Received.Enqueue(request);
                await session.Requests.Writer.WriteAsync(request, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (IOException) when (session.IntentionallyClosed) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested || session.IntentionallyClosed) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop();
        foreach (var session in _sessions) session.Close();
        await _accept;
        await Task.WhenAll(_workers); // Never swallow gateway assertions or unexpected failures.
        _stop.Dispose();
    }
    internal sealed class Session(TcpClient tcp, CancellationToken ct)
    {
        private readonly SemaphoreSlim _send = new(1);
        private readonly SemaphoreSlim _receive = new(1);
        private readonly List<string[]> _pending = new();
        public NetworkStream Stream { get; } = tcp.GetStream();
        public int ClientId { get; set; }
        public bool IntentionallyClosed { get; private set; }
        public Channel<string[]> Requests { get; } = Channel.CreateUnbounded<string[]>();
        public ConcurrentQueue<string[]> Received { get; } = new();
        public async Task<string[]> NextAsync(string message)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _receive.WaitAsync(timeout.Token);
            try
            {
                int pending = _pending.FindIndex(fields => fields[0] == message);
                if (pending >= 0) { var found = _pending[pending]; _pending.RemoveAt(pending); return found; }
                while (true)
                {
                    var request = await Requests.Reader.ReadAsync(timeout.Token);
                    if (request[0] == message) return request;
                    _pending.Add(request); // Never discard another concurrent request while waiting for a channel.
                }
            }
            finally { _receive.Release(); }
        }
        public async Task SendCoalescedAsync(params string[][] messages)
        {
            using var bytes = new MemoryStream();
            foreach (var fields in messages)
            {
                var payload = IbClient.Encode(fields.Cast<object?>().ToArray());
                byte[] length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
                bytes.Write(length); bytes.Write(payload);
            }
            await _send.WaitAsync(ct);
            try { await Stream.WriteAsync(bytes.ToArray(), ct); }
            finally { _send.Release(); }
        }
        public async Task<string[]> ReadAsync()
        {
            byte[] length = new byte[4]; await Stream.ReadExactlyAsync(length, ct);
            int n = BinaryPrimitives.ReadInt32BigEndian(length);
            if (n <= 0 || n > 1024 * 1024) throw new InvalidDataException("Invalid client frame.");
            byte[] body = new byte[n]; await Stream.ReadExactlyAsync(body, ct);
            return Encoding.UTF8.GetString(body).TrimEnd('\0').Split('\0');
        }
        public async Task SendAsync(bool fragmented, params string[] fields)
        {
            var payload = IbClient.Encode(fields.Cast<object?>().ToArray());
            byte[] frame = new byte[payload.Length + 4]; BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length); payload.CopyTo(frame, 4);
            await _send.WaitAsync(ct);
            try
            {
                if (fragmented) { foreach (var value in frame) await Stream.WriteAsync(new[] { value }, ct); }
                else await Stream.WriteAsync(frame, ct);
            }
            finally { _send.Release(); }
        }
        public void Close() { IntentionallyClosed = true; tcp.Dispose(); }
    }
}
