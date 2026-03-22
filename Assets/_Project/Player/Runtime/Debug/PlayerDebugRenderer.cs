// PlayerDebugRenderer.cs
// DEBUG SCAFFOLDING — remove before shipping.
//
// Draws arena arc bands, lane center rays, active note markers, and the last
// touch hit point in the Game view using LineRenderers.
//
// Geometry points are computed in PlayfieldRoot local XY, then converted to
// world space via:
//   playfieldRoot.TransformPoint(localX, localY, 0)
//
// This is intentionally simple: no fancy materials, no pooling beyond a fixed
// note-marker array, no per-frame allocations for the static arc/lane lines.
//
// Wiring (done in Unity Editor, not here):
//   1) Create an empty GameObject in the PlayerBoot scene named "DebugRenderer".
//   2) Add component PlayerDebugRenderer.
//   3) Assign the existing PlayerAppController to the Inspector field.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// DEBUG SCAFFOLDING: Visual overlay for arenas, lanes, notes, and touch hits.
    /// All geometry uses PlayfieldRoot local XY → world via TransformPoint (spec §5.4).
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Debug/PlayerDebugRenderer")]
    public class PlayerDebugRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("DEBUG SCAFFOLDING — remove before shipping")]

        [Tooltip("The PlayerAppController in the scene.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Optional: if assigned, arena/lane outlines are lifted to sit on the frustum " +
                 "surface. If null, all lines are drawn at z=0 (flat interaction plane).")]
        [SerializeField] private PlayerDebugArenaSurface arenaSurface;

        [Tooltip("Number of line segments used to approximate each arc (higher = smoother).")]
        [SerializeField] private int arcSegments = 48;

        [Tooltip("Width of all debug lines in world units.")]
        [SerializeField] private float lineWidth = 0.01f;

        [Tooltip("Half-size of note / touch-hit diamond markers in world units.")]
        [SerializeField] private float markerHalfSize = 0.04f;

        [Tooltip("Maximum simultaneous note markers drawn (pool size).")]
        [SerializeField] private int maxNoteMarkers = 32;

        [Header("Note Approach (DEBUG)")]
        [Tooltip("How many ms before a note's hit time it becomes visible and starts approaching.")]
        [SerializeField] private int noteLeadTimeMs = 2000;

        [Tooltip("Spawn radius as a fraction of band width from the inner edge (0 = inner, 1 = outer).")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnRadiusFactor = 0f;

        [Tooltip("If true, show notes within noteLeadTimeMs even when outside the narrow judgement window.")]
        [SerializeField] private bool showNotesOutsideWindow = true;

        [Header("Flick Direction Arrows (DEBUG)")]

        [Tooltip("Draw a short arrow at each visible flick note showing its expected gesture direction.")]
        [SerializeField] private bool showFlickDirectionArrows = true;

        [Tooltip("Arrow length in normalized playfield units (same scale as arena radii).")]
        [SerializeField] private float flickArrowLengthNorm = 0.08f;

        [Tooltip("Extra local Z added to the arrow above the note marker surface, to prevent z-fighting.")]
        [SerializeField] private float flickArrowZBias = 0.002f;

        [Header("Hit Band Arcs (DEBUG)")]
        [Tooltip("Draw inner and outer hit-band arcs per arena (input-only; never affects judgement).")]
        [SerializeField] private bool showHitBandArcs = true;

        [Tooltip("Color of the hit-band inner and outer debug arcs.")]
        [SerializeField] private Color hitBandColor = Color.green;

        [Header("Lane Edge Lines (DEBUG)")]
        [Tooltip("Draw left/right boundary radial lines per lane wedge (innerLocal → visualOuterLocal).")]
        [SerializeField] private bool showLaneEdges = true;

        [Tooltip("Draw inner-ring and judgement-ring arc segments spanning each lane wedge.")]
        [SerializeField] private bool showLaneArcs = true;

        [Tooltip("Color of lane boundary lines and arcs (distinct from the center-ray laneColor).")]
        [SerializeField] private Color laneEdgeColor = new Color(1f, 0.7f, 0f); // amber

        [Header("Touch Lane Highlight (DEBUG)")]
        [Tooltip("Highlight the edge lines of any lane whose wedge contains the current touch.")]
        [SerializeField] private bool showLaneHighlight = true;

        [Tooltip("When true, use the hit band (judgement-centred) for radial containment; " +
                 "when false, use the full chart band (innerLocal..visualOuterLocal).")]
        [SerializeField] private bool useHitBandForLaneHighlight = true;

        [Tooltip("How long (seconds) lane edges stay highlighted after the touch enters the wedge.")]
        [SerializeField] private float laneHighlightDuration = 0.1f;

        [Tooltip("Color applied to a lane's boundary lines when the touch is inside the wedge.")]
        [SerializeField] private Color laneHighlightColor = Color.green;

        [Header("Colors")]
        [SerializeField] private Color arenaColor         = Color.cyan;
        [SerializeField] private Color laneColor          = Color.yellow;
        [SerializeField] private Color tapColor           = Color.white;
        [SerializeField] private Color flickColor         = new Color(0f, 1f, 0.5f);    // teal
        [SerializeField] private Color catchColor         = Color.magenta;
        [SerializeField] private Color holdColor          = new Color(0.3f, 0.6f, 1f);  // blue
        [SerializeField] private Color touchColor         = Color.red;
        [SerializeField] private Color flickArrowColor    = Color.green;
        [SerializeField] private Color judgementRingColor = new Color(0.85f, 0.6f, 1f); // purple-white

        [Header("Hold Note Visuals (DEBUG)")]
        [Tooltip("Draw a rail segment from hold start to hold end along the lane center.")]
        [SerializeField] private bool showHoldRails = true;
        [Tooltip("(Reserved) Draw tick markers on the hold rail — not yet implemented.")]
        [SerializeField] private bool showHoldTicks = true;

        [Header("Judgement Flashes (DEBUG)")]
        [Tooltip("Duration in seconds that each judgement flash remains visible.")]
        [SerializeField] private float flashDuration         = 0.35f;

        [Tooltip("Half-size of judgement flash diamond markers in world units.")]
        [SerializeField] private float flashHalfSize         = 0.08f;

        [SerializeField] private Color flashPerfectPlusColor = new Color(1f, 0.9f, 0f); // gold
        [SerializeField] private Color flashPerfectColor     = Color.yellow;
        [SerializeField] private Color flashGreatColor       = Color.cyan;
        [SerializeField] private Color flashMissColor        = Color.red;

        [Header("Geometry Snapshot (DEBUG)")]

        [Tooltip("When enabled, displays a compact readout of evaluated arena + lane geometry " +
                 "in the bottom-left of the Game view, updated once per debugSnapshotIntervalSeconds. " +
                 "Shows arcStartDeg / arcSweepDeg, outerLocal / innerLocal / judgementRadiusLocal, " +
                 "frustum Z heights, and lane center/width/edge angles. " +
                 "Reads from ChartRuntimeEvaluator directly — the same source used by " +
                 "JudgementRingRenderer, NoteApproachRenderer, and ArenaHitTester (spec §5.9). " +
                 "Does NOT require DebugShowTouchBand to be on.")]
        [SerializeField] private bool debugShowEvaluatedGeometrySnapshot = false;

        [Tooltip("How often (seconds) the snapshot text is refreshed. " +
                 "Default 1.0 s means you can watch a slowly-animated chart and see the values " +
                 "change each second. " +
                 "Clamped to a minimum of 0.1 s to avoid update-storm in fast tests.")]
        [SerializeField] private float debugSnapshotIntervalSeconds = 1.0f;

        // -------------------------------------------------------------------
        // Internal state
        // -------------------------------------------------------------------

        // True once arena/lane LineRenderers have been built from geometry.
        private bool _geometryBuilt;

        // Shared unlit material — visible in Game view on all platforms.
        private Material _lineMat;

        // Per-arena: 7 LineRenderers — [0] outer arc, [1] inner arc,
        //                              [2] start-angle ray, [3] end-angle ray,
        //                              [4] judgement ring arc,
        //                              [5] hit band inner arc, [6] hit band outer arc.
        private readonly Dictionary<string, LineRenderer[]> _arenaLRs =
            new Dictionary<string, LineRenderer[]>(StringComparer.Ordinal);

        // Per-lane: 5 LineRenderers — [0] center ray,
        //                              [1] left edge radial, [2] right edge radial,
        //                              [3] inner arc (lane wedge span), [4] judgement arc.
        private readonly Dictionary<string, LineRenderer[]> _laneLRs =
            new Dictionary<string, LineRenderer[]>(StringComparer.Ordinal);

        // Expire time per laneId for the touch containment highlight.
        private readonly Dictionary<string, float> _laneHighlightExpire =
            new Dictionary<string, float>(StringComparer.Ordinal);

        // Fixed pool of note-marker LineRenderers (diamond shape).
        // Markers beyond maxNoteMarkers are silently dropped — debug acceptable.
        private LineRenderer[] _notePool;

        // Single touch-hit marker LineRenderer (yellow diamond = visual-surface or plane hit).
        private LineRenderer _touchLR;

        // Parallax debug: grey diamond at the flat-plane projection, and orange line connecting
        // plane point to surface hit point. Shown only when DebugShowInputProjection = true.
        private LineRenderer _touchPlaneLR;
        private LineRenderer _touchParallaxLineLR;

        // Pool of flick direction arrow LineRenderers (parallel to _notePool; index-aligned).
        private LineRenderer[] _flickArrowPool;

        // Hold note duration rail (2-point line, start → end endpoint; parallel to _notePool).
        private LineRenderer[] _holdRailPool;

        // Hold note end-marker diamonds (parallel to _notePool).
        private LineRenderer[] _holdEndMarkerPool;

        // -------------------------------------------------------------------
        // Judgement flash state (ring buffer — no per-flash allocation)
        // -------------------------------------------------------------------

        // One flash record per recent judgement.
        private struct JudgementFlash
        {
            /// <summary>LaneId of the judged note; used to look up world position.</summary>
            public string        LaneId;

            /// <summary>Judgement tier for colour selection.</summary>
            public JudgementTier Tier;

            /// <summary>True when Tier == Perfect and hit was within PerfectPlus sub-window.</summary>
            public bool          IsPerfectPlus;

            /// <summary>Time.time value at which this flash should stop rendering.</summary>
            public float         ExpireTime;
        }

        // Fixed capacity — overwrites oldest entry when full.
        private const int               MaxFlashes = 16;
        private readonly JudgementFlash[] _flashes = new JudgementFlash[MaxFlashes];
        private int                       _flashHead = 0;     // next write index (ring)
        private LineRenderer[]            _flashPool;         // built in TryBuildStaticGeometry

        // Debug counters for OnGUI hold readout — updated each frame in UpdateNoteMarkers.
        private int _debugHoldPresent;   // Active hold notes in note source that passed State filter
        private int _debugHoldRendered;  // Hold notes that passed all filters and were drawn

        // -------------------------------------------------------------------
        // Geometry snapshot state
        // -------------------------------------------------------------------
        // Updated at most once per debugSnapshotIntervalSeconds — not every frame.
        // _snapshotText is the cached display string; OnGUI reads it every frame without
        // rebuilding.  _snapshotSb is pre-allocated and reused on each rebuild so the
        // only GC allocation per interval is the single _snapshotSb.ToString() call.

        // Elapsed unscaled time since the last snapshot rebuild.
        // Unscaled so the readout still updates when timeScale = 0 (editor pause).
        private float _snapshotTimer;

        // Most-recently built display string; null until the first interval fires.
        private string _snapshotText;

        // Number of '\n'-delimited lines in _snapshotText (cached to avoid per-frame counting).
        private int _snapshotLineCount;

        // Pre-allocated StringBuilder — cleared and reused each rebuild.
        private StringBuilder _snapshotSb;
        private const int SnapshotSbCapacity = 512;

        // Pixel height of a single OnGUI label row.  Centralised here so all OnGUI
        // drawing uses the same value without re-declaring a local in every if-block
        // (which triggers CS0136 when two sibling scopes share a name).
        private const int GuiLineHeight = 18;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // "Sprites/Default" is unlit and requires no extra shader setup.
            _lineMat = new Material(Shader.Find("Sprites/Default"));
            _lineMat.hideFlags = HideFlags.HideAndDontSave;

            // Pre-allocate the snapshot StringBuilder so no heap allocation happens on the
            // first rebuild.  Capacity 512 is comfortably larger than a 5-line snapshot.
            _snapshotSb = new StringBuilder(SnapshotSbCapacity);
        }

        private void OnDestroy()
        {
            if (_lineMat != null) { Destroy(_lineMat); }
        }

        private void OnEnable()
        {
            if (playerAppController != null)
                playerAppController.OnJudgement += OnJudgementReceived;
        }

        private void OnDisable()
        {
            if (playerAppController != null)
                playerAppController.OnJudgement -= OnJudgementReceived;
        }

        // Called by PlayerAppController.OnJudgement; writes into the ring buffer.
        // Allocation-free: no new structs on the heap (JudgementFlash is a value type).
        private void OnJudgementReceived(JudgementRecord record)
        {
            int slot = _flashHead % MaxFlashes;
            _flashes[slot] = new JudgementFlash
            {
                LaneId        = record.Note.LaneId,
                Tier          = record.Tier,
                IsPerfectPlus = record.IsPerfectPlus,
                ExpireTime    = Time.time + flashDuration,
            };
            _flashHead++;
        }

        private void LateUpdate()
        {
            // LateUpdate runs after PlayerAppController.Update(), so note state
            // is already this frame's when we read DebugActiveNotes.

            if (playerAppController == null) { return; }

            // Defer geometry build until PlayerAppController.Start() has run and
            // populated the geometry dictionaries (they are null before then).
            if (!_geometryBuilt)
            {
                TryBuildStaticGeometry();
                return; // nothing to draw yet
            }

            UpdateArenaLineRenderers(); // re-position arena arcs/rays/ring from current evaluated geometry
            UpdateHitBandArcs();        // must run every frame — radii depend on runtime settings
            UpdateLaneLineRenderers();  // re-position edge/center lines from current evaluated geometry
            UpdateNoteMarkers();
            UpdateTouchMarker();
            UpdateLaneHighlights();
            UpdateJudgementFlashes();
            UpdateGeometrySnapshot();  // timer-gated; rebuilds cached string at most once per interval
        }

        // Repositions arena LineRenderers [0]–[4] from the current evaluated arena geometry
        // each frame.  Without this, those renderers would be frozen at time-0 values while
        // lane arcs (updated by UpdateLaneLineRenderers) and production JudgementRingRenderer
        // correctly follow animated tracks.
        //
        // Mirrors UpdateLaneLineRenderers but for the arena-level overlays:
        //   [0] outer arc at visualOuterLocal
        //   [1] inner arc at innerLocal
        //   [2] radial ray at arcStartDeg
        //   [3] radial ray at arcStartDeg + arcSweepDeg
        //   [4] judgement ring arc at judgementRadiusLocal (single source of truth via
        //       ArenaHitTester.ComputeHitBandLocal — same formula as all other ring visuals)
        //
        // [5] and [6] (hit-band arcs) are handled separately by UpdateHitBandArcs().
        private void UpdateArenaLineRenderers()
        {
            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            PlayfieldTransform pfT    = playerAppController.DebugPlayfieldTransform;
            Transform          pfRoot = playerAppController.playfieldRoot;
            if (arenas == null || pfT == null) { return; }

            foreach (KeyValuePair<string, LineRenderer[]> kvp in _arenaLRs)
            {
                if (!arenas.TryGetValue(kvp.Key, out ArenaGeometry geo)) { continue; }

                LineRenderer[] lrs = kvp.Value;
                if (lrs == null || lrs.Length < 5) { continue; }

                // All radius math in PlayfieldLocal to stay aspect-safe (spec §5.5).
                float outerLocal = pfT.NormRadiusToLocal(geo.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(geo.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Single source of truth for judgementRadiusLocal and visualOuterLocal —
                // same helper used by JudgementEngine, UpdateHitBandArcs, and BuildArenaLineRenderers.
                ArenaHitTester.ComputeHitBandLocal(geo, pfT,
                    out _, out _, out float judgementRadiusLocal, out float visualOuterLocal);

                // s01: normalised [0=inner, 1=outer] position used for frustum Z only. VISUAL ONLY.
                float s01Judgement = (outerLocal > innerLocal)
                    ? Mathf.Clamp01((judgementRadiusLocal - innerLocal) / (outerLocal - innerLocal))
                    : 0f;

                Vector2 center = pfT.NormalizedToLocal(new Vector2(geo.CenterXNorm, geo.CenterYNorm));

                // [0] outer arc at the visual outer rim.
                SetArcPositions(lrs[0], center, visualOuterLocal,
                                geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: 1f);

                // [1] inner arc at the chart inner edge.
                SetArcPositions(lrs[1], center, innerLocal,
                                geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: 0f);

                // [2] radial boundary ray at arc start angle.
                SetRadialPositions(lrs[2], center, innerLocal, visualOuterLocal,
                                   geo.ArcStartDeg, pfRoot);

                // [3] radial boundary ray at arc end angle (start + sweep).
                SetRadialPositions(lrs[3], center, innerLocal, visualOuterLocal,
                                   geo.ArcStartDeg + geo.ArcSweepDeg, pfRoot);

                // [4] judgement ring arc — where notes land visually (VISUAL ONLY, spec §5.8).
                // This is the arena-level ring; lane judgement arcs drawn by UpdateLaneLineRenderers
                // are exact subsets spanning each lane's wedge at the same radius.
                SetArcPositions(lrs[4], center, judgementRadiusLocal,
                                geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: s01Judgement);
            }
        }

        // Refreshes lrs[5] (hitInner) and lrs[6] (hitOuter) positions each frame.
        // Required because HitBandInnerCoverage01 and related settings are runtime-
        // changeable statics; baking positions once at build time is not sufficient.
        private void UpdateHitBandArcs()
        {
            if (!showHitBandArcs) { return; }

            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            PlayfieldTransform pfT   = playerAppController.DebugPlayfieldTransform;
            Transform          pfRoot = playerAppController.playfieldRoot;
            if (arenas == null || pfT == null) { return; }

            foreach (KeyValuePair<string, ArenaGeometry> kvp in arenas)
            {
                if (!_arenaLRs.TryGetValue(kvp.Key, out LineRenderer[] lrs)) { continue; }
                if (lrs == null || lrs.Length < 7)                            { continue; }

                ArenaGeometry geo = kvp.Value;

                // Single source of truth — same call as JudgementEngine and lane highlight.
                ArenaHitTester.ComputeHitBandLocal(geo, pfT,
                    out float hitInner, out float hitOuter, out _, out _);

                float outerLocal = pfT.NormRadiusToLocal(geo.OuterRadiusNorm);
                float innerLocal = outerLocal - pfT.NormRadiusToLocal(geo.BandThicknessNorm);

                // s01: normalized [0=inner, 1=outer] for frustum Z interpolation. VISUAL ONLY.
                float s01HitInner = (outerLocal > innerLocal)
                    ? Mathf.Clamp01((hitInner - innerLocal) / (outerLocal - innerLocal)) : 0f;
                float s01HitOuter = (outerLocal > innerLocal)
                    ? Mathf.Clamp01((hitOuter - innerLocal) / (outerLocal - innerLocal)) : 1f;

                Vector2 center = pfT.NormalizedToLocal(new Vector2(geo.CenterXNorm, geo.CenterYNorm));

                SetArcPositions(lrs[5], center, hitInner,
                    geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: s01HitInner);
                SetArcPositions(lrs[6], center, hitOuter,
                    geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: s01HitOuter);
            }
        }

        // Repositions all 5 lane LineRenderers from the current evaluated lane/arena geometry.
        // Mirrors BuildLaneLineRenderer but runs every frame so animated lanes stay in sync.
        private void UpdateLaneLineRenderers()
        {
            if (!showLaneEdges && !showLaneArcs) { return; }

            IReadOnlyDictionary<string, LaneGeometry>  lanes  = playerAppController.DebugLaneGeometries;
            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            IReadOnlyDictionary<string, string>        lToA   = playerAppController.DebugLaneToArena;
            PlayfieldTransform pfT    = playerAppController.DebugPlayfieldTransform;
            Transform          pfRoot = playerAppController.playfieldRoot;
            if (lanes == null || arenas == null || lToA == null || pfT == null) { return; }

            foreach (KeyValuePair<string, LineRenderer[]> kvp in _laneLRs)
            {
                string         laneId = kvp.Key;
                LineRenderer[] lrs    = kvp.Value;
                if (lrs == null || lrs.Length < 5) { continue; }

                if (!lanes.TryGetValue(laneId, out LaneGeometry lane))      { continue; }
                if (!lToA.TryGetValue(laneId, out string arenaId))           { continue; }
                if (!arenas.TryGetValue(arenaId, out ArenaGeometry arena))   { continue; }

                float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;
                float minDim     = pfT.MinDimLocal;
                Vector2 center   = pfT.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                float visualOuterLocal     = outerLocal + PlayerSettingsStore.VisualOuterExpandNorm * minDim;
                float judgementRadiusLocal = outerLocal - PlayerSettingsStore.JudgementInsetNorm * minDim;
                float s01Judgement         = (outerLocal > innerLocal)
                    ? Mathf.Clamp01((judgementRadiusLocal - innerLocal) / (outerLocal - innerLocal))
                    : 0f;

                float leftDeg  = lane.CenterDeg - lane.WidthDeg * 0.5f;
                float rightDeg = lane.CenterDeg + lane.WidthDeg * 0.5f;

                // lrs[0]: center ray, inner → judgement ring.
                SetRadialPositions(lrs[0], center, innerLocal, judgementRadiusLocal,
                                   lane.CenterDeg, pfRoot, outerS01: s01Judgement);

                // lrs[1]: left boundary edge, inner → visualOuter.
                SetRadialPositions(lrs[1], center, innerLocal, visualOuterLocal,
                                   leftDeg, pfRoot, outerS01: 1f);

                // lrs[2]: right boundary edge, inner → visualOuter.
                SetRadialPositions(lrs[2], center, innerLocal, visualOuterLocal,
                                   rightDeg, pfRoot, outerS01: 1f);

                // lrs[3]: inner arc spanning the lane wedge.
                SetArcPositions(lrs[3], center, innerLocal, leftDeg, lane.WidthDeg, pfRoot, s01: 0f);

                // lrs[4]: judgement arc spanning the lane wedge.
                SetArcPositions(lrs[4], center, judgementRadiusLocal, leftDeg, lane.WidthDeg,
                                pfRoot, s01: s01Judgement);
            }
        }

        // -------------------------------------------------------------------
        // Static geometry build (one-shot, deferred until controller is ready)
        // -------------------------------------------------------------------

        private void TryBuildStaticGeometry()
        {
            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            IReadOnlyDictionary<string, LaneGeometry>  lanes  = playerAppController.DebugLaneGeometries;
            IReadOnlyDictionary<string, string>        lToA   = playerAppController.DebugLaneToArena;
            PlayfieldTransform                         pfT    = playerAppController.DebugPlayfieldTransform;

            // Not ready yet — controller hasn't finished Start().
            if (arenas == null || lanes == null || lToA == null || pfT == null) { return; }

            Transform pfRoot = playerAppController.playfieldRoot;

            // Arena arc bands — 4 LRs each.
            foreach (KeyValuePair<string, ArenaGeometry> kvp in arenas)
            {
                BuildArenaLineRenderers(kvp.Key, kvp.Value, pfT, pfRoot);
            }

            // Lane center rays — 1 LR each.
            foreach (KeyValuePair<string, LaneGeometry> kvp in lanes)
            {
                if (!lToA.TryGetValue(kvp.Key, out string arenaId))        { continue; }
                if (!arenas.TryGetValue(arenaId, out ArenaGeometry arena))  { continue; }
                BuildLaneLineRenderer(kvp.Key, kvp.Value, arena, pfT, pfRoot);
            }

            // Note marker pool (diamonds, reused each LateUpdate).
            _notePool = new LineRenderer[maxNoteMarkers];
            for (int i = 0; i < maxNoteMarkers; i++)
            {
                _notePool[i] = CreateLineRenderer($"NoteMarker_{i}", tapColor);
                _notePool[i].positionCount = 5;       // diamond: 4 corners + close
                _notePool[i].gameObject.SetActive(false);
            }

            // Flick direction arrow pool (parallel to _notePool, index-aligned).
            _flickArrowPool = new LineRenderer[maxNoteMarkers];
            for (int i = 0; i < maxNoteMarkers; i++)
            {
                _flickArrowPool[i] = CreateLineRenderer($"FlickArrow_{i}", flickArrowColor);
                _flickArrowPool[i].positionCount = 2;
                _flickArrowPool[i].gameObject.SetActive(false);
            }

            // Hold duration rail pool (2-point line; index-aligned with _notePool).
            _holdRailPool = new LineRenderer[maxNoteMarkers];
            for (int i = 0; i < maxNoteMarkers; i++)
            {
                _holdRailPool[i] = CreateLineRenderer($"HoldRail_{i}", holdColor);
                _holdRailPool[i].positionCount = 2;
                _holdRailPool[i].startWidth = lineWidth * 3f; // VISUAL ONLY — thicker than lane lines
                _holdRailPool[i].endWidth   = lineWidth * 3f;
                _holdRailPool[i].gameObject.SetActive(false);
            }

            // Hold end marker pool (diamond at hold's end timestamp position; index-aligned).
            _holdEndMarkerPool = new LineRenderer[maxNoteMarkers];
            for (int i = 0; i < maxNoteMarkers; i++)
            {
                _holdEndMarkerPool[i] = CreateLineRenderer($"HoldEndMarker_{i}", holdColor);
                _holdEndMarkerPool[i].positionCount = 5;
                _holdEndMarkerPool[i].gameObject.SetActive(false);
            }

            // Touch hit marker (diamond — yellow; shows the actual hit point used for gameplay).
            _touchLR = CreateLineRenderer("TouchMarker", touchColor);
            _touchLR.positionCount = 5;
            _touchLR.gameObject.SetActive(false);

            // Parallax debug: flat-plane projection point (grey diamond).
            _touchPlaneLR = CreateLineRenderer("TouchPlaneMarker", new Color(0.55f, 0.55f, 0.55f));
            _touchPlaneLR.positionCount = 5;
            _touchPlaneLR.gameObject.SetActive(false);

            // Parallax debug: orange line connecting flat-plane point to surface-hit point.
            _touchParallaxLineLR = CreateLineRenderer("TouchParallaxLine", new Color(1f, 0.5f, 0f));
            _touchParallaxLineLR.positionCount = 2;
            _touchParallaxLineLR.gameObject.SetActive(false);

            // Judgement flash pool (ring buffer; colours set per-flash in UpdateJudgementFlashes).
            _flashPool = new LineRenderer[MaxFlashes];
            for (int i = 0; i < MaxFlashes; i++)
            {
                _flashPool[i] = CreateLineRenderer($"FlashMarker_{i}", Color.white);
                _flashPool[i].positionCount = 5;
                _flashPool[i].gameObject.SetActive(false);
            }

            _geometryBuilt = true;
        }

        // -------------------------------------------------------------------
        // Arena builders
        // -------------------------------------------------------------------

        // Builds 7 LineRenderers for one arena:
        //   [0] outer arc polyline at visualOuterLocal  (chart outer + VisualOuterExpandNorm)
        //   [1] inner arc polyline at innerLocal
        //   [2] radial ray at arcStartDeg               (inner → visualOuterLocal)
        //   [3] radial ray at arcStartDeg + arcSweepDeg (inner → visualOuterLocal)
        //   [4] judgement ring arc at judgementRadiusLocal  (outerLocal − JudgementInsetNorm)
        //   [5] hit band inner arc at hitInnerLocal  (INPUT-ONLY — never used for hit-testing)
        //   [6] hit band outer arc at hitOuterLocal  (INPUT-ONLY — never used for hit-testing)
        private void BuildArenaLineRenderers(
            string arenaId, ArenaGeometry geo, PlayfieldTransform pfT, Transform pfRoot)
        {
            // Compute local-unit radii (spec §5.5).
            float outerLocal = pfT.NormRadiusToLocal(geo.OuterRadiusNorm);
            float bandLocal  = pfT.NormRadiusToLocal(geo.BandThicknessNorm);
            float innerLocal = outerLocal - bandLocal;

            // Shared hit-band + visual radii — same values as JudgementEngine and lane highlight.
            ArenaHitTester.ComputeHitBandLocal(geo, pfT,
                out float hitInnerLocal, out float hitOuterLocal,
                out float judgementRadiusLocal, out float visualOuterLocal);

            float minDim = pfT.MinDimLocal; // needed for s01 only (frustum Z, VISUAL ONLY)

            // s01: normalized [0=innerLocal, 1=outerLocal] — for frustum Z only, VISUAL ONLY.
            float s01Judgement = (outerLocal > innerLocal)
                ? Mathf.Clamp01((judgementRadiusLocal - innerLocal) / (outerLocal - innerLocal))
                : 0f;
            // s01 for hit band (clamped — hitInner/hitOuter may fall outside [inner, outer]).
            float s01HitInner = (outerLocal > innerLocal)
                ? Mathf.Clamp01((hitInnerLocal - innerLocal) / (outerLocal - innerLocal))
                : 0f;
            float s01HitOuter = (outerLocal > innerLocal)
                ? Mathf.Clamp01((hitOuterLocal - innerLocal) / (outerLocal - innerLocal))
                : 1f;

            // Arena center in PlayfieldRoot local XY (spec §5.5).
            Vector2 center = pfT.NormalizedToLocal(new Vector2(geo.CenterXNorm, geo.CenterYNorm));

            var lrs = new LineRenderer[7];

            // Outer arc drawn at the visual outer rim.
            lrs[0] = CreateLineRenderer($"Arena_{arenaId}_OuterArc", arenaColor);
            SetArcPositions(lrs[0], center, visualOuterLocal, geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: 1f);

            lrs[1] = CreateLineRenderer($"Arena_{arenaId}_InnerArc", arenaColor);
            SetArcPositions(lrs[1], center, innerLocal, geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: 0f);

            // Arc boundary rays — inner → visualOuterLocal so they bound the full visual arc.
            lrs[2] = CreateLineRenderer($"Arena_{arenaId}_StartRay", arenaColor);
            SetRadialPositions(lrs[2], center, innerLocal, visualOuterLocal, geo.ArcStartDeg, pfRoot);

            // Raw un-normalized angle is fine — cos/sin handle any float correctly.
            lrs[3] = CreateLineRenderer($"Arena_{arenaId}_EndRay", arenaColor);
            SetRadialPositions(lrs[3], center, innerLocal, visualOuterLocal,
                               geo.ArcStartDeg + geo.ArcSweepDeg, pfRoot);

            // Judgement ring: distinct thin arc where notes land and judgement occurs. VISUAL ONLY.
            lrs[4] = CreateLineRenderer($"Arena_{arenaId}_JudgementRing", judgementRingColor);
            SetArcPositions(lrs[4], center, judgementRadiusLocal,
                            geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: s01Judgement);

            // Hit band inner/outer arcs: show the actual input acceptance zone. INPUT-ONLY DEBUG.
            lrs[5] = CreateLineRenderer($"Arena_{arenaId}_HitBandInner", hitBandColor);
            SetArcPositions(lrs[5], center, hitInnerLocal,
                            geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: s01HitInner);
            lrs[5].gameObject.SetActive(showHitBandArcs);

            lrs[6] = CreateLineRenderer($"Arena_{arenaId}_HitBandOuter", hitBandColor);
            SetArcPositions(lrs[6], center, hitOuterLocal,
                            geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: s01HitOuter);
            lrs[6].gameObject.SetActive(showHitBandArcs);

            _arenaLRs[arenaId] = lrs;
        }

        // -------------------------------------------------------------------
        // Lane builder
        // -------------------------------------------------------------------

        // Builds 5 LineRenderers for one lane:
        //   [0] center ray   — from innerLocal to judgementRadiusLocal along lane.CenterDeg
        //   [1] left edge    — radial from innerLocal to visualOuterLocal at leftDeg
        //   [2] right edge   — radial from innerLocal to visualOuterLocal at rightDeg
        //   [3] inner arc    — arc spanning [leftDeg, rightDeg] at innerLocal
        //   [4] judgement arc — arc spanning [leftDeg, rightDeg] at judgementRadiusLocal
        // Lane edges can overlap between adjacent lanes; all are drawn unconditionally.
        private void BuildLaneLineRenderer(
            string laneId, LaneGeometry lane, ArenaGeometry arena,
            PlayfieldTransform pfT, Transform pfRoot)
        {
            float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
            float bandLocal  = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
            float innerLocal = outerLocal - bandLocal;
            float minDim     = pfT.MinDimLocal;
            Vector2 center   = pfT.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

            float visualOuterLocal     = outerLocal + PlayerSettingsStore.VisualOuterExpandNorm * minDim;
            float judgementRadiusLocal = outerLocal - PlayerSettingsStore.JudgementInsetNorm * minDim;
            float s01Judgement         = (outerLocal > innerLocal)
                ? Mathf.Clamp01((judgementRadiusLocal - innerLocal) / (outerLocal - innerLocal))
                : 0f;

            // Left/right boundary angles from the lane center.
            float leftDeg  = lane.CenterDeg - lane.WidthDeg * 0.5f;
            float rightDeg = lane.CenterDeg + lane.WidthDeg * 0.5f;

            var lrs = new LineRenderer[5];

            // Center ray: inner → judgement ring. VISUAL ONLY.
            lrs[0] = CreateLineRenderer($"Lane_{laneId}_Center", laneColor);
            SetRadialPositions(lrs[0], center, innerLocal, judgementRadiusLocal,
                               lane.CenterDeg, pfRoot, outerS01: s01Judgement);

            // Left boundary edge: inner → visualOuter at leftDeg.
            lrs[1] = CreateLineRenderer($"Lane_{laneId}_LeftEdge", laneEdgeColor);
            SetRadialPositions(lrs[1], center, innerLocal, visualOuterLocal, leftDeg, pfRoot, outerS01: 1f);
            lrs[1].gameObject.SetActive(showLaneEdges);

            // Right boundary edge: inner → visualOuter at rightDeg.
            lrs[2] = CreateLineRenderer($"Lane_{laneId}_RightEdge", laneEdgeColor);
            SetRadialPositions(lrs[2], center, innerLocal, visualOuterLocal, rightDeg, pfRoot, outerS01: 1f);
            lrs[2].gameObject.SetActive(showLaneEdges);

            // Inner arc spanning the lane wedge at the chart inner edge.
            lrs[3] = CreateLineRenderer($"Lane_{laneId}_InnerArc", laneEdgeColor);
            SetArcPositions(lrs[3], center, innerLocal, leftDeg, lane.WidthDeg, pfRoot, s01: 0f);
            lrs[3].gameObject.SetActive(showLaneArcs);

            // Judgement arc spanning the lane wedge at judgementRadiusLocal.
            lrs[4] = CreateLineRenderer($"Lane_{laneId}_JudgementArc", laneEdgeColor);
            SetArcPositions(lrs[4], center, judgementRadiusLocal, leftDeg, lane.WidthDeg, pfRoot, s01: s01Judgement);
            lrs[4].gameObject.SetActive(showLaneArcs);

            _laneLRs[laneId] = lrs;
        }

        // -------------------------------------------------------------------
        // Per-frame: note markers
        // -------------------------------------------------------------------

        // Draws note diamonds approaching the judgement ring (judgementRadiusLocal).
        // judgementRadiusLocal = outerLocal - JudgementInsetNorm*minDimLocal  (VISUAL ONLY).
        //
        // Approach formula:
        //   timeToHitMs = note.PrimaryTimeMs - effectiveChartTimeMs
        //   alpha       = 1 - clamp01(timeToHitMs / noteLeadTimeMs)
        //               → 0 at spawn (timeToHitMs == noteLeadTimeMs)
        //               → 1 at hit   (timeToHitMs == 0)
        //   spawnR      = innerLocal + spawnRadiusFactor * (judgementRadiusLocal - innerLocal)
        //   r           = lerp(spawnR, judgementRadiusLocal, alpha)
        //
        // With showNotesOutsideWindow=true we read DebugAllNotes (all Active notes up to
        // ActivationLeadMs away) and filter to noteLeadTimeMs ourselves, giving a smooth
        // approach well before the note enters the narrow judgement window.
        private void UpdateNoteMarkers()
        {
            if (_notePool == null) { return; }

            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            IReadOnlyDictionary<string, LaneGeometry>  lanes  = playerAppController.DebugLaneGeometries;
            IReadOnlyDictionary<string, string>        lToA   = playerAppController.DebugLaneToArena;
            PlayfieldTransform                         pfT    = playerAppController.DebugPlayfieldTransform;
            Transform                                  pfRoot = playerAppController.playfieldRoot;

            if (arenas == null || lanes == null || lToA == null || pfT == null)
            {
                DisableAllNoteMarkers();
                return;
            }

            // Broader source shows approaching notes; narrow source shows only hittable ones.
            IReadOnlyList<RuntimeNote> noteSource = showNotesOutsideWindow
                ? playerAppController.DebugAllNotes
                : playerAppController.DebugActiveNotes;

            double chartTimeMs   = playerAppController.DebugEffectiveChartTimeMs;
            double greatWindowMs = playerAppController.DebugGreatWindowMs;

            int poolIdx = 0;
            _debugHoldPresent  = 0;
            _debugHoldRendered = 0;

            if (noteSource != null)
            {
                for (int i = 0; i < noteSource.Count && poolIdx < _notePool.Length; i++)
                {
                    RuntimeNote note = noteSource[i];

                    // Only draw notes that are in the Active lifecycle state.
                    if (note.State != NoteState.Active) { continue; }

                    bool isHold = note.Type == NoteType.Hold;
                    if (isHold) { _debugHoldPresent++; }

                    // Hold notes use EndTimeMs for the "past miss" cut-off (a hold's start time
                    // can be well in the past while the note is actively held).
                    // Bound holds are never culled by the time window — always stay visible.
                    double timeToHitMs;
                    if (isHold)
                    {
                        double startToHit = note.StartTimeMs - chartTimeMs;
                        double endToHit   = note.EndTimeMs   - chartTimeMs;
                        bool   isHolding  = note.HoldBind == HoldBindState.Bound;

                        // Cull if start is beyond the approach window AND not currently held.
                        if (!isHolding && startToHit > noteLeadTimeMs) { continue; }
                        // Cull once the end has fully cleared the miss window.
                        if (endToHit < -greatWindowMs) { continue; }

                        // Use startToHit for alpha; Clamp01 will map past values → alpha=1
                        // (start marker sits at judgement ring once the hold is in progress).
                        timeToHitMs = startToHit;
                    }
                    else
                    {
                        // Standard tap / flick / catch filter.
                        timeToHitMs = note.PrimaryTimeMs - chartTimeMs;
                        if (timeToHitMs > noteLeadTimeMs) { continue; }
                        if (timeToHitMs < -greatWindowMs) { continue; }
                    }

                    if (!lanes.TryGetValue(note.LaneId, out LaneGeometry lane))   { continue; }
                    if (!lToA.TryGetValue(note.LaneId,  out string arenaId))      { continue; }
                    if (!arenas.TryGetValue(arenaId,    out ArenaGeometry arena)) { continue; }

                    float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                    float bandLocal  = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                    float innerLocal = outerLocal - bandLocal;

                    // Notes land on the inset judgement ring, not the chart outer edge. VISUAL ONLY.
                    float judgementRadiusLocal = outerLocal
                        - PlayerSettingsStore.JudgementInsetNorm * pfT.MinDimLocal;

                    // Spawn radius: a fraction of the band width above the inner edge.
                    float spawnR = innerLocal + spawnRadiusFactor * (judgementRadiusLocal - innerLocal);

                    // alpha 0→1 as note travels from spawn to judgement radius.
                    // Guard against noteLeadTimeMs == 0 to avoid division by zero.
                    float alpha = (noteLeadTimeMs > 0)
                        ? 1f - Mathf.Clamp01((float)timeToHitMs / noteLeadTimeMs)
                        : 1f;

                    // Current radius along the lane center ray (targets judgement ring, not outer edge).
                    float r = Mathf.Lerp(spawnR, judgementRadiusLocal, alpha);

                    Vector2 center   = pfT.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));
                    float thetaRad   = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                    Vector2 localPt  = center + new Vector2(Mathf.Cos(thetaRad), Mathf.Sin(thetaRad)) * r;

                    // Lift the note marker onto the frustum surface. VISUAL ONLY — s01 derived
                    // from current approach radius r, not used for hit-testing anywhere.
                    float s01Note    = (outerLocal > innerLocal)
                        ? Mathf.Clamp01((r - innerLocal) / (outerLocal - innerLocal))
                        : 1f;
                    Vector3 worldPos = pfRoot.TransformPoint(localPt.x, localPt.y, VisualOnlyLocalZ(s01Note));

                    // Fade from dim (spawn) to full-bright (hit) so far notes don't clutter.
                    Color noteBase = note.Type == NoteType.Flick ? flickColor
                                   : note.Type == NoteType.Catch ? catchColor
                                   : isHold                      ? holdColor
                                   :                               tapColor;
                    Color c = noteBase;
                    c.a = Mathf.Lerp(0.3f, 1.0f, alpha);
                    _notePool[poolIdx].startColor = c;
                    _notePool[poolIdx].endColor   = c;

                    _notePool[poolIdx].gameObject.SetActive(true);
                    SetDiamondPositions(_notePool[poolIdx], worldPos, pfRoot);

                    // Flick direction arrow — VISUAL ONLY, no hit-testing impact.
                    // Arrow mapping MUST match JudgementEngine.IsFlickDirectionMatch.
                    if (showFlickDirectionArrows
                        && note.Type == NoteType.Flick
                        && !string.IsNullOrEmpty(note.FlickDirection))
                    {
                        // Arrow length in PlayfieldRoot local units (spec §5.5).
                        float arrowLenLocal = pfT.NormRadiusToLocal(flickArrowLengthNorm);

                        // Expected direction via JudgementEngine helper (keeps mapping in sync).
                        Vector2 expectedDir = JudgementEngine.DebugFlickExpectedDir(
                            note.FlickDirection, AngleUtil.Normalize360(lane.CenterDeg));

                        // Slight Z bias above note marker surface to prevent z-fighting. VISUAL ONLY.
                        float   arrowZ      = VisualOnlyLocalZ(s01Note) + flickArrowZBias;
                        Vector2 endPt       = localPt + expectedDir * arrowLenLocal;
                        Vector3 arrowStart  = pfRoot.TransformPoint(localPt.x, localPt.y, arrowZ);
                        Vector3 arrowEnd    = pfRoot.TransformPoint(endPt.x,   endPt.y,   arrowZ);

                        Color ac = flickArrowColor;
                        ac.a = c.a; // fade with note marker opacity
                        _flickArrowPool[poolIdx].startColor = ac;
                        _flickArrowPool[poolIdx].endColor   = ac;
                        _flickArrowPool[poolIdx].SetPosition(0, arrowStart);
                        _flickArrowPool[poolIdx].SetPosition(1, arrowEnd);
                        _flickArrowPool[poolIdx].gameObject.SetActive(true);
                    }
                    else
                    {
                        _flickArrowPool[poolIdx].gameObject.SetActive(false);
                    }

                    // Hold duration rail + end marker — VISUAL ONLY.
                    // Each endpoint maps its own timeToHitMs independently:
                    //   start uses note.StartTimeMs (already in timeToHitMs above)
                    //   end   uses note.EndTimeMs   (may be beyond lead time → clamped to spawnR)
                    if (isHold && showHoldRails)
                    {
                        double endTimeToHitMs = note.EndTimeMs - chartTimeMs;
                        float  endAlpha = (noteLeadTimeMs > 0)
                            ? 1f - Mathf.Clamp01((float)endTimeToHitMs / noteLeadTimeMs)
                            : 1f;
                        float   endR       = Mathf.Lerp(spawnR, judgementRadiusLocal, endAlpha);
                        Vector2 endLocalPt = center + new Vector2(Mathf.Cos(thetaRad), Mathf.Sin(thetaRad)) * endR;
                        float   s01End     = (outerLocal > innerLocal)
                            ? Mathf.Clamp01((endR - innerLocal) / (outerLocal - innerLocal)) : 1f;
                        Vector3 endWorldPos = pfRoot.TransformPoint(
                            endLocalPt.x, endLocalPt.y, VisualOnlyLocalZ(s01End));

                        // Rail: 2-point line from start endpoint → end endpoint along lane center.
                        Color rc = holdColor; rc.a = c.a;
                        _holdRailPool[poolIdx].startColor = rc;
                        _holdRailPool[poolIdx].endColor   = rc;
                        _holdRailPool[poolIdx].SetPosition(0, worldPos);
                        _holdRailPool[poolIdx].SetPosition(1, endWorldPos);
                        _holdRailPool[poolIdx].gameObject.SetActive(true);

                        // End marker: diamond faded by end-point alpha.
                        Color ec = holdColor; ec.a = Mathf.Lerp(0.3f, 1.0f, endAlpha);
                        _holdEndMarkerPool[poolIdx].startColor = ec;
                        _holdEndMarkerPool[poolIdx].endColor   = ec;
                        _holdEndMarkerPool[poolIdx].gameObject.SetActive(true);
                        SetDiamondPositions(_holdEndMarkerPool[poolIdx], endWorldPos, pfRoot);
                    }
                    else
                    {
                        _holdRailPool[poolIdx].gameObject.SetActive(false);
                        _holdEndMarkerPool[poolIdx].gameObject.SetActive(false);
                    }

                    if (isHold) { _debugHoldRendered++; }
                    poolIdx++;
                }
            }

            // Disable unused pool entries.
            DisableNoteMarkersFrom(poolIdx);
            DisableFlickArrowsFrom(poolIdx);
            DisableHoldVisualsFrom(poolIdx);
        }

        private void DisableAllNoteMarkers()
        {
            DisableNoteMarkersFrom(0);
            DisableFlickArrowsFrom(0);
            DisableHoldVisualsFrom(0);
        }

        private void DisableHoldVisualsFrom(int startIdx)
        {
            if (_holdRailPool == null) { return; }
            for (int i = startIdx; i < _holdRailPool.Length; i++)
            {
                _holdRailPool[i].gameObject.SetActive(false);
                _holdEndMarkerPool[i].gameObject.SetActive(false);
            }
        }

        private void DisableNoteMarkersFrom(int startIdx)
        {
            for (int i = startIdx; i < _notePool.Length; i++)
            {
                _notePool[i].gameObject.SetActive(false);
            }
        }

        private void DisableFlickArrowsFrom(int startIdx)
        {
            if (_flickArrowPool == null) { return; }
            for (int i = startIdx; i < _flickArrowPool.Length; i++)
            {
                _flickArrowPool[i].gameObject.SetActive(false);
            }
        }

        // -------------------------------------------------------------------
        // Per-frame: lane highlight (touch containment)
        // -------------------------------------------------------------------

        // Each frame: for any lane whose wedge contains the current touch, refreshes its
        // edge-line color to laneHighlightColor for laneHighlightDuration seconds.
        // Lanes that don't contain the touch revert to laneEdgeColor after the timer expires.
        // Multiple overlapping lanes all light up independently.
        private void UpdateLaneHighlights()
        {
            if (!showLaneHighlight || _laneLRs.Count == 0) { return; }

            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            IReadOnlyDictionary<string, LaneGeometry>  lanes  = playerAppController.DebugLaneGeometries;
            IReadOnlyDictionary<string, string>        lToA   = playerAppController.DebugLaneToArena;
            PlayfieldTransform                         pfT    = playerAppController.DebugPlayfieldTransform;

            if (arenas == null || lanes == null || lToA == null || pfT == null) { return; }

            float now = Time.time;

            // Step 1: extend expire time for lanes that contain the current touch.
            if (playerAppController.DebugHasTouchHit)
            {
                Vector2 hitLocal = playerAppController.DebugLastTouchLocalXY;

                foreach (KeyValuePair<string, LaneGeometry> kvp in lanes)
                {
                    if (!lToA.TryGetValue(kvp.Key, out string arenaId))           { continue; }
                    if (!arenas.TryGetValue(arenaId, out ArenaGeometry arenaGeo))  { continue; }

                    if (IsInsideLaneWedge(hitLocal, kvp.Value, arenaGeo, pfT))
                    {
                        _laneHighlightExpire[kvp.Key] = now + laneHighlightDuration;
                    }
                }
            }

            // Step 2: apply highlight or restore default color per lane.
            foreach (KeyValuePair<string, LineRenderer[]> kvp in _laneLRs)
            {
                LineRenderer[] lrs = kvp.Value;
                if (lrs == null || lrs.Length < 5) { continue; }

                bool lit = _laneHighlightExpire.TryGetValue(kvp.Key, out float expireTime)
                           && now < expireTime;
                Color c = lit ? laneHighlightColor : laneEdgeColor;

                // lrs[0] is the center ray — keep it laneColor always.
                // lrs[1..4] are the boundary lines/arcs — tint on highlight.
                for (int i = 1; i < lrs.Length; i++)
                {
                    if (lrs[i] == null) { continue; }
                    lrs[i].startColor = c;
                    lrs[i].endColor   = c;
                }
            }
        }

        // Returns true if hitLocalXY is inside the radial band AND angular wedge of the lane.
        // Radial band: hit band (judgement-centred) when useHitBandForLaneHighlight, else
        // chart band (innerLocal..visualOuterLocal). VISUAL ONLY — no gameplay effect.
        private bool IsInsideLaneWedge(
            Vector2 hitLocalXY, LaneGeometry lane, ArenaGeometry arena, PlayfieldTransform pfT)
        {
            Vector2 centerLocal = pfT.NormalizedToLocal(
                new Vector2(arena.CenterXNorm, arena.CenterYNorm));

            Vector2 v        = hitLocalXY - centerLocal;
            float   r        = v.magnitude;
            float   thetaDeg = AngleUtil.Normalize360(Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg);

            // Radial band — exact same values as JudgementEngine and green arcs.
            float bandMin, bandMax;
            if (useHitBandForLaneHighlight)
            {
                ArenaHitTester.ComputeHitBandLocal(arena, pfT,
                    out bandMin, out bandMax, out _, out _);
            }
            else
            {
                float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                float innerLocal = outerLocal - pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                bandMin = innerLocal;
                bandMax = outerLocal + PlayerSettingsStore.VisualOuterExpandNorm * pfT.MinDimLocal;
            }

            if (r < bandMin || r > bandMax) { return false; }

            // Arena arc test (wrap-safe).
            if (!AngleUtil.IsAngleInArc(thetaDeg, arena.ArcStartDeg, arena.ArcSweepDeg)) { return false; }

            // Lane angular slice test — same math as ArenaHitTester.IsInsideLane.
            float laneCenter = AngleUtil.Normalize360(lane.CenterDeg);
            float halfWidth  = lane.WidthDeg * 0.5f;
            float delta      = AngleUtil.ShortestSignedAngleDeltaDeg(laneCenter, thetaDeg);
            return Mathf.Abs(delta) <= halfWidth;
        }

        // -------------------------------------------------------------------
        // Per-frame: touch marker
        // -------------------------------------------------------------------

        // Shows a diamond at the last touch hit point in PlayfieldLocal space.
        // When DebugShowInputProjection is true and the visual surface was used, also shows:
        //   _touchPlaneLR        — grey diamond at the flat-plane projected position
        //   _touchParallaxLineLR — orange line from plane point to surface hit point
        private void UpdateTouchMarker()
        {
            if (_touchLR == null) { return; }

            bool hasTouchHit = playerAppController.DebugHasTouchHit;

            if (hasTouchHit)
            {
                Vector2 hitLocal = playerAppController.DebugLastTouchLocalXY;
                Vector3 worldPos = playerAppController.playfieldRoot
                                       .TransformPoint(hitLocal.x, hitLocal.y, 0f);
                _touchLR.gameObject.SetActive(true);
                SetDiamondPositions(_touchLR, worldPos, playerAppController.playfieldRoot);
            }
            else
            {
                _touchLR.gameObject.SetActive(false);
            }

            // Parallax debug markers — only shown when projection info is enabled and a surface hit occurred.
            bool showParallax = hasTouchHit
                && PlayerSettingsStore.DebugShowInputProjection
                && playerAppController.DebugUsedVisualSurface;

            if (_touchPlaneLR != null)
            {
                if (showParallax)
                {
                    Vector2 planeLocal = playerAppController.DebugLastPlaneLocalXY;
                    Vector3 planeWorld = playerAppController.playfieldRoot
                                            .TransformPoint(planeLocal.x, planeLocal.y, 0f);
                    _touchPlaneLR.gameObject.SetActive(true);
                    SetDiamondPositions(_touchPlaneLR, planeWorld, playerAppController.playfieldRoot);
                }
                else
                {
                    _touchPlaneLR.gameObject.SetActive(false);
                }
            }

            if (_touchParallaxLineLR != null)
            {
                if (showParallax)
                {
                    Vector2 planeLocal  = playerAppController.DebugLastPlaneLocalXY;
                    Vector2 surfLocal   = playerAppController.DebugLastTouchLocalXY;
                    Vector3 planeWorld  = playerAppController.playfieldRoot
                                             .TransformPoint(planeLocal.x, planeLocal.y, 0f);
                    Vector3 surfWorld   = playerAppController.playfieldRoot
                                             .TransformPoint(surfLocal.x,  surfLocal.y,  0f);
                    _touchParallaxLineLR.gameObject.SetActive(true);
                    _touchParallaxLineLR.SetPosition(0, planeWorld);
                    _touchParallaxLineLR.SetPosition(1, surfWorld);
                }
                else
                {
                    _touchParallaxLineLR.gameObject.SetActive(false);
                }
            }
        }

        // -------------------------------------------------------------------
        // OnGUI: touch band debug overlay (PlayerSettingsStore.DebugShowTouchBand)
        // -------------------------------------------------------------------

        // Draws a live text overlay with touch-band and input-projection info.
        // OnGUI is active when either DebugShowTouchBand or DebugShowInputProjection is true.
        //
        // Touch-band lines (one per arena, when DebugShowTouchBand):
        //   [arenaId] r=0.412  hit=[0.360..0.460]  jdg=0.410  radial=PASS  arc=PASS  lanes=[lane-1]
        // Input projection line (when DebugShowInputProjection):
        //   [Input] usedVisualSurface=true  plane=(0.12, -0.05)  surface=(0.10, -0.07)  delta=0.022
        private void OnGUI()
        {
            bool showBand     = PlayerSettingsStore.DebugShowTouchBand;
            bool showProj     = PlayerSettingsStore.DebugShowInputProjection;
            bool showSnapshot = debugShowEvaluatedGeometrySnapshot;

            // At least one overlay must be active, otherwise skip all GUI work.
            if (!showBand && !showProj && !showSnapshot) { return; }
            if (!_geometryBuilt || playerAppController == null) { return; }

            // --- Geometry snapshot (bottom-left, independent of touch state) ---
            // Drawn first so other overlays (touch band etc.) can render on top if needed.
            // _snapshotText is cached — rebuilt only once per interval in UpdateGeometrySnapshot,
            // so OnGUI itself performs no string work beyond the GUI.Label call.
            if (showSnapshot && _snapshotText != null)
            {
                int boxH  = _snapshotLineCount * GuiLineHeight;
                int snapY = Screen.height - boxH - 10;

                // Drop-shadow (black, 1px offset) then light-green foreground.
                GUI.color = Color.black;
                GUI.Label(new Rect(11, snapY + 1, Screen.width - 20, boxH), _snapshotText);
                GUI.color = new Color(0.7f, 1f, 0.7f); // light green
                GUI.Label(new Rect(10, snapY,     Screen.width - 20, boxH), _snapshotText);
                GUI.color = Color.white;
            }

            // Only proceed with touch-dependent overlays when they are enabled.
            if (!showBand && !showProj) { return; }

            // Lane-1 animated geometry readout — visible every frame so animated movement is
            // immediately apparent without needing an active touch.
            if (showBand)
            {
                IReadOnlyDictionary<string, LaneGeometry> lanesAll =
                    playerAppController.DebugLaneGeometries;
                if (lanesAll != null && lanesAll.TryGetValue("lane-1", out LaneGeometry dbgLane1))
                {
                    string laneLine =
                        $"[lane-1] centerDeg={dbgLane1.CenterDeg:F2}  widthDeg={dbgLane1.WidthDeg:F2}";
                    GUI.color = Color.black;
                    GUI.Label(new Rect(11, 11, Screen.width, GuiLineHeight), laneLine);
                    GUI.color = Color.yellow;
                    GUI.Label(new Rect(10, 10, Screen.width, GuiLineHeight), laneLine);
                    GUI.color = Color.white;
                }
            }

            // Hold debug counter — always visible when DebugShowTouchBand is on.
            if (showBand)
            {
                string holdLine = $"HoldRendered={_debugHoldRendered}  HoldPresent={_debugHoldPresent}";
                GUI.color = Color.black;
                GUI.Label(new Rect(11, 29, Screen.width, GuiLineHeight), holdLine);
                GUI.color = Color.cyan;
                GUI.Label(new Rect(10, 28, Screen.width, GuiLineHeight), holdLine);
                GUI.color = Color.white;
            }

            if (!playerAppController.DebugHasTouchHit) { return; }

            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            IReadOnlyDictionary<string, LaneGeometry>  lanes  = playerAppController.DebugLaneGeometries;
            IReadOnlyDictionary<string, string>        lToA   = playerAppController.DebugLaneToArena;
            PlayfieldTransform                         pfT    = playerAppController.DebugPlayfieldTransform;

            if (arenas == null || lanes == null || lToA == null || pfT == null) { return; }

            Vector2 hitLocal = playerAppController.DebugLastTouchLocalXY;

            int y = 10;

            // Input projection line (DebugShowInputProjection).
            if (showProj)
            {
                bool   usedSurf  = playerAppController.DebugUsedVisualSurface;
                Vector2 planeXY  = playerAppController.DebugLastPlaneLocalXY;
                Vector2 surfXY   = playerAppController.DebugLastTouchLocalXY; // same as plane if not surf
                float   delta    = (surfXY - planeXY).magnitude;
                string projLine  =
                    $"[Input] usedVisualSurface={usedSurf}" +
                    $"  plane=({planeXY.x:F3},{planeXY.y:F3})" +
                    $"  surface=({surfXY.x:F3},{surfXY.y:F3})" +
                    $"  delta={delta:F4}";
                GUI.color = Color.black;
                GUI.Label(new Rect(11, y + 1, Screen.width, GuiLineHeight), projLine);
                GUI.color = new Color(1f, 0.65f, 0f); // orange
                GUI.Label(new Rect(10, y,     Screen.width, GuiLineHeight), projLine);
                GUI.color = Color.white;
                y += GuiLineHeight;
            }

            // Per-arena touch-band lines (DebugShowTouchBand).
            if (!showBand) { GUI.color = Color.white; return; }

            foreach (KeyValuePair<string, ArenaGeometry> akvp in arenas)
            {
                ArenaGeometry arena = akvp.Value;

                // Same canonical computation used by gameplay and green arcs.
                ArenaHitTester.ComputeHitBandLocal(arena, pfT,
                    out float hitInner, out float hitOuter,
                    out float judgement, out _);

                // Inward depth and coverage — for tuning display only.
                float dbgChartOuter  = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                float dbgChartInner  = dbgChartOuter - pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                float dbgDepth       = Mathf.Max(0f, judgement - dbgChartInner);
                float dbgCov01       = PlayerSettingsStore.HitBandInnerCoverage01;

                Vector2 actr     = pfT.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));
                Vector2 av       = hitLocal - actr;
                float   r        = av.magnitude;
                float   thetaDeg = AngleUtil.Normalize360(Mathf.Atan2(av.y, av.x) * Mathf.Rad2Deg);

                bool radialPass = (r >= hitInner && r <= hitOuter);
                bool arcPass    = AngleUtil.IsAngleInArc(thetaDeg, arena.ArcStartDeg, arena.ArcSweepDeg);

                // Collect lane IDs whose wedge contains the touch.
                string hitLanes = "";
                foreach (KeyValuePair<string, LaneGeometry> lkvp in lanes)
                {
                    if (!lToA.TryGetValue(lkvp.Key, out string la) || la != akvp.Key) { continue; }
                    float lc    = AngleUtil.Normalize360(lkvp.Value.CenterDeg);
                    float hw    = lkvp.Value.WidthDeg * 0.5f;
                    float delta = AngleUtil.ShortestSignedAngleDeltaDeg(lc, thetaDeg);
                    if (radialPass && arcPass && Mathf.Abs(delta) <= hw)
                        hitLanes += (hitLanes.Length > 0 ? "," : "") + lkvp.Key;
                }

                string line =
                    $"[{akvp.Key}] r={r:F3}  hit=[{hitInner:F3}..{hitOuter:F3}]" +
                    $"  jdg={judgement:F3}  chart=[{dbgChartInner:F3}..{dbgChartOuter:F3}]" +
                    $"  cov={dbgCov01:F2}  depth={dbgDepth:F3}" +
                    $"  radial={(radialPass ? "PASS" : "FAIL")}" +
                    $"  arc={(arcPass ? "PASS" : "FAIL")}  lanes=[{hitLanes}]";

                // Drop-shadow + white label.
                GUI.color = Color.black;
                GUI.Label(new Rect(11, y + 1, Screen.width, GuiLineHeight), line);
                GUI.color = Color.white;
                GUI.Label(new Rect(10, y,     Screen.width, GuiLineHeight), line);

                y += GuiLineHeight;
            }

            GUI.color = Color.white; // restore default
        }

        // -------------------------------------------------------------------
        // Geometry snapshot
        // -------------------------------------------------------------------

        // Rebuilds the cached snapshot text at most once per debugSnapshotIntervalSeconds.
        //
        // Data sources (spec §5.9 single-source-of-truth):
        //   • ChartRuntimeEvaluator.GetArena(0) / TryGetLane("lane-1", ...) — raw evaluated floats.
        //   • PlayfieldTransform.NormRadiusToLocal  — normalised → local-unit radius conversion.
        //   • ArenaHitTester.ComputeHitBandLocal    — same judgementRadiusLocal formula used by
        //     JudgementRingRenderer, UpdateHitBandArcs, and gameplay hit-testing.
        //   • arenaSurface.FrustumHeightInner/Outer — frustum Z (visual only; null-guarded).
        //
        // Allocation policy:
        //   _snapshotSb is pre-allocated in Awake and reused (Length = 0 to clear).
        //   value.ToString("F2") allocates one small string per field per interval — acceptable
        //   at one-per-second.  The final _snapshotSb.ToString() is the only durable allocation.
        //   No allocations occur on frames where the timer has not expired.
        private void UpdateGeometrySnapshot()
        {
            if (!debugShowEvaluatedGeometrySnapshot) { return; }

            // Accumulate unscaled time so the readout works when timeScale = 0.
            _snapshotTimer += Time.unscaledDeltaTime;

            // Floor at 0.1 s to avoid update-storm in fast or unit-test contexts.
            float interval = Mathf.Max(0.1f, debugSnapshotIntervalSeconds);
            if (_snapshotTimer < interval) { return; }

            _snapshotTimer = 0f;

            // Defensive init in case Awake was not called (e.g. component added at runtime).
            if (_snapshotSb == null)
                _snapshotSb = new StringBuilder(SnapshotSbCapacity);

            ChartRuntimeEvaluator evaluator = playerAppController.Evaluator;
            PlayfieldTransform    pfT       = playerAppController.DebugPlayfieldTransform;

            _snapshotSb.Length = 0; // clear without allocation

            // Case 1: evaluator not yet ready (PlayerAppController hasn't finished Start).
            if (evaluator == null || pfT == null)
            {
                _snapshotSb.Append("Evaluator: null");
                _snapshotLineCount = 1;
                _snapshotText = _snapshotSb.ToString();
                return;
            }

            double timeMs = playerAppController.DebugEffectiveChartTimeMs;

            // ── Line 1: header ────────────────────────────────────────────────────────
            _snapshotSb.Append("--- EvalSnapshot @ ");
            _snapshotSb.Append(((long)timeMs).ToString());
            _snapshotSb.Append("ms ---");
            int lines = 1;

            // ── Lines 2-N: first arena ────────────────────────────────────────────────
            if (evaluator.ArenaCount > 0)
            {
                EvaluatedArena ea = evaluator.GetArena(0);

                float outerLocal = pfT.NormRadiusToLocal(ea.OuterRadiusNorm);
                float bandLocal  = pfT.NormRadiusToLocal(ea.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Build an ArenaGeometry view of the evaluated data to call ComputeHitBandLocal —
                // this is the single source of truth for judgementRadiusLocal (same path as
                // JudgementRingRenderer and UpdateHitBandArcs).
                var ag = new ArenaGeometry
                {
                    CenterXNorm       = ea.CenterXNorm,
                    CenterYNorm       = ea.CenterYNorm,
                    OuterRadiusNorm   = ea.OuterRadiusNorm,
                    BandThicknessNorm = ea.BandThicknessNorm,
                    ArcStartDeg       = ea.ArcStartDeg,
                    ArcSweepDeg       = ea.ArcSweepDeg,
                };
                ArenaHitTester.ComputeHitBandLocal(ag, pfT,
                    out _, out _, out float jdgLocal, out _);

                // Arena geometry line.
                _snapshotSb.Append("\nArena[");
                _snapshotSb.Append(ea.ArenaId ?? "?");
                _snapshotSb.Append("]:  arcStart=");
                _snapshotSb.Append(ea.ArcStartDeg.ToString("F2"));
                _snapshotSb.Append("\u00B0  sweep=");   // °  (degree symbol)
                _snapshotSb.Append(ea.ArcSweepDeg.ToString("F2"));
                _snapshotSb.Append("\u00B0  |  outer=");
                _snapshotSb.Append(outerLocal.ToString("F3"));
                _snapshotSb.Append("L  inner=");
                _snapshotSb.Append(innerLocal.ToString("F3"));
                _snapshotSb.Append("L  jdg=");
                _snapshotSb.Append(jdgLocal.ToString("F3"));
                _snapshotSb.Append("L");
                lines++;

                // Frustum heights line (only when arenaSurface is wired up).
                if (arenaSurface != null)
                {
                    _snapshotSb.Append("\nFrustum:  zInner=");
                    _snapshotSb.Append(arenaSurface.FrustumHeightInner.ToString("F3"));
                    _snapshotSb.Append("  zOuter=");
                    _snapshotSb.Append(arenaSurface.FrustumHeightOuter.ToString("F3"));
                    lines++;
                }
            }
            else
            {
                _snapshotSb.Append("\n(no arenas in evaluator)");
                lines++;
            }

            // ── Lane line: "lane-1" preferred, fallback to index 0 ────────────────────
            // We need the parent arena to compute judgementRadiusLocal for the lane display.
            // Use the same arena we sampled above (GetArena(0)) as the lane's arena reference.
            bool gotLane = evaluator.TryGetLane("lane-1", out EvaluatedLane el);
            if (!gotLane && evaluator.LaneCount > 0)
            {
                el      = evaluator.GetLane(0);
                gotLane = true;
            }

            if (gotLane)
            {
                float leftDeg  = el.CenterDeg - el.WidthDeg * 0.5f;
                float rightDeg = el.CenterDeg + el.WidthDeg * 0.5f;

                _snapshotSb.Append("\nLane[");
                _snapshotSb.Append(el.LaneId ?? "?");
                _snapshotSb.Append("]:  center=");
                _snapshotSb.Append(el.CenterDeg.ToString("F2"));
                _snapshotSb.Append("\u00B0  width=");
                _snapshotSb.Append(el.WidthDeg.ToString("F2"));
                _snapshotSb.Append("\u00B0  |  left=");
                _snapshotSb.Append(leftDeg.ToString("F2"));
                _snapshotSb.Append("\u00B0  right=");
                _snapshotSb.Append(rightDeg.ToString("F2"));
                _snapshotSb.Append("\u00B0");
                lines++;
            }

            _snapshotLineCount = lines;
            _snapshotText      = _snapshotSb.ToString(); // one allocation per interval — acceptable
        }

        // -------------------------------------------------------------------
        // LineRenderer position helpers
        // -------------------------------------------------------------------

        // VISUAL ONLY — never use this Z value for hit-testing or judgement.
        // Returns the PlayfieldRoot local Z for a point at normalized band position s01:
        //   s01 = 0  →  inner edge  (FrustumHeightInner)
        //   s01 = 1  →  outer edge  (FrustumHeightOuter)
        // Falls back to 0 when no arenaSurface is assigned or frustum profile is off.
        private float VisualOnlyLocalZ(float s01)
        {
            if (arenaSurface != null && arenaSurface.UseFrustumProfile)
            {
                return Mathf.Lerp(arenaSurface.FrustumHeightInner,
                                  arenaSurface.FrustumHeightOuter, s01);
            }
            return 0f;
        }

        // Sets N world-space positions along a circular arc.
        // startDeg/sweepDeg are raw floats — cos/sin handle any value correctly.
        // s01: normalized band position (0=inner, 1=outer) — used for frustum Z only.
        private void SetArcPositions(
            LineRenderer lr,
            Vector2      centerLocal,
            float        radius,
            float        startDeg,
            float        sweepDeg,
            Transform    pfRoot,
            float        s01 = 0f)   // VISUAL ONLY: 0=inner edge, 1=outer edge
        {
            int   n = Mathf.Max(2, arcSegments);
            float z = VisualOnlyLocalZ(s01); // VISUAL ONLY — not used for hit-testing
            lr.positionCount = n;

            for (int i = 0; i < n; i++)
            {
                float t   = (n > 1) ? (float)i / (n - 1) : 0f;
                float deg = startDeg + t * sweepDeg;
                float rad = deg * Mathf.Deg2Rad;

                Vector2 pt = centerLocal + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;
                lr.SetPosition(i, pfRoot.TransformPoint(pt.x, pt.y, z));
            }
        }

        // Sets 2 world-space positions: inner edge → outer edge along a radial ray.
        // Each endpoint uses its own frustum Z so the ray lies on the cone surface.
        // outerS01: normalized band position of the outer endpoint (default 1 = chart outer edge).
        //   Pass s01Judgement when the ray should end at the inset judgement ring. VISUAL ONLY.
        private void SetRadialPositions(
            LineRenderer lr,
            Vector2      centerLocal,
            float        innerLocal,
            float        outerLocal,
            float        angleDeg,
            Transform    pfRoot,
            float        outerS01 = 1f)  // VISUAL ONLY
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            Vector2 dir   = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            Vector2 inner = centerLocal + dir * innerLocal;
            Vector2 outer = centerLocal + dir * outerLocal;

            lr.positionCount = 2;
            lr.SetPosition(0, pfRoot.TransformPoint(inner.x, inner.y, VisualOnlyLocalZ(0f)));       // VISUAL ONLY
            lr.SetPosition(1, pfRoot.TransformPoint(outer.x, outer.y, VisualOnlyLocalZ(outerS01))); // VISUAL ONLY
        }

        // Sets 5 world-space positions forming a diamond in the PlayfieldRoot XY plane.
        // pfRoot.right / pfRoot.up are the in-plane axes (spec §5.4: localZ is the normal).
        private void SetDiamondPositions(LineRenderer lr, Vector3 worldCenter, Transform pfRoot)
        {
            Vector3 r = pfRoot.right * markerHalfSize;
            Vector3 u = pfRoot.up   * markerHalfSize;

            lr.positionCount = 5;
            lr.SetPosition(0, worldCenter + u);   // top
            lr.SetPosition(1, worldCenter + r);   // right
            lr.SetPosition(2, worldCenter - u);   // bottom
            lr.SetPosition(3, worldCenter - r);   // left
            lr.SetPosition(4, worldCenter + u);   // top (close loop)
        }

        // -------------------------------------------------------------------
        // Per-frame: judgement flash markers
        // -------------------------------------------------------------------

        // Iterates the ring buffer each frame and renders active (non-expired) flashes.
        // Each flash is drawn as a diamond at the outer ring of its lane center,
        // expanding and fading over flashDuration seconds.  VISUAL ONLY.
        private void UpdateJudgementFlashes()
        {
            if (_flashPool == null) { return; }

            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            IReadOnlyDictionary<string, LaneGeometry>  lanes  = playerAppController.DebugLaneGeometries;
            IReadOnlyDictionary<string, string>        lToA   = playerAppController.DebugLaneToArena;
            PlayfieldTransform                         pfT    = playerAppController.DebugPlayfieldTransform;
            Transform                                  pfRoot = playerAppController.playfieldRoot;

            if (arenas == null || lanes == null || lToA == null || pfT == null) { return; }

            float now = Time.time;

            for (int i = 0; i < MaxFlashes; i++)
            {
                JudgementFlash flash = _flashes[i];

                // Slot is empty or expired — hide and skip.
                if (flash.ExpireTime <= now || string.IsNullOrEmpty(flash.LaneId))
                {
                    _flashPool[i].gameObject.SetActive(false);
                    continue;
                }

                // Resolve geometry for this lane.
                if (!lanes.TryGetValue(flash.LaneId,  out LaneGeometry  lane)  ||
                    !lToA.TryGetValue(flash.LaneId,   out string arenaId)       ||
                    !arenas.TryGetValue(arenaId,       out ArenaGeometry arena))
                {
                    _flashPool[i].gameObject.SetActive(false);
                    continue;
                }

                // World position: judgement ring at lane center. VISUAL ONLY — not used for hit-testing.
                float   outerLocal             = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                float   bandLocal              = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                float   innerLocal             = outerLocal - bandLocal;
                float   judgementRadiusLocal   = outerLocal
                    - PlayerSettingsStore.JudgementInsetNorm * pfT.MinDimLocal;
                float   s01Judgement           = (outerLocal > innerLocal)
                    ? Mathf.Clamp01((judgementRadiusLocal - innerLocal) / (outerLocal - innerLocal))
                    : 1f;
                Vector2 center     = pfT.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));
                float   thetaRad   = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                Vector2 localPt    = center + new Vector2(Mathf.Cos(thetaRad), Mathf.Sin(thetaRad)) * judgementRadiusLocal;
                Vector3 worldPos   = pfRoot.TransformPoint(localPt.x, localPt.y, VisualOnlyLocalZ(s01Judgement)); // VISUAL ONLY

                // Expand-and-fade: lifeRatio 1→0 over flashDuration.
                float lifeRatio = Mathf.Clamp01((flash.ExpireTime - now) / flashDuration);

                Color c = FlashColor(flash.Tier, flash.IsPerfectPlus);
                c.a = lifeRatio; // fade out
                _flashPool[i].startColor = c;
                _flashPool[i].endColor   = c;

                // Diamond grows from 60 % to 100 % of flashHalfSize as it fades out.
                float halfSize = Mathf.Lerp(flashHalfSize, flashHalfSize * 0.6f, lifeRatio);
                SetDiamondPositionsScaled(_flashPool[i], worldPos, pfRoot, halfSize);
                _flashPool[i].gameObject.SetActive(true);
            }
        }

        // Returns the colour for a flash based on tier and perfect-plus flag.
        private Color FlashColor(JudgementTier tier, bool isPerfectPlus)
        {
            if (isPerfectPlus)              { return flashPerfectPlusColor; }
            switch (tier)
            {
                case JudgementTier.Perfect: return flashPerfectColor;
                case JudgementTier.Great:   return flashGreatColor;
                default:                    return flashMissColor;
            }
        }

        // Like SetDiamondPositions but with an explicit half-size (for flash scaling).
        private void SetDiamondPositionsScaled(
            LineRenderer lr, Vector3 worldCenter, Transform pfRoot, float halfSize)
        {
            Vector3 r = pfRoot.right * halfSize;
            Vector3 u = pfRoot.up   * halfSize;

            lr.positionCount = 5;
            lr.SetPosition(0, worldCenter + u);   // top
            lr.SetPosition(1, worldCenter + r);   // right
            lr.SetPosition(2, worldCenter - u);   // bottom
            lr.SetPosition(3, worldCenter - r);   // left
            lr.SetPosition(4, worldCenter + u);   // top (close loop)
        }

        // -------------------------------------------------------------------
        // LineRenderer factory
        // -------------------------------------------------------------------

        // Creates a child GameObject with a configured LineRenderer.
        private LineRenderer CreateLineRenderer(string goName, Color color)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            lr.material      = _lineMat;
            lr.startColor    = color;
            lr.endColor      = color;
            lr.startWidth    = lineWidth;
            lr.endWidth      = lineWidth;
            lr.useWorldSpace = true;
            lr.positionCount = 0;
            return lr;
        }
    }
}
