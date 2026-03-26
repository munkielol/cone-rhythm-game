// PlayerAppController.cs
// Minimal end-to-end gameplay harness for RhythmicFlow v0.
//
// Responsibilities (one MonoBehaviour, one scene object):
//   1) Scan packs (PackScanner) on Start.
//   2) Auto-select first valid pack + first difficulty.
//   3) Extract audio/song.ogg from the .rpk to a temp file and load it
//      via UnityWebRequestMultimedia (coroutine).
//   4) Start AudioSource and Conductor on the same frame (DSP-time locked, spec §3.3).
//   5) Each Update:
//        a) Evaluate all animated chart tracks at effectiveChartTimeMs via ChartRuntimeEvaluator.
//        b) Advance/sweep NoteScheduler.
//        c) Poll touches (mouse in Editor/Standalone, Input.touches on mobile).
//        d) Raycast screen→ playfieldRoot local XY plane → normalized XY.
//        e) Feed touches into FlickGestureTracker (Begin/Update/End).
//        f) Run JudgementEngine: TryJudgeCatch (once), then per-touch
//           TryJudgeTap / TryBindHold / TryJudgeFlick.
//        g) Evaluate hold ticks (spec §7.5.1).
//        h) Clean up ended touches.
//   6) OnGUI overlay: status, chart time, note count, last judgement.
//
// Geometry is evaluated per-frame via ChartRuntimeEvaluator (not time-0 snapshot).
// Visual renderers (HoldBodyRenderer, NoteApproachRenderer, JudgementRingRenderer)
// read from the geometry dictionaries that are synced from the evaluator each frame.
// NO UnityEditor APIs.
// NO .meta file creation.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using RhythmicFlow.Shared;
using UnityEngine;
using UnityEngine.Networking;

namespace RhythmicFlow.Player
{
    // Run Awake early so visual-quality settings are written to PlayerSettingsStore before
    // note renderers (at default order 0) read them in their own Awake calls.
    [DefaultExecutionOrder(-100)]
    public class PlayerAppController : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector fields
        // -------------------------------------------------------------------

        [Header("Scene Wiring")]
        [Tooltip("Gameplay camera used for screen→playfield raycasting.")]
        public Camera gameplayCamera;

        [Tooltip("Transform whose local XY plane is the authoritative interaction surface (spec §5.4).")]
        public Transform playfieldRoot;

        [Tooltip("Bottom-left of the safe-area rectangle in playfieldRoot local XY.")]
        public Vector2 playfieldLocalMin = new Vector2(-0.5f, -0.5f);

        [Tooltip("Top-right of the safe-area rectangle in playfieldRoot local XY.")]
        public Vector2 playfieldLocalMax = new Vector2( 0.5f,  0.5f);

        [Tooltip("AudioSource that will receive the extracted OGG clip.")]
        public AudioSource musicAudioSource;

        [Header("Pack Location")]
        [Tooltip("Sub-folder under Application.persistentDataPath used in builds.")]
        public string packsSubfolderName = "Packs";

        [Tooltip("When true and running in the Unity Editor, load packs from " +
                 "<project-root>/<editorPacksFolderName> instead of persistentDataPath. " +
                 "Has no effect in builds.")]
        public bool useEditorProjectRootPacks = true;

        [Tooltip("Folder name under the project root (repo) used when the Editor override is active.")]
        public string editorPacksFolderName = "DevPacks";

        [Header("Gameplay")]
        [Tooltip("Timing window set — Standard (30/90 ms) or Challenger (22/60 ms). Spec §4.1.")]
        public GameplayMode gameplayMode = GameplayMode.Standard;

        [Header("Note Approach")]
        [Tooltip("How many ms before a note's hit time it first becomes visible.\n\n" +
                 "Shared source of truth for all visual renderers — Tap, Catch, Flick, and Hold\n" +
                 "all read NoteLeadTimeMs from here. A single change propagates to every renderer.\n\n" +
                 "Note: PlayerDebugRenderer has its own separate noteLeadTimeMs field;\n" +
                 "ensure they match to keep the debug hold rail visually aligned.\n" +
                 "Default: 2000")]
        [SerializeField] private int noteLeadTimeMs = 2000;

