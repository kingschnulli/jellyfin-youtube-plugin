using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>Represents one active disk-backed ffmpeg HLS session.</summary>
public sealed class ManagedTranscodeSession
{
    /// <summary>Gets or sets the session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets or sets the source YouTube video identifier.</summary>
    public required string VideoId { get; init; }

    /// <summary>Gets or sets the session working directory.</summary>
    public required string DirectoryPath { get; init; }

    /// <summary>Gets or sets the on-disk HLS playlist path.</summary>
    public required string PlaylistPath { get; init; }

    /// <summary>Gets or sets the ffmpeg process producing the HLS files.</summary>
    public required Process Process { get; init; }

    /// <summary>Gets or sets the background task draining ffmpeg stderr.</summary>
    public required Task ErrorPumpTask { get; init; }

    /// <summary>Gets or sets the UTC time when the session was created.</summary>
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Gets or sets a value indicating whether a client has requested any playlist or segment file for this session.</summary>
    public bool HasClientAccess { get; set; }

    /// <summary>Gets or sets the last time this session was accessed.</summary>
    public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
}