using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Jellyfin.Plugin.BetterRecs.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterRecs.Services;

/// <summary>
/// Registers BetterRecs' single blended "Recommended for You" row with the Home
/// Screen Sections (HSS) plugin at server startup.
///
/// HSS is loaded in a separate plugin load context, so — per its documented
/// integration contract — we reach it only by reflection, never a direct assembly
/// reference. We hand HSS a payload naming the assembly/class/method it should invoke
/// to fetch results (<see cref="HomeScreenResultsHandler"/>); HSS instantiates that
/// handler against the server's service provider on each request.
///
/// To stay robust across load contexts we build the payload <c>JObject</c> using the
/// exact <c>Newtonsoft.Json.Linq.JObject</c> type that HSS itself expects (taken from
/// the parameter type of its <c>RegisterSection</c> method), rather than referencing
/// Newtonsoft ourselves — that guarantees the argument type matches and keeps us free
/// of a Newtonsoft dependency. If HSS isn't installed this no-ops quietly.
/// </summary>
public sealed class HomeScreenRegistrationTask : IScheduledTask
{
    // Stable id for our section so re-registration (restart / re-run of this task)
    // overwrites the same entry instead of creating duplicates.
    private const string SectionId = "b7e1c4a2-9d3f-4a6b-8c2e-5f0a1d7e3b94";

    private readonly ILogger<HomeScreenRegistrationTask> _logger;

    public HomeScreenRegistrationTask(ILogger<HomeScreenRegistrationTask> logger)
    {
        _logger = logger;
    }

    public string Name        => "BetterRecs: register home-screen section";
    public string Key         => "BetterRecs.RegisterHomeScreenSection";
    public string Description => "Registers the BetterRecs blended recommendation row with the Home Screen Sections plugin.";
    public string Category    => "BetterRecs";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Runs once the server has fully started, by which point all plugins — HSS
        // included — are loaded and HSS's service provider is wired up.
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        RegisterSection();
        progress.Report(100);
        return Task.CompletedTask;
    }

    private void RegisterSection()
    {
        var plugin = Plugin.Instance;
        if (plugin is null || !plugin.Configuration.Enabled || !plugin.Configuration.HomeSectionsEnabled)
        {
            _logger.LogInformation("BetterRecs: home sections disabled; skipping HSS registration.");
            return;
        }

        var hssAssembly = AssemblyLoadContext.All
            .SelectMany(ctx => ctx.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains(".HomeScreenSections", StringComparison.Ordinal) ?? false);

        if (hssAssembly is null)
        {
            _logger.LogInformation(
                "BetterRecs: Home Screen Sections plugin not found; skipping home-row registration. " +
                "Install 'Home Screen Sections' to surface the '{Title}' row.",
                plugin.Configuration.HomeSectionTitle);
            return;
        }

        var pluginInterface = hssAssembly.GetType("Jellyfin.Plugin.HomeScreenSections.PluginInterface");
        var register        = pluginInterface?.GetMethod("RegisterSection", BindingFlags.Public | BindingFlags.Static);
        if (register is null || register.GetParameters().Length != 1)
        {
            _logger.LogWarning(
                "BetterRecs: found Home Screen Sections but its PluginInterface.RegisterSection is missing or has " +
                "an unexpected signature; update HSS to a compatible version.");
            return;
        }

        var title = plugin.Configuration.HomeSectionTitle;

        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"]              = SectionId,
            ["displayText"]     = title,
            ["limit"]           = 1,
            ["additionalData"]  = string.Empty,
            ["resultsAssembly"] = typeof(HomeScreenResultsHandler).Assembly.FullName,
            ["resultsClass"]    = typeof(HomeScreenResultsHandler).FullName,
            ["resultsMethod"]   = nameof(HomeScreenResultsHandler.GetRecommendedForYou),
        });

        try
        {
            // Parse the payload into HSS's own JObject type so the argument matches the
            // RegisterSection parameter across load contexts.
            var jobjectType = register.GetParameters()[0].ParameterType;
            var parse       = jobjectType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            if (parse is null)
            {
                _logger.LogWarning("BetterRecs: could not locate JObject.Parse on HSS's JSON type; aborting registration.");
                return;
            }

            var payloadObj = parse.Invoke(null, [payloadJson]);
            register.Invoke(null, [payloadObj]);

            _logger.LogInformation("BetterRecs: registered '{Title}' home-screen section with HSS.", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BetterRecs: failed to register home-screen section with HSS.");
        }
    }
}
