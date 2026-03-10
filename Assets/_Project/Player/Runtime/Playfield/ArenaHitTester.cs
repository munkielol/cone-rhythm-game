// ArenaHitTester.cs
// Implements the canonical arena/lane hit-test algorithm from the spec.
//
// Hit-testing always runs in PlayfieldLocal space (PlayfieldRoot local XY) to avoid
// aspect-ratio distortion. The frustum visual is never used for interaction.
//
// Canonical algorithm (locked, spec §5.5):
//
//   Precompute per-arena:
//     centerLocalXY = NormalizedToLocal((centerX, centerY))
//     minDimLocal   = min(playfieldWidth, playfieldHeight)
//     outerLocal    = outerRadius * minDimLocal
//     bandLocal     = bandThickness * minDimLocal
//     innerLocal    = outerLocal - bandLocal
//
//   Per touch:
//     v     = hitLocalXY - centerLocalXY
//     r     = length(v)
//     deg   = atan2(v.y, v.x), normalized to [0, 360)
//
//   Band test:   innerLocal <= r <= outerLocal
//   Arc test:    wrap-safe (AngleUtil.IsAngleInArc)
//   Lane test:   abs(ShortestSignedAngleDelta(deg, laneCenterDeg)) <= halfWidthDeg
//
//   Final: hit iff band AND arc AND lane tests all pass.
//
// ArenaGeometry and LaneGeometry hold the evaluated (sampled) values at a given timeMs.

using UnityEngine;

namespace RhythmicFlow.Player
{
    // -----------------------------------------------------------------------
    // Data structs: evaluated geometry at a given chart time
    // -----------------------------------------------------------------------

    /// <summary>
    /// Arena geometry evaluated at a specific timeMs (sampled from keyframe tracks).
    /// All values are already evaluated; no further keyframe interpolation needed.
    /// Spec §5.5.
    /// </summary>
    public struct ArenaGeometry
    {
        /// <summary>Normalized center X of the arena (0..1).</summary>
        public float CenterXNorm;
        /// <summary>Normalized center Y of the arena (0..1).</summary>
        public float CenterYNorm;
        /// <summary>Outer radius normalized to playfield min-dimension.</summary>
        public float OuterRadiusNorm;
        /// <summary>Band thickness normalized to playfield min-dimension.</summary>
        public float BandThicknessNorm;
        /// <summary>Arc start angle in degrees (0° = right, CCW).</summary>
        public float ArcStartDeg;
        /// <summary>Arc sweep in degrees (0..360]; 360 = full ring.</summary>
        public float ArcSweepDeg;
    }

    /// <summary>
    /// Lane geometry evaluated at a specific timeMs.
    /// Spec §5.5.
    /// </summary>
    public struct LaneGeometry
    {
        /// <summary>Lane center angle in degrees (0° = right, CCW).</summary>
        public float CenterDeg;
        /// <summary>Lane full angular width in degrees (must be > 0).</summary>
        public float WidthDeg;
    }

    // -----------------------------------------------------------------------
    // ArenaHitTester
    // -----------------------------------------------------------------------

    /// <summary>
    /// Performs the canonical hit-test for arena/lane membership.
    /// All math runs in PlayfieldLocal space (spec §5.5 "Authoritative interaction space").
    /// </summary>
    public static class ArenaHitTester
    {
        // -------------------------------------------------------------------
        // Hit polar decomposition
        // -------------------------------------------------------------------

        /// <summary>
        /// Decomposes a PlayfieldLocal hit point relative to an arena center into
        /// polar coordinates (r, thetaDeg) and the normalized band position s.
        ///
        /// Spec §5.5 algorithm step 2 + band position formula.
        /// </summary>
        /// <param name="hitLocalXY">Hit point in PlayfieldRoot local XY.</param>
        /// <param name="arena">Evaluated arena geometry at hit time.</param>
        /// <param name="playfieldTransform">Current playfield transform.</param>
        /// <param name="r">Distance from arena center in local units.</param>
        /// <param name="thetaDeg">Angle in [0, 360) from arena center.</param>
        /// <param name="s">Normalized band position: 0 = inner edge, 1 = outer edge.</param>
        public static void DecomposeHit(
            Vector2          hitLocalXY,
            ArenaGeometry    arena,
            PlayfieldTransform playfieldTransform,
            out float        r,
            out float        thetaDeg,
            out float        s)
        {
            Vector2 centerLocal = playfieldTransform.NormalizedToLocal(
                new Vector2(arena.CenterXNorm, arena.CenterYNorm));

            Vector2 v = hitLocalXY - centerLocal;

            r        = v.magnitude;
            thetaDeg = AngleUtil.Normalize360(Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg);

            // Compute band position (spec §5.5).
            float outerLocal = playfieldTransform.NormRadiusToLocal(arena.OuterRadiusNorm);
            float bandLocal  = playfieldTransform.NormRadiusToLocal(arena.BandThicknessNorm);
            float innerLocal = outerLocal - bandLocal;

            s = (bandLocal > 0f)
                ? Mathf.Clamp01((r - innerLocal) / bandLocal)
                : 0f;
        }

