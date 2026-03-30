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

            var bytes = await HttpClient.GetByteArrayAsync(imageUrl, timeoutCts.Token).ConfigureAwait(false);
            var extension = GetImageExtension(imageUrl);

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