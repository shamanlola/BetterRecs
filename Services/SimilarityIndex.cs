using System.Diagnostics;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.BetterRecs.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BetterRecs.Services;

/// <summary>
/// Builds and maintains an in-memory <see cref="IndexSnapshot"/> of pre-computed
/// item features so that similarity requests never touch the database on the hot
/// path. Runs as a hosted service: it builds once at startup, refreshes on a
/// configurable interval, and rebuilds (debounced) when the library changes.
/// </summary>
public sealed class SimilarityIndex : IHostedService, IDisposable
{
    // How often the background loop wakes to decide whether a rebuild is due.
    private static readonly TimeSpan _tick = TimeSpan.FromSeconds(30);

    // Only these item kinds are indexed — they are what clients request "Similar"
    // for, and it keeps the index (and the people lookups) small. Filtering by kind
    // in the query is also essential: an unrestricted GetItemList tries to
    // deserialize every row in the library DB, which throws if any row has a type
    // the server can't map ("Cannot deserialize unknown type").
    private static readonly BaseItemKind[] _indexedKinds = { BaseItemKind.Movie, BaseItemKind.Series };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SimilarityIndex> _logger;

    private volatile IndexSnapshot? _current;
    private volatile bool _dirty;
    private DateTime _lastBuildUtc = DateTime.MinValue;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SimilarityIndex(ILibraryManager libraryManager, ILogger<SimilarityIndex> logger)
    {
        _libraryManager = libraryManager;
        _logger         = logger;
    }

    /// <summary>The latest completed snapshot, or null until the first build finishes.</summary>
    public IndexSnapshot? Current => _current;

    /// <summary>Marks the index stale so the background loop rebuilds on its next tick.</summary>
    public void RequestRefresh() => _dirty = true;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded   += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;

        _cts  = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded   -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;

        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
        }
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e) => _dirty = true;

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Build once up-front; until this completes, requests use the live fallback.
            Build(cancellationToken);

            using var timer = new PeriodicTimer(_tick);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var refreshMinutes = Math.Max(1, Plugin.Instance?.Configuration.IndexRefreshMinutes ?? 30);
                var stale          = DateTime.UtcNow - _lastBuildUtc >= TimeSpan.FromMinutes(refreshMinutes);

                if (_current is null || _dirty || stale)
                    Build(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BetterRecs: index background loop stopped unexpectedly");
        }
    }

    private void Build(CancellationToken cancellationToken)
    {
        // Clear the flag first so changes arriving during the build re-trigger it.
        _dirty = false;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var config    = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var pool      = new StringPool();

            var allItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive        = true,
                IsVirtualItem    = false,
                IncludeItemTypes = _indexedKinds,
            });

            var items = new List<ItemFeatures>();
            var byId  = new Dictionary<Guid, ItemFeatures>();

            foreach (var item in allItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var people = config.PeopleMatch == PeopleMatchMode.None
                    ? (IReadOnlyList<PersonInfo>)Array.Empty<PersonInfo>()
                    : _libraryManager.GetPeople(new InternalPeopleQuery { ItemId = item.Id });

                var features = FeatureExtractor.Extract(item, pool, config, people);
                items.Add(features);
                byId[item.Id] = features;
            }

            _current      = new IndexSnapshot(items, byId);
            _lastBuildUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "BetterRecs: similarity index built — {Count} items in {Elapsed} ms ({Strings} unique tokens)",
                items.Count, stopwatch.ElapsedMilliseconds, pool.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BetterRecs: failed to build similarity index; keeping previous snapshot");
        }
    }

    public void Dispose() => _cts?.Dispose();
}
