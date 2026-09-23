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
        public ActionResult<IEnumerable<string>> GetPlaylists()
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Playlist },
                SearchTerm = "_Favorites" // Finds playlists ending in _Favorites
            };

            var playlists = _libraryManager.GetItemList(query)
                                           .Select(p => p.Name)
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
}