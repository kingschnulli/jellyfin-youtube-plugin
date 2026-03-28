using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Builds and caches merged DASH files on disk so Jellyfin can read them as normal seekable files.
/// </summary>
public class DashMergeCacheService
{
    private readonly YtDlpService _ytDlpService;
    private readonly ILogger<DashMergeCacheService> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="DashMergeCacheService"/> class.</summary>
    public DashMergeCacheService(YtDlpService ytDlpService, ILogger<DashMergeCacheService> logger)
    {
        _ytDlpService = ytDlpService;
        _logger = logger;
    }

    /// <summary>
    /// Returns a local merged MP4 file for the requested DASH pair, creating it if needed.
    /// </summary>
    public async Task<string?> GetOrCreateAsync(
        string videoId,
        string videoUrl,
        string audioUrl,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(videoId);
        CleanupExpiredFiles();

        if (IsUsable(cachePath))
        {
            _logger.LogDebug("Reusing cached DASH merge for video {VideoId}: {CachePath}", videoId, cachePath);
            Touch(cachePath);
            return cachePath;
        }

        var lazyTask = _inflight.GetOrAdd(
            videoId,
            _ => new Lazy<Task<string?>>(
                () => CreateMergedFileAsync(videoId, videoUrl, audioUrl, cachePath, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazyTask.Value.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(videoId, out _);
        }
    }

    private async Task<string?> CreateMergedFileAsync(
        string videoId,
        string videoUrl,
        string audioUrl,
        string cachePath,
        CancellationToken cancellationToken)
    {
        if (IsUsable(cachePath))
        {
            Touch(cachePath);
            return cachePath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        var tempPath = cachePath + ".tmp.mp4";
        TryDeleteFile(tempPath);

        _logger.LogInformation("Creating cached DASH merge for video {VideoId} at {CachePath}", videoId, cachePath);

        var success = await _ytDlpService.MergeDashToFileAsync(videoUrl, audioUrl, tempPath, cancellationToken)
            .ConfigureAwait(false);
        if (!success)
        {
            TryDeleteFile(tempPath);
            return null;
        }

        TryDeleteFile(cachePath);
        File.Move(tempPath, cachePath, overwrite: true);
        Touch(cachePath);

        return cachePath;
    }

    private void CleanupExpiredFiles()
    {
        var cacheDirectory = GetCacheDirectory();
        if (!Directory.Exists(cacheDirectory))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(cacheDirectory, "*.mp4"))
        {
            try
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
                if (DateTime.UtcNow - lastWriteUtc > GetTtl())
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete expired DASH cache file {CachePath}", filePath);
            }
        }
    }

    private static bool IsUsable(string filePath)
        => File.Exists(filePath) && new FileInfo(filePath).Length > 0;

    private static void Touch(string filePath)
    {
        var now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(filePath, now);
        File.SetLastAccessTimeUtc(filePath, now);
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
        }
    }

    private static string GetCacheDirectory()
        => Path.Combine(Path.GetTempPath(), "Jellyfin.YouTubeSync", "dash-cache");

    private static string GetCachePath(string videoId)
        => Path.Combine(GetCacheDirectory(), $"{videoId}.mp4");

    private static TimeSpan GetTtl()
    {
        var cacheMinutes = Plugin.Instance?.Configuration.CacheMinutes ?? 5;
        return TimeSpan.FromMinutes(Math.Max(cacheMinutes, 30));
    }
}