using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeSync.Configuration;
using Jellyfin.Plugin.YouTubeSync.Metadata;
using Jellyfin.Plugin.YouTubeSync.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

/// <summary>
/// Creates and maintains the .strm / .nfo file tree inside the configured Jellyfin library path.
/// One sub-folder is created per source; each folder gets a Kodi-compatible .nfo file
/// (tvshow.nfo for channels / series playlists, movie.nfo for movie-mode playlists).
/// </summary>
public class SyncService
{
    private const int MaxPerSourceConcurrency = 4;
    private const int MinimumRetentionEntryScanCount = 25;
    private const int EstimatedUploadsPerDayForRetentionScan = 5;

    private readonly YtDlpService _ytDlpService;
    private readonly SyncPlaylistFeedExpander _playlistFeedExpander;
    private readonly ILogger<SyncService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncService"/> class.</summary>
    public SyncService(YtDlpService ytDlpService, SyncPlaylistFeedExpander playlistFeedExpander, ILogger<SyncService> logger)
    {
        _ytDlpService = ytDlpService;
        _playlistFeedExpander = playlistFeedExpander;
        _logger = logger;
    }

    /// <summary>Syncs all configured sources, reporting progress from 0–100.</summary>
    public async Task SyncAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || config.Sources.Count == 0)
        {
            _logger.LogInformation("No sources configured – skipping sync.");
            progress.Report(100);
            return;
        }

        var sources = config.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceProgressBase = (double)i / sources.Count * 100;
            var sourceProgressSpan = 100d / sources.Count;
            await SyncSourceAsync(sources[i], progress, sourceProgressBase, sourceProgressSpan, cancellationToken).ConfigureAwait(false);
            progress.Report(sourceProgressBase + sourceProgressSpan);
        }
    }

    private async Task SyncSourceAsync(
        SourceDefinition source,
        IProgress<double> progress,
        double sourceProgressBase,
        double sourceProgressSpan,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var sourceInfo = await _ytDlpService.GetSourceInfoAsync(source.Url, cancellationToken).ConfigureAwait(false);
        var retentionCutoffUtc = config.VideoRetentionDays > 0
            ? DateTime.UtcNow.AddDays(-config.VideoRetentionDays)
            : (DateTime?)null;
        var isChannelPlaylistFeed = source.Type == SourceType.Channel && source.Feed == ChannelFeed.Playlists;
        if (isChannelPlaylistFeed)
        {
            retentionCutoffUtc = null;
        }

        var name = string.IsNullOrWhiteSpace(source.Name)
            ? sourceInfo?.Title ?? source.Id
            : source.Name;
        var description = string.IsNullOrWhiteSpace(source.Description)
            ? sourceInfo?.Description ?? string.Empty
            : source.Description;
        var thumbnailUrl = string.IsNullOrWhiteSpace(source.ThumbnailUrl)
            ? sourceInfo?.ThumbnailUrl ?? string.Empty
            : source.ThumbnailUrl;
        var posterUrl = sourceInfo?.PosterUrl ?? thumbnailUrl;

        var sourceDir = Path.Combine(config.LibraryBasePath, SyncSeasonLayout.SanitizeFileName(name));

        _logger.LogInformation("Starting sync for source '{Name}'", name);

        var maxEntryScanCount = GetMaxEntryScanCount(config.VideoRetentionDays, config.MaxVideosPerSource);
        var playlistDiscoveryLimit = GetPlaylistDiscoveryLimit(config.RecentPlaylistsToKeep);

        if (isChannelPlaylistFeed)
        {
            if (playlistDiscoveryLimit > 0)
            {
                _logger.LogInformation(
                    "Applying playlist discovery limit {PlaylistDiscoveryLimit} for source '{Name}'.",
                    playlistDiscoveryLimit,
                    name);
            }
        }
        else if (maxEntryScanCount > 0)
        {
            _logger.LogInformation(
                "Applying upfront entry scan limit {MaxEntryScanCount} for source '{Name}' with retention {RetentionDays} day(s).",
                maxEntryScanCount,
                name,
                config.VideoRetentionDays);
        }

        var entries = await _ytDlpService
            .GetPlaylistEntriesAsync(
                source.Url,
                isChannelPlaylistFeed ? 0 : config.VideoRetentionDays,
                isChannelPlaylistFeed ? playlistDiscoveryLimit : maxEntryScanCount,
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PlaylistSeasonDefinition> playlistSeasonDefinitions = Array.Empty<PlaylistSeasonDefinition>();

        if (isChannelPlaylistFeed)
        {
            playlistSeasonDefinitions = _playlistFeedExpander.BuildSeasonDefinitions(entries);
            entries = await _playlistFeedExpander.ExpandAsync(entries, playlistSeasonDefinitions, cancellationToken)
                .ConfigureAwait(false);
        }

        Directory.CreateDirectory(sourceDir);
        await WriteSourceMetadataAsync(
                source,
                sourceDir,
                name,
                description,
                thumbnailUrl,
                posterUrl,
                playlistSeasonDefinitions,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Fetched {Count} playlist entr{Suffix} for source '{Name}'",
            entries.Count,
            entries.Count == 1 ? "y" : "ies",
            name);

        var videos = new ConcurrentBag<VideoMetadata>();
        var desiredVideoDirectories = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var desiredSeasonDirectories = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var metadataProcessed = 0;

        await Parallel.ForEachAsync(
                entries,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaxPerSourceConcurrency
                },
                async (entry, innerCancellationToken) =>
                {
                    try
                    {
                        var videoId = GetString(entry, "id");
                        if (string.IsNullOrWhiteSpace(videoId))
                        {
                            return;
                        }

                        var metadata = await _ytDlpService.GetVideoMetadataAsync(videoId, innerCancellationToken).ConfigureAwait(false)
                            ?? BuildFallbackVideoMetadata(entry, videoId, name);

                        NormalizeVideoMetadata(metadata, entry, videoId, name);

                        if (metadata.PublishedUtc is null)
                        {
                            metadata.PublishedUtc = await _ytDlpService.GetVideoPublishedDateAsync(videoId, innerCancellationToken)
                                .ConfigureAwait(false);
                        }

                        if (metadata.PublishedUtc is null)
                        {
                            _logger.LogWarning(
                                "Skipping video {VideoId} during sync for source {SourceName} because no published date could be extracted.",
                                videoId,
                                name);
                            return;
                        }

                        if (retentionCutoffUtc is DateTime cutoffUtc
                            && metadata.PublishedUtc is DateTime publishedUtc
                            && publishedUtc < cutoffUtc)
                        {
                            return;
                        }

                        videos.Add(metadata);

                        var seasonFolder = SyncSeasonLayout.GetSeasonFolderName(metadata, source);
                        var parentDir = string.IsNullOrEmpty(seasonFolder)
                            ? sourceDir
                            : Path.Combine(sourceDir, seasonFolder);
                        var videoDir = Path.Combine(parentDir, SyncSeasonLayout.BuildVideoFolderName(metadata.Title, metadata.VideoId));

                        desiredVideoDirectories.TryAdd(videoDir, 0);
                        if (!string.IsNullOrEmpty(seasonFolder))
                        {
                            desiredSeasonDirectories.TryAdd(parentDir, 0);
                        }

                        await WriteVideoShellAsync(metadata, videoDir, config.JellyfinBaseUrl, innerCancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        ReportPhaseProgress(
                            progress,
                            sourceProgressBase,
                            sourceProgressSpan,
                            Interlocked.Increment(ref metadataProcessed),
                            entries.Count,
                            0.0,
                            0.5,
                            "metadata",
                            name);
                    }
                })
            .ConfigureAwait(false);

        var retainedVideos = videos
            .OrderByDescending(v => v.PublishedUtc ?? DateTime.MinValue)
            .ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation(
            "Syncing {Count} video(s) for source '{Name}'",
            retainedVideos.Count,
            name);

        await WriteSeasonMetadataAsync(source, sourceDir, name, retainedVideos, cancellationToken).ConfigureAwait(false);

        var seasonEpisodeCounters = SyncSeasonLayout.BuildSeasonEpisodeCounters(retainedVideos, source);
        var filesWritten = 0;

        await Parallel.ForEachAsync(
                retainedVideos,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaxPerSourceConcurrency
                },
                async (video, innerCancellationToken) =>
                {
                    try
                    {
                        var seasonFolder = SyncSeasonLayout.GetSeasonFolderName(video, source);
                        var parentDir = string.IsNullOrEmpty(seasonFolder)
                            ? sourceDir
                            : Path.Combine(sourceDir, seasonFolder);
                        var videoDir = Path.Combine(parentDir, SyncSeasonLayout.BuildVideoFolderName(video.Title, video.VideoId));
                        var seasonNumber = SyncSeasonLayout.GetSeasonNumber(video, source);
                        var episodeNumber = SyncSeasonLayout.GetEpisodeNumber(video, seasonEpisodeCounters, source);

                        await WriteVideoFilesAsync(
                                video,
                                source.Mode,
                                seasonNumber,
                                episodeNumber,
                                videoDir,
                                config.JellyfinBaseUrl,
                                name,
                                innerCancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        ReportPhaseProgress(
                            progress,
                            sourceProgressBase,
                            sourceProgressSpan,
                            Interlocked.Increment(ref filesWritten),
                            retainedVideos.Count,
                            0.5,
                            1.0,
                            "files",
                            name);
                    }
                })
            .ConfigureAwait(false);

        CleanupObsoleteContent(
            sourceDir,
            source.Mode,
            new HashSet<string>(desiredSeasonDirectories.Keys, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(desiredVideoDirectories.Keys, StringComparer.OrdinalIgnoreCase));

        _logger.LogInformation("Completed sync for source '{Name}'", name);
    }

    private async Task WriteVideoShellAsync(
        VideoMetadata video,
        string videoDir,
        string jellyfinBaseUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(video.VideoId))
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(video.Title) ? video.VideoId : video.Title;
        var safeName = SyncSeasonLayout.SanitizeFileName(title);
        Directory.CreateDirectory(videoDir);

        var strmPath = Path.Combine(videoDir, $"{safeName}.strm");
        var resolverUrl = $"{jellyfinBaseUrl.TrimEnd('/')}/YouTubeSync/resolve/{video.VideoId}";
        await File.WriteAllTextAsync(strmPath, resolverUrl, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteVideoFilesAsync(
        VideoMetadata video,
        SourceMode sourceMode,
        int? seasonNumber,
        int? episodeNumber,
        string videoDir,
        string jellyfinBaseUrl,
        string sourceName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(video.VideoId))
        {
            return;
        }

        var title = video.Title;
        if (string.IsNullOrEmpty(title))
        {
            title = video.VideoId;
        }

        var safeName = SyncSeasonLayout.SanitizeFileName(title);
        Directory.CreateDirectory(videoDir);

        var strmPath = Path.Combine(videoDir, $"{safeName}.strm");
        var nfoPath = Path.Combine(videoDir, $"{safeName}.nfo");
        var thumbFileName = string.IsNullOrWhiteSpace(video.ThumbnailUrl)
            ? string.Empty
            : SyncArtworkHelper.GetArtworkFileName(video.ThumbnailUrl, "poster");

        await WriteVideoShellAsync(video, videoDir, jellyfinBaseUrl, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Wrote {StrmPath}", strmPath);

        var nfo = sourceMode == SourceMode.Movies
            ? SyncNfoBuilder.BuildMovieVideoNfo(video, sourceName, thumbFileName)
            : SyncNfoBuilder.BuildEpisodeNfo(video, sourceName, seasonNumber, episodeNumber, thumbFileName);
        await File.WriteAllTextAsync(nfoPath, nfo, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        await SyncArtworkHelper.DownloadArtworkAsync(_logger, video.ThumbnailUrl, videoDir, new[] { "folder", "poster" }, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteSourceMetadataAsync(
        SourceDefinition source,
        string dir,
        string name,
        string description,
        string thumbnailUrl,
        string posterUrl,
        IReadOnlyList<PlaylistSeasonDefinition> playlistSeasonDefinitions,
        CancellationToken cancellationToken)
    {
        bool isSeries = source.Type == SourceType.Channel || source.Mode == SourceMode.Series;
        var nfoFileName = isSeries ? "tvshow.nfo" : "movie.nfo";
        var nfoPath = Path.Combine(dir, nfoFileName);
        var folderFileName = string.IsNullOrWhiteSpace(thumbnailUrl)
            ? string.Empty
            : SyncArtworkHelper.GetArtworkFileName(thumbnailUrl, "folder");
        var posterOrThumbnailUrl = string.IsNullOrWhiteSpace(posterUrl) ? thumbnailUrl : posterUrl;
        var posterFileName = string.IsNullOrWhiteSpace(posterOrThumbnailUrl)
            ? string.Empty
            : SyncArtworkHelper.GetArtworkFileName(posterOrThumbnailUrl, "poster");
        var bannerFileName = string.IsNullOrWhiteSpace(posterOrThumbnailUrl)
            ? string.Empty
            : SyncArtworkHelper.GetArtworkFileName(posterOrThumbnailUrl, "banner");

        var content = isSeries
            ? SyncNfoBuilder.BuildTvShowNfo(source, name, description, folderFileName, posterFileName, bannerFileName, playlistSeasonDefinitions)
            : SyncNfoBuilder.BuildCollectionNfo(source, name, description, folderFileName, posterFileName);

        await File.WriteAllTextAsync(nfoPath, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        var artworkDownloads = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        SyncArtworkHelper.AddArtworkTarget(artworkDownloads, thumbnailUrl, "folder");

        SyncArtworkHelper.AddArtworkTarget(artworkDownloads, posterOrThumbnailUrl, "poster");
        SyncArtworkHelper.AddArtworkTarget(artworkDownloads, posterOrThumbnailUrl, "banner");

        foreach (var artworkDownload in artworkDownloads)
        {
            await SyncArtworkHelper.DownloadArtworkAsync(_logger, artworkDownload.Key, dir, artworkDownload.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteSeasonMetadataAsync(
        SourceDefinition source,
        string sourceDir,
        string sourceName,
        IReadOnlyList<VideoMetadata> retainedVideos,
        CancellationToken cancellationToken)
    {
        if (source.Mode == SourceMode.Movies || retainedVideos.Count == 0)
        {
            return;
        }

        var seasonGroups = retainedVideos
            .GroupBy(video => SyncSeasonLayout.GetSeasonFolderName(video, source), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key));

        foreach (var seasonGroup in seasonGroups)
        {
            var seasonVideos = seasonGroup.ToList();
            var seasonDir = Path.Combine(sourceDir, seasonGroup.Key);
            var seasonNumber = SyncSeasonLayout.GetSeasonNumber(seasonVideos[0], source);
            var seasonTitle = SelectSeasonTitle(source, seasonVideos, seasonGroup.Key);
            var (folderUrl, posterUrl) = SelectSeasonArtwork(source, seasonVideos);

            Directory.CreateDirectory(seasonDir);

            var seasonArtworkDownloads = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            SyncArtworkHelper.AddArtworkTarget(seasonArtworkDownloads, folderUrl, "folder");
            SyncArtworkHelper.AddArtworkTarget(seasonArtworkDownloads, posterUrl, "poster");

            foreach (var artworkDownload in seasonArtworkDownloads)
            {
                await SyncArtworkHelper.DownloadArtworkAsync(_logger, artworkDownload.Key, seasonDir, artworkDownload.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            var seasonNfoPath = Path.Combine(seasonDir, "season.nfo");
            var posterFileName = string.IsNullOrWhiteSpace(posterUrl)
                ? string.Empty
                : SyncArtworkHelper.GetArtworkFileName(posterUrl, "poster");
            var folderFileName = string.IsNullOrWhiteSpace(folderUrl)
                ? string.Empty
                : SyncArtworkHelper.GetArtworkFileName(folderUrl, "folder");
            var seasonNfo = SyncNfoBuilder.BuildSeasonNfo(sourceName, seasonTitle, seasonNumber, posterFileName, folderFileName);
            await File.WriteAllTextAsync(seasonNfoPath, seasonNfo, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetString(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<string>() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int? GetNullableInt(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<int>(); }
        catch { return null; }
    }

    private static VideoMetadata BuildFallbackVideoMetadata(JsonNode entry, string videoId, string sourceName)
    {
        return new VideoMetadata
        {
            VideoId = videoId,
            SyncId = GetString(entry, "__sync_id"),
            Title = GetString(entry, "title"),
            Description = GetString(entry, "description"),
            ThumbnailUrl = GetFallbackVideoThumbnailUrl(entry),
            ChannelName = sourceName,
            PlaylistId = GetString(entry, "__playlist_id"),
            PlaylistTitle = GetString(entry, "__playlist_title"),
            PlaylistThumbnailUrl = GetString(entry, "__playlist_thumbnail_url"),
            PlaylistPosterUrl = GetString(entry, "__playlist_poster_url"),
            PlaylistSeasonNumber = GetNullableInt(entry, "__playlist_season_number"),
            PlaylistEpisodeNumber = GetNullableInt(entry, "__playlist_episode_number"),
            PublishedUtc = YtDlpService.ParsePublishedDate(entry)
        };
    }

    private static string GetFallbackVideoThumbnailUrl(JsonNode? entry)
    {
        var bestUrl = YtDlpService.GetBestVideoThumbnailUrl(entry);
        if (!string.IsNullOrWhiteSpace(bestUrl))
        {
            return bestUrl;
        }

        return GetString(entry, "thumbnail");
    }

    private static void NormalizeVideoMetadata(VideoMetadata metadata, JsonNode entry, string videoId, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(metadata.SyncId))
        {
            metadata.SyncId = GetString(entry, "__sync_id");
        }

        if (string.IsNullOrWhiteSpace(metadata.SyncId))
        {
            metadata.SyncId = videoId;
        }

        if (string.IsNullOrWhiteSpace(metadata.VideoId))
        {
            metadata.VideoId = videoId;
        }

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            metadata.Title = GetString(entry, "title");
        }

        if (string.IsNullOrWhiteSpace(metadata.Description))
        {
            metadata.Description = GetString(entry, "description");
        }

        if (string.IsNullOrWhiteSpace(metadata.ChannelName))
        {
            metadata.ChannelName = sourceName;
        }

        if (string.IsNullOrWhiteSpace(metadata.PlaylistId))
        {
            metadata.PlaylistId = GetString(entry, "__playlist_id");
        }

        if (string.IsNullOrWhiteSpace(metadata.PlaylistTitle))
        {
            metadata.PlaylistTitle = GetString(entry, "__playlist_title");
        }

        if (string.IsNullOrWhiteSpace(metadata.PlaylistThumbnailUrl))
        {
            metadata.PlaylistThumbnailUrl = GetString(entry, "__playlist_thumbnail_url");
        }

        if (string.IsNullOrWhiteSpace(metadata.PlaylistPosterUrl))
        {
            metadata.PlaylistPosterUrl = GetString(entry, "__playlist_poster_url");
        }

        if (metadata.PlaylistSeasonNumber is null)
        {
            metadata.PlaylistSeasonNumber = GetNullableInt(entry, "__playlist_season_number");
        }

        if (metadata.PlaylistEpisodeNumber is null)
        {
            metadata.PlaylistEpisodeNumber = GetNullableInt(entry, "__playlist_episode_number");
        }

        if (metadata.PublishedUtc is null)
        {
            metadata.PublishedUtc = YtDlpService.ParsePublishedDate(entry);
        }
    }

    private static (string FolderUrl, string PosterUrl) SelectSeasonArtwork(
        SourceDefinition source,
        IReadOnlyList<VideoMetadata> seasonVideos)
    {
        var fallbackThumbnailUrl = SelectSeasonFallbackThumbnailUrl(source, seasonVideos);

        if (source.Type == SourceType.Channel && source.Feed == ChannelFeed.Playlists)
        {
            var playlistThumbnailUrl = seasonVideos
                .Select(video => video.PlaylistThumbnailUrl)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;
            var playlistPosterUrl = seasonVideos
                .Select(video => video.PlaylistPosterUrl)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;

            var folderUrl = !string.IsNullOrWhiteSpace(playlistThumbnailUrl)
                ? playlistThumbnailUrl
                : !string.IsNullOrWhiteSpace(playlistPosterUrl)
                    ? playlistPosterUrl
                    : fallbackThumbnailUrl;
            var posterUrl = !string.IsNullOrWhiteSpace(playlistPosterUrl)
                ? playlistPosterUrl
                : !string.IsNullOrWhiteSpace(playlistThumbnailUrl)
                    ? playlistThumbnailUrl
                    : fallbackThumbnailUrl;

            return (folderUrl, posterUrl);
        }

        return (fallbackThumbnailUrl, fallbackThumbnailUrl);
    }

    private static string SelectSeasonFallbackThumbnailUrl(
        SourceDefinition source,
        IReadOnlyList<VideoMetadata> seasonVideos)
    {
        IEnumerable<VideoMetadata> candidates = seasonVideos
            .Where(video => !string.IsNullOrWhiteSpace(video.ThumbnailUrl));

        VideoMetadata? selectedVideo;
        if (source.Type == SourceType.Channel && source.Feed == ChannelFeed.Playlists)
        {
            selectedVideo = candidates
                .OrderByDescending(video => video.PlaylistEpisodeNumber ?? int.MinValue)
                .ThenByDescending(video => video.PublishedUtc ?? DateTime.MinValue)
                .ThenBy(video => video.Title, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        else
        {
            selectedVideo = candidates
                .OrderByDescending(video => video.PublishedUtc ?? DateTime.MinValue)
                .ThenBy(video => video.Title, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        return selectedVideo?.ThumbnailUrl ?? string.Empty;
    }

    private static string SelectSeasonTitle(
        SourceDefinition source,
        IReadOnlyList<VideoMetadata> seasonVideos,
        string seasonFolderName)
    {
        if (source.Type == SourceType.Channel && source.Feed == ChannelFeed.Playlists)
        {
            return seasonVideos
                .Select(video => video.PlaylistTitle)
                .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))
                ?? seasonFolderName;
        }

        return seasonVideos[0].PublishedUtc is DateTime publishedUtc
            ? publishedUtc.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : seasonFolderName;
    }

    private void CleanupObsoleteContent(
        string sourceDir,
        SourceMode sourceMode,
        HashSet<string> desiredSeasonDirectories,
        HashSet<string> desiredVideoDirectories)
    {
        DeleteLegacyRootVideoFiles(sourceDir);

        if (sourceMode == SourceMode.Movies)
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceDir))
            {
                if (!desiredVideoDirectories.Contains(directory))
                {
                    TryDeleteDirectory(directory);
                }
            }

            return;
        }

        foreach (var seasonDirectory in Directory.EnumerateDirectories(sourceDir))
        {
            if (!Path.GetFileName(seasonDirectory).StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!desiredSeasonDirectories.Contains(seasonDirectory))
            {
                TryDeleteDirectory(seasonDirectory);
                continue;
            }

            foreach (var videoDirectory in Directory.EnumerateDirectories(seasonDirectory))
            {
                if (!desiredVideoDirectories.Contains(videoDirectory))
                {
                    TryDeleteDirectory(videoDirectory);
                }
            }
        }

        DeleteLegacySeasonPosterAliases(sourceDir);
    }

    private static void DeleteLegacySeasonPosterAliases(string sourceDir)
    {
        foreach (var filePath in Directory.EnumerateFiles(sourceDir, "season*-poster.*"))
        {
            File.Delete(filePath);
        }
    }

    private static void DeleteLegacyRootVideoFiles(string sourceDir)
    {
        foreach (var filePath in Directory.EnumerateFiles(sourceDir))
        {
            var fileName = Path.GetFileName(filePath);
            if (fileName.Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("movie.nfo", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("folder.", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("poster.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fileName.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(filePath);
            }
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
            _logger.LogDebug(ex, "Failed to delete obsolete synced directory {DirectoryPath}", directoryPath);
        }
    }

    private void ReportPhaseProgress(
        IProgress<double> progress,
        double sourceProgressBase,
        double sourceProgressSpan,
        int completed,
        int total,
        double phaseStartFraction,
        double phaseEndFraction,
        string phaseName,
        string sourceName)
    {
        if (total <= 0)
        {
            progress.Report(sourceProgressBase + sourceProgressSpan * phaseEndFraction);
            return;
        }

        var phaseFraction = phaseStartFraction + ((phaseEndFraction - phaseStartFraction) * completed / total);
        var progressValue = sourceProgressBase + sourceProgressSpan * phaseFraction;
        progress.Report(progressValue);

        if (completed == 1 || completed == total || completed % 25 == 0)
        {
            _logger.LogInformation(
                "Source '{Name}' {Phase} progress: {Completed}/{Total}",
                sourceName,
                phaseName,
                completed,
                total);
        }
    }

    private static int GetMaxEntryScanCount(int videoRetentionDays, int maxVideosPerSource)
    {
        if (videoRetentionDays <= 0)
        {
            return 0;
        }

        var retentionBasedLimit = Math.Max(
            MinimumRetentionEntryScanCount,
            videoRetentionDays * EstimatedUploadsPerDayForRetentionScan);

        if (maxVideosPerSource > 0)
        {
            return Math.Min(retentionBasedLimit, maxVideosPerSource);
        }

        return retentionBasedLimit;
    }

    private static int GetPlaylistDiscoveryLimit(int recentPlaylistsToKeep)
    {
        return recentPlaylistsToKeep > 0 ? recentPlaylistsToKeep : 0;
    }
}
