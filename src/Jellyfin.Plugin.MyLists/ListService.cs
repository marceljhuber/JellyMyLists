using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MyLists;

/// <summary>Sync/merge logic and the JSON shapes the web page consumes.</summary>
public sealed class ListService(ListStore store, Resolver resolver, IHttpClientFactory http)
{
    private static string EntryId(Importers.Candidate c) => c.Imdb ?? (c.Tmdb is not null ? "tmdb" + c.Tmdb : Resolver.Norm(c.Title) + c.Year);

    private static string EntryId(ListEntry e) => e.Imdb ?? (e.Tmdb is not null ? "tmdb" + e.Tmdb : Resolver.Norm(e.Title) + e.Year);

    /// <summary>Merge freshly read candidates into a list: update source positions, add new, drop vanished (but never manual ones).</summary>
    public void Merge(MyList list, IReadOnlyList<Importers.Candidate> candidates, bool appendOnly = false)
    {
        store.Mutate(list, l =>
        {
            var existing = l.Entries.ToDictionary(EntryId, e => e);
            var seen = new HashSet<string>();
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var id = EntryId(c);
                if (!seen.Add(id))
                {
                    continue;
                }

                if (existing.TryGetValue(id, out var e))
                {
                    e.SourcePos = appendOnly ? e.SourcePos : i;
                    e.Imdb ??= c.Imdb;
                    e.Tmdb ??= c.Tmdb;
                }
                else
                {
                    l.Entries.Add(new ListEntry { Imdb = c.Imdb, Tmdb = c.Tmdb, Title = c.Title, Year = c.Year, SourcePos = appendOnly ? l.Entries.Count : i });
                }
            }

            if (!appendOnly)
            {
                l.Entries.RemoveAll(e => !e.Manual && !seen.Contains(EntryId(e)));
            }

            l.SyncedAt = DateTime.UtcNow;
        });
    }

    /// <summary>Refresh a list from its source. Rule lists recompute locally; URL sources are re-fetched.</summary>
    public async Task SyncAsync(MyList list, User user, CancellationToken ct)
    {
        switch (list.SourceType)
        {
            case "rule" when list.Rule is not null:
                var items = resolver.RuleMatches(user, list.Rule);
                Merge(list, items.Select(i => new Importers.Candidate(i.ProviderIds.GetValueOrDefault("Imdb"), i.ProviderIds.GetValueOrDefault("Tmdb"), i.Name, i.ProductionYear)).ToList());
                // Rule entries are real items: pin them so renamed/duplicate titles still resolve.
                store.Mutate(list, l =>
                {
                    foreach (var e in l.Entries.Where(e => e.ItemId is null))
                    {
                        e.ItemId = items.FirstOrDefault(i => (e.Imdb is not null && i.ProviderIds.GetValueOrDefault("Imdb") == e.Imdb) || (e.Imdb is null && i.Name == e.Title && i.ProductionYear == e.Year))?.Id.ToString("N");
                    }
                });
                break;
            case "letterboxd" when !string.IsNullOrWhiteSpace(list.SourceUrl):
                Merge(list, await Importers.LetterboxdAsync(Client(), list.SourceUrl, ct).ConfigureAwait(false));
                break;
            case "mdblist" when !string.IsNullOrWhiteSpace(list.SourceUrl):
                Merge(list, await Importers.MdbListAsync(Client(), list.SourceUrl, ct).ConfigureAwait(false));
                break;
            case "tmdb" when !string.IsNullOrWhiteSpace(list.SourceUrl):
                Merge(list, await Importers.TmdbListAsync(Client(), list.SourceUrl, Plugin.Instance?.Configuration.TmdbApiKey ?? string.Empty, ct).ConfigureAwait(false));
                break;
            default:
                throw new ImportException("This list has no source to sync (CSV and manual lists are edited, not synced).");
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid User, string List), (long Version, string Rule)> _ruleState = new();

    /// <summary>Recompute a rule list only if the library or the rule changed since the last time; otherwise the stored entries are current.</summary>
    public async Task EnsureRuleFreshAsync(MyList list, User user, CancellationToken ct)
    {
        var key = (user.Id, list.Id);
        var rule = System.Text.Json.JsonSerializer.Serialize(list.Rule);
        if (_ruleState.TryGetValue(key, out var state) && state.Version == resolver.Version && state.Rule == rule)
        {
            return;
        }

        var version = resolver.Version;
        await SyncAsync(list, user, ct).ConfigureAwait(false);
        _ruleState[key] = (version, rule);
    }

    private int _refreshing;

    /// <summary>Re-read URL based lists that have not been synced for a day, in the background so page loads never wait on a remote site.</summary>
    public void RefreshStaleInBackground(IEnumerable<MyList> lists, User user)
    {
        var stale = lists.Where(l => l.SourceType is "mdblist" && l.SyncedAt is { } t && DateTime.UtcNow - t > TimeSpan.FromHours(24)).ToList();
        if (stale.Count == 0 || Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var l in stale)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        await SyncAsync(l, user, cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Keep the old contents; the user can re-sync by hand and sees the error then.
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>Show, before importing, how each pasted line was understood and whether it is in the library.</summary>
    public object Preview(string text, Resolver.Snapshot snap) =>
        Importers.ParseText(text).Select(c =>
        {
            var item = snap.Find(new ListEntry { Imdb = c.Imdb, Tmdb = c.Tmdb, Title = c.Title, Year = c.Year });
            return new { title = c.Title, year = c.Year, imdb = c.Imdb, match = item is null ? null : $"{item.Name}{(item.ProductionYear is { } y ? $" ({y})" : string.Empty)}", itemId = item is null ? null : Resolver.ItemKey(item) };
        }).ToList();

    private HttpClient Client()
    {
        var c = http.CreateClient();
        c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    public object Summary(MyList l, Resolver.Snapshot snap)
    {
        var entries = store.EntriesOf(l);
        var resolved = entries.Select(e => snap.Find(e)).ToList();
        var inLib = resolved.Where(i => i is not null).Cast<BaseItem>().ToList();
        return new
        {
            id = l.Id,
            name = l.Name,
            sourceType = l.SourceType,
            sourceUrl = l.SourceUrl,
            defaultSort = l.DefaultSort,
            syncedAt = l.SyncedAt,
            createdAt = l.CreatedAt,
            sortIndex = l.SortIndex,
            cover = l.CoverFile,
            total = entries.Count,
            available = inLib.Count,
            watched = inLib.Count(i => snap.Played.Contains(Resolver.ItemKey(i))),
            covers = entries.OrderBy(e => e.ManualPos ?? e.SourcePos).Select(e => snap.Find(e)).Where(i => i is not null).Take(4).Select(i => Resolver.ItemKey(i!)).ToList(),
        };
    }

    public object Detail(MyList l, Resolver.Snapshot snap)
    {
        var entries = store.EntriesOf(l).Select(e =>
        {
            var item = snap.Find(e);
            return new
            {
                key = e.Key,
                title = item?.Name ?? e.Title,
                year = item?.ProductionYear ?? e.Year,
                inLibrary = item is not null,
                itemId = item is null ? null : Resolver.ItemKey(item),
                imageTag = item?.DateModified.Ticks.ToString(),
                played = item is not null && snap.Played.Contains(Resolver.ItemKey(item)),
                imdb = e.Imdb ?? item?.ProviderIds.GetValueOrDefault("Imdb"),
                premiere = item?.PremiereDate,
                rating = item?.CommunityRating,
                runtimeMin = item?.RunTimeTicks is { } t ? (int)(t / 600_000_000) : (int?)null,
                sourcePos = e.SourcePos,
                manualPos = e.ManualPos,
                manual = e.Manual,
            };
        }).ToList();
        return new
        {
            list = new { id = l.Id, name = l.Name, sourceType = l.SourceType, sourceUrl = l.SourceUrl, rule = l.Rule, defaultSort = l.DefaultSort, syncedAt = l.SyncedAt, cover = l.CoverFile },
            entries,
        };
    }

    public void SaveOrder(MyList list, IReadOnlyList<string> keys)
    {
        store.Mutate(list, l =>
        {
            var pos = keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
            var next = keys.Count;
            foreach (var e in l.Entries)
            {
                e.ManualPos = pos.TryGetValue(e.Key, out var p) ? p : next++;
            }
        });
    }

    public void AddManual(MyList list, BaseItem item)
    {
        store.Mutate(list, l =>
        {
            if (l.Entries.Any(e => e.ItemId == Resolver.ItemKey(item)))
            {
                return;
            }

            l.Entries.Add(new ListEntry
            {
                ItemId = Resolver.ItemKey(item),
                Imdb = item.ProviderIds.GetValueOrDefault("Imdb"),
                Tmdb = item.ProviderIds.GetValueOrDefault("Tmdb"),
                Title = item.Name,
                Year = item.ProductionYear,
                SourcePos = l.Entries.Count == 0 ? 0 : l.Entries.Max(e => e.SourcePos) + 1,
                Manual = true,
            });
        });
    }
}
