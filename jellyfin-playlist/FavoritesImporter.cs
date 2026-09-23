using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace JellyfinPlaylist
{
    /// <summary>
    /// Replays a Favorites.json produced by <see cref="FavoritesExportTask"/> onto this server,
    /// marking the same tracks, albums and artists as favorites for one user. Only ever adds
    /// favorites; nothing is un-favorited.
    /// </summary>
    internal sealed class FavoritesImporter
    {
        /// <summary>Cap on how many unmatched names are listed back to the caller.</summary>
        private const int MaxReportedMisses = 25;

        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;

        public FavoritesImporter(ILibraryManager libraryManager, IUserDataManager userDataManager)
        {
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
        }

        public ImportResultJson Import(User user, FavoritesJson manifest, CancellationToken cancellationToken)
        {
            var trackIndex = new LibraryItemIndex(GetItems(BaseItemKind.Audio, isFolder: false), TrackNameKeys);
            var albumIndex = new LibraryItemIndex(GetItems(BaseItemKind.MusicAlbum), AlbumNameKeys);
            var artistIndex = new LibraryItemIndex(GetItems(BaseItemKind.MusicArtist), ArtistNameKeys);

            return new ImportResultJson
            {
                User = user.Username,
                GeneratedAtUtc = manifest.GeneratedAtUtc,
                Tracks = Apply(user, manifest.Tracks, trackIndex, cancellationToken,
                    track => track.ProviderIds,
                    track => track.Path,
                    track => new[] { LibraryItemIndex.NameKey(track.Name) }),
                Albums = Apply(user, manifest.Albums, albumIndex, cancellationToken,
                    album => album.ProviderIds,
                    // An album carries no path of its own, but the folder holding its tracks is one.
                    album => ParentOf(album.Tracks.Select(track => track.Path).FirstOrDefault()),
                    album => album.Artists
                        .Select(artist => LibraryItemIndex.NameKey(album.Name, artist))
                        .Append(LibraryItemIndex.NameKey(album.Name))),
                Artists = Apply(user, manifest.Artists, artistIndex, cancellationToken,
                    artist => artist.ProviderIds,
                    artist => null,
                    artist => new[] { LibraryItemIndex.NameKey(artist.Name) })
            };
        }

        private ImportCategoryJson Apply<T>(
            User user,
            IReadOnlyCollection<T> entries,
            LibraryItemIndex index,
            CancellationToken cancellationToken,
            Func<T, Dictionary<string, string>> providerIds,
            Func<T, string?> path,
            Func<T, IEnumerable<string>> nameKeys)
            where T : INamedExport
        {
            var category = new ImportCategoryJson { Total = entries.Count };

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var item = index.Find(providerIds(entry), path(entry), nameKeys(entry));
                if (item is null)
                {
                    category.NotFound++;
                    if (category.Missing.Count < MaxReportedMisses)
                    {
                        category.Missing.Add(entry.Name);
                    }

                    continue;
                }

                if (MarkFavorite(user, item, cancellationToken))
                {
                    category.Favorited++;
                }
                else
                {
                    category.AlreadyFavorite++;
                }
            }

            return category;
        }

        private bool MarkFavorite(User user, BaseItem item, CancellationToken cancellationToken)
        {
            var userData = _userDataManager.GetUserData(user, item);
            if (userData is null || userData.IsFavorite)
            {
                return false;
            }

            userData.IsFavorite = true;
            _userDataManager.SaveUserData(user, item, userData, UserDataSaveReason.Import, cancellationToken);

            return true;
        }

        private IReadOnlyList<BaseItem> GetItems(BaseItemKind kind, bool? isFolder = null)
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                IsFolder = isFolder
            });
        }

        internal static IEnumerable<string> TrackNameKeys(BaseItem item)
        {
            yield return LibraryItemIndex.NameKey(item.Name);
        }

        internal static IEnumerable<string> AlbumNameKeys(BaseItem item)
        {
            if (item is MusicAlbum album)
            {
                foreach (var artist in album.AlbumArtists.Concat(album.Artists))
                {
                    yield return LibraryItemIndex.NameKey(album.Name, artist);
                }
            }

            yield return LibraryItemIndex.NameKey(item.Name);
        }

        internal static IEnumerable<string> ArtistNameKeys(BaseItem item)
        {
            yield return LibraryItemIndex.NameKey(item.Name);
        }

        internal static string? ParentOf(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var normalized = path.Replace('\\', '/');
            var separator = normalized.LastIndexOf('/');

            return separator <= 0 ? null : normalized.Substring(0, separator);
        }
    }

    public sealed class ImportResultJson
    {
        public string User { get; set; } = string.Empty;
        public string SourceServerName { get; set; } = string.Empty;
        public DateTime? GeneratedAtUtc { get; set; }
        public ImportCategoryJson Tracks { get; set; } = new();
        public ImportCategoryJson Albums { get; set; } = new();
        public ImportCategoryJson Artists { get; set; } = new();
    }

    public sealed class ImportCategoryJson
    {
        public int Total { get; set; }
        public int Favorited { get; set; }
        public int AlreadyFavorite { get; set; }
        public int NotFound { get; set; }
        public List<string> Missing { get; set; } = new();
    }
}
