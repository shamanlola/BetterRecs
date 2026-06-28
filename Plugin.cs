using Jellyfin.Plugin.BetterRecs.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterRecs;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Earliest guaranteed execution point — if this line is absent from the
        // server log, the running DLL is NOT this build (deployment problem).
        logger.LogInformation(
            "BetterRecs v{Version} loaded (Enabled={Enabled})",
            Version, Configuration.Enabled);
    }

    public override string Name => "BetterRecs";

    public override Guid Id => new("d3a7e2b1-4c9f-4e8a-b5d6-2f1c8e3a7b9d");

    public override string Description =>
        "Better recommendations for Jellyfin. Replaces the built-in Similar Items with a " +
        "multi-dimensional weighted scoring engine, and serves personalised \"Because you watched …\" " +
        "rows via the /BetterRecs/Recommendations API. When the Home Screen Sections (HSS) plugin is " +
        "installed, it also adds a blended \"Recommended for You\" row to the home screen. " +
        "Matches on genres, tags, ratings, release year, and cast/crew. Fully configurable.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name         = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
        };
    }
}
