// ChartNote.cs
// All four note types (tap, flick, catch, hold) share one class.
// Unity JsonUtility cannot deserialize polymorphic/base-class types,
// so we use a single flat struct with a "type" discriminator string.
// Only the fields relevant to the active type are meaningful.
//
// File order of notes is the stable final tie-break for simultaneous judgment (spec §7.6).

using System;
using System.Collections.Generic;

namespace RhythmicFlow.Shared
{
    // String constants for the note type field (spec §9.1).
    public static class NoteType
    {
        public const string Tap   = "tap";
        public const string Flick = "flick";
        public const string Catch = "catch";
        public const string Hold  = "hold";
    }

    // String constants for the flick direction field (spec §9.3).
    // Directions are lane-relative at note time:
    //   L/R = tangential (CCW / CW around arena center)
    //   U/D = radial    (out / in  from arena center)
    public static class FlickDirection
    {
        public const string Left  = "L";
        public const string Right = "R";
        public const string Up    = "U";
        public const string Down  = "D";
    }

    [Serializable]
    public class ChartNote
    {
        // Stable GUID assigned by the editor (spec §9.1).
        // Required. Used for undo/selection in the editor; player logs it on error.
        public string noteId;

        // The lane this note belongs to. Must match an existing laneId.
        public string laneId;

        // Note type: "tap" | "flick" | "catch" | "hold".
        public string type;

        // Whether this note requires player input (spec §9.1).
        // Default is true; only write false for visual-only decorative notes.
        // JsonUtility NOTE: the C# field initializer (= true) means this field stays
        // true when absent from JSON, and overrides to false only when the JSON
        // explicitly contains "judging": false.
        public bool judging = true;

        // ---- Tap fields (type == "tap") ----
        // ---- Catch fields (type == "catch") ----
        // Timestamp in milliseconds from chart start.
        public int timeMs;

        // ---- Flick fields (type == "flick") ----
        // Uses timeMs above. Direction is lane-relative: "L" | "R" | "U" | "D".
        public string direction;

        // ---- Hold fields (type == "hold") ----
        // Hold start timestamp. Must be < endTimeMs.
        public int startTimeMs;

        // Hold end timestamp. Must be > startTimeMs.
        public int endTimeMs;

        // Baked tick timestamps in milliseconds, authored by the editor (spec §9.5).
        // Rules (spec §12.1):
        //   – Must be strictly increasing (sorted, no duplicates).
        //   – All values must lie within [startTimeMs, endTimeMs].
        // The player treats these as authoritative; it does not re-derive ticks at runtime.
        public List<int> tickTimesMs = new List<int>();
    }
}
