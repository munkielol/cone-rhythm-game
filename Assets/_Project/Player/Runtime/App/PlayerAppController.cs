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
//        a) Advance/sweep NoteScheduler.
//        b) Poll touches (mouse in Editor/Standalone, Input.touches on mobile).
//        c) Raycast screen→ playfieldRoot local XY plane → normalized XY.
//        d) Feed touches into FlickGestureTracker (Begin/Update/End).
//        e) Run JudgementEngine: TryJudgeCatch (once), then per-touch
//           TryJudgeTap / TryBindHold / TryJudgeFlick.
//        f) Evaluate hold ticks (spec §7.5.1).
//        g) Clean up ended touches.
//   6) OnGUI overlay: status, chart time, note count, last judgement.
//
// NO note rendering — geometry snapshots use first-keyframe values only (v0 harness).
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
        // Geometry — static snapshots (first-keyframe, v0 harness)
        // -------------------------------------------------------------------

        private Dictionary<string, ArenaGeometry> _arenaGeos;
        private Dictionary<string, LaneGeometry>  _laneGeos;
        private Dictionary<string, string>        _laneToArena; // laneId → arenaId

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
        // Debug overlay
        // -------------------------------------------------------------------

        private string _lastJudgementText = "—";

        /// <summary>
        /// Fires each time a note is judged (tap / catch / flick).
        /// Subscribed by PlayerDebugRenderer for judgement flash visuals.
        /// Does NOT fire for sweep-misses (those bypass StoreJudgement).
        /// </summary>
        public event Action<JudgementRecord> OnJudgement;

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

            // Build static geometry snapshots (first keyframe of each track).
            BuildGeometrySnapshots();

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

            _statusText = $"Loading audio: {_activePack.Title}";
            _state      = AppState.LoadingAudio;
            StartCoroutine(LoadAudioAndStart());
        }

        private void Update()
        {
            if (_state != AppState.Playing) { return; }

            double chartTimeMs = _conductor.EffectiveChartTimeMs;

            // --- Geometry: re-evaluate all animated tracks at current chart time ---
            EvaluateGeometry((int)chartTimeMs);

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
            //     not swept before this frame's input can hit them. ---
            _scheduler.SweepMissed(chartTimeMs, _judgementEngine.Windows.GreatWindowMs);

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

                NoteScheduler.EvaluateHoldTicks(hold, _prevChartTimeMs, chartTimeMs, insideLane,
                    (tickMs, isPerfect) =>
                    {
                        Debug.Log($"[HoldTick] hold={hold.NoteId} " +
                                  $"tick@{tickMs}ms → {(isPerfect ? "Perfect" : "Miss")}");
                    });
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
                }
            }

            _boundHolds.Remove(touchId);
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
        // Geometry — track evaluation
        // ===================================================================

        // Initializes geometry dictionaries and builds the static lane→arena map.
        // Calls EvaluateGeometry(0) so the dicts are populated before playback begins.
        private void BuildGeometrySnapshots()
        {
            _arenaGeos   = new Dictionary<string, ArenaGeometry>(StringComparer.Ordinal);
            _laneGeos    = new Dictionary<string, LaneGeometry>(StringComparer.Ordinal);
            _laneToArena = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (ChartLane lane in _chart.lanes)
            {
                if (lane == null || string.IsNullOrEmpty(lane.laneId)) { continue; }
                if (!string.IsNullOrEmpty(lane.arenaId))
                {
                    _laneToArena[lane.laneId] = lane.arenaId;
                }
            }

            EvaluateGeometry(0);

            Debug.Log($"[PlayerApp] Geometry built: " +
                      $"{_arenaGeos.Count} arena(s), {_laneGeos.Count} lane(s).");
        }

        // Evaluates all animated arena/lane tracks at timeMs and updates the geometry dicts.
        // Called once at chart load (t=0) and every frame during playback.
        private void EvaluateGeometry(int timeMs)
        {
            foreach (ChartArena arena in _chart.arenas)
            {
                if (arena == null || string.IsNullOrEmpty(arena.arenaId)) { continue; }

                _arenaGeos[arena.arenaId] = new ArenaGeometry
                {
                    CenterXNorm       = arena.centerX.Evaluate(timeMs,       0.5f),
                    CenterYNorm       = arena.centerY.Evaluate(timeMs,       0.5f),
                    OuterRadiusNorm   = arena.outerRadius.Evaluate(timeMs,   0.4f),
                    BandThicknessNorm = arena.bandThickness.Evaluate(timeMs, 0.1f),
                    ArcStartDeg       = arena.arcStartDeg.Evaluate(timeMs,   0f),
                    ArcSweepDeg       = arena.arcSweepDeg.Evaluate(timeMs,   360f),
                };
            }

            foreach (ChartLane lane in _chart.lanes)
            {
                if (lane == null || string.IsNullOrEmpty(lane.laneId)) { continue; }

                _laneGeos[lane.laneId] = new LaneGeometry
                {
                    CenterDeg = lane.centerDeg.EvaluateAngleDeg(timeMs, 0f),
                    WidthDeg  = lane.widthDeg.Evaluate(timeMs,          30f),
                };
            }
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
