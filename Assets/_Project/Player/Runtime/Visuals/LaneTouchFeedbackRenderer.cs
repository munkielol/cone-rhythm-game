// LaneTouchFeedbackRenderer.cs
// Production lane-touch feedback renderer (spec §5.11.1).
//
// Renders a subtle filled annular-sector highlight over each lane that has an
// active touch inside it.  The highlight fades in when a touch enters a lane
// and fades out when the touch leaves or ends.
//
// ── What this renderer does ───────────────────────────────────────────────────
//
//   Per enabled lane each LateUpdate:
//     • Tests each active touch from PlayerAppController.ActiveTouches against
//       the lane using ArenaHitTester.IsInsideFullLane (same radial + angular
//       membership test used by JudgementEngine and ArenaColliderProvider).
//     • Updates a per-lane fade-opacity weight [0..1]:
//         – Touch present:    opacity → 1 over laneTouchFeedback.fadeInDuration
//         – Touch absent:     opacity → 0 over laneTouchFeedback.fadeOutDuration
//         – fadeInDuration  = 0 → instant on
//         – fadeOutDuration = 0 → instant off
//     • Draws a filled annular sector when opacity > 0, using Graphics.DrawMesh.
//
// ── What this renderer does NOT do ───────────────────────────────────────────
//
//   • Does NOT affect input, hit-testing, judgement, or scoring.
//   • Does NOT depend on PlayerDebugRenderer or any debug component.
//   • Does NOT depend on note renderers or the note lifecycle.
//   • Does NOT fire on judgement events — purely driven by current touch state.
//   • Does NOT render judgement feedback (reserved for JudgementFeedbackRenderer).
//   • Does NOT render hold-specific feedback (deferred).
//
// ── Highlight geometry ────────────────────────────────────────────────────────
//
//   Per lane (fullLaneCoverage = false — default):
//     outer radius = judgementRadius       (judgement ring, where notes land)
//     inner radius = max(innerLocal, judgementRadius − radialExtentLocal)
//
//   Per lane (fullLaneCoverage = true — full visible lane):
//     outer radius = visualOuterLocal      (same rim used by ArenaSurfaceRenderer)
//     inner radius = innerLocal            (arena inner edge)
//
//   Arc span in both modes:
//     arc span   = lane.WidthDeg × laneTouchFeedback.laneWidthScale
//     arc center = lane.CenterDeg
//
//   Z layout — flat overlay above the arena cone:
//     overlayZ  = FrustumZAtRadius(judgementRing) + overlayHeightLocal
//     The Z anchor is always the judgement ring radius so FrustumZAtRadius stays
//     within the valid [innerLocal, outerLocal] interpolation range even when the
//     sector extends outward to visualOuterLocal.
//     Both inner and outer arc vertices share this same Z.
//     The result is a flat disc segment that sits visibly above the arena surface
//     stack and reads clearly from the game camera, regardless of frustum tilt.
//     This avoids the Z-fighting and low-visibility problems of a surface-hugging
//     approach driven only by a tiny per-layer epsilon.
//
// ── Rendering pattern ─────────────────────────────────────────────────────────
//
//   Identical to ArenaSurfaceRenderer and JudgementRingRenderer:
//     – Pre-allocated Mesh pool (MaxLanePool slots); vertices written in-place
//       each LateUpdate — zero per-frame GC allocation after Awake.
//     – Graphics.DrawMesh — visible in Game view without Gizmos; no child GOs.
//     – Single MaterialPropertyBlock, _Color updated per lane before each call
//       to encode the per-lane fade opacity into the draw.
//
// ── Wiring ────────────────────────────────────────────────────────────────────
//
//   1. Add this component to any GO in the Player scene.
//   2. Assign playerAppController, frustumProfile, and skinSet in the Inspector.
//   3. Assign a material (Unlit/Transparent) to skinSet.laneTouchFeedback.material.
//   4. Enable skinSet.laneTouchFeedback.enabled = true (default).

