using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;

namespace NesXbox.Logic
{
    public static class ImportChannel
    {
        public const string Usb = "usb";
        public const string Lan = "lan";
        public const string HomebrewHub = "homebrewhub";
        public const string GitHub = "github";
        public const string Recovered = "recovered";
    }

    /// <summary>One game in library.json. Field names follow docs/GAME_LIBRARY.md.</summary>
    [DataContract]
    public sealed class GameEntry
    {
        [DataMember(Name = "id", Order = 0)] public string Id { get; set; }
        [DataMember(Name = "rom_path", Order = 1)] public string RomPath { get; set; }
        [DataMember(Name = "title", Order = 2)] public string Title { get; set; }
        [DataMember(Name = "sha256", Order = 3)] public string Sha256 { get; set; }
        [DataMember(Name = "mapper", Order = 4)] public int Mapper { get; set; }
        [DataMember(Name = "size", Order = 5)] public long Size { get; set; }
        [DataMember(Name = "battery", Order = 6)] public bool Battery { get; set; }
        [DataMember(Name = "source", Order = 7)] public string Source { get; set; }
        [DataMember(Name = "source_url", Order = 8)] public string SourceUrl { get; set; }
        [DataMember(Name = "license", Order = 9)] public string License { get; set; }
        [DataMember(Name = "developer", Order = 10)] public string Developer { get; set; }
        [DataMember(Name = "cover_path", Order = 11)] public string CoverPath { get; set; }
        [DataMember(Name = "favorite", Order = 12)] public bool Favorite { get; set; }
        [DataMember(Name = "imported_at", Order = 13)] public string ImportedAt { get; set; }
        [DataMember(Name = "last_played_at", Order = 14)] public string LastPlayedAt { get; set; }

        /// <summary>Runtime only: false when the ROM file is missing on disk.</summary>
        [IgnoreDataMember] public bool Available { get; set; } = true;

        [IgnoreDataMember] public bool MapperSupported => InesHeader.IsMapperSupported(Mapper);
        [IgnoreDataMember] public bool Playable => Available && MapperSupported;
        [IgnoreDataMember] public DateTime ImportedAtUtc => ParseTime(ImportedAt) ?? DateTime.MinValue;
        [IgnoreDataMember] public DateTime? LastPlayedAtUtc => ParseTime(LastPlayedAt);

        public static string FormatTime(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        public static DateTime? ParseTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : (DateTime?)null;
        }
    }

    [DataContract]
    public sealed class LibraryDocument
    {
        public const int CurrentSchema = 1;
        [DataMember(Name = "schema_version", Order = 0)] public int SchemaVersion { get; set; } = CurrentSchema;
        [DataMember(Name = "games", Order = 1)] public List<GameEntry> Games { get; set; } = new List<GameEntry>();
    }
}
