// JudgementEngine.cs
// Evaluates input touches against active notes and produces JudgementRecords.
//
// Arbitration (tie-break order, locked, spec §7.6):
//   When multiple note candidates are hit by a single touch at once:
//   1) Smallest abs timing error  |effectiveChartTimeMs - noteTimeMs|
//   2) Smallest angular distance to lane centerline
//   3) Higher lane priority  (lane.priority larger wins)
//   4) Lower noteIndex  (earlier in file order — stable fallback)
//
// Note type rules:
//   Tap   — TouchBegin inside lane within GreatWindowMs (spec §7.2)
//   Flick — event-based: FlickGestureTracker emits FlickEvents; engine matches each to a note
//           (spec §7.3). Supports rapid consecutive flicks without lifting.
//   Catch — any touch inside lane within [timeMs-Great, timeMs+Great] (spec §7.4.1)
//   Hold  — TouchBegin inside lane within GreatWindowMs of startTimeMs (spec §7.5)
//
// Hold ticks and Catch: Perfect-or-Miss only (spec §4.4).
// Flick: Perfect/Great/Miss by timing; FlickPerfectWindowCoversGreatWindow suppresses Great (spec §7.3).
//
// This class is allocation-light: candidate lists are reused across calls.

using System;
using System.Collections.Generic;
using RhythmicFlow.Shared;
using UnityEngine;

namespace RhythmicFlow.Player
{
    // -----------------------------------------------------------------------
    // Input touch snapshot (one frame)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Touch state at a single point in time, mapped to PlayfieldLocal coordinates.
    /// </summary>
    public struct TouchSnapshot
    {
        /// <summary>Stable touch identifier (from Input.GetTouch or new Input System).</summary>
        public int TouchId;

        /// <summary>Hit position in PlayfieldRoot local XY (from ray-plane intersection).</summary>
        public Vector2 HitLocalXY;

        /// <summary>Whether this is the first frame of this touch (TouchPhase.Began).</summary>
        public bool IsNew;

        /// <summary>
        /// True if this touch is currently bound to a hold note (spec §7.1).
        /// Bound touches may still satisfy Flick/Catch (spec §7.1 footnote).
        /// </summary>
        public bool IsBound;

        /// <summary>
        /// The hold note this touch is bound to (null if IsBound == false).
        /// </summary>
        public RuntimeNote BoundHold;
    }

    // -----------------------------------------------------------------------
    // Candidate (used internally during arbitration)
    // -----------------------------------------------------------------------

    // Holds a note candidate with pre-computed sort keys.
    internal struct NoteCandidate
    {
        public RuntimeNote Note;
        public double      AbsTimingErrorMs;
        public float       AngularDistanceDeg;
        public int         LanePriority;
    }

    // -----------------------------------------------------------------------
    // LaneInfo (required by JudgementEngine for arbitration criteria 2-3)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runtime lane metadata needed for judgement arbitration.
    /// Populated at chart load time from ChartLane and ChartJsonV1 ordering.
    /// </summary>
    public class RuntimeLane
    {
        /// <summary>Lane identifier matching ChartLane.laneId.</summary>
        public string LaneId { get; set; }

        /// <summary>Priority for tie-breaking (spec §7.6 criterion 3; larger wins).</summary>
        public int Priority { get; set; }
    }

    // -----------------------------------------------------------------------
    // JudgementEngine
    // -----------------------------------------------------------------------

    /// <summary>
    /// Processes one touch against a set of active notes and returns the best candidate.
    /// Performs full arbitration per spec §7.6.
    /// </summary>
    public class JudgementEngine
    {
        private readonly JudgementWindows            _windows;
        private readonly PlayfieldTransform          _playfieldTransform;
        private readonly Dictionary<string, RuntimeLane> _laneMap;      // laneId → RuntimeLane
        private readonly Dictionary<string, string>      _laneToArena;  // laneId → arenaId (F2 fix)

        // Reusable candidate buffer (avoids per-frame allocation).
        private readonly List<NoteCandidate> _candidates = new List<NoteCandidate>(16);

        // Reusable candidate buffer for flick event matching (inner loop per event).
        // Separate from _candidates so arming + event loops don't interfere.
        private readonly List<NoteCandidate> _flickCandidates = new List<NoteCandidate>(8);

