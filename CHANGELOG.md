# Changelog

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