        [Tooltip("Spawn radius as a fraction of the approach path from inner arc (0) to judgement ring (1).\n\n" +
                 "0 = notes first appear at the inner band edge and travel outward (v0 default).\n" +
                 "1 = notes spawn directly at the judgement ring with no travel.\n\n" +
                 "Shared source of truth for all visual renderers. Keep at 0 for v0.\n" +
                 "Default: 0")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnRadiusFactor = 0f;

        [Header("Visual Quality (startup-read — exit and re-enter Play mode to apply changes)")]
        [Tooltip(
            "Number of arc column quads across each note cap edge (Tap / Catch / Flick / Hold head).\n\n" +
            "Higher values produce smoother curves on wide lanes. Min: 3. Default: 5.\n\n" +
            "STARTUP-ONLY: this value is written to PlayerPrefs in Awake and read by note renderers\n" +
            "in their own Awake. Changing it during Play mode has no effect on the current session —\n" +
            "exit Play mode, adjust the value, then re-enter Play mode to rebuild the mesh pools.\n\n" +
            "Does NOT affect hold body ribbon, arena, lane, judgement ring, or lane guide geometry.\n\n" +
            "Spec §8.3.1 / PlayerSettingsStore.NoteCapArcSegments.")]
        [SerializeField] private int noteCapArcSegments = PlayerSettingsStore.DefaultNoteCapArcSegments;

        [Header("Input Projection")]
        [Tooltip("When true, screen rays are tested against visualSurfaceLayerMask before falling back " +
                 "to the flat Z=0 plane. Corrects parallax on frustum-shaped arena surfaces. Spec §5.2.1.")]
        public bool useVisualSurfaceRaycast = true;

        [Tooltip("Physics layer(s) that contain the visible arena surface collider(s). " +
                 "Only used when useVisualSurfaceRaycast is true.")]
        public LayerMask visualSurfaceLayerMask;

        [Tooltip("(Optional, debug only) Transform of the visual surface root GO — used only " +
                 "for labelling in the debug overlay, not for any gameplay logic.")]
        public Transform visualSurfaceRoot;

        // -------------------------------------------------------------------
        // State machine
        // -------------------------------------------------------------------

        private enum AppState { Boot, LoadingAudio, Playing, Error }

        private AppState _state      = AppState.Boot;
        private string   _statusText = "Booting…";

        // -------------------------------------------------------------------
        // Core systems
        // -------------------------------------------------------------------

        private PackCatalog         _catalog;
        private PackEntry           _activePack;
        private ChartJsonV1         _chart;
        private PlayfieldTransform  _playfieldTransform;
        private NoteScheduler       _scheduler;
        private JudgementEngine     _judgementEngine;
        private FlickGestureTracker _flickTracker;
        private Conductor           _conductor;

        // -------------------------------------------------------------------
        // Geometry — per-frame evaluated via ChartRuntimeEvaluator (spec §5.9)
        // -------------------------------------------------------------------

        // Single source of truth for all animated chart geometry.
        // Created once per loaded chart; Evaluate(timeMs) called each frame.
        private ChartRuntimeEvaluator _evaluator;

        // Synced from _evaluator each frame; exposed to renderers and JudgementEngine.
        // Contains only ENABLED arenas/lanes — disabled ones are excluded so
        // hit-testing and visuals automatically skip them (spec §5.6).
        private Dictionary<string, ArenaGeometry> _arenaGeos;
        private Dictionary<string, LaneGeometry>  _laneGeos;
        private Dictionary<string, string>        _laneToArena; // laneId → arenaId (immutable)

        // -------------------------------------------------------------------
        // Per-frame reuse buffers (avoid per-frame allocation)
        // -------------------------------------------------------------------

        private readonly List<RuntimeNote>   _activeNotes  = new List<RuntimeNote>(64);
        private readonly List<TouchSnapshot> _touches      = new List<TouchSnapshot>(10);
        private readonly List<int>           _endedTouches = new List<int>(10);

        // -------------------------------------------------------------------
        // Touch tracking state
        // -------------------------------------------------------------------

        // TouchId → bound RuntimeNote for active Hold notes.
        private readonly Dictionary<int, RuntimeNote> _boundHolds = new Dictionary<int, RuntimeNote>();

        // Latest normalized position per touchId — used for hold tick lane check.
        private readonly Dictionary<int, Vector2> _touchPosNorm = new Dictionary<int, Vector2>();

        // Temp buffer: hold touchIds that finished this frame (can't remove while iterating).
        private readonly List<int> _holdFinishBuffer = new List<int>(4);

        // -------------------------------------------------------------------
        // Timing
        // -------------------------------------------------------------------

        private double _prevChartTimeMs;

        // Notes become Active this many ms before their PrimaryTimeMs.
        // Must be >= GreatWindowMs; 5 s leaves room for visual approach (future work).
        private const double ActivationLeadMs = 5000.0;

        // -------------------------------------------------------------------
        // Mouse simulation (Editor / Standalone)
        // -------------------------------------------------------------------

        private bool _mouseWasDown;

        // -------------------------------------------------------------------
        // Scoring (spec §4.5)
        // -------------------------------------------------------------------

        private ScoreTracker _scoreTracker;

        // Chart-clock time (ms) past which all notes have had a chance to be swept.
        // Computed at chart load from the latest note end time + greatWindowMs.
        private double _songEndThresholdMs;

        // True once the end-of-song summary has been logged. Prevents double-firing.
        private bool _songFinished;

        // Cached delegate for NoteScheduler.SweepMissed callback — avoids per-frame
        // delegate allocation (spec §9 "no per-frame allocations on gameplay hot path").
        private Action<RuntimeNote> _onSweepMissDelegate;

        [Header("Scoring Debug")]
        [Tooltip("When true, logs each individual judgement with updated score/combo. " +
                 "Default false — enable during playtesting only.")]
        public bool debugLogScoreEachJudgement = false;

        // -------------------------------------------------------------------
        // Debug overlay
        // -------------------------------------------------------------------

        private string _lastJudgementText = "—";

        /// <summary>
        /// Fires each time a note is judged (tap / catch / flick / sweep-miss of non-hold).
        /// Subscribed by PlayerDebugRenderer for judgement flash visuals and by ScoreTracker.
        ///
        /// Note: hold-bind events (fired at hold START by TryBindHold) also come through here
        /// with record.Note.Type == "hold". ScoreTracker ignores them; scoring for holds
        /// uses OnHoldResolved instead (spec §4.5).
        ///
        /// Sweep-missed non-hold notes now also fire this event with Tier = Miss.
        /// </summary>
        public event Action<JudgementRecord> OnJudgement;

        /// <summary>
        /// Fires when a hold note is fully resolved — exactly once per hold.
        ///   Tier.Perfect — hold completed (player held through endTimeMs; State = Hit).
        ///   Tier.Miss    — hold never bound (State = Missed with HoldBind = Unbound).
        ///
        /// This event is now LIFECYCLE-ONLY for completed/released holds; scoring is
        /// driven entirely by OnHoldTick.  The only case that still affects score is
        /// HoldBind == Unbound (player never started the hold), which counts as one Miss
        /// to break the combo (spec §4.5).
        /// </summary>
        public event Action<JudgementRecord> OnHoldResolved;

        /// <summary>
        /// Fires for each baked tick of an active hold note (spec §4.4 / §7.5.1).
        ///   Tier.Perfect — bound touch was inside the lane at the tick time.
        ///   Tier.Miss    — bound touch was outside the lane, OR hold was released early.
        ///
        /// Exactly one Miss fires when a hold fails (first missed tick or early release),
        /// then no further tick events are emitted for that hold (spec §4.5 — "no spam").
        ///
        /// ScoreTracker subscribes here for all hold-related combo/score updates.
        /// </summary>
        public event Action<JudgementRecord> OnHoldTick;

        // -------------------------------------------------------------------
        // VISUALS API — read-only access for runtime visual renderers.
        // Exposed without "Debug" prefix; these stay after debug scaffolding is removed.
        // -------------------------------------------------------------------

        /// <summary>All runtime notes sorted by PrimaryTimeMs (null before Start completes).</summary>
        public IReadOnlyList<RuntimeNote>                 NotesAll         => _scheduler?.AllNotes;

        /// <summary>Current effective chart time in ms (0 before playback starts).</summary>
        public double                                     EffectiveChartTimeMs => _conductor?.EffectiveChartTimeMs ?? 0.0;

        /// <summary>PlayfieldTransform coordinate converter (null before Start completes).</summary>
        public PlayfieldTransform                         PlayfieldTf      => _playfieldTransform;

        /// <summary>Evaluated lane geometries keyed by laneId (null before Start completes).</summary>
        public IReadOnlyDictionary<string, LaneGeometry>  LaneGeometries   => _laneGeos;

        /// <summary>Evaluated arena geometries keyed by arenaId (null before Start completes).</summary>
        public IReadOnlyDictionary<string, ArenaGeometry> ArenaGeometries  => _arenaGeos;

        /// <summary>Maps laneId → arenaId (null before Start completes).</summary>
        public IReadOnlyDictionary<string, string>        LaneToArena      => _laneToArena;

        /// <summary>Great-window size in ms from the active gameplay mode (default 90 if not yet ready).</summary>
        public double                                     GreatWindowMs    => _judgementEngine?.Windows.GreatWindowMs ?? 90.0;

        /// <summary>
        /// Per-frame chart evaluator — the single source of truth for animated arena/lane/camera
        /// geometry.  NoteApproachRenderer and JudgementRingRenderer read EvaluatedArena/Lane
        /// values (including enabledBool and opacity) directly from here.
        /// Null before Start() completes.
        /// </summary>
        public ChartRuntimeEvaluator                      Evaluator        => _evaluator;

        /// <summary>
        /// Shared note approach lead time in milliseconds. All visual renderers read this value —
        /// Tap, Catch, Flick, and Hold. A single Inspector change propagates everywhere.
        /// Default: 2000.
        /// </summary>
        public int   NoteLeadTimeMs    => noteLeadTimeMs;

        /// <summary>
        /// Shared note spawn radius fraction [0..1].
        /// 0 = notes first appear at the inner band edge; 1 = at the judgement ring.
        /// All visual renderers read this value. Default: 0 (v0).
        /// </summary>
        public float SpawnRadiusFactor => spawnRadiusFactor;

        /// <summary>
        /// Active touches this frame in PlayfieldRoot local XY.
        /// Updated at the start of each <c>Update()</c> before judgement processing.
        /// Empty (not null) before Start() completes or when no touch is active.
        /// Production feedback renderers (e.g. <c>LaneTouchFeedbackRenderer</c>) read
        /// this to determine per-lane touch state without duplicating input logic.
        /// </summary>
        public IReadOnlyList<TouchSnapshot> ActiveTouches => _touches;

        // -------------------------------------------------------------------
        // DEBUG RENDERER API — read-only access for PlayerDebugRenderer.
        // All members prefixed "Debug" to signal scaffolding status.
        // Remove when PlayerDebugRenderer is removed.
        // -------------------------------------------------------------------

        /// <summary>DEBUG: Evaluated arena geometries (null until Start() completes).</summary>
        public IReadOnlyDictionary<string, ArenaGeometry> DebugArenaGeometries  => _arenaGeos;

        /// <summary>DEBUG: Evaluated lane geometries (null until Start() completes).</summary>
        public IReadOnlyDictionary<string, LaneGeometry>  DebugLaneGeometries   => _laneGeos;

        /// <summary>DEBUG: Maps laneId → arenaId (null until Start() completes).</summary>
        public IReadOnlyDictionary<string, string>        DebugLaneToArena      => _laneToArena;

        /// <summary>DEBUG: PlayfieldTransform instance (null until Start() completes).</summary>
        public PlayfieldTransform                         DebugPlayfieldTransform => _playfieldTransform;

        /// <summary>DEBUG: Notes within the judgement window this frame (updated each Update).</summary>
        public IReadOnlyList<RuntimeNote>                 DebugActiveNotes       => _activeNotes;

        /// <summary>DEBUG: All runtime notes sorted by PrimaryTimeMs. Active ones have entered
        /// the ActivationLeadMs window. Use for approach visualization.</summary>
        public IReadOnlyList<RuntimeNote>                 DebugAllNotes          => _scheduler?.AllNotes;

        /// <summary>DEBUG: Current effective chart time in ms (0 before playback starts).</summary>
        public double                                     DebugEffectiveChartTimeMs => _conductor?.EffectiveChartTimeMs ?? 0.0;

        /// <summary>DEBUG: Great-window size in ms from the active gameplay mode.</summary>
        public double                                     DebugGreatWindowMs     => _judgementEngine?.Windows.GreatWindowMs ?? 90.0;

        // Last successful touch hit in PlayfieldRoot local XY this frame.
        private Vector2 _debugLastTouchLocalXY;
        private bool    _debugHasTouchHit;

        /// <summary>DEBUG: True if a touch hit the playfield plane this frame.</summary>
        public bool    DebugHasTouchHit      => _debugHasTouchHit;

        /// <summary>DEBUG: Last touch hit point in PlayfieldRoot local XY (valid when DebugHasTouchHit).</summary>
        public Vector2 DebugLastTouchLocalXY => _debugLastTouchLocalXY;

        // Parallax-projection debug state (valid when DebugHasTouchHit).
        private Vector2 _debugLastPlaneLocalXY;
        private bool    _debugUsedVisualSurface;

        /// <summary>DEBUG: Flat-plane projected touch point this frame (always set even when visual surface is used).</summary>
        public Vector2 DebugLastPlaneLocalXY  => _debugLastPlaneLocalXY;

        /// <summary>DEBUG: True when the last touch used a visual surface raycast (not the flat plane).</summary>
        public bool    DebugUsedVisualSurface => _debugUsedVisualSurface;

        // ===================================================================
        // Unity lifecycle
        // ===================================================================

        // Awake runs before Start and — thanks to [DefaultExecutionOrder(-100)] — before
        // note renderers' Awake calls.  We use it exclusively to push visual-quality
        // settings into PlayerSettingsStore before the renderers read them.
        //
        // Note renderers read PlayerSettingsStore.NoteCapArcSegments in their own Awake
        // and allocate fixed-size mesh pools from that value.  This must happen first.
        private void Awake()
        {
            // Write the Inspector-facing noteCapArcSegments value to PlayerSettingsStore.
            // PlayerSettingsStore.NoteCapArcSegments (the property setter) persists the
            // clamped value to PlayerPrefs and enforces the minimum of 3.
            //
            // The note renderers' Awake calls (at default execution order 0) then read
            // back this value via the getter and build their mesh pools accordingly.
            //
            // TODO: replace this Inspector-field approach with a proper settings UI when
            // one is added to the project.  The PlayerPrefs path remains the same.
            PlayerSettingsStore.NoteCapArcSegments = noteCapArcSegments;
        }

        private void Start()
        {
            if (gameplayCamera == null || playfieldRoot == null || musicAudioSource == null)
            {
                SetError("Missing required Inspector references (Camera / playfieldRoot / AudioSource).");
                return;
            }

            _playfieldTransform = new PlayfieldTransform(playfieldLocalMin, playfieldLocalMax);
            _flickTracker       = new FlickGestureTracker();
            _conductor          = new Conductor();
            _catalog            = new PackCatalog();

            string packsRoot = ResolvePacksDirectory();
            PackScanner.Scan(_catalog, packsRoot);

            if (_catalog.Count == 0)
            {
                SetError($"No valid packs found in:\n{packsRoot}" +
                         "\n\nCopy at least one .rpk file there and restart.");
                return;
            }

            // Auto-select first pack, first difficulty (deterministic).
            _activePack = _catalog.Entries[0];
            DifficultyEntry diff = _activePack.Difficulties[0];

            Debug.Log($"[PlayerApp] Selected: '{_activePack.Title}' / '{diff.DifficultyId}'");

            // Read chart JSON from the RPK archive.
            if (!RpkReader.TryReadTextEntry(_activePack.RpkPath, diff.ChartPath,
                out string chartJson, out string readErr))
            {
                SetError($"Chart read error: {readErr}");
                return;
            }

            if (!ChartJsonReader.TryReadFromText(chartJson, out _chart, out string parseErr))
            {
                SetError($"Chart parse error: {parseErr}");
                return;
            }

            // Build the chart evaluator and pre-populate geometry at t=0.
            InitEvaluator();

            // Build RuntimeLane list for JudgementEngine (priority + laneId).
            var runtimeLanes = new List<RuntimeLane>(_chart.lanes.Count);
            foreach (ChartLane lane in _chart.lanes)
            {
                if (lane == null || string.IsNullOrEmpty(lane.laneId)) { continue; }
                runtimeLanes.Add(new RuntimeLane { LaneId = lane.laneId, Priority = lane.priority });
            }

            _scheduler       = new NoteScheduler(_chart);
            _judgementEngine = new JudgementEngine(
                gameplayMode, _playfieldTransform, runtimeLanes, _laneToArena);

            // --- Scoring (spec §4.5) ---
            // Cache the sweep-miss delegate once to avoid per-frame allocation.
            _onSweepMissDelegate = HandleSweepMiss;

            // Compute the chart-clock time after which all notes have been resolved
            // and the end-of-song summary can be safely logged.
            _songEndThresholdMs = ComputeSongEndThresholdMs();

            // Create the score tracker and wire it to this controller's events.
            _scoreTracker = new ScoreTracker
            {
                DebugLogScoreEachJudgement = debugLogScoreEachJudgement
            };
            _scoreTracker.Initialize(this);

            _statusText = $"Loading audio: {_activePack.Title}";
            _state      = AppState.LoadingAudio;
            StartCoroutine(LoadAudioAndStart());
        }

        private void Update()
        {
            if (_state != AppState.Playing) { return; }

            double chartTimeMs = _conductor.EffectiveChartTimeMs;

            // --- Geometry: per-frame evaluation via ChartRuntimeEvaluator (spec §5.9) ---
            // All animated arena/lane/camera tracks are sampled at effectiveChartTimeMs
            // (includes audioOffsetMs + UserOffsetMs per spec §3.3).
            _evaluator.Evaluate((int)chartTimeMs);
            SyncGeometryFromEvaluator();
            ApplyEvaluatedCamera();

            // --- Note lifecycle ---
            _scheduler.AdvanceActive(chartTimeMs, ActivationLeadMs);
            _scheduler.GetActiveInWindow(chartTimeMs, _judgementEngine.Windows.GreatWindowMs,
                _activeNotes);

            // --- Input: gather touches, update flick tracker ---
            GatherTouches(chartTimeMs);

            // --- Hold ticks (spec §7.5.1 — process before judgement) ---
            ProcessHoldTicks(chartTimeMs);

            // --- Judgement: Catch (all touches at once, spec §7.4) ---
            if (_judgementEngine.TryJudgeCatch(_touches, _activeNotes, chartTimeMs,
                _laneGeos, _arenaGeos, out JudgementRecord catchRec))
            {
                StoreJudgement(catchRec);
            }

            // --- Judgement: per-touch Tap / Hold / Flick ---
            foreach (TouchSnapshot touch in _touches)
            {
                if (_judgementEngine.TryJudgeTap(touch, _activeNotes, chartTimeMs,
                    _laneGeos, _arenaGeos, out JudgementRecord tapRec))
                {
                    StoreJudgement(tapRec);
                }

                if (_judgementEngine.TryBindHold(touch, _activeNotes, chartTimeMs,
                    _laneGeos, _arenaGeos, out RuntimeNote boundNote, out JudgementRecord holdRec))
                {
                    _boundHolds[touch.TouchId] = boundNote;
                    StoreJudgement(holdRec);
                }

                // Loop: each call processes one FlickEvent; loop until no more events for this touch.
                while (_judgementEngine.TryJudgeFlick(touch, _activeNotes, chartTimeMs,
                    _laneGeos, _arenaGeos, _flickTracker,
                    out JudgementRecord flickRec))
                {
                    StoreJudgement(flickRec);
                }
            }

            // --- Miss sweep: runs after all judgement so notes at the late edge are
            //     not swept before this frame's input can hit them.
            //     _onSweepMissDelegate (cached in Start) routes each newly-missed note
            //     to HandleSweepMiss for scoring — no per-frame allocation (spec §9). ---
            _scheduler.SweepMissed(chartTimeMs, _judgementEngine.Windows.GreatWindowMs,
                _onSweepMissDelegate);

            // --- Song end detection: log summary once audio stops and chart clock
            //     has passed the last note's expiry (spec §4.5 / §8.5). ---
            if (!_songFinished) { CheckSongEnd(chartTimeMs); }

            // --- Clean up ended touches ---
            foreach (int id in _endedTouches)
            {
                ReleaseHoldIfBound(id);
                _flickTracker.RemoveTouch(id);
                _touchPosNorm.Remove(id);
            }

            _prevChartTimeMs = chartTimeMs;
        }

        // ===================================================================
        // Audio loading coroutine
        // ===================================================================

        private IEnumerator LoadAudioAndStart()
        {
            // Extract audio/song.ogg bytes from the .rpk (it's a ZIP).
            if (!RpkReader.TryReadBinaryEntry(_activePack.RpkPath, "audio/song.ogg",
                out byte[] oggBytes, out string extractErr))
            {
                SetError($"Audio extract failed: {extractErr}");
                yield break;
            }

            // Write to a temp file so UnityWebRequest can stream it.
            string tempPath = Path.Combine(Application.temporaryCachePath, "rf_song.ogg");
            try
            {
                File.WriteAllBytes(tempPath, oggBytes);
            }
            catch (Exception ex)
            {
                SetError($"Temp audio write failed: {ex.Message}");
                yield break;
            }

            string url = new Uri(tempPath).AbsoluteUri;

            using (UnityWebRequest req =
                UnityWebRequestMultimedia.GetAudioClip(url, AudioType.OGGVORBIS))
            {
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    SetError($"Audio WebRequest failed: {req.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null)
                {
                    SetError("AudioClip was null after successful WebRequest.");
                    yield break;
                }

                musicAudioSource.clip = clip;

                // Start AudioSource and Conductor on the same frame for DSP-time sync (spec §3.3).
                musicAudioSource.Play();
                _conductor.StartPlaying(_chart.song.audioOffsetMs);
                _prevChartTimeMs = _conductor.EffectiveChartTimeMs;

                _state      = AppState.Playing;
                _statusText = $"{_activePack.Title} — {_activePack.Artist}";

                Debug.Log($"[PlayerApp] Playback started. " +
                          $"Notes: {_scheduler.Count}, " +
                          $"Arenas: {_arenaGeos.Count}, " +
                          $"Lanes: {_laneGeos.Count}");
            }
        }

        // ===================================================================
        // Input gathering
        // ===================================================================

        private void GatherTouches(double chartTimeMs)
        {
            _touches.Clear();
            _endedTouches.Clear();
            _debugHasTouchHit = false; // DEBUG: reset each frame

#if UNITY_EDITOR || UNITY_STANDALONE
            GatherMouseTouch(chartTimeMs);
#else
            GatherMobileTouches(chartTimeMs);
#endif
        }

        // Simulates a single touch (id=0) from the left mouse button.
        private void GatherMouseTouch(double chartTimeMs)
        {
            const int MouseId  = 0;
            Vector2   screenPos = Input.mousePosition;

            if (Input.GetMouseButtonDown(0))
            {
                if (TryProjectScreenToPlayfieldLocalXY(screenPos, out Vector2 hitLocal, out Vector2 posNorm, out bool usedSurf0))
                {
                    _flickTracker.BeginTouch(MouseId, chartTimeMs, posNorm);
                    _touchPosNorm[MouseId]     = posNorm;
                    _touches.Add(MakeSnapshot(MouseId, hitLocal, isNew: true));
                    _debugLastTouchLocalXY = hitLocal;  // DEBUG
                    _debugHasTouchHit      = true;      // DEBUG
                    _debugUsedVisualSurface = usedSurf0; // DEBUG
                }
                _mouseWasDown = true;
            }
            else if (Input.GetMouseButton(0) && _mouseWasDown)
            {
                if (TryProjectScreenToPlayfieldLocalXY(screenPos, out Vector2 hitLocal, out Vector2 posNorm, out bool usedSurfH))
                {
                    _flickTracker.UpdateTouch(MouseId, chartTimeMs, posNorm);
                    _touchPosNorm[MouseId]     = posNorm;
                    _touches.Add(MakeSnapshot(MouseId, hitLocal, isNew: false));
                    _debugLastTouchLocalXY = hitLocal;   // DEBUG
                    _debugHasTouchHit      = true;       // DEBUG
                    _debugUsedVisualSurface = usedSurfH; // DEBUG
                }
            }
            else if (Input.GetMouseButtonUp(0) && _mouseWasDown)
            {
                if (TryRaycast(screenPos, out _, out Vector2 posNorm))
                {
                    _flickTracker.EndTouch(MouseId, chartTimeMs, posNorm);
                }
                _endedTouches.Add(MouseId);
                _mouseWasDown = false;
            }
        }

        private void GatherMobileTouches(double chartTimeMs)
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch t  = Input.GetTouch(i);
                int   id = t.fingerId;

                switch (t.phase)
                {
                    case TouchPhase.Began:
                        if (TryProjectScreenToPlayfieldLocalXY(t.position, out Vector2 hitLocalB, out Vector2 posNormB, out bool usedSurfB))
                        {
                            _flickTracker.BeginTouch(id, chartTimeMs, posNormB);
                            _touchPosNorm[id]      = posNormB;
                            _touches.Add(MakeSnapshot(id, hitLocalB, isNew: true));
                            _debugLastTouchLocalXY  = hitLocalB;  // DEBUG
                            _debugHasTouchHit       = true;       // DEBUG
                            _debugUsedVisualSurface = usedSurfB;  // DEBUG
                        }
                        break;

                    case TouchPhase.Moved:
                    case TouchPhase.Stationary:
                        if (TryProjectScreenToPlayfieldLocalXY(t.position, out Vector2 hitLocalM, out Vector2 posNormM, out bool usedSurfM))
                        {
                            _flickTracker.UpdateTouch(id, chartTimeMs, posNormM);
                            _touchPosNorm[id]      = posNormM;
                            _touches.Add(MakeSnapshot(id, hitLocalM, isNew: false));
                            _debugLastTouchLocalXY  = hitLocalM;  // DEBUG
                            _debugHasTouchHit       = true;       // DEBUG
                            _debugUsedVisualSurface = usedSurfM;  // DEBUG
                        }
                        break;

                    case TouchPhase.Ended:
                    case TouchPhase.Canceled:
                        if (TryRaycast(t.position, out _, out Vector2 posNormE))
                        {
                            _flickTracker.EndTouch(id, chartTimeMs, posNormE);
                        }
                        _endedTouches.Add(id);
                        _touchPosNorm.Remove(id);
                        break;
                }
            }
        }

