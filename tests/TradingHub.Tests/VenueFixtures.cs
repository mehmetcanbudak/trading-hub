using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>No-network HTTP and WebSocket fixtures for public venue feeds.</summary>
internal static class VenueFixtures
{
    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public sealed class Http(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<(Uri Uri, string? Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue((request.RequestUri!, body));
            return respond(request, body);
        }
    }

    public static Task NoDelay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public sealed class Socket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> _frames = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        private WebSocketState _state = WebSocketState.Open;
        public ConcurrentQueue<string> Sent { get; } = new();
        public TaskCompletionSource Subscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SendServer(string frame) => _frames.Writer.TryWrite((Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text));
        public void CloseServer() => _frames.Writer.TryWrite((Array.Empty<byte>(), WebSocketMessageType.Close));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { _state = WebSocketState.Closed; _frames.Writer.TryComplete(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.CloseSent; return Task.CompletedTask; }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Enqueue(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            Subscribed.TrySetResult();
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await _frames.Reader.ReadAsync(cancellationToken);
            if (frame.Type == WebSocketMessageType.Close) _state = WebSocketState.CloseReceived;
            frame.Bytes.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(frame.Bytes.Length, frame.Type, true);
        }
    }

    /// <summary>Hands out queued sockets in connection order and records the endpoints.</summary>
    public sealed class Sockets
    {
        private readonly Channel<Socket> _ready = Channel.CreateUnbounded<Socket>();
        public ConcurrentQueue<Uri> Endpoints { get; } = new();
        public Socket Add() { var socket = new Socket(); _ready.Writer.TryWrite(socket); return socket; }

        public async Task<WebSocket> ConnectAsync(Uri endpoint, CancellationToken ct)
        {
            Endpoints.Enqueue(endpoint);
            return await _ready.Reader.ReadAsync(ct);
        }
    }

    /// <summary>Collects live callbacks and lets tests await specific publications.</summary>
    public sealed class Sink : IMarketDataSink
    {
        private readonly Channel<object> _events = Channel.CreateUnbounded<object>();
        public ConcurrentQueue<Trade> Trades { get; } = new();
        public ConcurrentQueue<DepthUpdate> Depth { get; } = new();
        public ConcurrentQueue<(FeedStatus Status, string? Message)> Statuses { get; } = new();

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            foreach (var trade in trades) Trades.Enqueue(trade);
            _events.Writer.TryWrite(trades);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update)
        {
            Depth.Enqueue(update);
            _events.Writer.TryWrite(update);
        }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            Statuses.Enqueue((status, message));
            _events.Writer.TryWrite((status, message));
        }

        public async Task<T> NextAsync<T>(Func<T, bool> accepts)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var item in _events.Reader.ReadAllAsync(deadline.Token))
                if (item is T typed && accepts(typed)) return typed;
            throw new InvalidOperationException("The fixture stream ended before the expected publication.");
        }
    }
}
