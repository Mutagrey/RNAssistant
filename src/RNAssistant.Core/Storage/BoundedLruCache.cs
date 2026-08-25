using System;
using System.Collections.Generic;

namespace RNAssistant.Core.Storage
{
    internal sealed class BoundedLruCache<TValue>
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries;
        private readonly int _maximumEntries;
        private readonly long _maximumEntryWeight;
        private readonly long _maximumTotalWeight;
        private readonly Func<TValue, long> _measure;
        private long _clock;
        private long _totalWeight;

        public BoundedLruCache(
            int maximumEntries,
            long maximumEntryWeight,
            long maximumTotalWeight,
            Func<TValue, long> measure,
            IEqualityComparer<string> comparer)
        {
            if (maximumEntries <= 0) throw new ArgumentOutOfRangeException("maximumEntries");
            if (maximumEntryWeight < 0) throw new ArgumentOutOfRangeException("maximumEntryWeight");
            if (maximumTotalWeight < 0) throw new ArgumentOutOfRangeException("maximumTotalWeight");
            _measure = measure ?? throw new ArgumentNullException("measure");
            _maximumEntries = maximumEntries;
            _maximumEntryWeight = maximumEntryWeight;
            _maximumTotalWeight = maximumTotalWeight;
            _entries = new Dictionary<string, Entry>(comparer ?? StringComparer.Ordinal);
        }

        public bool TryGet(string key, out TValue value)
        {
            value = default(TValue);
            if (string.IsNullOrWhiteSpace(key)) return false;
            lock (_sync)
            {
                Entry entry;
                if (!_entries.TryGetValue(key, out entry)) return false;
                entry.LastAccess = ++_clock;
                value = entry.Value;
                return true;
            }
        }

        public void Set(string key, TValue value)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Cache key is required.", "key");
            var weight = Math.Max(0L, _measure(value));
            lock (_sync)
            {
                RemoveLocked(key);
                if (weight > _maximumEntryWeight || weight > _maximumTotalWeight) return;
                _entries[key] = new Entry
                {
                    Value = value,
                    Weight = weight,
                    LastAccess = ++_clock
                };
                _totalWeight += weight;
                TrimLocked();
            }
        }

        public void Remove(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            lock (_sync) RemoveLocked(key);
        }

        public void Move(string oldKey, string newKey)
        {
            if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey)) return;
            lock (_sync)
            {
                Entry entry;
                if (!_entries.TryGetValue(oldKey, out entry)) return;
                if (_entries.Comparer.Equals(oldKey, newKey))
                {
                    entry.LastAccess = ++_clock;
                    return;
                }
                RemoveLocked(newKey);
                _entries.Remove(oldKey);
                entry.LastAccess = ++_clock;
                _entries[newKey] = entry;
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _entries.Clear();
                _totalWeight = 0;
            }
        }

        private void TrimLocked()
        {
            while (_entries.Count > _maximumEntries || _totalWeight > _maximumTotalWeight)
            {
                string oldestKey = null;
                var oldestAccess = long.MaxValue;
                foreach (var item in _entries)
                {
                    if (item.Value.LastAccess >= oldestAccess) continue;
                    oldestKey = item.Key;
                    oldestAccess = item.Value.LastAccess;
                }
                if (oldestKey == null) return;
                RemoveLocked(oldestKey);
            }
        }

        private void RemoveLocked(string key)
        {
            Entry entry;
            if (!_entries.TryGetValue(key, out entry)) return;
            _totalWeight -= entry.Weight;
            _entries.Remove(key);
        }

        private sealed class Entry
        {
            public TValue Value { get; set; }
            public long Weight { get; set; }
            public long LastAccess { get; set; }
        }
    }
}
