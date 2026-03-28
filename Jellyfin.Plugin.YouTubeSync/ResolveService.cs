using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Orchestrates the resolution of a YouTube video ID to a playable stream strategy.
/// Results are stored in <see cref="SimpleResolveCache"/> to avoid calling yt-dlp on every request.
/// </summary>
public class ResolveService
{
    private readonly YtDlpService _ytDlpService;
    private readonly FormatSelector _formatSelector;
    private readonly SimpleResolveCache _cache;
    private readonly ILogger<ResolveService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ResolveService"/> class.</summary>
    public ResolveService(
        YtDlpService ytDlpService,
        FormatSelector formatSelector,
        SimpleResolveCache cache,
        ILogger<ResolveService> logger)
    {
        _ytDlpService = ytDlpService;
        _formatSelector = formatSelector;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a YouTube video ID to either a direct redirect URL or a DASH merge pair.
    /// Returns <c>null</c> if resolution fails or no compatible format is available.
    /// </summary>
    public async Task<PlaybackResolveResult?> ResolveAsync(string videoId, CancellationToken cancellationToken)
    {
        if (_cache.TryGet(videoId, out var cached))
        {
            _logger.LogDebug("Cache hit for video {VideoId}", videoId);
            return cached;
        }

        _logger.LogInformation("Resolving video {VideoId} via yt-dlp", videoId);

        var playbackUrl = await _ytDlpService.GetPlaybackUrlAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(playbackUrl))
        {
            var directResult = PlaybackResolveResult.Redirect(playbackUrl);
            var directCacheMinutes = Plugin.Instance?.Configuration.CacheMinutes ?? 5;
            _cache.Set(videoId, directResult, directCacheMinutes);

            _logger.LogInformation(
                "Resolved {VideoId} via direct playback URL – cached for {Minutes} min",
                videoId,
                directCacheMinutes);

            return directResult;
        }

        var info = await _ytDlpService.GetVideoInfoAsync(videoId, cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            _logger.LogWarning("yt-dlp returned no info for video {VideoId}", videoId);
            return null;
        }

        var result = _formatSelector.SelectBestFormat(info);
        if (result is null)
        {
            _logger.LogWarning(
                "No compatible progressive or DASH fallback format found for video {VideoId}.",
                videoId);
            return null;
        }

        var cacheMinutes = Plugin.Instance?.Configuration.CacheMinutes ?? 5;
        _cache.Set(videoId, result, cacheMinutes);

        _logger.LogInformation(
            "Resolved {VideoId} – cached for {Minutes} min",
            videoId,
            cacheMinutes);

        return result;
    }
}

