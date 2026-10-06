# Architecture and decisions

## What it is
A Jellyfin server plugin (C#, one assembly) plus a small static web page. The page lives at `/MyLists/`, is served by the plugin
itself and talks to the plugin's JSON API with the user's normal Jellyfin token. A sidebar entry is injected into jellyfin-web
through the File Transformation plugin (optional).

## Files (`src/Jellyfin.Plugin.MyLists`)
| File | Role |
|---|---|
| `Plugin.cs`, `PluginServiceRegistrator.cs`, `Configuration/` | Plugin identity, DI registration, settings page (sidebar toggle, TMDB key) |
| `Api/MyListsController.cs` | Routes (below); serves the embedded `Web/` files; auth via `IAuthorizationContext` |
| `ListService.cs` | Merge/sync of source entries, rule freshness cache, DTOs for the page, background refresh of URL sources |
| `Resolver.cs` | Per-user movie index (cached, invalidated by library events), watched set (never cached), rule queries with role check |
| `Importers.cs` | Text/CSV parsing, MDBList, Letterboxd, TMDB. No Jellyfin dependencies, so it is unit-tested directly |
| `ListStore.cs` | All lists in one JSON file, atomic writes, lock around every read/write of entries; covers on disk |
| `Models.cs` | `MyList`, `ListEntry`, `RuleSpec` |
| `SidebarInjector.cs`, `Web/inject.js` | Registers an `index.html` transformation with File Transformation; adds the drawer entry (clones a native MUI item) |
| `Web/app.js`, `styles.css`, `index.html`, `icon.svg` | The page: plain JS, hash routing, no build step |

## API (all under `/MyLists`, all need a signed-in user except `status`, static files and `cover/*`)
`GET api/status` · `GET api/lists` · `POST api/lists` (create) · `GET api/lists/{id}` · `POST api/lists/{id}` (rename, sort, rule, url) ·
`DELETE api/lists/{id}` · `POST api/lists/{id}/sync` · `POST api/lists/{id}/import` (append text) · `POST api/lists/{id}/order` (save order) ·
`POST api/lists/{id}/entries` · `DELETE api/lists/{id}/entries/{key}` · `POST|DELETE api/lists/{id}/cover` · `GET cover/{file}` ·
`POST api/order` (the user's own order of lists) · `POST api/preview` (how pasted text is understood) · `GET api/search?q=`

## Data model
A list has `SourceType` (manual | rule | csv | mdblist | letterboxd | tmdb), optional rule/url, `DefaultSort`, `CoverFile`, and entries.
An entry stores *what the source said* (IMDb id, TMDB id, title, year), not a Jellyfin item. It is matched to a library movie on every read, so
movies you add later show up without touching the list, and unmatched entries are kept ("not in your library").
`SourcePos` (position in the source) and `ManualPos` (saved order) are separate, so saving an order never destroys the source order,
and a re-sync only rewrites `SourcePos`. Entries added by hand are flagged `Manual` and survive a sync.
Watched state is **not stored**; it comes from Jellyfin per user.

## Overview view settings
Card size, columns, rows, cover height, toggles, sort, direction and source filter live in `localStorage['ml.view']` (per browser; defaults in `VIEW_DEFAULTS` in `app.js`).
Only the "My order" sort is server-side: `MyList.SortIndex`, written by `POST api/order`, which makes drag and drop work across devices.
The grid is plain CSS driven by `--card-w`, `--cols`, `--cover-h`. The rows limit measures how many columns the browser actually laid out and hides the cards beyond `cols × rows`.

## Decisions worth knowing (and why)
- **No IMDb/Letterboxd URL scraping as the main path.** From a server, IMDb answers `202` with an empty body (WAF) and Letterboxd `403` (Cloudflare);
  neither has a free API for this (Letterboxd's is by application and not for personal projects). Reliable routes: MDBList public JSON, CSV exports, pasted text.
  The Letterboxd URL source stays as best effort with a clear error.
- **MDBList** public lists are available as `https://mdblist.com/lists/<user>/<list>/json?limit=&offset=` without a key. Array order is the list order; `id` is the TMDB id.
- **Matching order:** IMDb id, TMDB id, then normalised title + year (±1). The library query must use `DtoOptions(true)`; with a field-less `DtoOptions`
  Jellyfin leaves `ProviderIds` empty and id matching silently never hits (this was a real bug until 0.3.0).
- **Rules check the role per movie.** Jellyfin's `InternalItemsQuery.Person` ignores `PersonTypes`, so a "director" query also returned producer/writer credits.
  The DB query only narrows by name (resolved case-insensitively via `GetPeopleNames`), `GetPeople(item)` then verifies `PersonKind`.
- **Caching:** the index is rebuilt only when `ItemAdded/Updated/Removed` bumps a version; watched state is a lean query on every load; rule lists recompute only when version or rule changed.
- **Sidebar link needs a capture-phase click handler** that calls `location.assign`; jellyfin-web's router otherwise turns the absolute URL into `#/home/http://…`.
- **Details links carry `&serverId=`** like Jellyfin's own links.
- **Covers are served without a token** (an `<img>` can't send one); protection is the unguessable list GUID in the file name.
- **Standalone page, not a native Jellyfin view.** Cheap and robust, but native TV/mobile apps can't show it.

## Versions
Built per Jellyfin line: `-p:JellyfinTarget=10.11` (net9.0, default) or `12` (net10.0). The 4th version part encodes the line (`1011`, `1200`).
10.11 is tested. 12 compiles, has never been run.

## Test
`dotnet test tests/MyLists.Tests` (parser), `dev/e2e.mjs` (API + browser against `dev/demo-server.sh`). See README → Development.

## Open items / ideas
- "Only the first N movies" option for URL sources (e.g. IMDb Top 250 → top 100).
- Run and fix on a Jellyfin 12 server; maybe add 10.10.
- Confirm on a real server that poster links open the details page (fixed with `serverId`; verified only on the demo server).
- Letterboxd URL and TMDB list import are untested against the real services.
- Release as a plugin repository (`manifest.json` + GitHub release, like JellyTrends) so it installs from the Jellyfin catalog.
- Poster size / columns for the list page (only the overview is customisable so far); view settings per user on the server instead of per browser.
- Series support; shared lists between users; GitHub Actions to build and test.
