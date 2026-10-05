namespace Jellyfin.Plugin.YouTubeSync.Playback;

/// <summary>
/// Represents the yt-dlp-resolved input URLs used for managed transcoding.
/// A managed session may use either a combined input URL or separate video and audio URLs.
/// </summary>
public sealed record ManagedPlaybackInput(string VideoUrl, string? AudioUrl, string? VideoHeaders = null, string? AudioHeaders = null)
{
    /// <summary>Gets a value indicating whether the input uses separate audio and video URLs.</summary>
    public bool HasSeparateAudio => !string.IsNullOrWhiteSpace(AudioUrl);
}