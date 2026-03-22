// HoldBodyRenderer.cs
// Scroll-style hold body renderer — behaves like an ArcCreate long note.
//
// ══════════════════════════════════════════════════════════════════════
//  APPROACH FORMULA  (shared with PlayerDebugRenderer §6.1)
//
//   timeToHitMs = eventTimeMs − chartTimeMs
//   alpha       = 1 − Clamp01( timeToHitMs / noteLeadTimeMs )
//   r           = Lerp( spawnR, judgementR, alpha )
//
//   alpha = 0  →  r = spawnR      (event far in the future)
//   alpha = 1  →  r = judgementR  (event at/past current time)
//
//   Negative timeToHitMs → Clamp01=0 → alpha=1 → r=judgementR.
//   Head naturally pins at judgementR once startTimeMs passes.
// ══════════════════════════════════════════════════════════════════════
//
//  THREE PHASES OF A HOLD NOTE
//
//   A)  Before start  (chartTime < startTimeMs)
//       Head and tail both approach; ribbon spans tail→head.
//
//   B)  During hold  (startTimeMs ≤ chartTime ≤ endTimeMs)
//       headR = judgementR (pinned by Clamp01).
//       tailR still approaching → ribbon shrinks (consumption).
//
//   C)  After end  (chartTime > endTimeMs + greatWindowMs)
//       Ribbon hidden (ComputeHoldEndpointsR returns visible=false).
//
// ══════════════════════════════════════════════════════════════════════
//  Z COMPUTATION — FRUSTUM SURFACE ALIGNMENT
//
//   The debug hold rail (PlayerDebugRenderer) places each endpoint in
//   PlayfieldRoot local XY, then lifts it in local Z using:
//
//     s01   = Clamp01( (r − innerLocal) / (outerLocal − innerLocal) )
//     localZ = Lerp( frustumHeightInner, frustumHeightOuter, s01 )
//            ≈ Lerp( 0.001, 0.15, s01 )
//
//   ComputeEndpointLocalZ() replicates this formula so the ribbon
//   endpoints land on the same frustum surface as the debug rail.
//
// ══════════════════════════════════════════════════════════════════════
//  TRAPEZOID WIDTH — MATCHING THE FRUSTUM LANE BORDERS
//
//   Lane borders are radial lines at centerDeg ± widthDeg/2.
//   The chord (straight-line width) between those borders at radius r is:
//
//       width(r) = 2 · r · sin( widthDeg/2 · Deg2Rad )
//
//   Head and tail are at different radii, so the ribbon is a TRAPEZOID:
//       widthHead = 2 · headR · sin(halfWidthDeg) · holdLaneWidthRatio
//       widthTail = 2 · tailR · sin(halfWidthDeg) · holdLaneWidthRatio
//
//   Vertex layout (PlayfieldRoot local space):
//       [0] tail-left   = tailLocal3 − tangLocal × (widthTail / 2)
//       [1] tail-right  = tailLocal3 + tangLocal × (widthTail / 2)
//       [2] head-right  = headLocal3 + tangLocal × (widthHead / 2)
//       [3] head-left   = headLocal3 − tangLocal × (widthHead / 2)
//
//   A pool of pre-allocated Meshes (MaxHoldPool) is filled in Awake.
//   LateUpdate writes vertices into the mesh in-place each frame (no
//   per-frame GC allocation). Graphics.DrawMesh receives
//   pfRoot.localToWorldMatrix as the model→world transform.
//
// Spec §5.7.1.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Runtime visual ribbon renderer for Hold note bodies (scroll/long-note style).
    /// Draws trapezoid meshes in PlayfieldRoot-local space; promotes to world via localToWorldMatrix.
    /// Attach to any GameObject in the Player scene; assign PlayerAppController and a hold Material.
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/HoldBodyRenderer")]
    public class HoldBodyRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector — Wiring
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("The PlayerAppController this renderer reads notes and geometry from.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Material for hold-body ribbons. Use an unlit / additive shader with _Color support.")]
        [SerializeField] private Material holdMaterial;

        // -------------------------------------------------------------------
        // Inspector — Frustum surface alignment
        // -------------------------------------------------------------------

        [Header("Frustum Surface Alignment")]
        [Tooltip("Optional: assign the PlayerDebugArenaSurface in the scene. " +
                 "When set, frustum height values are read automatically from it (matching " +
                 "the debug hold-rail exactly). If null, the manual values below are used.")]
        [SerializeField] private PlayerDebugArenaSurface arenaSurface;

        [Tooltip("When true and no arenaSurface is assigned, the ribbon endpoints are lifted " +
                 "onto the frustum cone surface using the manual height values below. " +
                 "Set to false to use a flat ribbon at surfaceOffsetLocal.")]
        [SerializeField] private bool useFrustumProfile = true;

        [Tooltip("Local Z at the inner ring edge (matches PlayerDebugArenaSurface.frustumHeightInner). " +
                 "Only used when arenaSurface is not assigned. Default: 0.001.")]
        [SerializeField] private float frustumHeightInner = 0.001f;

        [Tooltip("Local Z at the outer ring edge (matches PlayerDebugArenaSurface.frustumHeightOuter). " +
                 "Only used when arenaSurface is not assigned. Default: 0.15.")]
        [SerializeField] private float frustumHeightOuter = 0.15f;

        [Tooltip("Additional Z offset added on top of the computed frustum height to prevent " +
                 "z-fighting when useFrustumProfile is false. Ignored when frustum profile is active " +
                 "(frustumHeightInner already provides z-fight clearance from z=0).")]
        [SerializeField] private float surfaceOffsetLocal = 0.002f;

        // -------------------------------------------------------------------
        // Inspector — Phase colors
        // -------------------------------------------------------------------

        [Header("Phase Colors")]
        [Tooltip("Ribbon color while the hold is approaching (before startTimeMs is reached).")]
        [SerializeField] private Color holdColorApproaching = new Color(0.3f, 0.6f, 1f, 0.75f);

        [Tooltip("Ribbon color while the hold is actively being held (HoldBind == Bound). Typically brighter.")]
        [SerializeField] private Color holdColorActive = new Color(0.5f, 0.85f, 1f, 1.0f);

        [Tooltip("Ribbon color when the hold is in a failed/non-judging state:\n" +
                 "  • Released early  (HoldBind == Finished, still before endTimeMs)\n" +
                 "  • Missed start    (NoteState == Missed — never bound)\n\n" +
                 "The ribbon keeps shrinking geometrically until endTimeMs, using this dim color\n" +
                 "to signal 'no longer scoring'. Distinct from holdColorApproaching/Active.\n" +
                 "Default: dim semi-transparent red.")]
        [SerializeField] private Color holdColorReleased = new Color(0.8f, 0.2f, 0.2f, 0.55f);

        // -------------------------------------------------------------------
        // Inspector — Approach
        // -------------------------------------------------------------------

        [Header("Approach")]
        [Tooltip("How many ms before StartTimeMs the ribbon first becomes visible.\n\n" +
                 "MUST match noteLeadTimeMs in PlayerDebugRenderer (default 2000) so that:\n" +
                 "  alpha = 1 − Clamp01(headToHit / noteLeadTimeMs)\n" +
                 "gives alpha=0 (spawn at innerLocal) on the first visible frame.\n\n" +
                 "With noteLeadTimeMs=2000 and ActivationLeadMs=5000 (in PlayerAppController),\n" +
                 "notes become Active 5000 ms before startTimeMs, but only VISIBLE for the\n" +
                 "last 2000 ms — so alpha=0 (innerLocal) is always the first rendered position.\n\n" +
                 "BUG: if this is set higher than noteLeadTimeMs in PlayerDebugRenderer (e.g. 5000),\n" +
                 "notes appear mid-approach on the first frame whenever startTimeMs - t0 < noteLeadTimeMs\n" +
                 "(i.e. the song starts and the hold is already partway through its approach window).")]
        [SerializeField] private int noteLeadTimeMs = 2000;

        [Tooltip("Spawn radius as a fraction of the approach path from inner arc to judgement ring. " +
                 "0 = spawn at inner arc (v0 default — holds first appear at the inner band edge " +
                 "and travel outward). 1 = spawn at judgement ring (no travel). Keep at 0 for v0.")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnRadiusFactor = 0f;

        // -------------------------------------------------------------------
        // Inspector — Ribbon sizing
        // -------------------------------------------------------------------

        [Header("Ribbon Sizing")]
        [Tooltip("Hold ribbon width as a fraction of the lane's chord width at each endpoint radius. " +
                 "Chord formula: width(r) = 2 · r · sin(laneAngularWidth/2). " +
                 "The ribbon is a trapezoid: tail and head have different widths because they are " +
                 "at different radii. 1.0 = full lane width; 0.7 is recommended.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float holdLaneWidthRatio = 0.7f;

        // -------------------------------------------------------------------
        // Inspector — Debug
        // -------------------------------------------------------------------

        [Header("Debug")]
        [Tooltip("Draws Debug.DrawLine between the ribbon's two center endpoints in world space. " +
                 "Use this to compare the ribbon centerline against the debug hold rail.")]
        [SerializeField] private bool debugDrawEndpoints = false;

        [Tooltip("Draws the ribbon mesh trapezoid outline (4 edges + centerline) in world space. " +
                 "Verifies that the mesh exactly covers the expected trapezoid area.")]
        [SerializeField] private bool debugDrawMeshOutline = true;

        [Tooltip("Logs approach values once per second for every currently visible hold.\n" +
                 "Output: spawnRadiusFactor, noteLeadTimeMs, chartTimeMs, headToHitMs, alphaHead,\n" +
                 "        innerLocalRadius, judgementR, spawnR, headR, tailR.\n" +
                 "Use this to confirm alphaHead≈0 on the first frame a hold becomes visible.\n" +
                 "Enable Gizmos in the Game view to also see Debug.DrawLine overlays.")]
        [SerializeField] private bool debugLogSpawnOncePerSecond = false;

        [Tooltip("Draws reference arcs and position ticks for each visible hold:\n" +
                 "  Magenta arc  — innerLocal radius (actual inner band edge).\n" +
                 "  Green arc    — spawnR (where hold tail/head spawn at alpha=0).\n" +
                 "                 With spawnRadiusFactor=0, green should overlap magenta.\n" +
                 "  Cyan tick    — headR position along lane centre.\n" +
                 "  Yellow tick  — tailR position along lane centre.\n" +
                 "Requires Gizmos enabled in Game view to be visible.")]
        [SerializeField] private bool debugDrawSpawnArcs = false;

        // -------------------------------------------------------------------
        // Internals — mesh pool
        // -------------------------------------------------------------------

        // Maximum simultaneous hold notes that can be rendered. Meshes are allocated
        // once in Awake and reused every frame (vertices written in-place, no GC alloc).
        private const int MaxHoldPool = 64;

        private Mesh[]   _meshPool;   // pool of 4-vertex trapezoid meshes
        private int      _poolUsed;   // reused count for current frame, reset each LateUpdate

        // Single scratch array written before updating each pool mesh.
        // Shared across all hold iterations (sequential, not concurrent).
        private readonly Vector3[] _vertScratch = new Vector3[4];

        // Reused every DrawMesh call — no per-frame allocation.
        // Must be created in Awake; Unity forbids engine-object ctor in field initializers.
        private MaterialPropertyBlock _propBlock;

        // Countdown for debugLogSpawnOncePerSecond: fires when ≤ 0, then resets to 1s.
        private float _debugLogTimer;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // Pre-allocate the mesh pool. Each mesh has 4 vertices set up for
            // a trapezoid quad. Triangles and UVs are set once here and never change.
            _meshPool = new Mesh[MaxHoldPool];
            for (int i = 0; i < MaxHoldPool; i++)
            {
                _meshPool[i] = BuildTrapezoidMesh();
            }

            _propBlock = new MaterialPropertyBlock();
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
            if (playerAppController == null || holdMaterial == null || _meshPool == null) { return; }

            var allNotes = playerAppController.NotesAll;
            if (allNotes == null) { return; }

            var arenaGeos   = playerAppController.ArenaGeometries;
            var laneGeos    = playerAppController.LaneGeometries;
            var laneToArena = playerAppController.LaneToArena;
            var pfTf        = playerAppController.PlayfieldTf;
            Transform pfRoot = playerAppController.playfieldRoot;

            if (arenaGeos == null || laneGeos == null || laneToArena == null
                || pfTf == null || pfRoot == null) { return; }

            double chartTimeMs   = playerAppController.EffectiveChartTimeMs;
            double greatWindowMs = playerAppController.GreatWindowMs;

            // localToWorld is the model→world matrix passed to Graphics.DrawMesh.
            // Vertices are written in PlayfieldRoot local space, so this promotes them correctly.
            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            // ── Throttle for debugLogSpawnOncePerSecond ───────────────────────────────
            // Decrement once per LateUpdate (not once per note) so the timer is frame-rate agnostic.
            bool logThisFrame = false;
            if (debugLogSpawnOncePerSecond)
            {
                _debugLogTimer -= Time.deltaTime;
                if (_debugLogTimer <= 0f)
                {
                    _debugLogTimer = 1f;   // fire, then reset to 1-second cooldown
                    logThisFrame   = true;
                }
            }

            // Reset pool usage counter — reuse all slots from the top each frame.
            _poolUsed = 0;

            foreach (RuntimeNote note in allNotes)
            {
                if (note.Type != NoteType.Hold) { continue; }

                // ── Lifecycle filter ──────────────────────────────────────────────────────
                //
                // VISUAL LIFETIME vs JUDGING ELIGIBILITY are decoupled (spec §5.7.1):
                //
                //   NoteState.Hit    → successfully completed (all ticks scored, or hold bound
                //                      and EvaluateHoldTicks closed it). Visual done. SKIP.
                //
                //   NoteState.Missed → missed start OR swept after early release.
                //                      Judging stopped, but the hold body stays visible until
                //                      endTimeMs so the player can see where they went wrong.
                //                      ComputeHoldEndpointsR handles the endTimeMs cutoff:
                //                        head pins at judgementR (headToHit < 0 → alpha = 1),
                //                        tail shrinks to judgementR at endTimeMs (degenerate → hidden).
                //                      KEEP RENDERING (dim color applied in Step 3 below).
                //
                //   NoteState.Active
                //     HoldBind.Unbound   → approaching, not yet hittable.
                //     HoldBind.Bound     → being held, ticks scoring.
                //     HoldBind.Finished  → released early; still Active until SweepMissed fires.
                //                          Stays visible, rendered dim (same as Missed).
                //
                if (note.State == NoteState.Hit) { continue; }

                // Pool exhausted — more holds than MaxHoldPool are simultaneously visible.
                if (_poolUsed >= MaxHoldPool) { break; }

                // Look up geometry for this note's lane and arena.
                if (!laneGeos.TryGetValue(note.LaneId,    out LaneGeometry  lane))  { continue; }
                if (!laneToArena.TryGetValue(note.LaneId, out string arenaId))      { continue; }
                if (!arenaGeos.TryGetValue(arenaId,       out ArenaGeometry arena)) { continue; }

                // ── Step 1: Compute band radii (PlayfieldRoot local units) ───────────────

                float outerLocal = pfTf.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfTf.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // judgementR: visual inset from the chart outer edge. VISUAL ONLY.
                float judgementR = outerLocal
                    - PlayerSettingsStore.JudgementInsetNorm * pfTf.MinDimLocal;

                // spawnR: where notes first appear.
                // spawnRadiusFactor=0 (v0 default) → spawn at inner arc.
                float spawnR = innerLocal + spawnRadiusFactor * (judgementR - innerLocal);

                // ── Step 2: Map start/end times to local radii (with visibility check) ──

                ComputeHoldEndpointsR(
                    note.StartTimeMs, note.EndTimeMs,
                    chartTimeMs, greatWindowMs,
                    spawnR, judgementR,
                    out float headR, out float tailR, out bool visible);

                if (!visible) { continue; }

                // ── debugLogSpawnOncePerSecond ─────────────────────────────────────────────
                // Log once per second whenever a hold is visible, to diagnose spawn radius.
                // Output confirms:  H1 = alphaHead near 0 on first visible frame
                //                   H2 = innerLocalRadius matches the debug inner arc
                //                   H3 = spawnRadiusFactor is the Inspector value, not overwritten
                if (logThisFrame)
                {
                    double headToHitForLog = note.StartTimeMs - chartTimeMs;
                    float  alphaHead = (noteLeadTimeMs > 0)
                        ? 1f - Mathf.Clamp01((float)headToHitForLog / noteLeadTimeMs)
                        : 1f;

                    // VISIBILITY GATE TRACE:
                    //   scheduler activates notes 5000ms before PrimaryTimeMs (ActivationLeadMs).
                    //   HoldBodyRenderer shows them when headToHit <= noteLeadTimeMs.
                    //   With noteLeadTimeMs=2000 the first visible frame always has alphaHead=0
                    //   → headR=spawnR=innerLocal.  If alphaHead > 0.01 on first render the
                    //   mismatch is exposed here.
                    Debug.Log(
                        $"[HoldBodyRenderer] note={note.NoteId}" +
                        $"\n  spawnRadiusFactor={spawnRadiusFactor:F3}  noteLeadTimeMs={noteLeadTimeMs}" +
                        $"\n  chartTimeMs={chartTimeMs:F0}  headToHitMs={headToHitForLog:F0}" +
                        $"\n  alphaHead={alphaHead:F3}  (0=at spawn, 1=at judgement)" +
                        $"\n  innerLocal={innerLocal:F4}  judgementR={judgementR:F4}  spawnR={spawnR:F4}" +
                        $"\n  headR={headR:F4}  tailR={tailR:F4}");
                }

                // Degenerate: skip to avoid divide-by-zero or zero-area mesh.
                if (headR - tailR < 0.0001f) { continue; }

                // ── Step 3: Phase-based color ─────────────────────────────────────────────
                //
                // State / HoldBind → color mapping:
                //
                //   Missed (any HoldBind)           → holdColorReleased  (dim: failed/non-judging)
                //   Active + HoldBind.Bound         → holdColorActive    (bright: scoring)
                //   Active + HoldBind.Finished      → holdColorReleased  (dim: released early, pre-sweep)
                //   Active + HoldBind.Unbound       → holdColorApproaching (approaching)
                //
                // HoldBind.Finished + State.Hit is already filtered above (never reaches here).

                Color ribbonColor;
                if (note.State == NoteState.Missed)
                {
                    // Missed start, or released early and swept — non-judging ghost.
                    ribbonColor = holdColorReleased;
                }
                else
                {
                    switch (note.HoldBind)
                    {
                        case HoldBindState.Bound:    ribbonColor = holdColorActive;     break;
                        case HoldBindState.Finished: ribbonColor = holdColorReleased;   break;
                        default:                     ribbonColor = holdColorApproaching; break;
                    }
                }
                _propBlock.SetColor("_Color", ribbonColor);

                // ── Step 4: Local-space axes ──────────────────────────────────────────────

                float thetaRad = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                float cosT     = Mathf.Cos(thetaRad);
                float sinT     = Mathf.Sin(thetaRad);

                // dirLocal: unit radial-outward vector in local XY — the strip length axis.
                // (Not used to build the matrix here, but useful to understand the coordinate frame.)

                // tangLocal: 90° CCW from radial direction, in local XY — the width axis.
                // Cross(localZ, dir) = Cross((0,0,1), (cosT,sinT,0)) = (-sinT, cosT, 0).
                var tangLocal = new Vector3(-sinT, cosT, 0f);

                // ── Step 5: Compute 3D local-space endpoints (XY + frustum Z) ────────────

                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // Tail: the inner (younger) end of the hold ribbon. Y=0 in mesh indexing.
                var tailLocal3 = new Vector3(
                    ctr.x + tailR * cosT,
                    ctr.y + tailR * sinT,
                    ComputeEndpointLocalZ(tailR, innerLocal, outerLocal));

                // Head: the outer (older) end pinned at judgementR during the hold. Y=1 in mesh indexing.
                var headLocal3 = new Vector3(
                    ctr.x + headR * cosT,
                    ctr.y + headR * sinT,
                    ComputeEndpointLocalZ(headR, innerLocal, outerLocal));

                // ── Step 6: Trapezoid width at each endpoint ──────────────────────────────
                //
                // Lane borders are radial lines at centerDeg ± widthDeg/2.
                // The chord between those lines at radius r is:
                //   width(r) = 2 · r · sin( widthDeg/2 · Deg2Rad )
                //
                // Because head and tail are at different radii, the ribbon is a trapezoid.

                float halfWidthDeg = lane.WidthDeg * 0.5f;
                float widthHead    = ComputeLaneWidthAtRadiusLocal(headR, halfWidthDeg) * holdLaneWidthRatio;
                float widthTail    = ComputeLaneWidthAtRadiusLocal(tailR, halfWidthDeg) * holdLaneWidthRatio;

                // ── Step 7: Build 4 local-space trapezoid vertices ────────────────────────
                //
                // tangLocal has z=0, so offset only affects XY. Each vertex inherits the
                // endpoint's Z from tailLocal3 or headLocal3 (frustum-lifted Z).
                //
                // Mesh vertex index layout (matches BuildTrapezoidMesh triangles):
                //   [0] tail-left   (Y=0 end, −tangent side)
                //   [1] tail-right  (Y=0 end, +tangent side)
                //   [2] head-right  (Y=1 end, +tangent side)
                //   [3] head-left   (Y=1 end, −tangent side)

                _vertScratch[0] = tailLocal3 - tangLocal * (widthTail * 0.5f);  // tail-left
                _vertScratch[1] = tailLocal3 + tangLocal * (widthTail * 0.5f);  // tail-right
                _vertScratch[2] = headLocal3 + tangLocal * (widthHead * 0.5f);  // head-right
                _vertScratch[3] = headLocal3 - tangLocal * (widthHead * 0.5f);  // head-left

                // Write vertices into the pooled mesh. Triangles and UVs are already set.
                Mesh trapMesh = _meshPool[_poolUsed++];
                trapMesh.vertices = _vertScratch;
                trapMesh.RecalculateBounds(); // required for correct frustum culling

                // ── Step 8: Debug visualizations ─────────────────────────────────────────

                if (debugDrawEndpoints || debugDrawMeshOutline)
                {
                    if (debugDrawEndpoints)
                    {
                        // Centerline connecting tail and head midpoints — compare against debug hold rail.
                        Vector3 tailWorld = pfRoot.TransformPoint(tailLocal3);
                        Vector3 headWorld = pfRoot.TransformPoint(headLocal3);
                        Debug.DrawLine(tailWorld, headWorld, ribbonColor);
                    }

                    if (debugDrawMeshOutline)
                    {
                        // 4 trapezoid corners in world space — should outline the drawn mesh exactly.
                        Vector3 p0 = pfRoot.TransformPoint(_vertScratch[0]); // tail-left
                        Vector3 p1 = pfRoot.TransformPoint(_vertScratch[1]); // tail-right
                        Vector3 p2 = pfRoot.TransformPoint(_vertScratch[2]); // head-right
                        Vector3 p3 = pfRoot.TransformPoint(_vertScratch[3]); // head-left

                        Color outlineColor = Color.cyan;
                        Debug.DrawLine(p0, p1, outlineColor); // tail edge
                        Debug.DrawLine(p2, p3, outlineColor); // head edge
                        Debug.DrawLine(p0, p3, outlineColor); // left edge
                        Debug.DrawLine(p1, p2, outlineColor); // right edge

                        // Centerline (yellow) — compare against the debug hold rail.
                        Vector3 tailCenter = pfRoot.TransformPoint(tailLocal3);
                        Vector3 headCenter = pfRoot.TransformPoint(headLocal3);
                        Debug.DrawLine(tailCenter, headCenter, Color.yellow);
                    }
                }

                // Draw the trapezoid mesh. Vertices are in PlayfieldRoot local space;
                // localToWorld promotes them to world space for rendering.
                Graphics.DrawMesh(
                    trapMesh, localToWorld, holdMaterial,
                    gameObject.layer, null, 0, _propBlock);

                // ── debugDrawSpawnArcs ─────────────────────────────────────────────────────
                // Reference arcs that let you visually verify spawnR == innerLocal when factor=0.
                //
                //   Magenta  — innerLocal (actual inner band edge).
                //   Green    — spawnR    (should overlap magenta when spawnRadiusFactor=0).
                //   Cyan     — headR position tick along the lane centre.
                //   Yellow   — tailR position tick along the lane centre.
                if (debugDrawSpawnArcs)
                {
                    float halfWidthDegs = lane.WidthDeg * 0.5f;

                    // Inner arc (magenta) — the authoritative inner edge used by debug renderer.
                    DrawDebugArc(pfRoot, ctr, innerLocal, lane.CenterDeg, halfWidthDegs,
                        ComputeEndpointLocalZ(innerLocal, innerLocal, outerLocal), Color.magenta);

                    // Spawn arc (green) — where head/tail sit at alpha=0.
                    // Overlap with magenta confirms spawnRadiusFactor=0 is working.
                    DrawDebugArc(pfRoot, ctr, spawnR, lane.CenterDeg, halfWidthDegs,
                        ComputeEndpointLocalZ(spawnR, innerLocal, outerLocal), Color.green);

                    // Head position tick (cyan) — current headR along lane centre.
                    DrawDebugRadialTick(pfRoot, ctr, headR, lane.CenterDeg,
                        innerLocal, outerLocal, Color.cyan);

                    // Tail position tick (yellow) — current tailR along lane centre.
                    DrawDebugRadialTick(pfRoot, ctr, tailR, lane.CenterDeg,
                        innerLocal, outerLocal, Color.yellow);
                }
            }
        }

        // -------------------------------------------------------------------
        // Hold endpoint radii
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes headR (outer) and tailR (inner) in PlayfieldLocal units.
        /// Consumption: headR pins to judgementR once chartTime ≥ startTimeMs (Clamp01 natural behaviour).
        /// visible = false when note is not on screen or tail has cleared the miss window.
        /// </summary>
        private void ComputeHoldEndpointsR(
            int    startTimeMs,
            int    endTimeMs,
            double chartTimeMs,
            double greatWindowMs,
            float  spawnR,
            float  judgementR,
            out float headR,
            out float tailR,
            out bool  visible)
        {
            double headToHit = startTimeMs - chartTimeMs;
            double tailToHit = endTimeMs   - chartTimeMs;

            if (headToHit > noteLeadTimeMs)
            {
                // Head not yet on screen — whole ribbon invisible.
                headR = spawnR; tailR = spawnR; visible = false; return;
            }

            if (tailToHit < -greatWindowMs)
            {
                // Tail has cleared the miss window — ribbon done.
                headR = judgementR; tailR = judgementR; visible = false; return;
            }

            // headApproachParam: when headToHit ≤ 0, Clamp01 gives alpha=1 → headR = judgementR (pinned).
            headR   = ComputeApproachR((float)headToHit, spawnR, judgementR);
            tailR   = ComputeApproachR((float)tailToHit, spawnR, judgementR);
            visible = true;
        }

        /// <summary>
        /// Maps time-to-event to a local radius (spec §6.1).
        /// Delegates to <see cref="NoteApproachMath.ApproachRadius"/> — single source of truth.
        /// </summary>
        private float ComputeApproachR(float timeToHitMs, float spawnR, float judgementR)
            => NoteApproachMath.ApproachRadius(timeToHitMs, noteLeadTimeMs, spawnR, judgementR);

        // -------------------------------------------------------------------
        // Frustum Z helper — matches PlayerDebugRenderer.VisualOnlyLocalZ
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes the PlayfieldRoot local Z for a ribbon endpoint at radius <paramref name="r"/>.
        /// Delegates to <see cref="NoteApproachMath.FrustumZAtRadius"/> — single source of truth.
        ///
        /// When <see cref="arenaSurface"/> is assigned its values are used automatically,
        /// keeping the ribbon in sync with <see cref="PlayerDebugArenaSurface"/> without manual entry.
        /// Falls back to the inspector <c>useFrustumProfile</c> / <c>frustumHeightInner/Outer</c>
        /// fields when arenaSurface is null.
        /// When the frustum profile is disabled, returns <c>surfaceOffsetLocal</c>.
        /// </summary>
        private float ComputeEndpointLocalZ(float r, float innerLocal, float outerLocal)
        {
            bool useProfile = (arenaSurface != null)
                ? arenaSurface.UseFrustumProfile
                : useFrustumProfile;

            if (!useProfile) { return surfaceOffsetLocal; }

            float hInner = (arenaSurface != null) ? arenaSurface.FrustumHeightInner : frustumHeightInner;
            float hOuter = (arenaSurface != null) ? arenaSurface.FrustumHeightOuter : frustumHeightOuter;

            return NoteApproachMath.FrustumZAtRadius(r, innerLocal, outerLocal, hInner, hOuter);
        }

        // -------------------------------------------------------------------
        // Lane width helper
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes the chord width of a lane at a given local radius.
        /// Delegates to <see cref="NoteApproachMath.LaneChordWidthAtRadius"/> — single source of truth.
        /// </summary>
        private static float ComputeLaneWidthAtRadiusLocal(float r, float halfWidthDeg)
            => NoteApproachMath.LaneChordWidthAtRadius(r, halfWidthDeg);

        // -------------------------------------------------------------------
        // Debug draw helpers (no GC — only Debug.DrawLine calls)
        // -------------------------------------------------------------------

        /// <summary>
        /// Draws a polyline approximating an arc at <paramref name="radius"/> centred on
        /// <paramref name="center"/> (PlayfieldRoot local XY), spanning the lane's angular width.
        /// <paramref name="localZ"/> lifts the arc onto the frustum surface (same as endpoint Z).
        /// </summary>
        private static void DrawDebugArc(
            Transform pfRoot,
            Vector2   center,
            float     radius,
            float     centerDeg,
            float     halfWidthDeg,
            float     localZ,
            Color     color,
            int       segments = 12)
        {
            float startDeg = centerDeg - halfWidthDeg;
            float step     = (halfWidthDeg * 2f) / segments;

            float   a0   = startDeg * Mathf.Deg2Rad;
            Vector3 prev = pfRoot.TransformPoint(
                center.x + radius * Mathf.Cos(a0),
                center.y + radius * Mathf.Sin(a0),
                localZ);

            for (int i = 1; i <= segments; i++)
            {
                float   a    = (startDeg + i * step) * Mathf.Deg2Rad;
                Vector3 curr = pfRoot.TransformPoint(
                    center.x + radius * Mathf.Cos(a),
                    center.y + radius * Mathf.Sin(a),
                    localZ);
                Debug.DrawLine(prev, curr, color);
                prev = curr;
            }
        }

        /// <summary>
        /// Draws a short tangential tick at the given <paramref name="radius"/> along the lane
        /// centre, useful for marking the exact headR / tailR position of a hold endpoint.
        /// </summary>
        private static void DrawDebugRadialTick(
            Transform pfRoot,
            Vector2   center,
            float     radius,
            float     centerDeg,
            float     innerLocal,
            float     outerLocal,
            Color     color)
        {
            // Tick spans ±3° around lane centre at the given radius.
            const float HalfTickDeg = 3f;
            float span = outerLocal > innerLocal ? outerLocal - innerLocal : 1f;
            float s01  = Mathf.Clamp01((radius - innerLocal) / span);
            float localZ = Mathf.Lerp(0.001f, 0.15f, s01); // simplified frustum Z for debug only

            DrawDebugArc(pfRoot, center, radius, centerDeg, HalfTickDeg, localZ, color, segments: 4);
        }

        // -------------------------------------------------------------------
        // Trapezoid mesh template
        // -------------------------------------------------------------------

        // Builds a 4-vertex mesh with stable triangles and UVs.
        // Vertices are placeholder zeros — LateUpdate writes real positions each frame.
        //
        // Vertex layout:
        //   [0] tail-left   UV(0,0)
        //   [1] tail-right  UV(1,0)
        //   [2] head-right  UV(1,1)
        //   [3] head-left   UV(0,1)
        //
        // Triangles: {0,1,2} and {0,2,3}  (two CCW triangles covering the quad)
        private static Mesh BuildTrapezoidMesh()
        {
            var mesh = new Mesh { name = "HoldBodyTrapezoid" };

            // Placeholder zero vertices — overwritten every frame in LateUpdate.
            mesh.vertices = new Vector3[4];

            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),  // [0] tail-left
                new Vector2(1f, 0f),  // [1] tail-right
                new Vector2(1f, 1f),  // [2] head-right
                new Vector2(0f, 1f),  // [3] head-left
            };

            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };

            // Bounds will be recalculated per-frame after vertex update.
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
