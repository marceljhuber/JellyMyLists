namespace Jellyfin.Plugin.MyLists;

/// <summary>
/// Pure list mutations (no Jellyfin types), so they can be unit tested and are shared by the service.
/// All of them tolerate duplicate keys/ids instead of throwing: a list file edited by an older
/// version, a repeated CSV row or a double-sent drag-and-drop request must never break a list.
/// </summary>
public static class ListMerge
{
    public static string EntryId(string? imdb, string? tmdb, string title, int? year) =>
        imdb ?? (tmdb is not null ? "tmdb" + tmdb : Importers.Norm(title) + year);

    public static string EntryId(ListEntry e) => EntryId(e.Imdb, e.Tmdb, e.Title, e.Year);

    public static string EntryId(Importers.Candidate c) => EntryId(c.Imdb, c.Tmdb, c.Title, c.Year);

    /// <summary>Merge freshly read candidates into a list: update source positions, add new, drop vanished (but never manual ones).</summary>
    public static void Apply(MyList list, IReadOnlyList<Importers.Candidate> candidates, bool appendOnly)
    {
        // First entry wins for duplicate ids (ToDictionary used to throw and break every later sync).
        var existing = new Dictionary<string, ListEntry>();
        foreach (var e in list.Entries)
        {
            existing.TryAdd(EntryId(e), e);
        }

        var seen = new HashSet<string>();
        var nextAppend = list.Entries.Count == 0 ? 0 : list.Entries.Max(e => e.SourcePos) + 1;
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
                var entry = new ListEntry { Imdb = c.Imdb, Tmdb = c.Tmdb, Title = c.Title, Year = c.Year, SourcePos = appendOnly ? nextAppend++ : i };
                list.Entries.Add(entry);
                existing[id] = entry;
            }
        }

        if (!appendOnly)
        {
            list.Entries.RemoveAll(e => !e.Manual && !seen.Contains(EntryId(e)));
        }

        list.SyncedAt = DateTime.UtcNow;
    }

    /// <summary>Positions from a client-sent order; repeated keys keep their first position.</summary>
    public static Dictionary<string, int> Positions(IEnumerable<string> keys)
    {
        var pos = new Dictionary<string, int>();
        foreach (var k in keys)
        {
            pos.TryAdd(k, pos.Count);
        }

        return pos;
    }

    /// <summary>Store a manual entry order; entries not mentioned keep their relative order after the mentioned ones.</summary>
    public static void SetManualOrder(MyList list, IReadOnlyList<string> keys)
    {
        var pos = Positions(keys);
        var next = pos.Count;
        foreach (var e in list.Entries.OrderBy(e => e.ManualPos ?? int.MaxValue).ThenBy(e => e.SourcePos))
        {
            e.ManualPos = pos.TryGetValue(e.Key, out var p) ? p : next++;
        }
    }
}
