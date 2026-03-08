// ChartLane.cs
// A lane is an angular slice of a parent arena band.
// Notes are placed into lanes; each note belongs to exactly one lane.
// Multiple lanes in the same arena can overlap (spec §5.5 / §7).

using System;

namespace RhythmicFlow.Shared
{
    [Serializable]
    public class ChartLane
    {
        // Unique string identifier. Must be non-empty and unique within the chart.
        public string laneId;

        // The arena this lane belongs to. Must match an existing arenaId (spec §14).
        public string arenaId;

        // Render order and input tie-break priority (larger value = higher priority, spec §7.6).
        public int priority;

        // --- Interaction toggle ---
        // 0 = lane inactive (no hit-testing), 1 = active.
        // RULES (spec §5.6 / §12.1): values must be exactly 0 or 1; easing must be "hold" only.
        public FloatTrack enabled = new FloatTrack();

        // --- Visual opacity (does not affect hit-testing, spec §5.6) ---
        // Range: 0.0 (transparent) to 1.0 (opaque).
        public FloatTrack opacity = new FloatTrack();

        // --- Angular position ---
        // Center angle of the lane slice in degrees. 0° = right (+X), increases CCW.
        public FloatTrack centerDeg = new FloatTrack();

        // --- Angular width ---
        // Full angular width of the lane slice in degrees. Must be > 0.
        // The lane spans [centerDeg - widthDeg/2, centerDeg + widthDeg/2] (wrap-safe).
        public FloatTrack widthDeg = new FloatTrack();
    }
}
