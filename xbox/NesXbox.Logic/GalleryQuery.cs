using System;
using System.Collections.Generic;
using System.Linq;

namespace NesXbox.Logic
{
    public enum GalleryFilter { All, Favorites, RecentlyPlayed, RecentlyAdded }
    public enum GallerySort { Title, DateAdded, LastPlayed }

    public static class GalleryQuery
    {
        public const int RecentLimit = 20;

        public static List<GameEntry> Apply(IEnumerable<GameEntry> games, GalleryFilter filter, GallerySort sort, string search)
        {
            var q = games;
            switch (filter)
            {
                case GalleryFilter.Favorites: q = q.Where(g => g.Favorite); break;
                case GalleryFilter.RecentlyPlayed:
                    q = q.Where(g => g.LastPlayedAtUtc.HasValue).OrderByDescending(g => g.LastPlayedAtUtc).Take(RecentLimit);
                    break;
                case GalleryFilter.RecentlyAdded: q = q.OrderByDescending(g => g.ImportedAtUtc).Take(RecentLimit); break;
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                string s = search.Trim();
                q = q.Where(g => (g.Title ?? "").IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (g.Developer ?? "").IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            switch (sort)
            {
                case GallerySort.DateAdded: return q.OrderByDescending(g => g.ImportedAtUtc).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
                case GallerySort.LastPlayed:
                    return q.OrderByDescending(g => g.LastPlayedAtUtc ?? DateTime.MinValue).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
                default: return q.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal).ToList();
            }
        }

        /// <summary>Card status line: Ready / Unsupported mapper / File missing.</summary>
        public static string StatusOf(GameEntry g)
        {
            if (!g.Available) return "File missing";
            if (!g.MapperSupported) return "Unsupported mapper " + g.Mapper;
            return "Ready";
        }

        /// <summary>Stable placeholder colour for generated cartridge art (0xRRGGBB), derived from the hash.</summary>
        public static uint PlaceholderColor(GameEntry g)
        {
            uint h = Convert.ToUInt32((g.Sha256 ?? "000000").Substring(0, 6), 16);
            // Keep it dark enough for white text: clamp each channel to 40..160.
            uint r = 40 + ((h >> 16) & 0xFF) * 120 / 255, gr = 40 + ((h >> 8) & 0xFF) * 120 / 255, b = 40 + (h & 0xFF) * 120 / 255;
            return (r << 16) | (gr << 8) | b;
        }
    }
}
