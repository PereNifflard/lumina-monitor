namespace LuminaMonitor.Formats;

/// <summary>
/// Most-recently-used cache of decompressed block runs, keyed by run index.
/// </summary>
/// <remarks>
/// Bounded by a byte budget rather than an entry count because run sizes
/// vary (hdiutil's 1 MiB is common, other tools go larger); the newest entry
/// is always kept so a run bigger than the whole budget still serves the
/// read that fetched it.
/// </remarks>
internal sealed class RunCache(long budget)
{
    private readonly Dictionary<int, LinkedListNode<(int Key, byte[] Data)>> _map = new();
    private readonly LinkedList<(int Key, byte[] Data)> _order = new();
    private long _used;

    public bool TryGet(int key, out byte[] data)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            data = node.Value.Data;
            return true;
        }
        data = [];
        return false;
    }

    public void Add(int key, byte[] data)
    {
        _map[key] = _order.AddFirst((key, data));
        _used += data.Length;
        while (_used > budget && _order.Count > 1)
        {
            var oldest = _order.Last!;
            _order.RemoveLast();
            _map.Remove(oldest.Value.Key);
            _used -= oldest.Value.Data.Length;
        }
    }
}