        // Builds a TouchSnapshot, resolving IsBound from the current _boundHolds state.
        private TouchSnapshot MakeSnapshot(int touchId, Vector2 hitLocalXY, bool isNew)
        {
            bool        isBound   = _boundHolds.TryGetValue(touchId, out RuntimeNote boundHold);
            return new TouchSnapshot
            {
                TouchId    = touchId,
                HitLocalXY = hitLocalXY,
                IsNew      = isNew,
                IsBound    = isBound,
                BoundHold  = boundHold,
            };
        }

        // ===================================================================
        // Hold tick processing (spec §7.5.1)
        // ===================================================================

        private void ProcessHoldTicks(double chartTimeMs)
        {
            foreach (var kvp in _boundHolds)
            {
                int         touchId = kvp.Key;
                RuntimeNote hold    = kvp.Value;

                if (hold.HoldBind != HoldBindState.Bound) { continue; }

                // Is the bound touch currently inside the hold's lane?
                bool insideLane = false;

                if (_touchPosNorm.TryGetValue(touchId, out Vector2 posNorm)        &&
                    _laneGeos.TryGetValue(hold.LaneId, out LaneGeometry laneGeo)   &&
                    _laneToArena.TryGetValue(hold.LaneId, out string arenaId)       &&
                    _arenaGeos.TryGetValue(arenaId, out ArenaGeometry arenaGeo))
                {
                    Vector2 hitLocal = _playfieldTransform.NormalizedToLocal(posNorm);
                    insideLane = ArenaHitTester.IsInsideFullLane(
                        hitLocal, arenaGeo, laneGeo, _playfieldTransform);
                }

                // Capture state before evaluation so we can detect the Active→Hit transition.
                NoteState stateBeforeTicks = hold.State;

                NoteScheduler.EvaluateHoldTicks(hold, _prevChartTimeMs, chartTimeMs, insideLane,
                    (tickMs, isPerfect) =>
                    {
                        JudgementTier tickTier = isPerfect
                            ? JudgementTier.Perfect
                            : JudgementTier.Miss;

                        Debug.Log($"[HoldTick] hold={hold.NoteId} " +
                                  $"tick@{tickMs}ms → {tickTier}");

                        // Emit tick judgement for scoring and combo (spec §4.4 / §4.5).
                        // ScoreTracker.HandleHoldTick updates PerfectCount/MissCount, combo,
                        // and TotalScore for each tick.
                        var tickRecord = new JudgementRecord
                        {
                            Note          = hold,
                            Tier          = tickTier,
                            IsPerfectPlus = false,
                            TimingErrorMs = 0.0,
                        };
                        OnHoldTick?.Invoke(tickRecord);

                        // On the first missed tick, fail the hold immediately:
                        //   - Set HoldBind = Finished so EvaluateHoldTicks breaks its loop
                        //     (no further ticks emitted — spec §4.5 "no spam").
                        //   - The visual system (HoldBodyRenderer) already handles
                        //     HoldBind == Finished by showing dim color until endTimeMs.
                        if (!isPerfect)
                        {
                            hold.HoldBind = HoldBindState.Finished;
                        }
                    });

                // EvaluateHoldTicks sets State = Hit when chartTimeMs >= EndTimeMs while
                // the hold is still Bound (all ticks passed).  Fire OnHoldResolved for
                // lifecycle subscribers; ScoreTracker ignores this for non-Unbound holds
                // because ticks already handled the score (spec §4.5).
                if (stateBeforeTicks != NoteState.Hit && hold.State == NoteState.Hit)
                {
                    var resolveRecord = new JudgementRecord
                    {
                        Note          = hold,
                        Tier          = JudgementTier.Perfect,
                        IsPerfectPlus = false,
                        TimingErrorMs = 0.0,
                    };
                    OnHoldResolved?.Invoke(resolveRecord);
                }
            }

            // Remove holds that finished this frame.
            // (Cannot mutate _boundHolds while iterating it.)
            _holdFinishBuffer.Clear();
            foreach (var kvp in _boundHolds)
            {
                if (kvp.Value.HoldBind == HoldBindState.Finished ||
                    kvp.Value.State    == NoteState.Hit)
                {
                    _holdFinishBuffer.Add(kvp.Key);
                }
            }
            foreach (int id in _holdFinishBuffer) { _boundHolds.Remove(id); }
        }

