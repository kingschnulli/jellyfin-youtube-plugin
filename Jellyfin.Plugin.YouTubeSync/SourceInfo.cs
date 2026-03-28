namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>Channel or playlist metadata returned by <see cref="YtDlpService.GetSourceInfoAsync"/>.</summary>
public sealed class SourceInfo
{
    /// <summary>Gets or sets the channel / playlist title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel / playlist description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL of the best available thumbnail or channel avatar.</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected source type (Channel or Playlist).</summary>
    public SourceType Type { get; set; } = SourceType.Channel;
}
