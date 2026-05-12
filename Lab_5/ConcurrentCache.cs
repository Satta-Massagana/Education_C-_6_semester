using System.Collections.Concurrent;

namespace Lab5.ConcurrentCollections;

public sealed class CacheItem
{
    public required object Value { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public TimeSpan ExpirationTime { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed class ConcurrentCache
{
    public ConcurrentDictionary<string, ConcurrentBag<CacheItem>> Cache { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void AddToCache(string key, object value, TimeSpan? expirationTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        ConcurrentBag<CacheItem> bag = Cache.GetOrAdd(key, _ => new ConcurrentBag<CacheItem>());
        bag.Add(
            new CacheItem
            {
                Value = value,
                Timestamp = DateTime.UtcNow,
                ExpirationTime = expirationTime ?? TimeSpan.FromMinutes(5),
            }
        );
    }

    public bool TryGetFromCache(string key, out object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (Cache.TryGetValue(key, out ConcurrentBag<CacheItem>? bag))
        {
            DateTime now = DateTime.UtcNow;
            CacheItem? lastValid = bag.Where(item => now - item.Timestamp <= item.ExpirationTime)
                .OrderByDescending(item => item.Timestamp)
                .FirstOrDefault();

            if (lastValid is not null)
            {
                value = lastValid.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    public bool RemoveFromCache(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Cache.TryRemove(key, out _);
    }

    public void ClearCache()
    {
        Cache.Clear();
    }

    public int GetCacheSize()
    {
        return Cache.Sum(pair => pair.Value.Count);
    }
}
