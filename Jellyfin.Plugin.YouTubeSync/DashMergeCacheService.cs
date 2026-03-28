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
    private const int ReadyByteThreshold = 512 * 1024;
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly YtDlpService _ytDlpService;
    private readonly ILogger<DashMergeCacheService> _logger;
    private readonly ConcurrentDictionary<string, DashMergeSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="DashMergeCacheService"/> class.</summary>
    public DashMergeCacheService(YtDlpService ytDlpService, ILogger<DashMergeCacheService> logger)
    {
        _ytDlpService = ytDlpService;
        _logger = logger;
    }

    /// <summary>
    /// Returns a local DASH merge session, creating it if needed.
    /// </summary>
    public async Task<DashMergeLease?> AcquireAsync(
        string videoId,
        string videoUrl,
        string audioUrl,
        CancellationToken cancellationToken)
    {
        CleanupExpiredFiles();

        var session = _sessions.GetOrAdd(
            videoId,
            _ => CreateSession(videoId, videoUrl, audioUrl));

        try
        {
            var ready = await session.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                if (session.MergeTask.IsCompleted)
                {
                    _sessions.TryRemove(videoId, out _);
                }

                return null;
            }

            session.AddReader();
            Touch(session.FilePath);
            return new DashMergeLease(this, session);
        }
        catch
        {
            if (session.MergeTask.IsCompleted)
            {
                _sessions.TryRemove(videoId, out _);
            }

            throw;
        }
    }

    private void Release(DashMergeSession session)
    {
        session.RemoveReader();

        if (session.ReaderCount == 0 && !session.MergeTask.IsCompleted)
        {
            _logger.LogInformation(
                "No active readers remain for in-progress DASH merge {VideoId}; cancelling merge.",
                session.VideoId);
            session.Cancel();
        }

        if (session.MergeTask.IsCompleted && session.ReaderCount == 0)
        {
            _sessions.TryRemove(session.VideoId, out _);
        }
    }

    private DashMergeSession CreateSession(string videoId, string videoUrl, string audioUrl)
    {
        var cachePath = GetCachePath(videoId);
        TryDeleteFile(cachePath);
        var cts = new CancellationTokenSource();

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        _logger.LogInformation("Creating growing DASH merge for video {VideoId} at {CachePath}", videoId, cachePath);

        var mergeTask = RunMergeAsync(videoId, videoUrl, audioUrl, cachePath, cts.Token);
        return new DashMergeSession(videoId, cachePath, mergeTask, cts);
    }

    private async Task<bool> RunMergeAsync(
        string videoId,
        string videoUrl,
        string audioUrl,
        string cachePath,
        CancellationToken cancellationToken)
    {
        var success = await _ytDlpService.MergeDashToFileAsync(videoUrl, audioUrl, cachePath, cancellationToken)
            .ConfigureAwait(false);
        if (!success)
        {
            TryDeleteFile(cachePath);
            return false;
        }

        Touch(cachePath);
        _logger.LogInformation("Completed DASH merge for video {VideoId} at {CachePath}", videoId, cachePath);
        return true;
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
            if (IsActiveSessionFile(filePath))
            {
                continue;
            }

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

    private bool IsActiveSessionFile(string filePath)
    {
        foreach (var session in _sessions.Values)
        {
            if (string.Equals(session.FilePath, filePath, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
        return TimeSpan.FromMinutes(Math.Max(cacheMinutes, 10));
    }

    /// <summary>
    /// Represents an acquired DASH merge file that should be released when the response ends.
    /// </summary>
    public sealed class DashMergeLease : IAsyncDisposable
    {
        private readonly DashMergeCacheService _owner;
        private readonly DashMergeSession _session;
        private bool _disposed;

        internal DashMergeLease(DashMergeCacheService owner, DashMergeSession session)
        {
            _owner = owner;
            _session = session;
        }

        /// <summary>Gets the local file path being written/read.</summary>
        public string FilePath => _session.FilePath;

        /// <summary>Gets the background merge task.</summary>
        public Task<bool> MergeTask => _session.MergeTask;

        /// <summary>Gets a value indicating whether the merge is already complete.</summary>
        public bool IsComplete => _session.MergeTask.IsCompletedSuccessfully && _session.MergeTask.Result;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _owner.Release(_session);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DashMergeSession
    {
        private int _readerCount;

        public DashMergeSession(string videoId, string filePath, Task<bool> mergeTask, CancellationTokenSource cts)
        {
            VideoId = videoId;
            FilePath = filePath;
            MergeTask = mergeTask;
            CancellationTokenSource = cts;
        }

        public string VideoId { get; }

        public string FilePath { get; }

        public Task<bool> MergeTask { get; }

        public CancellationTokenSource CancellationTokenSource { get; }

        public int ReaderCount => _readerCount;

        public void AddReader() => Interlocked.Increment(ref _readerCount);

        public void RemoveReader() => Interlocked.Decrement(ref _readerCount);

        public void Cancel() => CancellationTokenSource.Cancel();

        public async Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            var start = DateTime.UtcNow;

            while (DateTime.UtcNow - start < ReadyTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (File.Exists(FilePath) && new FileInfo(FilePath).Length >= ReadyByteThreshold)
                {
                    return true;
                }

                if (MergeTask.IsCompleted)
                {
                    return File.Exists(FilePath) && new FileInfo(FilePath).Length > 0 && await MergeTask.ConfigureAwait(false);
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            return File.Exists(FilePath) && new FileInfo(FilePath).Length > 0;
        }
    }
}