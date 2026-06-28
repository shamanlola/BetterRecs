using Jellyfin.Plugin.BetterRecs.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterRecs.Api;

/// <summary>
/// One serialised "Because you watched X" row. <see cref="Items"/> uses the same
/// <see cref="QueryResult{T}"/> of <see cref="BaseItemDto"/> shape every Jellyfin
/// client already knows how to render, so a home-section consumer can drop it
/// straight into a row.
/// </summary>
public sealed class RecommendationSectionDto
{
    public Guid SourceItemId { get; set; }

    public string? SourceItemName { get; set; }

    public string Title { get; set; } = string.Empty;

    public QueryResult<BaseItemDto> Items { get; set; } = new();
}

/// <summary>
/// Serves BetterRecs' personalised "Because you watched X" recommendation rows.
/// Unlike the Similar-Items interceptor, this is a normal authenticated Jellyfin
/// API controller, so it runs after authentication and can resolve the current user.
/// </summary>
[ApiController]
[Authorize]
[Route("BetterRecs")]
[Produces("application/json")]
public sealed class RecommendationsController : ControllerBase
{
    private readonly RecommendationService _recommendationService;
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;
    private readonly ILogger<RecommendationsController> _logger;

    public RecommendationsController(
        RecommendationService recommendationService,
        IDtoService dtoService,
        IUserManager userManager,
        ILogger<RecommendationsController> logger)
    {
        _recommendationService = recommendationService;
        _dtoService            = dtoService;
        _userManager           = userManager;
        _logger                = logger;
    }

    /// <summary>
    /// Returns the "Because you watched X" rows for a user.
    /// </summary>
    /// <param name="userId">The user to build recommendations for.</param>
    [HttpGet("Recommendations")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IReadOnlyList<RecommendationSectionDto>> GetRecommendations(
        [FromQuery] Guid userId)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || !plugin.Configuration.Enabled || !plugin.Configuration.HomeSectionsEnabled)
            return Ok(Array.Empty<RecommendationSectionDto>());

        if (userId == Guid.Empty)
            return BadRequest("A userId query parameter is required.");

        var user = _userManager.GetUserById(userId);
        if (user is null)
            return BadRequest($"Unknown user '{userId}'.");

        try
        {
            var sections   = _recommendationService.GetBecauseYouWatched(userId, plugin.Configuration);
            var dtoOptions = new DtoOptions();

            var result = sections.Select(section =>
            {
                var dtos = section.Items
                    .Select(item => _dtoService.GetBaseItemDto(item, dtoOptions, user))
                    .ToArray();

                return new RecommendationSectionDto
                {
                    SourceItemId   = section.Source.Id,
                    SourceItemName = section.Source.Name,
                    Title          = section.Title,
                    Items          = new QueryResult<BaseItemDto>(dtos),
                };
            }).ToArray();

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BetterRecs: failed to build recommendations for user {UserId}", userId);
            return Ok(Array.Empty<RecommendationSectionDto>());
        }
    }
}
