using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MyLists;

/// <summary>All lists in one JSON file in the plugin data folder, written atomically.</summary>
public sealed class ListStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    public string CoverDir { get; }
    private readonly ILogger<ListStore> _logger;
    private List<MyList> _lists = [];

    public ListStore(IApplicationPaths paths, ILogger<ListStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(paths.DataPath, "plugins", "MyLists");
        Directory.CreateDirectory(dir);
        CoverDir = Path.Combine(dir, "covers");
        Directory.CreateDirectory(CoverDir);
        _path = Path.Combine(dir, "lists.json");
        try
        {
            if (File.Exists(_path))
            {
                _lists = JsonSerializer.Deserialize<List<MyList>>(File.ReadAllText(_path), Json) ?? [];
            }
        }
        catch (Exception e)
        {
            // Keep the unreadable file around instead of overwriting the user's lists with an empty one.
            _logger.LogError(e, "MyLists: could not read {Path}; keeping a copy and starting empty", _path);
            File.Copy(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
        }
    }

    public List<MyList> ForOwner(string ownerId)
    {
        lock (_lock)
        {
            return _lists.Where(l => l.OwnerId == ownerId).OrderBy(l => l.SortIndex ?? int.MaxValue).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public MyList? Get(string ownerId, string id)
    {
        lock (_lock)
        {
            return _lists.FirstOrDefault(l => l.Id == id && l.OwnerId == ownerId);
        }
    }

    public void Add(MyList list)
    {
        lock (_lock)
        {
            _lists.Add(list);
            Save();
        }
    }

    public bool Remove(string ownerId, string id)
    {
        lock (_lock)
        {
            var cover = _lists.FirstOrDefault(l => l.Id == id && l.OwnerId == ownerId)?.CoverFile;
            var removed = _lists.RemoveAll(l => l.Id == id && l.OwnerId == ownerId) > 0;
            if (removed)
            {
                DeleteCover(cover);
                Save();
            }

            return removed;
        }
    }

    /// <summary>A copy of the entries taken under the lock, so readers never see a list being modified by another request or the background refresh.</summary>
    public List<ListEntry> EntriesOf(MyList list)
    {
        lock (_lock)
        {
            return [.. list.Entries];
        }
    }

    /// <summary>Run a mutation under the lock and persist.</summary>
    public void Mutate(MyList list, Action<MyList> change)
    {
        lock (_lock)
        {
            change(list);
            Save();
        }
    }

    /// <summary>Store the user's own order of lists; lists not mentioned keep their place after the mentioned ones.</summary>
    public void SetOrder(string ownerId, IReadOnlyList<string> ids)
    {
        lock (_lock)
        {
            var pos = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
            var next = ids.Count;
            foreach (var l in _lists.Where(l => l.OwnerId == ownerId).OrderBy(l => l.SortIndex ?? int.MaxValue).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                l.SortIndex = pos.TryGetValue(l.Id, out var p) ? p : next++;
            }

            Save();
        }
    }

    public void DeleteCover(string? file)
    {
        if (!string.IsNullOrEmpty(file))
        {
            File.Delete(Path.Combine(CoverDir, Path.GetFileName(file)));
        }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_lists, Json));
        File.Move(tmp, _path, true);
    }
}
