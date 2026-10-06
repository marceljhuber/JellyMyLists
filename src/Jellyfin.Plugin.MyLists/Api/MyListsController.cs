using System.Reflection;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MyLists.Api;

/// <summary>Serves the My Lists page at /MyLists/ and its API at /MyLists/api/*. Every list belongs to the signed-in Jellyfin user.</summary>
[ApiController]
[AllowAnonymous]
[Route("MyLists")]
public sealed class MyListsController(
    ListStore store,
    ListService service,
    Resolver resolver,
    ILibraryManager libraryManager,
    IAuthorizationContext authContext) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
    };

    private static readonly string AssetVersion = typeof(MyListsController).Assembly.GetName().Version?.ToString() ?? "0";

    private ContentResult JsonOut(object? value, int status = 200) =>
        new() { Content = JsonSerializer.Serialize(value, Json), ContentType = "application/json", StatusCode = status };

    private ContentResult Error(int status, string message) => JsonOut(new { error = message }, status);

    private async Task<User?> CurrentUser()
    {
        try
        {
            var info = await authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            return info.IsAuthenticated ? info.User : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Owner(User u) => u.Id.ToString("N");

    // ------------------------------------------------------------------ static web app

    [HttpGet("")]
    public IActionResult Index()
    {
        if (!(Request.Path.Value ?? string.Empty).EndsWith('/'))
        {
            return Redirect($"{Request.PathBase}{Request.Path}/");
        }

        return Asset("index.html");
    }

    [HttpGet("{file}")]
    public IActionResult Asset([FromRoute] string file)
    {
        if (!Mime.TryGetValue(Path.GetExtension(file), out var type) || file.Contains("..", StringComparison.Ordinal))
        {
            return NotFound();
        }

        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Web/{file}");
        if (stream is null)
        {
            return NotFound();
        }

        if (file.EndsWith(".html", StringComparison.Ordinal))
        {
            using var reader = new StreamReader(stream);
            Response.Headers.CacheControl = "no-cache";
            return Content(reader.ReadToEnd().Replace("{{v}}", AssetVersion, StringComparison.Ordinal), type);
        }

        // app.js / styles.css are requested with ?v=<plugin version>, so they can be cached for good; inject.js (no version) must revalidate.
        Response.Headers.CacheControl = Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "no-cache";
        return File(stream, type);
    }

    // ------------------------------------------------------------------ API

    [HttpGet("api/status")]
    public async Task<IActionResult> Status()
    {
        var user = await CurrentUser().ConfigureAwait(false);
        return JsonOut(new
        {
            user = user?.Username,
            sidebar = Plugin.Instance?.Configuration.ShowInSidebar ?? true,
            tmdb = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.TmdbApiKey),
        });
    }

    [HttpGet("api/lists")]
    public async Task<IActionResult> Lists()
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        var lists = store.ForOwner(Owner(user));
        foreach (var l in lists.Where(l => l.SourceType == "rule"))
        {
            await service.EnsureRuleFreshAsync(l, user, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        service.RefreshStaleInBackground(lists, user);
        var snap = resolver.Build(user);
        return JsonOut(lists.Select(l => service.Summary(l, snap)));
    }

    public sealed record CreateRequest(string? Name, string? SourceType, string? SourceUrl, string? Text, RuleSpec? Rule, string? DefaultSort);

    [HttpPost("api/lists")]
    public async Task<IActionResult> Create([FromBody] CreateRequest req)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Error(400, "Give the list a name");
        }

        var list = new MyList
        {
            OwnerId = Owner(user),
            Name = req.Name.Trim(),
            SourceType = req.SourceType is "rule" or "csv" or "letterboxd" or "tmdb" or "mdblist" ? req.SourceType : "manual",
            SourceUrl = req.SourceUrl?.Trim(),
            Rule = req.Rule,
            DefaultSort = MyList.Sorts.Contains(req.DefaultSort) ? req.DefaultSort! : "source",
        };
        try
        {
            switch (list.SourceType)
            {
                case "csv":
                    var parsed = Importers.ParseText(req.Text ?? string.Empty);
                    if (parsed.Count == 0)
                    {
                        return Error(400, "Nothing to import: paste an IMDb/Letterboxd CSV or one title or IMDb id per line");
                    }

                    store.Add(list);
                    service.Merge(list, parsed);
                    break;
                case "rule":
                    store.Add(list);
                    await service.SyncAsync(list, user, HttpContext.RequestAborted).ConfigureAwait(false);
                    break;
                case "letterboxd" or "tmdb" or "mdblist":
                    store.Add(list);
                    try
                    {
                        await service.SyncAsync(list, user, HttpContext.RequestAborted).ConfigureAwait(false);
                    }
                    catch
                    {
                        store.Remove(Owner(user), list.Id); // don't leave an empty husk behind a failed import
                        throw;
                    }

                    break;
                default:
                    store.Add(list);
                    break;
            }
        }
        catch (ImportException e)
        {
            return Error(422, e.Message);
        }
        catch (HttpRequestException e)
        {
            return Error(502, "Could not reach the source: " + e.Message);
        }

        return JsonOut(service.Summary(list, resolver.Build(user)), 201);
    }

    public sealed record PreviewRequest(string? Text);

    [HttpPost("api/preview")]
    public async Task<IActionResult> Preview([FromBody] PreviewRequest req)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        return JsonOut(service.Preview(req.Text ?? string.Empty, resolver.Build(user)));
    }

    public sealed record ListOrderRequest(List<string>? Ids);

    /// <summary>Save the user's own order of lists (drag and drop on the overview).</summary>
    [HttpPost("api/order")]
    public async Task<IActionResult> ListOrder([FromBody] ListOrderRequest req)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        store.SetOrder(Owner(user), req.Ids ?? []);
        return JsonOut(new { ok = true });
    }

    [HttpGet("api/lists/{id}")]
    public async Task<IActionResult> Get(string id)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (store.Get(Owner(user), id) is not { } list)
        {
            return Error(404, "List not found");
        }

        if (list.SourceType == "rule")
        {
            await service.EnsureRuleFreshAsync(list, user, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        return JsonOut(service.Detail(list, resolver.Build(user)));
    }

    public sealed record UpdateRequest(string? Name, string? DefaultSort, string? SourceUrl, RuleSpec? Rule);

    [HttpPost("api/lists/{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateRequest req)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (store.Get(Owner(user), id) is not { } list)
        {
            return Error(404, "List not found");
        }

        store.Mutate(list, l =>
        {
            if (!string.IsNullOrWhiteSpace(req.Name))
            {
                l.Name = req.Name.Trim();
            }

            if (MyList.Sorts.Contains(req.DefaultSort))
            {
                l.DefaultSort = req.DefaultSort!;
            }

            if (req.SourceUrl is not null)
            {
                l.SourceUrl = req.SourceUrl.Trim();
            }

            if (req.Rule is not null && l.SourceType == "rule")
            {
                l.Rule = req.Rule;
            }
        });
        return JsonOut(new { ok = true });
    }

    private static readonly Dictionary<string, (string Ext, string Mime)> ImageKinds = new()
    {
        ["png"] = (".png", "image/png"),
        ["jpg"] = (".jpg", "image/jpeg"),
        ["webp"] = (".webp", "image/webp"),
    };

    private static string? Sniff(byte[] b) =>
        b.Length > 12 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "jpg"
        : b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? "webp"
        : null;

    /// <summary>Upload a custom cover (raw PNG/JPEG/WebP body, max 5 MB).</summary>
    [HttpPost("api/lists/{id}/cover")]
    [RequestSizeLimit(6_000_000)]
    public async Task<IActionResult> SetCover(string id)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (store.Get(Owner(user), id) is not { } list)
        {
            return Error(404, "List not found");
        }

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, HttpContext.RequestAborted).ConfigureAwait(false);
        var bytes = ms.ToArray();
        if (bytes.Length > 5_000_000)
        {
            return Error(413, "Image is larger than 5 MB");
        }

        if (Sniff(bytes) is not { } kind)
        {
            return Error(400, "Use a PNG, JPEG or WebP image");
        }

        // New file name per upload so browsers never show a stale cover.
        var file = $"{list.Id}-{DateTime.UtcNow.Ticks}{ImageKinds[kind].Ext}";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(store.CoverDir, file), bytes).ConfigureAwait(false);
        var old = list.CoverFile;
        store.Mutate(list, l => l.CoverFile = file);
        store.DeleteCover(old);
        return JsonOut(new { cover = file });
    }

    [HttpDelete("api/lists/{id}/cover")]
    public async Task<IActionResult> ClearCover(string id)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (store.Get(Owner(user), id) is not { } list)
        {
            return Error(404, "List not found");
        }

        var old = list.CoverFile;
        store.Mutate(list, l => l.CoverFile = null);
        store.DeleteCover(old);
        return JsonOut(new { ok = true });
    }

    /// <summary>Served without a token because &lt;img&gt; cannot send one; the file name contains the unguessable list id.</summary>
    [HttpGet("cover/{file}")]
    public IActionResult Cover(string file)
    {
        var name = Path.GetFileName(file);
        var path = Path.Combine(store.CoverDir, name);
        var ext = Path.GetExtension(name).TrimStart('.').Replace("jpeg", "jpg", StringComparison.Ordinal);
        if (!ImageKinds.TryGetValue(ext, out var kind) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return PhysicalFile(path, kind.Mime);
    }

    [HttpDelete("api/lists/{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        return store.Remove(Owner(user), id) ? JsonOut(new { ok = true }) : Error(404, "List not found");
    }

    [HttpPost("api/lists/{id}/sync")]
    public async Task<IActionResult> Sync(string id)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (store.Get(Owner(user), id) is not { } list)
        {
            return Error(404, "List not found");
        }

        try
        {
            await service.SyncAsync(list, user, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (ImportException e)
        {
            return Error(422, e.Message);
        }
        catch (HttpRequestException e)
        {
            return Error(502, "Could not reach the source: " + e.Message);
        }

        return JsonOut(service.Detail(list, resolver.Build(user)));
    }

    public sealed record ImportRequest(string? Text);

    /// <summary>Append CSV/text rows to an existing list (keeps what is there).</summary>
    [HttpPost("api/lists/{id}/import")]
    public async Task<IActionResult> Import(string id, [FromBody] ImportRequest req)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (store.Get(Owner(user), id) is not { } list)
        {
            return Error(404, "List not found");
        }

        var parsed = Importers.ParseText(req.Text ?? string.Empty);
        if (parsed.Count == 0)
        {
            return Error(400, "Nothing to import");
        }

        service.Merge(list, parsed, appendOnly: true);
        return JsonOut(service.Detail(list, resolver.Build(user)));
    }

    public sealed record OrderRequest(List<string>? Keys);

    [HttpPost("api/lists/{id}/order")]
    public async Task<IActionResult> Order(string id, [FromBody] OrderRequest req)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (store.Get(Owner(user), id) is not { } list)
        {
            return Error(404, "List not found");
        }

        service.SaveOrder(list, req.Keys ?? []);
        return JsonOut(new { ok = true });
    }

    public sealed record AddRequest(string? ItemId);

    [HttpPost("api/lists/{id}/entries")]
    public async Task<IActionResult> AddEntry(string id, [FromBody] AddRequest req)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (store.Get(Owner(user), id) is not { } list)
        {
            return Error(404, "List not found");
        }

        if (list.SourceType == "rule")
        {
            return Error(400, "Rule lists fill themselves; edit the rule instead");
        }

        if (!Guid.TryParse(req.ItemId, out var itemId) || libraryManager.GetItemById(itemId) is not Movie movie)
        {
            return Error(404, "Movie not found");
        }

        service.AddManual(list, movie);
        return JsonOut(service.Detail(list, resolver.Build(user)));
    }

    [HttpDelete("api/lists/{id}/entries/{key}")]
    public async Task<IActionResult> RemoveEntry(string id, string key)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (store.Get(Owner(user), id) is not { } list)
        {
            return Error(404, "List not found");
        }

        store.Mutate(list, l => l.Entries.RemoveAll(e => e.Key == key));
        return JsonOut(new { ok = true });
    }

    [HttpGet("api/search")]
    public async Task<IActionResult> Search([FromQuery] string? q)
    {
        if (await CurrentUser().ConfigureAwait(false) is not { } user)
        {
            return Error(401, "Sign in to Jellyfin first");
        }

        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        {
            return JsonOut(Array.Empty<object>());
        }

        var query = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            SearchTerm = q,
            Limit = 12,
            DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(false),
        };
        return JsonOut(libraryManager.GetItemList(query).Select(i => new { itemId = Resolver.ItemKey(i), title = i.Name, year = i.ProductionYear }));
    }
}
