using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Wraps yt-dlp invocations. All methods run yt-dlp out-of-process and parse its JSON output.
/// </summary>
public class YtDlpService
{
    private const string BroadCompatibility720pSelector = "b[protocol!*=m3u8][ext=mp4][height=720]/b[protocol!*=m3u8][ext=mp4][height<=720]/b[height=720]/b[height<=720]";
    private const string Balanced1080pSelector = "b[height=1080]/b[height=720]/b[height<=1080]/b[height<=720]/b";
    private const string MaximumQualitySelector = "b";
    private const string ManagedPlaybackInputSelector = "bv*[height<=1080][vcodec^=avc1]+ba[acodec^=mp4a]/bv*[height<=1080]+ba/b[height<=1080]/b";

    private readonly ILogger<YtDlpService> _logger;

    /// <summary>Initializes a new instance of the <see cref="YtDlpService"/> class.</summary>
    public YtDlpService(ILogger<YtDlpService> logger)
    {
        _logger = logger;
    }

    private string YtDlpPath => Plugin.Instance?.Configuration.YtDlpPath ?? "yt-dlp";

    /// <summary>
    /// Returns the final playback URL for a single video using yt-dlp's own format selection.
    /// This may be a direct media URL or an HLS manifest URL.
    /// </summary>
    public async Task<string?> GetPlaybackUrlAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var playbackTarget = GetPlaybackTarget();
        var playbackSelector = GetPlaybackFormatSelector(playbackTarget);
        var result = await RunYtDlpTextAsync(
            new[] { "-f", playbackSelector, "--get-url", "--no-playlist", url },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var lines = result
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            return null;
        }

