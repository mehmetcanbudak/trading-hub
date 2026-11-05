// Field numbers/framing adapted from kvp61/trading-hub at 50220ba91613161685f84384825d855cb4dbeb76,
// originally ralphtradesz/trading-hub (MIT with Commons Clause), and longportapp/openapi-protobufs quote/api.proto.
// Parsers reject missing required measurements; protobuf defaults are not evidence of an observed zero price.
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace TradingHub.Feeds.Longbridge;

public enum LongbridgeCommand : byte
{
    Close = 0, Heartbeat = 1, Auth = 2, Subscribe = 6, Unsubscribe = 7,
    StaticInfo = 10, Quote = 11, Depth = 14, Trades = 17, HistoryCandles = 27,
    PushQuote = 101, PushDepth = 102, PushTrades = 104,
}

public readonly record struct LongbridgePacket(byte Type, byte Command, uint RequestId, byte Status, byte[] Body);

/// <summary>Version-one quote framing. One complete binary WebSocket message contains one packet.</summary>
public static class LongbridgeProtocol
{
    public const int MaximumBodyLength = 4 * 1024 * 1024;
    public static byte[] Request(byte command, uint id, ushort timeoutMs, ReadOnlySpan<byte> body) => Encode(1, command, id, 0, timeoutMs, body);
    public static byte[] Response(byte command, uint id, byte status, ReadOnlySpan<byte> body) => Encode(2, command, id, status, 0, body);
    private static byte[] Encode(byte type, byte command, uint id, byte status, ushort timeoutMs, ReadOnlySpan<byte> body)
    {
        if (body.Length > MaximumBodyLength) throw new InvalidDataException("Longbridge packet exceeds the bounded message size.");
        int header = type == 1 ? 11 : 10;
        var bytes = new byte[header + body.Length];
        bytes[0] = type; bytes[1] = command;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(2), id);
        if (type == 1) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), timeoutMs); else bytes[6] = status;
        bytes[header - 3] = (byte)(body.Length >> 16); bytes[header - 2] = (byte)(body.Length >> 8); bytes[header - 1] = (byte)body.Length;
        body.CopyTo(bytes.AsSpan(header));
        return bytes;
    }
    public static LongbridgePacket Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5) throw new InvalidDataException("Truncated Longbridge packet.");
        if ((bytes[0] & 0xD0) != 0) throw new InvalidDataException("Unsupported verified/reserved Longbridge packet flags; authenticity is not assumed.");
        byte type = (byte)(bytes[0] & 15);
        int header = type switch { 1 => 11, 2 => 10, 3 => 5, _ => throw new InvalidDataException("Unknown Longbridge packet type.") };
        if (bytes.Length < header) throw new InvalidDataException("Truncated Longbridge packet header.");
        int length = (bytes[header - 3] << 16) | (bytes[header - 2] << 8) | bytes[header - 1];
        if (length > MaximumBodyLength || bytes.Length != header + length) throw new InvalidDataException("Invalid Longbridge packet length.");
        var body = bytes[header..].ToArray();
        if ((bytes[0] & 0x20) != 0)
        {
            using var input = new MemoryStream(body, writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = gzip.Read(buffer)) != 0)
            {
                if (output.Length + read > MaximumBodyLength) throw new InvalidDataException("Longbridge decompressed message exceeds the size limit.");
                output.Write(buffer, 0, read);
            }
            body = output.ToArray();
        }
        return new(type, bytes[1], type == 3 ? 0 : BinaryPrimitives.ReadUInt32BigEndian(bytes[2..]), type == 2 ? bytes[6] : (byte)0, body);
    }
}

internal sealed class LbWriter
{
    private readonly MemoryStream _stream = new();
    public byte[] Build() => _stream.ToArray();
    private void Varint(ulong n)
    {
        while (n >= 128) { _stream.WriteByte((byte)(n | 128)); n >>= 7; }
        _stream.WriteByte((byte)n);
    }
    public void Number(int field, long n) { Varint((ulong)(field << 3)); Varint(unchecked((ulong)n)); }
    public void Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    public void Bytes(int field, ReadOnlySpan<byte> value) { Varint((ulong)((field << 3) | 2)); Varint((ulong)value.Length); _stream.Write(value); }
}

