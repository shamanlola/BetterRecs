using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.BetterRecs.Configuration;

public enum PeopleMatchMode
{
    None,
    DirectorsOnly,
    DirectorsAndActors
}

public class PluginConfiguration : BasePluginConfiguration
{
    // ── Scoring weights (0–100 each) ─────────────────────────────────────────
    public int GenreWeight { get; set; } = 40;
    public int TagWeight { get; set; } = 20;
    public int CommunityRatingWeight { get; set; } = 15;
    public int ParentalRatingWeight { get; set; } = 10;
    public int YearWeight { get; set; } = 10;
    public int PeopleWeight { get; set; } = 5;

    // ── Filters ───────────────────────────────────────────────────────────────
    // Community rating is on a 0–10 scale; items further than this are excluded.
    public float MaxCommunityRatingDistance { get; set; } = 3.0f;

    // Steps on the parental rating ladder (0–5). 2 means R is acceptable for PG-13.
    public int MaxParentalRatingDistance { get; set; } = 2;

    // Candidate must share at least this many genres with the source item.
    public int MinGenreMatches { get; set; } = 1;

    // Candidate must share at least this many tags with the source item.
    public int MinTagMatches { get; set; } = 0;

    public bool ExcludeWatched { get; set; } = false;
    public bool SameMediaTypeOnly { get; set; } = true;

    // ── Results ───────────────────────────────────────────────────────────────
    // When fewer than MinResults pass all filters, filters are progressively
    // relaxed (if RelaxFiltersWhenTooFewResults is true).
    public int MinResults { get; set; } = 6;
    public int MaxResults { get; set; } = 12;

    // 0 = purely deterministic (highest score wins).
    // 100 = highly exploratory (noise added; lower-scoring items can surface).
    public int Randomness { get; set; } = 20;

    // ── Advanced ──────────────────────────────────────────────────────────────
    // Items more than this many years apart score 0 on the year dimension.
    public int MaxYearGap { get; set; } = 20;

    public PeopleMatchMode PeopleMatch { get; set; } = PeopleMatchMode.DirectorsOnly;

    // How many top-billed actors to consider when PeopleMatch includes actors.
    public int TopActorCount { get; set; } = 5;

    // Automatically relax MinGenreMatches/MinTagMatches/rating distance when
    // the result set would otherwise be smaller than MinResults.
    public bool RelaxFiltersWhenTooFewResults { get; set; } = true;

    // How often (minutes) the background feature index is fully rebuilt. The index
    // also rebuilds automatically when the library changes, so this is just an upper
    // bound on staleness. Minimum 1.
    public int IndexRefreshMinutes { get; set; } = 30;

    // ── Home-screen recommendations ──────────────────────────────────────────────
    // Master switch for the recommendation feature: the HSS home-screen row and the
    // /BetterRecs/Recommendations API both honour this.
    public bool HomeSectionsEnabled { get; set; } = true;

    // How many per-title "Because you watched X" rows the /BetterRecs/Recommendations
    // API returns in one response. This governs ONLY that raw API endpoint (consumed
    // by front-ends such as KefinTweaks); the HSS integration always renders a single
    // blended row, so this is not surfaced in the settings UI.
    public int HomeSectionCount { get; set; } = 3;

    // How many recommended items to put in the row.
    public int HomeSectionItemCount { get; set; } = 12;

    // Heading for the single blended row injected into the home screen via the Home
    // Screen Sections (HSS) plugin.
    public string HomeSectionTitle { get; set; } = "Recommended for You";

    // How many of the user's recently-played titles are blended together to build
    // the single HSS home row. Each contributes its top matches; the results are
    // merged, de-duplicated and interleaved so the row reflects several things the
    // user watched rather than just the most recent one.
    public int HomeSectionBlendCount { get; set; } = 5;

    // The pool of most-recently-played items to draw the blend's source titles from.
    // Sources are picked from this pool (optionally at random — see
    // HomeSectionShuffleSources) so the row reflects more than just the latest title.
    public int RecentlyWatchedPoolSize { get; set; } = 20;

    // When true, the source titles are re-picked at random from the recent pool on
    // every request, so the row is regenerated and changes on each home-screen refresh.
    // When false, the most recently played titles are used in order (stable row).
    public bool HomeSectionShuffleSources { get; set; } = true;

    // Whether already-watched items may appear *inside* the recommendation row.
    // Off by default — recommendations should surface things you haven't seen.
    public bool RecommendWatchedItems { get; set; } = false;

    // ── Feature flags ─────────────────────────────────────────────────────────
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Shallow copy used to derive a per-section configuration (different MaxResults,
    /// watched/cross-type behaviour) without mutating the saved settings. Every field
    /// is a value type or string, so a memberwise clone is safe.
    /// </summary>
    public PluginConfiguration Clone() => (PluginConfiguration)MemberwiseClone();
}
