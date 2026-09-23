using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace JellyfinPlaylist
{
    /// <summary>
    /// The playlists folder is inferred from the library rather than configured: music is assumed
    /// to be laid out as &lt;root&gt;/&lt;artist&gt;/&lt;album&gt;/&lt;track&gt;, and generated playlists live in
    /// &lt;root&gt;/playlists. Export, import and the configuration page must all agree on where that is,
    /// so the derivation lives here instead of being repeated in each caller.
    /// </summary>
    internal static class PlaylistLocator
    {
        public const string PlaylistFolderName = "playlists";

        public const string ManifestFileName = "Favorites.json";

        /// <summary>
        /// Seeded from any track in the library rather than from a favorited one, so that the folder
        /// still resolves on a server that has no favorites yet -- which is exactly the case when
        /// importing onto a fresh server.
        /// </summary>
        public static string? GetPlaylistDirectory(ILibraryManager libraryManager)
        {
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

        public static string? GetManifestPath(ILibraryManager libraryManager)
        {
            var playlistDirectory = GetPlaylistDirectory(libraryManager);

            return playlistDirectory is null ? null : Path.Combine(playlistDirectory, ManifestFileName);
        }
    }
}
