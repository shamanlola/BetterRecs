using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.BetterRecs.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterRecs.Services;

/// <summary>
/// One "Because you watched X" row: a heading, the source item it was derived
/// from, and the recommended items to display.
/// </summary>
public sealed class RecommendationSection
{
    public required BaseItem Source { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyList<BaseItem> Items { get; init; }
}

/// <summary>
/// Builds personalised "Because you watched X" rows for the home screen.
/// It picks a handful of titles the user has recently played, then reuses the
/// exact same scoring engine as the "Similar Items" feature to populate each row.
/// </summary>
public sealed class RecommendationService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly SimilarityService _similarityService;
    private readonly ILogger<RecommendationService> _logger;

    public RecommendationService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        SimilarityService similarityService,
        ILogger<RecommendationService> logger)
    {
        _libraryManager    = libraryManager;
        _userManager       = userManager;
        _similarityService = similarityService;
        _logger            = logger;
    }

    /// <summary>
    /// Produces up to <see cref="PluginConfiguration.HomeSectionCount"/> rows for the
    /// given user. Returns an empty list when the feature is disabled, the user is
    /// unknown, or the user has not watched anything yet.
    /// </summary>
    public IReadOnlyList<RecommendationSection> GetBecauseYouWatched(Guid userId, PluginConfiguration config)
    {
        if (!config.HomeSectionsEnabled)
            return [];

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            _logger.LogWarning("BetterRecs: recommendations requested for unknown user {UserId}", userId);
            return [];
        }

        var sources = PickSourceItems(user, config);
        if (sources.Count == 0)
            return [];

        // Per-section settings derived from the saved config: smaller result count,
        // and the home-section-specific watched / cross-type toggles applied on top.
        var sectionConfig = config.Clone();
        sectionConfig.MaxResults        = Math.Max(1, config.HomeSectionItemCount);
        sectionConfig.MinResults        = Math.Min(config.MinResults, sectionConfig.MaxResults);
        sectionConfig.ExcludeWatched    = !config.RecommendWatchedItems;
        sectionConfig.SameMediaTypeOnly = !config.CrossTypeRecommendations;

        var sections = new List<RecommendationSection>(sources.Count);

        // Don't repeat the same recommendation across two rows in one response.
        var seen = new HashSet<Guid>(sources.Select(s => s.Id));

        foreach (var source in sources)
        {
            if (sections.Count >= Math.Max(1, config.HomeSectionCount)) break;

            var similar = _similarityService.GetSimilarItems(source.Id, userId, sectionConfig);

            var items = new List<BaseItem>(sectionConfig.MaxResults);
            foreach (var item in similar)
            {
                if (!seen.Add(item.Id)) continue;
                items.Add(item);
            }

            if (items.Count == 0) continue;

            sections.Add(new RecommendationSection
            {
                Source = source,
                Title  = $"Because you watched {source.Name}",
                Items  = items,
            });
        }

        _logger.LogDebug(
            "BetterRecs: built {Count} 'Because you watched' sections for user {UserId}",
            sections.Count, userId);

        return sections;
    }

    /// <summary>
    /// Returns the source titles to build rows around: the user's recently played
    /// movies/series, optionally shuffled so the home screen rotates over time.
    /// </summary>
    private IReadOnlyList<BaseItem> PickSourceItems(User user, PluginConfiguration config)
    {
        var poolSize = Math.Max(config.HomeSectionCount, config.RecentlyWatchedPoolSize);

        var query = new InternalItemsQuery(user)
        {
            IsPlayed         = true,
            Recursive        = true,
            IsVirtualItem    = false,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            OrderBy          = [(ItemSortBy.DatePlayed, Jellyfin.Database.Implementations.Enums.SortOrder.Descending)],
            Limit            = poolSize,
        };

        var recent = _libraryManager.GetItemList(query);
        if (recent.Count == 0)
            return [];

        if (config.HomeSectionShuffleSources)
        {
            // Fisher–Yates over a copy; keeps the recency-ordered query but rotates
            // which of the recent titles actually become rows this time.
            var copy = recent.ToList();
            for (var i = copy.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (copy[i], copy[j]) = (copy[j], copy[i]);
            }

            return copy;
        }

        return (IReadOnlyList<BaseItem>)recent;
    }
}
