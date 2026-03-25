// HoldBodyRenderer.cs
// Hold note body renderer — moving textured object with hold-body-local UV mapping.
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
//  CHORD WIDTH — UV MAPPING ALONG RIBBON WIDTH
//
//   Lane borders are radial lines at centerDeg ± widthDeg/2.
//   The chord (straight-line width) between those borders at radius r is:
//
//       width(r) = 2 · r · sin( widthDeg/2 · Deg2Rad )
//
//   Head and tail are at different radii, so chord width differs per row:
//       widthHead = 2 · headR · sin(halfWidthDeg) · holdLaneWidthRatio
//       widthTail = 2 · tailR · sin(halfWidthDeg) · holdLaneWidthRatio
//
//   holdLaneWidthRatio is read from NoteSkinSet.holdLaneWidthRatio.
//
// ══════════════════════════════════════════════════════════════════════
//  MESH LAYOUT — ARC-CONFORMING HOLD BODY
//
//   The ribbon is an arc-conforming grid (holdArcSegments × holdRadialSegments),
//   using the same geometry approach as LaneSurfaceRenderer.  This makes the
//   hold body follow the lane's actual arc shape instead of appearing as a
//   straight flat trapezoid that cuts across the lane curvature.
//
//   Vertex grid: (holdArcSegments+1) columns × (holdRadialSegments+1) rows.
//     Columns: angular span from (centerDeg - holdHalfAngleDeg)
//                               to (centerDeg + holdHalfAngleDeg).
//     Rows:    radial span from tailR (inner/younger) to headR (outer/older).
//     Z:       FrustumZAtRadius(r, …) + NoteLayerZLift at each row.
//
//   Each vertex: (center.x + cos(θ)·r, center.y + sin(θ)·r, z)
//   This is the same formula used by LaneSurfaceRenderer and produces a
//   surface that conforms to the arc of the lane and the frustum cone.
//
//   Default: holdArcSegments=8, holdRadialSegments=4.  These Inspector fields
//   must remain fixed after Awake (pool mesh topology cannot change at runtime).
//
// ══════════════════════════════════════════════════════════════════════
//  SKIN SYSTEM INTEGRATION (both axes)
//
//   Material:  NoteSkinSet.noteBodyMaterial (shared template).
//   Texture:   NoteSkinSet.GetHoldBodyTexture() → set via _MainTex MPB.
//   Width UV:  fixed left edge + tiled center + fixed right edge,
//              using NoteCapGeometryBuilder.ComputeHoldWidthU per column.
//   Length UV: hold-body-local tiling along ribbon length (V axis).
//              Phase A (approach):  V=0 at the head vertex, V=bodyLength×rate at the tail.
//                  The UV span is frozen on the hold body — the whole textured
//                  object moves together, like a tap note.
//              Phase B (during hold): V at the head advances as the body is consumed
//                  at the judgement line.  Different parts of the pattern arrive at
//                  the pinned front over time.  The rate is derived from approach
//                  geometry; no separate flow-rate parameter is used.
//   Phase tint: holdColorApproaching / holdColorActive / holdColorReleased
//              are applied as _Color tint multipliers on top of the texture.
//
// Spec §5.7.1.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Runtime visual ribbon renderer for Hold note bodies.
    /// Draws arc-conforming grid meshes in PlayfieldRoot-local space; promotes to world via localToWorldMatrix.
    ///
    /// <para>Width-side skinning reads from <see cref="NoteSkinSet"/>:
    /// fixed decorative borders, tiled center, and hold body texture.
    /// Length-direction (V) tiling uses hold-body-local UV mapping —
    /// the texture is fixed to the hold body during approach (Phase A) and consumed
    /// at the judgement line during Phase B (spec §5.7.1 "Body motion readability").</para>
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
        // Inspector — Hold Head Cap
        // -------------------------------------------------------------------

        [Header("Hold Head Cap")]
        [Tooltip("When enabled, draws a curved note-head cap at the leading edge (headR) of the hold " +
                 "body each frame.\n\n" +
                 "The cap uses tap-note curved-cap geometry (FillCapVerticesEdgeAware) so the hold " +
                 "head looks consistent with other note types at the judgement line.\n\n" +
                 "When disabled, the arc body itself provides hold visibility once headR > tailR " +
                 "(after the first approach frame).")]
        [SerializeField] private bool drawHoldHeadCap = true;

        // -------------------------------------------------------------------
        // Inspector — Mesh Quality
        // -------------------------------------------------------------------

        [Header("Mesh Quality")]
        [Tooltip("Number of angular column subdivisions across the hold body width.\n\n" +
                 "More segments produce smoother arc edges for wide lanes.\n" +
                 "Must not be changed at runtime after Awake.\n" +
                 "Default: 8")]
        [Min(1)]
        [SerializeField] private int holdArcSegments = 8;

        [Tooltip("Number of radial row subdivisions along the hold body depth.\n\n" +
                 "More segments improve frustum cone conformance along the hold length.\n" +
                 "Must not be changed at runtime after Awake.\n" +
                 "Default: 4")]
        [Min(1)]
        [SerializeField] private int holdRadialSegments = 4;

        // -------------------------------------------------------------------
        // Internals — mesh pool
        //
        // Each ribbon is an arc-conforming grid: (holdArcSegments+1) columns ×
        // (holdRadialSegments+1) rows.  Vertex and index counts are computed once
        // in Awake from the Inspector values and stay fixed for the session.
        // -------------------------------------------------------------------

        private const int MaxHoldPool = 64;   // simultaneous hold ribbons

        // Arc mesh geometry counts — computed once in Awake, fixed for the session.
        // Must not be changed at runtime (pool mesh topology cannot change after allocation).
        private int _holdArcSegs;        // angular columns (clamped from holdArcSegments)
        private int _holdRadialSegs;     // radial rows     (clamped from holdRadialSegments)
        private int _vertsPerRibbon;     // (_holdArcSegs+1) * (_holdRadialSegs+1)
        private int _indicesPerRibbon;   // _holdArcSegs * _holdRadialSegs * 6

        private Mesh[] _meshPool;   // pool of arc ribbon meshes
        private int    _poolUsed;   // slots used this frame, reset each LateUpdate

        // Scratch arrays — allocated in Awake from _vertsPerRibbon (no fixed size).
        // Reused every frame: no per-frame GC allocation after Awake.
        private Vector3[] _vertScratch;
        private Vector2[] _uvScratch;

        // Cap pool: one curved-cap mesh per hold head, drawn before the ribbon each frame.
        // Keeps the hold visible from the very first approach frame, even when the ribbon
        // is zero-area (headR == tailR == spawnR on that first frame).
        private Mesh[] _capPool;
        private int    _capPoolUsed;

        // Scratch arrays for hold head cap geometry — filled per-frame, no GC alloc.
        // Lengths match NoteCapGeometryBuilder.VertexCount (12): 2 rows × (ColumnCount+1) cols.
        private readonly Vector3[] _capVertScratch = new Vector3[NoteCapGeometryBuilder.VertexCount];
        private readonly Vector2[] _capUvScratch   = new Vector2[NoteCapGeometryBuilder.VertexCount];

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
            // Clamp Inspector values — guard against invalid zero input.
            _holdArcSegs    = Mathf.Max(1, holdArcSegments);
            _holdRadialSegs = Mathf.Max(1, holdRadialSegments);

            _vertsPerRibbon   = (_holdArcSegs + 1) * (_holdRadialSegs + 1);
            _indicesPerRibbon = _holdArcSegs * _holdRadialSegs * 6;

            // Allocate scratch arrays to match the arc mesh vertex count.
            // Written in-place every frame — no per-frame GC after Awake.
            _vertScratch = new Vector3[_vertsPerRibbon];
            _uvScratch   = new Vector2[_vertsPerRibbon];

            // Pre-allocate the arc ribbon mesh pool.  Triangles are stable; only
            // vertices and UVs are overwritten each frame.
            _meshPool = new Mesh[MaxHoldPool];
            for (int i = 0; i < MaxHoldPool; i++)
            {
                _meshPool[i] = BuildHoldArcMesh();
            }

            // Pre-allocate cap pool. Caps use NoteCapGeometryBuilder topology (12 verts, 30 indices)
            // built once here — identical to TapNoteRenderer's pool.
            _capPool = new Mesh[MaxHoldPool];
            for (int i = 0; i < MaxHoldPool; i++)
            {
                _capPool[i] = NoteCapGeometryBuilder.BuildCapMesh("HoldHeadCap");
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
            if (_capPool != null)
            {
                for (int i = 0; i < _capPool.Length; i++)
                {
                    if (_capPool[i] != null) { Destroy(_capPool[i]); _capPool[i] = null; }
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
            // holdLaneWidthRatio:       ribbon angular width fraction (from NoteSkinSet).
            // holdFlipVertical:         controls V axis orientation along ribbon length.
            // holdLengthTileRatePerUnit: how many times the texture repeats per local unit of
            //                           hold length. Read once; applied per-note in the loop below.
            float skinLaneWidthRatio = noteSkinSet.holdLaneWidthRatio;
            bool  flipVertical       = noteSkinSet.holdFlipVertical;
            float lengthTileRate     = Mathf.Max(0.01f, noteSkinSet.holdLengthTileRatePerUnit);

            // ── Cap appearance (head cap drawn on every approach frame) ───────────────
            // capHalfThicknessLocal: radial half-depth matching the single-interaction family
            //   (Tap/Catch/Flick) so the hold head cap looks the same size as a tap note cap.
            // capTex: tap body texture — gives the hold head cap a consistent appearance
            //   with other note types. Falls back to null (color-only) if not assigned.
            // hInner/hOuter: explicit frustum heights for FillCapVerticesEdgeAware, same
            //   reads as TapNoteRenderer.
            float capHalfThicknessLocal = noteSkinSet.noteRadialHalfThicknessLocal;
            Texture2D capTex = noteSkinSet.GetBodyTexture(NoteBodySkinType.Tap);
            float hInner = (frustumProfile != null && frustumProfile.UseFrustumProfile)
                ? frustumProfile.FrustumHeightInner : surfaceOffsetLocal;
            float hOuter = (frustumProfile != null && frustumProfile.UseFrustumProfile)
                ? frustumProfile.FrustumHeightOuter : surfaceOffsetLocal;

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

            // Reset pool usage counters — reuse all slots from the top each frame.
            _poolUsed    = 0;
            _capPoolUsed = 0;

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

                // ── Phase-based tint — needed by both head cap and ribbon ─────────────────
                //
                // Applied as _Color multiplier on top of body/hold texture.
                //   Missed (any HoldBind)       → holdColorReleased  (dim: failed/non-judging)
                //   Active + HoldBind.Bound     → holdColorActive    (bright: scoring)
                //   Active + HoldBind.Finished  → holdColorReleased  (dim: released early)
                //   Active + HoldBind.Unbound   → holdColorApproaching
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

                // ── Arena centre and angular half-width — shared by cap and ribbon ─────────
                Vector2 ctr        = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));
                float halfWidthDeg = lane.WidthDeg * 0.5f;

                // ── Hold head cap (optional — toggle: drawHoldHeadCap) ──────────────────
                //
                // When ON:  draws a curved-cap note head at headR from the very first approach
                //   frame — even when the arc body is zero-area (headR == tailR == spawnR).
                //   Geometry identical to TapNoteRenderer so the cap looks consistent with
                //   other note types at the judgement line.
                // When OFF: the arc body itself provides visibility once headR > tailR (a single
                //   frame after first appearance).  The arc spans the full lane width so it is
                //   clearly visible even when radially thin.
                if (drawHoldHeadCap && _capPoolUsed < MaxHoldPool)
                {
                    float noteHalfAngleDeg = NoteCapGeometryBuilder.NoteHalfAngleDeg(
                        halfWidthDeg, skinLaneWidthRatio);

                    // Radial extent of the cap, clamped to the arena band [spawnR, judgementR].
                    float capTailR = Mathf.Max(headR - capHalfThicknessLocal, spawnR);
                    float capHeadR = Mathf.Min(headR + capHalfThicknessLocal, judgementR);

                    if (capHeadR - capTailR > 0.0001f)
                    {
                        NoteCapGeometryBuilder.FillCapVerticesEdgeAware(
                            _capVertScratch, ctr, capTailR, capHeadR,
                            AngleUtil.Normalize360(lane.CenterDeg), noteHalfAngleDeg,
                            headR,   // approach radius — for chord-to-angle conversion
                            noteSkinSet.bodyLeftEdgeLocalWidth,
                            noteSkinSet.bodyRightEdgeLocalWidth,
                            innerLocal, outerLocal, hInner, hOuter,
                            zOffset: NoteLayerZLift);

                        NoteCapGeometryBuilder.FillCapUVs(
                            _capUvScratch, headR, noteHalfAngleDeg, noteSkinSet,
                            flipBodyVertical: false);

                        if (capTex != null)
                            _propBlock.SetTexture("_MainTex", capTex);
                        _propBlock.SetColor("_Color", ribbonColor);

                        Mesh capMesh = _capPool[_capPoolUsed++];
                        capMesh.vertices = _capVertScratch;
                        capMesh.uv       = _capUvScratch;
                        capMesh.RecalculateBounds();

                        Graphics.DrawMesh(capMesh, localToWorld, bodyMaterial,
                            gameObject.layer, null, 0, _propBlock);
                    }
                }

                // Degenerate ribbon guard: cap was drawn above; skip ribbon only.
                if (headR - tailR < 0.0001f) { continue; }

                // ── Length-direction V tiling (hold-body-local, two-phase) ─────────────────
                //
                // V is assigned in hold-body-local space — the same model as Tap/Catch/Flick:
                // the texture is fixed to the hold body and moves with it.
                //
                // PHASE A (chartTime < startTimeMs):
                //   holdElapsedR = 0 (Mathf.Max guard).
                //   vHead = 0  (head vertex always at V=0 — body-local front).
                //   vTail = bodyLength × rate  (body-local back — constant while body length is constant).
                //   The UV span is frozen on the hold body.  The hold advances as one textured object,
                //   the same way a tap note does — the texture rides with the geometry.
                //
                // PHASE B (startTimeMs ≤ chartTime):
                //   holdElapsedR grows as chartTimeMs advances past startTimeMs.
                //   vHead = holdElapsedR × rate  (advances from 0 — new UV values arrive at pinned front).
                //   vTail = (holdElapsedR + bodyLength) × rate  (also advances; bodyLength shrinks as hold is consumed).
                //   The same textured hold body is consumed at the judgement line: different parts of the
                //   pattern arrive at the front over time.
                //
                // SEAMLESS PHASE A→B JOIN:
                //   At startTimeMs: holdElapsedR=0, headR=judgementR, bodyLength=(judgementR-tailR).
                //   Phase A gives: vHead=0, vTail=bodyLength×rate.
                //   Phase B gives: vHead=0, vTail=(0+bodyLength)×rate.  Identical. ✓
                //
                // NO FAKE FLOW-RATE PARAMETER:
                //   The Phase B advance rate = (holdBandR/noteLeadTimeMs) × lengthTileRate.
                //   Derived from approach geometry and the skin tile rate only.
                //   No separate authored scroll-speed setting is introduced.
                //
                // HEAD CAP ON OR OFF:
                //   V uses headR/tailR/holdElapsedR only — independent of whether the cap is drawn.
                float holdBandR    = judgementR - spawnR;  // arena band in local units
                float holdElapsedR = (noteLeadTimeMs > 0 && holdBandR > 0f)
                    ? Mathf.Max(0f, (float)(chartTimeMs - note.StartTimeMs))
                      * holdBandR / noteLeadTimeMs
                    : 0f;

                float bodyLength = headR - tailR;  // > 0 (degenerate guard passed above)
                float vHeadRaw   = holdElapsedR * lengthTileRate;
                float vTailRaw   = (holdElapsedR + bodyLength) * lengthTileRate;

                float vHead = flipVertical ? (1f - vHeadRaw) : vHeadRaw;
                float vTail = flipVertical ? (1f - vTailRaw) : vTailRaw;

                // Set MaterialPropertyBlock for ribbon: hold body texture + phase tint.
                // The MPB may have been overwritten by the cap draw above, so re-set explicitly.
                if (holdTex != null)
                    _propBlock.SetTexture("_MainTex", holdTex);
                _propBlock.SetColor("_Color", ribbonColor);

                // ── Steps 4-7: Arc-conforming hold body vertices ───────────────────────────
                //
                // Uses the same arc-sector formula as LaneSurfaceRenderer:
                //   vertex = (ctr.x + cos(θ)·r, ctr.y + sin(θ)·r, FrustumZAtRadius(r) + lift)
                // Each column sweeps angularly across the lane; each row spans tailR→headR radially.
                // This makes the hold body conform to the lane arc instead of appearing as a
                // flat straight trapezoid that cuts across the lane curvature.

                float holdHalfAngleDeg = halfWidthDeg * skinLaneWidthRatio;
                float bodyLeftDeg  = AngleUtil.Normalize360(lane.CenterDeg) - holdHalfAngleDeg;
                float bodyRightDeg = AngleUtil.Normalize360(lane.CenterDeg) + holdHalfAngleDeg;

                FillHoldArcVerts(_vertScratch, _uvScratch, tailR, headR,
                    bodyLeftDeg, bodyRightDeg, ctr, innerLocal, outerLocal,
                    hInner, hOuter, vTail, vHead, noteSkinSet, _holdArcSegs, _holdRadialSegs);

                // Write vertices and UVs into the pooled arc mesh.
                // Triangles were set once in BuildHoldArcMesh and never change.
                Mesh arcMesh = _meshPool[_poolUsed++];
                arcMesh.vertices = _vertScratch;
                arcMesh.uv       = _uvScratch;
                arcMesh.RecalculateBounds(); // required for correct frustum culling

                // ── Step 8: Debug visualizations ─────────────────────────────────────────
                //
                // Arc vertex layout (row-major):
                //   tail-left  = index 0
                //   tail-right = index _holdArcSegs
                //   head-left  = index _holdRadialSegs * (_holdArcSegs + 1)
                //   head-right = headLeft + _holdArcSegs

                if (debugDrawEndpoints || debugDrawMeshOutline)
                {
                    int headLeft  = _holdRadialSegs * (_holdArcSegs + 1);
                    int headRight = headLeft + _holdArcSegs;

                    if (debugDrawEndpoints)
                    {
                        // Centerline connecting tail and head midpoints — compare against debug hold rail.
                        int tailMid = _holdArcSegs / 2;
                        int headMid = headLeft + _holdArcSegs / 2;
                        Vector3 tailWorld = pfRoot.TransformPoint(_vertScratch[tailMid]);
                        Vector3 headWorld = pfRoot.TransformPoint(_vertScratch[headMid]);
                        Debug.DrawLine(tailWorld, headWorld, ribbonColor);
                    }

                    if (debugDrawMeshOutline)
                    {
                        // Outer 4 corners of the arc body in world space — should outline the mesh exactly.
                        Vector3 p0 = pfRoot.TransformPoint(_vertScratch[0]);
                        Vector3 p1 = pfRoot.TransformPoint(_vertScratch[_holdArcSegs]);
                        Vector3 p2 = pfRoot.TransformPoint(_vertScratch[headRight]);
                        Vector3 p3 = pfRoot.TransformPoint(_vertScratch[headLeft]);

                        Color outlineColor = Color.cyan;
                        Debug.DrawLine(p0, p1, outlineColor); // tail edge
                        Debug.DrawLine(p2, p3, outlineColor); // head edge
                        Debug.DrawLine(p0, p3, outlineColor); // left edge
                        Debug.DrawLine(p1, p2, outlineColor); // right edge

                        // Centerline (yellow) — compare against the debug hold rail.
                        int tailMid = _holdArcSegs / 2;
                        int headMid = headLeft + _holdArcSegs / 2;
                        Vector3 tailCenter = pfRoot.TransformPoint(_vertScratch[tailMid]);
                        Vector3 headCenter = pfRoot.TransformPoint(_vertScratch[headMid]);
                        Debug.DrawLine(tailCenter, headCenter, Color.yellow);
                    }
                }

                // Draw the arc mesh. Vertices are in PlayfieldRoot local space;
                // localToWorld promotes them to world space for rendering.
                Graphics.DrawMesh(
                    arcMesh, localToWorld, bodyMaterial,
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
        // Hold arc mesh template
        // -------------------------------------------------------------------

        /// <summary>
        /// Builds a pooled arc-grid mesh with stable triangle topology.
        /// Grid: <c>(_holdArcSegs+1)</c> columns × <c>(_holdRadialSegs+1)</c> rows, row-major.
        /// Placeholder zero vertices are written here; LateUpdate overwrites positions and UVs
        /// every frame via <c>_vertScratch</c> / <c>_uvScratch</c> — no per-frame GC allocation.
        /// Winding: CCW, matching <see cref="LaneSurfaceRenderer"/> (v00→v10→v11, v00→v11→v01).
        /// </summary>
        private Mesh BuildHoldArcMesh()
        {
            var mesh = new Mesh { name = "HoldBodyArc" };

            // Placeholder zero vertices — overwritten every frame in LateUpdate.
            mesh.vertices = new Vector3[_vertsPerRibbon];
            mesh.uv       = new Vector2[_vertsPerRibbon];

            // Triangles: (_holdArcSegs × _holdRadialSegs) quads, each as two CCW tris.
            // Topology is fixed for the session — only vertices/UVs change per-frame.
            int numCols = _holdArcSegs + 1;
            var tris = new int[_indicesPerRibbon];
            int t = 0;
            for (int row = 0; row < _holdRadialSegs; row++)
            {
                for (int col = 0; col < _holdArcSegs; col++)
                {
                    int v00 =  row      * numCols + col;
                    int v10 = (row + 1) * numCols + col;
                    int v11 = (row + 1) * numCols + col + 1;
                    int v01 =  row      * numCols + col + 1;
                    tris[t++] = v00; tris[t++] = v10; tris[t++] = v11; // Tri A (CCW)
                    tris[t++] = v00; tris[t++] = v11; tris[t++] = v01; // Tri B (CCW)
                }
            }
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        // -------------------------------------------------------------------
        // Arc vertex fill
        // -------------------------------------------------------------------

        /// <summary>
        /// Fills <paramref name="verts"/> and <paramref name="uvs"/> with arc-conforming hold body
        /// geometry. Uses the same formula as <c>LaneSurfaceRenderer.FillLaneSectorVerts</c>:
        ///   <c>vertex = (ctr.x + cos(θ)·r, ctr.y + sin(θ)·r, FrustumZAtRadius(r) + NoteLayerZLift)</c>
        ///
        /// <para>Grid layout (row-major): rows sweep radially from <paramref name="tailR"/> (row 0)
        /// to <paramref name="headR"/> (row <paramref name="radSegs"/>); columns sweep angularly from
        /// <paramref name="leftDeg"/> to <paramref name="rightDeg"/>.</para>
        ///
        /// <para>No allocations — caller provides scratch arrays sized
        /// <c>(<paramref name="arcSegs"/>+1) × (<paramref name="radSegs"/>+1)</c>.</para>
        /// </summary>
        private static void FillHoldArcVerts(
            Vector3[]  verts,
            Vector2[]  uvs,
            float      tailR,
            float      headR,
            float      leftDeg,
            float      rightDeg,
            Vector2    ctr,
            float      arenaInnerLocal,
            float      arenaOuterLocal,
            float      hInner,
            float      hOuter,
            float      vTail,
            float      vHead,
            NoteSkinSet skin,
            int        arcSegs,
            int        radSegs)
        {
            int numCols = arcSegs + 1;
            float halfAngleRad  = (rightDeg - leftDeg) * 0.5f * Mathf.Deg2Rad;

            for (int row = 0; row <= radSegs; row++)
            {
                float rowFrac       = (float)row / radSegs;
                float r             = Mathf.Lerp(tailR, headR, rowFrac);
                float z             = NoteApproachMath.FrustumZAtRadius(
                                          r, arenaInnerLocal, arenaOuterLocal, hInner, hOuter)
                                      + NoteLayerZLift;
                float vCoord        = Mathf.Lerp(vTail, vHead, rowFrac);
                float totalChordAtR = 2f * r * Mathf.Sin(halfAngleRad);

                for (int col = 0; col <= arcSegs; col++)
                {
                    float uFrac         = (float)col / arcSegs;
                    float angleDeg      = Mathf.Lerp(leftDeg, rightDeg, uFrac);
                    float angleRad      = angleDeg * Mathf.Deg2Rad;
                    float cosA          = Mathf.Cos(angleRad);
                    float sinA          = Mathf.Sin(angleRad);
                    // Exact chord from left edge to this column's angle — used for UV mapping.
                    float deltaHalfRad  = (angleDeg - leftDeg) * 0.5f * Mathf.Deg2Rad;
                    float chordFromLeft = (r > 0f) ? 2f * r * Mathf.Sin(deltaHalfRad) : 0f;

                    int idx   = row * numCols + col;
                    verts[idx] = new Vector3(ctr.x + cosA * r, ctr.y + sinA * r, z);
                    uvs[idx]   = new Vector2(
                        NoteCapGeometryBuilder.ComputeHoldWidthU(chordFromLeft, totalChordAtR, skin),
                        vCoord);
                }
            }
        }
    }
}