        // -------------------------------------------------------------------
        // Hit band computation (spec §5.5.2) — single source of truth
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes hit-band and visual radii for an arena in PlayfieldLocal units.
        /// This is the single canonical source used by JudgementEngine (gameplay) and
        /// PlayerDebugRenderer (green arcs + highlight) — all callers must use this
        /// method so visual arcs always match the actual acceptance zone.
        ///
        /// <para><b>Inward bound — coverage fraction (spec §5.5.2):</b><br/>
        ///   inwardDepthLocal = max(0, judgementRadiusLocal − chartInnerLocal)<br/>
        ///   hitBandInner     = judgementRadiusLocal − Clamp01(HitBandInnerCoverage01) × inwardDepthLocal<br/>
        ///   HitBandInnerCoverage01 = 0 → no inward tolerance (hitInner == judgement).<br/>
        ///   HitBandInnerCoverage01 = 1 → full depth to chartInnerLocal.<br/>
        /// </para>
        ///
        /// <para><b>Outward bound — explicit norm (spec §5.5.2):</b><br/>
        ///   hitBandOuter  = judgement + HitBandOuterInsetNorm × minDim<br/>
        ///   hitOuterLocal = hitBandOuter + InputBandExpandOuterNorm × minDim (NOT clamped)<br/>
        ///   The outward tolerance intentionally exceeds visualOuterLocal to absorb flat-plane<br/>
        ///   parallax: a camera above z=0 projects the outer arc (z=0.15) to a flat-plane radius<br/>
        ///   larger than chartOuter. Clamping to visualOuterLocal removes that margin.<br/>
        /// </para>
        ///
        /// <para><b>InputBandExpand* additive fine-tune:</b><br/>
        ///   hitInnerLocal = max(hitBandInner − InputBandExpandInnerNorm × minDim, chartInner)<br/>
        ///   hitOuterLocal = hitBandOuter + InputBandExpandOuterNorm × minDim<br/>
        /// </para>
        /// </summary>
        public static void ComputeHitBandLocal(
            ArenaGeometry      arena,
            PlayfieldTransform playfieldTransform,
            out float          hitInnerLocal,
            out float          hitOuterLocal,
            out float          judgementRadiusLocal,
            out float          visualOuterLocal)
        {
            float minDim     = playfieldTransform.MinDimLocal;
            float chartOuter = playfieldTransform.NormRadiusToLocal(arena.OuterRadiusNorm);
            float chartBand  = playfieldTransform.NormRadiusToLocal(arena.BandThicknessNorm);
            float chartInner = chartOuter - chartBand;

            judgementRadiusLocal = chartOuter - PlayerSettingsStore.JudgementInsetNorm * minDim;
            visualOuterLocal     = chartOuter + PlayerSettingsStore.VisualOuterExpandNorm * minDim;

            // Inward bound: fraction of available depth from judgement line toward chartInner (spec §5.5.2).
            // coverage=0 → hitInner at judgement (no inward allowance).
            // coverage=1 → hitInner at chartInner (full inward depth).
            float inwardDepthLocal = Mathf.Max(0f, judgementRadiusLocal - chartInner);
            float hitBandInner = judgementRadiusLocal
                - Mathf.Clamp01(PlayerSettingsStore.HitBandInnerCoverage01) * inwardDepthLocal;

            // Outward bound: explicit norm from judgement line outward (spec §5.5.2).
            float hitBandOuter = judgementRadiusLocal + PlayerSettingsStore.HitBandOuterInsetNorm * minDim;

            // InputBandExpand* additive fine-tune on top.
            // Inner clamped to chartInner so the hit band never extends past the ring's inner edge.
            // Outer is NOT clamped to visualOuterLocal: the outward tolerance intentionally extends
            // beyond the visible mesh to absorb flat-plane parallax.  When the camera looks at the
            // frustum from above, the screen ray through the outer arc (z=0.15) intersects the z=0
            // plane at r > chartOuter.  Without outward tolerance those taps would fail even though
            // the visual arc is squarely under the finger.  The visual surface raycast eliminates
            // the parallax when the MeshCollider layer is correctly assigned.
            hitInnerLocal = Mathf.Max(
                hitBandInner - PlayerSettingsStore.InputBandExpandInnerNorm * minDim,
                chartInner);
            hitOuterLocal =
                hitBandOuter + PlayerSettingsStore.InputBandExpandOuterNorm * minDim;
        }