        // -------------------------------------------------------------------
        // Debug: flick logging (disabled by default)
        // -------------------------------------------------------------------

        /// <summary>
        /// DEBUG: Set true to log why each flick gesture or note candidate is rejected.
        /// Normal value: false. Toggle via the Inspector or in code before gameplay.
        /// Each (note, touch) pair and each gesture attempt is logged at most once per
        /// session to avoid per-frame spam. Toggle off then on to effectively reset.
        /// </summary>
        public bool DebugLogFlick = true;

        // Tracks already-logged keys so we only emit one log per failure per (note, touch).
        // Key format:  "G:{touchId}"          — gesture threshold failures
        //              "N:{noteId}:{touchId}" — per-note skip reasons
        private readonly HashSet<string> _flickDebugLogged =
            new HashSet<string>(StringComparer.Ordinal);

        // Armed set for free-touch flick arming (FlickRequireTouchBegin = false).
        // Key format: "ARM:{noteId}:{touchId}"
        // Cleared when a note leaves the timing window or is judged.
        private readonly HashSet<string> _flickArmedSet =
            new HashSet<string>(StringComparer.Ordinal);

        // -------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------

        /// <param name="laneIdToArenaId">
        /// Maps every laneId to the arenaId that owns it. Required for correct
        /// multi-arena hit-testing (F2 fix — see IsInsideLane). Build this from
        /// ChartJsonV1.arenas[*].lanes[*] at chart-load time.
        /// </param>
        public JudgementEngine(
            GameplayMode       mode,
            PlayfieldTransform playfieldTransform,
            IEnumerable<RuntimeLane> lanes,
            IReadOnlyDictionary<string, string> laneIdToArenaId)
        {
            _windows            = JudgementWindows.ForMode(mode);
            _playfieldTransform = playfieldTransform;

            _laneMap = new Dictionary<string, RuntimeLane>(StringComparer.Ordinal);

            foreach (RuntimeLane lane in lanes)
            {
                _laneMap[lane.LaneId] = lane;
            }

            // Copy into a mutable dict so we don't hold a reference to the caller's object.
            _laneToArena = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var kvp in laneIdToArenaId)
            {
                _laneToArena[kvp.Key] = kvp.Value;
            }
        }

        // Exposed for external callers (e.g. scoring).
        public JudgementWindows Windows => _windows;

        // -------------------------------------------------------------------
        // Tap judgement (spec §7.2)
        // -------------------------------------------------------------------

        /// <summary>
        /// Evaluates a new touch against all Active Tap notes.
        /// Returns a JudgementRecord if a candidate was found; returns false otherwise.
        /// </summary>
        public bool TryJudgeTap(
            TouchSnapshot             touch,
            IReadOnlyList<RuntimeNote> activeNotes,
            double                    effectiveChartTimeMs,
            IDictionary<string, LaneGeometry>  laneGeometries,
            IDictionary<string, ArenaGeometry> arenaGeometries,
            out JudgementRecord       record)
        {
            record = default;

            if (!touch.IsNew) { return false; } // Tap requires TouchBegin.

            _candidates.Clear();

            foreach (RuntimeNote note in activeNotes)
            {
                if (note.Type != NoteType.Tap) { continue; }
                if (!note.Judging)             { continue; }

                double timingError = effectiveChartTimeMs - note.TimeMs;

                if (!_windows.IsHittable(timingError)) { continue; }

                if (!IsInsideLane(touch.HitLocalXY, note.LaneId, laneGeometries, arenaGeometries,
                    out float thetaDeg)) { continue; }

                _candidates.Add(new NoteCandidate
                {
                    Note               = note,
                    AbsTimingErrorMs   = Math.Abs(timingError),
                    AngularDistanceDeg = GetAngularDistance(thetaDeg, note.LaneId, laneGeometries),
                    LanePriority       = GetLanePriority(note.LaneId)
                });
            }

            if (_candidates.Count == 0) { return false; }

            RuntimeNote best = Arbitrate(_candidates);
            double bestError = effectiveChartTimeMs - best.TimeMs;
            var (tier, isPlus) = _windows.Evaluate(
                bestError, PlayerSettingsStore.PerfectWindowCoversGreatWindow);

            best.State = NoteState.Hit;
            record = MakeRecord(best, tier, isPlus, bestError);

            LogJudgement(record);
            return true;
        }

