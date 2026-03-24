// LaneSurfaceRenderer.cs
// Persistent lane surface renderer — draws one full-width annular sector per enabled lane.
//
// ── What this renderer does ───────────────────────────────────────────────────
//
//   Per active arena each LateUpdate:
//     • Reads evaluated geometry from ChartRuntimeEvaluator (per-frame, not static).
//     • Calls ArenaOccupancyEvaluator.Compute() to get the current visible lane
//       intervals for this arena — the same clamped intervals ArenaSurfaceRenderer
//       uses when subtracting lanes from the arena fill.
//     • For each raw lane interval from the occupancy evaluator:
//           – Builds a filled sector mesh spanning the clamped lane angular extent.
//           – Angular: laneInterval.StartDeg → laneInterval.EndDeg
//           – Radial:  innerLocal → visualOuterLocal
//     • Subdivides radially into radialSegments rings so the mesh conforms to the
//       frustum cone surface — each ring row is placed at its own FrustumZAtRadius.
//     • Draws via Graphics.DrawMesh with an inline-configured MaterialPropertyBlock.
//
// ── Shared occupancy model ────────────────────────────────────────────────────
//
//   ArenaOccupancyEvaluator (Shared) is the single source of truth for the current
//   visible lane intervals (spec §5.5.3).  LaneSurfaceRenderer reads the raw
//   (pre-merge) lane intervals — GetLaneInterval(i) — rather than the merged
//   occupied union, so each authored lane still draws its own surface independently.
//   Overlapping lanes therefore both render; ArenaSurfaceRenderer subtracts their
//   union when computing fill intervals, keeping the two renderers consistent.
//
//   Using ArenaOccupancyEvaluator.GetLaneInterval() guarantees that the clamped
//   angular interval drawn here is byte-for-byte identical to the interval
//   ArenaSurfaceRenderer uses when deciding where NOT to draw the arena fill.
//   This eliminates any possible seam between lane surface edges and arena fill edges.
//
// ── Loop structure ────────────────────────────────────────────────────────────
//
//   Outer loop: arenas — provides arena geometry context.
//   Inner loop: lane intervals from ArenaOccupancyEvaluator — one mesh per interval.
//
//   This is arena-first rather than the previous lane-first approach.  The set of
//   drawn meshes is identical: one mesh per currently enabled, non-degenerate lane
//   interval per arena.  Draw order is now by angular position within each arena
//   (ArenaOccupancyEvaluator sorts intervals by left edge) rather than by evaluator
//   lane index.  Since all lanes share the same material and tint, this is not
//   visually observable.
//
// ── Lane visual bounding ──────────────────────────────────────────────────────
//
//   Lane angular intervals are clamped to the parent arena's span inside
//   ArenaOccupancyEvaluator.Compute().  The same clamp formula applies:
//
//     arenaStart = arena.ArcStartDeg
//     arenaEnd   = arenaStart + Clamp(arena.ArcSweepDeg, 0, 360)
//     laneLeft   = Max(CenterDeg - WidthDeg/2, arenaStart)
//     laneRight  = Min(CenterDeg + WidthDeg/2, arenaEnd)
//
//   Intervals degenerate after clamping (zero or negative width) are discarded by
//   ArenaOccupancyEvaluator and are therefore never drawn.
//
//   Limitation: lanes that straddle the 0°/360° seam of a full-circle arena are
//   not handled (same limitation as ArenaOccupancyEvaluator and ArenaSurfaceRenderer;
//   deferred).
//
// ── What this renderer does NOT do ───────────────────────────────────────────
//
//   • Does NOT own the interval clamping math — that lives in ArenaOccupancyEvaluator.
//   • Does NOT respond to touch or hit events.  (LaneTouchFeedbackRenderer owns that.)
//   • Does NOT draw guide lines.  (LaneGuideRenderer owns that.)
//   • Does NOT modify LaneTouchFeedbackRenderer, LaneGuideRenderer, or ArenaSurfaceRenderer.
//   • Does NOT require a new skin ScriptableObject — all config is inline-serialized.
//
// ── Vertex layout per lane mesh ───────────────────────────────────────────────
//
//   Columns = arcSegments + 1  (angular, left → right, across clamped span)
//   Rows    = radialSegments + 1  (radial, inner → outer)
//   Index:    verts[row * (arcSegments + 1) + col]
//
//   UV:  u = col / arcSegments  (0 = left edge, 1 = right edge of clamped span)
//        v = row / radialSegments  (0 = innerLocal, 1 = visualOuterLocal)
//
//   Z:   FrustumZAtRadius(radius_at_row, arenaInner, arenaOuter, hInner, hOuter) + liftLocal
//        Each row samples the cone height for its radius — correct surface conformance.
//
// ── Triangle winding ──────────────────────────────────────────────────────────
//
//   Two CCW tris per quad (normal faces +Z / toward camera):
//     v00 → v10 → v11,  v00 → v11 → v01
//   where v00 = (row r, col c), v10 = (row r+1, col c), etc.
//
// ── Rendering pattern ─────────────────────────────────────────────────────────
//
//   Identical to LaneGuideRenderer and ArenaSurfaceRenderer:
//     – pre-allocated Mesh pool (MaxLanePool slots), vertices written in-place
//       once per lane per LateUpdate — zero per-frame GC allocation after Awake.
//     – Graphics.DrawMesh — works in Game view without Gizmos; no child GOs.
//     – arcSegments and radialSegments must not be changed at runtime after Awake.
//
// ── Wiring ────────────────────────────────────────────────────────────────────
//
//   1. Attach to any GO in the Player scene.
//   2. Assign playerAppController, laneMaterial, frustumProfile in the Inspector.
//   3. Use an unlit, alpha-blended shader that supports _Color on laneMaterial.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Persistent lane surface renderer.  Draws one full-width annular sector per
    /// currently visible lane interval, conforming to the arena frustum cone surface.
    ///
    /// <para>Lane visible intervals are sourced from <see cref="ArenaOccupancyEvaluator"/>
    /// (Shared) — the same source <see cref="ArenaSurfaceRenderer"/> uses for fill
    /// subtraction (spec §5.5.3).  This guarantees pixel-perfect alignment between lane
    /// surface edges and arena fill edges at all times.</para>
    ///
    /// <para>Attach to any GO in the Player scene.  Assign <see cref="playerAppController"/>,
    /// <see cref="laneMaterial"/>, and <see cref="frustumProfile"/> in the Inspector.</para>
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/LaneSurfaceRenderer")]
    public class LaneSurfaceRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("PlayerAppController providing evaluated lane and arena geometry.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Material for the lane surface.  Use an unlit, alpha-blended shader with _Color support.")]
        [SerializeField] private Material laneMaterial;

        [Header("Frustum Surface Alignment")]
        [Tooltip("Shared frustum profile.  Assign the same profile used by the production note renderers.")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        [Header("Appearance")]
        [Tooltip("Base tint color for all lane surfaces.  Alpha controls overall opacity.")]
        [SerializeField] private Color laneTint = new Color(0.4f, 0.6f, 1.0f, 0.25f);

        [Tooltip("Local Z offset above the arena cone surface (PlayfieldLocal units).\n\n" +
                 "Each row vertex is placed at FrustumZAtRadius(radius) + liftLocal, so the\n" +
                 "entire sector floats uniformly above the cone surface.\n\n" +
                 "Small values keep the sector flush with the surface (recommended: 0.005–0.01).\n" +
                 "0 = flush with cone (may Z-fight ArenaSurfaceRenderer layers).\n" +
                 "Default: 0.005")]
        [Min(0f)]
        [SerializeField] private float liftLocal = 0.005f;

        [Header("Mesh Quality")]
        [Tooltip("Number of angular segments across the lane width.  More segments give smoother arc edges.\n" +
                 "Must not be changed at runtime after Awake.\n" +
                 "Default: 16")]
        [Min(1)]
        [SerializeField] private int arcSegments = 16;

        [Tooltip("Number of radial ring subdivisions across the lane depth.  More segments improve\n" +
                 "surface conformance on the frustum cone slope.\n" +
                 "Must not be changed at runtime after Awake.\n" +
                 "Default: 4")]
        [Min(1)]
        [SerializeField] private int radialSegments = 4;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // Maximum number of lane surface meshes drawn per frame across all arenas.
        // Also used as the lane capacity for the shared occupancy evaluator — 64 is
        // far above any realistic lane count in a single chart.
        private const int MaxLanePool = 64;

        // Computed in Awake from arcSegments / radialSegments.
        private int _vertsPerLane;  // (arcSegments+1) * (radialSegments+1)
        private int _trisPerLane;   // arcSegments * radialSegments * 6  (indices)

        private Mesh[] _meshPool;
        private int    _poolUsed;

        // Scratch arrays — allocated once in Awake, reused every frame (no GC).
        private Vector3[] _vertScratch;
        private Vector2[] _uvScratch;

        private MaterialPropertyBlock _propBlock;

        // Shared occupancy evaluator — single source of truth for visible lane intervals.
        // Created in Awake; Compute() is called once per arena per LateUpdate frame.
        // Results (lane intervals) are read immediately after each Compute() call.
        private ArenaOccupancyEvaluator _occupancy;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // Clamp in case Inspector values are invalid.
            arcSegments    = Mathf.Max(1, arcSegments);
            radialSegments = Mathf.Max(1, radialSegments);

            int numCols = arcSegments    + 1;
            int numRows = radialSegments + 1;

            _vertsPerLane = numCols * numRows;
            _trisPerLane  = arcSegments * radialSegments * 6;

            _vertScratch = new Vector3[_vertsPerLane];
            _uvScratch   = new Vector2[_vertsPerLane];

            // ── Shared occupancy evaluator ─────────────────────────────────────────
            // MaxLanePool sets the per-arena lane capacity.  ArenaOccupancyEvaluator
            // silently ignores lanes beyond this count — same limit as the mesh pool.
            _occupancy = new ArenaOccupancyEvaluator(MaxLanePool);

            // ── Triangle indices — same pattern for every lane mesh, built once ────
            //
            // For quad at (row r, col c):
            //   v00 = r       * numCols + c     (inner-left)
            //   v10 = (r + 1) * numCols + c     (outer-left)
            //   v11 = (r + 1) * numCols + c + 1 (outer-right)
            //   v01 = r       * numCols + c + 1 (inner-right)
            //
            // CCW from +Z:  v00 → v10 → v11,  v00 → v11 → v01
            var triIndices = new int[_trisPerLane];
            int t = 0;
            for (int r = 0; r < radialSegments; r++)
            {
                for (int c = 0; c < arcSegments; c++)
                {
                    int v00 =  r      * numCols + c;
                    int v10 = (r + 1) * numCols + c;
                    int v11 = (r + 1) * numCols + c + 1;
                    int v01 =  r      * numCols + c + 1;

                    triIndices[t++] = v00;
                    triIndices[t++] = v10;
                    triIndices[t++] = v11;

                    triIndices[t++] = v00;
                    triIndices[t++] = v11;
                    triIndices[t++] = v01;
                }
            }

            // ── Pre-allocate mesh pool ─────────────────────────────────────────────
            _meshPool = new Mesh[MaxLanePool];
            for (int i = 0; i < MaxLanePool; i++)
            {
                var m = new Mesh { name = "LaneSurface" };
                m.vertices  = new Vector3[_vertsPerLane]; // zero-filled placeholder
                m.uv        = new Vector2[_vertsPerLane]; // zero-filled placeholder
                m.triangles = triIndices;                 // Unity copies internally
                m.RecalculateBounds();
                _meshPool[i] = m;
            }

            _propBlock = new MaterialPropertyBlock();
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
            if (playerAppController == null || laneMaterial == null) { return; }

            // evaluator.Evaluate(timeMs) is called in PlayerAppController.Update()
            // before LateUpdate runs, so all evaluated arena/lane geometry is current.
            ChartRuntimeEvaluator evaluator = playerAppController.Evaluator;
            PlayfieldTransform    pfT       = playerAppController.PlayfieldTf;
            Transform             pfRoot    = playerAppController.playfieldRoot;

            if (evaluator == null || pfT == null || pfRoot == null) { return; }

            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            float hInner = ReadFrustumHeightInner();
            float hOuter = ReadFrustumHeightOuter();

            // Set the tint color once — all lanes share the same appearance.
            _propBlock.SetColor("_Color", laneTint);

            _poolUsed = 0;

            // ── Outer loop: arenas ────────────────────────────────────────────────────
            //
            // We iterate arenas rather than lanes so that ArenaOccupancyEvaluator.Compute()
            // can be called once per arena and its results consumed immediately.
            // This avoids per-lane arena lookups and keeps the interval math in one place.
            for (int arenaIdx = 0; arenaIdx < evaluator.ArenaCount; arenaIdx++)
            {
                // Stop all rendering if the mesh pool is exhausted.
                if (_poolUsed >= MaxLanePool) { break; }

                EvaluatedArena arena = evaluator.GetArena(arenaIdx);
                if (string.IsNullOrEmpty(arena.ArenaId) || !arena.EnabledBool) { continue; }

                // ── Arena geometry in PlayfieldLocal units ─────────────────────────────
                float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Extend to the visual outer edge, consistent with LaneGuideRenderer
                // and the note renderers.
                float visualOuterLocal = outerLocal
                    + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;

                // Skip degenerate geometry that would produce zero-area triangles.
                if (visualOuterLocal <= innerLocal || innerLocal < 0f) { continue; }

                Vector2 center = pfT.NormalizedToLocal(
                    new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // ── Compute visible lane intervals via shared occupancy evaluator ────────
                //
                // ArenaOccupancyEvaluator.Compute() collects all currently enabled lanes
                // for this arena, clamps each to the arena's angular span, and stores the
                // result in its raw lane interval array (sorted by left edge).
                //
                // These are exactly the intervals ArenaSurfaceRenderer uses when computing
                // fill gaps — so lane surface edges and fill gaps are guaranteed to align.
                //
                // Disabled arenas and degenerate sweeps return false; both are already
                // guarded above, so Compute() always returns true at this point.
                if (!_occupancy.Compute(arena, evaluator)) { continue; }

                // ── Inner loop: lane intervals ─────────────────────────────────────────
                //
                // ArenaOccupancyEvaluator.GetLaneInterval(i) returns the i-th raw (pre-merge)
                // lane interval — one per enabled lane that survived clamping.  Using raw
                // intervals (not the merged occupied union) means each authored lane draws
                // its own sector, so overlapping lanes both render independently.
                for (int i = 0; i < _occupancy.LaneIntervalCount; i++)
                {
                    // Stop this arena's lanes if the pool is exhausted mid-arena.
                    if (_poolUsed >= MaxLanePool) { break; }

                    AngularInterval laneInterval = _occupancy.GetLaneInterval(i);

                    // ── Fill mesh vertices and UVs ─────────────────────────────────────
                    // laneInterval.StartDeg and EndDeg are already clamped to the arena span
                    // by ArenaOccupancyEvaluator — no further clamping needed here.
                    FillLaneSectorVerts(
                        _vertScratch, _uvScratch,
                        laneInterval.StartDeg, laneInterval.EndDeg,
                        center,
                        innerLocal, visualOuterLocal,
                        innerLocal, outerLocal,
                        hInner, hOuter,
                        liftLocal,
                        arcSegments, radialSegments);

                    // ── Upload and draw ────────────────────────────────────────────────
                    int slot = _poolUsed++;
                    _meshPool[slot].vertices = _vertScratch;
                    _meshPool[slot].uv       = _uvScratch;
                    _meshPool[slot].RecalculateBounds();
                    Graphics.DrawMesh(_meshPool[slot], localToWorld, laneMaterial,
                        gameObject.layer, null, 0, _propBlock);
                }
            }
        }

        // -------------------------------------------------------------------
        // Vertex fill
        // -------------------------------------------------------------------

        // Fills vertices and UVs for one full-width annular sector.
        //
        // Grid: (arcSegs+1) columns × (radSegs+1) rows.
        // Each row is at a fixed radius, whose Z is sampled from FrustumZAtRadius.
        // Each column steps angularly from leftDeg to rightDeg.
        //
        // leftDeg and rightDeg are the clamped interval edges from ArenaOccupancyEvaluator
        // (AngularInterval.StartDeg / EndDeg).  No further clamping is needed.
        //
        // Parameters:
        //   leftDeg, rightDeg       — clamped angular span of the lane in degrees.
        //   center                  — XY center of the arena in PlayfieldLocal units.
        //   innerLocal              — inner arc radius (PlayfieldLocal).
        //   visualOuterLocal        — outer arc radius (PlayfieldLocal).
        //   arenaInnerLocal,
        //   arenaOuterLocal         — reference radii for FrustumZAtRadius interpolation.
        //   hInner, hOuter          — frustum cone heights at arena inner/outer radii.
        //   liftLocal               — Z offset added above the cone surface per vertex.
        //   arcSegs, radSegs        — mesh subdivision counts.
        private static void FillLaneSectorVerts(
            Vector3[] verts, Vector2[] uvs,
            float leftDeg, float rightDeg,
            Vector2 center,
            float innerLocal, float visualOuterLocal,
            float arenaInnerLocal, float arenaOuterLocal,
            float hInner, float hOuter,
            float liftLocal,
            int arcSegs, int radSegs)
        {
            int numCols = arcSegs + 1;

            for (int row = 0; row <= radSegs; row++)
            {
                float vCoord = (float)row / radSegs;
                float r      = Mathf.Lerp(innerLocal, visualOuterLocal, vCoord);

                // Z is sampled from the frustum cone at this radius, then lifted.
                // FrustumZAtRadius uses Clamp01 internally — safe to pass
                // visualOuterLocal even when it is slightly beyond arenaOuterLocal.
                float z = NoteApproachMath.FrustumZAtRadius(
                    r, arenaInnerLocal, arenaOuterLocal, hInner, hOuter) + liftLocal;

                for (int col = 0; col <= arcSegs; col++)
                {
                    float uCoord   = (float)col / arcSegs;
                    float angleDeg = Mathf.Lerp(leftDeg, rightDeg, uCoord);
                    float angleRad = angleDeg * Mathf.Deg2Rad;
                    float cosA     = Mathf.Cos(angleRad);
                    float sinA     = Mathf.Sin(angleRad);

                    int idx = row * numCols + col;
                    verts[idx] = new Vector3(center.x + cosA * r, center.y + sinA * r, z);
                    uvs[idx]   = new Vector2(uCoord, vCoord);
                }
            }
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
