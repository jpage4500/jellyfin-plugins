using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MediaBrowser.Controller;
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
        private readonly IServerApplicationHost _applicationHost;

        public FavoritesController(
            ILibraryManager libraryManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            IAuthorizationContext authorizationContext,
            IServerApplicationHost applicationHost)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _authorizationContext = authorizationContext;
            _applicationHost = applicationHost;
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

        /// <summary>
        /// Every Favorites*.json sitting in the playlists folder, whichever server wrote it.
        /// Servers can share a media folder, so this is how a user picks which one to import.
        /// </summary>
        [HttpGet("Manifests")]
        public ActionResult<IEnumerable<ManifestSummaryJson>> GetManifests()
        {
            var playlistDirectory = PlaylistLocator.GetPlaylistDirectory(_libraryManager);
            if (string.IsNullOrEmpty(playlistDirectory) || !Directory.Exists(playlistDirectory))
            {
                return Ok(Array.Empty<ManifestSummaryJson>());
            }

            var pattern = PlaylistLocator.ManifestPrefix + "*" + PlaylistLocator.ManifestExtension;
            var summaries = Directory.EnumerateFiles(playlistDirectory, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Summarize(path))
                .Where(summary => summary is not null)
                .Select(summary => summary!)
                .OrderByDescending(summary => summary.IsThisServer)
                .ThenByDescending(summary => summary.GeneratedAtUtc)
                .ToList();

            return Ok(summaries);
        }

        private ManifestSummaryJson? Summarize(string path)
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<FavoritesJson>(
                    System.IO.File.ReadAllText(path), ManifestOptions);
                if (manifest is null)
                {
                    return null;
                }

                var fileName = Path.GetFileName(path);

                return new ManifestSummaryJson
                {
                    FileName = fileName,
                    // A manifest written before servers were scoped carries no name; fall back to
                    // the filename so it is still selectable rather than appearing blank.
                    ServerName = string.IsNullOrWhiteSpace(manifest.ServerName)
                        ? Path.GetFileNameWithoutExtension(fileName)
                        : manifest.ServerName,
                    ServerId = manifest.ServerId,
                    IsThisServer = !string.IsNullOrEmpty(manifest.ServerId)
                        && string.Equals(manifest.ServerId, _applicationHost.SystemId, StringComparison.OrdinalIgnoreCase),
                    GeneratedAtUtc = manifest.GeneratedAtUtc,
                    Tracks = manifest.Tracks.Count,
                    Albums = manifest.Albums.Count,
                    Artists = manifest.Artists.Count
                };
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                return null;
            }
        }

        // Endpoint to trigger the export manually
        [HttpPost("Export")]
        public ActionResult<ExportResultJson> ExportNow(CancellationToken cancellationToken)
        {
            if (!PlaylistLocator.TryPrepareDirectory(_libraryManager, out _, out var error))
            {
                return BadRequest(new { message = error });
            }

            var exportTask = new FavoritesExportTask(_userManager, _libraryManager, _applicationHost);

            try
            {
                return Ok(exportTask.Export(cancellationToken));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return BadRequest(new { message = "Export failed while writing playlists: " + exception.Message });
            }
        }

        /// <summary>
        /// Replays one manifest onto this server, favoriting the same items. Favorites are per-user
        /// in Jellyfin but the export merges every user's, so the import targets a single user: the
        /// caller, unless <paramref name="userId"/> says otherwise. It only ever adds favorites.
        /// </summary>
        [HttpPost("Import")]
        public async Task<ActionResult<ImportResultJson>> ImportNow(
            [FromQuery] string? file,
            [FromQuery] Guid? userId,
            CancellationToken cancellationToken)
        {
            var authorizationInfo = await _authorizationContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            var targetUserId = userId ?? authorizationInfo.UserId;

            var user = targetUserId.Equals(default) ? null : _userManager.GetUserById(targetUserId);
            if (user is null)
            {
                return NotFound(new { message = "Could not determine which user to import favorites for." });
            }

            var playlistDirectory = PlaylistLocator.GetPlaylistDirectory(_libraryManager);
            if (string.IsNullOrEmpty(playlistDirectory) || !Directory.Exists(playlistDirectory))
            {
                return NotFound(new { message = "No playlists folder was found to import from." });
            }

            // The name comes from the browser, so it is validated against the folder rather than
            // trusted -- a bare filename is still a path-traversal vector.
            if (string.IsNullOrWhiteSpace(file)
                || !PlaylistLocator.TryResolveManifest(playlistDirectory, file, out var manifestPath))
            {
                return NotFound(new { message = $"No manifest named '{file}' in {playlistDirectory}." });
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
            var result = importer.Import(user, manifest, cancellationToken);
            result.SourceServerName = string.IsNullOrWhiteSpace(manifest.ServerName) ? file : manifest.ServerName;

            return Ok(result);
        }
    }

    public sealed class ManifestSummaryJson
    {
        public string FileName { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string ServerId { get; set; } = string.Empty;
        public bool IsThisServer { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public int Tracks { get; set; }
        public int Albums { get; set; }
        public int Artists { get; set; }
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