        // -------------------------------------------------------------------
        // Catch judgement (spec §7.4 / §7.4.1)
        // -------------------------------------------------------------------

        /// <summary>
        /// Evaluates all Active Catch notes (any touch, not just new ones).
        /// A catch is Perfect if any touch is inside the lane at any instant in the window.
        /// Must be called once per frame for all active touches.
        /// </summary>
        public bool TryJudgeCatch(
            IReadOnlyList<TouchSnapshot>       touches,
            IReadOnlyList<RuntimeNote>         activeNotes,
            double                             effectiveChartTimeMs,
            IDictionary<string, LaneGeometry>  laneGeometries,
            IDictionary<string, ArenaGeometry> arenaGeometries,
            out JudgementRecord                record)
        {
            record = default;

            _candidates.Clear();

            foreach (RuntimeNote note in activeNotes)
            {
                if (note.Type != NoteType.Catch) { continue; }
                if (!note.Judging)               { continue; }

                double timingError = effectiveChartTimeMs - note.TimeMs;

                if (!_windows.IsHittable(timingError)) { continue; }

                // Any touch inside the lane satisfies the catch (spec §7.4).
                bool anyTouchInside = false;
                float bestTheta = 0f;

                foreach (TouchSnapshot t in touches)
                {
                    if (IsInsideLane(t.HitLocalXY, note.LaneId, laneGeometries, arenaGeometries,
                        out float theta))
                    {
                        anyTouchInside = true;
                        bestTheta = theta;
                        break;
                    }
                }

                if (!anyTouchInside) { continue; }

                _candidates.Add(new NoteCandidate
                {
                    Note               = note,
                    AbsTimingErrorMs   = Math.Abs(timingError),
                    AngularDistanceDeg = GetAngularDistance(bestTheta, note.LaneId, laneGeometries),
                    LanePriority       = GetLanePriority(note.LaneId)
                });
            }

            if (_candidates.Count == 0) { return false; }

            RuntimeNote best = Arbitrate(_candidates);
            // Spec §4.4: Catch is Perfect-or-Miss. Within window → Perfect.
            best.State = NoteState.Hit;
            record = MakeRecord(best, JudgementTier.Perfect, isPerfectPlus: false,
                timingErrorMs: effectiveChartTimeMs - best.TimeMs);

            LogJudgement(record);
            return true;
        }

        // -------------------------------------------------------------------
        // Flick judgement (spec §7.3 / §7.3.1)
        // -------------------------------------------------------------------

