// ChartJsonV1.cs
// Root document for a single chart difficulty.
// Exported as  charts/<difficultyId>.json  inside the .rpk song pack.
// Deserialized with Unity's built-in JsonUtility.
//
// JsonUtility constraints that shaped this design:
//   – No Dictionary<K,V> support → all parameters use named list/struct fields.
//   – No polymorphic (base-class) deserialization → all note types share ChartNote.
//   – Nullable types not supported → C# field initializers supply safe defaults.

using System;
using System.Collections.Generic;

namespace RhythmicFlow.Shared
{
    // Root chart document.
    [Serializable]
    public class ChartJsonV1
    {
        // Schema version integer. Must equal 1 for v0 charts.
        // Player and editor both reject charts with an unsupported formatVersion.
        public int formatVersion;

        // Song identity and timing offset for this difficulty.
        public ChartSong song = new ChartSong();

        // Tempo map: sorted, non-overlapping tempo segments.
        // Used by the editor for beat-grid snapping and hold-tick generation.
        // The player uses it for display/analytics only; hold tickTimesMs are authoritative.
        public ChartTempo tempo = new ChartTempo();

        // All arenas in this chart. An arena is an annular arc band on the playfield.
        public List<ChartArena> arenas = new List<ChartArena>();

        // All lanes in this chart. A lane is an angular slice of a parent arena.
        public List<ChartLane> lanes = new List<ChartLane>();

        // Animated camera data for the gameplay camera (visual only).
        public ChartCamera camera = new ChartCamera();

        // All notes in this chart.
        // IMPORTANT: file order is the stable tie-break for simultaneous judgment (spec §7.6).
        public List<ChartNote> notes = new List<ChartNote>();
    }
}
