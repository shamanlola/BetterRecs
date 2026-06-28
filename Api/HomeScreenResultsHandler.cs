using Jellyfin.Plugin.BetterRecs.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterRecs.Api;

/// <summary>
/// Payload the Home Screen Sections (HSS) plugin serialises into a registered
/// section's results method. HSS sends a small JSON object ({ UserId, AdditionalData })
/// and deserialises it into whatever parameter type the method declares, so this
/// mirrors that shape. It is deliberately independent of HSS's own
/// <c>HomeScreenSectionPayload</c> type: HSS lives in a separate plugin load context
/// and must only be reached via reflection, never a direct assembly reference.
/// </summary>
public sealed class HomeScreenSectionPayload
{
    public Guid UserId { get; set; }

    public string? AdditionalData { get; set; }
}

/// <summary>
/// Produces the items for the single blended "Recommended for You" row that
/// BetterRecs injects into the home screen through HSS. HSS instantiates this class
/// itself (via <c>ActivatorUtilities</c> against the server's service provider) and
/// invokes <see cref="GetRecommendedForYou"/> by reflection, so the constructor may
/// take any service registered in Jellyfin's container — and the type itself does
/// not need to be registered.
/// </summary>
public sealed class HomeScreenResultsHandler
{
    private readonly RecommendationService _recommendationService;
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;
    private readonly ILogger<HomeScreenResultsHandler> _logger;

    public HomeScreenResultsHandler(
        RecommendationService recommendationService,
        IDtoService dtoService,
        IUserManager userManager,
        ILogger<HomeScreenResultsHandler> logger)
    {
        _recommendationService = recommendationService;
        _dtoService            = dtoService;
        _userManager           = userManager;
        _logger                = logger;
    }

    /// <summary>
    /// Invoked by HSS for each render of the row. Returns the blended recommendations
    /// in the standard <see cref="QueryResult{T}"/> of <see cref="BaseItemDto"/> shape
    /// the home-screen renderer expects. The return type is a Jellyfin core type shared
    /// across plugin load contexts, so HSS's <c>as QueryResult&lt;BaseItemDto&gt;</c> cast
    /// resolves cleanly.
    /// </summary>
    public QueryResult<BaseItemDto> GetRecommendedForYou(HomeScreenSectionPayload payload)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || !plugin.Configuration.Enabled || !plugin.Configuration.HomeSectionsEnabled)
            return new QueryResult<BaseItemDto>();

        var user = _userManager.GetUserById(payload.UserId);
        if (user is null)
            return new QueryResult<BaseItemDto>();

        try
        {
            var items = _recommendationService.GetCombinedRecommendations(payload.UserId, plugin.Configuration);
            if (items.Count == 0)
                return new QueryResult<BaseItemDto>();

            var dtoOptions = new DtoOptions
            {
                Fields =
                [
                    ItemFields.PrimaryImageAspectRatio,
                    ItemFields.MediaSourceCount,
                ],
                ImageTypes =
                [
                    ImageType.Primary,
                    ImageType.Backdrop,
                    ImageType.Thumb,
                ],
                ImageTypeLimit = 1,
            };

            var dtos = _dtoService.GetBaseItemDtos(items.ToList(), dtoOptions, user);
            return new QueryResult<BaseItemDto>(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BetterRecs: failed to build blended home row for user {UserId}", payload.UserId);
            return new QueryResult<BaseItemDto>();
        }
    }
}