        /// <summary>
        /// Evaluates a touch against all Active Flick notes using the event-based model (spec §7.3).
        ///
        /// A FlickEvent is produced by <see cref="FlickGestureTracker"/> when a gesture exceeds
        /// distance, velocity, and elapsed-time thresholds.  Multiple FlickEvents can occur during
        /// one continuous touch, enabling rapid sequences (e.g. U then D without lifting).
        ///
        /// Call once per touch per frame.  Returns true for the FIRST event that matches a note;
        /// call again in a loop until false to consume all queued events for the same touch.
        ///
        /// Flick tier selection is governed by
        /// <see cref="PlayerSettingsStore.FlickPerfectWindowCoversGreatWindow"/> (spec §8.3.1):
        ///   false (default): Perfect / Great / Miss based on timing windows.
        ///   true:            Perfect covers full GreatWindowMs; Great suppressed.
        /// </summary>
        /// <param name="touch">Current touch state (any phase).</param>
        /// <param name="gestureTracker">Shared FlickGestureTracker fed by the input layer.</param>
        public bool TryJudgeFlick(
            TouchSnapshot              touch,
            IReadOnlyList<RuntimeNote> activeNotes,
            double                     effectiveChartTimeMs,
            IDictionary<string, LaneGeometry>  laneGeometries,
            IDictionary<string, ArenaGeometry> arenaGeometries,
            FlickGestureTracker        gestureTracker,
            out JudgementRecord        record)
        {
            record = default;

            // ---- Arming pass ----
            // A note is "armed" for a touch when the touch has been inside the note's lane
            // within the timing window. Once armed, FlickEvents may be judged even if the
            // event position is outside the lane (the swipe naturally leaves the lane).

            if (!PlayerSettingsStore.FlickRequireTouchBegin)
            {
                // Free-touch mode: arm on first frame the touch is in-lane + in-window.
                // Also reset the gesture baseline at that moment so elapsed/distance are
                // measured from eligibility, not from the original touch-down.
                foreach (RuntimeNote armNote in activeNotes)
                {
                    if (armNote.Type != NoteType.Flick || !armNote.Judging) { continue; }

                    double armTe = effectiveChartTimeMs - armNote.TimeMs;
                    if (!_windows.IsHittable(armTe))
                    {
                        // Note left the window — remove stale arm entry.
                        _flickArmedSet.Remove($"ARM:{armNote.NoteId}:{touch.TouchId}");
                        continue;
                    }

                    if (!IsInsideLane(touch.HitLocalXY, armNote.LaneId,
                        laneGeometries, arenaGeometries, out _)) { continue; }

                    string ak = $"ARM:{armNote.NoteId}:{touch.TouchId}";
                    if (!_flickArmedSet.Contains(ak))
                    {
                        // First time eligible: reset gesture baseline and mark armed.
                        Vector2 posNorm = _playfieldTransform.LocalToNormalized(touch.HitLocalXY);
                        gestureTracker.ResetGesture(touch.TouchId, effectiveChartTimeMs, posNorm);
                        _flickArmedSet.Add(ak);
                    }
                }
            }
            else
            {
                // Require-begin mode: arm on any frame where the touch is in-lane + in-window,
                // provided the touch began recently (within FlickMaxGestureTimeMs).
                // This handles the case where the timing window opens a few frames after the
                // touch starts — IsNew would be false by then, so we can't gate on it.
                if (gestureTracker.TryGetTouchBeginTimeMs(touch.TouchId, out double beginTimeMs) &&
                    (effectiveChartTimeMs - beginTimeMs) <= PlayerSettingsStore.FlickMaxGestureTimeMs)
                {
                    foreach (RuntimeNote armNote in activeNotes)
                    {
                        if (armNote.Type != NoteType.Flick || !armNote.Judging) { continue; }

                        double armTe = effectiveChartTimeMs - armNote.TimeMs;
                        if (!_windows.IsHittable(armTe)) { continue; }

                        if (!IsInsideLane(touch.HitLocalXY, armNote.LaneId,
                            laneGeometries, arenaGeometries, out _)) { continue; }

                        _flickArmedSet.Add($"ARM:{armNote.NoteId}:{touch.TouchId}");
                    }
                }
            }

            // Process FlickEvents one at a time.  Each call to TryJudgeFlick consumes at most
            // one event (the first that matches a note), so the caller must loop.
            while (gestureTracker.TryDequeueEvent(touch.TouchId, out FlickEvent evt))
            {
                // FlickRequireTouchBegin gate:
                // Only allow events where the gesture was completed within FlickMaxGestureTimeMs
                // of the original touch-begin.  Events from gestures on an ongoing (non-new)
                // touch are discarded.
                if (PlayerSettingsStore.FlickRequireTouchBegin)
                {
                    double timeSinceBegin = evt.EventTimeMs - evt.TouchBeginTimeMs;
                    if (timeSinceBegin > PlayerSettingsStore.FlickMaxGestureTimeMs)
                    {
                        if (DebugLogFlick && _flickDebugLogged.Add(
                            $"EGATE:{touch.TouchId}:{evt.EventTimeMs:F0}"))
                        {
                            Debug.Log(
                                $"[FlickDebug] Event GATE  touchId={touch.TouchId}" +
                                $"  t={evt.EventTimeMs:F0}ms  reason=require_begin_too_late" +
                                $"  timeSinceBegin={timeSinceBegin:F0}ms" +
                                $"  max={PlayerSettingsStore.FlickMaxGestureTimeMs}ms");
                        }
                        continue; // Discard this event; check the next.
                    }
                }

                // Find all matching flick note candidates for this event.
                // Use event position (converted to local XY) for lane hit testing, and
                // event displacement for direction matching.
                _flickCandidates.Clear();
                Vector2 evtHitLocal = _playfieldTransform.NormalizedToLocal(evt.PosNorm);

                foreach (RuntimeNote note in activeNotes)
                {
                    if (note.Type != NoteType.Flick) { continue; }
                    if (!note.Judging)               { continue; }

                    // Timing check: event time must be within the note's great window.
                    double timingError = evt.EventTimeMs - note.TimeMs;
                    if (!_windows.IsHittable(timingError))
                    {
                        if (DebugLogFlick && _flickDebugLogged.Add(
                            $"ESKIP:{note.NoteId}:{touch.TouchId}:{evt.EventTimeMs:F0}"))
                        {
                            Debug.Log(
                                $"[FlickDebug] Note SKIP  noteId={note.NoteId}" +
                                $"  laneId={note.LaneId}  touchId={touch.TouchId}" +
                                $"  reason=timing_window  timingErr={timingError:F1}ms" +
                                $"  window=±{_windows.GreatWindowMs:F0}ms" +
                                $"  eventTime={evt.EventTimeMs:F0}ms");
                        }
                        // Remove stale arm entry regardless of toggle mode.
                        _flickArmedSet.Remove($"ARM:{note.NoteId}:{touch.TouchId}");
                        continue;
                    }

                    // Arming check: the touch must have entered this lane within the timing
                    // window at some earlier point. Once armed the event position may be
                    // anywhere — the swipe is allowed to exit the lane after arming.
                    string armKey = $"ARM:{note.NoteId}:{touch.TouchId}";
                    if (!_flickArmedSet.Contains(armKey))
                    {
                        if (DebugLogFlick && _flickDebugLogged.Add(
                            $"ESKIP:{note.NoteId}:{touch.TouchId}:{evt.EventTimeMs:F0}"))
                        {
                            Debug.Log(
                                $"[FlickDebug] Note SKIP  noteId={note.NoteId}" +
                                $"  laneId={note.LaneId}  touchId={touch.TouchId}" +
                                $"  reason=not_armed" +
                                $"  eventTime={evt.EventTimeMs:F0}ms");
                        }
                        continue;
                    }

                    // Compute thetaDeg for arbitration (angular distance to lane center).
                    // If the event position is outside the lane (touch left after arming),
                    // fall back to the lane center angle so AngularDistanceDeg = 0.
                    bool insideAtEvent = IsInsideLane(evtHitLocal, note.LaneId,
                        laneGeometries, arenaGeometries, out float thetaDeg);
                    if (!insideAtEvent)
                    {
                        if (DebugLogFlick && _flickDebugLogged.Add(
                            $"EOUT:{note.NoteId}:{touch.TouchId}:{evt.EventTimeMs:F0}"))
                        {
                            Debug.Log(
                                $"[FlickDebug] Note  noteId={note.NoteId}" +
                                $"  laneId={note.LaneId}  touchId={touch.TouchId}" +
                                $"  outside_lane_ignored (armed)" +
                                $"  evtHitLocal=({evtHitLocal.x:F3},{evtHitLocal.y:F3})" +
                                $"  eventTime={evt.EventTimeMs:F0}ms");
                        }
                        // Use lane center as fallback so angular-distance tie-break = 0.
                        if (laneGeometries.TryGetValue(note.LaneId, out LaneGeometry fallbackGeo))
                        {
                            thetaDeg = fallbackGeo.CenterDeg;
                        }
                    }

                    // Direction check (spec §7.3.1).
                    if (!string.IsNullOrEmpty(note.FlickDirection))
                    {
                        if (!laneGeometries.TryGetValue(note.LaneId, out LaneGeometry laneGeo))
                        {
                            continue;
                        }

                        if (!IsFlickDirectionMatch(evt.DispNorm, note.FlickDirection,
                            laneGeo.CenterDeg))
                        {
                            if (DebugLogFlick && _flickDebugLogged.Add(
                                $"ESKIP:{note.NoteId}:{touch.TouchId}:{evt.EventTimeMs:F0}"))
                            {
                                string det = DebugDisplacementDir(evt.DispNorm);
                                Debug.Log(
                                    $"[FlickDebug] Note SKIP  noteId={note.NoteId}" +
                                    $"  laneId={note.LaneId}  touchId={touch.TouchId}" +
                                    $"  reason=direction_mismatch" +
                                    $"  required={note.FlickDirection}  detected={det}" +
                                    $"  disp=({evt.DispNorm.x:F4},{evt.DispNorm.y:F4})" +
                                    $"  laneCenterDeg={laneGeo.CenterDeg:F1}" +
                                    $"  eventTime={evt.EventTimeMs:F0}ms");
                            }
                            continue;
                        }
                    }

                    _flickCandidates.Add(new NoteCandidate
                    {
                        Note               = note,
                        AbsTimingErrorMs   = Math.Abs(timingError),
                        AngularDistanceDeg = GetAngularDistance(thetaDeg, note.LaneId, laneGeometries),
                        LanePriority       = GetLanePriority(note.LaneId)
                    });
                }

                if (_flickCandidates.Count == 0)
                {
                    if (DebugLogFlick && _flickDebugLogged.Add(
                        $"EMISS:{touch.TouchId}:{evt.EventTimeMs:F0}"))
                    {
                        Debug.Log(
                            $"[FlickDebug] Event no-match  touchId={touch.TouchId}" +
                            $"  t={evt.EventTimeMs:F0}ms" +
                            $"  dist={evt.DistanceNorm:F4}  vel={evt.VelocityNormPerSec:F3}/s" +
                            $"  disp=({evt.DispNorm.x:F4},{evt.DispNorm.y:F4})");
                    }
                    continue; // No matching note; try next queued event.
                }

                RuntimeNote best      = Arbitrate(_flickCandidates);
                double      bestError = evt.EventTimeMs - best.TimeMs;

                // Tier selection (spec §7.3 + FlickPerfectWindowCoversGreatWindow toggle):
                //   false (default): Perfect / Great / Miss by timing (same evaluation as tap).
                //   true: Perfect covers full GreatWindowMs; Great suppressed.
                var (tier, isPlus) = _windows.Evaluate(
                    bestError, PlayerSettingsStore.FlickPerfectWindowCoversGreatWindow);

                best.State = NoteState.Hit;
                record     = MakeRecord(best, tier, isPlus, bestError);

                // Clean up arm entry for the judged note (both toggle modes).
                _flickArmedSet.Remove($"ARM:{best.NoteId}:{touch.TouchId}");

                LogFlickJudgement(record, evt);
                return true; // One note judged per call; caller loops to process more events.
            }

            return false; // No events or no matching candidates.
        }

