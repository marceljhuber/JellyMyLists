using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MyLists.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether a "My Lists" entry is added to the web sidebar (needs File Transformation).</summary>
    public bool ShowInSidebar { get; set; } = true;

    /// <summary>Gets or sets a TMDB v3 API key, only needed to import TMDB lists.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;
}
