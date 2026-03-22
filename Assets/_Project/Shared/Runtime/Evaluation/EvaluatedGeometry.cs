// EvaluatedGeometry.cs
// Shared runtime structs produced by ChartRuntimeEvaluator each frame.
// These are the canonical evaluated (sampled-at-timeMs) representations of
// ChartArena, ChartLane, and ChartCamera.  BOTH the Player App and the
// Chart Editor Playfield Preview consume these structs — neither app
// re-implements keyframe evaluation logic.
//
// Design notes:
//   – All float fields are already evaluated; no further keyframe math needed.
//   – Angle fields (ArcStartDeg, CenterDeg, rotations) are already
//     wrap-normalised to [0, 360) by ChartRuntimeEvaluator.
//   – Radii remain normalised (0..1 relative to playfield min-dimension);
//     conversion to PlayfieldLocal units is done in the app layer via
//     PlayfieldTransform.
//   – EnabledBool is decoded from the 0/1 float track (value >= 0.5 → true,
//     spec §5.9).
//   – Immutable fields (ArenaId, LaneId, ArenaId on lane, Priority) are set
//     once in the ChartRuntimeEvaluator constructor and never change.
//
// Spec anchors: player spec §5.6, §5.9; chart editor spec §3.3.

namespace RhythmicFlow.Shared
{
    // -----------------------------------------------------------------------
    // EvaluatedArena
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluated state of one arena at a specific timeMs.
    /// Produced every frame by <see cref="ChartRuntimeEvaluator.Evaluate"/>.
    /// All animated values are sampled; no keyframe math is needed after this.
    /// Spec §5.5 / §5.6 / §5.9.
    /// </summary>
    public struct EvaluatedArena
    {
        // -----------------------------------------------------------------
        // Immutable identity (set once in ChartRuntimeEvaluator constructor)
        // -----------------------------------------------------------------

        /// <summary>String identifier from the chart. Never changes after construction.</summary>
        public string ArenaId;

        // -----------------------------------------------------------------
        // Animated state (written each Evaluate() call)
        // -----------------------------------------------------------------

        /// <summary>
        /// True when the arena is interactive (enabled track value >= 0.5).
        /// Disabled arenas receive no hit-testing (spec §5.6).
        /// </summary>
        public bool EnabledBool;

        /// <summary>Visual opacity [0..1]. Does not affect hit-testing (spec §5.6).</summary>
        public float Opacity;

        /// <summary>Normalized center X [0..1] (0 = left edge, 1 = right edge).</summary>
        public float CenterXNorm;

        /// <summary>Normalized center Y [0..1] (0 = bottom edge, 1 = top edge).</summary>
        public float CenterYNorm;

        /// <summary>Outer radius, normalised to the playfield min-dimension.</summary>
        public float OuterRadiusNorm;

        /// <summary>Band thickness, normalised to the playfield min-dimension.</summary>
        public float BandThicknessNorm;

        /// <summary>
        /// Arc start angle in degrees [0, 360).  0° = +X axis, angles increase CCW.
        /// Evaluated with shortest-path wrap (spec §5.9).
        /// </summary>
        public float ArcStartDeg;

        /// <summary>Arc sweep in degrees (0, 360].  360 = full ring.</summary>
        public float ArcSweepDeg;
    }

    // -----------------------------------------------------------------------
    // EvaluatedLane
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluated state of one lane at a specific timeMs.
    /// Produced every frame by <see cref="ChartRuntimeEvaluator.Evaluate"/>.
    /// Spec §5.5 / §5.6 / §5.9.
    /// </summary>
    public struct EvaluatedLane
    {
        // -----------------------------------------------------------------
        // Immutable identity (set once in ChartRuntimeEvaluator constructor)
        // -----------------------------------------------------------------

        /// <summary>String identifier from the chart. Never changes after construction.</summary>
        public string LaneId;

        /// <summary>Parent arena identifier. Never changes after construction.</summary>
        public string ArenaId;

        /// <summary>
        /// Input tie-break priority (larger = higher priority, spec §7.6).
        /// Never changes after construction.
        /// </summary>
        public int Priority;

        // -----------------------------------------------------------------
        // Animated state (written each Evaluate() call)
        // -----------------------------------------------------------------

        /// <summary>
        /// True when the lane is interactive (enabled track value >= 0.5).
        /// Disabled lanes receive no hit-testing (spec §5.6).
        /// </summary>
        public bool EnabledBool;

        /// <summary>Visual opacity [0..1]. Does not affect hit-testing (spec §5.6).</summary>
        public float Opacity;

        /// <summary>
        /// Lane center angle in degrees [0, 360).  0° = +X axis, angles increase CCW.
        /// Evaluated with shortest-path wrap (spec §5.9).
        /// </summary>
        public float CenterDeg;

        /// <summary>Full angular width in degrees.  Must be > 0 in a valid chart.</summary>
        public float WidthDeg;
    }

    // -----------------------------------------------------------------------
    // EvaluatedCamera
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluated state of the chart camera at a specific timeMs.
    /// Produced every frame by <see cref="ChartRuntimeEvaluator.Evaluate"/>.
    ///
    /// <para>Camera motion is purely visual; hit-testing is never affected
    /// (spec §8 / §14).</para>
    /// </summary>
    public struct EvaluatedCamera
    {
        /// <summary>True when the camera track is active (enabled value >= 0.5).</summary>
        public bool EnabledBool;

        /// <summary>World-space camera position X.</summary>
        public float PosX;

        /// <summary>World-space camera position Y.</summary>
        public float PosY;

        /// <summary>World-space camera position Z.</summary>
        public float PosZ;

        /// <summary>Camera pitch (X-axis rotation) in degrees [0, 360).</summary>
        public float RotPitchDeg;

        /// <summary>Camera yaw (Y-axis rotation) in degrees [0, 360).</summary>
        public float RotYawDeg;

        /// <summary>Camera roll (Z-axis rotation) in degrees [0, 360).</summary>
        public float RotRollDeg;

        /// <summary>Vertical field of view in degrees.</summary>
        public float FovDeg;
    }
}
