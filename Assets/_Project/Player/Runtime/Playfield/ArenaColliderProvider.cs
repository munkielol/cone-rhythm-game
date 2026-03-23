// ArenaColliderProvider.cs
// Production-safe arena surface collider provider.
//
// Creates and maintains one MeshCollider per active arena, shaped to the cone/frustum
// surface.  Used by PlayerAppController.TryProjectScreenToPlayfieldLocalXY for
// parallax-correct input (spec §5.2.1).
//
// ── Why this exists ──────────────────────────────────────────────────────────
//
//   Before this component existed, PlayerDebugArenaSurface was the only source of
//   arena MeshColliders.  That was a debug-owned component — removing or disabling
//   it for release would break visual-surface raycasts.  This component owns the
//   colliders instead, so PlayerDebugArenaSurface is no longer required for input.
//
// ── Relationship to PlayerAppController ──────────────────────────────────────
//
//   PlayerAppController.TryProjectScreenToPlayfieldLocalXY casts a Physics ray
//   against visualSurfaceLayerMask.  This component simply ensures there are
//   MeshColliders on that layer shaped to the current arena surfaces.  The two
//   components are decoupled: PlayerAppController doesn't reference this class
//   and this class doesn't reference PlayerAppController's input logic.
//
// ── Layer setup ──────────────────────────────────────────────────────────────
//
//   Set this GameObject's layer to the same layer as
//   PlayerAppController.visualSurfaceLayerMask.  Child collider GOs inherit
//   the layer from this GO.
//
// ── Frustum shape ────────────────────────────────────────────────────────────
//
//   Inner-arc vertices sit at local Z = FrustumHeightInner (default 0.001).
//   Outer-arc vertices sit at local Z = FrustumHeightOuter (default 0.15).
//   When frustumProfile is null or its UseFrustumProfile is false, both edges
//   default to a tiny flat Z (0.001) — no frustum shape, but collider still works.
//
// ── PhysX "cleaning the mesh failed" fix ─────────────────────────────────────
//
//   Each arena uses a COLLIDER-ONLY child GO (no MeshFilter).  Without a
//   MeshFilter Unity cannot auto-populate MeshCollider.sharedMesh at
//   AddComponent time, preventing the degenerate-mesh PhysX warning.
//
// ── Change detection ─────────────────────────────────────────────────────────
//
//   Nine geometry parameters (radii, center, arc, Z heights) are tracked per
//   arena.  Collider meshes are rebuilt in-place only when a parameter drifts
//   past a small epsilon — static charts incur no per-frame rebuild cost.
//
// ── Allocation policy ────────────────────────────────────────────────────────
//
//   Vertex scratch arrays and Mesh objects are pre-allocated once per arena and
//   reused every rebuild.  No per-frame heap allocation after initialization.
//
// Wiring (see §Manual setup at end of this file or in response):
//   1. Attach this component to a GO in the PlayerBoot scene.
//   2. Set the GO's layer to match PlayerAppController.visualSurfaceLayerMask.
//   3. Assign playerAppController and frustumProfile in the Inspector.
//   4. PlayerDebugArenaSurface no longer needs its collider path to be active.

