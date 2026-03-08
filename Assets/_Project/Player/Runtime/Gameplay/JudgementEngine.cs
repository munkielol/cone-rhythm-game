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
//   Flick — arming + gesture threshold check (spec §7.3 / §7.3.1)
//   Catch — any touch inside lane within [timeMs-Great, timeMs+Great] (spec §7.4.1)
//   Hold  — TouchBegin inside lane within GreatWindowMs of startTimeMs (spec §7.5)
//
// Hold ticks and Catch: Perfect-or-Miss only (spec §4.4).
// Flick: Perfect-or-Miss only (spec §7.3).
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
            var (tier, isPlus) = _windows.Evaluate(bestError);

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
            var (tier, isPlus) = _windows.Evaluate(bestError);

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
    }
}
