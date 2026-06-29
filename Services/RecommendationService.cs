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
        var sectionConfig = BuildSectionConfig(config);

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
    /// Builds a single blended recommendation row: it draws matches from several of
    /// the user's recently-played titles and interleaves them into one de-duplicated
    /// list. Used by the Home Screen Sections (HSS) integration, which renders one
    /// statically-titled row rather than a row per source title.
    /// </summary>
    public IReadOnlyList<BaseItem> GetCombinedRecommendations(Guid userId, PluginConfiguration config)
    {
        if (!config.HomeSectionsEnabled)
            return [];

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            _logger.LogWarning("BetterRecs: combined recommendations requested for unknown user {UserId}", userId);
            return [];
        }

        var sources = PickSourceItems(user, config);
        if (sources.Count == 0)
            return [];

        var blendCount    = Math.Clamp(config.HomeSectionBlendCount, 1, sources.Count);
        var itemCount     = Math.Max(1, config.HomeSectionItemCount);
        var sectionConfig = BuildSectionConfig(config);

        // Pull each source's matches (already ordered best-first) into its own queue.
        var queues = new List<Queue<BaseItem>>(blendCount);
        foreach (var source in sources.Take(blendCount))
        {
            var similar = _similarityService.GetSimilarItems(source.Id, userId, sectionConfig);
            if (similar.Count > 0)
                queues.Add(new Queue<BaseItem>(similar));
        }

        if (queues.Count == 0)
            return [];

        var sourcesUsed = queues.Count;

        // Round-robin interleave so every source contributes roughly equally while
        // each source's own ranking is preserved. Skip the source titles themselves
        // and any item already taken from another source's list.
        var seen   = new HashSet<Guid>(sources.Select(s => s.Id));
        var merged = new List<BaseItem>(itemCount);

        while (merged.Count < itemCount && queues.Count > 0)
        {
            for (var i = 0; i < queues.Count && merged.Count < itemCount; i++)
            {
                var queue = queues[i];

                // Advance this queue to its next not-yet-used item.
                while (queue.Count > 0)
                {
                    var candidate = queue.Dequeue();
                    if (seen.Add(candidate.Id)) { merged.Add(candidate); break; }
                }
            }

            // Drop exhausted queues so the loop can terminate.
            queues.RemoveAll(q => q.Count == 0);
        }

        _logger.LogDebug(
            "BetterRecs: built blended home row of {Count} items from {Sources} sources for user {UserId}",
            merged.Count, sourcesUsed, userId);

        return merged;
    }

    /// <summary>
    /// Derives a per-row configuration from the saved settings: a smaller result
    /// count, and the home-section-specific watched / cross-type toggles applied on
    /// top. The clone keeps the saved configuration untouched.
    /// </summary>
    private static PluginConfiguration BuildSectionConfig(PluginConfiguration config)
    {
        var sectionConfig = config.Clone();
        sectionConfig.MaxResults        = Math.Max(1, config.HomeSectionItemCount);
        sectionConfig.MinResults        = Math.Min(config.MinResults, sectionConfig.MaxResults);
        sectionConfig.ExcludeWatched    = !config.RecommendWatchedItems;
        // The blended row already mixes media types through its different source
        // titles, so each source recommends only its own type (no movie→TV jumps).
        sectionConfig.SameMediaTypeOnly = true;

        // The "Recommended for You" row gets its own discovery level, independent of
        // the global Randomness that Similar Items uses. A single 0–100 knob drives two
        // levers at once: the score-noise applied when ordering (so lower-ranked items
        // can surface) AND a proportional loosening of the candidate filters (so the
        // pool itself widens to genuinely more distant titles, not just a reshuffle of
        // near-identical ones). At discovery 0 the row keeps the saved filters exactly.
        var discovery = Math.Clamp(config.HomeSectionDiscovery, 0, 100);
        var t = discovery / 100.0;
        sectionConfig.Randomness                 = discovery;
        sectionConfig.MinGenreMatches            = (int)Math.Round(config.MinGenreMatches * (1 - t), MidpointRounding.AwayFromZero);
        sectionConfig.MinTagMatches              = (int)Math.Round(config.MinTagMatches * (1 - t), MidpointRounding.AwayFromZero);
        sectionConfig.MaxCommunityRatingDistance = config.MaxCommunityRatingDistance + (float)t * (10f - config.MaxCommunityRatingDistance);
        sectionConfig.MaxParentalRatingDistance  = config.MaxParentalRatingDistance + (int)Math.Round(t * (5 - config.MaxParentalRatingDistance), MidpointRounding.AwayFromZero);
        return sectionConfig;
    }

    /// <summary>
    /// Returns the source titles to build rows around: the user's recently played
    /// movies/series, optionally shuffled so the home screen rotates over time.
    /// </summary>
    private IReadOnlyList<BaseItem> PickSourceItems(User user, PluginConfiguration config)
    {
        // Big enough to satisfy whichever consumer asks for the most sources: the
        // per-title API rows (HomeSectionCount) or the blended row (HomeSectionBlendCount),
        // while still honouring the configured recently-watched pool for variety.
        var poolSize = Math.Max(
            Math.Max(config.HomeSectionCount, config.HomeSectionBlendCount),
            config.RecentlyWatchedPoolSize);

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
