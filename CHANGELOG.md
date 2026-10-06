# Changelog

## 0.5.0
- Customisable overview: card size (180-480 px), columns (auto or 1-8), rows shown (all or 1-8, with "Show more"), cover height, and toggles for movie counts, progress bar and source label. The view is remembered per browser.
- Sorting of the lists: my own order (drag & drop, saved on the server), name, date created, last updated, number of movies, % watched, left to watch, source type; reverse direction; search; filter by source; hide completed lists.
- On phones fixed column counts are capped at 2.

## 0.4.0
- Speed: the movie index is cached per user (rebuilt only when the library changes), rule lists are recomputed only when the library or the rule changed, watched state stays live. API calls on a 1,800 movie library: ~110 ms → ~25 ms.
- Posters are lazy `<img>` tags (they used to all load at once), smaller size, static files cached for good, the web font no longer blocks rendering.
- The browser tab now has an icon: Jellyfin's own favicon (own list icon as fallback).
- Clearer README and plugin description.

## 0.3.3
- Fix: Letterboxd list exports were misread (their metadata header row was taken for the film table).
- Fix: list entries are read under a lock, so a background refresh can no longer break a page load.
- Default sorting is validated.
- xUnit tests for the text/CSV parser, an end-to-end test (`dev/e2e.mjs`), documentation and screenshots.

## 0.3.2
- Fix: rule lists now check the person's real role. "Director = X" no longer includes films where X is only producer, writer or actor.
- Names in rules are case insensitive; director and actor combine.

## 0.3.1
- Fix: the sidebar entry opened `#/home/http://…`; the click is now intercepted and navigates for real.
- Centered layout with larger posters (about 210 px), earlier line wrap, mobile layout.

## 0.3.0
- MDBList import (public lists, no key), with daily background refresh.
- Import preview ("Check") and a tolerant parser for ChatGPT output; copyable prompt.
- Fix: provider ids (IMDb/TMDB) were never loaded, so only title + year matched. Ids now win.

## 0.2.0
- ElegantFin-like theme, "Add movies" dialog, custom list covers, details links with `serverId`.

## 0.1.0
- First version: manual, rule and CSV lists; sorting; saved order; watched movies greyed out.
