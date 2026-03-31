using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

public sealed class SyncPlaylistFeedExpander
{
    private readonly YtDlpService _ytDlpService;
    private readonly ILogger<SyncPlaylistFeedExpander> _logger;

    public SyncPlaylistFeedExpander(YtDlpService ytDlpService, ILogger<SyncPlaylistFeedExpander> logger)
    {
        _ytDlpService = ytDlpService;
        _logger = logger;
    }

    internal IReadOnlyList<PlaylistSeasonDefinition> BuildSeasonDefinitions(IReadOnlyList<JsonNode> playlistEntries)
    {
        var definitions = new List<PlaylistSeasonDefinition>(playlistEntries.Count);
        var seenPlaylistIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var playlistEntry in playlistEntries)
        {
            var playlistId = GetChannelPlaylistId(playlistEntry);
            if (string.IsNullOrWhiteSpace(playlistId) || !seenPlaylistIds.Add(playlistId))
            {
                continue;
            }

            var title = GetString(playlistEntry, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                title = playlistId;
            }

            definitions.Add(new PlaylistSeasonDefinition(playlistId, title, definitions.Count + 1));
        }

        return definitions;
    }

    internal async Task<IReadOnlyList<JsonNode>> ExpandAsync(
        IReadOnlyList<JsonNode> playlistEntries,
        IReadOnlyList<PlaylistSeasonDefinition> playlistSeasonDefinitions,
        int maxPlaylistEntryScanCount,
        CancellationToken cancellationToken)
    {
        var expandedEntries = new List<JsonNode>();
        var discoveredPlaylists = 0;
        var seasonLookup = playlistSeasonDefinitions.ToDictionary(definition => definition.PlaylistId, StringComparer.OrdinalIgnoreCase);

        foreach (var playlistEntry in playlistEntries)
        {
            var playlistUrl = GetChannelPlaylistUrl(playlistEntry);
            if (string.IsNullOrWhiteSpace(playlistUrl))
            {
                continue;
            }

            var playlistId = GetChannelPlaylistId(playlistEntry);
            if (string.IsNullOrWhiteSpace(playlistId)
                || !seasonLookup.TryGetValue(playlistId, out var seasonDefinition))
            {
                continue;
            }

            discoveredPlaylists++;
            var playlistInfo = await _ytDlpService.GetSourceInfoAsync(playlistUrl, cancellationToken).ConfigureAwait(false);
            var playlistThumbnailUrl = playlistInfo?.ThumbnailUrl ?? string.Empty;
            var playlistPosterUrl = string.IsNullOrWhiteSpace(playlistInfo?.PosterUrl)
                ? playlistThumbnailUrl
                : playlistInfo.PosterUrl;
            var playlistVideos = await _ytDlpService
                .GetPlaylistEntriesAsync(playlistUrl, 0, maxPlaylistEntryScanCount, cancellationToken)
                .ConfigureAwait(false);

            for (var index = 0; index < playlistVideos.Count; index++)
            {
                var videoEntry = playlistVideos[index];
                var videoId = GetString(videoEntry, "id");
                if (string.IsNullOrWhiteSpace(videoId))
                {
                    continue;
                }

                AttachPlaylistMetadata(
                    videoEntry,
                    playlistId,
                    seasonDefinition,
                    index + 1,
                    videoId,
                    playlistThumbnailUrl,
                    playlistPosterUrl);
                expandedEntries.Add(videoEntry);
            }
        }

        _logger.LogInformation(
            "Expanded {PlaylistCount} discovered playlist(s) into {VideoCount} unique video entr{Suffix}.",
            discoveredPlaylists,
            expandedEntries.Count,
            expandedEntries.Count == 1 ? "y" : "ies");

        return expandedEntries;
    }

    private static void AttachPlaylistMetadata(
        JsonNode videoEntry,
        string playlistId,
        PlaylistSeasonDefinition seasonDefinition,
        int episodeNumber,
        string videoId,
        string playlistThumbnailUrl,
        string playlistPosterUrl)
    {
        if (videoEntry is not JsonObject videoObject)
        {
            return;
        }

        videoObject["__playlist_id"] = playlistId;
        videoObject["__playlist_title"] = seasonDefinition.Title;
        videoObject["__playlist_season_number"] = seasonDefinition.SeasonNumber;
        videoObject["__playlist_episode_number"] = episodeNumber;
        videoObject["__sync_id"] = playlistId + ":" + videoId;

        if (!string.IsNullOrWhiteSpace(playlistThumbnailUrl))
        {
            videoObject["__playlist_thumbnail_url"] = playlistThumbnailUrl;
        }

        if (!string.IsNullOrWhiteSpace(playlistPosterUrl))
        {
            videoObject["__playlist_poster_url"] = playlistPosterUrl;
        }
    }

    private static string GetChannelPlaylistUrl(JsonNode entry)
    {
        var webpageUrl = GetString(entry, "webpage_url");
        if (!string.IsNullOrWhiteSpace(webpageUrl) && webpageUrl.Contains("list=", StringComparison.OrdinalIgnoreCase))
        {
            return webpageUrl;
        }

        var url = GetString(entry, "url");
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            if (url.Contains("list=", StringComparison.OrdinalIgnoreCase))
            {
                return $"https://www.youtube.com/{url.TrimStart('/')}";
            }
        }

        var playlistId = GetString(entry, "id");
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return string.Empty;
        }

        return $"https://www.youtube.com/playlist?list={playlistId}";
    }

    private static string GetChannelPlaylistId(JsonNode entry)
    {
        var playlistId = GetString(entry, "id");
        if (!string.IsNullOrWhiteSpace(playlistId))
        {
            return playlistId;
        }

        var playlistUrl = GetChannelPlaylistUrl(entry);
        if (string.IsNullOrWhiteSpace(playlistUrl))
        {
            return string.Empty;
        }

        try
        {
            var uri = new Uri(playlistUrl);
            var query = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var pair in query)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }
        }
        catch (UriFormatException)
        {
        }

        return string.Empty;
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
}