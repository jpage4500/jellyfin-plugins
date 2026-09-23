using System.IO;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using Jellyfin.Data.Enums;
using System.Threading;
using System.Threading.Tasks;

namespace JellyfinPlaylist
{
    [ApiController]
    [Route("Plugin/FavoritesExporter")]
    [Authorize] // Requires the user to be logged in
    public class FavoritesController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;

        public FavoritesController(ILibraryManager libraryManager, IUserManager userManager)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
        }

        // Endpoint to list the generated playlists
        [HttpGet("Playlists")]
        public ActionResult<IEnumerable<GeneratedPlaylist>> GetPlaylists()
        {
            var firstTrackPath = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsFolder = false
            })
            .Select(item => item.Path)
            .FirstOrDefault(path => !string.IsNullOrEmpty(path));

            if (string.IsNullOrEmpty(firstTrackPath))
            {
                return Ok(Array.Empty<GeneratedPlaylist>());
            }

            var musicRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(firstTrackPath)));
            if (string.IsNullOrEmpty(musicRoot))
            {
                return Ok(Array.Empty<GeneratedPlaylist>());
            }

            var playlistDirectory = Path.Combine(musicRoot, "playlists");
            if (!Directory.Exists(playlistDirectory))
            {
                return Ok(Array.Empty<GeneratedPlaylist>());
            }

            var playlists = Directory.EnumerateFiles(playlistDirectory, "*.m3u", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new GeneratedPlaylist
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Songs = System.IO.File.ReadLines(path)
                        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                        .Select(line => new GeneratedSong
                        {
                            Name = Path.GetFileNameWithoutExtension(line),
                            Path = line
                        })
                        .ToList()
                })
                .ToList();

            return Ok(playlists);
        }

        // Endpoint to trigger the export manually
        [HttpPost("Export")]
        public async Task<ActionResult> ExportNow()
        {
            var exportTask = new FavoritesExportTask(_userManager, _libraryManager);
            await exportTask.ExecuteAsync(new Progress<double>(), CancellationToken.None);
            return Ok(new { message = "Export task completed." });
        }

        // Placeholder for future import logic
        [HttpPost("Import")]
        public ActionResult ImportNow()
        {
            return Ok(new { message = "Import functionality coming soon!" });
        }
    }

    public sealed class GeneratedPlaylist
    {
        public string Name { get; set; } = string.Empty;
        public List<GeneratedSong> Songs { get; set; } = new();
    }

    public sealed class GeneratedSong
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }
}