using Jellyfin.Plugin.MyLists.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MyLists;

/// <summary>My Lists: ordered personal movie lists (manual, rule based or imported) with watched titles greyed out.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "My Lists";

    public override Guid Id => Guid.Parse("8c2b7d54-3e1a-4f6b-9a0d-5b7c1e2f4a63");

    public override string Description => "Watch-checklists: ordered movie lists (IMDb Top 250, a director's films, a friend's top 10) where movies you have already watched stay in place, greyed out.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo { Name = Name, EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html" },
    ];
}