        // Called when a touch ends while a hold is bound to it (early release).
        private void ReleaseHoldIfBound(int touchId)
        {
            if (!_boundHolds.TryGetValue(touchId, out RuntimeNote hold)) { return; }

            if (hold.HoldBind == HoldBindState.Bound)
            {
                // Spec §7.5: early release → remaining ticks are Missed.
                hold.HoldBind = HoldBindState.Finished;
                int remaining = hold.TickTimesMs.Count - hold.NextTickIndex;

                if (remaining > 0)
                {
                    Debug.Log($"[PlayerApp] Hold {hold.NoteId} released early; " +
                              $"{remaining} tick(s) missed.");

                    // Emit exactly ONE Miss tick for the first unprocessed tick.
                    // This breaks the combo and marks the hold as failed for scoring,
                    // matching the rule "first missed tick fails the hold" (spec §7.5 / §4.5).
                    // We emit at most one so the player is not penalised for all remaining
                    // ticks — consistent with "no spam" (spec §4.5).
                    var earlyReleaseMiss = new JudgementRecord
                    {
                        Note          = hold,
                        Tier          = JudgementTier.Miss,
                        IsPerfectPlus = false,
                        TimingErrorMs = 0.0,
                    };
                    OnHoldTick?.Invoke(earlyReleaseMiss);
                }
                else
                {
                    // Touch released after all ticks were already processed — no miss emitted.
                    Debug.Log($"[PlayerApp] Hold {hold.NoteId} released after all ticks processed.");
                }
            }

            _boundHolds.Remove(touchId);
        }

