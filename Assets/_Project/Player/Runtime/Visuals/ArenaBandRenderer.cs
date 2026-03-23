// ArenaBandRenderer.cs
// Production arena outline renderer (spec §5.5 / §5.6 arena visuals).
//
// Draws two thin arc strips per active arena via Graphics.DrawMesh:
//   – outer arc at visualOuterLocal  (outerLocal + VisualOuterExpandNorm)
//   – inner arc at innerLocal        (outerLocal − bandLocal)
//
// Both strips follow animated arena properties in real time.  No dependency on
// PlayerDebugRenderer or PlayerDebugArenaSurface.
//
// Rendering pattern is identical to JudgementRingRenderer:
//   – pre-allocated Mesh pool, vertices written in-place every LateUpdate
//   – Graphics.DrawMesh — works in Game view without Gizmos, no child GOs required
//   – MaterialPropertyBlock per arc type to allow different colors
//
// Wiring:
//   1. Attach to any GO in the Player scene.
//   2. Assign playerAppController, bandMaterial, frustumProfile in the Inspector.
//   3. No scene/prefab YAML edits required.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production arena outline renderer.  Draws outer + inner arc strips per active arena.
    ///
    /// <para>Attach to any GO in the Player scene.  Assign
    /// <see cref="playerAppController"/>, <see cref="bandMaterial"/>, and
    /// <see cref="frustumProfile"/> in the Inspector.</para>
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/ArenaBandRenderer")]
    public class ArenaBandRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("PlayerAppController providing evaluated arena geometry.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Material for both arc strips.  Use an unlit shader with _Color + _MainTex support " +
                 "(e.g. Sprites/Default or a custom unlit shader).")]
        [SerializeField] private Material bandMaterial;

        [Header("Frustum Surface Alignment")]
        [Tooltip("Shared frustum profile.  Assign the same profile used by the production note renderers " +
                 "so arc strips sit on the same cone surface.")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        [Header("Appearance")]
        [Tooltip("Color of the outer arc strip (visual outer boundary of the arena).")]
        [SerializeField] private Color outerArcColor = new Color(0.70f, 0.85f, 1.00f, 0.85f);

        [Tooltip("Color of the inner arc strip (inner boundary of the playable band).")]
        [SerializeField] private Color innerArcColor = new Color(0.50f, 0.65f, 0.90f, 0.55f);

        [Tooltip("Radial half-thickness of each arc strip in PlayfieldLocal units.  Default: 0.004.")]
        [SerializeField] private float arcHalfThicknessLocal = 0.004f;

        [Header("Geometry")]
        [Tooltip("Arc segments per arena.  More = smoother curves.  Default: 48.")]
        [SerializeField] private int arcSegments = 48;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // Two mesh pools, one per arc type (outer / inner).
        // One mesh per arena slot; vertices written in-place every LateUpdate.
        private const int MaxArenaPool = 16;

        private Mesh[] _outerMeshPool;
        private Mesh[] _innerMeshPool;
        private int    _poolUsed;

        // Shared vertex scratch — one arc strip's worth of vertices.
        // Reused first for the outer arc, then for the inner arc of each arena.
        // Size = arcSegments × 4 separate verts per segment quad.
        private Vector3[] _vertScratch;

        // Separate property blocks so the two arc types can have different colors.
        private MaterialPropertyBlock _outerPropBlock;
        private MaterialPropertyBlock _innerPropBlock;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            arcSegments = Mathf.Max(4, arcSegments);

            int vertsPerMesh = arcSegments * 4; // 4 separate verts per segment quad
            int trisPerMesh  = arcSegments * 6; // 2 triangles × 3 indices per quad

            _vertScratch = new Vector3[vertsPerMesh];

            // Triangle index pattern — same topology for every mesh, set once here.
            // Quad layout per segment:
            //   [v+0] = strip-inner edge at angle A   [v+1] = strip-outer edge at angle A
            //   [v+2] = strip-outer edge at angle B   [v+3] = strip-inner edge at angle B
            var triPattern = new int[trisPerMesh];
            for (int seg = 0; seg < arcSegments; seg++)
            {
                int v = seg * 4;
                int t = seg * 6;
                triPattern[t + 0] = v;
                triPattern[t + 1] = v + 1;
                triPattern[t + 2] = v + 2;
                triPattern[t + 3] = v;
                triPattern[t + 4] = v + 2;
                triPattern[t + 5] = v + 3;
            }

            _outerMeshPool = new Mesh[MaxArenaPool];
            _innerMeshPool = new Mesh[MaxArenaPool];

            for (int i = 0; i < MaxArenaPool; i++)
            {
                _outerMeshPool[i] = BuildPlaceholderMesh("ArenaBandOuter", vertsPerMesh, triPattern);
                _innerMeshPool[i] = BuildPlaceholderMesh("ArenaBandInner", vertsPerMesh, triPattern);
            }

            _outerPropBlock = new MaterialPropertyBlock();
            _outerPropBlock.SetColor("_Color", outerArcColor);

            _innerPropBlock = new MaterialPropertyBlock();
            _innerPropBlock.SetColor("_Color", innerArcColor);
        }

        private static Mesh BuildPlaceholderMesh(string meshName, int vertCount, int[] tris)
        {
            // Unity copies the tris array internally, so the same array can be reused here.
            var m = new Mesh { name = meshName };
            m.vertices  = new Vector3[vertCount]; // zero-filled; overwritten each LateUpdate
            m.triangles = tris;
            m.RecalculateBounds();
            return m;
        }

        private void OnDestroy()
        {
            DestroyMeshPool(_outerMeshPool);
            DestroyMeshPool(_innerMeshPool);
        }

        private static void DestroyMeshPool(Mesh[] pool)
        {
            if (pool == null) { return; }
            for (int i = 0; i < pool.Length; i++)
            {
                if (pool[i] != null) { Destroy(pool[i]); pool[i] = null; }
            }
        }

        // -------------------------------------------------------------------
        // Per-frame rendering
        // -------------------------------------------------------------------

        private void LateUpdate()
        {
            if (playerAppController == null || bandMaterial == null) { return; }

            ChartRuntimeEvaluator evaluator = playerAppController.Evaluator;
            PlayfieldTransform    pfT       = playerAppController.PlayfieldTf;
            Transform             pfRoot    = playerAppController.playfieldRoot;

            if (evaluator == null || pfT == null || pfRoot == null) { return; }

            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            float hInner = ReadFrustumHeightInner();
            float hOuter = ReadFrustumHeightOuter();

            _poolUsed = 0;

            for (int i = 0; i < evaluator.ArenaCount; i++)
            {
                if (_poolUsed >= MaxArenaPool) { break; }

                EvaluatedArena ea = evaluator.GetArena(i);
                if (string.IsNullOrEmpty(ea.ArenaId) || !ea.EnabledBool) { continue; }

                float arcSweep = Mathf.Clamp(ea.ArcSweepDeg, 0f, 360f);
                if (arcSweep < 0.1f) { continue; }

                // ── Arena radii in PlayfieldLocal units ───────────────────────────
                float outerLocal = pfT.NormRadiusToLocal(ea.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(ea.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Visual outer edge matches ArenaColliderProvider / PlayerDebugArenaSurface.
                float visualOuterLocal = outerLocal
                    + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;

                Vector2 center = pfT.NormalizedToLocal(new Vector2(ea.CenterXNorm, ea.CenterYNorm));

                // ── Pre-compute Z at each strip edge (constant across the full arc) ─
                // Outer arc strip: centred at visualOuterLocal.
                float outerStripInnerR = visualOuterLocal - arcHalfThicknessLocal;
                float outerStripOuterR = visualOuterLocal + arcHalfThicknessLocal;
                float zOuterI = NoteApproachMath.FrustumZAtRadius(
                    outerStripInnerR, innerLocal, outerLocal, hInner, hOuter);
                float zOuterO = NoteApproachMath.FrustumZAtRadius(
                    outerStripOuterR, innerLocal, outerLocal, hInner, hOuter);

                // Inner arc strip: centred at innerLocal.
                float innerStripInnerR = innerLocal - arcHalfThicknessLocal;
                float innerStripOuterR = innerLocal + arcHalfThicknessLocal;
                float zInnerI = NoteApproachMath.FrustumZAtRadius(
                    innerStripInnerR, innerLocal, outerLocal, hInner, hOuter);
                float zInnerO = NoteApproachMath.FrustumZAtRadius(
                    innerStripOuterR, innerLocal, outerLocal, hInner, hOuter);

                int slot = _poolUsed++;

                // ── Outer arc ─────────────────────────────────────────────────────
                FillArcStripVerts(_vertScratch, arcSegments,
                    ea.ArcStartDeg, arcSweep, center,
                    outerStripInnerR, outerStripOuterR, zOuterI, zOuterO);

                _outerMeshPool[slot].vertices = _vertScratch;
                _outerMeshPool[slot].RecalculateBounds();
                Graphics.DrawMesh(_outerMeshPool[slot], localToWorld, bandMaterial,
                    gameObject.layer, null, 0, _outerPropBlock);

                // ── Inner arc ─────────────────────────────────────────────────────
                FillArcStripVerts(_vertScratch, arcSegments,
                    ea.ArcStartDeg, arcSweep, center,
                    innerStripInnerR, innerStripOuterR, zInnerI, zInnerO);

                _innerMeshPool[slot].vertices = _vertScratch;
                _innerMeshPool[slot].RecalculateBounds();
                Graphics.DrawMesh(_innerMeshPool[slot], localToWorld, bandMaterial,
                    gameObject.layer, null, 0, _innerPropBlock);
            }
        }

        // Fills verts[] in-place for one arc strip (arcSegments quads, 4 separate verts each).
        //
        // All vertices are in pfRoot local XY space; the DrawMesh localToWorld matrix
        // converts them to world space.  This matches JudgementRingRenderer's convention.
        //
        // Strip spans angles [startDeg, startDeg + sweepDeg] at radii [stripInnerR, stripOuterR].
        // zStripInner / zStripOuter are pre-computed Z heights for the two radial edges.
        private static void FillArcStripVerts(
            Vector3[] verts, int segments,
            float startDeg, float sweepDeg, Vector2 center,
            float stripInnerR, float stripOuterR,
            float zStripInner, float zStripOuter)
        {
            float degPerSeg = sweepDeg / segments;

            for (int seg = 0; seg < segments; seg++)
            {
                float degA = startDeg + seg       * degPerSeg;
                float degB = startDeg + (seg + 1) * degPerSeg;
                float radA = degA * Mathf.Deg2Rad;
                float radB = degB * Mathf.Deg2Rad;
                float cosA = Mathf.Cos(radA);
                float sinA = Mathf.Sin(radA);
                float cosB = Mathf.Cos(radB);
                float sinB = Mathf.Sin(radB);

                int v = seg * 4;
                verts[v + 0] = new Vector3(
                    center.x + stripInnerR * cosA, center.y + stripInnerR * sinA, zStripInner);
                verts[v + 1] = new Vector3(
                    center.x + stripOuterR * cosA, center.y + stripOuterR * sinA, zStripOuter);
                verts[v + 2] = new Vector3(
                    center.x + stripOuterR * cosB, center.y + stripOuterR * sinB, zStripOuter);
                verts[v + 3] = new Vector3(
                    center.x + stripInnerR * cosB, center.y + stripInnerR * sinB, zStripInner);
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
