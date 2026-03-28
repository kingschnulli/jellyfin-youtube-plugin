using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Creates and maintains the .strm / .nfo file tree inside the configured Jellyfin library path.
/// One sub-folder is created per source; each folder gets a Kodi-compatible .nfo file
/// (tvshow.nfo for channels / series playlists, movie.nfo for movie-mode playlists).
/// </summary>
public class SyncService
{
    private readonly YtDlpService _ytDlpService;
    private readonly ILogger<SyncService> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncService"/> class.</summary>
    public SyncService(YtDlpService ytDlpService, ILogger<SyncService> logger)
    {
        _ytDlpService = ytDlpService;
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
            await SyncSourceAsync(sources[i], cancellationToken).ConfigureAwait(false);
            progress.Report((double)(i + 1) / sources.Count * 100);
        }
    }

    private async Task SyncSourceAsync(SourceDefinition source, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;

        // Auto-resolve display name / description / thumbnail from YouTube when not manually set.
        var name = source.Name;
        var description = source.Description;
        var thumbnailUrl = source.ThumbnailUrl;

        if (string.IsNullOrEmpty(name))
        {
            _logger.LogInformation(
                "No display name configured for source '{Id}'; fetching metadata from YouTube.",
                source.Id);

            var info = await _ytDlpService.GetSourceInfoAsync(source.Url, cancellationToken)
                .ConfigureAwait(false);

            if (info is not null)
            {
                name = string.IsNullOrEmpty(info.Title) ? source.Id : info.Title;
                if (string.IsNullOrEmpty(description))
                {
                    description = info.Description;
                }

                if (string.IsNullOrEmpty(thumbnailUrl))
                {
                    thumbnailUrl = info.ThumbnailUrl;
                }
            }
            else
            {
                name = source.Id;
            }
        }

        var sourceDir = Path.Combine(config.LibraryBasePath, SanitizeFileName(name));

        Directory.CreateDirectory(sourceDir);
        WriteSourceNfo(source, sourceDir, name, description, thumbnailUrl);

        var entries = await _ytDlpService
            .GetPlaylistEntriesAsync(source.Url, config.MaxVideosPerSource, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Syncing {Count} video(s) for source '{Name}'",
            entries.Count,
            name);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteVideoFilesAsync(entry, sourceDir, config.JellyfinBaseUrl, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WriteVideoFilesAsync(
        JsonNode entry,
        string sourceDir,
        string jellyfinBaseUrl,
        CancellationToken cancellationToken)
    {
        var videoId = GetString(entry, "id");
        if (string.IsNullOrEmpty(videoId))
        {
            return;
        }

        var title = GetString(entry, "title");
        if (string.IsNullOrEmpty(title))
        {
            title = videoId;
        }

        var description = GetString(entry, "description");
        var uploadDate = GetString(entry, "upload_date");
        var safeName = SanitizeFileName(title);

        var strmPath = Path.Combine(sourceDir, $"{safeName}.strm");
        var nfoPath = Path.Combine(sourceDir, $"{safeName}.nfo");

        if (!File.Exists(strmPath))
        {
            var resolverUrl = $"{jellyfinBaseUrl.TrimEnd('/')}/YouTubeSync/resolve/{videoId}";
            await File.WriteAllTextAsync(strmPath, resolverUrl, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogDebug("Created {StrmPath}", strmPath);
        }

        if (!File.Exists(nfoPath))
        {
            var nfo = BuildVideoNfo(title, description, videoId, uploadDate);
            await File.WriteAllTextAsync(nfoPath, nfo, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // ── NFO builders ──────────────────────────────────────────────────────────

    private static void WriteSourceNfo(
        SourceDefinition source,
        string dir,
        string name,
        string description,
        string thumbnailUrl)
    {
        bool isSeries = source.Type == SourceType.Channel || source.Mode == SourceMode.Series;
        var nfoFileName = isSeries ? "tvshow.nfo" : "movie.nfo";
        var nfoPath = Path.Combine(dir, nfoFileName);

        if (File.Exists(nfoPath))
        {
            return;
        }

        var content = isSeries
            ? BuildTvShowNfo(source, name, description, thumbnailUrl)
            : BuildCollectionNfo(source, name, description, thumbnailUrl);

        File.WriteAllText(nfoPath, content, Encoding.UTF8);
    }

    private static string BuildTvShowNfo(SourceDefinition source, string name, string description, string thumbnailUrl)
    {
        var thumb = string.IsNullOrEmpty(thumbnailUrl)
            ? string.Empty
            : $"\n  <thumb aspect=\"poster\">{Xml(thumbnailUrl)}</thumb>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <tvshow>
          <title>{Xml(name)}</title>
          <plot>{Xml(description)}</plot>
          <uniqueid type="youtube" default="true">{Xml(source.Id)}</uniqueid>{thumb}
        </tvshow>
        """;
    }

    private static string BuildCollectionNfo(SourceDefinition source, string name, string description, string thumbnailUrl)
    {
        var thumb = string.IsNullOrEmpty(thumbnailUrl)
            ? string.Empty
            : $"\n  <thumb aspect=\"poster\">{Xml(thumbnailUrl)}</thumb>";

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <movie>
          <title>{Xml(name)}</title>
          <plot>{Xml(description)}</plot>
          <uniqueid type="youtube" default="true">{Xml(source.Id)}</uniqueid>{thumb}
        </movie>
        """;
    }

    private static string BuildVideoNfo(string title, string description, string videoId, string uploadDate)
    {
        var aired = string.Empty;
        if (uploadDate.Length == 8)
        {
            aired = $"\n  <aired>{uploadDate[..4]}-{uploadDate[4..6]}-{uploadDate[6..]}</aired>";
        }

        return $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <episodedetails>
          <title>{Xml(title)}</title>
          <plot>{Xml(description)}</plot>
          <uniqueid type="youtube" default="true">{Xml(videoId)}</uniqueid>{aired}
        </episodedetails>
        """;
    }

    // ── utilities ─────────────────────────────────────────────────────────────

    private static string GetString(JsonNode? node, string key)
    {
        try { return node?[key]?.GetValue<string>() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            sb.Replace(c, '_');
        }

        return sb.ToString();
    }

    private static string Xml(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
}
