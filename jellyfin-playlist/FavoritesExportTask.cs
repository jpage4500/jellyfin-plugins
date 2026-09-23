using System;
using System.Collections.Generic;
using System.Text.Json;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;
using Jellyfin.Data.Enums;

namespace JellyfinPlaylist
{
    public class FavoritesExportTask : IScheduledTask
    {
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;

        public FavoritesExportTask(IUserManager userManager, ILibraryManager libraryManager)
        {
            _userManager = userManager;
            _libraryManager = libraryManager;
        }

        public string Name => "Export Favorites to M3U";
        public string Key => "FavoritesExportTask";
        public string Description => "Generates an M3U playlist of favorited songs for each user.";
        public string Category => "Library";

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var favoriteTracks = new Dictionary<string, Audio>(StringComparer.OrdinalIgnoreCase);
            var favoriteAlbums = new Dictionary<Guid, MusicAlbum>();
            var favoriteArtists = new Dictionary<Guid, MusicArtist>();
            var allTracks = new Dictionary<string, Audio>(StringComparer.OrdinalIgnoreCase);

            foreach (var user in _userManager.GetUsers())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var favoriteItems = _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { BaseItemKind.Audio, BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist },
                    IsFavorite = true
                });

                foreach (var item in favoriteItems)
                {
                    if (item is Audio audio && !string.IsNullOrEmpty(audio.Path))
                    {
                        favoriteTracks.TryAdd(audio.Path, audio);
                    }
                    else if (item is MusicAlbum album)
                    {
                        favoriteAlbums.TryAdd(album.Id, album);
                    }
                    else if (item is MusicArtist artist)
                    {
                        favoriteArtists.TryAdd(artist.Id, artist);
                    }
                }

                foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    IsFolder = false
                }).OfType<Audio>())
                {
                    if (!string.IsNullOrEmpty(item.Path))
                    {
                        allTracks.TryAdd(item.Path, item);
                    }
                }
            }

            var albumTracks = favoriteAlbums.ToDictionary(pair => pair.Key, pair => GetAlbumTracks(pair.Value));
            var artistTracks = favoriteArtists.ToDictionary(
                pair => pair.Key,
                pair => allTracks.Values
                    .Where(track => track.Artists.Any(artist => pair.Value.Name.Equals(artist, StringComparison.OrdinalIgnoreCase)))
                    .ToList());

            var playlistDirectory = PlaylistLocator.GetPlaylistDirectory(_libraryManager);
            if (string.IsNullOrEmpty(playlistDirectory))
            {
                return Task.CompletedTask;
            }

            Directory.CreateDirectory(playlistDirectory);

            WritePlaylist(Path.Combine(playlistDirectory, "Favorites.m3u"), playlistDirectory, favoriteTracks.Values);

            foreach (var album in favoriteAlbums.Values)
            {
                var artistName = album.AlbumArtists.FirstOrDefault() ?? "Unknown Artist";
                var filename = SanitizeFilename($"{artistName} - {album.Name}.m3u");
                WritePlaylist(Path.Combine(playlistDirectory, filename), playlistDirectory, albumTracks[album.Id]);
            }

            foreach (var artist in favoriteArtists.Values)
            {
                var filename = SanitizeFilename($"{artist.Name}.m3u");
                WritePlaylist(Path.Combine(playlistDirectory, filename), playlistDirectory, artistTracks[artist.Id]);
            }

            var export = new FavoritesJson
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Tracks = favoriteTracks.Values.Select(track => ToTrackExport(track, playlistDirectory)).ToList(),
                Albums = favoriteAlbums.Values.Select(album => new FavoriteAlbumJson
                {
                    Name = album.Name,
                    Artists = album.AlbumArtists.ToList(),
                    ProviderIds = album.ProviderIds.ToDictionary(pair => pair.Key, pair => pair.Value),
                    Tracks = albumTracks[album.Id].Select(track => ToTrackExport(track, playlistDirectory)).ToList()
                }).ToList(),
                Artists = favoriteArtists.Values.Select(artist => new FavoriteArtistJson
                {
                    Name = artist.Name,
                    ProviderIds = artist.ProviderIds.ToDictionary(pair => pair.Key, pair => pair.Value),
                    Tracks = artistTracks[artist.Id].Select(track => ToTrackExport(track, playlistDirectory)).ToList()
                }).ToList()
            };

            File.WriteAllText(
                Path.Combine(playlistDirectory, PlaylistLocator.ManifestFileName),
                JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true }));

            return Task.CompletedTask;
        }

        private List<Audio> GetAlbumTracks(MusicAlbum album)
        {
            return _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = album.Id,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsFolder = false
            }).OfType<Audio>().Where(track => !string.IsNullOrEmpty(track.Path)).ToList();
        }

        private static void WritePlaylist(string path, string playlistDirectory, IEnumerable<Audio> tracks)
        {
            using var writer = new StreamWriter(path, false);
            writer.WriteLine("#EXTM3U");
            foreach (var track in tracks.Where(track => !string.IsNullOrEmpty(track.Path)).GroupBy(track => track.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                var relativePath = Path.GetRelativePath(playlistDirectory, track.Path).Replace(Path.DirectorySeparatorChar, '/');
                writer.WriteLine(relativePath);
            }
        }

        private static FavoriteTrackJson ToTrackExport(Audio track, string playlistDirectory)
        {
            return new FavoriteTrackJson
            {
                Name = track.Name,
                Path = Path.GetRelativePath(playlistDirectory, track.Path).Replace(Path.DirectorySeparatorChar, '/'),
                ProviderIds = track.ProviderIds.ToDictionary(pair => pair.Key, pair => pair.Value)
            };
        }

        private static string SanitizeFilename(string filename)
        {
            var invalidCharacters = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '/' }).ToHashSet();
            return string.Concat(filename.Select(character => invalidCharacters.Contains(character) ? '_' : character));
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
                }
            };
        }
    }

    internal sealed class FavoritesJson
    {
        public DateTime GeneratedAtUtc { get; set; }
        public List<FavoriteTrackJson> Tracks { get; set; } = new();
        public List<FavoriteAlbumJson> Albums { get; set; } = new();
        public List<FavoriteArtistJson> Artists { get; set; } = new();
    }

    internal interface INamedExport
    {
        string Name { get; }
    }

    internal sealed class FavoriteTrackJson : INamedExport
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public Dictionary<string, string> ProviderIds { get; set; } = new();
    }

    internal sealed class FavoriteAlbumJson : INamedExport
    {
        public string Name { get; set; } = string.Empty;
        public List<string> Artists { get; set; } = new();
        public Dictionary<string, string> ProviderIds { get; set; } = new();
        public List<FavoriteTrackJson> Tracks { get; set; } = new();
    }

    internal sealed class FavoriteArtistJson : INamedExport
    {
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, string> ProviderIds { get; set; } = new();
        public List<FavoriteTrackJson> Tracks { get; set; } = new();
    }
}