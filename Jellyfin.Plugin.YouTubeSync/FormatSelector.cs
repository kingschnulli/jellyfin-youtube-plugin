using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Selects the best Jellyfin-compatible (progressive, ≤1080p) format
/// from a yt-dlp JSON response.
///
/// Selection uses a tiered fallback strategy:
///   Tier 1: Progressive stream ≤1080p  – highest resolution, then MP4-preferred, then highest bitrate.
///   Tier 2: Any progressive stream     – last resort when no ≤1080p progressive stream exists.
///   DASH-only (split video/audio) streams are always rejected.
///
/// MP4 is preferred as a secondary tiebreaker (same resolution), but a higher-resolution
/// non-MP4 stream (e.g. H264/ADTS in a TS container) is always preferred over a lower-resolution MP4.
/// </summary>
public class FormatSelector
{
    private readonly ILogger<FormatSelector> _logger;

    /// <summary>Initializes a new instance of the <see cref="FormatSelector"/> class.</summary>
    public FormatSelector(ILogger<FormatSelector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the direct CDN URL for the best progressive format, or <c>null</c> when
    /// no compatible progressive format exists (i.e. only split DASH streams are available).
    /// </summary>
    public string? SelectBestFormat(JsonNode videoInfo)
    {
        var formats = videoInfo["formats"]?.AsArray();
        if (formats is null || formats.Count == 0)
        {
            _logger.LogWarning("yt-dlp response contains no 'formats' array.");
            return null;
        }

        // Tier 1: best progressive stream ≤1080p – highest resolution wins; MP4 preferred at equal resolution.
        var best = PickBest(formats, maxHeight: 1080)
            // Tier 2: any progressive stream – fallback when no ≤1080p progressive stream exists.
            ?? PickBest(formats, maxHeight: null);

        if (best is null)
        {
            _logger.LogInformation(
                "No progressive format found. "
                + "Only DASH or split streams are available. "
                + "DASH proxy is not supported in v1.");
            return null;
        }

        var url = best["url"]?.GetValue<string>();
        _logger.LogDebug(
            "Selected format: id={FormatId} height={Height} tbr={Tbr}",
            GetString(best, "format_id"),
            GetInt(best, "height"),
            GetDouble(best, "tbr"));

        return url;
    }

    private static JsonNode? PickBest(JsonArray formats, int? maxHeight)
    {
        var query = formats
            .Where(f => f is not null)
            .Where(IsProgressive);

        if (maxHeight.HasValue)
        {
            var limit = maxHeight.Value;
            query = query.Where(f => GetInt(f, "height") <= limit);
        }

        return query
            .OrderByDescending(f => GetInt(f, "height"))
            .ThenByDescending(f => IsMp4(f) ? 1 : 0)
            .ThenByDescending(f => GetDouble(f, "tbr"))
            .FirstOrDefault();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsProgressive(JsonNode? f)
    {
        var vcodec = GetString(f, "vcodec");
        var acodec = GetString(f, "acodec");
        return vcodec != "none" && vcodec.Length > 0
            && acodec != "none" && acodec.Length > 0;
    }

    private static bool IsMp4(JsonNode? f)
        => GetString(f, "ext") == "mp4";

    private static string GetString(JsonNode? node, string key)
    {
        try
        {
            return node?[key]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetInt(JsonNode? node, string key)
    {
        try
        {
            return node?[key]?.GetValue<int>() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double GetDouble(JsonNode? node, string key)
    {
        try
        {
            return node?[key]?.GetValue<double>() ?? 0d;
        }
        catch
        {
            return 0d;
        }
    }
}
