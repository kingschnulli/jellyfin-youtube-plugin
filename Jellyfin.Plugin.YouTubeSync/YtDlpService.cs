using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
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
    private readonly ILogger<YtDlpService> _logger;

    /// <summary>Initializes a new instance of the <see cref="YtDlpService"/> class.</summary>
    public YtDlpService(ILogger<YtDlpService> logger)
    {
        _logger = logger;
    }

    private string YtDlpPath => Plugin.Instance?.Configuration.YtDlpPath ?? "yt-dlp";
    private string FfmpegPath => string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.FfmpegPath)
        ? "ffmpeg"
        : Plugin.Instance!.Configuration.FfmpegPath;

    /// <summary>
    /// Returns full yt-dlp JSON metadata for a single video (equivalent to <c>yt-dlp -J</c>).
    /// </summary>
    public Task<JsonNode?> GetVideoInfoAsync(string videoId, CancellationToken cancellationToken)
    {
        var url = $"https://www.youtube.com/watch?v={videoId}";
        return RunYtDlpJsonAsync(new[] { "-J", "--no-playlist", url }, cancellationToken);
    }

    /// <summary>
    /// Returns the flat-playlist JSON entry list for a channel or playlist URL.
    /// Each entry contains at minimum <c>id</c>, <c>title</c>, and <c>url</c>.
    /// </summary>
    public async Task<IReadOnlyList<JsonNode>> GetPlaylistEntriesAsync(
        string playlistUrl,
        int maxVideos,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "--flat-playlist", "-J" };
        if (maxVideos > 0)
        {
            args.Add("--playlist-end");
            args.Add(maxVideos.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    /// <summary>
    /// Merges DASH video and audio into a normal MP4 file that can be served with range support.
    /// </summary>
    public async Task<bool> MergeDashToFileAsync(
        string videoUrl,
        string audioUrl,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in BuildFfmpegFileArguments(videoUrl, audioUrl, outputPath))
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogDebug("Running ffmpeg with arguments: {Arguments}", string.Join(" ", psi.ArgumentList));

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        using var cancellationRegistration = cancellationToken.Register(() => StopProcess(process));

        try
        {
            process.Start();
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _logger.LogError(
                    "ffmpeg exited with code {ExitCode} while merging DASH. Stderr: {Error}",
                    process.ExitCode,
                    error);
                return false;
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                _logger.LogError("ffmpeg completed but produced no output file at {OutputPath}", outputPath);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cancelled ffmpeg DASH merge for output {OutputPath}", outputPath);
            StopProcess(process);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run ffmpeg using binary '{FfmpegPath}'", psi.FileName);
            StopProcess(process);
            return false;
        }
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

    private static IEnumerable<string> BuildFfmpegFileArguments(string videoUrl, string audioUrl, string outputPath)
    {
        yield return "-nostdin";
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "warning";
        yield return "-y";
        yield return "-reconnect";
        yield return "1";
        yield return "-reconnect_streamed";
        yield return "1";
        yield return "-reconnect_delay_max";
        yield return "2";
        yield return "-i";
        yield return videoUrl;
        yield return "-i";
        yield return audioUrl;
        yield return "-map";
        yield return "0:v:0";
        yield return "-map";
        yield return "1:a:0";
        yield return "-c";
        yield return "copy";
        yield return "-movflags";
        yield return "+faststart";
        yield return "-f";
        yield return "mp4";
        yield return outputPath;
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