        // -------------------------------------------------------------------
        // Hold start binding (spec §7.5)
        // -------------------------------------------------------------------

        /// <summary>
        /// Tries to bind a new touch to an Active Hold note.
        /// Hold start requires TouchBegin inside lane within GreatWindowMs of startTimeMs.
        /// Returns true if a hold was bound (touch.BoundHold is updated externally by caller).
        /// </summary>
        public bool TryBindHold(
            TouchSnapshot              touch,
            IReadOnlyList<RuntimeNote> activeNotes,
            double                     effectiveChartTimeMs,
            IDictionary<string, LaneGeometry>  laneGeometries,
            IDictionary<string, ArenaGeometry> arenaGeometries,
            out RuntimeNote            boundHold,
            out JudgementRecord        record)
        {
            boundHold = null;
            record    = default;

            if (!touch.IsNew) { return false; } // Hold bind requires TouchBegin.

            _candidates.Clear();

            foreach (RuntimeNote note in activeNotes)
            {
                if (note.Type != NoteType.Hold)             { continue; }
                if (note.HoldBind != HoldBindState.Unbound) { continue; }
                if (!note.Judging)                          { continue; }

                double timingError = effectiveChartTimeMs - note.StartTimeMs;

                if (!_windows.IsHittable(timingError)) { continue; }

                if (!IsInsideLane(touch.HitLocalXY, note.LaneId, laneGeometries, arenaGeometries,
                    out float thetaDeg)) { continue; }

                _candidates.Add(new NoteCandidate
                {
                    Note               = note,
                    AbsTimingErrorMs   = Math.Abs(timingError),
                    AngularDistanceDeg = GetAngularDistance(thetaDeg, note.LaneId, laneGeometries),
                    LanePriority       = GetLanePriority(note.LaneId)
                });
            }

            if (_candidates.Count == 0) { return false; }

            RuntimeNote best = Arbitrate(_candidates);
            double bestError = effectiveChartTimeMs - best.StartTimeMs;
            var (tier, isPlus) = _windows.Evaluate(
                bestError, PlayerSettingsStore.PerfectWindowCoversGreatWindow);

            best.HoldBind    = HoldBindState.Bound;
            best.BoundTouchId = touch.TouchId;
            // Hold note stays Active; its State remains Active until fully resolved.

            boundHold = best;
            record = MakeRecord(best, tier, isPlus, bestError);

            LogJudgement(record);
            return true;
        }

