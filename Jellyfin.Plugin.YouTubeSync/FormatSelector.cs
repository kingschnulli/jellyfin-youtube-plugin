using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Selects the best playback strategy from a yt-dlp JSON response.
///
/// Selection uses one rule set:
///   1. Choose the highest-quality result at or below 1080p.
///   2. A higher-resolution DASH pair beats a lower-resolution progressive stream.
///   3. At equal resolution, prefer the progressive stream to avoid live muxing when quality is the same.
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
    /// Returns the best playback result for a video, or <c>null</c> when no compatible
    /// progressive or DASH fallback streams are available.
    /// </summary>
    public PlaybackResolveResult? SelectBestFormat(JsonNode videoInfo)
    {
        var formats = videoInfo["formats"]?.AsArray();
        if (formats is null || formats.Count == 0)
        {
            _logger.LogWarning("yt-dlp response contains no 'formats' array.");
            return null;
        }

        LogAvailableFormats(formats);

        var bestProgressive = PickBestProgressive(formats, maxHeight: 1080);
        var dashPair = PickBestDashPair(formats, maxHeight: 1080);

        if (ShouldUseProgressive(bestProgressive, dashPair))
        {
            var url = bestProgressive!["url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(url))
            {
                _logger.LogInformation(
                    "Selected progressive format: id={FormatId} height={Height}p ext={Ext} vcodec={VCodec} acodec={ACodec} tbr={Tbr}",
                    GetString(bestProgressive, "format_id"),
                    GetInt(bestProgressive, "height"),
                    GetString(bestProgressive, "ext"),
                    ShortenCodec(GetString(bestProgressive, "vcodec")),
                    ShortenCodec(GetString(bestProgressive, "acodec")),
                    GetDouble(bestProgressive, "tbr"));

                return PlaybackResolveResult.Redirect(url);
            }
        }

        if (dashPair is null)
        {
            _logger.LogInformation(
                "No compatible progressive or DASH fallback format found.");
            return null;
        }

        _logger.LogInformation(
            "Selected DASH fallback: videoId={VideoFormatId} videoHeight={VideoHeight}p videoExt={VideoExt} vcodec={VCodec} audioId={AudioFormatId} audioExt={AudioExt} acodec={ACodec}",
            GetString(dashPair.Video, "format_id"),
            GetInt(dashPair.Video, "height"),
            GetString(dashPair.Video, "ext"),
            ShortenCodec(GetString(dashPair.Video, "vcodec")),
            GetString(dashPair.Audio, "format_id"),
            GetString(dashPair.Audio, "ext"),
            ShortenCodec(GetString(dashPair.Audio, "acodec")));

        return PlaybackResolveResult.DashMerge(dashPair.VideoUrl, dashPair.AudioUrl);
    }

    private static JsonNode? PickBestProgressive(JsonArray formats, int maxHeight)
    {
        return formats
            .Where(f => f is not null)
            .Where(IsProgressive)
            .Where(f => GetInt(f, "height") > 0 && GetInt(f, "height") <= maxHeight)
            .OrderByDescending(f => GetInt(f, "height"))
            .ThenByDescending(f => IsMp4(f) ? 1 : 0)
            .ThenByDescending(f => GetDouble(f, "tbr"))
            .FirstOrDefault();
    }

    private static DashPair? PickBestDashPair(JsonArray formats, int maxHeight)
    {
        var video = formats
            .Where(f => f is not null)
            .Where(IsVideoOnlyDash)
            .Where(f => GetInt(f, "height") > 0 && GetInt(f, "height") <= maxHeight)
            .Where(HasUrl)
            .OrderByDescending(f => GetInt(f, "height"))
            .ThenByDescending(f => IsAvc1(f) ? 1 : 0)
            .ThenByDescending(f => IsMp4(f) ? 1 : 0)
            .ThenByDescending(f => GetDouble(f, "tbr"))
            .FirstOrDefault();

        var audio = formats
            .Where(f => f is not null)
            .Where(IsAudioOnlyDash)
            .Where(HasUrl)
            .OrderByDescending(f => IsMp4Audio(f) ? 1 : 0)
            .ThenByDescending(f => GetDouble(f, "abr"))
            .ThenByDescending(f => GetDouble(f, "tbr"))
            .FirstOrDefault();

        var videoUrl = video?["url"]?.GetValue<string>();
        var audioUrl = audio?["url"]?.GetValue<string>();

        return !string.IsNullOrWhiteSpace(videoUrl) && !string.IsNullOrWhiteSpace(audioUrl)
            ? new DashPair(video!, audio!, videoUrl, audioUrl)
            : null;
    }

    private static bool ShouldUseProgressive(JsonNode? progressive, DashPair? dashPair)
    {
        if (progressive is null)
        {
            return false;
        }

        if (dashPair is null)
        {
            return true;
        }

        var progressiveHeight = GetInt(progressive, "height");
        var dashHeight = GetInt(dashPair.Video, "height");

        if (progressiveHeight != dashHeight)
        {
            return progressiveHeight > dashHeight;
        }

        return GetDouble(progressive, "tbr") >= GetDouble(dashPair.Video, "tbr");
    }

    // ── logging ──────────────────────────────────────────────────────────────

    private void LogAvailableFormats(JsonArray formats)
    {
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

        // Always log the summary and eligible progressive formats at Information so this
        // is visible without enabling Debug logging in Jellyfin.
        var progressiveSection = progressive.Count > 0
            ? "  Progressive (combined video+audio) – eligible for selection:\n"
              + string.Join("\n", progressive.Select(l => $"    {l}"))
            : "  Progressive: none found.";

        _logger.LogInformation(
            "Available formats ({Total} total, {PCount} progressive, {DVCount} DASH-video, "
            + "{DACount} DASH-audio, {OCount} other/storyboard):\n{ProgressiveSection}",
            formats.Count,
            progressive.Count,
            dashVideo.Count,
            dashAudio.Count,
            other.Count,
            progressiveSection);

        // Full per-stream DASH details are verbose; keep them at Debug.
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var sb = new StringBuilder();

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

        if (sb.Length > 0)
        {
            _logger.LogDebug("{DashFormatList}", sb.ToString().TrimEnd());
        }
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

    private static bool IsMp4Audio(JsonNode? f)
    {
        var ext = GetString(f, "ext");
        var acodec = ShortenCodec(GetString(f, "acodec"));
        return ext == "m4a" || ext == "mp4" || acodec == "mp4a";
    }

    private static bool IsAvc1(JsonNode? f)
    {
        var vcodec = ShortenCodec(GetString(f, "vcodec"));
        return vcodec == "avc1" || vcodec == "h264";
    }

    private static bool HasUrl(JsonNode? f)
        => !string.IsNullOrWhiteSpace(GetString(f, "url"));

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

    private sealed record DashPair(JsonNode Video, JsonNode Audio, string VideoUrl, string AudioUrl);
}

