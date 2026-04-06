using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync.Sync;

internal static class SyncArtworkHelper
{
    private static readonly TimeSpan ArtworkDownloadTimeout = TimeSpan.FromSeconds(20);
    private static readonly string[] ArtworkExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
    private static readonly HttpClient HttpClient = new();

    public static async Task DownloadArtworkAsync(
        ILogger logger,
        string imageUrl,
        string directory,
        IReadOnlyList<string> baseNames,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || baseNames.Count == 0)
        {
            return;
        }

        if (AreAllArtworkTargetsPresent(directory, baseNames))
        {
            logger.LogDebug("Skipping artwork download for {ImageUrl} because all targets already exist in {Directory}", imageUrl, directory);
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ArtworkDownloadTimeout);

            var downloadResult = await TryDownloadArtworkBytesAsync(imageUrl, timeoutCts.Token).ConfigureAwait(false);
            if (downloadResult is null)
            {
                logger.LogWarning("Failed to download artwork from {ImageUrl}", imageUrl);
                return;
            }

            var (resolvedUrl, bytes) = downloadResult.Value;
            var extension = GetImageExtension(resolvedUrl);

            foreach (var baseName in baseNames)
            {
                if (HasArtworkVariant(directory, baseName))
                {
                    continue;
                }

                var targetPath = Path.Combine(directory, baseName + extension);
                await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Timed out downloading artwork from {ImageUrl} after {TimeoutSeconds} seconds.",
                imageUrl,
                ArtworkDownloadTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download artwork from {ImageUrl}", imageUrl);
        }
    }

    public static void AddArtworkTarget(Dictionary<string, List<string>> artworkDownloads, string imageUrl, string baseName)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return;
        }

        if (!artworkDownloads.TryGetValue(imageUrl, out var baseNames))
        {
            baseNames = new List<string>();
            artworkDownloads[imageUrl] = baseNames;
        }

        if (!baseNames.Any(existing => existing.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
        {
            baseNames.Add(baseName);
        }
    }

    public static string GetArtworkFileName(string imageUrl, string baseName)
    {
        return baseName + GetImageExtension(imageUrl);
    }

    private static string GetImageExtension(string imageUrl)
    {
        try
        {
            var extension = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
            if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase))
            {
                return extension;
            }
        }
        catch (UriFormatException)
        {
        }

        return ".jpg";
    }

    private static async Task<(string ResolvedUrl, byte[] Bytes)?> TryDownloadArtworkBytesAsync(string imageUrl, CancellationToken cancellationToken)
    {
        foreach (var candidateUrl in EnumerateArtworkDownloadUrls(imageUrl))
        {
            try
            {
                var bytes = await HttpClient.GetByteArrayAsync(candidateUrl, cancellationToken).ConfigureAwait(false);
                return (candidateUrl, bytes);
            }
            catch (HttpRequestException)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateArtworkDownloadUrls(string imageUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (seen.Add(imageUrl))
        {
            yield return imageUrl;
        }

        if (!TryParseYoutubeThumbnail(imageUrl, out var prefix, out var videoId, out var fileName, out var extension))
        {
            yield break;
        }

        foreach (var candidateFileName in EnumerateYoutubeThumbnailFallbackFileNames(fileName))
        {
            var candidateUrl = $"https://i.ytimg.com/{prefix}/{videoId}/{candidateFileName}{extension}";
            if (seen.Add(candidateUrl))
            {
                yield return candidateUrl;
            }
        }

        foreach (var alternatePrefix in EnumerateAlternateYoutubePrefixes(prefix))
        {
            var candidateUrl = $"https://i.ytimg.com/{alternatePrefix}/{videoId}/{fileName}{extension}";
            if (seen.Add(candidateUrl))
            {
                yield return candidateUrl;
            }

            foreach (var candidateFileName in EnumerateYoutubeThumbnailFallbackFileNames(fileName))
            {
                candidateUrl = $"https://i.ytimg.com/{alternatePrefix}/{videoId}/{candidateFileName}{extension}";
                if (seen.Add(candidateUrl))
                {
                    yield return candidateUrl;
                }
            }
        }

        foreach (var alternateExtension in EnumerateAlternateYoutubeExtensions(extension))
        {
            var candidateUrl = $"https://i.ytimg.com/{prefix}/{videoId}/{fileName}{alternateExtension}";
            if (seen.Add(candidateUrl))
            {
                yield return candidateUrl;
            }

            foreach (var candidateFileName in EnumerateYoutubeThumbnailFallbackFileNames(fileName))
            {
                candidateUrl = $"https://i.ytimg.com/{prefix}/{videoId}/{candidateFileName}{alternateExtension}";
                if (seen.Add(candidateUrl))
                {
                    yield return candidateUrl;
                }
            }

            foreach (var alternatePrefix in EnumerateAlternateYoutubePrefixes(prefix))
            {
                candidateUrl = $"https://i.ytimg.com/{alternatePrefix}/{videoId}/{fileName}{alternateExtension}";
                if (seen.Add(candidateUrl))
                {
                    yield return candidateUrl;
                }

                foreach (var candidateFileName in EnumerateYoutubeThumbnailFallbackFileNames(fileName))
                {
                    candidateUrl = $"https://i.ytimg.com/{alternatePrefix}/{videoId}/{candidateFileName}{alternateExtension}";
                    if (seen.Add(candidateUrl))
                    {
                        yield return candidateUrl;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateYoutubeThumbnailFallbackFileNames(string fileName)
    {
        yield return fileName;

        if (fileName.Equals("maxresdefault", StringComparison.OrdinalIgnoreCase))
        {
            yield return "hq720";
            yield return "sddefault";
            yield return "hqdefault";
            yield return "mqdefault";
            yield return "default";
            yield break;
        }

        if (fileName.Equals("hq720", StringComparison.OrdinalIgnoreCase))
        {
            yield return "sddefault";
            yield return "hqdefault";
            yield return "mqdefault";
            yield return "default";
            yield break;
        }

        if (fileName.Equals("sddefault", StringComparison.OrdinalIgnoreCase))
        {
            yield return "hqdefault";
            yield return "mqdefault";
            yield return "default";
            yield break;
        }

        if (fileName.Equals("hqdefault", StringComparison.OrdinalIgnoreCase))
        {
            yield return "mqdefault";
            yield return "default";
            yield break;
        }

        if (fileName.Equals("mqdefault", StringComparison.OrdinalIgnoreCase))
        {
            yield return "default";
        }
    }

    private static IEnumerable<string> EnumerateAlternateYoutubePrefixes(string prefix)
    {
        if (prefix.Equals("vi_webp", StringComparison.OrdinalIgnoreCase))
        {
            yield return "vi";
        }
        else if (prefix.Equals("vi", StringComparison.OrdinalIgnoreCase))
        {
            yield return "vi_webp";
        }
    }

    private static IEnumerable<string> EnumerateAlternateYoutubeExtensions(string extension)
    {
        if (extension.Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            yield return ".jpg";
        }
        else if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            yield return ".webp";
        }
    }

    private static bool TryParseYoutubeThumbnail(
        string imageUrl,
        out string prefix,
        out string videoId,
        out string fileName,
        out string extension)
    {
        prefix = string.Empty;
        videoId = string.Empty;
        fileName = string.Empty;
        extension = string.Empty;

        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("ytimg.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3)
        {
            return false;
        }

        prefix = segments[0];
        videoId = segments[1];
        extension = Path.GetExtension(segments[2]);
        fileName = Path.GetFileNameWithoutExtension(segments[2]);

        return !string.IsNullOrWhiteSpace(prefix)
            && !string.IsNullOrWhiteSpace(videoId)
            && !string.IsNullOrWhiteSpace(fileName)
            && !string.IsNullOrWhiteSpace(extension);
    }

    private static bool AreAllArtworkTargetsPresent(string directory, IReadOnlyList<string> baseNames)
    {
        foreach (var baseName in baseNames)
        {
            if (!HasArtworkVariant(directory, baseName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasArtworkVariant(string directory, string baseName)
    {
        foreach (var extension in ArtworkExtensions)
        {
            var candidatePath = Path.Combine(directory, baseName + extension);
            if (File.Exists(candidatePath))
            {
                return true;
            }
        }

        return false;
    }
}