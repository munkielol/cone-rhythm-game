// JudgementRingRenderer.cs
// Always-on visual judgement ring rendered at judgementR per arena (spec §5.8).
//
// The judgement ring is a permanent gameplay landmark — it shows players exactly
// where notes land, serving as the timing reference.  It must be visible whenever
// an arena is enabled, regardless of whether notes are currently on screen.
//
// ══════════════════════════════════════════════════════════════════════
//  RING GEOMETRY
//
//   judgementR = outerLocal − JudgementInsetNorm × minDimLocal  (spec §5.8)
//
//   The ring is a thin arc strip at radius judgementR, spanning the arena's
//   arcStartDeg + arcSweepDeg.  Each arena gets a pre-allocated mesh:
//     – RingSegments (default 32) uniform arc steps
//     – Each step = one trapezoid quad: inner arc at (judgementR − halfThick)
//       and outer arc at (judgementR + halfThick)
//     – Vertices lifted to frustum Z at judgementR via NoteApproachMath
//
//   Pre-allocated mesh pool; vertices written in-place each LateUpdate.
//   Drawn via Graphics.DrawMesh — visible in Game view without Gizmos.
// ══════════════════════════════════════════════════════════════════════
//
// Spec §5.8.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Renders the always-on judgement arc per arena (spec §5.8).
    /// Draws a thin ring at <c>judgementR = outerLocal − JudgementInsetNorm × minDim</c>.
    ///
    /// <para>Attach to any GameObject in the Player scene.
    /// Assign <see cref="playerAppController"/> and a Material in the Inspector.
    /// No prefab edits required.</para>
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/JudgementRingRenderer")]
    public class JudgementRingRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector — Wiring
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("The PlayerAppController this renderer reads arena geometry from.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Material for the judgement ring.  Use an unlit shader with _Color support.")]
        [SerializeField] private Material ringMaterial;

        // -------------------------------------------------------------------
        // Inspector — Ring appearance
        // -------------------------------------------------------------------

        [Header("Ring Appearance")]
        [Tooltip("Color of the judgement ring arc.")]
        [SerializeField] private Color ringColor = new Color(0.85f, 0.75f, 1.0f, 0.9f);

        [Tooltip("Radial half-thickness of the ring strip in PlayfieldLocal units.  " +
                 "The ring spans [judgementR − half, judgementR + half] radially.  Default: 0.008.")]
        [SerializeField] private float ringHalfThicknessLocal = 0.008f;

        // -------------------------------------------------------------------
        // Inspector — Geometry resolution
        // -------------------------------------------------------------------

        [Header("Geometry")]
        [Tooltip("Number of arc segments per full 360° ring.  More = smoother ring.  Default: 32.")]
        [SerializeField] private int ringSegments = 32;

        // -------------------------------------------------------------------
        // Inspector — Frustum surface alignment
        // -------------------------------------------------------------------

        [Header("Frustum Surface Alignment")]
        [Tooltip("Shared frustum profile. When assigned, frustum heights are read from this component " +
                 "so the ring sits on the same cone surface as all note renderers. " +
                 "If null, a flat Z at 0.002 is used.")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        // -------------------------------------------------------------------
        // Internals — mesh pool
        // -------------------------------------------------------------------

        // One ring mesh per arena slot.  Pre-allocated in Awake.
        private const int MaxArenaPool = 16;

        private Mesh[]  _meshPool;      // one mesh per active arena slot
        private int     _poolUsed;

        // Reused vertex scratch buffer for one ring mesh.
        // MaxVerts = 4 verts × MaxSegments (separate quads, no vertex sharing).
        private Vector3[] _vertScratch;
        private int[]     _triScratch;

        private MaterialPropertyBlock _propBlock;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // Clamp to a safe minimum.
            ringSegments = Mathf.Max(4, ringSegments);

            int vertsPerMesh = ringSegments * 4;
            int trisPerMesh  = ringSegments * 6;

            _meshPool    = new Mesh[MaxArenaPool];
            _vertScratch = new Vector3[vertsPerMesh];
            _triScratch  = new int[trisPerMesh];

            // Build triangle index pattern once — it never changes.
            for (int seg = 0; seg < ringSegments; seg++)
            {
                int v = seg * 4;
                int t = seg * 6;
                // Quad: [v] inner-start, [v+1] outer-start, [v+2] outer-end, [v+3] inner-end
                _triScratch[t + 0] = v;
                _triScratch[t + 1] = v + 1;
                _triScratch[t + 2] = v + 2;
                _triScratch[t + 3] = v;
                _triScratch[t + 4] = v + 2;
                _triScratch[t + 5] = v + 3;
            }

            for (int i = 0; i < MaxArenaPool; i++)
            {
                var m = new Mesh { name = "JudgementRing" };
                m.vertices  = new Vector3[vertsPerMesh];
                m.triangles = _triScratch;
                m.RecalculateBounds();
                _meshPool[i] = m;
            }

            _propBlock = new MaterialPropertyBlock();
            _propBlock.SetColor("_Color", ringColor);
        }

        private void OnDestroy()
        {
            if (_meshPool != null)
            {
                for (int i = 0; i < _meshPool.Length; i++)
                {
                    if (_meshPool[i] != null) { Destroy(_meshPool[i]); _meshPool[i] = null; }
                }
            }
        }

        private void LateUpdate()
        {
            if (playerAppController == null || ringMaterial == null || _meshPool == null)
            {
                return;
            }

            var evaluator = playerAppController.Evaluator;
            var pfTf      = playerAppController.PlayfieldTf;
            Transform pfRoot = playerAppController.playfieldRoot;

            if (evaluator == null || pfTf == null || pfRoot == null) { return; }

            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            float hInner = ReadFrustumHeightInner();
            float hOuter = ReadFrustumHeightOuter();

            _poolUsed = 0;

            // Iterate all arenas in the evaluator — those marked disabled are skipped.
            for (int arenaIdx = 0; arenaIdx < evaluator.ArenaCount; arenaIdx++)
            {
                if (_poolUsed >= MaxArenaPool) { break; }

                EvaluatedArena ea = evaluator.GetArena(arenaIdx);

                // Only draw the ring for enabled arenas (spec §5.6 / §5.8).
                if (!ea.EnabledBool) { continue; }
                if (string.IsNullOrEmpty(ea.ArenaId)) { continue; }

                // ── Arena radii (PlayfieldLocal units) ───────────────────────────────────
                float outerLocal = pfTf.NormRadiusToLocal(ea.OuterRadiusNorm);
                float bandLocal  = pfTf.NormRadiusToLocal(ea.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                float judgementR = NoteApproachMath.JudgementRadius(
                    outerLocal, pfTf.MinDimLocal, PlayerSettingsStore.JudgementInsetNorm);

                float ringInner = judgementR - ringHalfThicknessLocal;
                float ringOuter = judgementR + ringHalfThicknessLocal;

                // Clamp ring to band extents.
                ringInner = Mathf.Max(ringInner, innerLocal);
                ringOuter = Mathf.Min(ringOuter, outerLocal);

                if (ringOuter - ringInner < 0.0001f) { continue; }

                // ── Arc span ─────────────────────────────────────────────────────────────
                float arcStart = ea.ArcStartDeg;
                float arcSweep = Mathf.Clamp(ea.ArcSweepDeg, 0f, 360f);
                if (arcSweep < 0.1f) { continue; }

                // Actual number of segments for this arc (proportional to sweep).
                int segsUsed = Mathf.Max(1, Mathf.RoundToInt(ringSegments * arcSweep / 360f));

                // ── Arena center in PlayfieldLocal units ──────────────────────────────────
                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(ea.CenterXNorm, ea.CenterYNorm));

                // ── Frustum Z at judgement radius ─────────────────────────────────────────
                float ringZ = NoteApproachMath.FrustumZAtRadius(judgementR, innerLocal, outerLocal,
                    hInner, hOuter);
                float ringInnerZ = NoteApproachMath.FrustumZAtRadius(ringInner, innerLocal, outerLocal,
                    hInner, hOuter);
                float ringOuterZ = NoteApproachMath.FrustumZAtRadius(ringOuter, innerLocal, outerLocal,
                    hInner, hOuter);

                // ── Build ring mesh vertices ──────────────────────────────────────────────
                // Each segment is one quad with 4 separate vertices.
                // Quad layout: [inner-start, outer-start, outer-end, inner-end]
                float degPerSeg = arcSweep / segsUsed;

                for (int seg = 0; seg < segsUsed; seg++)
                {
                    float degA = arcStart + seg       * degPerSeg;
                    float degB = arcStart + (seg + 1) * degPerSeg;

                    float radA = degA * Mathf.Deg2Rad;
                    float radB = degB * Mathf.Deg2Rad;

                    float cosA = Mathf.Cos(radA);
                    float sinA = Mathf.Sin(radA);
                    float cosB = Mathf.Cos(radB);
                    float sinB = Mathf.Sin(radB);

                    int v = seg * 4;

                    // inner edge at angle A
                    _vertScratch[v + 0] = new Vector3(
                        ctr.x + ringInner * cosA,
                        ctr.y + ringInner * sinA,
                        ringInnerZ);

                    // outer edge at angle A
                    _vertScratch[v + 1] = new Vector3(
                        ctr.x + ringOuter * cosA,
                        ctr.y + ringOuter * sinA,
                        ringOuterZ);

                    // outer edge at angle B
                    _vertScratch[v + 2] = new Vector3(
                        ctr.x + ringOuter * cosB,
                        ctr.y + ringOuter * sinB,
                        ringOuterZ);

                    // inner edge at angle B
                    _vertScratch[v + 3] = new Vector3(
                        ctr.x + ringInner * cosB,
                        ctr.y + ringInner * sinB,
                        ringInnerZ);
                }

                // If fewer segments were used than the pre-allocated count, collapse the
                // remaining verts to zero so they don't pollute the draw.
                for (int seg = segsUsed; seg < ringSegments; seg++)
                {
                    int v = seg * 4;
                    _vertScratch[v + 0] =
                    _vertScratch[v + 1] =
                    _vertScratch[v + 2] =
                    _vertScratch[v + 3] = Vector3.zero;
                }

                Mesh mesh = _meshPool[_poolUsed++];
                mesh.vertices = _vertScratch;
                mesh.RecalculateBounds();

                Graphics.DrawMesh(mesh, localToWorld, ringMaterial,
                    gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Frustum height helpers
        // -------------------------------------------------------------------

        private float ReadFrustumHeightInner()
        {
            if (frustumProfile != null && frustumProfile.UseFrustumProfile) { return frustumProfile.FrustumHeightInner; }
            return 0.002f;
        }

        private float ReadFrustumHeightOuter()
        {
            if (frustumProfile != null && frustumProfile.UseFrustumProfile) { return frustumProfile.FrustumHeightOuter; }
            return 0.002f;
        }
    }
}
