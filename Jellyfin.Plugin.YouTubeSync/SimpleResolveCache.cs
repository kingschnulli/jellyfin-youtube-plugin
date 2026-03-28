using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Simple in-memory cache for resolved YouTube CDN URLs.
/// Each entry expires after a configurable number of minutes.
/// </summary>
public class SimpleResolveCache
{
    private sealed record CacheEntry(string Url, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Tries to retrieve a cached URL for the given video ID.
    /// Returns <c>false</c> (and sets <paramref name="url"/> to <c>null</c>) when the entry is absent or expired.
    /// </summary>
    public bool TryGet(string videoId, out string? url)
    {
        if (_cache.TryGetValue(videoId, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
        {
            url = entry.Url;
            return true;
        }

        url = null;
        return false;
    }

    /// <summary>Stores a resolved URL in the cache with the given TTL in minutes.</summary>
    public void Set(string videoId, string url, int minutes)
    {
        _cache[videoId] = new CacheEntry(url, DateTime.UtcNow.AddMinutes(minutes));
    }
}

