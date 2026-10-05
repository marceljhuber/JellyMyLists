namespace Jellyfin.Plugin.MyLists;

public sealed class ListEntry
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public string? Imdb { get; set; }
    public string? Tmdb { get; set; }
    public string? ItemId { get; set; } // rule lists: the Jellyfin item itself
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int SourcePos { get; set; }
    public int? ManualPos { get; set; }
    /// <summary>Added by hand; sync from the source never removes these.</summary>
    public bool Manual { get; set; }
}

public sealed class RuleSpec
{
    public string? Director { get; set; }
    public string? Actor { get; set; }
    public string? Genre { get; set; }
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
}

public sealed class MyList
{
    public static readonly string[] Sorts = ["source", "manual", "release_asc", "release_desc", "title", "rating", "runtime", "unwatched_first"];

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>manual | rule | csv | letterboxd | tmdb</summary>
    public string SourceType { get; set; } = "manual";
    public string? SourceUrl { get; set; }
    public RuleSpec? Rule { get; set; }
    /// <summary>source | manual | release_asc | release_desc | title | rating | runtime | unwatched_first</summary>
    public string DefaultSort { get; set; } = "source";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SyncedAt { get; set; }
    /// <summary>File name of a user-uploaded cover in the plugin data folder, or null for the automatic poster collage.</summary>
    public string? CoverFile { get; set; }
    public List<ListEntry> Entries { get; set; } = [];
}
