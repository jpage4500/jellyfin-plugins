using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using System.Threading;
using System.Threading.Tasks;

namespace JellyfinPlaylist
{
    [ApiController]
    [Route("Plugin/FavoritesExporter")]
    [Authorize] // Requires the user to be logged in
    public class FavoritesController : ControllerBase
    {
        private static readonly JsonSerializerOptions ManifestOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IAuthorizationContext _authorizationContext;

        public FavoritesController(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            IAuthorizationContext authorizationContext)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _authorizationContext = authorizationContext;
        }

        // Endpoint to list the generated playlists
        [HttpGet("Playlists")]
        public ActionResult<IEnumerable<GeneratedPlaylist>> GetPlaylists()
        {
            var playlistDirectory = PlaylistLocator.GetPlaylistDirectory(_libraryManager);
            if (string.IsNullOrEmpty(playlistDirectory) || !Directory.Exists(playlistDirectory))
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

        /// <summary>
        /// Replays the Favorites.json sitting in the playlists folder, favoriting the same items
        /// on this server. Favorites are per-user in Jellyfin but the export merges every user's,
        /// so the import targets a single user: the caller, unless <paramref name="userId"/> says
        /// otherwise. It only ever adds favorites -- nothing is un-favorited.
        /// </summary>
        [HttpPost("Import")]
        public async Task<ActionResult<ImportResultJson>> ImportNow([FromQuery] Guid? userId, CancellationToken cancellationToken)
        {
            var authorizationInfo = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            var targetUserId = userId ?? authorizationInfo.UserId;

            var user = targetUserId.Equals(default) ? null : _userManager.GetUserById(targetUserId);
            if (user is null)
            {
                return NotFound(new { message = "Could not determine which user to import favorites for." });
            }

            var manifestPath = PlaylistLocator.GetManifestPath(_libraryManager);
            if (string.IsNullOrEmpty(manifestPath))
            {
                return NotFound(new { message = "No music library was found to import into." });
            }

            if (!System.IO.File.Exists(manifestPath))
            {
                return NotFound(new { message = "No " + PlaylistLocator.ManifestFileName + " found at " + manifestPath + "." });
            }

            FavoritesJson? manifest;
            try
            {
                await using var stream = System.IO.File.OpenRead(manifestPath);
                manifest = await JsonSerializer.DeserializeAsync<FavoritesJson>(stream, ManifestOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                return BadRequest(new { message = manifestPath + " could not be read: " + exception.Message });
            }

            if (manifest is null)
            {
                return BadRequest(new { message = manifestPath + " is empty." });
            }

            var importer = new FavoritesImporter(_libraryManager, _userDataManager);

            return Ok(importer.Import(user, manifest, cancellationToken));
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
