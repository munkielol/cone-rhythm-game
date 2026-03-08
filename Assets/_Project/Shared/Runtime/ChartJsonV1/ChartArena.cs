// ChartArena.cs
// An arena is an annular arc band rendered on the playfield.
// All geometry is expressed in normalized playfield space (0..1).
// All animated fields are FloatTrack keyframe lists.
//
// Angle convention (locked, spec §5.5 / §6):
//   0° = +X (right), angles increase CCW, values normalized to [0, 360).
//
// Radius convention (spec §5.5):
//   outerRadius and bandThickness are normalized to
//   min(playfieldWidthLocal, playfieldHeightLocal) — the "min-dim" rule.
//   innerRadius = outerRadius - bandThickness.
//   All radius math must use PlayfieldLocal (not screen pixels) to stay aspect-safe.

using System;

namespace RhythmicFlow.Shared
{
    [Serializable]
    public class ChartArena
    {
        // Unique string identifier. Must be non-empty and unique within the chart.
        public string arenaId;

        // --- Interaction toggle ---
        // 0 = arena inactive (no hit-testing), 1 = active.
        // RULES (spec §5.6 / §12.1): values must be exactly 0 or 1; easing must be "hold" only.
        public FloatTrack enabled = new FloatTrack();

        // --- Visual opacity (does not affect hit-testing, spec §5.6) ---
        // Range: 0.0 (transparent) to 1.0 (opaque).
        public FloatTrack opacity = new FloatTrack();

        // --- Center position in normalized playfield coordinates ---
        public FloatTrack centerX = new FloatTrack(); // 0 = left edge, 1 = right edge
        public FloatTrack centerY = new FloatTrack(); // 0 = bottom edge, 1 = top edge

        // --- Radii (normalized to playfield min-dimension) ---
        // outerRadius: distance from center to the outer edge of the band.
        public FloatTrack outerRadius = new FloatTrack();

        // bandThickness: radial depth of the band.
        // innerRadius = outerRadius - bandThickness.
        public FloatTrack bandThickness = new FloatTrack();

        // --- Arc span ---
        // arcStartDeg: angle at which the arc begins. 0° = right (+X axis), increases CCW.
        public FloatTrack arcStartDeg = new FloatTrack();

        // arcSweepDeg: how many degrees the arc spans. Range: (0, 360]. 360 = full ring.
        public FloatTrack arcSweepDeg = new FloatTrack();
    }
}
