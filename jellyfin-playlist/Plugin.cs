using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellyfinPlaylist
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Where to write playlists. Empty means derive it from the library, which is the right
        /// answer when the media folder is writable -- but a read-only or network-mounted library
        /// needs somewhere else, so this can point anywhere the server can write.
        /// </summary>
        public string PlaylistPath { get; set; } = string.Empty;
    }

    // Add IHasWebPages to the class definition
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public override string Name => "Favorites Exporter";
        public override Guid Id => Guid.Parse("f9b7b8d4-8d9e-4b3a-9a2f-3d5c6e8a1b2c");

        public static Plugin? Instance { get; private set; }

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        // This registers your HTML file in the Jellyfin Dashboard menu
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = $"{GetType().Namespace}.ConfigurationPage.html"
                }
            };
        }
    }
}
