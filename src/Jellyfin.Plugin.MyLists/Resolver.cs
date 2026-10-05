using System.Collections.Concurrent;
using System.Diagnostics;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MyLists;

/// <summary>Matches list entries to the movies a user can see and reads their watched state straight from Jellyfin.</summary>
public sealed class Resolver : IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Resolver> _logger;
    private long _version;
    private readonly ConcurrentDictionary<Guid, (long Version, Index Index)> _indexes = new();

    public Resolver(ILibraryManager libraryManager, ILogger<Resolver> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemUpdated += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
    }

    /// <summary>Bumped whenever something in the library changes; cached indexes and rule results older than this are rebuilt.</summary>
    public long Version => Interlocked.Read(ref _version);

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Folder)
        {
            Interlocked.Increment(ref _version);
        }
    }

    public void Dispose()
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemUpdated -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
    }

    /// <summary>Everything that only changes with the library: which movies a user can see and how to find them.</summary>
    public sealed class Index
    {
        public required Dictionary<string, BaseItem> ById { get; init; }
        public required Dictionary<string, BaseItem> ByImdb { get; init; }
        public required Dictionary<string, BaseItem> ByTmdb { get; init; }
        public required Dictionary<string, List<BaseItem>> ByTitle { get; init; }
    }

    public sealed class Snapshot
    {
        public required Index Index { get; init; }
        public required HashSet<string> Played { get; init; }

        public BaseItem? Find(ListEntry e)
        {
            if (e.ItemId is not null && Index.ById.TryGetValue(e.ItemId, out var direct))
            {
                return direct;
            }

            if (e.Imdb is not null && Index.ByImdb.TryGetValue(e.Imdb, out var a))
            {
                return a;
            }

            if (e.Tmdb is not null && Index.ByTmdb.TryGetValue(e.Tmdb, out var b))
            {
                return b;
            }

            if (Index.ByTitle.TryGetValue(Norm(e.Title), out var cands))
            {
                if (e.Year is { } y)
                {
                    // ±1 year: Letterboxd/IMDb and the library often disagree on festival vs release year.
                    return cands.FirstOrDefault(c => c.ProductionYear == y) ?? cands.FirstOrDefault(c => Math.Abs((c.ProductionYear ?? 0) - y) == 1);
                }

                return cands.Count == 1 ? cands[0] : null;
            }

            return null;
        }
    }

    public static string Norm(string s) => Importers.Norm(s);

    private static string Id(BaseItem i) => i.Id.ToString("N");

    private static InternalItemsQuery Query(User user, bool lean = false) => new(user)
    {
        IncludeItemTypes = [BaseItemKind.Movie],
        Recursive = true,
        // The index needs provider ids, which a field-less DtoOptions leaves empty (id matching would silently never hit);
        // the lean variant is only for "which ids match".
        DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(!lean),
    };

    public Snapshot Build(User user)
    {
        var version = Version;
        if (!_indexes.TryGetValue(user.Id, out var cached) || cached.Version != version)
        {
            var sw = Stopwatch.StartNew();
            var index = BuildIndex(user);
            _indexes[user.Id] = (version, index);
            _logger.LogInformation("MyLists: indexed {Count} movies for {User} in {Ms} ms", index.ById.Count, user.Username, sw.ElapsedMilliseconds);
            cached = (version, index);
        }

        // Watched state is never cached: one lean query, so a movie you just finished is greyed out on the next load.
        var q = Query(user, lean: true);
        q.IsPlayed = true;
        var played = _libraryManager.GetItemList(q).Select(Id).ToHashSet();
        return new Snapshot { Index = cached.Index, Played = played };
    }

    private Index BuildIndex(User user)
    {
        var all = _libraryManager.GetItemList(Query(user));
        var byImdb = new Dictionary<string, BaseItem>(StringComparer.OrdinalIgnoreCase);
        var byTmdb = new Dictionary<string, BaseItem>();
        var byTitle = new Dictionary<string, List<BaseItem>>();
        foreach (var item in all)
        {
            if (item.ProviderIds.TryGetValue("Imdb", out var imdb) && !string.IsNullOrEmpty(imdb))
            {
                byImdb.TryAdd(imdb, item);
            }

            if (item.ProviderIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrEmpty(tmdb))
            {
                byTmdb.TryAdd(tmdb, item);
            }

            foreach (var name in new[] { item.Name, item.OriginalTitle }.Where(n => !string.IsNullOrEmpty(n)).Distinct())
            {
                var key = Norm(name!);
                if (!byTitle.TryGetValue(key, out var l))
                {
                    byTitle[key] = l = [];
                }

                l.Add(item);
            }
        }

        return new Index { ById = all.ToDictionary(Id), ByImdb = byImdb, ByTmdb = byTmdb, ByTitle = byTitle };
    }

    /// <summary>Movies matching a rule (director and/or actor, plus optional genre and years), in release order. Empty rule matches nothing (never "the whole library").</summary>
    public List<BaseItem> RuleMatches(User user, RuleSpec rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Director) && string.IsNullOrWhiteSpace(rule.Actor) && string.IsNullOrWhiteSpace(rule.Genre))
        {
            return [];
        }

        var q = Query(user);
        // Narrow by name in the database first; Jellyfin ignores the role type for this filter,
        // so the role itself (director vs. producer/writer/actor) is verified per movie below.
        var typed = (!string.IsNullOrWhiteSpace(rule.Director) ? rule.Director : rule.Actor)!.Trim();
        // The database match is case sensitive: resolve what the user typed ("tarantino", "Quentin  Tarantino") to the stored name.
        var stored = _libraryManager.GetPeopleNames(new InternalPeopleQuery { NameContains = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last() })
            .FirstOrDefault(n => Norm(n) == Norm(typed));
        if (stored is null)
        {
            return [];
        }

        q.Person = stored;

        if (!string.IsNullOrWhiteSpace(rule.Genre))
        {
            q.Genres = [rule.Genre.Trim()];
        }

        var items = _libraryManager.GetItemList(q).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(rule.Director))
        {
            items = items.Where(i => HasPerson(i, rule.Director, PersonKind.Director));
        }

        if (!string.IsNullOrWhiteSpace(rule.Actor))
        {
            items = items.Where(i => HasPerson(i, rule.Actor, PersonKind.Actor));
        }

        if (rule.YearFrom is { } from)
        {
            items = items.Where(i => i.ProductionYear >= from);
        }

        if (rule.YearTo is { } to)
        {
            items = items.Where(i => i.ProductionYear <= to);
        }

        return items.OrderBy(i => i.PremiereDate ?? DateTime.MaxValue).ThenBy(i => i.Name).ToList();
    }

    private bool HasPerson(BaseItem item, string name, PersonKind kind)
    {
        var wanted = Norm(name);
        return _libraryManager.GetPeople(item).Any(p => p.Type == kind && Norm(p.Name) == wanted);
    }

    public static string ItemKey(BaseItem i) => Id(i);
}
