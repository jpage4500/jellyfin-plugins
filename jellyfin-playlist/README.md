<p align="center">
  <img src="icon.png" alt="Favorites Exporter" width="160">
</p>

# Favorites Exporter

A Jellyfin plugin that backs up your music favorites as portable `.m3u` playlists, and can replay
those favorites onto another server.

## What it does

**Export.** The plugin walks every user's favorited tracks, albums and artists and writes a set of
files into a `playlists/` folder inside your music library:

| File | Contents |
| --- | --- |
| `Favorites-<ServerName>.m3u` | Every favorited track |
| `<Artist> - <Album>.m3u` | One per favorited album |
| `<Artist>.m3u` | One per favorited artist |
| `Favorites-<ServerName>.json` | A manifest of all favorites, including provider ids, used for import |

Paths inside the playlists are relative and use forward slashes, so the files keep working when the
library is mounted somewhere else — in a container, on another machine, or in a different player.

Favorites from all users are merged into one set of playlists.

The export runs daily at 02:00 as the scheduled task **Export Favorites to M3U** (under *Library*),
and can be run on demand from the plugin's settings page.

**Import.** On another server that can see the same playlists folder, the plugin's settings page
lists the `Favorites-*.json` manifests it finds and lets you import one. Each entry is matched
against the local library by provider id, then by path, then by name, and is favorited only when it
matches exactly one item — anything ambiguous is skipped rather than guessed. Import adds favorites
for the user running it and never removes any.

## Requirements

Jellyfin **12.0** or newer (currently the unstable line). Stable 10.x servers will not show the
plugin in the catalogue.

## Installation

### From the plugin repository (recommended)

1. In Jellyfin, open **Dashboard → Plugins → Manage Repositories** and add a new repository:
   - **Name:** `jpage4500 plugins`
   - **URL:** `https://raw.githubusercontent.com/jpage4500/jellyfin-plugins/main/manifest.json`
2. Go to the plugin **catalogue**, find **Favorites Exporter** under *General*, and install it.
3. Restart Jellyfin.

Updates then appear in the dashboard like any other plugin.

### Manual

1. Download the latest `favorites-exporter_<version>.zip` from the
   [releases page](https://github.com/jpage4500/jellyfin-plugins/releases).
2. Unzip it into a new folder inside your server's plugin directory, e.g.
   `<jellyfin-config>/plugins/FavoritesExporter/`.
3. Restart Jellyfin.

## Usage

Open **Dashboard → Plugins → Favorites Exporter**.

- **Playlist folder** — leave empty to use `playlists/` at the root of your music library (this
  assumes an `<artist>/<album>/<track>` layout). Set it to any path the server can write to if your
  media folder is read-only or on a network share that refuses writes.
- **Export** — writes the playlists immediately instead of waiting for the nightly task.
- **Import Favorites** — pick a manifest exported by another server and favorite the matching items
  here.
- **Generated Playlists Found** — lists what is currently in the playlists folder.

If the folder cannot be written to, the page says so rather than failing silently.

## Building from source

```bash
dotnet build --configuration Release
./test-server.sh   # runs the build in a throwaway jellyfin/jellyfin:unstable container
```

`test-server.sh` expects `~/jellyfin-test/config` and `~/jellyfin-test/Music` to exist and serves
the dashboard at `http://localhost:8096`.
