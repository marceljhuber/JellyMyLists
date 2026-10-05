using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MyLists;

/// <summary>
/// Adds a &lt;script&gt; tag to jellyfin-web's index.html (via the optional File Transformation
/// plugin) that puts a MyLists entry into the sidebar. Without that plugin MyLists still
/// works at /MyLists/, there is just no menu entry.
/// </summary>
public sealed class SidebarInjector(ILogger<SidebarInjector> logger) : IHostedService
{
    private const string ScriptTag = "<script src=\"../MyLists/inject.js\" defer></script>";
    private static readonly Guid TransformationId = Guid.Parse("b3a9e0d2-6c41-4e7f-8a15-93d0c7f1e2b8");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ft = AssemblyLoadContext.All
                .SelectMany(c => c.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
            var register = ft?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?.GetMethod("RegisterTransformation");
            if (register is null)
            {
                logger.LogInformation("MyLists: File Transformation plugin not found; no sidebar entry (open /MyLists/ directly)");
                return Task.CompletedTask;
            }

            var payload = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["id"] = TransformationId.ToString(),
                // File Transformation groups transformations by this exact string and prefers an exact
                // path match, so use the same "index.html" key other plugins (Media Bar, Jellyfin Enhanced)
                // use; a different regex key would never run next to theirs. As a regex it also matches
                // jellyfin-web chunks like "session-login-index-html.*.chunk.js", hence the HTML check below.
                ["fileNamePattern"] = "index.html",
                ["callbackAssembly"] = GetType().Assembly.FullName,
                ["callbackClass"] = typeof(SidebarInjector).FullName,
                ["callbackMethod"] = nameof(TransformIndex),
            });

            // The payload type (Newtonsoft JObject) lives in File Transformation's load context,
            // so build it through that type's own Parse method instead of referencing Newtonsoft.
            var jObjectType = register.GetParameters()[0].ParameterType;
            var parse = jObjectType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            register.Invoke(null, [parse!.Invoke(null, [payload])]);
            logger.LogInformation("MyLists: sidebar entry registered via File Transformation");
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "MyLists: could not register the sidebar entry");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Callback invoked by File Transformation with { contents } of index.html.</summary>
    public static string TransformIndex(IndexPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        if (html.Contains("MyLists/inject.js", StringComparison.Ordinal))
        {
            return html;
        }

        // Only ever touch the real HTML document, never JS chunks that happen to match.
        var head = html.TrimStart();
        var isDocument = head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return !isDocument || at < 0 ? html : html.Insert(at, ScriptTag);
    }

    public sealed class IndexPayload
    {
        public string? Contents { get; set; }
    }
}
