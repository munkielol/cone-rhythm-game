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
//   holdLaneWidthRatio is now read from NoteSkinSet.holdLaneWidthRatio
//   (migrated from the old Inspector field on this component).
//
// ══════════════════════════════════════════════════════════════════════
//  MESH LAYOUT — 5-COLUMN RIBBON (added for width-side skin support)
//
//   The ribbon uses HoldColumnCount=5 columns × 2 rows = 12 vertices,
//   matching NoteCapGeometryBuilder's column count.  This is required
//   to represent the three-region fixed-edge + tiled-center UV layout
//   across the width — a 4-vertex quad can only do linear 0→1 UV.
//
//   Vertex layout (row-major, left-to-right per row):
//     Tail row (at tailR):  verts[0..5]   — inner/younger hold edge
//     Head row (at headR):  verts[6..11]  — outer/older hold edge
//
//   Vertex positions use uniform column fractions (i/5) along the
//   tangent axis — no arc math needed for a straight trapezoid.
//   UV is computed per-column per-frame using ComputeHoldWidthU.
//
// ══════════════════════════════════════════════════════════════════════
//  SKIN SYSTEM INTEGRATION (both axes)
//
//   Material:  NoteSkinSet.noteBodyMaterial (shared template).
//   Texture:   NoteSkinSet.GetHoldBodyTexture() → set via _MainTex MPB.
//   Width UV:  fixed left edge + tiled center + fixed right edge,
//              using NoteCapGeometryBuilder.ComputeHoldWidthU per column.
//   Length UV: head-anchored tiling along ribbon length (V axis).
//              V at the head/judgement end is always 0 (or 1 when flipped),
//              and V at the tail end extends by segmentLength × holdLengthTileRatePerUnit.
//              As the hold shrinks, tiles disappear toward the tail while the
//              visual pattern at the head stays stable.
//   Phase tint: holdColorApproaching / holdColorActive / holdColorReleased
//              are applied as _Color tint multipliers on top of the texture.
//
// Spec §5.7.1.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Runtime visual ribbon renderer for Hold note bodies (scroll/long-note style).
    /// Draws trapezoid meshes in PlayfieldRoot-local space; promotes to world via localToWorldMatrix.
    ///
    /// <para>Width-side skinning reads from <see cref="NoteSkinSet"/>:
    /// fixed decorative borders, tiled center, and hold body texture.
    /// Hold length tiling (<c>holdLengthTileRatePerUnit</c>) is deferred — V maps 0→1 for now.</para>
    ///
    /// Attach to any GameObject in the Player scene; assign PlayerAppController and NoteSkinSet.
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

        [Tooltip("Note skin set for hold body appearance.\n\n" +
                 "Provides:\n" +
                 "  • holdBodyTexture (or fallbackBodyTexture) — body texture for hold ribbons\n" +
                 "  • noteBodyMaterial — shared shader template (must support _MainTex, _Color via MPB)\n" +
                 "  • holdLaneWidthRatio — ribbon angular width fraction\n" +
                 "  • holdLeft/RightEdgeU, holdLeft/RightEdgeLocalWidth — border UV fractions\n" +
                 "  • holdCenterTileRatePerUnit — tiling across ribbon width (U axis)\n" +
                 "  • holdLengthTileRatePerUnit — tiling along ribbon length (V axis)\n" +
                 "  • holdFlipVertical — V-axis orientation")]
        [SerializeField] private NoteSkinSet noteSkinSet;

        // -------------------------------------------------------------------
        // Inspector — Frustum surface alignment
        // -------------------------------------------------------------------

        [Header("Frustum Surface Alignment")]
        [Tooltip("Shared frustum profile. When assigned, frustum heights are read from this component " +
                 "so the ribbon sits on the same cone surface as all other note types. " +
                 "If null, a flat Z at surfaceOffsetLocal is used.")]
        [SerializeField] private PlayfieldFrustumProfile frustumProfile;

        [Tooltip("Flat Z offset used when frustumProfile is not assigned. Prevents z-fighting. Default: 0.002.")]
        [SerializeField] private float surfaceOffsetLocal = 0.002f;

        // -------------------------------------------------------------------
        // Inspector — Phase tints
        //
        // These colors are applied as _Color tint multipliers on top of the
        // hold body texture (set via MaterialPropertyBlock).  With a white
        // texture they produce solid flat color (legacy behavior).  With a
        // real texture they modulate the texture colors by the tint, allowing
        // distinct approaching / active / released feedback to remain visible.
        // -------------------------------------------------------------------

        [Header("Phase Tints")]
        [Tooltip("_Color tint applied to the hold ribbon texture while approaching (before startTimeMs).\n\n" +
                 "Multiplied with the hold body texture via MaterialPropertyBlock._Color.\n" +
                 "Default: semi-transparent blue.")]
        [SerializeField] private Color holdColorApproaching = new Color(0.3f, 0.6f, 1f, 0.75f);

        [Tooltip("_Color tint while the hold is actively being held (HoldBind == Bound).\n\n" +
                 "Should be brighter / more saturated than holdColorApproaching to signal scoring.\n" +
                 "Default: bright blue-white.")]
        [SerializeField] private Color holdColorActive = new Color(0.5f, 0.85f, 1f, 1.0f);

        [Tooltip("_Color tint when the hold is in a failed/non-judging state:\n" +
                 "  • Released early  (HoldBind == Finished, still before endTimeMs)\n" +
                 "  • Missed start    (NoteState == Missed — never bound)\n\n" +
                 "The ribbon keeps shrinking geometrically until endTimeMs using this dim tint\n" +
                 "to signal 'no longer scoring'.\n" +
                 "Default: dim semi-transparent red.")]
        [SerializeField] private Color holdColorReleased = new Color(0.8f, 0.2f, 0.2f, 0.55f);

        // -------------------------------------------------------------------
        // Inspector — Debug
        // -------------------------------------------------------------------

        [Header("Debug")]
        [Tooltip("Draws Debug.DrawLine between the ribbon's two center endpoints in world space. " +
                 "Use this to compare the ribbon centerline against the debug hold rail.")]
        [SerializeField] private bool debugDrawEndpoints = false;

        [Tooltip("Draws the ribbon mesh trapezoid outline (outer 4 corners + centerline) in world space. " +
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
        // Internals — mesh pool constants
        //
        // HoldColumnCount=5 columns across width → 6 boundaries per row → 12 vertices.
        // This matches NoteCapGeometryBuilder.ColumnCount and is the minimum needed to
        // represent the three-region fixed-edge + tiled-center UV without severe
        // misrepresentation (a 4-vertex quad can only do linear U 0→1).
        // -------------------------------------------------------------------

        private const int MaxHoldPool     = 64;  // simultaneous hold ribbons
        private const int HoldColumnCount = 5;   // columns across ribbon width
        private const int HoldTailRow     = 0;                        // first vertex index of tail row
        private const int HoldHeadRow     = HoldColumnCount + 1;      // = 6; first index of head row
        private const int HoldVertCount   = (HoldColumnCount + 1) * 2; // = 12 vertices per mesh
        private const int HoldIndexCount  = HoldColumnCount * 6;       // = 30 triangle indices

        private Mesh[]   _meshPool;   // pool of 12-vertex ribbon meshes
        private int      _poolUsed;   // slots used this frame, reset each LateUpdate

        // Scratch arrays shared across all hold iterations (sequential — no concurrency).
        // Pre-allocated once; written into per-frame (no GC alloc at runtime).
        private readonly Vector3[] _vertScratch = new Vector3[HoldVertCount];
        private readonly Vector2[] _uvScratch   = new Vector2[HoldVertCount];

        // Reused every DrawMesh call — no per-frame allocation.
        // Must be created in Awake; Unity forbids engine-object ctor in field initializers.
        private MaterialPropertyBlock _propBlock;

        // Countdown for debugLogSpawnOncePerSecond: fires when ≤ 0, then resets to 1s.
        private float _debugLogTimer;

        // Once-only warning flags — prevent per-frame log spam on misconfiguration.
        private bool _warnedMissingSkin;
        private bool _warnedMissingMaterial;
        private bool _warnedMissingTexture;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // Pre-allocate the mesh pool. Each mesh uses 12 vertices (5 columns × 2 rows)
            // so UVs and vertices can be written in-place each frame without GC allocation.
            _meshPool = new Mesh[MaxHoldPool];
            for (int i = 0; i < MaxHoldPool; i++)
            {
                _meshPool[i] = BuildHoldRibbonMesh();
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
            // ── Guard: NoteSkinSet must be assigned ───────────────────────────────────
            if (noteSkinSet == null)
            {
                if (!_warnedMissingSkin)
                {
                    Debug.LogWarning("[HoldBodyRenderer] noteSkinSet is not assigned. " +
                                     "Hold bodies will not render. Assign a NoteSkinSet in the Inspector.", this);
                    _warnedMissingSkin = true;
                }
                return;
            }

            // ── Guard: material must be present ───────────────────────────────────────
            Material bodyMaterial = noteSkinSet.noteBodyMaterial;
            if (bodyMaterial == null)
            {
                if (!_warnedMissingMaterial)
                {
                    Debug.LogWarning("[HoldBodyRenderer] noteSkinSet.noteBodyMaterial is null. " +
                                     "Hold bodies will not render. Assign a material to the NoteSkinSet.", this);
                    _warnedMissingMaterial = true;
                }
                return;
            }

            if (playerAppController == null || _meshPool == null) { return; }

            var allNotes = playerAppController.NotesAll;
            if (allNotes == null) { return; }

            var arenaGeos   = playerAppController.ArenaGeometries;
            var laneGeos    = playerAppController.LaneGeometries;
            var laneToArena = playerAppController.LaneToArena;
            var pfTf        = playerAppController.PlayfieldTf;
            Transform pfRoot = playerAppController.playfieldRoot;

            if (arenaGeos == null || laneGeos == null || laneToArena == null
                || pfTf == null || pfRoot == null) { return; }

            // ── Resolve hold body texture (same for all notes this frame) ─────────────
            //
            // GetHoldBodyTexture() returns holdBodyTexture if set, otherwise fallbackBodyTexture.
            // A null result means neither is authored yet; we warn once and render without texture.
            Texture2D holdTex = noteSkinSet.GetHoldBodyTexture();
            if (holdTex == null && !_warnedMissingTexture)
            {
                Debug.LogWarning("[HoldBodyRenderer] No hold body texture available " +
                                 "(holdBodyTexture and fallbackBodyTexture are both null on the NoteSkinSet). " +
                                 "Hold bodies will render with _Color tint only.", this);
                _warnedMissingTexture = true;
            }

            // ── Skin settings read once per frame ────────────────────────────────────
            // holdLaneWidthRatio:     migrated from the old HoldBodyRenderer Inspector field.
            // holdFlipVertical:       controls V axis orientation along ribbon length.
            // holdLengthTileRatePerUnit: how many times the texture repeats per local unit of
            //                         hold length. Read once; applied per-note in the loop below.
            float skinLaneWidthRatio = noteSkinSet.holdLaneWidthRatio;
            bool  flipVertical       = noteSkinSet.holdFlipVertical;
            float lengthTileRate     = Mathf.Max(0.01f, noteSkinSet.holdLengthTileRatePerUnit);

            double chartTimeMs   = playerAppController.EffectiveChartTimeMs;
            double greatWindowMs = playerAppController.GreatWindowMs;

            // ── Shared approach settings from PlayerAppController ─────────────────────
            // Authoritative on PlayerAppController — a single Inspector change propagates
            // to Tap, Catch, Flick, and Hold simultaneously.
            int   noteLeadTimeMs    = playerAppController.NoteLeadTimeMs;
            float spawnRadiusFactor = playerAppController.SpawnRadiusFactor;

            // localToWorld is the model→world matrix passed to Graphics.DrawMesh.
            // Vertices are written in PlayfieldRoot local space, so this promotes them correctly.
            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            // ── Throttle for debugLogSpawnOncePerSecond ───────────────────────────────
            bool logThisFrame = false;
            if (debugLogSpawnOncePerSecond)
            {
                _debugLogTimer -= Time.deltaTime;
                if (_debugLogTimer <= 0f)
                {
                    _debugLogTimer = 1f;
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
                //   NoteState.Hit    → successfully completed. Visual done. SKIP.
                //
                //   NoteState.Missed → missed start OR swept after early release.
                //                      Judging stopped, but the hold body stays visible until
                //                      endTimeMs so the player can see where they went wrong.
                //                      KEEP RENDERING (dim tint applied in Step 3 below).
                //
                //   NoteState.Active
                //     HoldBind.Unbound   → approaching, not yet hittable.
                //     HoldBind.Bound     → being held, ticks scoring.
                //     HoldBind.Finished  → released early; still Active until SweepMissed fires.
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
                    noteLeadTimeMs,
                    out float headR, out float tailR, out bool visible);

                if (!visible) { continue; }

                // ── debugLogSpawnOncePerSecond ──────────────────────────────────────────
                if (logThisFrame)
                {
                    double headToHitForLog = note.StartTimeMs - chartTimeMs;
                    float  alphaHead = (noteLeadTimeMs > 0)
                        ? 1f - Mathf.Clamp01((float)headToHitForLog / noteLeadTimeMs)
                        : 1f;

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

                // ── Length-direction V tiling (head-anchored) ─────────────────────────────
                //
                // V is tiled along the hold length rather than stretched 0→1.
                // The tiling phase is anchored at the head/judgement end so the visual
                // pattern near the hit line stays stable as the hold is consumed.
                //
                // segmentLength: current rendered distance between head and tail endpoints.
                //   During approach (Phase A):  head and tail both move, ribbon shrinks from
                //                               the outer side.
                //   During hold     (Phase B):  headR pins at judgementR; tailR advances
                //                               inward — ribbon shrinks from the tail end.
                //   In both phases headV stays fixed at the anchor (0 or 1), so tiles
                //   appear/disappear only at the tail end, never at the judgement end.
                //
                // vSpan = segmentLength × holdLengthTileRatePerUnit
                //   A vSpan of 2.5 means the texture repeats 2.5× across the ribbon.
                //   Because the material uses a tiling/repeat sampler, UV values outside
                //   [0,1] wrap correctly — no special clamping is needed.
                //
                // holdFlipVertical = false:  headV = 0,  tailV = +vSpan
                //   (V increases from head toward tail; tile "rows" flow outward)
                // holdFlipVertical = true:   headV = 1,  tailV = 1 − vSpan
                //   (V decreases from head toward tail; texture is mirrored vertically)
                float segmentLength = headR - tailR;  // > 0.0001 (degenerate check above)
                float vSpan         = segmentLength * lengthTileRate;
                float vHead         = flipVertical ? 1f : 0f;
                float vTail         = flipVertical ? (1f - vSpan) : vSpan;

                // ── Step 3: Phase-based tint ──────────────────────────────────────────────
                //
                // Applied as _Color multiplier on top of the hold body texture.
                // With a white texture this reproduces the old flat-color behavior.
                // With a real texture the tint modulates but doesn't replace it.
                //
                //   Missed (any HoldBind)       → holdColorReleased  (dim: failed/non-judging)
                //   Active + HoldBind.Bound     → holdColorActive    (bright: scoring)
                //   Active + HoldBind.Finished  → holdColorReleased  (dim: released early)
                //   Active + HoldBind.Unbound   → holdColorApproaching
                //
                Color ribbonColor;
                if (note.State == NoteState.Missed)
                {
                    ribbonColor = holdColorReleased;
                }
                else
                {
                    switch (note.HoldBind)
                    {
                        case HoldBindState.Bound:    ribbonColor = holdColorActive;      break;
                        case HoldBindState.Finished: ribbonColor = holdColorReleased;    break;
                        default:                     ribbonColor = holdColorApproaching; break;
                    }
                }

                // Set MaterialPropertyBlock: texture + phase tint.
                // Both must be set before DrawMesh; MPB is reused (no allocation).
                if (holdTex != null)
                    _propBlock.SetTexture("_MainTex", holdTex);
                _propBlock.SetColor("_Color", ribbonColor);

                // ── Step 4: Local-space axes ──────────────────────────────────────────────

                float thetaRad = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                float cosT     = Mathf.Cos(thetaRad);
                float sinT     = Mathf.Sin(thetaRad);

                // tangLocal: 90° CCW from radial direction, in local XY — the ribbon width axis.
                // Cross(localZ, dir) = Cross((0,0,1), (cosT,sinT,0)) = (-sinT, cosT, 0).
                var tangLocal = new Vector3(-sinT, cosT, 0f);

                // ── Step 5: Compute 3D local-space endpoints (XY + frustum Z) ────────────

                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // Tail: inner (younger) end of the ribbon. Maps to V=vTail in UV.
                var tailLocal3 = new Vector3(
                    ctr.x + tailR * cosT,
                    ctr.y + tailR * sinT,
                    ComputeEndpointLocalZ(tailR, innerLocal, outerLocal));

                // Head: outer (older) end, pinned at judgementR during the hold. Maps to V=vHead.
                var headLocal3 = new Vector3(
                    ctr.x + headR * cosT,
                    ctr.y + headR * sinT,
                    ComputeEndpointLocalZ(headR, innerLocal, outerLocal));

                // ── Step 6: Trapezoid chord widths at each endpoint ───────────────────────
                //
                // Lane borders are radial lines at centerDeg ± widthDeg/2.
                // The chord between those lines at radius r is:
                //   width(r) = 2 · r · sin( widthDeg/2 · Deg2Rad )
                //
                // head and tail are at different radii → trapezoid.
                // holdLaneWidthRatio is now read from NoteSkinSet.

                float halfWidthDeg = lane.WidthDeg * 0.5f;
                float widthHead    = ComputeLaneWidthAtRadiusLocal(headR, halfWidthDeg) * skinLaneWidthRatio;
                float widthTail    = ComputeLaneWidthAtRadiusLocal(tailR, halfWidthDeg) * skinLaneWidthRatio;

                // ── Step 7: Build 12-vertex ribbon (HoldColumnCount columns × 2 rows) ─────
                //
                // Vertex positions use uniform column fractions (i / HoldColumnCount) along
                // the tangent axis — simple linear interpolation across the trapezoid width.
                //
                // UV (U axis = width, V axis = length):
                //   U: computed per-column via ComputeHoldWidthU — fixed-edge + tiled-center.
                //      Each row uses its own totalChord (widthTail or widthHead) because head
                //      and tail have different chord widths at different radii.
                //   V: head-anchored tiling along ribbon length.
                //      vHead and vTail were computed above from segmentLength × lengthTileRate,
                //      anchored at the head end so the pattern stays stable near judgement.
                //      All columns in a row share the same V (V does not vary across width).
                //
                // Mesh vertex indices:
                //   Tail row: [HoldTailRow .. HoldTailRow + HoldColumnCount] = [0..5]
                //   Head row: [HoldHeadRow .. HoldHeadRow + HoldColumnCount] = [6..11]

                for (int col = 0; col <= HoldColumnCount; col++)
                {
                    float frac = (float)col / HoldColumnCount;  // 0..1 uniform step

                    // Tail-row vertex: linear interpolation from tail-left to tail-right.
                    float chordTail = frac * widthTail;
                    _vertScratch[HoldTailRow + col] = tailLocal3
                        + tangLocal * (chordTail - widthTail * 0.5f);
                    _uvScratch[HoldTailRow + col] = new Vector2(
                        NoteCapGeometryBuilder.ComputeHoldWidthU(chordTail, widthTail, noteSkinSet),
                        vTail);

                    // Head-row vertex: linear interpolation from head-left to head-right.
                    float chordHead = frac * widthHead;
                    _vertScratch[HoldHeadRow + col] = headLocal3
                        + tangLocal * (chordHead - widthHead * 0.5f);
                    _uvScratch[HoldHeadRow + col] = new Vector2(
                        NoteCapGeometryBuilder.ComputeHoldWidthU(chordHead, widthHead, noteSkinSet),
                        vHead);
                }

                // Write vertices and UVs into the pooled mesh.
                // Triangles were set once in BuildHoldRibbonMesh and never change.
                Mesh trapMesh = _meshPool[_poolUsed++];
                trapMesh.vertices = _vertScratch;
                trapMesh.uv       = _uvScratch;
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
                        // Outer 4 corners of the trapezoid in world space — should outline the mesh exactly.
                        // Corner indices in the 12-vertex layout:
                        //   tail-left  = _vertScratch[HoldTailRow]
                        //   tail-right = _vertScratch[HoldTailRow + HoldColumnCount]
                        //   head-right = _vertScratch[HoldHeadRow + HoldColumnCount]
                        //   head-left  = _vertScratch[HoldHeadRow]
                        Vector3 p0 = pfRoot.TransformPoint(_vertScratch[HoldTailRow]);
                        Vector3 p1 = pfRoot.TransformPoint(_vertScratch[HoldTailRow + HoldColumnCount]);
                        Vector3 p2 = pfRoot.TransformPoint(_vertScratch[HoldHeadRow + HoldColumnCount]);
                        Vector3 p3 = pfRoot.TransformPoint(_vertScratch[HoldHeadRow]);

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

                // Draw the ribbon mesh. Vertices are in PlayfieldRoot local space;
                // localToWorld promotes them to world space for rendering.
                Graphics.DrawMesh(
                    trapMesh, localToWorld, bodyMaterial,
                    gameObject.layer, null, 0, _propBlock);

                // ── debugDrawSpawnArcs ─────────────────────────────────────────────────────
                // Reference arcs that let you visually verify spawnR == innerLocal when factor=0.
                if (debugDrawSpawnArcs)
                {
                    float halfWidthDegs = lane.WidthDeg * 0.5f;

                    DrawDebugArc(pfRoot, ctr, innerLocal, lane.CenterDeg, halfWidthDegs,
                        ComputeEndpointLocalZ(innerLocal, innerLocal, outerLocal), Color.magenta);

                    DrawDebugArc(pfRoot, ctr, spawnR, lane.CenterDeg, halfWidthDegs,
                        ComputeEndpointLocalZ(spawnR, innerLocal, outerLocal), Color.green);

                    DrawDebugRadialTick(pfRoot, ctr, headR, lane.CenterDeg,
                        innerLocal, outerLocal, Color.cyan);

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
            int    noteLeadTimeMs,  // shared approach setting from PlayerAppController
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
            headR   = ComputeApproachR((float)headToHit, spawnR, judgementR, noteLeadTimeMs);
            tailR   = ComputeApproachR((float)tailToHit, spawnR, judgementR, noteLeadTimeMs);
            visible = true;
        }

        /// <summary>
        /// Maps time-to-event to a local radius (spec §6.1).
        /// Delegates to <see cref="NoteApproachMath.ApproachRadius"/> — single source of truth.
        /// </summary>
        private static float ComputeApproachR(float timeToHitMs, float spawnR, float judgementR, int noteLeadTimeMs)
            => NoteApproachMath.ApproachRadius(timeToHitMs, noteLeadTimeMs, spawnR, judgementR);

        // -------------------------------------------------------------------
        // Frustum Z helper — matches PlayerDebugRenderer.VisualOnlyLocalZ
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes the PlayfieldRoot local Z for a ribbon endpoint at radius <paramref name="r"/>.
        /// Delegates to <see cref="NoteApproachMath.FrustumZAtRadius"/> — single source of truth.
        ///
        /// When <see cref="frustumProfile"/> is assigned its values are used automatically,
        /// keeping the ribbon in sync with all other production renderers.
        /// When the frustum profile is null or disabled, returns <c>surfaceOffsetLocal</c>.
        /// </summary>
        // Note-layer Z lift: places hold ribbons above the lane surface (liftLocal = 0.005f).
        // Must be greater than LaneSurfaceRenderer.liftLocal so ribbons are not occluded.
        private const float NoteLayerZLift = 0.010f;

        private float ComputeEndpointLocalZ(float r, float innerLocal, float outerLocal)
        {
            if (frustumProfile == null || !frustumProfile.UseFrustumProfile) { return surfaceOffsetLocal; }

            return NoteApproachMath.FrustumZAtRadius(r, innerLocal, outerLocal,
                frustumProfile.FrustumHeightInner, frustumProfile.FrustumHeightOuter) + NoteLayerZLift;
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
            const float HalfTickDeg = 3f;
            float span = outerLocal > innerLocal ? outerLocal - innerLocal : 1f;
            float s01  = Mathf.Clamp01((radius - innerLocal) / span);
            float localZ = Mathf.Lerp(0.001f, 0.15f, s01); // simplified frustum Z for debug only

            DrawDebugArc(pfRoot, center, radius, centerDeg, HalfTickDeg, localZ, color, segments: 4);
        }

        // -------------------------------------------------------------------
        // Hold ribbon mesh template
        // -------------------------------------------------------------------

        // Builds a 12-vertex mesh (5 columns × 2 rows) with stable triangle topology.
        // Placeholder zero vertices are written here; LateUpdate overwrites positions and
        // UVs every frame via _vertScratch / _uvScratch (no per-frame GC allocation).
        //
        // Vertex layout:
        //   Tail row [0..5]:  tail-left → tail-right  (inner/younger hold edge)
        //   Head row [6..11]: head-left → head-right  (outer/older hold edge)
        //
        // UV placeholder: U = i/HoldColumnCount, V = 0 (tail) / 1 (head).
        // These placeholders are overwritten per-frame; they are only used during
        // the first frame before LateUpdate runs.
        //
        // Triangles: HoldColumnCount quads, each as two CCW tris (matching
        // NoteCapGeometryBuilder winding convention):
        //   Tri A: tail_i,  tail_(i+1), head_(i+1)
        //   Tri B: tail_i,  head_(i+1), head_i
        private static Mesh BuildHoldRibbonMesh()
        {
            var mesh = new Mesh { name = "HoldBodyRibbon" };

            // Placeholder zero vertices — overwritten every frame in LateUpdate.
            mesh.vertices = new Vector3[HoldVertCount];

            // Placeholder UVs — overwritten every frame in LateUpdate.
            var uvs = new Vector2[HoldVertCount];
            for (int i = 0; i <= HoldColumnCount; i++)
            {
                float u = (float)i / HoldColumnCount;
                uvs[HoldTailRow + i] = new Vector2(u, 0f); // tail row
                uvs[HoldHeadRow + i] = new Vector2(u, 1f); // head row
            }
            mesh.uv = uvs;

            // Triangles: HoldColumnCount quads, 2 CCW tris each.
            var tris = new int[HoldIndexCount];
            for (int i = 0; i < HoldColumnCount; i++)
            {
                int tailI  = HoldTailRow + i;
                int tailI1 = HoldTailRow + i + 1;
                int headI  = HoldHeadRow + i;
                int headI1 = HoldHeadRow + i + 1;

                int t = i * 6;
                tris[t + 0] = tailI;  tris[t + 1] = tailI1; tris[t + 2] = headI1; // Tri A
                tris[t + 3] = tailI;  tris[t + 4] = headI1; tris[t + 5] = headI;  // Tri B
            }
            mesh.triangles = tris;

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
