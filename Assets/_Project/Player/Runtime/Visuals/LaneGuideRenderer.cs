// LaneGuideRenderer.cs
// Production lane guide renderer (spec §5.6 lane visuals).
//
// Draws three thin radial lines per active lane via Graphics.DrawMesh:
//   – left edge  at lane.CenterDeg − lane.WidthDeg × 0.5
//   – center     at lane.CenterDeg
//   – right edge at lane.CenterDeg + lane.WidthDeg × 0.5
//
// Each line spans innerLocal → visualOuterLocal along its angle, lifted onto the
// frustum cone surface using PlayfieldFrustumProfile.
//
// Rendering pattern is identical to JudgementRingRenderer and ArenaBandRenderer:
//   – pre-allocated Mesh pool, vertices written in-place every LateUpdate
//   – Graphics.DrawMesh — works in Game view without Gizmos, no child GOs required
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
    /// Production lane guide renderer.  Draws left-edge, center, and right-edge radial
    /// line strips for each active lane.
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

        [Tooltip("Local Z offset added to every guide line vertex to lift guides above the arena surface.\n\n" +
                 "Prevents Z-fighting with ArenaSurfaceRenderer layers when arena surface opacity is 1.\n" +
                 "Should be small (e.g. 0.003) to stay visually flush with the surface.\n" +
                 "Default: 0.003")]
        [Min(0f)]
        [SerializeField] private float surfaceOffsetLocal = 0.003f;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // One mesh per active lane slot.  Pre-allocated in Awake.
        private const int MaxLanePool = 64;

        // Each lane mesh = 3 quads (left edge + center + right edge).
        // 3 quads × 4 separate verts = 12 verts.
        // 3 quads × 2 tris × 3 indices = 18 tri indices.
        private const int QuadsPerLane = 3;
        private const int VertsPerLane = QuadsPerLane * 4;  // 12
        private const int TrisPerLane  = QuadsPerLane * 6;  // 18

        private Mesh[] _meshPool;
        private int    _poolUsed;

        // Vertex scratch: 3 quads × 4 verts.  Filled in-place each LateUpdate.
        private Vector3[] _vertScratch;

        private MaterialPropertyBlock _propBlock;

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

            for (int laneIdx = 0; laneIdx < evaluator.LaneCount; laneIdx++)
            {
                if (_poolUsed >= MaxLanePool) { break; }

                EvaluatedLane lane = evaluator.GetLane(laneIdx);
                if (string.IsNullOrEmpty(lane.LaneId) || !lane.EnabledBool) { continue; }

                // ── Find parent arena ─────────────────────────────────────────────
                // Linear scan: arena count is small (1-8 typical) so this is cheap.
                EvaluatedArena arena  = default;
                bool           found  = false;
                for (int a = 0; a < evaluator.ArenaCount; a++)
                {
                    EvaluatedArena candidate = evaluator.GetArena(a);
                    if (candidate.EnabledBool && candidate.ArenaId == lane.ArenaId)
                    {
                        arena = candidate;
                        found = true;
                        break;
                    }
                }
                if (!found) { continue; }

                // ── Arena-derived geometry ────────────────────────────────────────
                float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Guide lines extend to the visual outer edge, matching the arena outline.
                float visualOuterLocal = outerLocal
                    + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;

                Vector2 center = pfT.NormalizedToLocal(
                    new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // ── Lane edge angles ──────────────────────────────────────────────
                float halfWidth = lane.WidthDeg * 0.5f;
                float leftDeg   = lane.CenterDeg - halfWidth;
                float rightDeg  = lane.CenterDeg + halfWidth;

                // ── Fill 3 radial line quads into scratch ──────────────────────────
                // Quad 0: left edge
                FillRadialLineVerts(_vertScratch, 0, leftDeg, center,
                    innerLocal, visualOuterLocal,
                    innerLocal, outerLocal, hInner, hOuter,
                    lineHalfThicknessLocal, surfaceOffsetLocal);

                // Quad 1: center line
                FillRadialLineVerts(_vertScratch, 4, lane.CenterDeg, center,
                    innerLocal, visualOuterLocal,
                    innerLocal, outerLocal, hInner, hOuter,
                    lineHalfThicknessLocal, surfaceOffsetLocal);

                // Quad 2: right edge
                FillRadialLineVerts(_vertScratch, 8, rightDeg, center,
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