internal ref struct LbReader
{
    private readonly ReadOnlySpan<byte> _bytes;
    private int _position;
    public int Field { get; private set; }
    public int Wire { get; private set; }
    public LbReader(ReadOnlySpan<byte> bytes) { _bytes = bytes; _position = 0; Field = 0; Wire = 0; }
    public bool Next()
    {
        if (_position == _bytes.Length) return false;
        ulong tag = Varint();
        if ((tag >> 3) is 0 or > 536870911) throw new InvalidDataException("Invalid protobuf field number.");
        Field = (int)(tag >> 3); Wire = (int)(tag & 7);
        if (Wire is not (0 or 1 or 2 or 5)) throw new InvalidDataException("Unsupported protobuf wire type.");
        return true;
    }
    private ulong Varint()
    {
        ulong value = 0;
        for (int shift = 0; shift <= 63; shift += 7)
        {
            if (_position >= _bytes.Length) throw new InvalidDataException("Truncated protobuf varint.");
            byte b = _bytes[_position++];
            if (shift == 63 && b > 1) throw new InvalidDataException("Overflowing protobuf varint.");
            value |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return value;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }
    public long Number() { Require(0); return unchecked((long)Varint()); }
    public ReadOnlySpan<byte> Bytes()
    {
        Require(2);
        ulong count = Varint();
        if (count > (ulong)(_bytes.Length - _position)) throw new InvalidDataException("Truncated protobuf field.");
        var result = _bytes.Slice(_position, (int)count); _position += (int)count; return result;
    }
    public string Text() => Encoding.UTF8.GetString(Bytes());
    public decimal Decimal() => OptionalDecimal() ?? throw new InvalidDataException("Missing decimal in Longbridge data.");
    public decimal? OptionalDecimal()
    {
        string text = Text();
        if (text.Length == 0) return null;
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException("Invalid decimal in Longbridge data.");
        return value;
    }
    private void Require(int wire) { if (Wire != wire) throw new InvalidDataException("Unexpected Longbridge protobuf field type."); }
    public void Skip()
    {
        if (Wire == 0) { _ = Varint(); return; }
        if (Wire == 2) { _ = Bytes(); return; }
        int count = Wire == 1 ? 8 : 4;
        if (_bytes.Length - _position < count) throw new InvalidDataException("Truncated protobuf fixed field.");
        _position += count;
    }
}

internal static class LbMessages
{
    public static byte[] Symbol(string symbol) { var w = new LbWriter(); w.Text(1, symbol); return w.Build(); }
    public static byte[] Symbols(IEnumerable<string> symbols) { var w = new LbWriter(); foreach (var s in symbols) w.Text(1, s); return w.Build(); }
    public static byte[] Subscribe(IEnumerable<string> symbols, bool firstPush)
    {
        var w = new LbWriter(); foreach (var s in symbols) w.Text(1, s);
        w.Number(2, 1); w.Number(2, 2); w.Number(2, 4); w.Number(3, firstPush ? 1 : 0); return w.Build();
    }
    public static byte[] Trades(string symbol, int count) { var w = new LbWriter(); w.Text(1, symbol); w.Number(2, count); return w.Build(); }
    public static byte[] History(string symbol, int period, string date, string minute, int session)
    {
        var offset = new LbWriter(); offset.Number(1, 1); offset.Text(2, date); offset.Text(3, minute); offset.Number(4, 1000);
        var w = new LbWriter(); w.Text(1, symbol); w.Number(2, period); w.Number(3, 0); w.Number(4, 1); w.Bytes(5, offset.Build()); w.Number(7, session);
        return w.Build();
    }
    public static (long Code, string Message) Error(ReadOnlySpan<byte> bytes)
    {
        long code = 0; string message = ""; var r = new LbReader(bytes);
        while (r.Next()) { if (r.Field == 1) code = r.Number(); else if (r.Field == 2) message = r.Text(); else r.Skip(); }
        return (code, message);
    }
    public sealed record Static(string Symbol, string Name, string Exchange, string Currency, long? LotSize);
    public static List<Static> StaticInfo(ReadOnlySpan<byte> bytes)
    {
        var list = new List<Static>(); var outer = new LbReader(bytes);
        while (outer.Next())
        {
            if (outer.Field != 1) { outer.Skip(); continue; }
            var r = new LbReader(outer.Bytes()); string symbol = "", name = "", exchange = "", currency = ""; long? lot = null;
            while (r.Next())
            {
                switch (r.Field) { case 1: symbol = r.Text(); break; case 3: name = r.Text(); break; case 6: exchange = r.Text(); break; case 7: currency = r.Text(); break; case 8: lot = r.Number(); break; default: r.Skip(); break; }
            }
            if (symbol.Length == 0) throw new InvalidDataException("Longbridge static information has no symbol.");
            list.Add(new(symbol, name, exchange, currency, lot));
        }
        return list;
    }
    public static IReadOnlyList<LongbridgeQuote> Quotes(ReadOnlySpan<byte> bytes)
    {
        var list = new List<LongbridgeQuote>(); var r = new LbReader(bytes);
        while (r.Next()) { if (r.Field == 1) list.Add(Quote(r.Bytes(), push: false)); else r.Skip(); }
        return list;
    }
    public static LongbridgeQuote Quote(ReadOnlySpan<byte> bytes, bool push)
    {
        string symbol = ""; decimal? last = null, previous = null, open = null, high = null, low = null, turnover = null;
        long? timestamp = null, volume = null, sequence = null; int? status = null, session = null;
        var r = new LbReader(bytes);
        while (r.Next())
        {
            switch (r.Field)
            {
                case 1: symbol = r.Text(); break;
                case 2: if (push) sequence = r.Number(); else last = r.OptionalDecimal(); break;
                case 3: if (push) last = r.OptionalDecimal(); else previous = r.OptionalDecimal(); break;
                case 4: open = r.OptionalDecimal(); break; case 5: high = r.OptionalDecimal(); break; case 6: low = r.OptionalDecimal(); break;
                case 7: timestamp = r.Number(); break; case 8: volume = r.Number(); break; case 9: turnover = r.OptionalDecimal(); break;
                case 10: status = checked((int)r.Number()); break; case 11: if (push) session = checked((int)r.Number()); else r.Skip(); break;
                default: r.Skip(); break;
            }
        }
        if (symbol.Length == 0) throw new InvalidDataException("Longbridge quote has no symbol.");
        if (timestamp is < 0 || volume is < 0) throw new InvalidDataException("Longbridge quote has an invalid timestamp or cumulative volume.");
        return new(symbol, last, previous, open, high, low, timestamp is > 0 ? Time(timestamp.Value) : null, volume, turnover, status, session, sequence);
    }
    public readonly record struct TapeTrade(decimal Price, long Volume, long Timestamp, int Direction, int Session);
    public static (string Symbol, long? Sequence, List<TapeTrade> Trades) Tape(ReadOnlySpan<byte> bytes, bool push)
    {
        string symbol = ""; long? sequence = null; var trades = new List<TapeTrade>(); var outer = new LbReader(bytes);
        while (outer.Next())
        {
            if (outer.Field == 1) { symbol = outer.Text(); continue; }
            if (push && outer.Field == 2) { sequence = outer.Number(); continue; }
            if (outer.Field != (push ? 3 : 2)) { outer.Skip(); continue; }
            var r = new LbReader(outer.Bytes()); decimal? price = null; long? volume = null, time = null; int direction = 0, session = 0;
            while (r.Next())
            {
                switch (r.Field) { case 1: price = r.Decimal(); break; case 2: volume = r.Number(); break; case 3: time = r.Number(); break; case 5: direction = checked((int)r.Number()); break; case 6: session = checked((int)r.Number()); break; default: r.Skip(); break; }
            }
            if (price is not > 0 || volume is not > 0 || time is not > 0) throw new InvalidDataException("Longbridge trade is missing a positive observed price, size or timestamp.");
            trades.Add(new(price.Value, volume.Value, time.Value, direction, session));
        }
        if (symbol.Length == 0) throw new InvalidDataException("Longbridge tape has no symbol.");
        return (symbol, sequence, trades);
    }
    public readonly record struct Level(int Position, decimal? Price, long Volume, long? OrderCount);
    public static (string Symbol, long? Sequence, List<Level> Asks, List<Level> Bids) Depth(ReadOnlySpan<byte> bytes, bool push)
    {
        string symbol = ""; long? sequence = null; var asks = new List<Level>(); var bids = new List<Level>(); var outer = new LbReader(bytes);
        while (outer.Next())
        {
            if (outer.Field == 1) { symbol = outer.Text(); continue; }
            if (push && outer.Field == 2) { sequence = outer.Number(); continue; }
            if (outer.Field != (push ? 3 : 2) && outer.Field != (push ? 4 : 3)) { outer.Skip(); continue; }
            bool ask = outer.Field == (push ? 3 : 2); var r = new LbReader(outer.Bytes()); int? position = null; decimal? price = null; long volume = 0; long? orders = null;
            while (r.Next())
            {
                switch (r.Field) { case 1: position = checked((int)r.Number()); break; case 2: price = r.OptionalDecimal(); break; case 3: volume = r.Number(); break; case 4: orders = r.Number(); break; default: r.Skip(); break; }
            }
            if (position is not > 0 or > 1000 || volume < 0 || price is < 0 || (volume > 0 && price is null or <= 0))
                throw new InvalidDataException("Invalid or missing Longbridge depth position, price or size.");
            (ask ? asks : bids).Add(new(position.Value, price, volume, orders));
        }
        if (symbol.Length == 0) throw new InvalidDataException("Longbridge depth has no symbol.");
        return (symbol, sequence, asks, bids);
    }
    public readonly record struct Bar(long Timestamp, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
    public static (string Symbol, List<Bar> Bars) Candles(ReadOnlySpan<byte> bytes)
    {
        string symbol = ""; var list = new List<Bar>(); var outer = new LbReader(bytes);
        while (outer.Next())
        {
            if (outer.Field == 1) { symbol = outer.Text(); continue; }
            if (outer.Field != 2) { outer.Skip(); continue; }
            var r = new LbReader(outer.Bytes()); decimal? close = null, open = null, low = null, high = null; long volume = 0; long? time = null;
            while (r.Next())
            {
                switch (r.Field) { case 1: close = r.Decimal(); break; case 2: open = r.Decimal(); break; case 3: low = r.Decimal(); break; case 4: high = r.Decimal(); break; case 5: volume = r.Number(); break; case 7: time = r.Number(); break; default: r.Skip(); break; }
            }
            if (close is not > 0 || open is not > 0 || low is not > 0 || high is not > 0 || volume < 0 || time is not > 0 || low > high || open < low || open > high || close < low || close > high)
                throw new InvalidDataException("Invalid or missing observed Longbridge candle fields.");
            list.Add(new(time.Value, open.Value, high.Value, low.Value, close.Value, volume));
        }
        if (symbol.Length == 0) throw new InvalidDataException("Longbridge candles have no symbol.");
        return (symbol, list);
    }
    public static DateTime Time(long seconds)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("Invalid Longbridge exchange timestamp."); }
    }
}

/// <summary>Quote measurements; absent fields stay null. Quote volume is cumulative, never individual tape volume.</summary>
public sealed record LongbridgeQuote(string Symbol, decimal? LastPrice, decimal? PreviousClose, decimal? Open, decimal? High,
    decimal? Low, DateTime? Timestamp, long? Volume, decimal? Turnover, int? TradeStatus, int? TradeSession, long? Sequence);
