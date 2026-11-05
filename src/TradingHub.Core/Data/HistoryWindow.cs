using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>A bounded, last-writer-wins source window. The heap stores indices, never copies of retained trades.</summary>
internal sealed class HistoryWindow(int capacity)
{
    private readonly List<Trade> _rows = new(Math.Min(Math.Max(0, capacity), 4096));
    private Dictionary<TradeIdentity, int>? _indices;
    private int[]? _heap, _positions;
    private int _heapCount;
    private long _lastTime = long.MinValue, _lastId = long.MinValue;
    private bool _ordered = true;
    public bool Truncated { get; private set; }

    private static TradeIdentity Key(in Trade row) => TradeIdentity.From(row);
    private static int Compare(in Trade left, in Trade right)
    {
        int order = left.Time.CompareTo(right.Time);
        return order != 0 ? order : left.Id.CompareTo(right.Id);
    }

    public void Observe(in Trade row, bool include)
    {
        if (_indices == null && (!string.IsNullOrEmpty(row.Provenance.SourceEventId) || row.Time.Ticks < _lastTime || row.Id <= _lastId))
        {
            _indices = new(_rows.Count);
            for (int i = 0; i < _rows.Count; i++) _indices[Key(_rows[i])] = i;
        }
        _lastTime = row.Time.Ticks; _lastId = row.Id;
        if (_indices != null && _indices.TryGetValue(Key(row), out int existing))
        {
            if (!include) Remove(existing);
            else
            {
                bool moved = _rows[existing].Time != row.Time;
                _rows[existing] = row;
                if (moved) { _ordered = false; if (_heap != null) Fix(_positions![existing]); }
            }
            return;
        }
        if (!include) return;
        if (capacity <= 0) { Truncated = true; return; }
        if (_rows.Count < capacity)
        {
            int slot = _rows.Count;
            if (slot > 0 && Compare(_rows[^1], row) > 0) _ordered = false;
            _rows.Add(row);
            _indices?.Add(Key(row), slot);
            if (_heap != null)
            {
                _heap[_heapCount] = slot; _positions![slot] = _heapCount++;
                Up(_heapCount - 1);
            }
            return;
        }
        Truncated = true;
        EnsureHeap();
        int oldest = _heap![0];
        if (Compare(row, _rows[oldest]) <= 0) return;
        _indices?.Remove(Key(_rows[oldest]));
        _rows[oldest] = row;
        _indices?.Add(Key(row), oldest);
        _ordered = false;
        Down(0);
    }

    public List<Trade> Finish()
    {
        if (!_ordered) _rows.Sort(static (left, right) => Compare(left, right));
        return _rows;
    }

    private void Remove(int slot)
    {
        _indices!.Remove(Key(_rows[slot]));
        if (_heap != null)
        {
            int position = _positions![slot], replacement = _heap[--_heapCount];
            if (position < _heapCount) { _heap[position] = replacement; _positions[replacement] = position; Fix(position); }
        }
        int last = _rows.Count - 1;
        if (slot != last)
        {
            _rows[slot] = _rows[last]; _indices[Key(_rows[slot])] = slot;
            if (_heap != null)
            {
                int position = _positions![last]; _heap[position] = slot; _positions[slot] = position;
            }
            _ordered = false;
        }
        _rows.RemoveAt(last);
    }

    private void EnsureHeap()
    {
        if (_heap != null) return;
        _heapCount = _rows.Count;
        _heap = new int[capacity]; _positions = new int[capacity];
        for (int i = 0; i < _heapCount; i++) { _heap[i] = i; _positions[i] = i; }
        for (int i = _heapCount / 2 - 1; i >= 0; i--) Down(i);
    }

    private void Fix(int position)
    {
        int parent = (position - 1) / 2;
        if (position > 0 && Compare(_rows[_heap![position]], _rows[_heap[parent]]) < 0) Up(position);
        else Down(position);
    }
    private void Up(int position)
    {
        while (position > 0)
        {
            int parent = (position - 1) / 2;
            if (Compare(_rows[_heap![parent]], _rows[_heap[position]]) <= 0) break;
            Swap(parent, position); position = parent;
        }
    }
    private void Down(int position)
    {
        while (position * 2 + 1 < _heapCount)
        {
            int child = position * 2 + 1;
            if (child + 1 < _heapCount && Compare(_rows[_heap![child + 1]], _rows[_heap[child]]) < 0) child++;
            if (Compare(_rows[_heap![position]], _rows[_heap[child]]) <= 0) break;
            Swap(position, child); position = child;
        }
    }
    private void Swap(int left, int right)
    {
        (_heap![left], _heap[right]) = (_heap[right], _heap[left]);
        _positions![_heap[left]] = left; _positions[_heap[right]] = right;
    }
}
