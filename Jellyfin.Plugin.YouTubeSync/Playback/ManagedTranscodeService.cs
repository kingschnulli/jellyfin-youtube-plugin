using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Configuration;
using Jellyfin.Plugin.YouTubeSync.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Playback;

/// <summary>
/// Manages opt-in ffmpeg-backed playback sessions that transcode YouTube inputs into disk-backed HLS.
/// </summary>
public sealed class ManagedTranscodeService : IDisposable
{
    private const string PlaylistFileName = "index.m3u8";
    private static readonly TimeSpan UnclaimedSessionTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, ManagedTranscodeSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionCreateGate = new(1, 1);
    private readonly YtDlpService _ytDlpService;
    private readonly ILogger<ManagedTranscodeService> _logger;
    private readonly Timer _cleanupTimer;
    private readonly string _rootDirectory;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ManagedTranscodeService"/> class.</summary>
    public ManagedTranscodeService(YtDlpService ytDlpService, ILogger<ManagedTranscodeService> logger)
    {
        _ytDlpService = ytDlpService;
        _logger = logger;
        _rootDirectory = Path.Combine(Path.GetTempPath(), "Jellyfin.YouTubeSync", "managed-transcode");
        Directory.CreateDirectory(_rootDirectory);
        _cleanupTimer = new Timer(_ => CleanupExpiredSessions(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Tries to start a managed transcoding session for a video and returns the local playlist path on success.
    /// Returns <c>null</c> when the managed path is disabled or setup fails.
    /// </summary>
    public async Task<string?> TryCreateSessionAsync(string videoId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AllowManagedTranscoding)
        {
            return null;
        }

        await _sessionCreateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupExpiredSessions();

            var existingSession = TryReuseExistingSession(videoId);
            if (existingSession is not null)
            {
                return $"/YouTubeSync/session/{existingSession.SessionId}/{PlaylistFileName}";
            }

            var activeSessions = _sessions.Count(static pair => !pair.Value.Process.HasExited);
            if (activeSessions >= Math.Max(1, config.MaxConcurrentManagedTranscodes))
            {
                _logger.LogWarning(
                    "Managed transcoding skipped for {VideoId}: active session limit {Limit} reached.",
                    videoId,
                    config.MaxConcurrentManagedTranscodes);
                return null;
            }

            var input = await _ytDlpService.GetManagedPlaybackInputAsync(videoId, cancellationToken).ConfigureAwait(false);
            if (input is null)
            {
                _logger.LogWarning("Managed transcoding skipped for {VideoId}: no suitable yt-dlp input URLs.", videoId);
                return null;
            }

            var sessionId = Guid.NewGuid().ToString("N");
            var sessionDirectory = Path.Combine(_rootDirectory, sessionId);
            Directory.CreateDirectory(sessionDirectory);

            var playlistPath = Path.Combine(sessionDirectory, PlaylistFileName);
            var segmentPattern = Path.Combine(sessionDirectory, "segment_%03d.ts");
            var process = CreateFfmpegProcess(config, input, playlistPath, segmentPattern);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start ffmpeg managed transcoding session for {VideoId}", videoId);
                TryDeleteDirectory(sessionDirectory);
                process.Dispose();
                return null;
            }

            var errorPump = PumpStandardErrorAsync(process, sessionId);
            var session = new ManagedTranscodeSession
            {
                SessionId = sessionId,
                VideoId = videoId,
                DirectoryPath = sessionDirectory,
                PlaylistPath = playlistPath,
                Process = process,
                ErrorPumpTask = errorPump,
                CreatedUtc = DateTime.UtcNow,
                LastAccessUtc = DateTime.UtcNow
            };

            process.Exited += (_, _) => RemoveAndDisposeSession(sessionId);

            _sessions[sessionId] = session;

            var becameReady = await WaitForReadyAsync(session, cancellationToken).ConfigureAwait(false);
            if (!becameReady)
            {
                _logger.LogWarning("Managed transcoding session for {VideoId} failed before the first HLS segment was ready.", videoId);
                RemoveAndDisposeSession(sessionId);
                return null;
            }

            _logger.LogInformation("Managed transcoding session {SessionId} is ready for video {VideoId}", sessionId, videoId);
            return $"/YouTubeSync/session/{sessionId}/{PlaylistFileName}";
        }
        finally
        {
            _sessionCreateGate.Release();
        }
    }

