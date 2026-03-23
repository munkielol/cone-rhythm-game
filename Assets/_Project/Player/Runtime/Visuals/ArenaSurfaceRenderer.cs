// ArenaSurfaceRenderer.cs
// Production arena surface renderer (spec §5.0 / §5.8 production playfield visual components).
//
// Draws the filled annular sector for each active arena using Graphics.DrawMesh.
// Visual-only — owns no colliders, no debug responsibilities, no note rendering.
//
// ── What this renderer does ───────────────────────────────────────────────────
//
//   Per active arena each LateUpdate:
//     • Reads evaluated geometry from ChartRuntimeEvaluator.
//     • Builds a filled sector mesh spanning [innerLocal .. visualOuterLocal].
//     • Places vertices on the frustum cone surface using PlayfieldFrustumProfile.
//     • Draws using the base layer of ArenaSurfaceSkinSet (material, texture, tint,
//       opacity, UV tiling, UV scroll).
//
// ── What this renderer does NOT do ───────────────────────────────────────────
//
//   • Detail and accent layers are reserved for a later step.
//   • Does NOT own MeshColliders or affect input/hit-testing.
//     (That is ArenaColliderProvider's responsibility.)
//   • Does NOT depend on PlayerDebugArenaSurface or PlayerDebugRenderer.
//   • Does NOT render notes, holds, or feedback effects.
//
// ── Rendering pattern ─────────────────────────────────────────────────────────
//
//   Identical to JudgementRingRenderer and ArenaBandRenderer:
//     – pre-allocated Mesh pool (MaxArenaPool slots), vertices written in-place
//       each LateUpdate — zero per-frame GC allocation after Awake.
//     – Graphics.DrawMesh — works in Game view without Gizmos; no child GOs.
//     – Single MaterialPropertyBlock; updated each LateUpdate with current
//       tint, texture, UV scale, and UV scroll offset.
//
// ── Vertex layout ─────────────────────────────────────────────────────────────
//
//   Per mesh, N = arcSegments:
//     indices  0 .. N      → inner arc vertices at Z = zInner
//     indices N+1 .. 2N+1  → outer arc vertices at Z = zOuter
//
//   UVs (set once in Awake; tiling/scroll via _MainTex_ST):
//     inner arc:  u = i/N, v = 0
//     outer arc:  u = i/N, v = 1
//
// ── UV tiling and scroll ──────────────────────────────────────────────────────
//
//   Mesh UVs are normalized [0..1]. Actual tiling and scroll are driven by
//   _MainTex_ST (xy = scale, zw = offset) set on the MaterialPropertyBlock per
//   draw call. The shader must declare _MainTex_ST for tiling to take effect.
//   UV scroll accumulates _uvScrollOffset += uvScrollSpeed * deltaTime each frame.
//
// ── Frustum Z ─────────────────────────────────────────────────────────────────
//
//   Inner arc vertices are placed at PlayfieldFrustumProfile.FrustumHeightInner.
//   Outer arc vertices are placed at PlayfieldFrustumProfile.FrustumHeightOuter.
//   When frustumProfile is null or disabled, both default to 0.001 (flat surface).
//
// ── Wiring ────────────────────────────────────────────────────────────────────
//
//   1. Add this component to any GO in the Player scene.
//   2. Assign playerAppController, frustumProfile, and skinSet in the Inspector.
//   3. Ensure skinSet.baseLayer.material is assigned (Unlit/Transparent shader).
//   4. Optionally assign skinSet.baseLayer.texture for a textured surface.
//   5. This component coexists with ArenaBandRenderer, LaneGuideRenderer,
//      JudgementRingRenderer, ArenaColliderProvider — they are all independent.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production arena surface renderer.  Draws the filled annular sector for each
    /// active arena using the base layer of <see cref="ArenaSurfaceSkinSet"/>.
    ///
    /// <para>Attach to any GO in the Player scene.  Assign
    /// <see cref="playerAppController"/>, <see cref="frustumProfile"/>, and
    /// <see cref="skinSet"/> in the Inspector.</para>
    ///
    /// <para>Does not own colliders, debug overlays, or note rendering — purely visual.</para>
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/ArenaSurfaceRenderer")]
    public class ArenaSurfaceRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("PlayerAppController that provides ChartRuntimeEvaluator and PlayfieldTransform.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Shared frustum profile.  Assign the same PlayfieldFrustumProfile used by " +
                 "the other production renderers so the arena surface sits on the same cone.\n\n" +
                 "When null or UseFrustumProfile is false, inner and outer arcs both default " +
                 "to Z = 0.001 (flat surface — no cone tilt).")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        [Tooltip("Arena surface skin.  Controls the visual appearance of the filled arena sector.\n\n" +
                 "Only the base layer is rendered in this step.  Detail and accent layers are reserved.")]
        [SerializeField] private ArenaSurfaceSkinSet skinSet;

        [Header("Geometry")]
        [Tooltip("Arc segments per arena mesh.  More segments = smoother annular sector curves.\n\n" +
                 "Minimum: 3.  Default: 48.\n" +
                 "Note: changing this at runtime after Awake has no effect; the pool is fixed at Awake.")]
        [SerializeField] private int arcSegments = 48;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // One mesh per active arena slot.  Pre-allocated in Awake.
        private const int MaxArenaPool = 16;

        // Each arena mesh vertex layout:
        //   indices 0..N         → inner arc  (N+1 verts, Z = zInner)
        //   indices N+1..2N+1    → outer arc  (N+1 verts, Z = zOuter)
        //
        // Triangles: N quads × 2 tris × 3 indices = N*6 indices.

        private Mesh[] _meshPool;
        private int    _poolUsed;

        // Shared vertex scratch filled in-place per arena each LateUpdate.
        // Length = (arcSegments + 1) * 2.
        private Vector3[] _vertScratch;

        // MaterialPropertyBlock — reused across all draw calls; updated per LateUpdate.
        private MaterialPropertyBlock _propBlock;

        // Accumulated UV scroll offset (wraps to [0, 1) each frame to preserve precision).
        private Vector2 _uvScrollOffset;

        // Per-warning guards — prevent per-frame console spam on misconfiguration.
        private bool _hasWarnedMissingController;
        private bool _hasWarnedMissingSkinSet;
        private bool _hasWarnedMissingMaterial;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            arcSegments = Mathf.Max(3, arcSegments);

            int N         = arcSegments;
            int vertCount = (N + 1) * 2;
            int triCount  = N * 6;

            // Shared vertex scratch (overwritten per arena each frame, then copied into mesh).
            _vertScratch = new Vector3[vertCount];

            // ── Triangle index pattern ────────────────────────────────────────────────
            // Same topology for every pool mesh; Unity copies the array internally
            // at mesh.triangles assignment, so the same source array can be reused here.
            //
            // Per quad segment i (connecting arc sample i to arc sample i+1):
            //   inner[i]   = vertex i
            //   inner[i+1] = vertex i+1
            //   outer[i]   = vertex (N+1) + i
            //   outer[i+1] = vertex (N+1) + i+1
            //
            // Winding (CCW from +Z, matches ArenaBandRenderer / JudgementRingRenderer):
            //   tri 1: inner[i],  outer[i],  outer[i+1]
            //   tri 2: inner[i],  outer[i+1], inner[i+1]
            var triPattern = new int[triCount];
            for (int i = 0; i < N; i++)
            {
                int t = i * 6;
                triPattern[t + 0] = i;
                triPattern[t + 1] = N + 1 + i;
                triPattern[t + 2] = N + 1 + i + 1;
                triPattern[t + 3] = i;
                triPattern[t + 4] = N + 1 + i + 1;
                triPattern[t + 5] = i + 1;
            }

            // ── UV pattern ────────────────────────────────────────────────────────────
            // Normalized [0..1] UVs set once; actual tiling + scroll driven by _MainTex_ST.
            //   inner arc:  (u = i/N, v = 0)   — inner edge of the annular sector
            //   outer arc:  (u = i/N, v = 1)   — outer edge of the annular sector
            var uvs = new Vector2[vertCount];
            for (int i = 0; i <= N; i++)
            {
                float u = (float)i / N;
                uvs[i]         = new Vector2(u, 0f);
                uvs[N + 1 + i] = new Vector2(u, 1f);
            }

            // ── Mesh pool ─────────────────────────────────────────────────────────────
            _meshPool = new Mesh[MaxArenaPool];
            for (int slot = 0; slot < MaxArenaPool; slot++)
            {
                var m = new Mesh { name = "ArenaSurface" };
                m.vertices  = new Vector3[vertCount]; // zero-filled placeholder
                m.uv        = uvs;                    // static normalized UVs (Unity copies)
                m.triangles = triPattern;             // Unity copies internally
                m.RecalculateBounds();
                _meshPool[slot] = m;
            }

            _propBlock      = new MaterialPropertyBlock();
            _uvScrollOffset = Vector2.zero;
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
            // ── Guard: required references ────────────────────────────────────────────
            if (playerAppController == null)
            {
                if (!_hasWarnedMissingController)
                {
                    _hasWarnedMissingController = true;
                    Debug.LogWarning("[ArenaSurfaceRenderer] playerAppController is not assigned. " +
                                     "Arena surface will not render.  Assign it in the Inspector.");
                }
                return;
            }

            if (skinSet == null)
            {
                if (!_hasWarnedMissingSkinSet)
                {
                    _hasWarnedMissingSkinSet = true;
                    Debug.LogWarning("[ArenaSurfaceRenderer] skinSet is not assigned. " +
                                     "Arena surface will not render.  Assign an ArenaSurfaceSkinSet.");
                }
                return;
            }

            // ── Base layer checks ─────────────────────────────────────────────────────
            // Take a copy of the struct so member access is consistent within this frame.
            ArenaSurfaceLayer baseLayer = skinSet.baseLayer;

            if (!baseLayer.enabled) { return; } // Silently skip — not an error.

            if (baseLayer.material == null)
            {
                if (!_hasWarnedMissingMaterial)
                {
                    _hasWarnedMissingMaterial = true;
                    Debug.LogWarning("[ArenaSurfaceRenderer] skinSet.baseLayer.material is not assigned. " +
                                     "Assign a material (e.g. Unlit/Transparent) to the base layer.");
                }
                return;
            }

            // ── Evaluator access ──────────────────────────────────────────────────────
            ChartRuntimeEvaluator evaluator = playerAppController.Evaluator;
            PlayfieldTransform    pfT       = playerAppController.PlayfieldTf;
            Transform             pfRoot    = playerAppController.playfieldRoot;

            // Evaluator/pfT are null until PlayerAppController.Start() finishes loading.
            if (evaluator == null || pfT == null || pfRoot == null) { return; }

            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            // ── Frustum Z ─────────────────────────────────────────────────────────────
            bool  useProfile = frustumProfile != null && frustumProfile.UseFrustumProfile;
            float zInner     = useProfile ? frustumProfile.FrustumHeightInner : 0.001f;
            float zOuter     = useProfile ? frustumProfile.FrustumHeightOuter : 0.001f;

            // ── UV scroll ─────────────────────────────────────────────────────────────
            // Accumulate scroll and wrap to [0, 1) to prevent float precision drift
            // over long sessions.
            _uvScrollOffset += baseLayer.uvScrollSpeed * Time.deltaTime;
            _uvScrollOffset.x -= Mathf.Floor(_uvScrollOffset.x);
            _uvScrollOffset.y -= Mathf.Floor(_uvScrollOffset.y);

            // ── MaterialPropertyBlock ─────────────────────────────────────────────────
            // Update once for this LateUpdate pass; the same block is reused for every
            // arena draw call this frame.
            _propBlock.SetColor("_Color", skinSet.GetEffectiveTint(baseLayer));

            if (baseLayer.texture != null)
            {
                _propBlock.SetTexture("_MainTex", baseLayer.texture);
            }

            // _MainTex_ST convention: xy = tiling scale, zw = scroll offset.
            // The shader must expose _MainTex_ST for tiling/scroll to take effect.
            // Standard Unity Unlit/Transparent and Sprites/Default shaders support this.
            _propBlock.SetVector("_MainTex_ST", new Vector4(
                baseLayer.uvScale.x,
                baseLayer.uvScale.y,
                _uvScrollOffset.x,
                _uvScrollOffset.y));

            // ── Per-arena draw ────────────────────────────────────────────────────────
            _poolUsed = 0;

            for (int i = 0; i < evaluator.ArenaCount; i++)
            {
                if (_poolUsed >= MaxArenaPool) { break; }

                EvaluatedArena ea = evaluator.GetArena(i);
                if (string.IsNullOrEmpty(ea.ArenaId) || !ea.EnabledBool) { continue; }

                float arcSweep = Mathf.Clamp(ea.ArcSweepDeg, 0f, 360f);
                if (arcSweep < 0.1f) { continue; } // Degenerate — skip.

                // ── Arena geometry in PlayfieldLocal units ─────────────────────────────
                float outerLocal = pfT.NormRadiusToLocal(ea.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(ea.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Extend outer edge by the same visual rim used by ArenaBandRenderer
                // and ArenaColliderProvider so all production surfaces align.
                float visualOuterLocal = outerLocal
                    + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;

                // Skip degenerate geometry that would produce zero-area triangles.
                if (visualOuterLocal <= innerLocal || innerLocal < 0f) { continue; }

                Vector2 center = pfT.NormalizedToLocal(
                    new Vector2(ea.CenterXNorm, ea.CenterYNorm));

                // ── Fill vertex scratch ────────────────────────────────────────────────
                FillSectorVerts(_vertScratch, arcSegments,
                    ea.ArcStartDeg, arcSweep,
                    center, innerLocal, visualOuterLocal,
                    zInner, zOuter);

                int slot = _poolUsed++;
                _meshPool[slot].vertices = _vertScratch;
                _meshPool[slot].RecalculateBounds();
                Graphics.DrawMesh(_meshPool[slot], localToWorld, baseLayer.material,
                    gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Geometry helpers
        // -------------------------------------------------------------------

        // Fills the vertex scratch array in-place for one filled annular sector.
        //
        // Vertex layout:
        //   verts[0 .. N]         → inner arc  (radius = innerLocal, Z = zInner)
        //   verts[N+1 .. 2N+1]    → outer arc  (radius = outerLocal, Z = zOuter)
        //
        // All positions are in pfRoot local XY space.  The DrawMesh localToWorld
        // matrix converts them to world space — same convention as all other
        // production renderers in this project.
        private static void FillSectorVerts(
            Vector3[] verts, int N,
            float arcStartDeg, float arcSweepDeg,
            Vector2 center,
            float innerLocal, float outerLocal,
            float zInner, float zOuter)
        {
            float degPerStep = arcSweepDeg / N;

            for (int i = 0; i <= N; i++)
            {
                float deg  = arcStartDeg + i * degPerStep;
                float rad  = deg * Mathf.Deg2Rad;
                float cosA = Mathf.Cos(rad);
                float sinA = Mathf.Sin(rad);

                // Inner arc vertex at this angle.
                verts[i] = new Vector3(
                    center.x + cosA * innerLocal,
                    center.y + sinA * innerLocal,
                    zInner);

                // Outer arc vertex at this angle.
                verts[N + 1 + i] = new Vector3(
                    center.x + cosA * outerLocal,
                    center.y + sinA * outerLocal,
                    zOuter);
            }
        }
    }
}
