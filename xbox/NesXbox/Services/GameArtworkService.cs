using System;
using NesXbox.Logic;
using Windows.UI;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace NesXbox.Services
{
    /// <summary>
    /// Card artwork: a cached cover from LocalFolder when the source permitted it, otherwise a generated
    /// cartridge placeholder (hash-derived colour + initials). Never downloads commercial box art.
    /// </summary>
    static class GameArtworkService
    {
        public const int CardImageWidth = 200;

        public static ImageSource CoverFor(GameEntry g)
        {
            if (string.IsNullOrEmpty(g.CoverPath)) return null;
            // Decode at card size so hundreds of cards stay cheap; the GridView virtualises the rest.
            return new BitmapImage(new Uri("ms-appdata:///local/" + g.CoverPath)) { DecodePixelWidth = CardImageWidth };
        }

        public static Brush PlaceholderBrush(GameEntry g)
        {
            uint c = GalleryQuery.PlaceholderColor(g);
            return new SolidColorBrush(Color.FromArgb(0xFF, (byte)(c >> 16), (byte)(c >> 8), (byte)c));
        }

        public static string Initials(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "?";
            var words = title.Split(new[] { ' ', '-', ':' }, StringSplitOptions.RemoveEmptyEntries);
            string s = words.Length >= 2 ? words[0].Substring(0, 1) + words[1].Substring(0, 1) : title.Substring(0, Math.Min(2, title.Length));
            return s.ToUpperInvariant();
        }
    }
}
