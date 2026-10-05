using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MyLists;

/// <summary>Matches list entries to the movies a user can see and reads their watched state straight from Jellyfin.</summary>
public sealed class Resolver(ILibraryManager libraryManager)
{
    public sealed class Snapshot
    {
        public required Dictionary<string, BaseItem> ById { get; init; }
        public required Dictionary<string, BaseItem> ByImdb { get; init; }
        public required Dictionary<string, BaseItem> ByTmdb { get; init; }
        public required Dictionary<string, List<BaseItem>> ByTitle { get; init; }
        public required HashSet<string> Played { get; init; }

        public BaseItem? Find(ListEntry e)
        {
            if (e.ItemId is not null && ById.TryGetValue(e.ItemId, out var direct))
            {
                return direct;
            }

            if (e.Imdb is not null && ByImdb.TryGetValue(e.Imdb, out var a))
            {
                return a;
            }

            if (e.Tmdb is not null && ByTmdb.TryGetValue(e.Tmdb, out var b))
            {
                return b;
            }

            if (ByTitle.TryGetValue(Norm(e.Title), out var cands))
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

    private static InternalItemsQuery Query(User user) => new(user)
    {
        IncludeItemTypes = [BaseItemKind.Movie],
        Recursive = true,
        // All fields: with a field-less DtoOptions Jellyfin leaves ProviderIds empty and id matching silently never hits.
        DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(true),
    };

    public Snapshot Build(User user)
    {
        var all = libraryManager.GetItemList(Query(user));
        var q = Query(user);
        q.IsPlayed = true;
        var played = libraryManager.GetItemList(q).Select(Id).ToHashSet();

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

        return new Snapshot { ById = all.ToDictionary(Id), ByImdb = byImdb, ByTmdb = byTmdb, ByTitle = byTitle, Played = played };
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
        var stored = libraryManager.GetPeopleNames(new InternalPeopleQuery { NameContains = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last() })
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

        var items = libraryManager.GetItemList(q).AsEnumerable();
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
        return libraryManager.GetPeople(item).Any(p => p.Type == kind && Norm(p.Name) == wanted);
    }

    public static string ItemKey(BaseItem i) => Id(i);
}
