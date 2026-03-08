// PackCatalog.cs
// In-memory catalog of successfully loaded song packs.
// Populated by PackScanner on startup and on returning to Song Select (spec §2.6).
//
// PackEntry holds the data consumed by the Song Select UI (spec §8.1):
//   – jacket image bytes for best-fit display
//   – title, artist, lengthMs, bpmDisplay
//   – list of available difficulties (difficultyId + chart path)
//   – preview range if provided

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RhythmicFlow.Player
{
    // -----------------------------------------------------------------------
    // PackEntry — one successfully loaded .rpk pack
    // -----------------------------------------------------------------------

    /// <summary>
    /// A successfully loaded and validated song pack entry.
    /// Holds all data needed by Song Select UI (spec §8.1).
    /// </summary>
    public class PackEntry
    {
        /// <summary>Absolute path to the .rpk file on disk.</summary>
        public string RpkPath { get; set; }

        /// <summary>songId from songinfo.json.</summary>
        public string SongId { get; set; }

        /// <summary>Song title for display.</summary>
        public string Title { get; set; }

        /// <summary>Artist name for display.</summary>
        public string Artist { get; set; }

        /// <summary>Total song length in milliseconds.</summary>
        public int LengthMs { get; set; }

        /// <summary>BPM range for display (spec §2.3: bpmDisplay { min, max }).</summary>
        public float BpmMin { get; set; }
        public float BpmMax { get; set; }

        /// <summary>
        /// Raw bytes of the best-fit jacket image (any available size: 256/512/1024).
        /// Null if no jacket was found or loading failed.
        /// The UI must create a Texture2D from these bytes.
        /// </summary>
        public byte[] JacketBytes { get; set; }

        /// <summary>
        /// All difficulties available in this pack (spec §2.3: charts[]).
        /// </summary>
        public List<DifficultyEntry> Difficulties { get; set; } = new List<DifficultyEntry>();

        /// <summary>
        /// Optional preview loop range (spec §2.3: preview { startTimeMs, endTimeMs }).
        /// PreviewStartMs == PreviewEndMs == 0 means no preview defined.
        /// </summary>
        public int PreviewStartMs { get; set; }
        public int PreviewEndMs   { get; set; }

        /// <summary>True when a valid preview range was found in songinfo.json.</summary>
        public bool HasPreview => PreviewEndMs > PreviewStartMs;
    }

    /// <summary>
    /// One difficulty available in a pack (spec §2.3: charts[] entry).
    /// </summary>
    public class DifficultyEntry
    {
        /// <summary>Difficulty identifier, e.g. "easy", "normal", "hard".</summary>
        public string DifficultyId { get; set; }

        /// <summary>In-archive path to the chart JSON, e.g. "charts/normal.json".</summary>
        public string ChartPath { get; set; }
    }

    // -----------------------------------------------------------------------
    // PackCatalog — in-memory list of all valid packs
    // -----------------------------------------------------------------------

    /// <summary>
    /// In-memory catalog populated by PackScanner.
    /// Consumed by Song Select UI.
    /// Spec §2.6: re-populated on startup and on returning to Song Select.
    /// </summary>
    public class PackCatalog
    {
        private readonly List<PackEntry> _entries = new List<PackEntry>();

        /// <summary>Read-only view of all valid pack entries.</summary>
        public IReadOnlyList<PackEntry> Entries => _entries;

        /// <summary>Adds a successfully loaded pack entry to the catalog.</summary>
        public void Add(PackEntry entry) => _entries.Add(entry);

        /// <summary>Clears all entries (called before a fresh scan).</summary>
        public void Clear() => _entries.Clear();

        /// <summary>Total number of valid packs currently in the catalog.</summary>
        public int Count => _entries.Count;
    }
}
