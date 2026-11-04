// Adapted from PANASV/trading-hub cdb84ea16eee7ac8d6c1b74136d2eeacedadf834.
// Copyright (c) 2026 ralphtradesz and Trading Hub contributors; MIT with Commons Clause (repository LICENSE).
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace TradingHub.Feeds.InteractiveBrokers;

public sealed class IbException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Length-prefixed, NUL-separated TWS API protocol. Only the evidenced version 187 layouts are negotiated.</summary>
public sealed class IbClient : IDisposable
{
    public const int MinVersion = 187, MaxVersion = 187;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly object _sendLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<string?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readerStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IbClient(TcpClient tcp) { _tcp = tcp; _stream = tcp.GetStream(); }
    private static int _nextRequest = 1000;
    public int ServerVersion { get; private set; }
    public string ConnectionTime { get; private set; } = "";
    public bool IsConnected => !_closed.Task.IsCompleted;
    public Task<string?> Closed => _closed.Task;
    internal Task ReaderStopped => _readerStopped.Task;
    public event Action<string[]>? Message;
    public int NextRequestId() => Interlocked.Increment(ref _nextRequest);

    public static async Task<IbClient> ConnectAsync(string host, int port, int clientId, CancellationToken ct)
    {
        if (clientId < 0 || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("IB requires a host, valid TCP port and nonnegative unique client ID.");
        var tcp = new TcpClient { NoDelay = true, SendTimeout = 10000 };
        IbClient? client = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            client = new IbClient(tcp);
            await client._stream.WriteAsync("API\0"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            await client._stream.WriteAsync(Frame(Encoding.ASCII.GetBytes("v187..187")), timeout.Token).ConfigureAwait(false);
            var hello = await client.ReadAsync(timeout.Token).ConfigureAwait(false);
            if (hello.Length < 1 || !int.TryParse(hello[0], out int version) || version != 187)
                throw new NotSupportedException($"Unsupported IB protocol version {hello.FirstOrDefault() ?? "missing"}; this adapter requires negotiated version 187.");
            client.ServerVersion = version;
            client.ConnectionTime = hello.Length > 1 ? hello[1] : "";
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Starting(string[] fields)
            {
                if (fields[0] == "9")
                {
                    if (fields.Length != 3 || !int.TryParse(fields[1], out _) || !long.TryParse(fields[2], out long nextId) || nextId < 0)
                        throw new InvalidDataException("Malformed IB START_API readiness response.");
                    ready.TrySetResult();
                }
                if (fields[0] == "4")
                {
                    var error = Error(fields);
                    if (error.Code is 326 or 502 or 503 or 504 or 1100) ready.TrySetException(new IbException(error.Code, error.Text));
                }
            }
            client.Message += Starting;
            client.StartReader();
            client.Send(71, 2, clientId, "");
            try
            {
                var completed = await Task.WhenAny(ready.Task, client.Closed).WaitAsync(timeout.Token).ConfigureAwait(false);
                if (completed != ready.Task) throw new IOException(await client.Closed.ConfigureAwait(false) ?? "IB closed during START_API.");
                await ready.Task.ConfigureAwait(false);
            }
            finally { client.Message -= Starting; }
            return client;
        }
        catch { if (client != null) client.Dispose(); else tcp.Dispose(); throw; }
    }

    private void StartReader() => _ = Task.Run(async () =>
    {
        string? reason = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var fields = await ReadAsync(_stop.Token).ConfigureAwait(false);
                if (!_stop.IsCancellationRequested) Message?.Invoke(fields);
            }
        }
        catch (Exception ex) when (!_stop.IsCancellationRequested) { reason = ex.Message; }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally { _closed.TrySetResult(reason); _tcp.Dispose(); _readerStopped.TrySetResult(); }
    });

    private async Task<string[]> ReadAsync(CancellationToken ct)
    {
        byte[] length = new byte[4];
        await _stream.ReadExactlyAsync(length, ct).ConfigureAwait(false);
        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(length);
        if (count <= 0 || count > 16 * 1024 * 1024) throw new InvalidDataException($"Invalid IB frame length: {count}.");
        byte[] body = new byte[count];
        await _stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(body).TrimEnd('\0').Split('\0');
    }

    public static (int Id, int Code, string Text) Error(string[] fields)
    {
        var reader = new IbReader(fields, 1);
        reader.ReadInt();
        return (reader.ReadInt(), reader.ReadInt(), reader.ReadString());
    }
    public static string Field(object? value) => value switch
    {
        null => "", bool b => b ? "1" : "0", IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "", _ => value.ToString() ?? "",
    };
    public static byte[] Encode(params object?[] fields) => Encoding.UTF8.GetBytes(string.Join('\0', fields.Select(Field)) + '\0');
    private static byte[] Frame(byte[] payload)
    {
        byte[] bytes = new byte[payload.Length + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, payload.Length);
        payload.CopyTo(bytes, 4);
        return bytes;
    }
    public void Send(params object?[] fields)
    {
        if (fields.Length == 0 || fields[0] is not int message || message is not (71 or 20 or 25 or 97 or 98 or 10 or 11))
            throw new NotSupportedException("IB transport is data-only: account and order-execution messages are not supported.");
        lock (_sendLock)
        {
            if (!IsConnected) throw new IOException("IB transport is closed.");
            try { _stream.Write(Frame(Encode(fields))); }
            catch (Exception ex) { _closed.TrySetResult(ex.Message); _tcp.Dispose(); throw; }
        }
    }
    public void Dispose() { _stop.Cancel(); _closed.TrySetResult(null); _tcp.Dispose(); }
}

/// <summary>Strict field reader; malformed messages fail visibly rather than becoming fabricated zero values.</summary>
public sealed class IbReader(string[] fields, int start = 0)
{
    private int _index = start;
    public bool HasMore => _index < fields.Length;
    public string ReadString() => _index < fields.Length ? fields[_index++] : throw new InvalidDataException("Truncated IB message.");
    public int ReadInt() => int.Parse(ReadString(), CultureInfo.InvariantCulture);
    public long ReadLong() => long.Parse(ReadString(), CultureInfo.InvariantCulture);
    public double ReadDouble() => double.Parse(ReadString(), CultureInfo.InvariantCulture);
    public decimal ReadDecimal()
    {
        string value = ReadString();
        return value is "" or "9223372036854775807" ? 0 : decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
