using System;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinPlaylist
{
    /// <summary>
    /// Decides where playlists are written and what the aggregate files are called. Export, import
    /// and the configuration page all go through here so they cannot disagree.
    /// </summary>
    internal static class PlaylistLocator
    {
        public const string PlaylistFolderName = "playlists";

        public const string ManifestPrefix = "Favorites";

        public const string ManifestExtension = ".json";

        /// <summary>
        /// The configured folder if one is set, otherwise derived from the library: music is
        /// assumed to be laid out as &lt;root&gt;/&lt;artist&gt;/&lt;album&gt;/&lt;track&gt;, and playlists live in
        /// &lt;root&gt;/playlists. Seeded from any track rather than a favorited one, so the folder
        /// still resolves on a server with no favorites yet -- the state a fresh import machine
        /// is in.
        /// </summary>
        public static string? GetPlaylistDirectory(ILibraryManager libraryManager)
        {
            var configured = Plugin.Instance?.Configuration.PlaylistPath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            var firstTrackPath = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsFolder = false
            })
            .Select(item => item.Path)
            .FirstOrDefault(path => !string.IsNullOrEmpty(path));

            if (string.IsNullOrEmpty(firstTrackPath))
            {
                return null;
            }

            var musicRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(firstTrackPath)));

            return string.IsNullOrEmpty(musicRoot) ? null : Path.Combine(musicRoot, PlaylistFolderName);
        }

        /// <summary>
        /// Resolves the folder and makes sure it exists and can be written to. A read-only media
        /// share is a configuration problem, not a bug, so it comes back as a message rather than
        /// an exception from deep inside the export.
        /// </summary>
        public static bool TryPrepareDirectory(ILibraryManager libraryManager, out string directory, out string error)
        {
            directory = string.Empty;

            var resolved = GetPlaylistDirectory(libraryManager);
            if (string.IsNullOrEmpty(resolved))
            {
                error = "No music library was found, so there is nowhere to write playlists. "
                    + "Set a playlist folder in the plugin settings.";
                return false;
            }

            try
            {
                Directory.CreateDirectory(resolved);

                // Creating the folder can succeed on a share that then refuses the files, so prove
                // a write actually lands before the export starts producing them.
                var probe = Path.Combine(resolved, ".favorites-exporter-write-test");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                error = $"Cannot write to '{resolved}': {exception.Message} "
                    + "Grant the Jellyfin user write access, or set a different playlist folder in the plugin settings.";
                return false;
            }

            directory = resolved;
            error = string.Empty;

            return true;
        }

        /// <summary>
        /// Aggregate files carry the server name because several servers can share one media
        /// folder, and whichever exported last would otherwise silently overwrite the rest. The
        /// per-album and per-artist playlists are deliberately not scoped: their contents depend
        /// on the album, not on who wrote them.
        /// </summary>
        public static string ManifestFileName(string serverName)
            => $"{ManifestPrefix}-{SanitizeFilename(serverName)}{ManifestExtension}";

        public static string FavoritesPlaylistFileName(string serverName)
            => $"{ManifestPrefix}-{SanitizeFilename(serverName)}.m3u";

        public static string SanitizeFilename(string filename)
        {
            var invalid = Path.GetInvalidFileNameChars()
                .Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' })
                .ToHashSet();
            var cleaned = string.Concat(filename.Select(c => invalid.Contains(c) ? '_' : c)).Trim();

            return cleaned.Length == 0 ? "server" : cleaned;
        }

        /// <summary>
        /// Resolves a caller-supplied manifest name to a real file inside the playlists folder.
        /// The name arrives over HTTP, so it is treated as hostile: anything that is not a plain
        /// Favorites*.json sitting directly in that folder is refused.
        /// </summary>
        public static bool TryResolveManifest(string directory, string fileName, out string fullPath)
        {
            fullPath = string.Empty;

            if (string.IsNullOrWhiteSpace(fileName)
                || fileName != Path.GetFileName(fileName)
                || !fileName.StartsWith(ManifestPrefix, StringComparison.OrdinalIgnoreCase)
                || !fileName.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var root = Path.GetFullPath(directory);
            var candidate = Path.GetFullPath(Path.Combine(root, fileName));

            if (!candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return false;
            }

            fullPath = candidate;

            return File.Exists(candidate);
        }
    }
}
