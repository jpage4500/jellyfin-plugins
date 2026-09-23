# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A solution of Jellyfin server plugins. Currently one project: `jellyfin-playlist`, which ships the **Favorites Exporter** plugin — it walks every user's favorited audio/albums/artists and writes `.m3u` playlists plus a `Favorites.json` manifest into the music library.

Targets `net10.0` against `Jellyfin.Controller` / `Jellyfin.Model` `12.0.0-*` — the unstable Jellyfin line. Test against `jellyfin/jellyfin:unstable`; stable 10.x servers will not load this ABI.

## Commands

```bash
dotnet build --configuration Release
```

Run the plugin in a throwaway Jellyfin container (must be run from `jellyfin-playlist/`, since it bind-mounts a relative path):

```bash
cd jellyfin-playlist && ./test-server.sh
```

That script rebuilds Release, recreates the `jellyfin-dev` container, and mounts `./bin/Release/net10.0` directly as `/config/plugins/FavoritesExporter`. It expects `~/jellyfin-test/config` and `~/jellyfin-test/Music` to exist. Dashboard is at `http://localhost:8096`. Because the build output *is* the plugin directory, picking up a code change means rebuilding and restarting the container (`docker restart jellyfin-dev`).

Jellyfin writes `meta.json` into that mounted directory at load time; it is generated, not source.

There is no test project — verification is manual through the container.

## Releasing

`./package.sh <version>` (four numeric parts) builds Release, zips the assembly plus a generated
`meta.json` into `dist/`, and adds the version to `manifest.json` — the repository file Jellyfin
reads when a server adds this as a plugin repository. Publishing is manual and deliberate:

```
CHANGELOG="..." ./package.sh 1.0.1.0
gh release create v1.0.1.0 dist/favorites-exporter_1.0.1.0.zip
git add manifest.json && git commit -m "Release 1.0.1.0" && git push
```

- `sourceUrl` is built from the git remote and the `v<version>` tag, so the release tag and the
  version argument must match or the download 404s.
- Jellyfin verifies the zip against `checksum` (MD5) before installing, which is why the zip and
  the manifest entry are generated in one step — editing either by hand breaks installs.
- The assembly must sit at the root of the zip; Jellyfin unpacks it straight into the plugin folder.
- `TARGET_ABI` in the script is the *minimum server version*, not the plugin version. Too high and
  older servers never see the release; too low and they install it and fail at load.

## Architecture

Four pieces, all in namespace `JellyfinPlaylist`:

- **`Plugin.cs`** — `BasePlugin<PluginConfiguration>` + `IHasWebPages`. Registers the dashboard page by embedded-resource path.
- **`FavoritesExportTask.cs`** — `IScheduledTask` ("Export Favorites to M3U", category Library, daily at 02:00). Contains all the real logic.
- **`FavoritesController.cs`** — `[Authorize]` API under `/Plugin/FavoritesExporter`: `GET Playlists`, `POST Export`, `POST Import`. `Export` news up `FavoritesExportTask` directly rather than dispatching through `ITaskManager`.
- **`FavoritesImporter.cs`** / **`LibraryItemIndex.cs`** — the reverse trip: replays a `Favorites.json` from another server, favoriting the matching items here.
- **`PlaylistLocator.cs`** — the one place that decides where the playlists folder is.
- **`ConfigurationPage.html`** — embedded resource; the dashboard page, which calls the controller endpoints via the Jellyfin web client's global `ApiClient` / `Dashboard` objects.

### Constraints worth knowing before editing

- **Namespace is load-bearing.** The csproj pins the embedded resource's `LogicalName` to `JellyfinPlaylist.ConfigurationPage.html`, and `Plugin.GetPages()` derives the path from `GetType().Namespace`. Renaming the namespace (or the logical name) silently breaks the config page. Note the csproj `RootNamespace` is the unrelated `jellyfin_playlist`; `Class1.cs` is a leftover template file in that namespace and is unused.
- **No JS template literals in `ConfigurationPage.html`.** Jellyfin runs plugin configuration pages through its localization pass, which rewrites every dollar-brace token in the served file — inline `<script>` blocks included — *before* the script is evaluated. An interpolation like `${escapeHtml(name)}` reaches the browser as the bare text `escapeHtml(name)`, and `id="x-${index}"` collapses to a duplicate `id="x-index"` on every row. Use string concatenation.
- **The plugin GUID `f9b7b8d4-8d9e-4b3a-9a2f-3d5c6e8a1b2c` is duplicated** in `Plugin.cs` (`Id`) and `ConfigurationPage.html` (`pluginUniqueId`). Change both together.
- **Output location is inferred, not configured.** `PlaylistLocator` takes three `Path.GetDirectoryName` steps up from any track's path (assuming `<root>/<artist>/<album>/<track>`) and appends `playlists/`. Export, import and the config page all go through it, so the convention lives in exactly one place — keep it that way. It seeds from the first track in the *library* rather than the first *favorited* one, so the folder still resolves on a server with no favorites yet, which is the state a fresh import machine is in. `PluginConfiguration` is currently empty; making the path configurable is the natural fix.
- **Favorites are merged across all users** into a single set of playlists, despite the task description saying "for each user".
- M3U entries and JSON `Path` values are written relative to the playlist directory with forward slashes, so the output stays portable across the container/host boundary.
- **Import matching never guesses.** `LibraryItemIndex` tries provider id, then path suffix, then name, and accepts a key only when it matches exactly one library item. The uniqueness rule is not defensive padding: the exporter writes an item's whole `ProviderIds` bag, so each track carries album-level ids (`MusicBrainzAlbum`, `MusicBrainzReleaseGroup`) that every one of its siblings shares — a first-hit-wins lookup silently favorites the wrong track. Matching on a trailing run of path segments rather than the full path is what lets a library rooted at `/media/music` on one server match the same files rooted elsewhere on another.
- Import is per-user (favorites are per-user in Jellyfin, though export merges everyone's): it targets the calling user unless `?userId=` says otherwise, and only ever adds favorites.
- Output files: `Favorites.m3u` (favorited tracks only), one `<artist> - <album>.m3u` per favorited album, one `<artist>.m3u` per favorited artist, and `Favorites.json` (includes `ProviderIds` for re-matching on another server).