        // -------------------------------------------------------------------
        // Full hit test
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns true if <paramref name="hitLocalXY"/> is inside the given arena band and arc.
        /// Does NOT test lane membership — use IsInsideLane for the full check.
        /// Spec §5.5 steps 3–4.
        ///
        /// <para><b>Hit Band / Input Band (touch hit-testing only, spec §5.5.2 / §5.5.1):</b><br/>
        /// Pass pre-computed expansion values relative to the chart band edges:<br/>
        ///   effectiveInner = innerLocal − expandInnerLocal<br/>
        ///   effectiveOuter = outerLocal + expandOuterLocal<br/>
        /// <b>Negative values are valid</b>: a negative expandInnerLocal narrows the band inward
        /// (e.g. when the hit band inner edge is farther out than chartInnerLocal).
        /// Default values of 0 reproduce the unmodified spec §5.5 behaviour.<br/>
        /// Callers (JudgementEngine.IsInsideLane) derive these from the hit-band formula:<br/>
        ///   expandInnerLocal = chartInner − hitInner  (may be negative)<br/>
        ///   expandOuterLocal = hitOuter  − chartOuter (may be negative)
        /// </para>
        /// </summary>
        /// <param name="expandInnerLocal">
        /// Local-unit adjustment subtracted from innerLocal. Negative = narrow inward.
        /// Default: 0 (no change).
        /// </param>
        /// <param name="expandOuterLocal">
        /// Local-unit adjustment added to outerLocal. Negative = narrow outward.
        /// Default: 0 (no change).
        /// </param>
        public static bool IsInsideArenaBand(
            Vector2            hitLocalXY,
            ArenaGeometry      arena,
            PlayfieldTransform playfieldTransform,
            out float          thetaDeg,
            float              expandInnerLocal = 0f,
            float              expandOuterLocal = 0f)
        {
            Vector2 centerLocal = playfieldTransform.NormalizedToLocal(
                new Vector2(arena.CenterXNorm, arena.CenterYNorm));

            Vector2 v = hitLocalXY - centerLocal;

            float r  = v.magnitude;
            thetaDeg = AngleUtil.Normalize360(Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg);

            float outerLocal = playfieldTransform.NormRadiusToLocal(arena.OuterRadiusNorm);
            float bandLocal  = playfieldTransform.NormRadiusToLocal(arena.BandThicknessNorm);
            float innerLocal = outerLocal - bandLocal;

            // Step 3: Radial band test (spec §5.5 / §5.5.1 with optional input expansion).
            // expandInnerLocal / expandOuterLocal are non-zero only for touch hit-testing
            // (passed from JudgementEngine.IsInsideLane).  Debug/visual callers pass 0.
            if (r < innerLocal - expandInnerLocal || r > outerLocal + expandOuterLocal)
            {
                return false;
            }

            // Step 4: Arena arc test — wrap-safe (spec §5.5).
            return AngleUtil.IsAngleInArc(thetaDeg, arena.ArcStartDeg, arena.ArcSweepDeg);
        }

        /// <summary>
        /// Returns true if <paramref name="thetaDeg"/> is inside the given lane slice.
        /// Call this after IsInsideArenaBand confirms the point is in the band.
        /// Spec §5.5 step 5.
        /// </summary>
        /// <param name="thetaDeg">Angle of the touch in [0, 360), from IsInsideArenaBand.</param>
        /// <param name="lane">Evaluated lane geometry at hit time.</param>
        public static bool IsInsideLane(float thetaDeg, LaneGeometry lane)
        {
            // Spec §5.5 step 5:
            //   laneCenter = normalize360(centerDeg)
            //   halfWidth  = widthDeg * 0.5
            //   delta      = shortestSignedAngleDelta(deg, laneCenter)
            //   inside iff abs(delta) <= halfWidth
            float laneCenter = AngleUtil.Normalize360(lane.CenterDeg);
            float halfWidth  = lane.WidthDeg * 0.5f;
            float delta      = AngleUtil.ShortestSignedAngleDeltaDeg(laneCenter, thetaDeg);

            return Mathf.Abs(delta) <= halfWidth;
        }

        /// <summary>
        /// Combined arena-band + lane test. Returns true if the touch is inside the lane.
        /// Spec §5.5 steps 3–6 (full membership).
        /// </summary>
        public static bool IsInsideFullLane(
            Vector2            hitLocalXY,
            ArenaGeometry      arena,
            LaneGeometry       lane,
            PlayfieldTransform playfieldTransform)
        {
            if (!IsInsideArenaBand(hitLocalXY, arena, playfieldTransform, out float thetaDeg))
            {
                return false;
            }

            return IsInsideLane(thetaDeg, lane);
        }

        /// <summary>
        /// Returns the absolute angular distance from the touch to the lane centerline.
        /// Used as the tie-break metric in overlap arbitration (spec §7.6 criterion 2).
        /// </summary>
        public static float AngularDistanceToLaneCenter(float thetaDeg, LaneGeometry lane)
        {
            float laneCenter = AngleUtil.Normalize360(lane.CenterDeg);
            return Mathf.Abs(AngleUtil.ShortestSignedAngleDeltaDeg(laneCenter, thetaDeg));
        }
    }
}