        // -------------------------------------------------------------------
        // Arbitration (spec §7.6)
        // -------------------------------------------------------------------

        // Selects the best candidate using the four-criterion tie-break order.
        private static RuntimeNote Arbitrate(List<NoteCandidate> candidates)
        {
            if (candidates.Count == 1) { return candidates[0].Note; }

            // Sort ascending by: absTimingError, angularDist, -priority, noteIndex.
            candidates.Sort((a, b) =>
            {
                // Criterion 1: smallest abs timing error.
                int c = a.AbsTimingErrorMs.CompareTo(b.AbsTimingErrorMs);
                if (c != 0) { return c; }

                // Criterion 2: smallest angular distance to lane centerline.
                c = a.AngularDistanceDeg.CompareTo(b.AngularDistanceDeg);
                if (c != 0) { return c; }

                // Criterion 3: higher lane priority wins (descending).
                c = b.LanePriority.CompareTo(a.LanePriority);
                if (c != 0) { return c; }

                // Criterion 4: lower noteIndex (earlier in file order) wins.
                return a.Note.NoteIndex.CompareTo(b.Note.NoteIndex);
            });

            return candidates[0].Note;
        }

        // -------------------------------------------------------------------
        // Helper: lane membership test
        // -------------------------------------------------------------------

        private bool IsInsideLane(
            Vector2                           hitLocal,
            string                            laneId,
            IDictionary<string, LaneGeometry>  laneGeometries,
            IDictionary<string, ArenaGeometry> arenaGeometries,
            out float                         thetaDeg)
        {
            thetaDeg = 0f;

            if (!laneGeometries.TryGetValue(laneId, out LaneGeometry laneGeo)) { return false; }

            // Resolve which arena owns this lane using the construction-time map (F2 fix).
            // Previously this always picked the first arena in the dict, breaking multi-arena charts.
            if (!_laneToArena.TryGetValue(laneId, out string arenaId)) { return false; }
            if (!arenaGeometries.TryGetValue(arenaId, out ArenaGeometry arenaGeo)) { return false; }

            if (!ArenaHitTester.IsInsideArenaBand(hitLocal, arenaGeo, _playfieldTransform,
                out thetaDeg)) { return false; }

            return ArenaHitTester.IsInsideLane(thetaDeg, laneGeo);
        }

