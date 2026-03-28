using System;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>Defines the type of a YouTube source (channel or playlist).</summary>
public enum SourceType
{
    /// <summary>A YouTube channel.</summary>
    Channel,

    /// <summary>A YouTube playlist.</summary>
    Playlist
}

/// <summary>Determines how videos from a playlist are organised inside Jellyfin.</summary>
public enum SourceMode
{
    /// <summary>Videos are treated as TV-show episodes (tvshow.nfo parent).</summary>
    Series,

    /// <summary>Videos are treated as individual movies.</summary>
    Movies
}

/// <summary>Describes a single YouTube source (channel or playlist) to sync.</summary>
public class SourceDefinition
{
    /// <summary>
    /// Gets or sets the YouTube channel / playlist URL or bare ID.
    /// Full URLs such as <c>https://www.youtube.com/@handle</c> or
    /// <c>https://www.youtube.com/playlist?list=PLxxxxxx</c> are accepted.
    /// For backward compatibility a bare channel ID (e.g. <c>UCxxxxxx</c>) or
    /// playlist ID (e.g. <c>PLxxxxxx</c>) is still supported.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the source type.</summary>
    public SourceType Type { get; set; } = SourceType.Channel;

    /// <summary>Gets or sets how playlist content is structured inside Jellyfin.</summary>
    public SourceMode Mode { get; set; } = SourceMode.Series;

    /// <summary>Gets or sets the human-readable display name used as the folder name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional description written into the source .nfo file.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the thumbnail / channel-icon URL (auto-fetched from YouTube when empty).</summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets the yt-dlp-compatible URL for this source.
    /// <para>
    /// For channels the <c>/videos</c> tab is appended automatically so that only regular
    /// uploads are returned — Shorts and live streams are excluded.
    /// </para>
    /// </summary>
    public string Url
    {
        get
        {
            // Accept full YouTube URLs entered directly by the user.
            if (Id.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || Id.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (Type == SourceType.Channel)
                {
                    return EnsureChannelVideosTab(Id);
                }

                return Id;
            }

            // Legacy: bare ID → construct a standard URL.
            return Type == SourceType.Channel
                ? $"https://www.youtube.com/channel/{Id}/videos"
                : $"https://www.youtube.com/playlist?list={Id}";
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // Known YouTube channel tab suffixes — used by EnsureChannelVideosTab.
    private static readonly string[] ChannelTabSuffixes =
        ["/videos", "/shorts", "/streams", "/featured", "/about", "/community", "/playlists", "/channels"];

    /// <summary>
    /// Appends <c>/videos</c> to a channel URL if no tab sub-path is present.
    /// This ensures yt-dlp returns only regular uploads and ignores Shorts / live streams.
    /// </summary>
    private static string EnsureChannelVideosTab(string channelUrl)
    {
        var url = channelUrl.TrimEnd('/');

        foreach (var tab in ChannelTabSuffixes)
        {
            if (url.EndsWith(tab, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return url + "/videos";
    }
}
