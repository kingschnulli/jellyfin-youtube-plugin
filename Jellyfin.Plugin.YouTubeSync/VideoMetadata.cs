using System;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>Metadata used to write a synced video's files and NFO content.</summary>
public sealed class VideoMetadata
{
    /// <summary>Gets or sets the YouTube video identifier.</summary>
    public string VideoId { get; set; } = string.Empty;

    /// <summary>Gets or sets the video title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the video description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the thumbnail URL.</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the uploader or channel name.</summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>Gets or sets the published date in UTC when available.</summary>
    public DateTime? PublishedUtc { get; set; }

    /// <summary>Gets or sets the runtime in seconds when available.</summary>
    public int? DurationSeconds { get; set; }
}