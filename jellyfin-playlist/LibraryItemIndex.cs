using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace JellyfinPlaylist
{
    /// <summary>
    /// Library items keyed three ways so an entry exported from another server can be found here.
    /// The keys are tried in descending order of trustworthiness: provider id (survives a different
    /// library root, a different folder layout and retagged file names), then path suffix, then
    /// name. A key only counts when it matches exactly one item, which is what stops an exported
    /// track from matching a sibling: tracks carry album-level ids such as MusicBrainzAlbum
    /// alongside their own, and every track on the album shares those.
    /// </summary>
    internal sealed class LibraryItemIndex
    {
        /// <summary>Longest path suffix, in segments, used to match an exported path to a library item.</summary>
        private const int MaxPathKeySegments = 4;

        private readonly Dictionary<string, List<BaseItem>> _byProviderId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<BaseItem>> _byPathSuffix = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<BaseItem>> _byName = new(StringComparer.Ordinal);

        public LibraryItemIndex(IEnumerable<BaseItem> items, Func<BaseItem, IEnumerable<string>> nameKeys)
        {
            foreach (var item in items)
            {
                foreach (var pair in item.ProviderIds)
                {
                    if (!string.IsNullOrEmpty(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                    {
                        Add(_byProviderId, ProviderKey(pair.Key, pair.Value), item);
                    }
                }

                var segments = PathSegments(item.Path);
                for (var length = 1; length <= Math.Min(MaxPathKeySegments, segments.Count); length++)
                {
                    Add(_byPathSuffix, PathKey(segments, length), item);
                }

                foreach (var key in nameKeys(item))
                {
                    Add(_byName, key, item);
                }
            }
        }

        public BaseItem? Find(IReadOnlyDictionary<string, string> providerIds, string? path, IEnumerable<string> nameKeys)
        {
            foreach (var pair in providerIds)
            {
                if (!string.IsNullOrEmpty(pair.Key)
                    && !string.IsNullOrEmpty(pair.Value)
                    && Unique(_byProviderId, ProviderKey(pair.Key, pair.Value)) is { } byProviderId)
                {
                    return byProviderId;
                }
            }

            var segments = PathSegments(path);
            if (Unique(_byPathSuffix, PathKey(segments, Math.Min(MaxPathKeySegments, segments.Count))) is { } byPath)
            {
                return byPath;
            }

            foreach (var nameKey in nameKeys)
            {
                if (Unique(_byName, nameKey) is { } byName)
                {
                    return byName;
                }
            }

            return null;
        }

        /// <summary>
        /// Builds a lookup key out of the parts of an item's identity, e.g. album name and artist.
        /// </summary>
        public static string NameKey(params string?[] parts)
        {
            return string.Join('|', parts.Select(part => (part ?? string.Empty).Trim().ToLowerInvariant()));
        }

        /// <summary>
        /// Splits a path into its segments, dropping the "../" that exported paths carry because
        /// they are written relative to the playlists folder. Matching on a trailing run of
        /// segments rather than the whole path is what lets a library rooted at /media/music on one
        /// server match the same files rooted somewhere else on another.
        /// </summary>
        private static IReadOnlyList<string> PathSegments(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Array.Empty<string>();
            }

            return path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != "." && segment != "..")
                .Select(segment => segment.ToLowerInvariant())
                .ToList();
        }

        private static string? PathKey(IReadOnlyList<string> segments, int length)
        {
            return length <= 0 || length > segments.Count
                ? null
                : string.Join('/', segments.Skip(segments.Count - length));
        }

        private static string ProviderKey(string provider, string id)
        {
            return provider.Trim().ToLowerInvariant() + '=' + id.Trim();
        }

        private static void Add(Dictionary<string, List<BaseItem>> index, string? key, BaseItem item)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (!index.TryGetValue(key, out var bucket))
            {
                bucket = new List<BaseItem>();
                index[key] = bucket;
            }

            bucket.Add(item);
        }

        private static BaseItem? Unique(Dictionary<string, List<BaseItem>> index, string? key)
        {
            return !string.IsNullOrEmpty(key) && index.TryGetValue(key, out var bucket) && bucket.Count == 1
                ? bucket[0]
                : null;
        }
    }
}