        // ===================================================================
        // Scoring helpers (spec §4.5)
        // ===================================================================

        // Invoked by NoteScheduler.SweepMissed for each note newly marked Missed.
        // Routes non-hold notes through OnJudgement (for debug display etc.)
        // and hold notes through OnHoldResolved (for hold-specific scoring path).
        //
        // Hold guard: if a hold is swept while still Bound (can happen because
        // SweepMissed uses StartTimeMs not EndTimeMs for the expiry check), we
        // skip firing OnHoldResolved — the hold is still in progress and will
        // resolve later via EvaluateHoldTicks→State=Hit or the next sweep cycle
        // once HoldBind transitions to Finished/Unbound.
        private void HandleSweepMiss(RuntimeNote note)
        {
            var r = new JudgementRecord
            {
                Note          = note,
                Tier          = JudgementTier.Miss,
                IsPerfectPlus = false,
                TimingErrorMs = 0.0,
            };

            if (note.Type == NoteType.Hold)
            {
                // Score the hold as Missed only when it is truly done:
                //   Unbound  — player never started it (missed the bind window).
                //   Finished — player released early (ticks are all done/missed).
                // Skip if still Bound — mid-hold sweep edge case; let it resolve naturally.
                if (note.HoldBind != HoldBindState.Bound)
                {
                    OnHoldResolved?.Invoke(r);
                }
            }
            else
            {
                // Non-hold sweep-miss goes through the standard judgement path so
                // the debug overlay and any other OnJudgement subscribers see it.
                StoreJudgement(r);
            }
        }