        // -------------------------------------------------------------------
        // Helper: angular distance for criterion 2
        // -------------------------------------------------------------------

        private static float GetAngularDistance(
            float thetaDeg,
            string laneId,
            IDictionary<string, LaneGeometry> laneGeometries)
        {
            if (!laneGeometries.TryGetValue(laneId, out LaneGeometry laneGeo)) { return 0f; }

            return ArenaHitTester.AngularDistanceToLaneCenter(thetaDeg, laneGeo);
        }

        // -------------------------------------------------------------------
        // Helper: lane priority for criterion 3
        // -------------------------------------------------------------------

        private int GetLanePriority(string laneId)
        {
            return _laneMap.TryGetValue(laneId, out RuntimeLane lane) ? lane.Priority : 0;
        }

        // -------------------------------------------------------------------
        // Helper: build record
        // -------------------------------------------------------------------

        private static JudgementRecord MakeRecord(
            RuntimeNote   note,
            JudgementTier tier,
            bool          isPerfectPlus,
            double        timingErrorMs)
        {
            return new JudgementRecord
            {
                Note          = note,
                Tier          = tier,
                IsPerfectPlus = isPerfectPlus,
                TimingErrorMs = timingErrorMs
            };
        }

        // -------------------------------------------------------------------
        // Helper: flick direction match (spec §7.3.1)
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns true if <paramref name="displacementNorm"/> is within 45° of the
        /// expected lane-relative direction for the flick note.
        ///
        /// Lane-relative basis vectors at lane center angle θ (player-facing-inward frame):
        ///   U (radial-in)   = (-cos θ, -sin θ)   — inward toward arena center
        ///   D (radial-out)  = ( cos θ,  sin θ)   — outward from arena center
        ///   L (CW  tangent) = ( sin θ, -cos θ)   — clockwise (left when facing inward)
        ///   R (CCW tangent) = (-sin θ,  cos θ)   — counter-clockwise (right when facing inward)
        ///
        /// A match requires dot(normalised displacement, expected basis) >= cos(45°) ≈ 0.707.
        /// </summary>
        private static bool IsFlickDirectionMatch(
            Vector2 displacementNorm,
            string  requiredDir,
            float   laneCenterDeg)
        {
            if (displacementNorm.magnitude < 1e-4f) { return false; }

            float rad  = laneCenterDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(rad);
            float sinA = Mathf.Sin(rad);

            // Lane-relative basis in normalized playfield XY (player-facing-inward frame).
            Vector2 expected;
            switch (requiredDir)
            {
                case FlickDirection.Up:    expected = new Vector2(-cosA, -sinA); break; // radial-in
                case FlickDirection.Down:  expected = new Vector2( cosA,  sinA); break; // radial-out
                case FlickDirection.Left:  expected = new Vector2( sinA, -cosA); break; // CW tangent
                case FlickDirection.Right: expected = new Vector2(-sinA,  cosA); break; // CCW tangent
                default:                   return true;                                  // no constraint
            }

            // Gesture must point within 45° of the expected basis vector.
            return Vector2.Dot(displacementNorm.normalized, expected) >= 0.707f;
        }

