// LaneGuideRenderer.cs
// Production lane guide renderer (spec §5.6 lane visuals).
//
// Draws exactly two thin radial lines per logical lane via Graphics.DrawMesh:
//   – left edge  at the lane's true left logical boundary
//   – right edge at the lane's true right logical boundary
//
// The center guide has been intentionally removed: only left and right edge
// guides are drawn.  This keeps the playfield uncluttered and makes lane
// boundaries the only visual cue.
//
// ── Body segments vs. guide boundaries ───────────────────────────────────────
//
//   Body segments (used by LaneSurfaceRenderer) may split at the arena seam.
//   Guide boundaries do not split — they represent the lane's actual angular
//   extent.  See ArenaOccupancyEvaluator.TryGetLaneGuideBoundaries for the
//   derivation of how the two true boundaries are recovered from segments.
//
//   A seam-split lane (one that straddles a full-circle arena's seam) produces:
//     LaneSurfaceRenderer:  two body meshes  (one per segment, seam-aware)
//     LaneGuideRenderer:    one mesh         (two guides at real left/right only)
//
// ── Interval source ───────────────────────────────────────────────────────────
//
//   Guide edge angles are sourced from ArenaOccupancyEvaluator.TryGetLaneGuideBoundaries()
//   — which recovers the two logical boundaries from the same seam-aware data as
//   LaneSurfaceRenderer.  This guarantees that guide edges land on the actual lane
//   boundaries even for rotating arenas and seam-crossing lanes.
//
// ── Loop structure ────────────────────────────────────────────────────────────
//
//   Mirrors LaneSurfaceRenderer outer structure:
//     Outer loop: arenas — calls ArenaOccupancyEvaluator.Compute() once per arena.
//     Inner loop: evaluator lanes filtered to this arena — one mesh per logical lane
//                 (exactly two guides per lane, regardless of seam-split state).
//
// ── Z layering ────────────────────────────────────────────────────────────────
//
//   Visual layering from bottom (+Z = toward camera):
//     Arena surface    — FrustumZAtRadius (base)
//     Lane surface     — FrustumZAtRadius + 0.005  (LaneSurfaceRenderer.liftLocal)
//     Lane guides      — FrustumZAtRadius + 0.008  (surfaceOffsetLocal default)
//     Notes            — FrustumZAtRadius + 0.010  (NoteLayerZLift)
//
//   surfaceOffsetLocal must remain greater than LaneSurfaceRenderer.liftLocal (0.005)
//   so that guides are always visible above the lane surface body.
//
// ── Mesh layout per segment ────────────────────────────────────────────────────
//
//   Each visible segment generates one mesh with 2 quads:
//     Quad 0 (verts 0–3):  left-edge radial line at segment.StartDeg
//     Quad 1 (verts 4–7):  right-edge radial line at segment.EndDeg
//
//   Each line spans innerLocal → visualOuterLocal along its angle, lifted onto the
//   frustum cone surface using PlayfieldFrustumProfile + surfaceOffsetLocal.
//
// ── Rendering pattern ─────────────────────────────────────────────────────────
//
//   Identical to JudgementRingRenderer and ArenaBandRenderer:
//     – pre-allocated Mesh pool, vertices written in-place every LateUpdate
//     – Graphics.DrawMesh — works in Game view without Gizmos, no child GOs required
//
// No dependency on PlayerDebugRenderer or PlayerDebugArenaSurface.
//
// Wiring:
//   1. Attach to any GO in the Player scene.
//   2. Assign playerAppController, guideMaterial, frustumProfile in the Inspector.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production lane guide renderer.  Draws left-edge and right-edge radial
    /// line strips for each visible lane segment (seam-aware, arena-clamped).
    ///
    /// <para>Guide angles are sourced from <see cref="ArenaOccupancyEvaluator"/> —
    /// the same intervals used by <see cref="LaneSurfaceRenderer"/> — so guide edges
    /// are always aligned with the drawn lane surface.</para>
    ///
    /// <para>Attach to any GO in the Player scene.  Assign
    /// <see cref="playerAppController"/>, <see cref="guideMaterial"/>, and
    /// <see cref="frustumProfile"/> in the Inspector.</para>
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/LaneGuideRenderer")]
    public class LaneGuideRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("PlayerAppController providing evaluated lane and arena geometry.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Material for the lane guide lines.  Use an unlit shader with _Color support.")]
        [SerializeField] private Material guideMaterial;

        [Header("Frustum Surface Alignment")]
        [Tooltip("Shared frustum profile.  Assign the same profile used by the production note renderers.")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        [Header("Appearance")]
        [Tooltip("Color applied to all lane guide lines via MaterialPropertyBlock._Color.")]
        [SerializeField] private Color guideColor = new Color(1.0f, 0.75f, 0.3f, 0.8f);

        [Tooltip("Tangential half-thickness of each radial line in PlayfieldLocal units.  Default: 0.003.")]
        [SerializeField] private float lineHalfThicknessLocal = 0.003f;

        [Tooltip("Local Z offset added to every guide line vertex (PlayfieldLocal units).\n\n" +
                 "This lifts guides above the lane surface body so they remain visible.\n\n" +
                 "Z layering (bottom → top):\n" +
                 "  Lane surface  =  FrustumZAtRadius + 0.005  (LaneSurfaceRenderer.liftLocal)\n" +
                 "  Lane guides   =  FrustumZAtRadius + surfaceOffsetLocal  ← this field\n" +
                 "  Notes         =  FrustumZAtRadius + 0.010\n\n" +
                 "MUST remain above LaneSurfaceRenderer.liftLocal (0.005) or guides will be\n" +
                 "occluded by the lane body.  Keep below 0.010 to stay under notes.\n" +
                 "Default: 0.008")]
        [Min(0f)]
        [SerializeField] private float surfaceOffsetLocal = 0.008f;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // Maximum number of guide meshes drawn per frame across all arenas.
        // One mesh per visible lane segment; a seam-split lane produces 2 segments,
        // so this is a segment count, not a lane count.
        private const int MaxLanePool = 64;

        // Each segment mesh = 2 quads: left-edge guide + right-edge guide.
        // Center guide is intentionally omitted.
        // 2 quads × 4 separate verts = 8 verts.
        // 2 quads × 2 tris × 3 indices = 12 tri indices.
        private const int QuadsPerLane = 2;
        private const int VertsPerLane = QuadsPerLane * 4;  // 8
        private const int TrisPerLane  = QuadsPerLane * 6;  // 12

        private Mesh[] _meshPool;
        private int    _poolUsed;

        // Vertex scratch: 2 quads × 4 verts = 8 verts.  Filled in-place each LateUpdate.
        private Vector3[] _vertScratch;

        private MaterialPropertyBlock _propBlock;

        // Shared occupancy evaluator — provides seam-aware, arena-clamped lane intervals.
        // Mirrors the evaluator in LaneSurfaceRenderer so guide edges align with surface edges.
        private ArenaOccupancyEvaluator _occupancy;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            _vertScratch = new Vector3[VertsPerLane];

            // Triangle index pattern — same for every lane mesh, set once here.
            // Each quad: [v+0]=r0-left, [v+1]=r1-left, [v+2]=r1-right, [v+3]=r0-right
            var triPattern = new int[TrisPerLane];
            for (int q = 0; q < QuadsPerLane; q++)
            {
                int v = q * 4;
                int t = q * 6;
                triPattern[t + 0] = v;
                triPattern[t + 1] = v + 1;
                triPattern[t + 2] = v + 2;
                triPattern[t + 3] = v;
                triPattern[t + 4] = v + 2;
                triPattern[t + 5] = v + 3;
            }

            _meshPool = new Mesh[MaxLanePool];
            for (int i = 0; i < MaxLanePool; i++)
            {
                var m = new Mesh { name = "LaneGuide" };
                m.vertices  = new Vector3[VertsPerLane]; // zero-filled placeholder
                m.triangles = triPattern;                // Unity copies internally
                m.RecalculateBounds();
                _meshPool[i] = m;
            }

            _propBlock = new MaterialPropertyBlock();
            _propBlock.SetColor("_Color", guideColor);

            // Shared occupancy evaluator — same capacity as LaneSurfaceRenderer so both
            // renderers process the same set of lanes per arena.
            _occupancy = new ArenaOccupancyEvaluator(MaxLanePool);
        }

        private void OnDestroy()
        {
            if (_meshPool == null) { return; }
            for (int i = 0; i < _meshPool.Length; i++)
            {
                if (_meshPool[i] != null) { Destroy(_meshPool[i]); _meshPool[i] = null; }
            }
        }

        // -------------------------------------------------------------------
        // Per-frame rendering
        // -------------------------------------------------------------------

        private void LateUpdate()
        {
            if (playerAppController == null || guideMaterial == null) { return; }

            ChartRuntimeEvaluator evaluator = playerAppController.Evaluator;
            PlayfieldTransform    pfT       = playerAppController.PlayfieldTf;
            Transform             pfRoot    = playerAppController.playfieldRoot;

            if (evaluator == null || pfT == null || pfRoot == null) { return; }

            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            float hInner = ReadFrustumHeightInner();
            float hOuter = ReadFrustumHeightOuter();

            _poolUsed = 0;

            // ── Outer loop: arenas ────────────────────────────────────────────────
            //
            // We iterate arenas rather than lanes so that ArenaOccupancyEvaluator.Compute()
            // is called once per arena and its results consumed immediately — same
            // structure as LaneSurfaceRenderer.  This ensures guide edges are sourced
            // from the same seam-aware intervals as the lane surface, keeping them aligned.
            for (int arenaIdx = 0; arenaIdx < evaluator.ArenaCount; arenaIdx++)
            {
                if (_poolUsed >= MaxLanePool) { break; }

                EvaluatedArena arena = evaluator.GetArena(arenaIdx);
                if (string.IsNullOrEmpty(arena.ArenaId) || !arena.EnabledBool) { continue; }

                // ── Arena geometry in PlayfieldLocal units ─────────────────────────
                float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Guide lines extend to the visual outer edge, matching LaneSurfaceRenderer.
                float visualOuterLocal = outerLocal
                    + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;

                // Skip degenerate geometry (band collapsed or inverted).
                if (visualOuterLocal <= innerLocal || innerLocal < 0f) { continue; }

                Vector2 center = pfT.NormalizedToLocal(
                    new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // ── Compute seam-aware lane intervals ──────────────────────────────
                //
                // Compute() fills the occupancy evaluator with all enabled lanes for this
                // arena, sorted and seam-split as needed.  Returns false for disabled or
                // degenerate arenas (both already guarded above).
                if (!_occupancy.Compute(arena, evaluator)) { continue; }

                // ── Inner loop: authored lanes (identity-stable) ───────────────────
                //
                // Iterate by evaluator lane index so each authored lane always draws its
                // own guide regardless of whether crossing lanes have swapped their
                // sorted positions (same reasoning as LaneSurfaceRenderer).
                for (int laneIdx = 0; laneIdx < evaluator.LaneCount; laneIdx++)
                {
                    if (_poolUsed >= MaxLanePool) { break; }

                    EvaluatedLane lane = evaluator.GetLane(laneIdx);

                    // Skip lanes outside this arena or currently disabled.
                    if (string.IsNullOrEmpty(lane.LaneId) || !lane.EnabledBool) { continue; }
                    if (lane.ArenaId != arena.ArenaId) { continue; }

                    // ── Look up logical guide boundaries ──────────────────────────
                    //
                    // TryGetLaneGuideBoundaries returns exactly two boundary angles
                    // (left and right) for this lane, regardless of how many body
                    // segments the seam-aware evaluator produced.
                    //
                    // For a seam-split lane (lane crossing the full-circle arena seam),
                    // LaneSurfaceRenderer draws two body meshes but we still draw only
                    // two guides — at the lane's actual left and right edges, not at
                    // the seam.  See ArenaOccupancyEvaluator.TryGetLaneGuideBoundaries
                    // for the full derivation.
                    //
                    // Returns false only when the lane has no visible segments (entirely
                    // outside the arena span, or disabled — already filtered above, but
                    // guard for safety).
                    if (!_occupancy.TryGetLaneGuideBoundaries(
                        laneIdx, out float leftDeg, out float rightDeg)) { continue; }

                    // ── Draw exactly two guides per logical lane ───────────────────
                    //
                    // Quad 0: left-edge guide at the lane's true left logical boundary.
                    // Quad 1: right-edge guide at the lane's true right logical boundary.
                    //
                    // Boundary angles are in global-extended degrees; FillRadialLineVerts
                    // uses Mathf.Cos/Sin which handle values outside [0, 360) correctly.

                    FillRadialLineVerts(_vertScratch, 0, leftDeg, center,
                        innerLocal, visualOuterLocal,
                        innerLocal, outerLocal, hInner, hOuter,
                        lineHalfThicknessLocal, surfaceOffsetLocal);

                    FillRadialLineVerts(_vertScratch, 4, rightDeg, center,
                        innerLocal, visualOuterLocal,
                        innerLocal, outerLocal, hInner, hOuter,
                        lineHalfThicknessLocal, surfaceOffsetLocal);

                    int slot = _poolUsed++;
                    _meshPool[slot].vertices = _vertScratch;
                    _meshPool[slot].RecalculateBounds();
                    Graphics.DrawMesh(_meshPool[slot], localToWorld, guideMaterial,
                        gameObject.layer, null, 0, _propBlock);
                }
            }
        }

        // Fills 4 vertices starting at baseVert for one thin radial line quad.
        //
        // The line spans radius [r0, r1] along angleDeg with tangential half-width halfThick.
        // Vertices are in pfRoot local XY space; the DrawMesh localToWorld matrix converts to world.
        //
        // dir_radial  = ( cos(angleDeg),  sin(angleDeg) )
        // dir_tangent = (-sin(angleDeg),  cos(angleDeg) )  — 90° CCW from radial
        //
        // Vertex layout:
        //   [baseVert+0] = r0, left tangent side,  Z at r0
        //   [baseVert+1] = r1, left tangent side,  Z at r1
        //   [baseVert+2] = r1, right tangent side, Z at r1
        //   [baseVert+3] = r0, right tangent side, Z at r0
        private static void FillRadialLineVerts(
            Vector3[] verts, int baseVert,
            float angleDeg, Vector2 center,
            float r0, float r1,
            float arenaInnerLocal, float arenaOuterLocal,
            float hInner, float hOuter,
            float halfThick, float zOffset = 0f)
        {
            float rad  = angleDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(rad);
            float sinA = Mathf.Sin(rad);

            // Tangential direction perpendicular to radial (rotate radial 90° CCW).
            float tanX = -sinA;
            float tanY =  cosA;

            float z0 = NoteApproachMath.FrustumZAtRadius(
                r0, arenaInnerLocal, arenaOuterLocal, hInner, hOuter) + zOffset;
            float z1 = NoteApproachMath.FrustumZAtRadius(
                r1, arenaInnerLocal, arenaOuterLocal, hInner, hOuter) + zOffset;

            // Radial center positions at r0 and r1.
            float cx0 = center.x + cosA * r0;
            float cy0 = center.y + sinA * r0;
            float cx1 = center.x + cosA * r1;
            float cy1 = center.y + sinA * r1;

            verts[baseVert + 0] = new Vector3(cx0 - tanX * halfThick, cy0 - tanY * halfThick, z0);
            verts[baseVert + 1] = new Vector3(cx1 - tanX * halfThick, cy1 - tanY * halfThick, z1);
            verts[baseVert + 2] = new Vector3(cx1 + tanX * halfThick, cy1 + tanY * halfThick, z1);
            verts[baseVert + 3] = new Vector3(cx0 + tanX * halfThick, cy0 + tanY * halfThick, z0);
        }

        // -------------------------------------------------------------------
        // Frustum height helpers
        // -------------------------------------------------------------------

        private float ReadFrustumHeightInner() =>
            (frustumProfile != null && frustumProfile.UseFrustumProfile)
                ? frustumProfile.FrustumHeightInner : 0.001f;

        private float ReadFrustumHeightOuter() =>
            (frustumProfile != null && frustumProfile.UseFrustumProfile)
                ? frustumProfile.FrustumHeightOuter : 0.001f;
    }
}