        // Computes the latest chart-clock time at which any note could still be active,
        // used to confirm the song has fully ended before logging the score summary.
        // Uses EndTimeMs for holds (since that is when the hold's body expires), and
        // PrimaryTimeMs (== TimeMs) for all other note types.
        private double ComputeSongEndThresholdMs()
        {
            double maxExpiry = 0.0;

            foreach (RuntimeNote note in _scheduler.AllNotes)
            {
                // For holds, the body runs until EndTimeMs; for others, PrimaryTimeMs.
                double noteEnd = (note.Type == NoteType.Hold)
                    ? note.EndTimeMs
                    : note.PrimaryTimeMs;

                if (noteEnd > maxExpiry) { maxExpiry = noteEnd; }
            }

            // Add one Great window so the final sweep can process the last note.
            return maxExpiry + _judgementEngine.Windows.GreatWindowMs;
        }

        // Checks whether the song has ended (audio done + clock past last expiry).
        // Fires exactly once; sets _songFinished to prevent re-entry.
        private void CheckSongEnd(double chartTimeMs)
        {
            if (_state != AppState.Playing) { return; }

            // Wait for the audio to stop naturally (Unity stops AudioSource when clip ends).
            if (musicAudioSource.isPlaying) { return; }

            // Wait until the chart clock has passed the last note's expiry so all
            // pending sweep-misses have fired before we log the final summary.
            if (chartTimeMs < _songEndThresholdMs) { return; }

            _songFinished = true;

            // Build results snapshot and update status text for the OnGUI overlay.
            SongResults results = _scoreTracker.BuildResults();
            _statusText = $"Complete! Score={results.TotalScore}  MaxCombo={results.MaxCombo}  " +
                          $"P={results.PerfectCount} G={results.GreatCount} M={results.MissCount}";

            // Log the one-line summary (spec §4.5 / §8.5 requirement).
            _scoreTracker.LogSummary();
        }

