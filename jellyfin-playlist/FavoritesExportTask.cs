using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities; 
using MediaBrowser.Model.Tasks;
using Jellyfin.Data.Enums; // <-- Added this namespace for BaseItemKind

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
            var users = _userManager.GetUsers().ToList();
            
            foreach (var user in users)
            {
                var query = new InternalItemsQuery(user)
                {
                    // 1. Jellyfin 12 uses the BaseItemKind enum instead of strings
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    IsFavorite = true,
                    IsFolder = false
                };

                var favoriteTracks = _libraryManager.GetItemList(query);

                if (favoriteTracks.Count == 0) continue;

                var firstTrackPath = favoriteTracks.First().Path;
                if (string.IsNullOrEmpty(firstTrackPath)) continue;

                var musicDirectory = Path.GetDirectoryName(Path.GetDirectoryName(firstTrackPath)); 
                if (string.IsNullOrEmpty(musicDirectory)) continue;

                // 2. Jellyfin 12 renamed .Name to .Username
                var m3uPath = Path.Combine(musicDirectory, $"{user.Username}_Favorites.m3u");

                using (var writer = new StreamWriter(m3uPath, false))
                {
                    writer.WriteLine("#EXTM3U");
                    foreach (var track in favoriteTracks)
                    {
                        if (!string.IsNullOrEmpty(track.Path))
                        {
                            writer.WriteLine(track.Path);
                        }
                    }
                }
            }

            return Task.CompletedTask;
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
}