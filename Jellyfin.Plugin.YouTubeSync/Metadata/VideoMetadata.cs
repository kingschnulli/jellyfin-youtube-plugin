using System;

namespace Jellyfin.Plugin.YouTubeSync.Metadata;

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

    /// <summary>Gets or sets a sync-unique identifier used for local item identity.</summary>
    public string SyncId { get; set; } = string.Empty;

    /// <summary>Gets or sets the originating playlist identifier when the source syncs channel playlists.</summary>
    public string PlaylistId { get; set; } = string.Empty;

    /// <summary>Gets or sets the originating playlist title when the source syncs channel playlists.</summary>
    public string PlaylistTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the artwork URL detected for the originating playlist when available.</summary>
    public string PlaylistThumbnailUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the poster-style artwork URL detected for the originating playlist when available.</summary>
    public string PlaylistPosterUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the synthetic season number assigned to the playlist.</summary>
    public int? PlaylistSeasonNumber { get; set; }

    /// <summary>Gets or sets the position of the video inside its originating playlist.</summary>
    public int? PlaylistEpisodeNumber { get; set; }

    /// <summary>Gets or sets the published date in UTC when available.</summary>
    public DateTime? PublishedUtc { get; set; }

    /// <summary>Gets or sets the runtime in seconds when available.</summary>
    public int? DurationSeconds { get; set; }
}