        // Unsubscribes ScoreTracker when the controller is destroyed (e.g. scene change).
        private void OnDestroy()
        {
            _scoreTracker?.Dispose();
        }

        // ===================================================================
        // Pack directory resolution
        // ===================================================================

        // In Editor with useEditorProjectRootPacks=true:
        //   <project-root>/<editorPacksFolderName>    (e.g. …/cone-rhythm-game/DevPacks)
        // In builds (or when override is off):
        //   Application.persistentDataPath/<packsSubfolderName>
        private string ResolvePacksDirectory()
        {
#if UNITY_EDITOR
            if (useEditorProjectRootPacks)
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string dir         = Path.Combine(projectRoot, editorPacksFolderName);
                Debug.Log($"[PlayerApp] Pack location: EDITOR OVERRIDE → {dir}");
                return dir;
            }
#endif
            string buildDir = Path.Combine(Application.persistentDataPath, packsSubfolderName);
            Debug.Log($"[PlayerApp] Pack location: {buildDir}");
            return buildDir;
        }

        // ===================================================================
        // Geometry — evaluator setup and per-frame sync
        // ===================================================================

        // Creates the evaluator, builds the immutable lane→arena map, and populates
        // the geometry dicts at t=0 so they are ready before playback begins.
        private void InitEvaluator()
        {
            // Allocate output dicts. The evaluator owns evaluation; these dicts are
            // populated by SyncGeometryFromEvaluator() called every frame.
            _arenaGeos   = new Dictionary<string, ArenaGeometry>(StringComparer.Ordinal);
            _laneGeos    = new Dictionary<string, LaneGeometry>(StringComparer.Ordinal);
            _laneToArena = new Dictionary<string, string>(StringComparer.Ordinal);

            // Build the immutable lane→arena map from chart data (never changes).
            foreach (ChartLane lane in _chart.lanes)
            {
                if (lane == null || string.IsNullOrEmpty(lane.laneId)) { continue; }
                if (!string.IsNullOrEmpty(lane.arenaId))
                {
                    _laneToArena[lane.laneId] = lane.arenaId;
                }
            }

            // Create the evaluator. Its constructor runs Evaluate(0) internally.
            _evaluator = new ChartRuntimeEvaluator(_chart);

            // Sync dicts from initial evaluation so geometry is ready before first Update.
            SyncGeometryFromEvaluator();

            Debug.Log($"[PlayerApp] Evaluator ready: " +
                      $"{_evaluator.ArenaCount} arena(s), {_evaluator.LaneCount} lane(s).");
        }

        // Syncs _arenaGeos and _laneGeos from the evaluator's latest Evaluate() results.
        // Only ENABLED arenas/lanes are included — disabled ones are removed from the dicts
        // so hit-testing and visuals skip them automatically (spec §5.6).
        // Called every frame immediately after _evaluator.Evaluate(timeMs).
        private void SyncGeometryFromEvaluator()
        {
            // Arenas — map EvaluatedArena → ArenaGeometry (drop disabled).
            for (int i = 0; i < _evaluator.ArenaCount; i++)
            {
                EvaluatedArena ea = _evaluator.GetArena(i);
                if (string.IsNullOrEmpty(ea.ArenaId)) { continue; }

                if (ea.EnabledBool)
                {
                    _arenaGeos[ea.ArenaId] = new ArenaGeometry
                    {
                        CenterXNorm       = ea.CenterXNorm,
                        CenterYNorm       = ea.CenterYNorm,
                        OuterRadiusNorm   = ea.OuterRadiusNorm,
                        BandThicknessNorm = ea.BandThicknessNorm,
                        ArcStartDeg       = ea.ArcStartDeg,
                        ArcSweepDeg       = ea.ArcSweepDeg,
                    };
                }
                else
                {
                    // Remove disabled arenas so they receive no hit-testing or visuals.
                    _arenaGeos.Remove(ea.ArenaId);
                }
            }

            // Lanes — map EvaluatedLane → LaneGeometry (drop disabled).
            for (int i = 0; i < _evaluator.LaneCount; i++)
            {
                EvaluatedLane el = _evaluator.GetLane(i);
                if (string.IsNullOrEmpty(el.LaneId)) { continue; }

                if (el.EnabledBool)
                {
                    _laneGeos[el.LaneId] = new LaneGeometry
                    {
                        CenterDeg = el.CenterDeg,
                        WidthDeg  = el.WidthDeg,
                    };
                }
                else
                {
                    _laneGeos.Remove(el.LaneId);
                }
            }
        }

        // Applies the evaluated camera tracks to the gameplay camera.
        // Only overrides the scene camera if the chart has authored position keyframes.
        // Charts without camera tracks leave the scene camera position/rotation unchanged.
        private void ApplyEvaluatedCamera()
        {
            if (gameplayCamera == null || _evaluator == null) { return; }
            if (_chart.camera == null) { return; }

            // Guard: only apply if the chart has explicitly authored camera position.
            // A chart without camera keyframes should not snap the camera to (0,0,5).
            bool hasAuthoredPosition = (_chart.camera.posX.keyframes?.Count ?? 0) > 0
                                    || (_chart.camera.posY.keyframes?.Count ?? 0) > 0
                                    || (_chart.camera.posZ.keyframes?.Count ?? 0) > 0;
            if (!hasAuthoredPosition) { return; }

            EvaluatedCamera cam = _evaluator.Camera;
            if (!cam.EnabledBool) { return; }

            // Apply world-space position and rotation (chart camera tracks are world-space).
            gameplayCamera.transform.position = new UnityEngine.Vector3(cam.PosX, cam.PosY, cam.PosZ);
            gameplayCamera.transform.rotation =
                UnityEngine.Quaternion.Euler(cam.RotPitchDeg, cam.RotYawDeg, cam.RotRollDeg);

            if (cam.FovDeg > 0f) { gameplayCamera.fieldOfView = cam.FovDeg; }
        }

        // ===================================================================
        // Screen → playfield raycasting
        // ===================================================================

        // Projects a screen position to PlayfieldRoot local XY for hit-testing (spec §5.2.1).
        //
        // Two-step strategy:
        //   1. If useVisualSurfaceRaycast is enabled, cast a Physics ray against
        //      visualSurfaceLayerMask. If it hits, convert hit.point to PlayfieldRoot
        //      local space and take only (x, y) — localZ is discarded. This corrects
        //      the parallax mismatch that occurs when the arena surface has depth (frustum mesh).
        //   2. Fall back to the flat Z=0 plane intersection (TryRaycast) if:
        //      - useVisualSurfaceRaycast is false, OR
        //      - the Physics ray misses all colliders on the mask.
        //
        // The flat-plane result is always computed first for debug tracking (_debugLastPlaneLocalXY).
        private bool TryProjectScreenToPlayfieldLocalXY(
            Vector2  screenPos,
            out Vector2 hitLocalXY,
            out Vector2 posNorm,
            out bool    usedVisualSurface)
        {
            hitLocalXY        = Vector2.zero;
            posNorm           = Vector2.zero;
            usedVisualSurface = false;

            // Always compute the flat-plane projection — needed as fallback and for debug tracking.
            bool planeHit = TryRaycast(screenPos, out Vector2 planeLocalXY, out Vector2 planeNorm);
            _debugLastPlaneLocalXY = planeLocalXY; // DEBUG

            if (useVisualSurfaceRaycast && gameplayCamera != null)
            {
                Ray ray = gameplayCamera.ScreenPointToRay(screenPos);
                if (Physics.Raycast(ray, out RaycastHit physHit, Mathf.Infinity, visualSurfaceLayerMask))
                {
                    // Convert world hit point to PlayfieldRoot local space; take only XY.
                    Vector3 local3 = playfieldRoot.InverseTransformPoint(physHit.point);
                    hitLocalXY        = new Vector2(local3.x, local3.y);
                    posNorm           = _playfieldTransform.LocalToNormalized(hitLocalXY);
                    usedVisualSurface = true;
                    return true;
                }
            }

            if (!planeHit) { return false; }
            hitLocalXY = planeLocalXY;
            posNorm    = planeNorm;
            return true;
        }

        // Intersects a camera ray with playfieldRoot's local XY plane (local Z = 0).
        // Outputs the hit in local XY and normalized [0..1] playfield coordinates.
        private bool TryRaycast(Vector2 screenPos,
                                out Vector2 hitLocalXY,
                                out Vector2 posNorm)
        {
            hitLocalXY = Vector2.zero;
            posNorm    = Vector2.zero;

            Ray     ray    = gameplayCamera.ScreenPointToRay(screenPos);
            Vector3 normal = playfieldRoot.forward;   // world-space local +Z = plane normal
            Vector3 origin = playfieldRoot.position;  // a point on the local Z=0 plane

            // t = dot(origin - ray.origin, normal) / dot(ray.direction, normal)
            float denom = Vector3.Dot(normal, ray.direction);
            if (Mathf.Abs(denom) < 1e-6f) { return false; } // ray parallel to plane

            float t = Vector3.Dot(origin - ray.origin, normal) / denom;
            if (t < 0f) { return false; }                   // plane behind camera

            Vector3 worldHit = ray.origin + ray.direction * t;
            Vector3 localHit = playfieldRoot.InverseTransformPoint(worldHit);

            hitLocalXY = new Vector2(localHit.x, localHit.y);
            posNorm    = _playfieldTransform.LocalToNormalized(hitLocalXY);
            return true;
        }

        // ===================================================================
        // Helpers
        // ===================================================================

        private void StoreJudgement(JudgementRecord r)
        {
            string plus = r.IsPerfectPlus ? "+" : "";
            _lastJudgementText = $"{r.Note.Type}  {r.Tier}{plus}  " +
                                 $"({r.TimingErrorMs:+0.0;-0.0} ms)";
            OnJudgement?.Invoke(r);
        }

        private void SetError(string msg)
        {
            _statusText = msg;
            _state      = AppState.Error;
            Debug.LogError($"[PlayerApp] {msg}");
        }

        // ===================================================================
        // Debug overlay (OnGUI)
        // ===================================================================

        private void OnGUI()
        {
            const float X  = 10f;
            const float LH = 22f;
            float       y  = 10f;

            GUI.Label(new Rect(X, y, 700, LH),
                $"[RhythmicFlow v0]  State: {_state}  Mode: {gameplayMode}"); y += LH;

            GUI.Label(new Rect(X, y, 700, LH), _statusText); y += LH;

            if (_state == AppState.Playing && _conductor != null)
            {
                double ct = _conductor.EffectiveChartTimeMs;
                int    n  = _scheduler?.Count ?? 0;
                GUI.Label(new Rect(X, y, 700, LH),
                    $"Chart time: {ct:F0} ms   Total notes: {n}"); y += LH;
            }

            // Score / combo display (spec §4.5 / §8.5).
            if (_scoreTracker != null)
            {
                GUI.Label(new Rect(X, y, 700, LH),
                    $"Score: {_scoreTracker.TotalScore,8}   " +
                    $"Combo: {_scoreTracker.CurrentCombo,4} (max {_scoreTracker.MaxCombo,4})   " +
                    $"P={_scoreTracker.PerfectCount} G={_scoreTracker.GreatCount} " +
                    $"M={_scoreTracker.MissCount}");
                y += LH;
            }

            GUI.Label(new Rect(X, y, 700, LH),
                $"Last judgement: {_lastJudgementText}"); y += LH;

            if (_state == AppState.Error)
            {
                Color prev = GUI.color;
                GUI.color = Color.red;
                GUI.Label(new Rect(X, y, 800, LH * 5), $"ERROR:\n{_statusText}");
                GUI.color = prev;
            }
        }
    }
}
