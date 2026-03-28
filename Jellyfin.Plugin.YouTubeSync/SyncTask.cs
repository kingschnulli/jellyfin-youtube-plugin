using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>
/// Jellyfin scheduled task that triggers a full YouTube sync.
/// Appears in the dashboard under Scheduled Tasks → YouTube.
/// </summary>
public class SyncTask : IScheduledTask
{
    private readonly SyncService _syncService;
    private readonly ILogger<SyncTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="SyncTask"/> class.</summary>
    public SyncTask(SyncService syncService, ILogger<SyncTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "YouTube Sync";

    /// <inheritdoc />
    public string Key => "YouTubeSync";

    /// <inheritdoc />
    public string Description => "Syncs configured YouTube channels and playlists into Jellyfin by generating .strm and .nfo files.";

    /// <inheritdoc />
    public string Category => "YouTube";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("YouTube Sync task started.");
        await _syncService.SyncAllAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("YouTube Sync task completed.");
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        ];
    }
}
