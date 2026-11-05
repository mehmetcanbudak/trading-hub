using System.Text.Json;

namespace TradingHub.Trading;

/// <summary>Una ejecución de una cuenta: lo que queda guardado para el historial y las estadísticas.</summary>
public sealed record AccountFill(
    string Id,
    DateTime Time,
    string Symbol,
    bool Buy,
    decimal Quantity,
    decimal Price,
    decimal PointValue,
    decimal Commission = 0,
    string OrderId = "");

/// <summary>Operación completa: desde que se abre la posición hasta que vuelve a cero (o se da vuelta).</summary>
public sealed record AccountTrade(
    string Symbol,
    bool IsLong,
    DateTime OpenTime,
    DateTime CloseTime,
    decimal Quantity,
    decimal OpenPrice,
    decimal ClosePrice,
    decimal Pnl,
    decimal Commission)
{
    public decimal NetPnl => Pnl - Commission;
    public TimeSpan Duration => CloseTime - OpenTime;
}

/// <summary>Lo que sale de recorrer las ejecuciones: operaciones cerradas y cada resultado realizado (también los parciales).</summary>
public sealed record RoundTripResult(
    List<AccountTrade> Trades,
    List<(DateTime Time, decimal Net)> Realized,
    decimal RealizedNet,
    decimal Commission);

public static class RoundTrips
{
    /// <summary>
    /// Arma las operaciones cerradas a partir de las ejecuciones, con precio promedio: sumar a la posición
    /// promedia la entrada, reducirla realiza resultado, y darla vuelta cierra una operación y abre otra.
    /// </summary>
    public static RoundTripResult Analyze(IEnumerable<AccountFill> fills)
    {
        var trades = new List<AccountTrade>();
        var realized = new List<(DateTime, decimal)>();
        decimal totalNet = 0, totalComm = 0;
        foreach (var g in fills.GroupBy(f => f.Symbol))
        {
            decimal pos = 0, avg = 0, entryValue = 0, entryQty = 0, exitValue = 0, exitQty = 0, pnl = 0, comm = 0;
            bool isLong = false;
            DateTime open = default;
            foreach (var f in g.OrderBy(f => f.Time))
            {
                decimal q = f.Quantity, pv = f.PointValue > 0 ? f.PointValue : 1;
                if (q <= 0) continue;
                decimal commPerUnit = f.Commission / q;
                int dir = f.Buy ? 1 : -1;
                if (pos != 0 && Math.Sign(pos) != dir)
                {
                    decimal closing = Math.Min(q, Math.Abs(pos));
                    decimal gained = (f.Price - avg) * closing * Math.Sign(pos) * pv, c = commPerUnit * closing;
                    pnl += gained;
                    comm += c;
                    realized.Add((f.Time, gained - c));
                    totalNet += gained - c;
                    totalComm += c;
                    exitValue += f.Price * closing;
                    exitQty += closing;
                    pos += dir * closing;
                    q -= closing;
                    if (pos == 0)
                    {
                        trades.Add(new AccountTrade(g.Key, isLong, open, f.Time, entryQty, entryValue / entryQty, exitValue / exitQty, pnl, comm));
                        avg = entryValue = entryQty = exitValue = exitQty = pnl = comm = 0;
                    }
                }

                if (q > 0)
                {
                    if (pos == 0)
                    {
                        open = f.Time;
                        isLong = dir > 0;
                    }

                    decimal c = commPerUnit * q;
                    avg = (avg * Math.Abs(pos) + f.Price * q) / (Math.Abs(pos) + q);
                    pos += dir * q;
                    entryValue += f.Price * q;
                    entryQty += q;
                    comm += c;
                    // la comisión de entrada se descuenta al pagarla
                    if (c != 0)
                    {
                        realized.Add((f.Time, -c));
                        totalNet -= c;
                        totalComm += c;
                    }
                }
            }
        }

        trades.Sort((a, b) => a.CloseTime.CompareTo(b.CloseTime));
        realized.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new RoundTripResult(trades, realized, totalNet, totalComm);
    }
}

/// <summary>
/// Historial de ejecuciones por cuenta, guardado en disco (un JSON por línea) para que el saldo, las órdenes
/// ejecutadas y las estadísticas sobrevivan al cerrar Trading Hub.
/// </summary>
public sealed class AccountJournal
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly Func<string, Stream> _openAppend;
    private readonly Dictionary<string, List<AccountFill>> _fills = new();
    private readonly Dictionary<string, HashSet<string>> _ids = new();

    public AccountJournal(string dir, Func<string, Stream>? openAppend = null)
    {
        _dir = dir;
        _openAppend = openAppend ?? (static path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.Read, 4096, FileOptions.WriteThrough));
        Directory.CreateDirectory(dir);
    }

    /// <summary>Cambió el historial de una cuenta (id de la cuenta). Puede llegar desde otros hilos.</summary>
    public event Action<string>? Changed;

    private string FileOf(string account) =>
        Path.Combine(_dir, string.Concat(account.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".jsonl");

    private List<AccountFill> Load(string account)
    {
        if (_fills.TryGetValue(account, out var list)) return list;
        list = new List<AccountFill>();
        var ids = new HashSet<string>();
        var path = FileOf(account);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<AccountFill>(line) is { } f && ids.Add(f.Id)) list.Add(f);
                }
                catch (JsonException)
                {
                    // línea cortada (por ejemplo si se cerró justo al escribir): se saltea
                }
            }
        }

        _fills[account] = list;
        _ids[account] = ids;
        return list;
    }

    /// <summary>Cuántas ejecuciones tiene la cuenta (para saber si cambió sin copiar la lista).</summary>
    public int CountOf(string account)
    {
        lock (_gate) return Load(account).Count;
    }

    public IReadOnlyList<AccountFill> FillsOf(string account)
    {
        lock (_gate) return Load(account).ToList();
    }

    /// <summary>Guarda una ejecución (las repetidas, con el mismo id, se ignoran).</summary>
    public bool Add(string account, AccountFill fill)
    {
        lock (_gate)
        {
            var list = Load(account);
            if (_ids[account].Contains(fill.Id)) return false;
            using (var stream = _openAppend(FileOf(account)))
            {
                // A failed prior append may leave a partial last line. Preserve it, but never join a retry onto that fragment.
                bool needsBoundary = stream.Length > 0;
                if (needsBoundary)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    needsBoundary = stream.ReadByte() != '\n';
                }
                stream.Seek(0, SeekOrigin.End);
                if (needsBoundary) stream.WriteByte((byte)'\n');
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(fill));
                stream.WriteByte((byte)'\n');
                if (stream is FileStream file) file.Flush(flushToDisk: true); else stream.Flush();
            }
            // Only a successful durable append acknowledges delivery. A retry after failure must not hit an in-memory duplicate.
            _ids[account].Add(fill.Id);
            list.Add(fill);
        }

        Changed?.Invoke(account);
        return true;
    }

    /// <summary>Empieza de cero (el archivo anterior queda guardado aparte, no se borra).</summary>
    public void Restart(string account)
    {
        lock (_gate)
        {
            var path = FileOf(account);
            if (File.Exists(path)) File.Move(path, path + $".{DateTime.Now:yyyyMMdd-HHmmss}.bak");
            _fills[account] = new List<AccountFill>();
            _ids[account] = new HashSet<string>();
        }

        Changed?.Invoke(account);
    }
}
