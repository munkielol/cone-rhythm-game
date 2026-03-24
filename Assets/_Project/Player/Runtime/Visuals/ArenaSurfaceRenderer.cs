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
//     • Delegates occupancy computation to ArenaOccupancyEvaluator (Shared):
//         – Collects enabled lane intervals for this arena, clamped to the arena span.
//         – Merges overlapping lanes into an occupied union.
//         – Computes fill intervals as the complement of that union within the arena span.
//     • Builds one filled sector mesh per fill interval, spanning [innerLocal .. visualOuterLocal].
//     • Places vertices on the frustum cone surface using PlayfieldFrustumProfile.
//     • For each fill mesh — Base layer pass, then Detail layer pass, then Accent layer pass.
//
//   The arena surface acts as background/filler only in the angular regions between
//   lane bodies.  LaneSurfaceRenderer draws the lane bodies themselves.  This prevents
//   the arena surface from visually competing with lane surfaces underneath lane positions.
//
// ── What this renderer does NOT do ───────────────────────────────────────────
//
//   • Does NOT own MeshColliders or affect input/hit-testing.
//     (That is ArenaColliderProvider's responsibility.)
//   • Does NOT depend on PlayerDebugArenaSurface or PlayerDebugRenderer.
//   • Does NOT render notes, holds, or feedback effects.
//   • Does NOT render beneath enabled lane bodies.
//   • Does NOT own the occupancy math — that lives in ArenaOccupancyEvaluator (Shared).
//
// ── Gap-only rendering ────────────────────────────────────────────────────────
//
//   This renderer delegates gap computation to ArenaOccupancyEvaluator (Shared):
//     1. Compute(arena, evaluator) — collects lanes, merges intervals, computes fill.
//     2. Iterate FillIntervalCount — one DrawGapSector call per fill interval.
//
//   See ArenaOccupancyEvaluator.cs for the full sort/merge/complement algorithm.
//
//   Assumptions:
//     • Lane CenterDeg is authored within the parent arena's angular span.
//     • Lane edges that extend beyond the arena boundary are clamped at the arena edges.
//       No wrap-around splitting is performed for lanes that straddle the 0°/360° seam
//       of a full-circle arena (deferred — same limitation as ArenaOccupancyEvaluator).
//     • If no enabled lanes exist in an arena, the full arena sector is drawn (fallback
//       to previous behavior — gap = entire arena).
//     • If the gap pool is exhausted (_poolUsed >= MaxGapPool), remaining gap sectors
//       for that frame are silently skipped.
//
// ── Rendering pattern ─────────────────────────────────────────────────────────
//
//   Identical to JudgementRingRenderer and ArenaBandRenderer:
//     – pre-allocated Mesh pool (MaxGapPool slots), vertices written in-place
//       once per gap sector per LateUpdate — zero per-frame GC allocation after Awake.
//     – Graphics.DrawMesh — works in Game view without Gizmos; no child GOs.
//     – Three MaterialPropertyBlocks (_propBlock / _detailPropBlock / _accentPropBlock),
//       each configured once before the arena loop and reused for all gap draw calls.
//     – Detail and accent layers reuse the same mesh slot as base (no second
//       or third vertex fill needed).
//
// ── Vertex layout ─────────────────────────────────────────────────────────────
//
//   Per gap mesh, N = arcSegments:
//     indices  0 .. N      → inner arc vertices at Z = zInner
//     indices N+1 .. 2N+1  → outer arc vertices at Z = zOuter
//
//   UVs (set once in Awake; normalized [0..1] across each gap's angular extent):
//     inner arc:  u = i/N, v = 0
//     outer arc:  u = i/N, v = 1
//
//   Note: UV u=0..1 spans each gap's own angular extent, not the full arena sweep.
//   UV scroll is consistent across all gap meshes (same accumulated offset per frame).
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
//      LaneSurfaceRenderer, JudgementRingRenderer, ArenaColliderProvider — all independent.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production arena surface renderer.  Draws the filled annular sector for each
    /// active arena using the base, detail, and accent layers of <see cref="ArenaSurfaceSkinSet"/>.
    ///
    /// <para>Renders only the angular fill intervals — the portions of the arena span not
    /// currently occupied by any enabled lane.  Occupancy computation is delegated to
    /// <see cref="ArenaOccupancyEvaluator"/> (Shared), which is the single source of truth
    /// for arena partition math (spec §5.5.3).  <see cref="LaneSurfaceRenderer"/> draws the
    /// lane bodies; this renderer fills the space between them.</para>
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
                 "Base, detail, and accent layers are all rendered when enabled and assigned a material.")]
        [SerializeField] private ArenaSurfaceSkinSet skinSet;

        [Header("Geometry")]
        [Tooltip("Arc segments per gap mesh.  More segments = smoother arc edges.\n\n" +
                 "Minimum: 3.  Default: 48.\n" +
                 "Note: changing this at runtime after Awake has no effect; the pool is fixed at Awake.")]
        [SerializeField] private int arcSegments = 48;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        // Maximum gap sector meshes that can be drawn in one frame across all arenas.
        // Each enabled lane in an arena can produce at most one gap before it and one after
        // the last lane.  With MaxArenas = 16 arenas × up to 8 lanes each → at most 9 gaps
        // per arena → 144 total.  128 covers the common case; additional gaps are silently
        // skipped if the pool is full.
        private const int MaxGapPool = 128;

        // Maximum number of lanes per arena the shared occupancy evaluator is built for.
        // 64 is far above any realistic lane count per arena.
        private const int MaxLanesScratch = 64;

        // Each gap mesh vertex layout:
        //   indices 0..N         → inner arc  (N+1 verts, Z = zInner)
        //   indices N+1..2N+1    → outer arc  (N+1 verts, Z = zOuter)
        //
        // Triangles: N quads × 2 tris × 3 indices = N*6 indices.

        private Mesh[] _meshPool;
        private int    _poolUsed;

        // Shared vertex scratch filled in-place per gap sector each LateUpdate.
        // Length = (arcSegments + 1) * 2.
        private Vector3[] _vertScratch;

        // Shared arena partition evaluator — single source of truth for fill intervals.
        // One instance per renderer, created in Awake.  Compute() is called once per
        // arena per LateUpdate frame; results are valid until the next Compute() call.
        private ArenaOccupancyEvaluator _occupancy;

        // Base layer: MaterialPropertyBlock and accumulated UV scroll offset.
        // Configured once before the arena loop; reused for every gap draw call.
        private MaterialPropertyBlock _propBlock;
        private Vector2               _uvScrollOffset;

        // Detail layer: separate MaterialPropertyBlock and scroll offset.
        // Configured once before the arena loop when the detail layer will render.
        // Uses the same mesh slot as base (no second vertex fill needed).
        private MaterialPropertyBlock _detailPropBlock;
        private Vector2               _detailUvScrollOffset;

        // Accent layer: separate MaterialPropertyBlock and scroll offset.
        // Configured once before the arena loop when the accent layer will render.
        // Uses the same mesh slot as base (no third vertex fill needed).
        private MaterialPropertyBlock _accentPropBlock;
        private Vector2               _accentUvScrollOffset;

        // Per-warning guards — prevent per-frame console spam on misconfiguration.
        private bool _hasWarnedMissingController;
        private bool _hasWarnedMissingSkinSet;
        private bool _hasWarnedMissingMaterial;
        private bool _hasWarnedDetailMissingMaterial;
        private bool _hasWarnedAccentMissingMaterial;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            arcSegments = Mathf.Max(3, arcSegments);

            int N         = arcSegments;
            int vertCount = (N + 1) * 2;
            int triCount  = N * 6;

            // Shared vertex scratch (overwritten per gap sector each frame, then copied into mesh).
            _vertScratch = new Vector3[vertCount];

            // Shared arena partition evaluator — pre-allocated, zero GC per frame.
            // MaxLanesScratch sets the per-arena lane capacity; 64 covers all realistic charts.
            _occupancy = new ArenaOccupancyEvaluator(MaxLanesScratch);

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
            //   inner arc:  (u = i/N, v = 0)   — inner edge of the gap sector
            //   outer arc:  (u = i/N, v = 1)   — outer edge of the gap sector
            //
            // u spans each gap's own angular extent (0 = gap left edge, 1 = gap right edge).
            // The scroll offset in _MainTex_ST is the same for all gap meshes each frame,
            // so scrolling appears continuous across different gaps.
            var uvs = new Vector2[vertCount];
            for (int i = 0; i <= N; i++)
            {
                float u = (float)i / N;
                uvs[i]         = new Vector2(u, 0f);
                uvs[N + 1 + i] = new Vector2(u, 1f);
            }

            // ── Mesh pool ─────────────────────────────────────────────────────────────
            _meshPool = new Mesh[MaxGapPool];
            for (int slot = 0; slot < MaxGapPool; slot++)
            {
                var m = new Mesh { name = "ArenaSurfaceGap" };
                m.vertices  = new Vector3[vertCount]; // zero-filled placeholder
                m.uv        = uvs;                    // static normalized UVs (Unity copies)
                m.triangles = triPattern;             // Unity copies internally
                m.RecalculateBounds();
                _meshPool[slot] = m;
            }

            _propBlock      = new MaterialPropertyBlock();
            _uvScrollOffset = Vector2.zero;

            _detailPropBlock      = new MaterialPropertyBlock();
            _detailUvScrollOffset = Vector2.zero;

            _accentPropBlock      = new MaterialPropertyBlock();
            _accentUvScrollOffset = Vector2.zero;
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

            // ── Detail layer state ────────────────────────────────────────────────────
            // Resolved once here so the arena loop can branch cheaply without re-reading
            // the struct or re-checking the warning guard on every iteration.
            ArenaSurfaceLayer detailLayer      = skinSet.detailLayer;
            bool              detailWillRender = false;

            if (detailLayer.enabled)
            {
                if (detailLayer.material != null)
                {
                    detailWillRender = true;
                }
                else
                {
                    // Detail is enabled but has no material — warn once and skip.
                    if (!_hasWarnedDetailMissingMaterial)
                    {
                        _hasWarnedDetailMissingMaterial = true;
                        Debug.LogWarning("[ArenaSurfaceRenderer] skinSet.detailLayer is enabled but " +
                                         "detailLayer.material is not assigned.  " +
                                         "Assign a material to the detail layer or disable it.");
                    }
                }
            }

            // ── Accent layer state ────────────────────────────────────────────────────
            // Same pattern as detail: resolved once before the arena loop.
            ArenaSurfaceLayer accentLayer      = skinSet.accentLayer;
            bool              accentWillRender = false;

            if (accentLayer.enabled)
            {
                if (accentLayer.material != null)
                {
                    accentWillRender = true;
                }
                else
                {
                    // Accent is enabled but has no material — warn once and skip.
                    if (!_hasWarnedAccentMissingMaterial)
                    {
                        _hasWarnedAccentMissingMaterial = true;
                        Debug.LogWarning("[ArenaSurfaceRenderer] skinSet.accentLayer is enabled but " +
                                         "accentLayer.material is not assigned.  " +
                                         "Assign a material to the accent layer or disable it.");
                    }
                }
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

            // ── UV scroll — base layer ────────────────────────────────────────────────
            // Accumulate scroll and wrap to [0, 1) to prevent float precision drift
            // over long sessions.
            _uvScrollOffset += baseLayer.uvScrollSpeed * Time.deltaTime;
            _uvScrollOffset.x -= Mathf.Floor(_uvScrollOffset.x);
            _uvScrollOffset.y -= Mathf.Floor(_uvScrollOffset.y);

            // ── UV scroll — detail layer ──────────────────────────────────────────────
            // Independent accumulator — detail can scroll at a different rate/direction.
            // Only update when detail will actually render (no-op otherwise).
            if (detailWillRender)
            {
                _detailUvScrollOffset += detailLayer.uvScrollSpeed * Time.deltaTime;
                _detailUvScrollOffset.x -= Mathf.Floor(_detailUvScrollOffset.x);
                _detailUvScrollOffset.y -= Mathf.Floor(_detailUvScrollOffset.y);
            }

            // ── UV scroll — accent layer ──────────────────────────────────────────────
            // Independent accumulator — accent can scroll at a different rate/direction.
            // Only update when accent will actually render (no-op otherwise).
            if (accentWillRender)
            {
                _accentUvScrollOffset += accentLayer.uvScrollSpeed * Time.deltaTime;
                _accentUvScrollOffset.x -= Mathf.Floor(_accentUvScrollOffset.x);
                _accentUvScrollOffset.y -= Mathf.Floor(_accentUvScrollOffset.y);
            }

            // ── MaterialPropertyBlock — base layer ────────────────────────────────────
            // Configured once; reused for every gap base draw call this frame.
            // _MainTex_ST convention: xy = tiling scale, zw = scroll offset.
            // Standard Unity Unlit/Transparent and Sprites/Default shaders support this.
            _propBlock.SetColor("_Color", skinSet.GetEffectiveTint(baseLayer));
            if (baseLayer.texture != null)
            {
                _propBlock.SetTexture("_MainTex", baseLayer.texture);
            }
            _propBlock.SetVector("_MainTex_ST", new Vector4(
                baseLayer.uvScale.x,
                baseLayer.uvScale.y,
                _uvScrollOffset.x,
                _uvScrollOffset.y));

            // ── MaterialPropertyBlock — detail layer ──────────────────────────────────
            // Configured once; reused for every gap detail draw call this frame.
            // Same _MainTex_ST convention as base.
            if (detailWillRender)
            {
                _detailPropBlock.SetColor("_Color", skinSet.GetEffectiveTint(detailLayer));
                if (detailLayer.texture != null)
                {
                    _detailPropBlock.SetTexture("_MainTex", detailLayer.texture);
                }
                _detailPropBlock.SetVector("_MainTex_ST", new Vector4(
                    detailLayer.uvScale.x,
                    detailLayer.uvScale.y,
                    _detailUvScrollOffset.x,
                    _detailUvScrollOffset.y));
            }

            // ── MaterialPropertyBlock — accent layer ──────────────────────────────────
            // Configured once; reused for every gap accent draw call this frame.
            // Same _MainTex_ST convention as base and detail.
            if (accentWillRender)
            {
                _accentPropBlock.SetColor("_Color", skinSet.GetEffectiveTint(accentLayer));
                if (accentLayer.texture != null)
                {
                    _accentPropBlock.SetTexture("_MainTex", accentLayer.texture);
                }
                _accentPropBlock.SetVector("_MainTex_ST", new Vector4(
                    accentLayer.uvScale.x,
                    accentLayer.uvScale.y,
                    _accentUvScrollOffset.x,
                    _accentUvScrollOffset.y));
            }

            // ── Per-arena gap draw ────────────────────────────────────────────────────
            _poolUsed = 0;

            for (int i = 0; i < evaluator.ArenaCount; i++)
            {
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

                // ── Compute fill intervals via shared occupancy evaluator ───────────────
                //
                // ArenaOccupancyEvaluator.Compute() is the single source of truth for:
                //   • which angular intervals are currently occupied by enabled lanes
                //   • which fill intervals remain in the arena span after subtracting them
                //
                // If no enabled lanes exist, the entire arena span is one fill interval,
                // preserving the pre-lane full-arena draw behaviour.
                //
                // The arena was already validated (EnabledBool, arcSweep) above, so
                // Compute() will always return true here.  The check is kept for safety.
                if (!_occupancy.Compute(ea, evaluator)) { continue; }

                // ── Draw one sector mesh per fill interval ─────────────────────────────
                for (int f = 0; f < _occupancy.FillIntervalCount; f++)
                {
                    AngularInterval fill = _occupancy.GetFillInterval(f);
                    DrawGapSector(
                        fill.StartDeg, fill.SweepDeg,
                        center, innerLocal, visualOuterLocal, zInner, zOuter,
                        localToWorld, baseLayer, detailLayer, accentLayer,
                        detailWillRender, accentWillRender);
                }
            }
        }

        // -------------------------------------------------------------------
        // Gap sector draw helper
        // -------------------------------------------------------------------

        // Fills the vertex scratch array for one gap sector and issues the
        // base / detail / accent DrawMesh calls.  Increments _poolUsed.
        //
        // Caller must check _poolUsed < MaxGapPool before calling.
        // This method guards internally and is a no-op if the pool is full.
        private void DrawGapSector(
            float gapStartDeg, float gapSweepDeg,
            Vector2 center, float innerLocal, float visualOuterLocal,
            float zInner, float zOuter,
            Matrix4x4 localToWorld,
            in ArenaSurfaceLayer baseLayer,
            in ArenaSurfaceLayer detailLayer,
            in ArenaSurfaceLayer accentLayer,
            bool detailWillRender,
            bool accentWillRender)
        {
            // Skip degenerate gaps (too narrow to produce visible geometry).
            if (gapSweepDeg < 0.1f) { return; }

            // Guard: stop drawing if the pool is exhausted this frame.
            if (_poolUsed >= MaxGapPool) { return; }

            // Fill vertex scratch for this gap sector — same layout as the old
            // full-arena fill.  The only change is gapStartDeg/gapSweepDeg
            // rather than ea.ArcStartDeg/arcSweep.
            FillSectorVerts(_vertScratch, arcSegments,
                gapStartDeg, gapSweepDeg,
                center, innerLocal, visualOuterLocal,
                zInner, zOuter);

            int slot = _poolUsed++;
            _meshPool[slot].vertices = _vertScratch;
            _meshPool[slot].RecalculateBounds();

            // ── Base layer draw ────────────────────────────────────────────────────
            Graphics.DrawMesh(_meshPool[slot], localToWorld, baseLayer.material,
                gameObject.layer, null, 0, _propBlock);

            // ── Detail layer draw (same mesh, different material/propblock) ─────────
            // Rendered after base so it composites on top.  No second vertex fill
            // is needed — the mesh data written above is still valid.
            if (detailWillRender)
            {
                Graphics.DrawMesh(_meshPool[slot], localToWorld, detailLayer.material,
                    gameObject.layer, null, 0, _detailPropBlock);
            }

            // ── Accent layer draw (same mesh, different material/propblock) ─────────
            // Rendered after detail — top-most layer.  No third vertex fill needed.
            if (accentWillRender)
            {
                Graphics.DrawMesh(_meshPool[slot], localToWorld, accentLayer.material,
                    gameObject.layer, null, 0, _accentPropBlock);
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
