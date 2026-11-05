namespace TradingHub.App.Services;

/// <summary>
/// Pins confirmed private material independently of bounded historical/preview material.
/// Readers use one immutable snapshot without locking or accessing the private store.
/// </summary>
internal sealed class NotificationRedactor
{
    private const int MaximumRemembered = 64;
    private readonly object _gate = new();
    private readonly List<string> _remembered = new(MaximumRemembered);
    private HashSet<string> _active = new(StringComparer.Ordinal);
    private string[] _snapshot = Array.Empty<string>();

    internal void SetActive(IEnumerable<NotificationCredential> confirmedCredentials)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var credential in confirmedCredentials)
        {
            string authorization = credential.Authorization ?? "";
            Add(active, authorization);
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) Add(active, authorization[7..]);
            if (credential.FullWebhookUri is { Length: > 0 } target)
            {
                Add(active, target);
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri)) Add(active, uri.Authority);
            }
        }
        lock (_gate)
        {
            if (_active.SetEquals(active)) return;
            _active = active;
            PublishLocked();
        }
    }

    internal void Remember(NotificationCredential historicalOrPreviewCredential)
    {
        lock (_gate)
        {
            string authorization = historicalOrPreviewCredential.Authorization ?? "";
            bool changed = RetainLocked(authorization);
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) changed |= RetainLocked(authorization[7..]);
            if (historicalOrPreviewCredential.FullWebhookUri is { Length: > 0 } target)
            {
                changed |= RetainLocked(target);
                if (Uri.TryCreate(target, UriKind.Absolute, out var uri)) changed |= RetainLocked(uri.Authority);
            }
            if (changed) PublishLocked();
        }
    }

    internal AlertEvent Redact(AlertEvent alert)
    {
        string[] material = Volatile.Read(ref _snapshot);
        if (material.Length == 0) return alert;
        string id = alert.Id;
        foreach (string secret in material)
            if (id.Contains(secret, StringComparison.Ordinal))
            {
                id = NotificationPayload.OpaqueId(id);
                break;
            }
        string instrument = Clean(alert.Instrument, material), source = Clean(alert.Source, material), message = Clean(alert.Message, material);
        return id == alert.Id && instrument == alert.Instrument && source == alert.Source && message == alert.Message
            ? alert : alert with { Id = id, Instrument = instrument, Source = source, Message = message };
    }

    internal string Bounded(string? value, int length)
    {
        // Redact before truncation, including credentials longer than the producer's field limit.
        string safe = Clean(value ?? "", Volatile.Read(ref _snapshot));
        return safe.Length <= length ? safe : safe[..length];
    }

    private static void Add(HashSet<string> material, string value)
    {
        if (value.Length > 0) material.Add(value);
    }

    private bool RetainLocked(string value)
    {
        if (value.Length == 0) return false;
        int index = _remembered.IndexOf(value);
        if (index == _remembered.Count - 1 && index >= 0) return false;
        if (index >= 0) _remembered.RemoveAt(index);
        else if (_remembered.Count == MaximumRemembered) _remembered.RemoveAt(0);
        _remembered.Add(value);
        return index < 0;
    }

    private void PublishLocked()
    {
        int count = _active.Count;
        foreach (string value in _remembered)
            if (!_active.Contains(value)) count++;
        if (count == 0)
        {
            Volatile.Write(ref _snapshot, Array.Empty<string>());
            return;
        }
        var snapshot = new string[count];
        _active.CopyTo(snapshot);
        int index = _active.Count;
        foreach (string value in _remembered)
            if (!_active.Contains(value)) snapshot[index++] = value;
        Array.Sort(snapshot, static (left, right) =>
        {
            int byLength = right.Length.CompareTo(left.Length);
            return byLength != 0 ? byLength : StringComparer.Ordinal.Compare(left, right);
        });
        Volatile.Write(ref _snapshot, snapshot);
    }

    private static string Clean(string text, string[] material)
    {
        foreach (string secret in material) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }
}
