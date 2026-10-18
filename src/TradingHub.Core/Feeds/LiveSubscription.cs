namespace TradingHub.Feeds;

/// <summary>Suscripción que puede forzar una reconexión al instante (lo que se perdió se rellena solo).</summary>
public interface IReconnectable
{
    void Reconnect();
}

/// <summary>
/// Control de una suscripción en vivo con uno o más canales (por ejemplo la cinta y el libro, que en
/// Binance Futures van por conexiones distintas). Junta el estado de los canales en uno solo para la UI
/// (manda el peor) y permite cortar las conexiones actuales para que se reconecten en el momento.
/// </summary>
public sealed class LiveSubscription : IDisposable, IReconnectable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<CancellationTokenSource> _connections = new();
    private readonly Dictionary<string, (FeedStatus Status, string? Message)> _channels = new();
    private readonly Action<FeedStatus, string?> _report;
    private readonly string _name;
    private FeedStatus _lastStatus = (FeedStatus)(-1);
    private string? _lastMessage;

    public LiveSubscription(string name, Action<FeedStatus, string?> report)
    {
        _name = name;
        _report = report;
    }

    /// <summary>Se cancela al cerrar la suscripción.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Token de una conexión: se cancela al cerrar la suscripción o al pedir reconectar.</summary>
    public CancellationTokenSource BeginConnection()
    {
        var c = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_gate) _connections.Add(c);
        return c;
    }

    public void EndConnection(CancellationTokenSource c)
    {
        lock (_gate) _connections.Remove(c);
        c.Dispose();
    }

    private int _manual;

    /// <summary>True (una vez por canal y pedido) si el último corte lo pidió el usuario.</summary>
    public bool WasRequested(ref int seen) => Volatile.Read(ref _manual) != seen && (seen = Volatile.Read(ref _manual)) >= 0;

    /// <summary>Corta las conexiones actuales: cada canal se reconecta enseguida y rellena el hueco.</summary>
    public void Reconnect()
    {
        Interlocked.Increment(ref _manual);
        lock (_gate)
        {
            foreach (var c in _connections)
            {
                try
                {
                    c.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    /// <summary>Estado de un canal; a la UI llega el peor de todos.</summary>
    public void SetChannel(string channel, FeedStatus status, string? message)
    {
        FeedStatus overall;
        string? msg;
        lock (_gate)
        {
            _channels[channel] = (status, message);
            var worst = _channels.Values.MaxBy(v => Rank(v.Status));
            overall = worst.Status;
            msg = overall == FeedStatus.Connected ? _name + " connected" : worst.Message;
            if (overall == _lastStatus && msg == _lastMessage) return;
            _lastStatus = overall;
            _lastMessage = msg;
        }

        _report(overall, msg);
    }

    private static int Rank(FeedStatus s) => s switch
    {
        FeedStatus.Connected => 0,
        FeedStatus.Disconnected => 1,
        FeedStatus.Connecting => 2,
        FeedStatus.Reconnecting => 3,
        _ => 4,
    };

    /// <summary>Espera entre intentos: el primero al toque y después cada vez más (hasta 30 s).</summary>
    public static TimeSpan Backoff(int failures) =>
        failures <= 0 ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failures - 1)));

    public void Dispose() => _cts.Cancel();
}
