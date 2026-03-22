// PlayerDebugArenaSurface.cs
// DEBUG SCAFFOLDING — remove before shipping.
//
// Generates a solid ring-sector (annular arc band) mesh per arena so lanes appear
// to sit on a visible cone/frustum track in the Game view.
//
// Geometry is read from ChartRuntimeEvaluator via PlayerAppController.Evaluator each
// LateUpdate and meshes are rebuilt only when evaluated arena parameters change past an
// epsilon threshold.  This ensures the surface follows animated arena tracks (arcStartDeg,
// outerRadius, bandThickness, center) in real time, consistent with all other visual
// overlays (spec §5.9).
//
// --- Visibility toggles ---
// forceHideArenaSurfaceMesh disables MeshRenderer.enabled per arena child — it does not
// fight with UpdateArenaMeshes because we never use SetActive for the hide toggle.
// Child GOs remain active at all times; only Renderer.enabled and Collider.enabled change.
//
// --- Why child GOs stay active ---
// Previously the code called state.ChildGo.SetActive(ea.EnabledBool) each frame, which
// conflicted with any Inspector-level hide attempt.  Now:
//   • spec §5.6 disabled arenas → Renderer.enabled = false + Collider.enabled = false
//   • forceHideArenaSurfaceMesh → Renderer.enabled = false (Collider unaffected by default)
//   • forceDisableArenaSurfaceCollider → Collider.enabled = false
// This means toggling forceHideArenaSurfaceMesh in the Inspector has instant effect.
//
// --- PhysX "cleaning the mesh failed" fix ---
// Root cause: when AddComponent<MeshCollider>() is called on a GO that already has a
// MeshFilter, Unity auto-populates MeshCollider.sharedMesh from MeshFilter.sharedMesh at
// that exact instant — before any explicit assignment can clear it.  The placeholder mesh
// has all vertices at Vector3.zero (degenerate), so PhysX immediately tries to cook it and
// logs the warning.  Setting mc.sharedMesh = null after AddComponent is too late.
//
// Fix (Option A): the MeshCollider lives on a SEPARATE child GO that has NO MeshFilter.
// Unity therefore has nothing to auto-populate from, so no cooking happens at AddComponent
// time.  sharedMesh is only assigned after the first successful RebuildArenaVertices call
// (guarded by ArenaMeshState.HasValidGeometry).
//
// --- Vertex space ---
// PlayfieldRoot local XY defines the ring positions; local Z gives the frustum height.
// Conversion pipeline per vertex:
//   1. Compute (localX, localY, localZ) in PlayfieldRoot space.
//   2. worldPt  = pfRoot.TransformPoint(localX, localY, localZ)
//   3. meshPt   = go.transform.InverseTransformPoint(worldPt)
// Step 3 maps back to the mesh GO's local space so the mesh renders at the correct
// world position regardless of where the ArenaSurface GO is placed.
//
// --- Frustum profile ---
// Inner arc vertices are at localZ = frustumHeightInner (default 0.001).
// Outer arc vertices are at localZ = frustumHeightOuter (default 0.15).
// Hit-testing is always on the z=0 plane; these offsets are visual only.
//
// --- Triangulation ---
// Vertex layout: inner ring first (indices 0..N), outer ring second (N+1..2N+1).
// Each arc segment forms a quad from two CCW triangles (winding from +localZ):
//   Tri 1: inner[i] → outer[i]   → inner[i+1]
//   Tri 2: outer[i] → outer[i+1] → inner[i+1]
// Triangle indices are static (set once at creation); only vertex positions change.
//
// --- Allocation policy ---
// Per-arena vertex arrays (Vector3[(arcSegments+1)*2]) are pre-allocated lazily on first
// encounter and reused in-place every rebuild.  Mesh and child GO instances are created
// once per arena and updated in-place — no per-frame heap allocation.
//
// Wiring:
//   1. Create empty GO "ArenaSurface" in PlayerBoot scene.
//   2. Add component PlayerDebugArenaSurface.
//   3. Assign PlayerAppController reference in the Inspector.
//   4. (Optional) assign a Material to materialOverride.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// DEBUG SCAFFOLDING: Generates a solid cone/frustum-sector mesh per arena band.
    /// Meshes follow animated arena tracks (arcStartDeg, outerRadius, etc.) by rebuilding
    /// on geometry change each LateUpdate.
    /// Visual only — no effect on hit-testing or judgement.
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Debug/PlayerDebugArenaSurface")]
    public class PlayerDebugArenaSurface : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("DEBUG SCAFFOLDING — remove before shipping")]

        [Tooltip("PlayerAppController in the scene.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Number of arc segments per ring-sector mesh (higher = smoother).")]
        [SerializeField] private int arcSegments = 64;

        [Header("Frustum Profile (DEBUG)")]

        [Tooltip("If true, inner/outer vertices are offset in PlayfieldRoot local Z to create a " +
                 "3D cone/frustum shape. The z=0 interaction plane is never modified.")]
        [SerializeField] private bool useFrustumProfile = true;

        [Tooltip("Local Z offset for inner-arc vertices. A small positive value avoids " +
                 "z-fighting with the z=0 interaction plane.")]
        [SerializeField] private float frustumHeightInner = 0.001f;

        [Tooltip("Local Z offset for outer-arc vertices. Larger values create a steeper cone tilt.")]
        [SerializeField] private float frustumHeightOuter = 0.15f;

        [Header("Material")]

        [Tooltip("Optional material for all arena surfaces. " +
                 "If null, a simple opaque runtime material is created automatically. " +
                 "Assign a transparent material here for a see-through surface.")]
        [SerializeField] private Material materialOverride;

        [Header("Visibility (DEBUG)")]

        [Tooltip("When true, the grey arena fill mesh is hidden (MeshRenderer.enabled = false " +
                 "per arena child) while evaluation and debug line renderers continue running. " +
                 "Toggle this to see through the surface to the lane/hitband overlays. " +
                 "Does NOT affect hit-testing or chart evaluation. See spec §5.8 debug toggles.")]
        [SerializeField] private bool forceHideArenaSurfaceMesh = false;

        [Tooltip("When true, the MeshCollider is also disabled. This prevents visual-surface " +
                 "raycasts (spec §5.2.1) from hitting the arena surface mesh. Leave false if " +
                 "you rely on the parallax-correct input path for touch testing.")]
        [SerializeField] private bool forceDisableArenaSurfaceCollider = false;

        // -------------------------------------------------------------------
        // Per-arena mutable state (one instance created lazily per arena)
        // -------------------------------------------------------------------

        // Holds all mutable runtime data for a single arena's mesh.
        // Created lazily when an arena is first encountered in the evaluator.
        // All reference-type fields (GO, Mesh, Renderer, Collider) are allocated once.
        private sealed class ArenaMeshState
        {
            // Scene objects — created once per arena, never recreated unless arcSegments changes.
            //
            // Two separate child GOs:
            //   RenderChildGo  — holds MeshFilter + MeshRenderer only (NO MeshCollider).
            //   ColliderChildGo — holds MeshCollider only (NO MeshFilter).
            //
            // Keeping them separate prevents Unity from auto-populating MeshCollider.sharedMesh
            // from MeshFilter.sharedMesh at AddComponent time (the root cause of the PhysX warning).
            public GameObject   RenderChildGo;   // MeshFilter + MeshRenderer child; always active.
            public GameObject   ColliderChildGo; // MeshCollider child; always active.
            public Mesh         Mesh;            // Runtime mesh; vertices updated in-place on each rebuild.
            public MeshRenderer Renderer;        // Controlled by forceHideArenaSurfaceMesh + EnabledBool.
            public MeshCollider Collider;        // Controlled by forceDisableArenaSurfaceCollider + HasValidGeometry.

            // Pre-allocated vertex scratch: length = (arcSegments+1)*2 per arena.
            // Filled in-place during a vertex rebuild — zero per-frame heap allocation.
            public Vector3[] VertexScratch;

            // True once the first successful RebuildArenaVertices has produced valid geometry.
            // The MeshCollider sharedMesh is only assigned when this is true, preventing the
            // PhysX "cleaning the mesh failed" warning that occurs with zero-vertex placeholders.
            public bool HasValidGeometry;

            // Rate-limit flag: logs at most one collider-skip warning per arena per session.
            public bool HasLoggedColliderSkipWarning;

            // Change-detection watermarks for the nine parameters that drive vertex positions.
            // Initialized to float.MaxValue so the first LateUpdate always triggers an initial build.
            public float LastOuterLocal       = float.MaxValue;
            public float LastInnerLocal       = float.MaxValue;
            public float LastVisualOuterLocal = float.MaxValue;
            public float LastCenterX          = float.MaxValue; // PlayfieldLocal units
            public float LastCenterY          = float.MaxValue; // PlayfieldLocal units
            public float LastArcStartDeg      = float.MaxValue;
            public float LastArcSweepDeg      = float.MaxValue;
            public float LastEffectiveZInner  = float.MaxValue; // accounts for useFrustumProfile toggle
            public float LastEffectiveZOuter  = float.MaxValue;
        }

        // Keyed by arenaId — one entry per arena encountered in the evaluator.
        private readonly Dictionary<string, ArenaMeshState> _arenaStates =
            new Dictionary<string, ArenaMeshState>(StringComparer.Ordinal);

        // Runtime material created on demand when materialOverride is null.
        private Material _runtimeMat;

        // Change-detection thresholds.
        // Normalized radius units (~0.001 % of playfield width at epsilon 1e-5).
        private const float GeomEpsilon  = 1e-5f;
        // Degrees — 0.01° is sub-pixel at normal playfield sizes.
        private const float AngleEpsilon = 0.01f;

        // -------------------------------------------------------------------
        // Public read-only API for PlayerDebugRenderer
        // Minimal getters — expose only what the renderer needs to match Z offsets.
        // -------------------------------------------------------------------

        /// <summary>DEBUG: Whether the frustum vertical profile is currently active.</summary>
        public bool  UseFrustumProfile  => useFrustumProfile;

        /// <summary>DEBUG: PlayfieldRoot local Z at the inner arc edge.</summary>
        public float FrustumHeightInner => frustumHeightInner;

        /// <summary>DEBUG: PlayfieldRoot local Z at the outer arc edge.</summary>
        public float FrustumHeightOuter => frustumHeightOuter;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void OnEnable()
        {
            // Reset all change-detection watermarks so the next LateUpdate immediately
            // rebuilds every arena mesh to the current evaluated state.
            // This ensures that disabling then re-enabling the component mid-song causes
            // meshes to snap to the current geometry, not linger at a stale frame.
            foreach (ArenaMeshState state in _arenaStates.Values)
            {
                state.LastOuterLocal       = float.MaxValue;
                state.LastInnerLocal       = float.MaxValue;
                state.LastVisualOuterLocal = float.MaxValue;
                state.LastCenterX          = float.MaxValue;
                state.LastCenterY          = float.MaxValue;
                state.LastArcStartDeg      = float.MaxValue;
                state.LastArcSweepDeg      = float.MaxValue;
                state.LastEffectiveZInner  = float.MaxValue;
                state.LastEffectiveZOuter  = float.MaxValue;
            }
        }

        private void LateUpdate()
        {
            // Always run — UpdateArenaMeshes handles lazy creation and change-driven updates.
            UpdateArenaMeshes();
        }

        private void OnDestroy()
        {
            // Child GOs are destroyed automatically as children of this GO.
            // Mesh assets are separate runtime objects and must be explicitly destroyed
            // to prevent memory leaks when the component is removed.
            foreach (ArenaMeshState state in _arenaStates.Values)
            {
                if (state.Mesh != null) { Destroy(state.Mesh); }
            }

            if (_runtimeMat != null) { Destroy(_runtimeMat); }
        }

        // -------------------------------------------------------------------
        // Per-frame mesh update
        // -------------------------------------------------------------------

        // Iterates all arenas from the evaluator each LateUpdate.
        // Uses the evaluator directly (not DebugArenaGeometries) so that arenas disabled
        // by chart tracks can have their mesh hidden via Renderer.enabled rather than
        // SetActive, which would conflict with external visibility toggles.
        //
        // Child GOs remain active at all times.  Visibility is controlled by:
        //   Renderer.enabled = ea.EnabledBool && !forceHideArenaSurfaceMesh
        //   Collider.enabled = ea.EnabledBool && !forceDisableArenaSurfaceCollider && HasValidGeometry
        //
        // Rebuilds each arena's mesh vertices in-place only when a geometry parameter
        // has changed past the epsilon threshold (avoids heavy work on static charts).
        private void UpdateArenaMeshes()
        {
            if (playerAppController == null) { return; }

            ChartRuntimeEvaluator evaluator = playerAppController.Evaluator;
            PlayfieldTransform    pfT       = playerAppController.DebugPlayfieldTransform;
            Transform             pfRoot    = playerAppController.playfieldRoot;

            // Evaluator and pfT are null until PlayerAppController.Start() completes.
            if (evaluator == null || pfT == null || pfRoot == null) { return; }

            Material mat = materialOverride != null
                ? materialOverride
                : GetOrCreateRuntimeMaterial();

            // Vertex count is the same for every arena (driven by arcSegments).
            int N         = Mathf.Max(1, arcSegments);
            int vertCount = (N + 1) * 2;

            for (int i = 0; i < evaluator.ArenaCount; i++)
            {
                EvaluatedArena ea = evaluator.GetArena(i);
                if (string.IsNullOrEmpty(ea.ArenaId)) { continue; }

                // Lazy-create per-arena state the first time this arena is encountered.
                if (!_arenaStates.TryGetValue(ea.ArenaId, out ArenaMeshState state))
                {
                    state = CreateArenaMeshState(ea.ArenaId, vertCount, mat);
                    _arenaStates[ea.ArenaId] = state;
                }
                else if (state.VertexScratch.Length != vertCount)
                {
                    // arcSegments was changed at runtime (Inspector edit) — recreate entirely.
                    Destroy(state.Mesh);
                    if (state.RenderChildGo   != null) { Destroy(state.RenderChildGo); }
                    if (state.ColliderChildGo != null) { Destroy(state.ColliderChildGo); }
                    state = CreateArenaMeshState(ea.ArenaId, vertCount, mat);
                    _arenaStates[ea.ArenaId] = state;
                }

                // ── Visibility: Renderer and Collider, NOT SetActive ─────────────────────────
                // Child GO stays active so mesh updates can run regardless of visible state.
                // Using SetActive here would conflict with forceHideArenaSurfaceMesh because
                // this code runs every frame and would immediately re-enable a hidden GO.
                //
                // spec §5.6: disabled arenas have their visual hidden (Renderer off) and
                // collider disabled (no raycasts).
                bool arenaEnabled   = ea.EnabledBool;
                bool showRenderer   = arenaEnabled && !forceHideArenaSurfaceMesh;
                // Collider only activates once valid geometry has been built (see HasValidGeometry).
                bool showCollider   = arenaEnabled && !forceDisableArenaSurfaceCollider
                                      && state.HasValidGeometry;

                state.Renderer.enabled = showRenderer;
                state.Collider.enabled = showCollider;

                // Skip geometry updates for disabled arenas — their mesh doesn't need to be
                // current since the collider is off and no raycasts will hit it.
                if (!arenaEnabled) { continue; }

                // ── Derive vertex parameters ────────────────────────────────────────────────
                float outerLocal       = pfT.NormRadiusToLocal(ea.OuterRadiusNorm);
                float bandLocal        = pfT.NormRadiusToLocal(ea.BandThicknessNorm);
                float innerLocal       = outerLocal - bandLocal;

                // Visual outer rim extends beyond chart outerLocal. VISUAL ONLY.
                float visualOuterLocal = outerLocal
                    + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;

                // Arena center in PlayfieldRoot local XY (spec §5.5).
                Vector2 center = pfT.NormalizedToLocal(new Vector2(ea.CenterXNorm, ea.CenterYNorm));

                // Effective Z heights — account for the useFrustumProfile toggle so that
                // toggling the bool mid-song triggers a mesh rebuild.
                float effectiveZInner = useFrustumProfile ? frustumHeightInner : 0.001f;
                float effectiveZOuter = useFrustumProfile ? frustumHeightOuter : 0.001f;

                // ── Change detection ────────────────────────────────────────────────────────
                // Compare all nine derived vertex parameters against their watermarks.
                // Mathf.DeltaAngle gives the shortest path between two angles (handles 0/360
                // wraparound correctly), so a 1° animated step near 0° is always detected.
                bool changed =
                    Mathf.Abs(outerLocal       - state.LastOuterLocal)       > GeomEpsilon  ||
                    Mathf.Abs(innerLocal       - state.LastInnerLocal)       > GeomEpsilon  ||
                    Mathf.Abs(visualOuterLocal - state.LastVisualOuterLocal) > GeomEpsilon  ||
                    Mathf.Abs(center.x         - state.LastCenterX)          > GeomEpsilon  ||
                    Mathf.Abs(center.y         - state.LastCenterY)          > GeomEpsilon  ||
                    Mathf.Abs(Mathf.DeltaAngle(ea.ArcStartDeg, state.LastArcStartDeg)) > AngleEpsilon ||
                    Mathf.Abs(ea.ArcSweepDeg   - state.LastArcSweepDeg)     > AngleEpsilon ||
                    Mathf.Abs(effectiveZInner  - state.LastEffectiveZInner)  > GeomEpsilon  ||
                    Mathf.Abs(effectiveZOuter  - state.LastEffectiveZOuter)  > GeomEpsilon;

                if (!changed) { continue; }

                // ── Rebuild vertices in-place ───────────────────────────────────────────────
                RebuildArenaVertices(state, ea.ArenaId, ea.ArcStartDeg, ea.ArcSweepDeg,
                    center, innerLocal, visualOuterLocal,
                    effectiveZInner, effectiveZOuter, N, pfRoot);

                // Re-evaluate collider enable now that HasValidGeometry may have changed.
                state.Collider.enabled = arenaEnabled && !forceDisableArenaSurfaceCollider
                                         && state.HasValidGeometry;

                // ── Update watermarks ───────────────────────────────────────────────────────
                state.LastOuterLocal       = outerLocal;
                state.LastInnerLocal       = innerLocal;
                state.LastVisualOuterLocal = visualOuterLocal;
                state.LastCenterX          = center.x;
                state.LastCenterY          = center.y;
                state.LastArcStartDeg      = ea.ArcStartDeg;
                state.LastArcSweepDeg      = ea.ArcSweepDeg;
                state.LastEffectiveZInner  = effectiveZInner;
                state.LastEffectiveZOuter  = effectiveZOuter;
            }
        }

        // Fills VertexScratch in-place from the supplied geometry, then assigns the array
        // to the mesh and refreshes bounds and normals.
        //
        // Collider assignment is deferred until IsGeometryValidForCollider() passes.
        // This prevents the PhysX "cleaning the mesh failed" warning that occurs when
        // the collider is updated with degenerate (zero-area) geometry.
        //
        // Only called when change detection determines a rebuild is necessary.
        private void RebuildArenaVertices(
            ArenaMeshState state,
            string         arenaId,
            float arcStartDeg, float arcSweepDeg,
            Vector2 center,
            float innerLocal, float visualOuterLocal,
            float zInner, float zOuter,
            int N, Transform pfRoot)
        {
            // Fill vertex scratch in-place — no heap allocation.
            // Layout:
            //   indices 0..N      → inner arc  (innerLocal radius, z = zInner)
            //   indices N+1..2N+1 → outer arc  (visualOuterLocal, z = zOuter)
            for (int i = 0; i <= N; i++)
            {
                float t   = (float)i / N;
                float deg = arcStartDeg + t * arcSweepDeg;
                float rad = deg * Mathf.Deg2Rad;
                var   dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

                Vector2 innerPt = center + dir * innerLocal;
                Vector2 outerPt = center + dir * visualOuterLocal; // VISUAL ONLY

                // pfRoot local → world → mesh GO local (see header comment).
                Vector3 wInner = pfRoot.TransformPoint(innerPt.x, innerPt.y, zInner);
                Vector3 wOuter = pfRoot.TransformPoint(outerPt.x, outerPt.y, zOuter);

                state.VertexScratch[i]         = state.RenderChildGo.transform.InverseTransformPoint(wInner);
                state.VertexScratch[N + 1 + i] = state.RenderChildGo.transform.InverseTransformPoint(wOuter);
            }

            // Assign pre-allocated array to the existing Mesh instance.
            // Unity copies the array data internally; we do not allocate a new array.
            state.Mesh.vertices = state.VertexScratch;
            state.Mesh.RecalculateBounds();
            state.Mesh.RecalculateNormals();

            // Validate before touching the MeshCollider.
            // Degenerate geometry (zero/negative radii, near-zero sweep, or NaN vertices)
            // causes PhysX mesh cooking to fail with "cleaning the mesh failed" in the console.
            // We skip the collider update and mark HasValidGeometry = false when invalid,
            // rather than forwarding bad data to PhysX.
            bool valid = IsGeometryValidForCollider(state, arenaId,
                innerLocal, visualOuterLocal, arcSweepDeg, N);

            state.HasValidGeometry = valid;

            if (valid)
            {
                // Refresh the MeshCollider so parallax raycasts (spec §5.2.1) remain accurate
                // after the vertices change.  Setting sharedMesh = null first forces Unity to
                // rebuild the internal BVH from the updated vertex data.
                state.Collider.sharedMesh = null;
                state.Collider.sharedMesh = state.Mesh;
            }
        }

        // Lightweight validation: checks the conditions that most commonly cause PhysX to
        // fail mesh cooking without iterating every vertex (allocation-free).
        //
        // Checks (in order of cost — cheapest first):
        //   1. arcSweepDeg > threshold   — near-zero sweep → all quads degenerate
        //   2. innerLocal > 0            — zero/negative inner radius → degenerate ring
        //   3. visualOuterLocal > inner  — outer must exceed inner
        //   4. First vertex of each ring for NaN/Infinity (spot-check — catches pfRoot NaN)
        //
        // Logs at most once per arena per session (HasLoggedColliderSkipWarning guard).
        private bool IsGeometryValidForCollider(
            ArenaMeshState state,
            string         arenaId,
            float innerLocal, float visualOuterLocal,
            float arcSweepDeg, int N)
        {
            // 1. Arc sweep must be at least a few degrees to avoid zero-area quads.
            if (arcSweepDeg < 0.1f)
            {
                LogColliderSkipOnce(state, arenaId,
                    $"arcSweepDeg={arcSweepDeg:F3} < 0.1 — degenerate sweep");
                return false;
            }

            // 2. Inner radius must be positive (a ring with innerLocal <= 0 collapses to a disk
            //    or inverts, producing overlapping triangles that PhysX cannot cook).
            if (innerLocal <= 0f)
            {
                LogColliderSkipOnce(state, arenaId,
                    $"innerLocal={innerLocal:F4} <= 0 — degenerate ring");
                return false;
            }

            // 3. Outer must exceed inner so the band has positive radial width.
            if (visualOuterLocal <= innerLocal)
            {
                LogColliderSkipOnce(state, arenaId,
                    $"visualOuterLocal={visualOuterLocal:F4} <= innerLocal={innerLocal:F4}");
                return false;
            }

            // 4. Spot-check first vertex of inner ring and first vertex of outer ring.
            //    If pfRoot has a NaN transform, all vertices will be NaN.
            Vector3 v0 = state.VertexScratch[0];       // first inner vertex
            Vector3 v1 = state.VertexScratch[N + 1];   // first outer vertex

            if (HasNaNOrInfinity(v0) || HasNaNOrInfinity(v1))
            {
                LogColliderSkipOnce(state, arenaId,
                    $"vertex NaN/Infinity detected (v[0]={v0}, v[{N + 1}]={v1})");
                return false;
            }

            return true;
        }

        // True if any component of v is NaN or Infinity.
        private static bool HasNaNOrInfinity(Vector3 v)
        {
            return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);
        }

        // Logs a warning once per arena per session to avoid console spam.
        private static void LogColliderSkipOnce(ArenaMeshState state, string arenaId, string reason)
        {
            if (state.HasLoggedColliderSkipWarning) { return; }
            state.HasLoggedColliderSkipWarning = true;
            Debug.LogWarning($"[ArenaSurface] Skipping collider update for '{arenaId}': {reason}. " +
                             "MeshCollider will be enabled once geometry is valid.");
        }

        // -------------------------------------------------------------------
        // Lazy creation
        // -------------------------------------------------------------------

        // Creates two child GOs, Mesh, and all pre-allocated buffers for a new arena.
        // Triangles and UVs depend only on N (arcSegments), not on geometry, so they
        // are written once here and never change.
        //
        // Two child GOs are created (Option A PhysX fix — see header comment):
        //   renderGo   — MeshFilter + MeshRenderer; this is the visual surface.
        //   colliderGo — MeshCollider ONLY; no MeshFilter on this GO, so Unity cannot
        //                auto-populate MeshCollider.sharedMesh at AddComponent time.
        //
        // MeshCollider.sharedMesh is left null at creation and only assigned after the
        // first successful RebuildArenaVertices call (ArenaMeshState.HasValidGeometry = true).
        private ArenaMeshState CreateArenaMeshState(
            string arenaId, int vertCount, Material mat)
        {
            int N = (vertCount / 2) - 1; // inverse of: vertCount = (N+1)*2

            // --- Triangle indices (static — topology never changes) ---
            var triangles = new int[N * 6]; // N quads × 2 triangles × 3 indices
            for (int i = 0; i < N; i++)
            {
                int ti  = i * 6;
                int iI  = i;
                int iI1 = i + 1;
                int oI  = N + 1 + i;
                int oI1 = N + 1 + i + 1;

                // Triangle 1: inner[i] → outer[i] → inner[i+1]  (CCW from +localZ)
                triangles[ti + 0] = iI;
                triangles[ti + 1] = oI;
                triangles[ti + 2] = iI1;

                // Triangle 2: outer[i] → outer[i+1] → inner[i+1]
                triangles[ti + 3] = oI;
                triangles[ti + 4] = oI1;
                triangles[ti + 5] = iI1;
            }

            // --- UVs (static — u = arc progress, v = 0 inner / 1 outer) ---
            var uvs = new Vector2[vertCount];
            for (int i = 0; i <= N; i++)
            {
                float t = (float)i / N;
                uvs[i]         = new Vector2(t, 0f);
                uvs[N + 1 + i] = new Vector2(t, 1f);
            }

            // --- Mesh (placeholder vertices — overwritten on first RebuildArenaVertices call) ---
            var mesh = new Mesh
            {
                name      = $"ArenaSurface_{arenaId}",
                hideFlags = HideFlags.HideAndDontSave,
            };
            mesh.vertices  = new Vector3[vertCount]; // zeroed placeholder; real data written on rebuild
            mesh.uv        = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();

            // --- Render child GO (MeshFilter + MeshRenderer only) ---
            var renderGo = new GameObject($"ArenaSurface_{arenaId}");
            renderGo.transform.SetParent(transform, worldPositionStays: false);
            // Inherit physics layer so PlayerAppController.visualSurfaceLayerMask matches.
            renderGo.layer = gameObject.layer;

            var mf = renderGo.AddComponent<MeshFilter>();
            var mr = renderGo.AddComponent<MeshRenderer>();
            mf.sharedMesh     = mesh;
            mr.sharedMaterial = mat;

            // Apply current hide toggle immediately so the mesh doesn't flash visible on
            // the frame it's created (before the first RebuildArenaVertices call).
            mr.enabled = !forceHideArenaSurfaceMesh;

            // --- Collider child GO (MeshCollider only — NO MeshFilter) ---
            // CRITICAL: no MeshFilter on this GO.  If a MeshFilter were present, Unity would
            // read its sharedMesh during AddComponent<MeshCollider>() and auto-assign it to
            // the collider, immediately cooking the degenerate placeholder and producing the
            // "[Physics.PhysX] cleaning the mesh failed" warning.  With no MeshFilter, Unity
            // has nothing to auto-populate from, so the collider is clean at creation.
            var colliderGo = new GameObject($"ArenaSurface_{arenaId}_Collider");
            colliderGo.transform.SetParent(transform, worldPositionStays: false);
            colliderGo.layer = gameObject.layer;

            // AddComponent is safe here: no MeshFilter on colliderGo → no auto-population.
            var mc = colliderGo.AddComponent<MeshCollider>();
            mc.enabled    = false; // disabled until HasValidGeometry is true
            // mc.sharedMesh is already null by default; explicit for clarity.
            mc.sharedMesh = null;

            return new ArenaMeshState
            {
                RenderChildGo   = renderGo,
                ColliderChildGo = colliderGo,
                Mesh            = mesh,
                Renderer        = mr,
                Collider        = mc,
                VertexScratch = new Vector3[vertCount],
                HasValidGeometry              = false,
                HasLoggedColliderSkipWarning  = false,
                // Watermarks default to float.MaxValue (set in class field initializers),
                // guaranteeing an initial RebuildArenaVertices call on the very first frame.
            };
        }

        // -------------------------------------------------------------------
        // Runtime material
        // -------------------------------------------------------------------

        // Creates a simple opaque material when no materialOverride is assigned.
        // Tries URP Lit → Standard → Unlit/Color in priority order.
        // Sets CullMode.Off so the surface is visible from both sides of the playfield.
        private Material GetOrCreateRuntimeMaterial()
        {
            if (_runtimeMat != null) { return _runtimeMat; }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Standard")
                         ?? Shader.Find("Unlit/Color");

            _runtimeMat = new Material(shader);
            _runtimeMat.hideFlags = HideFlags.HideAndDontSave;

            // Neutral blue-gray, fully opaque.
            _runtimeMat.color = new Color(0.25f, 0.30f, 0.45f, 1.0f);

            // Disable back-face culling — surface should be visible from both sides.
            _runtimeMat.SetInt("_Cull", (int)CullMode.Off);

            return _runtimeMat;
        }
    }
}
