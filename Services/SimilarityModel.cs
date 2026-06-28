using Jellyfin.Plugin.BetterRecs.Configuration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.BetterRecs.Services;

/// <summary>
/// Per-build string interner. Genres, tags and people names repeat heavily across
/// a library, so collapsing them to a single shared instance keeps the index small.
/// A fresh pool is used for every rebuild, so it never grows unbounded.
/// </summary>
public sealed class StringPool
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public int Count => _map.Count;

    public string Intern(string value)
    {
        if (_map.TryGetValue(value, out var existing)) return existing;
        _map[value] = value;
        return value;
    }
}

/// <summary>
/// Immutable, pre-computed feature vector for a single library item. All the
/// expensive extraction (people DB lookups, set normalisation) happens once when
/// this is built; scoring a request then only touches these cached fields.
/// Token arrays are lower-cased, de-duplicated, interned and sorted by ordinal,
/// which makes intersection a linear two-pointer merge with no allocations.
/// </summary>
public sealed class ItemFeatures
{
    public required BaseItem Item { get; init; }
    public required Type ItemType { get; init; }
    public required string[] Genres { get; init; }
    public required string[] Tags { get; init; }
    public required string[] People { get; init; }
    public float? CommunityRating { get; init; }
    public int? RatingNumeric { get; init; }
    public int? Year { get; init; }
}

/// <summary>
/// Atomically-swapped snapshot of the whole index. Readers grab the current
/// reference once and iterate it lock-free while a rebuild constructs the next one.
/// </summary>
public sealed class IndexSnapshot
{
    public IReadOnlyList<ItemFeatures> Items { get; }
    public IReadOnlyDictionary<Guid, ItemFeatures> ById { get; }

    public IndexSnapshot(IReadOnlyList<ItemFeatures> items, IReadOnlyDictionary<Guid, ItemFeatures> byId)
    {
        Items = items;
        ById  = byId;
    }
}

/// <summary>
/// Turns a live <see cref="BaseItem"/> (plus its people list) into an
/// <see cref="ItemFeatures"/>. Shared by the background index builder and the
/// live fallback path so feature semantics are identical in both.
/// </summary>
public static class FeatureExtractor
{
    public static ItemFeatures Extract(
        BaseItem item,
        StringPool pool,
        PluginConfiguration config,
        IReadOnlyList<PersonInfo> people)
        => new()
        {
            Item            = item,
            ItemType        = item.GetType(),
            Genres          = ToTokens(item.Genres, pool),
            Tags            = ToTokens(item.Tags, pool),
            People          = ToPeopleTokens(people, config, pool),
            CommunityRating = item.CommunityRating,
            RatingNumeric   = RatingConverter.ToNumeric(item.OfficialRating),
            Year            = item.ProductionYear,
        };

    private static string[] ToTokens(IReadOnlyList<string>? values, StringPool pool)
    {
        if (values is null || values.Count == 0) return [];

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            set.Add(pool.Intern(value.Trim().ToLowerInvariant()));
        }

        return ToSortedArray(set);
    }

    private static string[] ToPeopleTokens(IReadOnlyList<PersonInfo> people, PluginConfiguration config, StringPool pool)
    {
        if (config.PeopleMatch == PeopleMatchMode.None || people.Count == 0) return [];

        var set = new HashSet<string>(StringComparer.Ordinal);

        // Enum names ("Director"/"Actor") are stable identifiers; comparing by string
        // avoids referencing PersonKind, which isn't exposed via the NuGet packages.
        foreach (var person in people)
        {
            if (person.Type.ToString() == "Director")
                AddName(set, person.Name, pool);
        }

        if (config.PeopleMatch == PeopleMatchMode.DirectorsAndActors)
        {
            var added = 0;
            foreach (var person in people)
            {
                if (person.Type.ToString() != "Actor") continue;
                if (added++ >= config.TopActorCount) break;   // people come back in billing order
                AddName(set, person.Name, pool);
            }
        }

        return ToSortedArray(set);
    }

    private static void AddName(HashSet<string> set, string? name, StringPool pool)
    {
        if (!string.IsNullOrWhiteSpace(name))
            set.Add(pool.Intern(name.Trim().ToLowerInvariant()));
    }

    private static string[] ToSortedArray(HashSet<string> set)
    {
        if (set.Count == 0) return [];
        var array = new string[set.Count];
        set.CopyTo(array);
        Array.Sort(array, StringComparer.Ordinal);
        return array;
    }
}
