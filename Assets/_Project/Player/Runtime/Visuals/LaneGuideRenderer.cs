// LaneGuideRenderer.cs
// Production lane guide renderer (spec §5.6 lane visuals).
//
// Draws exactly two thin 3D edge rails per logical lane via Graphics.DrawMesh:
//   – left rail   at the lane's true left logical boundary
//   – right rail  at the lane's true right logical boundary
//
// The center guide is intentionally omitted.
//
// ── 3D rail shape ─────────────────────────────────────────────────────────────
//
//   Each rail is a low-poly open tube (no end caps) running radially along a
//   lane-boundary angle from innerLocal to visualOuterLocal, conforming to the
//   frustum cone surface.
//
//   Tube parameters (baked constants):
//     RailRadialSegs   = 3  →  4 rings along the rail
//     RailProfileSides = 4  →  square cross-section (diamond orientation)
//
//   Per-rail geometry:
//     Verts   = RailRingCount * RailProfileSides = 4 × 4 = 16
//     Indices = RailRadialSegs * RailProfileSides * 2 * 3 = 3 × 4 × 6 = 72
//
//   Per-lane mesh (2 rails):
//     Verts   = 2 × 16 = 32
//     Indices = 2 × 72 = 144
//
// ── Tube frame construction ───────────────────────────────────────────────────
//
//   The tube cross-section is oriented in the plane perpendicular to the rail axis:
//
//     T (axis tangent)   = (cosA, sinA, slopeZ)  — radial direction + cone slope
//     N (tangential)     = (−sinA, cosA, 0)      — always ⊥ to T in XY
//     B (binormal)       = normalize(T_unnorm × N)
//                        = normalize(−slopeZ·cosA, −slopeZ·sinA, 1)
//                        ≈ (0, 0, 1) for a flat arena
//
//   Ring vertex p:  centre_at_r + railRadius × (cos(φ_p)·N + sin(φ_p)·B)
//     where φ_p = p × 2π / RailProfileSides
//
//   The slopeZ of the frustum cone is computed once per arena:
//     slopeZ = (hOuter − hInner) / (outerLocal − innerLocal)
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
//     LaneGuideRenderer:    one mesh         (two rails at real left/right only)
//
// ── Interval source ───────────────────────────────────────────────────────────
//
//   Rail edge angles are sourced from ArenaOccupancyEvaluator.TryGetLaneGuideBoundaries()
//   — which recovers the two logical boundaries from the same seam-aware data as
//   LaneSurfaceRenderer.  This guarantees rail edges land on the actual lane
//   boundaries even for rotating arenas and seam-crossing lanes.
//
// ── Loop structure ────────────────────────────────────────────────────────────
//
//   Mirrors LaneSurfaceRenderer outer structure:
//     Outer loop: arenas — calls ArenaOccupancyEvaluator.Compute() once per arena.
//     Inner loop: evaluator lanes filtered to this arena — one mesh per logical lane
//                 (exactly two rails per lane, regardless of seam-split state).
//
// ── Z layering ────────────────────────────────────────────────────────────────
//
//   Visual layering from bottom (+Z = toward camera):
//     Arena surface    — FrustumZAtRadius (base)
//     Lane surface     — FrustumZAtRadius + 0.005  (LaneSurfaceRenderer.liftLocal)
//     Lane rails       — FrustumZAtRadius + 0.008  (surfaceOffsetLocal default)
//     Notes            — FrustumZAtRadius + 0.010  (NoteLayerZLift)
//
//   surfaceOffsetLocal must remain greater than LaneSurfaceRenderer.liftLocal (0.005)
//   so that rails are always visible above the lane surface body.
//
// ── Rendering pattern ─────────────────────────────────────────────────────────
//
//   Identical to LaneSurfaceRenderer and ArenaSurfaceRenderer:
//     – pre-allocated Mesh pool, vertices written in-place every LateUpdate
//     – Graphics.DrawMesh — works in Game view without Gizmos, no child GOs required
//
// No dependency on PlayerDebugRenderer or PlayerDebugArenaSurface.
//
// Wiring:
//   1. Attach to any GO in the Player scene.
//   2. Assign playerAppController, guideMaterial, frustumProfile in the Inspector.
//   3. guideMaterial should use an unlit shader with _Color support; two-sided
//      rendering is recommended since the tube interior may be visible at grazing angles.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production lane guide renderer.  Draws left-edge and right-edge thin 3D
    /// tube rails for each visible logical lane (seam-aware, arena-clamped).
    ///
    /// <para>Rail boundary angles are sourced from <see cref="ArenaOccupancyEvaluator"/> —
    /// the same intervals used by <see cref="LaneSurfaceRenderer"/> — so rail edges
    /// are always aligned with the drawn lane surface edges.</para>
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

        [Tooltip("Material for the lane guide rails.  Use an unlit shader with _Color support.\n" +
                 "Two-sided rendering is recommended to avoid interior-face artefacts.")]
        [SerializeField] private Material guideMaterial;

        [Header("Frustum Surface Alignment")]
        [Tooltip("Shared frustum profile.  Assign the same profile used by the production note renderers.")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        [Header("Appearance")]
        [Tooltip("Color applied to all lane guide rails via MaterialPropertyBlock._Color.")]
        [SerializeField] private Color guideColor = new Color(1.0f, 0.75f, 0.3f, 0.8f);

        [Tooltip("Radius of the tube cross-section in PlayfieldLocal units.\n\n" +
                 "Controls how thick the 3D rail appears.  Smaller values = finer rail.\n" +
                 "Default: 0.003")]
        [Min(0.0001f)]
        [SerializeField] private float railRadiusLocal = 0.003f;

        [Tooltip("Local Z offset added to every rail vertex centre (PlayfieldLocal units).\n\n" +
                 "This lifts rails above the lane surface body so they remain visible.\n\n" +
                 "Z layering (bottom → top):\n" +
                 "  Lane surface  =  FrustumZAtRadius + 0.005  (LaneSurfaceRenderer.liftLocal)\n" +
                 "  Lane rails    =  FrustumZAtRadius + surfaceOffsetLocal  ← this field\n" +
                 "  Notes         =  FrustumZAtRadius + 0.010\n\n" +
                 "MUST remain above LaneSurfaceRenderer.liftLocal (0.005) or rails will be\n" +
                 "occluded by the lane body.  Keep below 0.010 to stay under notes.\n" +
                 "Default: 0.008")]
        [Min(0f)]
        [SerializeField] private float surfaceOffsetLocal = 0.008f;

        // -------------------------------------------------------------------
        // Tube geometry constants
        // -------------------------------------------------------------------

        // Two rails per logical lane: left boundary + right boundary.
        private const int RailsPerLane = 2;

        // Rail tube shape: 4-ring open tube with a square cross-section.
        // Changing either value requires rebuilding all pool meshes (Awake only).
        private const int RailRadialSegs   = 3;  // segments along rail length  → 4 rings
        private const int RailProfileSides = 4;  // sides around cross-section  → square

        // Derived counts (constant at runtime after Awake).
        private const int RailRingCount   = RailRadialSegs + 1;                        // 4
        private const int VertsPerRail    = RailRingCount * RailProfileSides;          // 16
        private const int IndicesPerRail  = RailRadialSegs * RailProfileSides * 2 * 3; // 72
        private const int VertsPerLane    = RailsPerLane * VertsPerRail;               // 32
        private const int IndicesPerLane  = RailsPerLane * IndicesPerRail;             // 144

        // -------------------------------------------------------------------
        // Pool and scratch
        // -------------------------------------------------------------------

        // Maximum number of guide meshes drawn per frame across all arenas.
        // One mesh per logical lane (two rails packed together per mesh).
        private const int MaxLanePool = 64;

        private Mesh[]    _meshPool;
        private int       _poolUsed;

        // Vertex scratch array: 2 rails × 16 verts = 32 entries.  Written each LateUpdate.
        private Vector3[] _vertScratch;

        private MaterialPropertyBlock _propBlock;

        // Shared occupancy evaluator — provides seam-aware, arena-clamped lane intervals.
        private ArenaOccupancyEvaluator _occupancy;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            _vertScratch = new Vector3[VertsPerLane];

            // ── Triangle index pattern ─────────────────────────────────────────────
            //
            // Side faces of each open tube rail:
            //   For each radial segment s (ring s → ring s+1):
            //     For each side p (around profile):
            //       Quad = {v00, v01, v11, v10} where vNext = (p+1) % RailProfileSides
            //       Two CCW tris (outward normal): v00,v01,v11  and  v00,v11,v10
            //
            // Rail 0 uses verts [0..15]; Rail 1 uses verts [16..31].
            var triPattern = new int[IndicesPerLane];
            int t = 0;
            for (int rail = 0; rail < RailsPerLane; rail++)
            {
                int vBase = rail * VertsPerRail;
                for (int s = 0; s < RailRadialSegs; s++)
                {
                    for (int p = 0; p < RailProfileSides; p++)
                    {
                        int vNext = (p + 1) % RailProfileSides;
                        int v00   = vBase + s * RailProfileSides + p;
                        int v01   = vBase + s * RailProfileSides + vNext;
                        int v10   = vBase + (s + 1) * RailProfileSides + p;
                        int v11   = vBase + (s + 1) * RailProfileSides + vNext;

                        // Outward-facing CCW winding (normal points away from tube axis).
                        triPattern[t++] = v00;
                        triPattern[t++] = v01;
                        triPattern[t++] = v11;

                        triPattern[t++] = v00;
                        triPattern[t++] = v11;
                        triPattern[t++] = v10;
                    }
                }
            }

            // ── Pre-allocate mesh pool ──────────────────────────────────────────────
            _meshPool = new Mesh[MaxLanePool];
            for (int i = 0; i < MaxLanePool; i++)
            {
                var m = new Mesh { name = "LaneGuideRail" };
                m.vertices  = new Vector3[VertsPerLane];  // zero-filled placeholder
                m.triangles = triPattern;                  // Unity copies internally
                m.RecalculateBounds();
                _meshPool[i] = m;
            }

            _propBlock = new MaterialPropertyBlock();
            _propBlock.SetColor("_Color", guideColor);

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
            for (int arenaIdx = 0; arenaIdx < evaluator.ArenaCount; arenaIdx++)
            {
                if (_poolUsed >= MaxLanePool) { break; }

                EvaluatedArena arena = evaluator.GetArena(arenaIdx);
                if (string.IsNullOrEmpty(arena.ArenaId) || !arena.EnabledBool) { continue; }

                // ── Arena geometry in PlayfieldLocal units ─────────────────────────
                float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Rails extend to the visual outer edge, matching LaneSurfaceRenderer.
                float visualOuterLocal = outerLocal
                    + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;

                // Skip degenerate geometry.
                if (visualOuterLocal <= innerLocal || innerLocal < 0f) { continue; }

                Vector2 center = pfT.NormalizedToLocal(
                    new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // ── Frustum cone slope ─────────────────────────────────────────────
                //
                // The tube frame construction needs dZ/dr (the rate of change of the
                // frustum Z with respect to radius) so the tube cross-section ring is
                // perpendicular to the actual 3D rail axis rather than to the XY plane.
                //
                // slopeZ = (hOuter − hInner) / (outerLocal − innerLocal)
                //
                // When frustumProfile is absent or disabled, hInner ≈ hOuter ≈ 0,
                // so slopeZ ≈ 0 (flat arena) and the frame degenerates to N=(tangential)
                // and B=(0,0,1).
                float slopeZ = (outerLocal > innerLocal)
                    ? (hOuter - hInner) / (outerLocal - innerLocal)
                    : 0f;

                // ── Compute seam-aware lane intervals ──────────────────────────────
                if (!_occupancy.Compute(arena, evaluator)) { continue; }

                // ── Inner loop: authored lanes (identity-stable) ───────────────────
                //
                // Iterate by evaluator lane index so each authored lane always draws its
                // own rail regardless of whether crossing lanes have swapped sorted positions.
                for (int laneIdx = 0; laneIdx < evaluator.LaneCount; laneIdx++)
                {
                    if (_poolUsed >= MaxLanePool) { break; }

                    EvaluatedLane lane = evaluator.GetLane(laneIdx);

                    if (string.IsNullOrEmpty(lane.LaneId) || !lane.EnabledBool) { continue; }
                    if (lane.ArenaId != arena.ArenaId) { continue; }

                    // ── Look up logical rail boundary angles ───────────────────────
                    //
                    // Returns exactly two boundary angles (left and right) for this
                    // logical lane regardless of seam-split state.  For a seam-split
                    // lane LaneSurfaceRenderer draws two body meshes but we draw only
                    // two rails — at the lane's actual left and right edges.
                    if (!_occupancy.TryGetLaneGuideBoundaries(
                        laneIdx, out float leftDeg, out float rightDeg)) { continue; }

                    // ── Fill rail 0 (left boundary) ───────────────────────────────
                    FillRailVerts(
                        _vertScratch, 0,
                        leftDeg, center,
                        innerLocal, visualOuterLocal,
                        innerLocal, outerLocal,
                        hInner, hOuter,
                        slopeZ, railRadiusLocal, surfaceOffsetLocal);

                    // ── Fill rail 1 (right boundary) ──────────────────────────────
                    FillRailVerts(
                        _vertScratch, VertsPerRail,
                        rightDeg, center,
                        innerLocal, visualOuterLocal,
                        innerLocal, outerLocal,
                        hInner, hOuter,
                        slopeZ, railRadiusLocal, surfaceOffsetLocal);

                    int slot = _poolUsed++;
                    _meshPool[slot].vertices = _vertScratch;
                    _meshPool[slot].RecalculateBounds();
                    Graphics.DrawMesh(_meshPool[slot], localToWorld, guideMaterial,
                        gameObject.layer, null, 0, _propBlock);
                }
            }
        }

        // -------------------------------------------------------------------
        // Rail tube geometry filler
        // -------------------------------------------------------------------

        // Fills VertsPerRail vertices (= RailRingCount × RailProfileSides = 16) for
        // one open-tube rail running radially from r0 to r1 at angleDeg.
        //
        // Frame:
        //   The rail axis tangent in 3D is T = (cosA, sinA, slopeZ).
        //   N  = (−sinA, cosA, 0)           — tangential in XY, always ⊥ to T
        //   B  = normalize(T_unnorm × N)
        //      = normalize(−slopeZ·cosA, −slopeZ·sinA, 1)
        //
        // Ring p vertex:  centre_at_ring + railRadius × (cos(φ_p)·N + sin(φ_p)·B)
        //   φ_p = p × 2π / RailProfileSides
        //
        // Parameters:
        //   verts            — scratch array (length ≥ baseVert + VertsPerRail).
        //   baseVert         — first index to write into verts[].
        //   angleDeg         — radial angle of the lane boundary in degrees.
        //   center           — XY arena centre in PlayfieldLocal.
        //   r0, r1           — inner/outer rail radii (PlayfieldLocal).
        //   arenaInnerLocal,
        //   arenaOuterLocal  — reference radii for FrustumZAtRadius interpolation.
        //   hInner, hOuter   — frustum cone heights at arena inner/outer radii.
        //   slopeZ           — dZ/dr of frustum cone surface (pre-computed per arena).
        //   railRadius       — tube cross-section radius (PlayfieldLocal).
        //   zOffset          — base Z lift above the frustum cone surface.
        private static void FillRailVerts(
            Vector3[] verts, int baseVert,
            float angleDeg, Vector2 center,
            float r0, float r1,
            float arenaInnerLocal, float arenaOuterLocal,
            float hInner, float hOuter,
            float slopeZ,
            float railRadius, float zOffset)
        {
            float rad  = angleDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(rad);
            float sinA = Mathf.Sin(rad);

            // N: tangential direction in XY — always perpendicular to the radial axis.
            float nX = -sinA;
            float nY =  cosA;
            // nZ = 0 (implicit)

            // B: adjust for cone slope so the ring is perpendicular to the 3D rail axis.
            //   B_unnorm = T_unnorm × N = (−slopeZ·cosA, −slopeZ·sinA, cos²A + sin²A)
            //                           = (−slopeZ·cosA, −slopeZ·sinA, 1)
            float bXu = -slopeZ * cosA;
            float bYu = -slopeZ * sinA;
            // bZu = 1 (implicit)
            float bLen = Mathf.Sqrt(bXu * bXu + bYu * bYu + 1f);
            float bX =  bXu / bLen;
            float bY =  bYu / bLen;
            float bZ =  1f  / bLen;

            // Pre-compute cosine/sine for each profile step.
            // phi_p = p × 2π / RailProfileSides
            for (int ring = 0; ring <= RailRadialSegs; ring++)
            {
                float tParam = (float)ring / RailRadialSegs;
                float r      = Mathf.Lerp(r0, r1, tParam);

                // Centre of this ring on the frustum cone surface.
                float cx = center.x + cosA * r;
                float cy = center.y + sinA * r;
                float cz = NoteApproachMath.FrustumZAtRadius(
                    r, arenaInnerLocal, arenaOuterLocal, hInner, hOuter) + zOffset;

                // Generate RailProfileSides vertices evenly around the cross-section.
                for (int p = 0; p < RailProfileSides; p++)
                {
                    float phi  = p * (2f * Mathf.PI / RailProfileSides);
                    float cPhi = Mathf.Cos(phi);
                    float sPhi = Mathf.Sin(phi);

                    // Offset from tube centre in 3D: cPhi·N + sPhi·B_norm
                    // N.z = 0 is elided.
                    float ox = railRadius * (cPhi * nX + sPhi * bX);
                    float oy = railRadius * (cPhi * nY + sPhi * bY);
                    float oz = railRadius * (               sPhi * bZ);

                    int idx = baseVert + ring * RailProfileSides + p;
                    verts[idx] = new Vector3(cx + ox, cy + oy, cz + oz);
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
