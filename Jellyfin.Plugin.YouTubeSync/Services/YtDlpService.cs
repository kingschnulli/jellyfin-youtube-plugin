using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Configuration;
using Jellyfin.Plugin.YouTubeSync.Metadata;
using Jellyfin.Plugin.YouTubeSync.Playback;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Services;

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
        var result = await RunYtDlpJsonAsync(
            new[] { "-f", ManagedPlaybackInputSelector, "-j", "--no-playlist", "--no-cookies", "--extractor-args", "youtube:player_client=visionos,ios", url },
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        var requestedFormats = result["requested_formats"]?.AsArray();
        if (requestedFormats is not null && requestedFormats.Count >= 2)
        {
            var videoFormat = requestedFormats[0];
            var audioFormat = requestedFormats[1];

            var videoUrl = GetString(videoFormat, "url");
            var audioUrl = GetString(audioFormat, "url");

            if (string.IsNullOrWhiteSpace(videoUrl) || string.IsNullOrWhiteSpace(audioUrl))
            {
                _logger.LogWarning("Managed playback input for {VideoId} was missing a URL in requested_formats.", videoId);
                return null;
            }

            _logger.LogInformation("Resolved managed playback input for {VideoId}: separate video+audio", videoId);
            return new ManagedPlaybackInput(
                videoUrl,
                audioUrl,
                BuildHeaderString(videoFormat?["http_headers"]),
                BuildHeaderString(audioFormat?["http_headers"]));
        }

        var combinedUrl = GetString(result, "url");
        if (string.IsNullOrWhiteSpace(combinedUrl))
        {
            _logger.LogWarning("Managed playback input for {VideoId} did not resolve to a usable URL.", videoId);
            return null;
        }

        _logger.LogInformation("Resolved managed playback input for {VideoId}: combined", videoId);
        return new ManagedPlaybackInput(combinedUrl, null, BuildHeaderString(result["http_headers"]));
    }

    /// <summary>
    /// Converts yt-dlp's per-format http_headers JSON object into an ffmpeg-compatible
    /// CRLF-separated header block suitable for the <c>-headers</c> input option.
    /// </summary>
    private static string? BuildHeaderString(JsonNode? headersNode)
    {
        if (headersNode is not JsonObject headersObject || headersObject.Count == 0)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var pair in headersObject)
        {
            string? value;
            try
            {
                value = pair.Value?.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                value = pair.Value?.ToJsonString().Trim('"');
            }

            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            builder.Append(pair.Key).Append(": ").Append(value).Append("\r\n");
        }

        return builder.Length > 0 ? builder.ToString() : null;
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
        int maxEntryScanCount,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "--flat-playlist", "-J" };
        if (maxEntryScanCount > 0)
        {
            args.Add("--playlist-end");
            args.Add(maxEntryScanCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

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
            ThumbnailUrl = GetBestVideoThumbnailUrl(result),
            ChannelName = GetString(result, "channel"),
            PublishedUtc = ParsePublishedDate(result),
            DurationSeconds = durationSeconds
        };
    }

    /// <summary>
    /// Fetches just the published date fields for a video as a lightweight fallback when the full metadata lookup lacks a usable date.
    /// </summary>
    public async Task<DateTime?> GetVideoPublishedDateAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        var result = await RunYtDlpTextAsync(
            new[]
            {
                "--no-playlist",
                "--print", "%(upload_date)s",
                "--print", "%(release_date)s",
                "--print", "%(timestamp)s",
                "--print", "%(release_timestamp)s",
                "--print", "%(release_year)s",
                url
            },
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var lines = result
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return ParsePublishedDateFromLines(lines);
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

        var thumbnailUrl = GetBestSourceAvatarUrl(result);
        var posterUrl = GetBestSourcePosterUrl(result);

        // Detect source type from yt-dlp's extractor key or URL pattern.
        var extractorKey = result["extractor_key"]?.GetValue<string>() ?? string.Empty;
        var isPlaylist = url.Contains("playlist?list=", StringComparison.OrdinalIgnoreCase)
                         || extractorKey.Equals("YoutubePlaylist", StringComparison.OrdinalIgnoreCase);

        return new SourceInfo
        {
            Title = title,
            Description = description,
            ThumbnailUrl = thumbnailUrl,
            PosterUrl = posterUrl,
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

    internal static string GetBestVideoThumbnailUrl(JsonNode? node)
    {
        return SelectThumbnailUrl(node?["thumbnails"]?.AsArray(), ThumbnailPreference.Video);
    }

    internal static string GetBestSourceAvatarUrl(JsonNode? node)
    {
        return SelectThumbnailUrl(node?["thumbnails"]?.AsArray(), ThumbnailPreference.Avatar);
    }

    internal static string GetBestSourcePosterUrl(JsonNode? node)
    {
        var banner = SelectThumbnailUrl(node?["thumbnails"]?.AsArray(), ThumbnailPreference.Banner);
        return string.IsNullOrWhiteSpace(banner) ? GetBestSourceAvatarUrl(node) : banner;
    }

    private static string SelectThumbnailUrl(JsonArray? thumbnails, ThumbnailPreference preference)
    {
        if (thumbnails is null || thumbnails.Count == 0)
        {
            return string.Empty;
        }

        var best = thumbnails
            .Where(t => t is not null)
            .Select(t => new
            {
                Node = t!,
                Width = GetInt(t, "width"),
                Height = GetInt(t, "height"),
                Preference = GetInt(t, "preference"),
                Url = GetString(t, "url"),
                Id = GetString(t, "id")
            })
            .Where(t => !string.IsNullOrWhiteSpace(t.Url))
            .OrderByDescending(t => ScoreThumbnail(t.Width, t.Height, t.Preference, t.Id, t.Url, preference))
            .ThenByDescending(t => t.Preference)
            .ThenByDescending(t => t.Width)
            .FirstOrDefault();

        return best?.Url ?? string.Empty;
    }

    private static double ScoreThumbnail(int width, int height, int sourcePreference, string id, string url, ThumbnailPreference preference)
    {
        var inferredSize = InferThumbnailSize(width, height, id, url, preference);
        width = inferredSize.width;
        height = inferredSize.height;

        if (width <= 0 || height <= 0)
        {
            return sourcePreference;
        }

        var ratio = (double)width / height;
        var area = width * height;
        var idLower = id.ToLowerInvariant();
        var urlLower = url.ToLowerInvariant();

        return preference switch
        {
            ThumbnailPreference.Avatar => (1000 - Math.Abs(ratio - 1.0) * 500) + area / 1000.0 + ScoreId(idLower, urlLower, "avatar", "profile", "channel") - ScoreId(idLower, urlLower, "banner", "hero"),
            ThumbnailPreference.Banner => (1000 - Math.Abs(ratio - 1.77) * 300) + area / 1000.0 + ScoreId(idLower, urlLower, "banner", "hero", "header") - ScoreId(idLower, urlLower, "avatar", "profile"),
            _ => (1000 - Math.Abs(ratio - 1.77) * 250) + area / 1000.0 + ScoreId(idLower, urlLower, "mq", "hq", "sd", "maxres", "hq720", "video") - ScoreId(idLower, urlLower, "avatar", "profile", "banner")
        };
    }

    private static (int width, int height) InferThumbnailSize(int width, int height, string id, string url, ThumbnailPreference preference)
    {
        if (width > 0 && height > 0)
        {
            return (width, height);
        }

        var key = string.Concat(id, " ", url).ToLowerInvariant();

        if (key.Contains("maxres"))
        {
            return (1920, 1080);
        }

        if (key.Contains("hq720"))
        {
            return (1280, 720);
        }

        if (key.Contains("sddefault") || key.Contains("sd1") || key.Contains("sd2") || key.Contains("sd3"))
        {
            return (640, 480);
        }

        if (key.Contains("hqdefault") || key.Contains("hq1") || key.Contains("hq2") || key.Contains("hq3"))
        {
            return (480, 360);
        }

        if (key.Contains("mqdefault") || key.Contains("mq1") || key.Contains("mq2") || key.Contains("mq3"))
        {
            return (320, 180);
        }

        if (key.Contains("default") || key.Contains("/0.") || key.Contains("/1.") || key.Contains("/2.") || key.Contains("/3."))
        {
            return (120, 90);
        }

        if (key.Contains("banner_uncropped"))
        {
            return (2560, 424);
        }

        if (key.Contains("avatar_uncropped"))
        {
            return (900, 900);
        }

        return preference switch
        {
            ThumbnailPreference.Avatar => (900, 900),
            ThumbnailPreference.Banner => (2560, 424),
            _ => (0, 0)
        };
    }

    private static double ScoreId(string id, string url, params string[] matches)
    {
        return matches.Any(match => id.Contains(match) || url.Contains(match)) ? 200 : 0;
    }

    private static int GetInt(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<int>() ?? 0; }
        catch (InvalidOperationException) { return 0; }
    }

    internal static DateTime? ParsePublishedDate(JsonNode? node)
    {
        var directDate = ParsePublishedDateValues(
            GetScalarString(node, "upload_date"),
            GetScalarString(node, "release_date"),
            GetScalarString(node, "timestamp"),
            GetScalarString(node, "release_timestamp"),
            GetScalarString(node, "release_year"));

        if (directDate is not null)
        {
            return directDate;
        }

        return null;
    }

    private static DateTime? ParsePublishedDateFromLines(IReadOnlyList<string> lines)
    {
        var values = new string[5];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i < lines.Count ? lines[i] : string.Empty;
        }

        return ParsePublishedDateValues(values[0], values[1], values[2], values[3], values[4]);
    }

    private static DateTime? ParsePublishedDateValues(
        string uploadDate,
        string releaseDate,
        string timestamp,
        string releaseTimestamp,
        string releaseYear)
    {
        foreach (var value in new[] { uploadDate, releaseDate })
        {
            if (value.Length == 8
                && DateTime.TryParseExact(
                    value,
                    "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsedDate))
            {
                return parsedDate;
            }
        }

        foreach (var value in new[] { releaseTimestamp, timestamp })
        {
            if (long.TryParse(value, out var unixTimestamp) && unixTimestamp > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
            }
        }

        if (int.TryParse(releaseYear, out var year) && year > 0)
        {
            return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return null;
    }

    private enum ThumbnailPreference
    {
        Avatar,
        Banner,
        Video
    }

    private static string GetScalarString(JsonNode? node, string key)
    {
        var value = node?[key];
        if (value is null)
        {
            return string.Empty;
        }

        try
        {
            return value.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            return value.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            return value.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            return value.ToJsonString().Trim('"');
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string GetString(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<string>() ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }
}
