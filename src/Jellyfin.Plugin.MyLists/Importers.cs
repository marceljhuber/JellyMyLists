using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MyLists;

public sealed class ImportException(string message) : Exception(message);

/// <summary>Turns CSV/pasted text, Letterboxd pages and TMDB lists into ordered candidates.</summary>
public static partial class Importers
{
    public sealed record Candidate(string? Imdb, string? Tmdb, string Title, int? Year);

    private const string BrowserUa = "Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0";

    /// <summary>
    /// Accepts an IMDb list export, a Letterboxd list/watchlist export, any CSV with title/year/imdb columns,
    /// or plain lines: "tt0110912", "Pulp Fiction (1994)", "Pulp Fiction, 1994".
    /// </summary>
    public static List<Candidate> ParseText(string text)
    {
        var rows = Csv(text);
        var result = new List<Candidate>();
        // Header row = first row that names a title/id column.
        // Letterboxd exports start with a list-metadata header ("Date,Name,Tags,URL,Description"); the film table has a Year/Position column.
        bool IsHeader(List<string> r) => r.Any(c => HeaderIs(c, "title", "name", "const", "imdb id", "imdbid", "tmdb id"));
        var h = rows.FindIndex(r => IsHeader(r) && r.Any(c => HeaderIs(c, "year", "release year", "position", "const", "imdb id", "imdbid", "tmdb id")));
        if (h < 0)
        {
            h = rows.FindIndex(IsHeader);
        }

        if (h >= 0)
        {
            var head = rows[h].Select(c => c.Trim().ToLowerInvariant()).ToList();
            int Col(params string[] n) => head.FindIndex(c => n.Contains(c));
            int cTitle = Col("title", "name"), cYear = Col("year", "release year"), cImdb = Col("const", "imdb id", "imdbid", "imdb"), cTmdb = Col("tmdb id", "tmdbid", "tmdb");
            string? At(List<string> r, int i) => i >= 0 && i < r.Count && !string.IsNullOrWhiteSpace(r[i]) ? r[i].Trim() : null;
            foreach (var r in rows.Skip(h + 1))
            {
                var title = At(r, cTitle);
                var imdb = At(r, cImdb);
                var tmdb = At(r, cTmdb);
                if (title is null && imdb is null && tmdb is null)
                {
                    continue;
                }

                result.Add(new Candidate(ImdbId(imdb), tmdb, title ?? imdb ?? tmdb!, int.TryParse(At(r, cYear), out var y) ? y : null));
            }

            return result;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = CleanLine(raw);
            if (line.Length == 0)
            {
                continue;
            }

            if (ImdbIdRegex().Match(line) is { Success: true } m)
            {
                result.Add(new Candidate(m.Value, null, m.Value, null));
            }
            else if (TitleYearRegex().Match(line) is { Success: true } ty)
            {
                result.Add(new Candidate(null, null, ty.Groups[1].Value.Trim().Trim('"', '\u201C', '\u201D'), int.Parse(ty.Groups[2].Value)));
            }
            else
            {
                result.Add(new Candidate(null, null, line, null));
            }
        }

        return result;
    }

