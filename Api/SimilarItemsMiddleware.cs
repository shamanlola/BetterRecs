using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.BetterRecs.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// User type is Jellyfin.Data.Entities.User (not a Controller type)

namespace Jellyfin.Plugin.BetterRecs.Api;

/// <summary>
/// Intercepts GET /Items/{itemId}/Similar requests before Jellyfin's built-in
/// handler can respond, and substitutes our enhanced similarity results.
/// The response shape is identical to what stock Jellyfin returns so all
/// existing clients (Web, Infuse, Swiftfin, etc.) work without modification.
/// </summary>
public sealed class SimilarItemsMiddleware : IMiddleware
{
    // Matches the "Similar" endpoints for the video item kinds the web/mobile
    // clients hit (movies and series both call /Items/{id}/Similar). The id is
    // captured loosely and validated with Guid.TryParse, because Jellyfin
    // serialises item ids in URLs WITHOUT hyphens ("N" format, 32 hex chars),
    // not the hyphenated "D" format.
    private static readonly Regex _route =
        new(@"^/(?:Items|Movies|Shows|Trailers)/([^/]+)/Similar$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Jellyfin's API serialises responses with PascalCase property names and a
    // set of custom converters (Guid as dashless "N", string-enums, etc.).
    // Reusing JsonDefaults.Options guarantees the bytes we write are byte-for-byte
    // compatible with what every client already expects from the stock endpoint.
    private static readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

    private readonly SimilarityService _similarityService;
    private readonly IDtoService _dtoService;
    private readonly IUserManager _userManager;
    private readonly ILogger<SimilarItemsMiddleware> _logger;

    public SimilarItemsMiddleware(
        SimilarityService similarityService,
        IDtoService dtoService,
        IUserManager userManager,
        ILogger<SimilarItemsMiddleware> logger)
    {
        _similarityService = similarityService;
        _dtoService        = dtoService;
        _userManager       = userManager;
        _logger            = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var plugin = Plugin.Instance;

        // Pass through when plugin is disabled or request doesn't match.
        if (plugin is null || !plugin.Configuration.Enabled
            || context.Request.Method != HttpMethods.Get)
        {
            await next(context);
            return;
        }

        var match = _route.Match(context.Request.Path.Value ?? string.Empty);
        if (!match.Success)
        {
            await next(context);
            return;
        }

        // Item ids arrive in either dashless ("N") or hyphenated ("D") form; both
        // parse here. If it isn't a valid Guid, let stock Jellyfin deal with it.
        if (!Guid.TryParse(match.Groups[1].Value, out var itemId))
        {
            await next(context);
            return;
        }

        try
        {
            // Parse optional UserId from query string (query keys are case-insensitive,
            // so this matches the "UserId" the clients actually send).
            Guid? userId = null;
            if (context.Request.Query.TryGetValue("userId", out var userIdStr)
                && Guid.TryParse(userIdStr, out var parsedUserId))
            {
                userId = parsedUserId;
            }

            // Respect the client's requested limit when present (stock honours it).
            int? limit = null;
            if (context.Request.Query.TryGetValue("limit", out var limitStr)
                && int.TryParse(limitStr, out var parsedLimit) && parsedLimit > 0)
            {
                limit = parsedLimit;
            }

            // Parse optional Fields query parameter for DTO projection.
            var fields = ParseItemFields(context.Request.Query["fields"]);

            var config = plugin.Configuration;
            var items  = _similarityService.GetSimilarItems(itemId, userId, config);

            if (limit.HasValue && items.Count > limit.Value)
                items = items.Take(limit.Value).ToList();

            var user = userId.HasValue ? _userManager.GetUserById(userId.Value) : null;
            var dtoOptions = new DtoOptions { Fields = fields };

            var dtos = items
                .Select(item => _dtoService.GetBaseItemDto(item, dtoOptions, user))
                .ToArray();

            var result = new QueryResult<BaseItemDto>
            {
                Items            = dtos,
                TotalRecordCount = dtos.Length,
            };

            _logger.LogDebug(
                "BetterRecs: served {Count} enhanced similar items for {ItemId}",
                dtos.Length, itemId);

            context.Response.StatusCode  = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.Body, result, _jsonOptions);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            // Never break the page: if anything goes wrong before we've written a
            // response, log it and fall back to Jellyfin's built-in handler.
            _logger.LogError(ex, "BetterRecs: failed to build similar items for {ItemId}; falling back to stock", itemId);
            await next(context);
        }
    }

    private static ItemFields[] ParseItemFields(Microsoft.Extensions.Primitives.StringValues raw)
    {
        if (raw.Count == 0) return [];

        var result = new List<ItemFields>();
        foreach (var value in raw)
        {
            if (string.IsNullOrEmpty(value)) continue;
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<ItemFields>(part, ignoreCase: true, out var field))
                    result.Add(field);
            }
        }

        return [.. result];
    }
}

/// <summary>
/// IStartupFilter that inserts SimilarItemsMiddleware early in the pipeline,
/// before ASP.NET Core's routing middleware, so it can short-circuit requests
/// before Jellyfin's own controller handles them.
/// </summary>
public sealed class SimilarItemsMiddlewareFilter : IStartupFilter
{
    private readonly ILogger<SimilarItemsMiddlewareFilter> _logger;

    public SimilarItemsMiddlewareFilter(ILogger<SimilarItemsMiddlewareFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            // Inserted ahead of Jellyfin's routing/endpoint middleware so it can
            // short-circuit the Similar endpoints before the stock controller runs.
            _logger.LogInformation("BetterRecs: similar-items interceptor inserted into the request pipeline");
            app.UseMiddleware<SimilarItemsMiddleware>();
            next(app);
        };
}
