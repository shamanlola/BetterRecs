namespace Jellyfin.Plugin.BetterRecs.Services;

/// <summary>
/// Converts parental rating strings to a unified 0–5 numeric ladder so that
/// proximity between ratings can be computed as a simple integer distance.
/// </summary>
public static class RatingConverter
{
    // Separate dictionaries for film and TV because "PG" means different things
    // across systems, but we still want cross-system distance to work sensibly.
    // Both ladders use 0–5; a film-R (4) is treated as close to TV-MA (5).
    private static readonly Dictionary<string, int> _filmRatings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["G"]      = 0,
        ["PG"]     = 1,
        ["PG-13"]  = 3,
        ["R"]      = 4,
        ["NC-17"]  = 5,
        ["X"]      = 5,
    };

    private static readonly Dictionary<string, int> _tvRatings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TV-Y"]   = 0,
        ["TV-Y7"]  = 1,
        ["TV-G"]   = 2,
        ["TV-PG"]  = 3,
        ["TV-14"]  = 4,
        ["TV-MA"]  = 5,
    };

    // UK, Australian, and common European ratings mapped to the 0–5 ladder.
    private static readonly Dictionary<string, int> _otherRatings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["U"]      = 0,   // UK universal
        ["PG"]     = 1,   // UK PG (already in film, but also used as fallback)
        ["12"]     = 2,
        ["12A"]    = 2,
        ["15"]     = 3,
        ["18"]     = 4,
        ["R18"]    = 5,
        ["E"]      = 0,   // Australia G-equivalent
        ["M"]      = 3,   // Australia
        ["MA15+"]  = 4,
        ["R18+"]   = 5,
        ["X18+"]   = 5,
    };

    /// <summary>
    /// Returns a 0–5 integer for the given rating string, or null if unknown
    /// (NR, Not Rated, Unrated, empty). Items with a null value are excluded
    /// from parental-rating distance filtering.
    /// </summary>
    public static int? ToNumeric(string? officialRating)
    {
        if (string.IsNullOrWhiteSpace(officialRating))
            return null;

        var r = officialRating.Trim();

        if (_filmRatings.TryGetValue(r, out var fv)) return fv;
        if (_tvRatings.TryGetValue(r, out var tv)) return tv;
        if (_otherRatings.TryGetValue(r, out var ov)) return ov;

        // "Not Rated", "NR", "Unrated", etc.
        return null;
    }

    /// <summary>
    /// Returns a proximity score in [0, 1]: 1 = identical, 0 = maximum distance (5 steps).
    /// Returns null when either rating is unrecognised (caller should skip this dimension).
    /// </summary>
    public static double? ProximityScore(string? ratingA, string? ratingB)
    {
        var a = ToNumeric(ratingA);
        var b = ToNumeric(ratingB);
        if (a is null || b is null) return null;
        return 1.0 - (Math.Abs(a.Value - b.Value) / 5.0);
    }
}