    /// <summary>Case, accent and punctuation insensitive key used to match titles and names.</summary>
    public static string Norm(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>Strips what chat assistants add around list lines: code fences, numbering, bullets, bold/quotes, trailing notes.</summary>
    public static string CleanLine(string raw)
    {
        var line = raw.Trim().Trim(',', ';');
        if (line.EndsWith(':') || line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        line = Leading().Replace(line, string.Empty);
        line = line.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal).Trim().Trim('"', '\u201C', '\u201D', '`', '\'').Trim();
        return line;
    }

    private static bool HeaderIs(string cell, params string[] names) => names.Contains(cell.Trim().ToLowerInvariant());

    private static string? ImdbId(string? s) => s is not null && ImdbIdRegex().IsMatch(s) ? s : null;

    /// <summary>Best effort: Letterboxd sits behind Cloudflare and may answer 403 to servers. CSV export always works.</summary>
    public static async Task<List<Candidate>> LetterboxdAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("letterboxd.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportException("Not a letterboxd.com URL");
        }

        var baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        baseUrl = PageSuffix().Replace(baseUrl, string.Empty);
        var result = new List<Candidate>();
        for (var page = 1; page <= 60; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, page == 1 ? baseUrl + "/" : $"{baseUrl}/page/{page}/");
            req.Headers.UserAgent.ParseAdd(BrowserUa);
            req.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (res.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new ImportException("Letterboxd blocked the server request (403). Use the list's \"Export list as CSV\" and import the CSV instead.");
            }

            if (!res.IsSuccessStatusCode)
            {
                if (page > 1)
                {
                    break;
                }

                throw new ImportException($"Letterboxd answered {(int)res.StatusCode}. Is the list public?");
            }

            var html = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var found = 0;
            var films = LbItemName().Matches(html);
            if (films.Count == 0)
            {
                films = LbFilmName().Matches(html);
            }

            foreach (Match m in films)
            {
                // data-item-name="Pulp Fiction (1994)"  or  data-film-name + data-film-release-year
                var name = WebUtility.HtmlDecode(m.Groups["name"].Value);
                var ty = TitleYearRegex().Match(name);
                result.Add(ty.Success
                    ? new Candidate(null, null, ty.Groups[1].Value.Trim(), int.Parse(ty.Groups[2].Value))
                    : new Candidate(null, null, name, null));
                found++;
            }

            if (found == 0 || !html.Contains("class=\"next\"", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        if (result.Count == 0)
        {
            throw new ImportException("No films found on that page. Use the list's CSV export instead.");
        }

        return result;
    }

    /// <summary>Public MDBList lists are served as JSON at "&lt;list url&gt;/json" without any API key.</summary>
    public static async Task<List<Candidate>> MdbListAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !uri.Host.EndsWith("mdblist.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/lists/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportException("Use a list URL like https://mdblist.com/lists/snoak/imdb-top-250-movies");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/json", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^5];
        }

        var result = new List<Candidate>();
        const int Page = 1000;
        for (var offset = 0; offset < Page * 20; offset += Page)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://mdblist.com{path}/json?limit={Page}&offset={offset}");
            req.Headers.UserAgent.ParseAdd(BrowserUa);
            using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                throw new ImportException(res.StatusCode == HttpStatusCode.NotFound
                    ? "MDBList list not found. Is the URL right and the list public?"
                    : $"MDBList answered {(int)res.StatusCode}.");
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
            catch (JsonException)
            {
                throw new ImportException("MDBList did not return a list. Is the list public?");
            }

            using (doc)
            {
                // Plain list: an array. Some list kinds wrap it as { movies: [], shows: [] }.
                var items = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToList()
                    : doc.RootElement.TryGetProperty("movies", out var mv) ? mv.EnumerateArray().ToList() : [];
                foreach (var it in items)
                {
                    if (it.TryGetProperty("mediatype", out var mt) && mt.GetString() is { } kind && kind != "movie")
                    {
                        continue;
                    }

                    var imdb = it.TryGetProperty("imdb_id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                    var tmdb = it.TryGetProperty("id", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64().ToString() : null;
                    var title = it.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                    int? year = it.TryGetProperty("release_year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32() : null;
                    if (imdb is null && tmdb is null && title is null)
                    {
                        continue;
                    }

                    result.Add(new Candidate(imdb, tmdb, title ?? imdb ?? tmdb!, year));
                }

                if (items.Count < Page)
                {
                    break;
                }
            }
        }

        if (result.Count == 0)
        {
            throw new ImportException("That MDBList list has no movies.");
        }

        return result;
    }

    public static async Task<List<Candidate>> TmdbListAsync(HttpClient http, string url, string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ImportException("Set a TMDB API key in the My Lists plugin settings first.");
        }

        var id = TmdbListId().Match(url) is { Success: true } m ? m.Groups[1].Value : throw new ImportException("Could not find a TMDB list id in the URL (…/list/12345).");
        var result = new List<Candidate>();
        for (var page = 1; ; page++)
        {
            using var res = await http.GetAsync($"https://api.themoviedb.org/3/list/{id}?api_key={Uri.EscapeDataString(apiKey)}&page={page}", ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                throw new ImportException($"TMDB answered {(int)res.StatusCode} (check the list id, that it is public, and the API key).");
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = doc.RootElement;
            foreach (var it in root.GetProperty("items").EnumerateArray())
            {
                if (it.TryGetProperty("media_type", out var mt) && mt.GetString() != "movie")
                {
                    continue;
                }

                var title = it.TryGetProperty("title", out var t) ? t.GetString() : null;
                var date = it.TryGetProperty("release_date", out var d) ? d.GetString() : null;
                result.Add(new Candidate(null, it.GetProperty("id").GetInt32().ToString(), title ?? "?", date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null));
            }

            if (page >= (root.TryGetProperty("total_pages", out var tp) ? tp.GetInt32() : 1))
            {
                break;
            }
        }

        return result;
    }

    // RFC 4180-ish parser: quoted fields, doubled quotes, embedded newlines.
    private static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        text = text.TrimStart('﻿');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(sb.ToString());
                sb.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(sb.ToString());
                sb.Clear();
                if (row.Any(x => x.Length > 0))
                {
                    rows.Add(row);
                }

                row = [];
            }
            else
            {
                sb.Append(c);
            }
        }

        row.Add(sb.ToString());
        if (row.Any(x => x.Length > 0))
        {
            rows.Add(row);
        }

        return rows;
    }

    [GeneratedRegex(@"\btt\d{7,9}\b")]
    private static partial Regex ImdbIdRegex();

    // "Title (1994)", "Title, 1994", "Title - 1994", "Title – 1994", "Title [1994]", optionally followed by a note.
    [GeneratedRegex(@"^(.+?)\s*(?:[\(\[]\s*|,\s*|\s[-\u2013\u2014]\s*)((?:18|19|20)\d{2})\s*[\)\]]?(?:\s*[-\u2013\u2014:].*)?$")]
    private static partial Regex TitleYearRegex();

    // "1.", "1)", "01 -", "-", "*", "•" in front of a line
    [GeneratedRegex(@"^\s*(?:\d{1,4}\s*[\.\)]\s+|\d{1,4}\s+[-\u2013]\s+|[-*\u2022]\s+)")]
    private static partial Regex Leading();

    [GeneratedRegex(@"/page/\d+$")]
    private static partial Regex PageSuffix();

    [GeneratedRegex("data-item-name=\"(?<name>[^\"]+)\"")]
    private static partial Regex LbItemName();

    [GeneratedRegex("data-film-name=\"(?<name>[^\"]+)\"")]
    private static partial Regex LbFilmName();

    [GeneratedRegex(@"/list/(\d+)")]
    private static partial Regex TmdbListId();
}