        var manifestUrl = lines.FirstOrDefault(IsManifestUrl);
        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            _logger.LogInformation(
                "Resolved playback URL for {VideoId}: manifest ({PlaybackTarget})",
                videoId,
                playbackTarget);
            return manifestUrl;
        }

        if (lines.Length > 1)
        {
            _logger.LogWarning(
                "yt-dlp returned multiple playback URLs for {VideoId} without a single manifest-style URL.",
                videoId);
            return null;
        }

        var playbackUrl = lines[0];
        _logger.LogInformation(
            "Resolved playback URL for {VideoId}: {PlaybackKind} ({PlaybackTarget})",
            videoId,
            DescribePlaybackUrl(playbackUrl),
            playbackTarget);
        return playbackUrl;
    }

    /// <summary>
    /// Returns the input URLs used by the managed transcoding pipeline.
    /// Prefers separate AVC video and AAC audio inputs up to 1080p.
    /// </summary>
    public async Task<ManagedPlaybackInput?> GetManagedPlaybackInputAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var result = await RunYtDlpTextAsync(
            new[] { "-f", ManagedPlaybackInputSelector, "--get-url", "--no-playlist", url },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var lines = result
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            return null;
        }

        if (lines.Length == 1)
        {
            _logger.LogInformation("Resolved managed playback input for {VideoId}: combined", videoId);
            return new ManagedPlaybackInput(lines[0], null);
        }

        _logger.LogInformation("Resolved managed playback input for {VideoId}: separate video+audio", videoId);
        return new ManagedPlaybackInput(lines[0], lines[1]);
    }

    /// <summary>Gets the currently configured playback target.</summary>
    public string GetPlaybackTarget()
    {
        var configured = Plugin.Instance?.Configuration.PlaybackTarget;
        return configured switch
        {
            PlaybackTargets.BroadCompatibility720p => PlaybackTargets.BroadCompatibility720p,
            PlaybackTargets.Balanced1080p => PlaybackTargets.Balanced1080p,
            PlaybackTargets.MaximumQuality => PlaybackTargets.MaximumQuality,
            _ => PlaybackTargets.BroadCompatibility720p
        };
    }

    private static string GetPlaybackFormatSelector(string playbackTarget)
    {
        return playbackTarget switch
        {
            PlaybackTargets.Balanced1080p => Balanced1080pSelector,
            PlaybackTargets.MaximumQuality => MaximumQualitySelector,
            _ => BroadCompatibility720pSelector
        };
    }

    /// <summary>
    /// Returns the flat-playlist JSON entry list for a channel or playlist URL.
    /// Each entry contains at minimum <c>id</c>, <c>title</c>, and <c>url</c>.
    /// </summary>
    public async Task<IReadOnlyList<JsonNode>> GetPlaylistEntriesAsync(
        string playlistUrl,
        int videoRetentionDays,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "--flat-playlist", "-J" };
        if (videoRetentionDays > 0)
        {
            args.Add("--dateafter");
            args.Add(DateTime.UtcNow.AddDays(-videoRetentionDays).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add(playlistUrl);

        var result = await RunYtDlpJsonAsync(args, cancellationToken).ConfigureAwait(false);
        var entries = result?["entries"]?.AsArray();
        if (entries is null)
        {
            return Array.Empty<JsonNode>();
        }

        var list = new List<JsonNode>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is not null)
            {
                list.Add(entry);
            }
        }

        return list;
    }

    /// <summary>
    /// Fetches full metadata for a single YouTube video.
    /// </summary>
    public async Task<VideoMetadata?> GetVideoMetadataAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var result = await RunYtDlpJsonAsync(new[] { "-J", "--no-playlist", url }, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        var uploadDate = GetString(result, "upload_date");
        DateTime? publishedUtc = null;
        if (uploadDate.Length == 8
            && DateTime.TryParseExact(
                uploadDate,
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsedDate))
        {
            publishedUtc = parsedDate;
        }

        int? durationSeconds = null;
        try
        {
            durationSeconds = result["duration"]?.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
        }

        return new VideoMetadata
        {
            VideoId = GetString(result, "id"),
            Title = GetString(result, "title"),
            Description = GetString(result, "description"),
            ThumbnailUrl = GetBestThumbnailUrl(result),
            ChannelName = GetString(result, "channel"),
            PublishedUtc = publishedUtc,
            DurationSeconds = durationSeconds
        };
    }

    /// <summary>
    /// Fetches metadata (title, description, thumbnail URL, detected type) for a channel or playlist URL.
    /// Only the first playlist entry is requested so the call is fast.
    /// </summary>
    public async Task<SourceInfo?> GetSourceInfoAsync(string url, CancellationToken cancellationToken)
    {
        // --playlist-end 1 limits video retrieval but the container metadata is always present.
        var args = new[] { "--flat-playlist", "-J", "--playlist-end", "1", url };
        var result = await RunYtDlpJsonAsync(args, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        var title = result["title"]?.GetValue<string>() ?? string.Empty;
        var description = result["description"]?.GetValue<string>() ?? string.Empty;

        // Pick the thumbnail with the greatest width (best quality).
        var thumbnailUrl = string.Empty;
        var thumbnails = result["thumbnails"]?.AsArray();
        if (thumbnails is { Count: > 0 })
        {
            var best = thumbnails
                .Where(t => t is not null)
                .OrderByDescending(t =>
                {
                    try { return t!["width"]?.GetValue<int>() ?? 0; }
                    catch (InvalidOperationException) { return 0; }
                })
                .FirstOrDefault();
            thumbnailUrl = best?["url"]?.GetValue<string>() ?? string.Empty;
        }

        // Detect source type from yt-dlp's extractor key or URL pattern.
        var extractorKey = result["extractor_key"]?.GetValue<string>() ?? string.Empty;
        var isPlaylist = url.Contains("playlist?list=", StringComparison.OrdinalIgnoreCase)
                         || extractorKey.Equals("YoutubePlaylist", StringComparison.OrdinalIgnoreCase);

        return new SourceInfo
        {
            Title = title,
            Description = description,
            ThumbnailUrl = thumbnailUrl,
            Type = isPlaylist ? SourceType.Playlist : SourceType.Channel
        };
    }

    private async Task<JsonNode?> RunYtDlpJsonAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogDebug("Running yt-dlp with arguments: {Arguments}", string.Join(" ", psi.ArgumentList));

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogError(
                    "yt-dlp exited with code {ExitCode}. Stderr: {Error}",
                    process.ExitCode,
                    error);
                return null;
            }

            return JsonNode.Parse(output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run yt-dlp");
            return null;
        }
    }

    private async Task<string?> RunYtDlpTextAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogDebug("Running yt-dlp with arguments: {Arguments}", string.Join(" ", psi.ArgumentList));

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogError(
                    "yt-dlp exited with code {ExitCode}. Stderr: {Error}",
                    process.ExitCode,
                    error);
                return null;
            }

            return output.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run yt-dlp");
            return null;
        }
    }

    private static string DescribePlaybackUrl(string playbackUrl)
    {
        if (IsManifestUrl(playbackUrl))
        {
            return "manifest";
        }

        return "direct media";
    }

    private static bool IsManifestUrl(string playbackUrl)
        => playbackUrl.Contains("manifest.googlevideo.com", StringComparison.OrdinalIgnoreCase)
           || playbackUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
           || playbackUrl.Contains("/api/manifest/", StringComparison.OrdinalIgnoreCase)
           || playbackUrl.Contains(".mpd", StringComparison.OrdinalIgnoreCase);

    private static string GetBestThumbnailUrl(JsonNode? node)
    {
        var direct = GetString(node, "thumbnail");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        var thumbnails = node?["thumbnails"]?.AsArray();
        if (thumbnails is null || thumbnails.Count == 0)
        {
            return string.Empty;
        }

        var best = thumbnails
            .Where(t => t is not null)
            .OrderByDescending(t =>
            {
                try { return t!["width"]?.GetValue<int>() ?? 0; }
                catch (InvalidOperationException) { return 0; }
            })
            .FirstOrDefault();

        return GetString(best, "url");
    }

    private static string GetString(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<string>() ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }
}