using UnityEngine;
using RhythmicFlow.Shared;
using System.Collections.Generic;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production lane-touch feedback renderer (spec §5.11.1).
    /// Draws a subtle filled sector highlight over each lane that is currently touched.
    ///
    /// <para>Visual-only — does not affect input, judgement, scoring, or note rendering.</para>
    ///
    /// <para>Attach to any GO in the Player scene.  Assign
    /// <see cref="playerAppController"/>, <see cref="frustumProfile"/>, and
    /// <see cref="skinSet"/> in the Inspector.</para>
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/LaneTouchFeedbackRenderer")]
    public class LaneTouchFeedbackRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("PlayerAppController providing lane/arena geometry and active touch state.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Shared frustum profile.  Assign the same profile used by the production renderers " +
                 "so the highlight sits on the same cone surface.\n\n" +
                 "When null or UseFrustumProfile is false, Z defaults to 0.001 (flat).")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        [Tooltip("Gameplay feedback skin.  Source of truth for lane-touch highlight appearance.\n\n" +
                 "Use skinSet.laneTouchFeedback.enabled to toggle the effect.")]
        [SerializeField] private GameplayFeedbackSkinSet skinSet;

        [Header("Geometry")]
        [Tooltip("Arc segments per lane highlight mesh.  More = smoother sector edges.\n\n" +
                 "Minimum: 3.  Default: 16.\n" +
                 "Changing this at runtime after Awake has no effect; the pool is fixed at Awake.")]
        [SerializeField] private int arcSegments = 16;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // One mesh per active lane slot.  Pre-allocated in Awake.
        private const int MaxLanePool = 64;

        // Vertex layout (N = arcSegments):
        //   indices 0..N         → inner arc  (N+1 verts, Z = zInner)
        //   indices N+1..2N+1    → outer arc  (N+1 verts, Z = zOuter)
        // Triangles: N quads × 2 tris × 3 indices = N*6.

        private Mesh[]    _meshPool;
        private int       _poolUsed;
        private Vector3[] _vertScratch;

        // Per-lane fade opacity [0..1], indexed by evaluator lane index.
        // Tracks fade-in and fade-out independently per lane across frames.
        // Size capped at MaxLanePool; any lane beyond that shares the last slot
        // (degenerate for typical charts with ≤ 64 lanes).
        private float[] _laneOpacities;

        // Single MaterialPropertyBlock reused for all draw calls.
        // _Color is updated per-lane before each Graphics.DrawMesh call.
        private MaterialPropertyBlock _propBlock;

        // Per-warning guards — fire once on misconfiguration, then go silent.
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

            _vertScratch   = new Vector3[vertCount];
            _laneOpacities = new float[MaxLanePool];

            // ── Triangle index pattern ────────────────────────────────────────────────
            // CCW winding from +Z, matching ArenaSurfaceRenderer / JudgementRingRenderer.
            // Per quad i (inner[i] → inner[i+1], outer[i] → outer[i+1]):
            //   tri 1: inner[i], outer[i],  outer[i+1]
            //   tri 2: inner[i], outer[i+1], inner[i+1]
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
            // Normalized [0..1] set once; tiling/scroll via _MainTex_ST if needed later.
            var uvs = new Vector2[vertCount];
            for (int i = 0; i <= N; i++)
            {
                float u = (float)i / N;
                uvs[i]         = new Vector2(u, 0f);
                uvs[N + 1 + i] = new Vector2(u, 1f);
            }

            // ── Mesh pool ─────────────────────────────────────────────────────────────
            _meshPool = new Mesh[MaxLanePool];
            for (int slot = 0; slot < MaxLanePool; slot++)
            {
                var m = new Mesh { name = "LaneTouchFeedback" };
                m.vertices  = new Vector3[vertCount]; // zero-filled placeholder
                m.uv        = uvs;                    // static normalized UVs
                m.triangles = triPattern;             // Unity copies internally
                m.RecalculateBounds();
                _meshPool[slot] = m;
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
            // ── Guard: required references ────────────────────────────────────────────
            if (playerAppController == null)
            {
                if (!_hasWarnedMissingController)
                {
                    _hasWarnedMissingController = true;
                    Debug.LogWarning("[LaneTouchFeedbackRenderer] playerAppController is not assigned. " +
                                     "Lane touch feedback will not render.  Assign it in the Inspector.");
                }
                return;
            }

            if (skinSet == null)
            {
                if (!_hasWarnedMissingSkinSet)
                {
                    _hasWarnedMissingSkinSet = true;
                    Debug.LogWarning("[LaneTouchFeedbackRenderer] skinSet is not assigned. " +
                                     "Lane touch feedback will not render.  " +
                                     "Assign a GameplayFeedbackSkinSet in the Inspector.");
                }
                return;
            }

            // ── Lane touch feedback config ────────────────────────────────────────────
            // Take a struct copy so member access is consistent within this frame.
            LaneTouchFeedback ltf = skinSet.laneTouchFeedback;

            if (!ltf.enabled) { return; } // Silently skip — not an error.

            if (ltf.material == null)
            {
                if (!_hasWarnedMissingMaterial)
                {
                    _hasWarnedMissingMaterial = true;
                    Debug.LogWarning("[LaneTouchFeedbackRenderer] skinSet.laneTouchFeedback.material " +
                                     "is not assigned.  Assign a material (e.g. Unlit/Transparent) " +
                                     "to the Lane Touch Feedback section of the skin set.");
                }
                return;
            }

            // ── Evaluator / playfield access ──────────────────────────────────────────
            ChartRuntimeEvaluator evaluator = playerAppController.Evaluator;
            PlayfieldTransform    pfT       = playerAppController.PlayfieldTf;
            Transform             pfRoot    = playerAppController.playfieldRoot;

            if (evaluator == null || pfT == null || pfRoot == null) { return; }

            // Geometry dictionaries (null before Start() completes).
            var arenaGeos   = playerAppController.ArenaGeometries;
            var laneGeos    = playerAppController.LaneGeometries;
            var laneToArena = playerAppController.LaneToArena;

            if (arenaGeos == null || laneGeos == null || laneToArena == null) { return; }

            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            // ── Touch state ───────────────────────────────────────────────────────────
            IReadOnlyList<TouchSnapshot> touches   = playerAppController.ActiveTouches;
            int                          touchCount = touches != null ? touches.Count : 0;

            // ── Frustum Z heights ─────────────────────────────────────────────────────
            bool  useProfile = frustumProfile != null && frustumProfile.UseFrustumProfile;
            float hInner     = useProfile ? frustumProfile.FrustumHeightInner : 0.001f;
            float hOuter     = useProfile ? frustumProfile.FrustumHeightOuter : 0.001f;

            float dt = Time.deltaTime;
            _poolUsed = 0;

            // ── Per-lane loop ─────────────────────────────────────────────────────────
            for (int laneIdx = 0; laneIdx < evaluator.LaneCount; laneIdx++)
            {
                if (_poolUsed >= MaxLanePool) { break; }

                EvaluatedLane lane = evaluator.GetLane(laneIdx);
                if (string.IsNullOrEmpty(lane.LaneId) || !lane.EnabledBool) { continue; }

                // ── Geometry lookup ────────────────────────────────────────────────────
                // Use the pre-evaluated dictionaries (enabled arenas/lanes only).
                if (!laneGeos.TryGetValue(lane.LaneId, out LaneGeometry laneGeo)) { continue; }
                if (!laneToArena.TryGetValue(lane.LaneId, out string arenaId))    { continue; }
                if (!arenaGeos.TryGetValue(arenaId, out ArenaGeometry arenaGeo))  { continue; }

                // Arena radii in PlayfieldLocal units.
                float outerLocal = pfT.NormRadiusToLocal(arenaGeo.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(arenaGeo.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                float judgementR = NoteApproachMath.JudgementRadius(
                    outerLocal, pfT.MinDimLocal, PlayerSettingsStore.JudgementInsetNorm);

                // Visual outer edge — same expansion used by ArenaSurfaceRenderer and
                // LaneGuideRenderer so the full-coverage overlay aligns with the visible rim.
                float visualOuterLocal = outerLocal
                    + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;

                // ── Touch membership test ──────────────────────────────────────────────
                // Uses the same IsInsideFullLane path as JudgementEngine — radial band +
                // arc + angular lane test.  No input-band expansion (visual use only).
                bool laneIsTouched = false;
                for (int ti = 0; ti < touchCount; ti++)
                {
                    if (ArenaHitTester.IsInsideFullLane(
                            touches[ti].HitLocalXY, arenaGeo, laneGeo, pfT))
                    {
                        laneIsTouched = true;
                        break;
                    }
                }

                // ── Per-lane fade opacity ──────────────────────────────────────────────
                // Index capped at MaxLanePool - 1 for safety (degenerate for typical charts).
                int opIdx = Mathf.Min(laneIdx, MaxLanePool - 1);

                if (laneIsTouched)
                {
                    // Fade in.
                    float fadeIn = ltf.fadeInDuration;
                    _laneOpacities[opIdx] = fadeIn > 0f
                        ? Mathf.Min(1f, _laneOpacities[opIdx] + dt / fadeIn)
                        : 1f;
                }
                else
                {
                    // Fade out.
                    float fadeOut = ltf.fadeOutDuration;
                    _laneOpacities[opIdx] = fadeOut > 0f
                        ? Mathf.Max(0f, _laneOpacities[opIdx] - dt / fadeOut)
                        : 0f;
                }

                // Skip draw when fully invisible — no draw call emitted.
                if (_laneOpacities[opIdx] <= 0f) { continue; }

                // ── Highlight geometry ─────────────────────────────────────────────────
                // fullLaneCoverage = false: narrow band anchored at the judgement ring.
                // fullLaneCoverage = true:  full visible lane from innerLocal to the
                //                           visual outer rim, matching ArenaSurfaceRenderer.

                float highlightOuter = ltf.fullLaneCoverage ? visualOuterLocal : judgementR;
                float highlightInner = ltf.fullLaneCoverage
                    ? innerLocal
                    : Mathf.Max(innerLocal, judgementR - ltf.radialExtentLocal);

                if (highlightOuter <= highlightInner) { continue; } // Degenerate — skip.

                float arcSweep = Mathf.Clamp(
                    laneGeo.WidthDeg * Mathf.Max(0.1f, ltf.laneWidthScale),
                    0.1f, 360f);
                float arcStart = laneGeo.CenterDeg - arcSweep * 0.5f;

                Vector2 center = pfT.NormalizedToLocal(
                    new Vector2(arenaGeo.CenterXNorm, arenaGeo.CenterYNorm));

                // Flat overlay Z — the entire sector shares one height above the cone.
                // The Z anchor is always judgementR, never highlightOuter, so that
                // FrustumZAtRadius stays within its valid [innerLocal, outerLocal] range
                // even when fullLaneCoverage extends the sector out to visualOuterLocal.
                // The overlay is then lifted by overlayHeightLocal above that anchor so it
                // sits clearly above all arena surface layers and reads as a distinct overlay
                // from the game camera without Z-fighting.
                float zAtJudgement = NoteApproachMath.FrustumZAtRadius(
                    judgementR, innerLocal, outerLocal, hInner, hOuter);
                float overlayZ = zAtJudgement + ltf.overlayHeightLocal;
                float zInner   = overlayZ;
                float zOuter   = overlayZ;

                // ── Fill vertex scratch ────────────────────────────────────────────────
                FillSectorVerts(_vertScratch, arcSegments, arcStart, arcSweep,
                    center, highlightInner, highlightOuter, zInner, zOuter);

                int slot = _poolUsed++;
                _meshPool[slot].vertices = _vertScratch;
                _meshPool[slot].RecalculateBounds();

                // ── Configure MaterialPropertyBlock for this lane ──────────────────────
                // Effective tint = skin tint × skin opacity, then alpha scaled by the
                // per-lane fade weight.  Updated per lane because the fade weight differs.
                Color tint  = skinSet.GetLaneTouchEffectiveTint();
                tint.a     *= _laneOpacities[opIdx];
                _propBlock.SetColor("_Color", tint);

                if (ltf.texture != null)
                {
                    _propBlock.SetTexture("_MainTex", ltf.texture);
                }

                // ── Draw ───────────────────────────────────────────────────────────────
                Graphics.DrawMesh(_meshPool[slot], localToWorld, ltf.material,
                    gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Geometry helper
        // -------------------------------------------------------------------

        // Fills the vertex scratch array in-place for one filled annular sector.
        //
        // Vertex layout:
        //   verts[0 .. N]         → inner arc  (radius = innerLocal, Z = zInner)
        //   verts[N+1 .. 2N+1]    → outer arc  (radius = outerLocal, Z = zOuter)
        //
        // All positions are in pfRoot local XY space.  The DrawMesh localToWorld
        // matrix converts them to world space — same convention as ArenaSurfaceRenderer.
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

                // Inner arc vertex.
                verts[i] = new Vector3(
                    center.x + cosA * innerLocal,
                    center.y + sinA * innerLocal,
                    zInner);

                // Outer arc vertex.
                verts[N + 1 + i] = new Vector3(
                    center.x + cosA * outerLocal,
                    center.y + sinA * outerLocal,
                    zOuter);
            }
        }
    }
}
