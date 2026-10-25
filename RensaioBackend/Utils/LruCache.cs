using System.Collections.Generic;

namespace RensaioBackend.Utils;

/// <summary>
/// A small, thread-safe, bounded least-recently-used cache.
///
/// Reads and writes are O(1): the dictionary gives lookups while the linked list tracks recency.
/// When <see cref="Capacity"/> is exceeded the least-recently-used entry is evicted. Intended for
/// cheap scalar mappings (e.g. url → resolved url) where an unbounded cache would grow forever but
/// a full <c>IMemoryCache</c> with expiry is unnecessary.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();

    private readonly struct Entry
    {
        public Entry(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }

        public TKey Key { get; }
        public TValue Value { get; }
    }

    public LruCache(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(capacity);
    }

    public int Capacity => _capacity;

    /// <summary>Number of entries currently cached.</summary>
    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>Returns the cached value for <paramref name="key"/> and promotes it to most-recent.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = default!;
            return false;
        }
    }

    /// <summary>Inserts or updates <paramref name="key"/>, evicting the least-recently-used entry when full.</summary>
    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                node.Value = new Entry(key, value);
                _order.AddFirst(node);
                return;
            }

            var added = _order.AddFirst(new Entry(key, value));
            _map[key] = added;

            if (_map.Count > _capacity)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _map.Remove(lru.Value.Key);
            }
        }
    }
}
