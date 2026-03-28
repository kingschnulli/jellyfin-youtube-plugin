using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        LogAvailableFormats(formats);

        // Tier 1: best progressive stream ≤1080p – highest resolution wins; MP4 preferred at equal resolution.
        var best = PickBest(formats, maxHeight: 1080)
            // Tier 2: any progressive stream – fallback when no ≤1080p progressive stream exists.
            ?? PickBest(formats, maxHeight: null);

        if (best is null)
        {
            _logger.LogInformation(
                "No progressive format found. Only DASH or split streams are available.");
            return null;
        }

        var url = best["url"]?.GetValue<string>();
        _logger.LogInformation(
            "Selected format: id={FormatId} height={Height}p ext={Ext} vcodec={VCodec} tbr={Tbr}",
            GetString(best, "format_id"),
            GetInt(best, "height"),
            GetString(best, "ext"),
            ShortenCodec(GetString(best, "vcodec")),
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

    // ── logging ──────────────────────────────────────────────────────────────

    private void LogAvailableFormats(JsonArray formats)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var progressive = new List<string>();
        var dashVideo = new List<string>();
        var dashAudio = new List<string>();
        var other = new List<string>();

        foreach (var f in formats.Where(f => f is not null))
        {
            var line = FormatSummary(f!);
            if (IsProgressive(f))
            {
                progressive.Add(line);
            }
            else if (IsVideoOnlyDash(f))
            {
                dashVideo.Add(line);
            }
            else if (IsAudioOnlyDash(f))
            {
                dashAudio.Add(line);
            }
            else
            {
                other.Add(line);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            $"Available formats ({formats.Count} total, "
            + $"{progressive.Count} progressive, "
            + $"{dashVideo.Count} DASH-video, "
            + $"{dashAudio.Count} DASH-audio, "
            + $"{other.Count} other/storyboard):");

        if (progressive.Count > 0)
        {
            sb.AppendLine("  Progressive (combined video+audio) – eligible for selection:");
            progressive.ForEach(l => sb.AppendLine($"    {l}"));
        }
        else
        {
            sb.AppendLine("  Progressive: none found.");
        }

        if (dashVideo.Count > 0)
        {
            sb.AppendLine("  DASH video-only (dropped – no audio):");
            dashVideo.ForEach(l => sb.AppendLine($"    {l}"));
        }

        if (dashAudio.Count > 0)
        {
            sb.AppendLine("  DASH audio-only (dropped – no video):");
            dashAudio.ForEach(l => sb.AppendLine($"    {l}"));
        }

        _logger.LogDebug("{FormatList}", sb.ToString().TrimEnd());
    }

    private static string FormatSummary(JsonNode f) =>
        $"[{GetString(f, "format_id"),6}] "
        + $"{GetInt(f, "height"),4}p "
        + $"{GetString(f, "ext"),-4} "
        + $"v={ShortenCodec(GetString(f, "vcodec")),-8} "
        + $"a={ShortenCodec(GetString(f, "acodec")),-8} "
        + $"tbr={GetDouble(f, "tbr"),8:F1}";

    // ── stream type predicates ────────────────────────────────────────────────

    private static bool IsProgressive(JsonNode? f)
    {
        var vcodec = GetString(f, "vcodec");
        var acodec = GetString(f, "acodec");
        return vcodec != "none" && vcodec.Length > 0
            && acodec != "none" && acodec.Length > 0;
    }

    private static bool IsVideoOnlyDash(JsonNode? f)
    {
        var vcodec = GetString(f, "vcodec");
        var acodec = GetString(f, "acodec");
        return vcodec != "none" && vcodec.Length > 0 && acodec == "none";
    }

    private static bool IsAudioOnlyDash(JsonNode? f)
    {
        var vcodec = GetString(f, "vcodec");
        var acodec = GetString(f, "acodec");
        return vcodec == "none" && acodec != "none" && acodec.Length > 0;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsMp4(JsonNode? f)
        => GetString(f, "ext") == "mp4";

    private static string ShortenCodec(string codec)
    {
        if (codec.Length == 0 || codec == "none")
        {
            return codec;
        }

        // "avc1.640028" → "avc1",  "mp4a.40.2" → "mp4a"
        var dot = codec.IndexOf('.');
        return dot < 0 ? codec : codec[..dot];
    }

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

    /// <summary>
    /// Returns the integer value of a JSON numeric field.
    /// Handles the case where yt-dlp emits heights as JSON floats (e.g. <c>720.0</c>)
    /// which would otherwise cause <see cref="System.Text.Json.Nodes.JsonNode.GetValue{T}"/>
    /// to throw and silently fall back to 0, corrupting the sort order.
    /// </summary>
    private static int GetInt(JsonNode? node, string key)
    {
        if (node?[key] is not { } valueNode)
        {
            return 0;
        }

        try
        {
            return valueNode.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            // Some yt-dlp versions emit integer fields as JSON doubles (e.g. 720.0).
            // Fall back to double → int conversion so height ordering is not corrupted.
            try
            {
                return (int)valueNode.GetValue<double>();
            }
            catch
            {
                return 0;
            }
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

