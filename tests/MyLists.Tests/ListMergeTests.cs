using Jellyfin.Plugin.MyLists;
using Xunit;

public class ListMergeTests
{
    private static Importers.Candidate C(string title, int? year = null, string? imdb = null) => new(imdb, null, title, year);

    [Fact]
    public void Sync_updates_positions_adds_and_drops_but_keeps_manual()
    {
        var list = new MyList();
        ListMerge.Apply(list, [C("A", 2000, "tt1"), C("B", 2001, "tt2"), C("C", 2002, "tt3")], appendOnly: false);
        list.Entries.Add(new ListEntry { Title = "Mine", Year = 1999, Manual = true, SourcePos = 99 });

        ListMerge.Apply(list, [C("C", 2002, "tt3"), C("A", 2000, "tt1"), C("D", 2003, "tt4")], appendOnly: false);

        var byTitle = list.Entries.ToDictionary(e => e.Title);
        Assert.Equal(["A", "C", "D", "Mine"], byTitle.Keys.Order());
        Assert.Equal(0, byTitle["C"].SourcePos);
        Assert.Equal(1, byTitle["A"].SourcePos);
        Assert.Equal(2, byTitle["D"].SourcePos);
        Assert.NotNull(list.SyncedAt);
    }

    [Fact]
    public void Duplicate_existing_entries_do_not_break_sync()
    {
        // Two entries with the same id (e.g. a CSV row repeated by an older version) used to make ToDictionary throw.
        var list = new MyList();
        list.Entries.Add(new ListEntry { Imdb = "tt1", Title = "A" });
        list.Entries.Add(new ListEntry { Imdb = "tt1", Title = "A again" });

        ListMerge.Apply(list, [C("A", null, "tt1"), C("B", null, "tt2")], appendOnly: true);

        Assert.Equal(3, list.Entries.Count);
        Assert.Equal("B", list.Entries[^1].Title);
    }

    [Fact]
    public void Append_puts_new_rows_after_the_highest_position_and_dedupes_the_paste()
    {
        var list = new MyList();
        list.Entries.Add(new ListEntry { Title = "Old", Year = 1990, SourcePos = 7 });

        ListMerge.Apply(list, [C("New", 2020), C("New", 2020), C("Old", 1990), C("Newer", 2021)], appendOnly: true);

        Assert.Equal(["Old", "New", "Newer"], list.Entries.Select(e => e.Title));
        Assert.Equal([7, 8, 9], list.Entries.Select(e => e.SourcePos));
    }

    [Fact]
    public void Title_year_ids_ignore_punctuation_and_case()
    {
        Assert.Equal(ListMerge.EntryId(null, null, "Talk to Me", 2022), ListMerge.EntryId(null, null, "talk to me!", 2022));
        Assert.NotEqual(ListMerge.EntryId(null, null, "Talk to Me", 2022), ListMerge.EntryId(null, null, "Talk to Me", 2023));
        Assert.Equal("tmdb42", ListMerge.EntryId(null, "42", "x", 1));
    }

    [Fact]
    public void Manual_order_tolerates_repeated_and_unknown_keys()
    {
        var list = new MyList();
        var a = new ListEntry { Title = "a", SourcePos = 0 };
        var b = new ListEntry { Title = "b", SourcePos = 1 };
        var c = new ListEntry { Title = "c", SourcePos = 2 };
        list.Entries.AddRange([a, b, c]);

        ListMerge.SetManualOrder(list, [c.Key, c.Key, "does-not-exist", a.Key]);

        Assert.Equal(0, c.ManualPos);
        Assert.Equal(2, a.ManualPos); // the unknown key keeps slot 1; positions only need to be ordered
        Assert.True(b.ManualPos > a.ManualPos);
    }

    [Fact]
    public void Positions_keep_first_occurrence()
    {
        var pos = ListMerge.Positions(["x", "y", "x", "z"]);
        Assert.Equal(new Dictionary<string, int> { ["x"] = 0, ["y"] = 1, ["z"] = 2 }, pos);
    }
}
