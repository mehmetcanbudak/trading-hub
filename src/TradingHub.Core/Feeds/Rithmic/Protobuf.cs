using System.Text;

namespace TradingHub.Feeds.Rithmic;

/// <summary>
/// Codificador/decodificador protobuf mínimo (solo lo que usa R|Protocol: varints y strings).
/// Evita depender del kit oficial para los mensajes básicos.
/// </summary>
public sealed class ProtoWriter
{
    private readonly MemoryStream _ms = new();

    private void Varint(ulong v)
    {
        while (v >= 0x80)
        {
            _ms.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }

        _ms.WriteByte((byte)v);
    }

    private void Tag(int field, int wire) => Varint(((ulong)field << 3) | (uint)wire);

    public ProtoWriter Int(int field, long value)
    {
        Tag(field, 0);
        Varint(unchecked((ulong)value));
        return this;
    }

    public ProtoWriter Bool(int field, bool value) => Int(field, value ? 1 : 0);

    public ProtoWriter String(int field, string? value)
    {
        if (value == null) return this;
        var bytes = Encoding.UTF8.GetBytes(value);
        Tag(field, 2);
        Varint((ulong)bytes.Length);
        _ms.Write(bytes);
        return this;
    }

    public ProtoWriter Double(int field, double value)
    {
        Tag(field, 1);
        _ms.Write(BitConverter.GetBytes(value));
        return this;
    }

    public byte[] ToArray() => _ms.ToArray();
}

/// <summary>Mensaje protobuf decodificado como mapa campo → valores.</summary>
public sealed class ProtoMessage
{
    private readonly Dictionary<int, List<object>> _fields = new();

    public IEnumerable<int> Fields => _fields.Keys;

    public static ProtoMessage Parse(ReadOnlySpan<byte> data)
    {
        var m = new ProtoMessage();
        int pos = 0;
        while (pos < data.Length)
        {
            ulong key = ReadVarint(data, ref pos);
            int field = (int)(key >> 3);
            int wire = (int)(key & 7);
            object value;
            switch (wire)
            {
                case 0:
                    value = ReadVarint(data, ref pos);
                    break;
                case 1:
                    value = BitConverter.ToDouble(data.Slice(pos, 8));
                    pos += 8;
                    break;
                case 2:
                {
                    int len = (int)ReadVarint(data, ref pos);
                    value = data.Slice(pos, len).ToArray();
                    pos += len;
                    break;
                }
                case 5:
                    value = BitConverter.ToSingle(data.Slice(pos, 4));
                    pos += 4;
                    break;
                default:
                    throw new FormatException($"Unsupported protobuf field type: {wire}");
            }

            if (!m._fields.TryGetValue(field, out var list)) m._fields[field] = list = new List<object>();
            list.Add(value);
        }

        return m;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong r = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[pos++];
            r |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return r;
            shift += 7;
        }
    }

    public bool Has(int field) => _fields.ContainsKey(field);

    public long Int(int field, long fallback = 0) =>
        _fields.TryGetValue(field, out var l) && l[0] is ulong u ? unchecked((long)u) : fallback;

    public double Double(int field, double fallback = 0) => _fields.TryGetValue(field, out var l) ? l[0] switch
    {
        double d => d,
        float f => f,
        ulong u => u,
        _ => fallback,
    } : fallback;

    /// <summary>Un número venga como venga: texto ("1234.50"), double, float o entero (Rithmic usa los cuatro).</summary>
    public decimal? Number(int field)
    {
        if (!_fields.TryGetValue(field, out var l)) return null;
        return l[0] switch
        {
            byte[] b when decimal.TryParse(Encoding.UTF8.GetString(b), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            double d when double.IsFinite(d) => (decimal)d,
            float f when float.IsFinite(f) => (decimal)f,
            ulong u => unchecked((long)u),
            _ => null,
        };
    }

    public string? String(int field) =>
        _fields.TryGetValue(field, out var l) && l[0] is byte[] b ? Encoding.UTF8.GetString(b) : null;

    /// <summary>Todos los valores enteros de un campo repetido (sueltos o empaquetados).</summary>
    public IEnumerable<long> Ints(int field)
    {
        if (!_fields.TryGetValue(field, out var l)) yield break;
        foreach (var v in l)
        {
            if (v is ulong u) yield return unchecked((long)u);
            else if (v is byte[] packed)
            {
                int pos = 0;
                while (pos < packed.Length) yield return unchecked((long)ReadVarint(packed, ref pos));
            }
        }
    }

    public IEnumerable<string> Strings(int field) =>
        _fields.TryGetValue(field, out var l) ? l.OfType<byte[]>().Select(b => Encoding.UTF8.GetString(b)) : Array.Empty<string>();
}
