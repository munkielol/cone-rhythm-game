// ChartSong.cs
// Song identity, tempo map, and timing offset stored in the chart file.

using System;
using System.Collections.Generic;

namespace RhythmicFlow.Shared
{
    // Song identity and timing offset for one chart difficulty.
    // Corresponds to the "song" field in the root ChartJsonV1 object (spec §14).
    [Serializable]
    public class ChartSong
    {
        // Matches the songId in songinfo.json so player can cross-reference.
        public string songId;

        // The difficulty identifier (e.g. "easy", "normal", "hard").
        public string difficultyId;

        // Relative path to audio inside the .rpk pack (always "audio/song.ogg" for v0).
        public string audioFile;

        // Chart timing offset in milliseconds. Default 0.
        // Sign convention (locked by spec §3.3 / §14):
        //   Positive = judge LATER (chart events occur later relative to audio playback).
        //   effectiveChartTimeMs = songDspTimeMs + audioOffsetMs + UserOffsetMs
        // This offset shifts all chart content: judgment, note spawn, keyframe evaluation,
        // hold tick processing. It does NOT shift audio playback.
        public int audioOffsetMs;
    }

    // Wrapper for the ordered list of tempo segments (spec §3.2 / §2.2).
    // Kept as a nested object so JsonUtility can handle it cleanly.
    [Serializable]
    public class ChartTempo
    {
        // Sorted, non-overlapping tempo segments.
        // First segment must have startTimeMs == 0 (spec §12.1).
        public List<TempoSegment> segments = new List<TempoSegment>();
    }

    // One segment of the tempo map.
    //
    // Two segment types (spec §3.2 / §2.2):
    //
    //   type == "constant"
    //     – startTimeMs : when this constant-BPM segment begins
    //     – bpm         : beats per minute (must be > 0)
    //
    //   type == "ramp" (linear BPM change in time)
    //     – startTimeMs : when the ramp begins
    //     – endTimeMs   : when the ramp ends (must be > startTimeMs)
    //     – startBpm    : BPM at the start of the ramp (must be > 0)
    //     – endBpm      : BPM at the end of the ramp (must be > 0)
    //     – BPM interpolates linearly in time between startBpm and endBpm.
    //
    // Unused fields for a given type should be left at their default (0).
    [Serializable]
    public class TempoSegment
    {
        // "constant" or "ramp".
        public string type;

        // Millisecond timestamp when this segment begins (always required).
        public int startTimeMs;

        // --- Constant segment ---
        public float bpm;        // used when type == "constant"

        // --- Ramp segment ---
        public int   endTimeMs;  // used when type == "ramp"
        public float startBpm;   // used when type == "ramp"
        public float endBpm;     // used when type == "ramp"
    }
}