using System.Collections.Generic;
using UnityEngine;
using RhythmicFlow.Shared;
using System;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production-safe arena surface collider provider for parallax-correct input (spec §5.2.1).
    ///
    /// <para>Maintains one <see cref="MeshCollider"/> per active arena, shaped to the
    /// cone/frustum surface.  Set this GameObject's layer to match
    /// <see cref="PlayerAppController.visualSurfaceLayerMask"/> and assign
    /// <see cref="playerAppController"/> and <see cref="frustumProfile"/> in the Inspector.</para>
    ///
    /// <para><see cref="PlayerDebugArenaSurface"/> may remain in the scene for debug mesh
    /// visualisation, but gameplay input no longer depends on it.</para>
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Input/ArenaColliderProvider")]
    public class ArenaColliderProvider : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector — Wiring
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("The PlayerAppController this provider reads arena geometry from.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Shared frustum profile used to shape the collider mesh Z heights. " +
                 "Assign the same profile as the production renderers so input surface matches visuals. " +
                 "If null, a flat Z = 0.001 is used (no frustum cone — input still works).")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        // -------------------------------------------------------------------
        // Inspector — Geometry
        // -------------------------------------------------------------------

        [Header("Geometry")]
        [Tooltip("Number of arc segments per ring-sector mesh. More = smoother collider. Default: 64.")]
        [SerializeField] private int arcSegments = 64;

        // -------------------------------------------------------------------
        // Per-arena collider state
        // -------------------------------------------------------------------

        // One instance per arena, created lazily on first encounter.
        private sealed class ArenaColliderState
        {
            // Child GO that holds the MeshCollider.  No MeshFilter on this GO —
            // prevents PhysX from auto-populating MeshCollider.sharedMesh from a
            // zero-vertex placeholder and logging "cleaning the mesh failed".
            public GameObject  ColliderGo;

            // Runtime mesh updated in-place every rebuild.  Shared with MeshCollider.
            public Mesh        Mesh;

            // The MeshCollider on ColliderGo.
            public MeshCollider Collider;

            // Pre-allocated vertex scratch: length = (arcSegments+1)*2.
            // Filled in-place during RebuildArenaCollider — no per-rebuild alloc.
            public Vector3[] VertexScratch;

            // True after the first successful RebuildArenaCollider call.
            // The MeshCollider.sharedMesh is only assigned once this is true,
            // preventing PhysX cooking of degenerate (zero-area) geometry.
            public bool HasValidGeometry;

            // Rate-limits the "skipping collider" warning to one log per arena.
            public bool HasLoggedSkipWarning;

            // Change-detection watermarks — initialized to float.MaxValue so the
            // very first LateUpdate always triggers a mesh build.
            public float LastOuterLocal       = float.MaxValue;
            public float LastInnerLocal       = float.MaxValue;
            public float LastVisualOuterLocal = float.MaxValue;
            public float LastCenterX          = float.MaxValue;
            public float LastCenterY          = float.MaxValue;
            public float LastArcStartDeg      = float.MaxValue;
            public float LastArcSweepDeg      = float.MaxValue;
            public float LastEffectiveZInner  = float.MaxValue;
            public float LastEffectiveZOuter  = float.MaxValue;
        }

        // Keyed by arenaId — one entry per arena encountered in the evaluator.
        private readonly Dictionary<string, ArenaColliderState> _states =
            new Dictionary<string, ArenaColliderState>(StringComparer.Ordinal);

        // Change-detection thresholds — same values as PlayerDebugArenaSurface.
        private const float GeomEpsilon  = 1e-5f;  // local-unit distance
        private const float AngleEpsilon = 0.01f;  // degrees

        // Once-only warning guards — prevent per-frame console spam on misconfiguration.
        private bool _hasWarnedMissingController;
        private bool _hasWarnedMissingProfile;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void OnEnable()
        {
            // Force a full rebuild on the next LateUpdate so that disabling then
            // re-enabling the component mid-session snaps colliders to current geometry.
            foreach (ArenaColliderState state in _states.Values)
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

        private void OnDestroy()
        {
            // Child GOs are destroyed automatically as scene children.
            // Mesh assets are separate runtime objects — explicitly destroy to prevent leaks.
            foreach (ArenaColliderState state in _states.Values)
            {
                if (state.Mesh != null) { Destroy(state.Mesh); }
            }
        }

        private void LateUpdate()
        {
            UpdateArenaColliders();
        }

        // -------------------------------------------------------------------
        // Per-frame collider update
        // -------------------------------------------------------------------

        // Iterates all arenas from the evaluator.  Creates collider state lazily on
        // first encounter.  Enables/disables the MeshCollider to match arena enabled
        // state.  Rebuilds collider mesh in-place only when geometry changes past
        // epsilon — avoids expensive MeshCollider cooking on every frame.
        private void UpdateArenaColliders()
        {
            if (playerAppController == null)
            {
                if (!_hasWarnedMissingController)
                {
                    _hasWarnedMissingController = true;
                    Debug.LogWarning("[ArenaColliderProvider] playerAppController is not assigned. " +
                                     "Visual-surface raycasts will fall back to the flat Z=0 plane.");
                }
                return;
            }

            ChartRuntimeEvaluator evaluator = playerAppController.Evaluator;
            PlayfieldTransform    pfT       = playerAppController.PlayfieldTf;
            Transform             pfRoot    = playerAppController.playfieldRoot;

            // Evaluator and pfT are null until PlayerAppController.Start() completes.
            if (evaluator == null || pfT == null || pfRoot == null) { return; }

            // Log once if frustumProfile is unassigned.  Colliders still build but
            // will be flat (no frustum tilt) — input still works.
            if (frustumProfile == null && !_hasWarnedMissingProfile)
            {
                _hasWarnedMissingProfile = true;
                Debug.LogWarning("[ArenaColliderProvider] frustumProfile is not assigned. " +
                                 "Collider meshes will use flat Z = 0.001 (no frustum cone shape). " +
                                 "Assign the same PlayfieldFrustumProfile used by the production renderers.");
            }

            int N         = Mathf.Max(1, arcSegments);
            int vertCount = (N + 1) * 2;

            for (int i = 0; i < evaluator.ArenaCount; i++)
            {
                EvaluatedArena ea = evaluator.GetArena(i);
                if (string.IsNullOrEmpty(ea.ArenaId)) { continue; }

                // Lazy-create state on first encounter.
                if (!_states.TryGetValue(ea.ArenaId, out ArenaColliderState state))
                {
                    state = CreateArenaColliderState(ea.ArenaId, vertCount);
                    _states[ea.ArenaId] = state;
                }
                else if (state.VertexScratch.Length != vertCount)
                {
                    // arcSegments was changed at runtime (Inspector tweak) — recreate.
                    Destroy(state.Mesh);
                    if (state.ColliderGo != null) { Destroy(state.ColliderGo); }
                    state = CreateArenaColliderState(ea.ArenaId, vertCount);
                    _states[ea.ArenaId] = state;
                }

                // ── Enable/disable collider driven by arena enabled state ─────────────────
                // Disabled arenas receive no hit-testing (spec §5.6); disabling the
                // MeshCollider prevents spurious raycasts from reaching them.
                bool arenaEnabled   = ea.EnabledBool;
                state.Collider.enabled = arenaEnabled && state.HasValidGeometry;

                // Skip geometry updates for disabled arenas — mesh doesn't need to
                // be current since no raycasts can hit a disabled collider.
                if (!arenaEnabled) { continue; }

                // ── Derive geometry parameters ────────────────────────────────────────────
                // Identical derivation to PlayerDebugArenaSurface so the collider
                // surface exactly matches the visual surface.
                float outerLocal = pfT.NormRadiusToLocal(ea.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(ea.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Extend the outer edge by the same visual rim used by the debug surface
                // so input registration matches the visible arena boundary.
                float visualOuterLocal = outerLocal
                    + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;

                // Arena center in PlayfieldRoot local XY (spec §5.5).
                Vector2 center = pfT.NormalizedToLocal(new Vector2(ea.CenterXNorm, ea.CenterYNorm));

                // Z heights from the shared frustum profile.  Same fallback logic as the
                // production renderers: if no profile (or profile disabled), use flat 0.001.
                bool   useProfile = frustumProfile != null && frustumProfile.UseFrustumProfile;
                float  zInner     = useProfile ? frustumProfile.FrustumHeightInner : 0.001f;
                float  zOuter     = useProfile ? frustumProfile.FrustumHeightOuter : 0.001f;

                // ── Change detection ──────────────────────────────────────────────────────
                // Compare all nine derived vertex parameters against their watermarks.
                // DeltaAngle handles arc angles near 0°/360° correctly.
                bool changed =
                    Mathf.Abs(outerLocal       - state.LastOuterLocal)       > GeomEpsilon  ||
                    Mathf.Abs(innerLocal       - state.LastInnerLocal)       > GeomEpsilon  ||
                    Mathf.Abs(visualOuterLocal - state.LastVisualOuterLocal) > GeomEpsilon  ||
                    Mathf.Abs(center.x         - state.LastCenterX)          > GeomEpsilon  ||
                    Mathf.Abs(center.y         - state.LastCenterY)          > GeomEpsilon  ||
                    Mathf.Abs(Mathf.DeltaAngle(ea.ArcStartDeg, state.LastArcStartDeg)) > AngleEpsilon ||
                    Mathf.Abs(ea.ArcSweepDeg   - state.LastArcSweepDeg)     > AngleEpsilon ||
                    Mathf.Abs(zInner           - state.LastEffectiveZInner)  > GeomEpsilon  ||
                    Mathf.Abs(zOuter           - state.LastEffectiveZOuter)  > GeomEpsilon;

                if (!changed) { continue; }

                // ── Rebuild collider mesh in-place ────────────────────────────────────────
                RebuildArenaCollider(state, ea.ArenaId,
                    ea.ArcStartDeg, ea.ArcSweepDeg,
                    center, innerLocal, visualOuterLocal,
                    zInner, zOuter, N, pfRoot);

                // Re-evaluate enable now that HasValidGeometry may have flipped.
                state.Collider.enabled = arenaEnabled && state.HasValidGeometry;

                // ── Update watermarks ─────────────────────────────────────────────────────
                state.LastOuterLocal       = outerLocal;
                state.LastInnerLocal       = innerLocal;
                state.LastVisualOuterLocal = visualOuterLocal;
                state.LastCenterX          = center.x;
                state.LastCenterY          = center.y;
                state.LastArcStartDeg      = ea.ArcStartDeg;
                state.LastArcSweepDeg      = ea.ArcSweepDeg;
                state.LastEffectiveZInner  = zInner;
                state.LastEffectiveZOuter  = zOuter;
            }
        }

        // -------------------------------------------------------------------
        // Mesh rebuild
        // -------------------------------------------------------------------

        // Fills VertexScratch in-place from the supplied geometry, then assigns the
        // array to the Mesh and refreshes the MeshCollider.
        //
        // Vertex layout (same as PlayerDebugArenaSurface):
        //   indices 0..N      → inner arc  (innerLocal radius, Z = zInner)
        //   indices N+1..2N+1 → outer arc  (visualOuterLocal,  Z = zOuter)
        //
        // Conversion pipeline per vertex:
        //   1. (localX, localY, localZ) in PlayfieldRoot space.
        //   2. worldPt  = pfRoot.TransformPoint(x, y, z)
        //   3. meshPt   = ColliderGo.transform.InverseTransformPoint(worldPt)
        // Step 3 is needed because the mesh is in ColliderGo's local space, not
        // in pfRoot's local space.
        //
        // MeshCollider.sharedMesh is only assigned when IsGeometryValid() passes,
        // preventing the PhysX "cleaning the mesh failed" warning on degenerate input.
        private void RebuildArenaCollider(
            ArenaColliderState state,
            string  arenaId,
            float   arcStartDeg, float arcSweepDeg,
            Vector2 center,
            float   innerLocal, float visualOuterLocal,
            float   zInner, float zOuter,
            int N, Transform pfRoot)
        {
            for (int i = 0; i <= N; i++)
            {
                float t   = (float)i / N;
                float deg = arcStartDeg + t * arcSweepDeg;
                float rad = deg * Mathf.Deg2Rad;
                var   dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

                Vector2 innerPt = center + dir * innerLocal;
                Vector2 outerPt = center + dir * visualOuterLocal;

                Vector3 wInner = pfRoot.TransformPoint(innerPt.x, innerPt.y, zInner);
                Vector3 wOuter = pfRoot.TransformPoint(outerPt.x, outerPt.y, zOuter);

                state.VertexScratch[i]         = state.ColliderGo.transform.InverseTransformPoint(wInner);
                state.VertexScratch[N + 1 + i] = state.ColliderGo.transform.InverseTransformPoint(wOuter);
            }

            state.Mesh.vertices = state.VertexScratch;
            state.Mesh.RecalculateBounds();

            bool valid = IsGeometryValid(state, arenaId, innerLocal, visualOuterLocal, arcSweepDeg, N);
            state.HasValidGeometry = valid;

            if (valid)
            {
                // Setting sharedMesh = null first forces Unity to rebuild the internal BVH
                // from the updated vertex data rather than reusing a stale cached version.
                state.Collider.sharedMesh = null;
                state.Collider.sharedMesh = state.Mesh;
            }
        }

        // -------------------------------------------------------------------
        // Geometry validation
        // -------------------------------------------------------------------

        // Lightweight check for the conditions most likely to cause PhysX to fail mesh
        // cooking — cheapest tests first to avoid iterating all vertices.
        private bool IsGeometryValid(
            ArenaColliderState state, string arenaId,
            float innerLocal, float visualOuterLocal, float arcSweepDeg, int N)
        {
            if (arcSweepDeg < 0.1f)
            {
                LogSkipOnce(state, arenaId, $"arcSweepDeg={arcSweepDeg:F3} < 0.1 — degenerate sweep");
                return false;
            }
            if (innerLocal <= 0f)
            {
                LogSkipOnce(state, arenaId, $"innerLocal={innerLocal:F4} <= 0 — degenerate ring");
                return false;
            }
            if (visualOuterLocal <= innerLocal)
            {
                LogSkipOnce(state, arenaId,
                    $"visualOuterLocal={visualOuterLocal:F4} <= innerLocal={innerLocal:F4}");
                return false;
            }

            // Spot-check first inner and first outer vertex for NaN/Infinity
            // (catches a pfRoot with an invalid transform).
            Vector3 v0 = state.VertexScratch[0];
            Vector3 v1 = state.VertexScratch[N + 1];
            if (HasNaNOrInfinity(v0) || HasNaNOrInfinity(v1))
            {
                LogSkipOnce(state, arenaId,
                    $"NaN/Infinity detected in vertices (v[0]={v0}, v[{N + 1}]={v1})");
                return false;
            }

            return true;
        }

        private static bool HasNaNOrInfinity(Vector3 v)
        {
            return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);
        }

        // Logs a skip warning at most once per arena per session to avoid console spam.
        private static void LogSkipOnce(ArenaColliderState state, string arenaId, string reason)
        {
            if (state.HasLoggedSkipWarning) { return; }
            state.HasLoggedSkipWarning = true;
            Debug.LogWarning($"[ArenaColliderProvider] Skipping collider update for '{arenaId}': " +
                             $"{reason}. Will retry once geometry becomes valid.");
        }

        // -------------------------------------------------------------------
        // Lazy state creation
        // -------------------------------------------------------------------

        // Creates the collider child GO, Mesh, and pre-allocated vertex scratch for
        // one arena.  Triangle indices are computed once here and never change.
        //
        // CRITICAL: ColliderGo has NO MeshFilter.  If a MeshFilter were present,
        // Unity would auto-populate MeshCollider.sharedMesh at AddComponent time
        // from the filter's (zero-vertex) placeholder, triggering the PhysX warning.
        // Without a MeshFilter there is nothing to auto-populate from.
        private ArenaColliderState CreateArenaColliderState(string arenaId, int vertCount)
        {
            int N = (vertCount / 2) - 1; // inverse of: vertCount = (N+1)*2

            // --- Triangle indices (ring-sector quad strip — static topology) ---
            // Winding: CCW from +localZ (same as PlayerDebugArenaSurface):
            //   Tri 1: inner[i] → outer[i]   → inner[i+1]
            //   Tri 2: outer[i] → outer[i+1] → inner[i+1]
            var triangles = new int[N * 6]; // N quads × 2 triangles × 3 indices
            for (int i = 0; i < N; i++)
            {
                int ti  = i * 6;
                int iI  = i;
                int iI1 = i + 1;
                int oI  = N + 1 + i;
                int oI1 = N + 1 + i + 1;

                triangles[ti + 0] = iI;
                triangles[ti + 1] = oI;
                triangles[ti + 2] = iI1;

                triangles[ti + 3] = oI;
                triangles[ti + 4] = oI1;
                triangles[ti + 5] = iI1;
            }

            // --- Mesh (zeroed placeholder vertices — overwritten on first rebuild) ---
            var mesh = new Mesh
            {
                name      = $"ArenaCollider_{arenaId}",
                hideFlags = HideFlags.HideAndDontSave,
            };
            mesh.vertices  = new Vector3[vertCount];
            mesh.triangles = triangles;
            mesh.RecalculateBounds();

            // --- Collider child GO — NO MeshFilter --------------------------------
            var colliderGo = new GameObject($"ArenaCollider_{arenaId}");
            colliderGo.transform.SetParent(transform, worldPositionStays: false);
            // Inherit the layer from this GO so raycasts against visualSurfaceLayerMask hit it.
            colliderGo.layer = gameObject.layer;

            // AddComponent is safe: no MeshFilter on this GO → no auto-population.
            var mc = colliderGo.AddComponent<MeshCollider>();
            mc.enabled    = false;  // disabled until HasValidGeometry = true
            mc.sharedMesh = null;   // explicit for clarity

            return new ArenaColliderState
            {
                ColliderGo           = colliderGo,
                Mesh                 = mesh,
                Collider             = mc,
                VertexScratch        = new Vector3[vertCount],
                HasValidGeometry     = false,
                HasLoggedSkipWarning = false,
                // Watermarks use float.MaxValue defaults from field initializers,
                // guaranteeing RebuildArenaCollider on the very first LateUpdate.
            };
        }
    }
}