    /// <summary>
    /// Tries to resolve a managed session file for serving over HTTP.
    /// </summary>
    public bool TryGetSessionFile(string sessionId, string fileName, out string filePath, out string contentType)
    {
        filePath = string.Empty;
        contentType = "application/octet-stream";

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            return false;
        }

        session.HasClientAccess = true;
        session.LastAccessUtc = DateTime.UtcNow;

        var candidatePath = Path.GetFullPath(Path.Combine(session.DirectoryPath, fileName));
        var sessionPath = Path.GetFullPath(session.DirectoryPath);
        if (!candidatePath.StartsWith(sessionPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidatePath))
        {
            return false;
        }

        filePath = candidatePath;
        contentType = GetContentType(fileName);
        return true;
    }

    private static string GetContentType(string fileName)
    {
        if (fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return "application/vnd.apple.mpegurl";
        }

        if (fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return "video/mp2t";
        }

        return "application/octet-stream";
    }

    private ManagedTranscodeSession? TryReuseExistingSession(string videoId)
    {
        foreach (var pair in _sessions)
        {
            var session = pair.Value;
            if (!string.Equals(session.VideoId, videoId, StringComparison.Ordinal))
            {
                continue;
            }

            if (session.Process.HasExited)
            {
                RemoveAndDisposeSession(pair.Key);
                continue;
            }

            if (!File.Exists(session.PlaylistPath))
            {
                continue;
            }

            session.LastAccessUtc = DateTime.UtcNow;
            _logger.LogInformation(
                "Reusing managed transcoding session {SessionId} for video {VideoId}",
                session.SessionId,
                videoId);
            return session;
        }

        return null;
    }

    private Process CreateFfmpegProcess(
        PluginConfiguration config,
        ManagedPlaybackInput input,
        string playlistPath,
        string segmentPattern)
    {
        var psi = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(config.FfmpegPath) ? "ffmpeg" : config.FfmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-analyzeduration");
        psi.ArgumentList.Add("200M");
        psi.ArgumentList.Add("-probesize");
        psi.ArgumentList.Add("1G");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input.VideoUrl);

        if (input.HasSeparateAudio)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(input.AudioUrl!);
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("1:a:0");
        }
        else
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:a:0?");
        }

        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-dn");
        psi.ArgumentList.Add("-max_muxing_queue_size");
        psi.ArgumentList.Add("4096");

        AddVideoEncoderArguments(psi, config.ManagedTranscodeHardwareMode);

        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("192k");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("48000");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("hls");
        psi.ArgumentList.Add("-hls_time");
        psi.ArgumentList.Add("4");
        psi.ArgumentList.Add("-hls_playlist_type");
        psi.ArgumentList.Add("event");
        psi.ArgumentList.Add("-hls_list_size");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-hls_segment_type");
        psi.ArgumentList.Add("mpegts");
        psi.ArgumentList.Add("-hls_flags");
        psi.ArgumentList.Add("independent_segments+temp_file");
        psi.ArgumentList.Add("-hls_segment_filename");
        psi.ArgumentList.Add(segmentPattern);
        psi.ArgumentList.Add(playlistPath);

        return new Process { StartInfo = psi, EnableRaisingEvents = true };
    }

    private static void AddVideoEncoderArguments(ProcessStartInfo psi, string hardwareMode)
    {
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add("scale='min(1920,iw)':-2:force_original_aspect_ratio=decrease,format=yuv420p");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-g");
        psi.ArgumentList.Add("120");
        psi.ArgumentList.Add("-keyint_min");
        psi.ArgumentList.Add("120");
        psi.ArgumentList.Add("-sc_threshold");
        psi.ArgumentList.Add("0");

        switch (NormalizeHardwareMode(hardwareMode))
        {
            case ManagedTranscodeHardwareModes.Qsv:
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("h264_qsv");
                psi.ArgumentList.Add("-global_quality");
                psi.ArgumentList.Add("21");
                psi.ArgumentList.Add("-look_ahead");
                psi.ArgumentList.Add("0");
                psi.ArgumentList.Add("-maxrate");
                psi.ArgumentList.Add("12M");
                psi.ArgumentList.Add("-bufsize");
                psi.ArgumentList.Add("24M");
                break;
            case ManagedTranscodeHardwareModes.Nvenc:
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("h264_nvenc");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("p5");
                psi.ArgumentList.Add("-cq");
                psi.ArgumentList.Add("21");
                psi.ArgumentList.Add("-rc");
                psi.ArgumentList.Add("vbr");
                psi.ArgumentList.Add("-maxrate");
                psi.ArgumentList.Add("12M");
                psi.ArgumentList.Add("-bufsize");
                psi.ArgumentList.Add("24M");
                break;
            case ManagedTranscodeHardwareModes.Vaapi:
                psi.ArgumentList.Add("-vaapi_device");
                psi.ArgumentList.Add("/dev/dri/renderD128");
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add("scale='min(1920,iw)':-2:force_original_aspect_ratio=decrease,format=nv12,hwupload");
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("h264_vaapi");
                psi.ArgumentList.Add("-qp");
                psi.ArgumentList.Add("21");
                break;
            case ManagedTranscodeHardwareModes.Amf:
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("h264_amf");
                psi.ArgumentList.Add("-quality");
                psi.ArgumentList.Add("quality");
                psi.ArgumentList.Add("-rc");
                psi.ArgumentList.Add("qvbr");
                psi.ArgumentList.Add("-qvbr_quality_level");
                psi.ArgumentList.Add("21");
                break;
            default:
                psi.ArgumentList.Add("-c:v");
                psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset");
                psi.ArgumentList.Add("veryfast");
                psi.ArgumentList.Add("-crf");
                psi.ArgumentList.Add("20");
                psi.ArgumentList.Add("-maxrate");
                psi.ArgumentList.Add("12M");
                psi.ArgumentList.Add("-bufsize");
                psi.ArgumentList.Add("24M");
                break;
        }
    }

    private static string NormalizeHardwareMode(string? hardwareMode)
    {
        return hardwareMode switch
        {
            ManagedTranscodeHardwareModes.Qsv => ManagedTranscodeHardwareModes.Qsv,
            ManagedTranscodeHardwareModes.Nvenc => ManagedTranscodeHardwareModes.Nvenc,
            ManagedTranscodeHardwareModes.Vaapi => ManagedTranscodeHardwareModes.Vaapi,
            ManagedTranscodeHardwareModes.Amf => ManagedTranscodeHardwareModes.Amf,
            _ => ManagedTranscodeHardwareModes.None
        };
    }

    private async Task<bool> WaitForReadyAsync(ManagedTranscodeSession session, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.Process.HasExited)
            {
                return false;
            }

            if (File.Exists(session.PlaylistPath) && Directory.EnumerateFiles(session.DirectoryPath, "*.ts").Any())
            {
                session.LastAccessUtc = DateTime.UtcNow;
                return true;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task PumpStandardErrorAsync(Process process, string sessionId)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                _logger.LogDebug("ffmpeg[{SessionId}] {Line}", sessionId, line);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopped reading ffmpeg stderr for session {SessionId}", sessionId);
        }
    }

    private void CleanupExpiredSessions()
    {
        var idleMinutes = Math.Max(1, Plugin.Instance?.Configuration.ManagedTranscodeSessionIdleMinutes ?? 2);
        var cutoff = DateTime.UtcNow.AddMinutes(-idleMinutes);
        var unclaimedCutoff = DateTime.UtcNow - UnclaimedSessionTimeout;

        foreach (var pair in _sessions)
        {
            var session = pair.Value;
            if (session.Process.HasExited)
            {
                RemoveAndDisposeSession(pair.Key);
                continue;
            }

            if (!session.HasClientAccess && session.CreatedUtc <= unclaimedCutoff)
            {
                _logger.LogInformation(
                    "Stopping unclaimed managed transcoding session {SessionId} for video {VideoId} after startup timeout.",
                    session.SessionId,
                    session.VideoId);
                RemoveAndDisposeSession(pair.Key);
                continue;
            }

            if (session.LastAccessUtc > cutoff)
            {
                continue;
            }

            _logger.LogInformation(
                "Stopping idle managed transcoding session {SessionId} for video {VideoId} after inactivity timeout.",
                session.SessionId,
                session.VideoId);
            RemoveAndDisposeSession(pair.Key);
        }
    }

    private void RemoveAndDisposeSession(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        try
        {
            if (!session.Process.HasExited)
            {
                session.Process.Kill(entireProcessTree: true);
                session.Process.WaitForExit(3000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stop managed transcoding session {SessionId} cleanly", sessionId);
        }
        finally
        {
            session.Process.Dispose();
            TryDeleteDirectory(session.DirectoryPath);
        }
    }

    private void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete managed transcoding directory {DirectoryPath}", directoryPath);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionCreateGate.Dispose();
        _cleanupTimer.Dispose();

        foreach (var sessionId in _sessions.Keys.ToArray())
        {
            RemoveAndDisposeSession(sessionId);
        }
    }
}