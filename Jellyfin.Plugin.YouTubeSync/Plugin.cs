using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.YouTubeSync;

/// <summary>Main plugin entry point.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "YouTubeSync";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("55a3502b-b6b2-4a3c-93d7-f3c4e7b1e0d5");

    /// <summary>Gets the running plugin instance (set during construction).</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "youtubeSyncConfig",
                EmbeddedResourcePath = $"{ns}.Web.youtubeSyncConfig.html"
            },
            new PluginPageInfo
            {
                Name = "youtubeSyncConfigjs",
                EmbeddedResourcePath = $"{ns}.Web.youtubeSyncConfig.js"
            }
        };
    }
}

/// <summary>Registers all plugin services with the Jellyfin DI container.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<YtDlpService>();
        serviceCollection.AddSingleton<FormatSelector>();
        serviceCollection.AddSingleton<SimpleResolveCache>();
        serviceCollection.AddSingleton<ResolveService>();
        serviceCollection.AddSingleton<SyncService>();
        serviceCollection.AddSingleton<IScheduledTask, SyncTask>();
    }
}