        // -------------------------------------------------------------------
        // Debug helper: flick direction → playfield XY basis vector
        // MUST stay in sync with IsFlickDirectionMatch — same mapping.
        // -------------------------------------------------------------------

        /// <summary>
        /// DEBUG: Returns the expected normalized playfield-XY direction for the given
        /// chart direction string and lane center angle (player-facing-inward frame).
        /// Returns Vector2.zero for unknown/empty directions.
        ///
        /// IMPORTANT: mapping MUST stay in sync with <see cref="IsFlickDirectionMatch"/>.
        /// </summary>
        public static Vector2 DebugFlickExpectedDir(string flickDir, float laneCenterDeg)
        {
            float rad  = laneCenterDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(rad);
            float sinA = Mathf.Sin(rad);

            switch (flickDir)
            {
                case FlickDirection.Up:    return new Vector2(-cosA, -sinA); // radial-in
                case FlickDirection.Down:  return new Vector2( cosA,  sinA); // radial-out
                case FlickDirection.Left:  return new Vector2( sinA, -cosA); // CW tangent
                case FlickDirection.Right: return new Vector2(-sinA,  cosA); // CCW tangent
                default:                   return Vector2.zero;
            }
        }

        // -------------------------------------------------------------------
        // Headless verification log (spec task requirement: "log judgements for headless verification")
        // -------------------------------------------------------------------

        private static void LogJudgement(JudgementRecord r)
        {
            string plusTag = r.IsPerfectPlus ? "+" : "";
            Debug.Log(
                $"[Judgement] {r.Note.Type} id={r.Note.NoteId} " +
                $"tier={r.Tier}{plusTag} " +
                $"timingErr={r.TimingErrorMs:F1}ms");
        }

        // Flick-specific log with event diagnostics (spec task: "log judgements for headless verification").
        private static void LogFlickJudgement(JudgementRecord r, FlickEvent evt)
        {
            string plusTag     = r.IsPerfectPlus ? "+" : "";
            string dirDetected = DebugDisplacementDir(evt.DispNorm);
            string dirRequired = string.IsNullOrEmpty(r.Note.FlickDirection)
                ? "any"
                : r.Note.FlickDirection;

            Debug.Log(
                $"[Judgement] Flick id={r.Note.NoteId} " +
                $"tier={r.Tier}{plusTag} " +
                $"timingErr={r.TimingErrorMs:F1}ms " +
                $"dist={evt.DistanceNorm:F4}norm " +
                $"vel={evt.VelocityNormPerSec:F2}norm/s " +
                $"eventTime={evt.EventTimeMs:F0}ms " +
                $"dir={dirDetected} required={dirRequired}");
        }

        // Returns a human-readable axis-dominant direction string from a displacement vector.
        // Used only in debug logs; does not affect gameplay logic.
        private static string DebugDisplacementDir(Vector2 disp)
        {
            if (disp.magnitude < 1e-4f) { return "None"; }
            if (Mathf.Abs(disp.x) >= Mathf.Abs(disp.y))
            {
                return disp.x >= 0f ? "Right" : "Left";
            }
            return disp.y >= 0f ? "Up" : "Down";
        }
    }
}
