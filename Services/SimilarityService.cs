using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.BetterRecs.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

// InternalPeopleQuery / InternalItemsQuery are the stable lookup APIs across Jellyfin 10.9+

namespace Jellyfin.Plugin.BetterRecs.Services;

public class SimilarityService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly SimilarityIndex _index;
    private readonly ILogger<SimilarityService> _logger;

    public SimilarityService(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        SimilarityIndex index,
        ILogger<SimilarityService> logger)
    {
        _libraryManager  = libraryManager;
        _userManager     = userManager;
        _userDataManager = userDataManager;
        _index           = index;
        _logger          = logger;
    }

    /// <summary>
    /// Returns up to config.MaxResults items ordered by similarity to the source item.
    /// Uses the pre-built feature index for an all-in-memory hot path; falls back to a
    /// live computation only when the index is cold or the source isn't indexed.
    /// </summary>
    public IReadOnlyList<BaseItem> GetSimilarItems(
        Guid sourceItemId,
        Guid? userId,
        PluginConfiguration config)
    {
        var user     = userId.HasValue ? _userManager.GetUserById(userId.Value) : null;
        var snapshot = _index.Current;

        // Fast path: everything we need is pre-computed in the snapshot.
        if (snapshot is not null && snapshot.ById.TryGetValue(sourceItemId, out var sourceFeatures))
            return ComputeSimilar(sourceFeatures, snapshot.Items, config, user);

        // Fallback: index not ready yet, or the source is a newly-added / non-indexed
        // item. Nudge a rebuild and compute this one request the slow way.
        _index.RequestRefresh();
        return ComputeLive(sourceItemId, user, config);
    }

    // ── Live fallback (slow path) ───────────────────────────────────────────────

    private IReadOnlyList<BaseItem> ComputeLive(Guid sourceItemId, User? user, PluginConfiguration config)
    {
        var source = _libraryManager.GetItemById(sourceItemId);
        if (source is null)
        {
            _logger.LogWarning("BetterRecs: source item {Id} not found", sourceItemId);
            return [];
        }

        // Restrict to specific item kinds. This both scopes candidates sensibly and
        // avoids an unrestricted GetItemList, which tries to deserialize every row in
        // the library DB and throws if any has a type the server can't map.
        var kinds = new HashSet<BaseItemKind> { source.GetBaseItemKind() };
        if (!config.SameMediaTypeOnly)
        {
            kinds.Add(BaseItemKind.Movie);
            kinds.Add(BaseItemKind.Series);
        }

        var query = new InternalItemsQuery
        {
            Recursive        = true,
            IsVirtualItem    = false,
            IncludeItemTypes = [.. kinds],
        };

        // Scope to the user so results respect that user's library access and
        // parental controls (matches stock Jellyfin behaviour).
        if (user is not null) query.User = user;

        var allItems = _libraryManager.GetItemList(query);
        var pool     = new StringPool();

        var sourceFeatures = FeatureExtractor.Extract(source, pool, config, PeopleFor(source, config));

        var candidates = new List<ItemFeatures>();
        foreach (var item in allItems)
        {
            if (item.Id == sourceItemId) continue;
            // Skip cross-type work early when we only want same-type results.
            if (config.SameMediaTypeOnly && item.GetType() != source.GetType()) continue;
            candidates.Add(FeatureExtractor.Extract(item, pool, config, PeopleFor(item, config)));
        }

        return ComputeSimilar(sourceFeatures, candidates, config, user);
    }

    private IReadOnlyList<PersonInfo> PeopleFor(BaseItem item, PluginConfiguration config)
        => config.PeopleMatch == PeopleMatchMode.None
            ? Array.Empty<PersonInfo>()
            : _libraryManager.GetPeople(new InternalPeopleQuery { ItemId = item.Id });

    // ── Core scoring (shared by both paths) ─────────────────────────────────────

    private IReadOnlyList<BaseItem> ComputeSimilar(
        ItemFeatures source,
        IReadOnlyList<ItemFeatures> candidates,
        PluginConfiguration config,
        User? user)
    {
        var pre = new List<ItemFeatures>();
        foreach (var candidate in candidates)
        {
            if (candidate.Item.Id == source.Item.Id) continue;
            if (config.SameMediaTypeOnly && candidate.ItemType != source.ItemType) continue;
            if (IntersectCount(source.Genres, candidate.Genres) < config.MinGenreMatches) continue;
            pre.Add(candidate);
        }

        var scored   = Score(pre, source, config);
        var filtered = ApplyFilters(scored, source, config);

        if (config.RelaxFiltersWhenTooFewResults && filtered.Count < config.MinResults)
            filtered = RelaxAndRetry(scored, source, config, candidates);

        return Materialize(SortWithRandomness(filtered, config), config, user);
    }

    private static List<ScoredFeature> Score(
        IReadOnlyList<ItemFeatures> candidates,
        ItemFeatures source,
        PluginConfiguration config)
    {
        var results = new List<ScoredFeature>(candidates.Count);

        foreach (var candidate in candidates)
        {
            double weightSum = 0;
            double scoreSum  = 0;

            void Add(double weight, double? score)
            {
                if (weight <= 0 || score is null) return;
                weightSum += weight;
                scoreSum  += weight * score.Value;
            }

            Add(config.GenreWeight,           Jaccard(source.Genres, candidate.Genres));
            Add(config.TagWeight,             Jaccard(source.Tags, candidate.Tags));
            Add(config.CommunityRatingWeight, CommunityRatingScore(source, candidate));
            Add(config.ParentalRatingWeight,  ParentalRatingScore(source, candidate));
            Add(config.YearWeight,            YearScore(source, candidate, config));
            Add(config.PeopleWeight,          Jaccard(source.People, candidate.People));

            var total = weightSum > 0 ? scoreSum / weightSum : 0;

            results.Add(new ScoredFeature(
                candidate, total,
                tagIntersection: IntersectCount(source.Tags, candidate.Tags)));
        }

        return results;
    }

    private static List<ScoredFeature> ApplyFilters(
        List<ScoredFeature> scored,
        ItemFeatures source,
        PluginConfiguration config)
    {
        return scored.Where(s =>
        {
            if (s.TagIntersection < config.MinTagMatches) return false;

            if (source.CommunityRating.HasValue)
            {
                // Missing candidate rating → treat as maximum distance (10 points).
                var distance = s.Feature.CommunityRating.HasValue
                    ? Math.Abs(source.CommunityRating.Value - s.Feature.CommunityRating.Value)
                    : 10f;
                if (distance > config.MaxCommunityRatingDistance) return false;
            }

            if (source.RatingNumeric.HasValue)
            {
                // Known source rating but unrecognised/missing candidate rating →
                // maximum distance (5 steps), so unrated items don't bypass the filter.
                var distance = s.Feature.RatingNumeric.HasValue
                    ? Math.Abs(source.RatingNumeric.Value - s.Feature.RatingNumeric.Value)
                    : 5;
                if (distance > config.MaxParentalRatingDistance) return false;
            }

            return true;
        }).ToList();
    }

    private static List<ScoredFeature> RelaxAndRetry(
        List<ScoredFeature> scored,
        ItemFeatures source,
        PluginConfiguration config,
        IReadOnlyList<ItemFeatures> allCandidates)
    {
        // Relax tags first against the items we already scored.
        var relaxed = CloneRelaxed(config, minTagMatches: 0);
        var result  = ApplyFilters(scored, source, relaxed);
        if (result.Count >= config.MinResults) return result;

        // Drop the genre minimum — rescore every (same-type) candidate.
        var pre = new List<ItemFeatures>();
        foreach (var candidate in allCandidates)
        {
            if (candidate.Item.Id == source.Item.Id) continue;
            if (config.SameMediaTypeOnly && candidate.ItemType != source.ItemType) continue;
            pre.Add(candidate);
        }

        var rescored = Score(pre, source, relaxed);
        result = ApplyFilters(rescored, source, relaxed);
        if (result.Count >= config.MinResults) return result;

        // Last resort: widen the rating distances entirely.
        relaxed.MaxCommunityRatingDistance = 10f;
        relaxed.MaxParentalRatingDistance  = 5;
        return ApplyFilters(rescored, source, relaxed);
    }

    private static PluginConfiguration CloneRelaxed(PluginConfiguration config, int minTagMatches)
        => new()
        {
            GenreWeight                = config.GenreWeight,
            TagWeight                  = config.TagWeight,
            CommunityRatingWeight      = config.CommunityRatingWeight,
            ParentalRatingWeight       = config.ParentalRatingWeight,
            YearWeight                 = config.YearWeight,
            PeopleWeight               = config.PeopleWeight,
            MaxCommunityRatingDistance = config.MaxCommunityRatingDistance,
            MaxParentalRatingDistance  = config.MaxParentalRatingDistance,
            MinGenreMatches            = 0,             // genre minimum dropped on relax
            MinTagMatches              = minTagMatches,
            SameMediaTypeOnly          = config.SameMediaTypeOnly,
            MaxYearGap                 = config.MaxYearGap,
        };

    private IReadOnlyList<BaseItem> Materialize(
        IEnumerable<ScoredFeature> ordered,
        PluginConfiguration config,
        User? user)
    {
        var results = new List<BaseItem>(config.MaxResults);
        foreach (var scored in ordered)
        {
            if (results.Count >= config.MaxResults) break;
            // "Watched" is checked here (not during scoring) so we only do user-data
            // lookups for the handful of items we actually intend to return.
            if (config.ExcludeWatched && user is not null && IsWatched(user, scored.Feature.Item)) continue;
            results.Add(scored.Feature.Item);
        }

        return results;
    }

    private bool IsWatched(User user, BaseItem item)
        => _userDataManager.GetUserData(user, item)?.Played ?? false;

    // ── Randomness & final sort ─────────────────────────────────────────────────

    private static IEnumerable<ScoredFeature> SortWithRandomness(List<ScoredFeature> items, PluginConfiguration config)
    {
        if (config.Randomness <= 0)
            return items.OrderByDescending(s => s.Score);

        var temperature = config.Randomness / 100.0;
        const double noiseScale = 0.3;
        foreach (var item in items)
            item.NoisyScore = item.Score + temperature * Random.Shared.NextDouble() * noiseScale;

        return items.OrderByDescending(s => s.NoisyScore);
    }

    // ── Dimension scorers ───────────────────────────────────────────────────────

    private static double? CommunityRatingScore(ItemFeatures source, ItemFeatures candidate)
    {
        if (!source.CommunityRating.HasValue || !candidate.CommunityRating.HasValue) return null;
        return 1.0 - (Math.Abs(source.CommunityRating.Value - candidate.CommunityRating.Value) / 10.0);
    }

    private static double? ParentalRatingScore(ItemFeatures source, ItemFeatures candidate)
    {
        if (!source.RatingNumeric.HasValue || !candidate.RatingNumeric.HasValue) return null;
        return 1.0 - (Math.Abs(source.RatingNumeric.Value - candidate.RatingNumeric.Value) / 5.0);
    }

    private static double? YearScore(ItemFeatures source, ItemFeatures candidate, PluginConfiguration config)
    {
        if (!source.Year.HasValue || !candidate.Year.HasValue) return null;
        var gap = Math.Abs(source.Year.Value - candidate.Year.Value);
        return Math.Max(0.0, 1.0 - (gap / (double)config.MaxYearGap));
    }

    // ── Sorted-array set utilities (no allocation, linear merge) ─────────────────

    private static double Jaccard(string[] a, string[] b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        var intersection = IntersectCount(a, b);
        var union = a.Length + b.Length - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static int IntersectCount(string[] a, string[] b)
    {
        int i = 0, j = 0, count = 0;
        while (i < a.Length && j < b.Length)
        {
            var cmp = string.CompareOrdinal(a[i], b[j]);
            if (cmp == 0) { count++; i++; j++; }
            else if (cmp < 0) { i++; }
            else { j++; }
        }

        return count;
    }

    private sealed class ScoredFeature
    {
        public ItemFeatures Feature { get; }
        public double Score { get; }
        public double NoisyScore { get; set; }
        public int TagIntersection { get; }

        public ScoredFeature(ItemFeatures feature, double score, int tagIntersection)
        {
            Feature         = feature;
            Score           = score;
            NoisyScore      = score;
            TagIntersection = tagIntersection;
        }
    }
}
