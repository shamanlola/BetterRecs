using Jellyfin.Plugin.BetterRecs.Api;
using Jellyfin.Plugin.BetterRecs.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.BetterRecs;

/// <summary>
/// Registers plugin services with Jellyfin's dependency-injection container.
/// Jellyfin discovers IPluginServiceRegistrator implementations automatically.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Background feature index — one instance, both injectable and run as a hosted
        // service so it builds at startup and refreshes itself.
        serviceCollection.AddSingleton<SimilarityIndex>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SimilarityIndex>());

        // Core similarity engine — singleton so its internal state isn't rebuilt per request.
        serviceCollection.AddSingleton<SimilarityService>();

        // Builds the "Because you watched X" home-screen rows, served by RecommendationsController.
        serviceCollection.AddSingleton<RecommendationService>();

        // The middleware itself (registered as transient so HttpContext lifetime is safe).
        serviceCollection.AddTransient<SimilarItemsMiddleware>();

        // IStartupFilter inserts the middleware at application startup, before routing,
        // so it can intercept /Items/{id}/Similar before Jellyfin's controller handles it.
        serviceCollection.AddSingleton<IStartupFilter, SimilarItemsMiddlewareFilter>();
    }
}
