using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MyLists;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ListStore>();
        serviceCollection.AddSingleton<Resolver>();
        serviceCollection.AddSingleton<ListService>();
        serviceCollection.AddHostedService<SidebarInjector>();
    }
}
