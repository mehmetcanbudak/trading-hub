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
    private readonly Dictionary<string, History> _accounts = new();

    private sealed class History
    {
        private IReadOnlyList<AccountFill>? _snapshot;
        public List<AccountFill> Fills { get; } = new();
        public Dictionary<string, int> Indices { get; } = new();
        public long Revision { get; private set; }
        public IReadOnlyList<AccountFill> Snapshot =>
            _snapshot ??= Fills.Count == 0 ? Array.Empty<AccountFill>() : Array.AsReadOnly(Fills.ToArray());

        public bool Accept(AccountFill fill)
        {
            if (Indices.TryGetValue(fill.Id, out int index))
            {
                if (Fills[index] == fill) return false;
                Fills[index] = fill;
            }
            else
            {
                Indices.Add(fill.Id, Fills.Count);
                Fills.Add(fill);
            }
            Revision++;
            _snapshot = null;
            return true;
        }

        public bool Clear()
        {
            if (Fills.Count == 0) return false;
            Fills.Clear();
            Indices.Clear();
            Revision++;
            _snapshot = null;
            return true;
        }
    }

    public AccountJournal(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    /// <summary>Cambió el historial de una cuenta (id de la cuenta). Puede llegar desde otros hilos.</summary>
    public event Action<string>? Changed;

    private string FileOf(string account) =>
        Path.Combine(_dir, string.Concat(account.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".jsonl");

    private History Load(string account)
    {
        if (_accounts.TryGetValue(account, out var history)) return history;
        history = new History();
        var path = FileOf(account);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<AccountFill>(line) is { } fill) history.Accept(fill);
                }
                catch (JsonException)
                {
                    // línea cortada (por ejemplo si se cerró justo al escribir): se saltea
                }
            }
        }

        _accounts[account] = history;
        return history;
    }

    /// <summary>The number of effective executions, with one entry per durable fill ID.</summary>
    public int CountOf(string account)
    {
        lock (_gate) return Load(account).Fills.Count;
    }

    /// <summary>A constant-time change token, including corrections and history resets.</summary>
    public long RevisionOf(string account)
    {
        lock (_gate) return Load(account).Revision;
    }

    /// <summary>Returns a stable read-only snapshot and its atomic token, reused until the next revision.</summary>
    public (long Revision, IReadOnlyList<AccountFill> Fills) SnapshotOf(string account)
    {
        lock (_gate)
        {
            var history = Load(account);
            return (history.Revision, history.Snapshot);
        }
    }

    public IReadOnlyList<AccountFill> FillsOf(string account)
    {
        lock (_gate) return Load(account).Snapshot;
    }

    /// <summary>Durably appends new or corrected executions; exact duplicates are ignored.</summary>
    public bool Add(string account, AccountFill fill)
    {
        lock (_gate)
        {
            var history = Load(account);
            if (history.Indices.TryGetValue(fill.Id, out int index) && history.Fills[index] == fill) return false;
            using (var file = new FileStream(FileOf(account), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                if (file.Length > 0)
                {
                    file.Position = file.Length - 1;
                    if (file.ReadByte() != '\n') file.WriteByte((byte)'\n');
                }
                JsonSerializer.Serialize(file, fill);
                file.WriteByte((byte)'\n');
                Common.StorageSync.Flush(file);
            }
            Common.StorageSync.SyncDirectory(_dir);
            history.Accept(fill);
        }

        Changed?.Invoke(account);
        return true;
    }

    /// <summary>Empieza de cero (el archivo anterior queda guardado aparte, no se borra).</summary>
    public void Restart(string account) => Restart(account, static directory => Common.StorageSync.SyncDirectory(directory));

    internal void Restart(string account, Action<string> syncDirectory)
    {
        bool changed;
        lock (_gate)
        {
            var history = Load(account);
            var path = FileOf(account);
            bool renamed = File.Exists(path);
            if (renamed)
            {
                File.Move(path, path + $".{DateTime.Now:yyyyMMdd-HHmmss}.bak");
            }
            changed = history.Clear();
            // The active namespace is already empty after rename, even if durability confirmation fails.
            if (renamed) syncDirectory(_dir);
        }

        if (changed) Changed?.Invoke(account);
    }
}
