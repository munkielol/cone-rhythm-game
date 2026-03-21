// ScoreTracker.cs
// Accumulates score, combo, and judgement counts for one play session.
//
// Scoring rules (v0, spec §4.5 — point-per-note scheme):
//   Perfect = 1000 pts | Great = 700 pts | Miss = 0 pts
//   Combo increments on Perfect/Great; resets to 0 on Miss.
//
// Hold scoring (spec §4.5):
//   - Hold-bind events (fired on OnJudgement when a player first presses a hold) are IGNORED.
//   - A hold is scored exactly once, via OnHoldResolved:
//       Hit  → JudgementTier.Perfect  (combo++, +1000 pts)
//       Miss → JudgementTier.Miss     (combo=0, +0 pts)
//   - Individual hold tick results are not scored (v0 simplification).
//
// Wire-up (no prefab / scene / YAML edit required):
//   ScoreTracker is a plain C# class (not a MonoBehaviour).
//   PlayerAppController creates it in Start() and calls Initialize().
//
// Thread safety: runs on Unity main thread only; no locking needed.

using RhythmicFlow.Shared;
using UnityEngine;

namespace RhythmicFlow.Player
{
    // -----------------------------------------------------------------------
    // SongResults — read-only snapshot produced at song end
    // -----------------------------------------------------------------------

    /// <summary>
    /// Immutable snapshot of all scoring state at song completion.
    /// Passed to OnSongFinished listeners and logged to the Console.
    /// </summary>
    public struct SongResults
    {
        /// <summary>Accumulated point score (sum of per-note awards).</summary>
        public long TotalScore;

        /// <summary>Combo count at the moment results were captured.</summary>
        public int CurrentCombo;

        /// <summary>Highest unbroken combo reached during the session.</summary>
        public int MaxCombo;

        /// <summary>Number of notes (including holds) judged Perfect.</summary>
        public int PerfectCount;

        /// <summary>Number of notes judged Great.</summary>
        public int GreatCount;

        /// <summary>Number of notes judged Miss (swept or hold-missed).</summary>
        public int MissCount;

        /// <summary>Total notes that have received any judgement this session.</summary>
        public int TotalJudgedCount;
    }

    // -----------------------------------------------------------------------
    // ScoreTracker
    // -----------------------------------------------------------------------

    /// <summary>
    /// Listens to PlayerAppController judgement events and maintains
    /// score, combo, and count statistics for one play session.
    ///
    /// <para>Create one instance per play session in PlayerAppController.Start(),
    /// call Initialize(), then read the public properties from any UI layer.</para>
    ///
    /// <para>Scoring rules: spec §4.5.</para>
    /// </summary>
    public class ScoreTracker
    {
        // -------------------------------------------------------------------
        // Scoring constants (locked, spec §4.5)
        // -------------------------------------------------------------------

        /// <summary>Points awarded for a Perfect judgement (spec §4.5).</summary>
        public const int PointsPerfect = 1000;

        /// <summary>Points awarded for a Great judgement (spec §4.5).</summary>
        public const int PointsGreat = 700;

        /// <summary>Points awarded for a Miss judgement (spec §4.5).</summary>
        public const int PointsMiss = 0;

        // -------------------------------------------------------------------
        // Public read-only state (safe to read from UI / results screen)
        // -------------------------------------------------------------------

        /// <summary>Current unbroken combo. Increments on Perfect/Great; resets to 0 on Miss.</summary>
        public int CurrentCombo { get; private set; }

        /// <summary>Highest combo achieved so far this session.</summary>
        public int MaxCombo { get; private set; }

        /// <summary>Notes (including holds) judged Perfect this session.</summary>
        public int PerfectCount { get; private set; }

        /// <summary>Notes judged Great this session.</summary>
        public int GreatCount { get; private set; }

        /// <summary>Notes judged Miss (sweep-missed or hold-missed) this session.</summary>
        public int MissCount { get; private set; }

        /// <summary>Total notes that have received any judgement (all tiers).</summary>
        public int TotalJudgedCount { get; private set; }

        /// <summary>Accumulated point score; sum of per-note awards.</summary>
        public long TotalScore { get; private set; }

        // -------------------------------------------------------------------
        // Debug toggle
        // -------------------------------------------------------------------

        /// <summary>
        /// When true, logs each individual judgement result with the updated
        /// score/combo to the Unity Console. Disabled by default to avoid
        /// per-hit log spam during normal play. Toggle in code or Inspector
        /// via PlayerAppController.debugLogScoreEachJudgement.
        /// </summary>
        public bool DebugLogScoreEachJudgement = false;

        // -------------------------------------------------------------------
        // Private — back-reference kept for safe unsubscription
        // -------------------------------------------------------------------

        private PlayerAppController _app;

        // -------------------------------------------------------------------
        // Initialization / teardown
        // -------------------------------------------------------------------

        /// <summary>
        /// Subscribes to PlayerAppController judgement events.
        /// Call once in PlayerAppController.Start() after the controller is ready.
        /// </summary>
        public void Initialize(PlayerAppController app)
        {
            _app = app;
            _app.OnJudgement    += HandleJudgement;
            _app.OnHoldResolved += HandleHoldResolved;
        }

        /// <summary>
        /// Unsubscribes all event handlers and releases the app reference.
        /// Call from PlayerAppController.OnDestroy() to avoid stale subscriptions.
        /// </summary>
        public void Dispose()
        {
            if (_app == null) { return; }
            _app.OnJudgement    -= HandleJudgement;
            _app.OnHoldResolved -= HandleHoldResolved;
            _app = null;
        }

        // -------------------------------------------------------------------
        // Event handlers
        // -------------------------------------------------------------------

        // Receives all judged-note records: Tap hits, Catch hits, Flick hits,
        // sweep-misses of non-hold notes, and (ignored) hold-bind events.
        private void HandleJudgement(JudgementRecord r)
        {
            // Hold-bind events are emitted on OnJudgement when the player first
            // presses a hold note (TryBindHold). We must NOT score them here;
            // hold scoring happens in HandleHoldResolved when the hold fully
            // resolves at endTimeMs (spec §4.5 — "score on final resolve").
            if (r.Note.Type == NoteType.Hold) { return; }

            ApplyJudgement(r.Tier, r.Note.Type);
        }

        // Receives hold-resolve events from PlayerAppController.OnHoldResolved:
        //   Tier.Perfect — hold completed successfully (State became Hit)
        //   Tier.Miss    — hold never bound, or released early (State became Missed)
        private void HandleHoldResolved(JudgementRecord r)
        {
            ApplyJudgement(r.Tier, r.Note.Type);
        }

        // -------------------------------------------------------------------
        // Core scoring — allocation-free, no LINQ
        // -------------------------------------------------------------------

        private void ApplyJudgement(JudgementTier tier, string noteType)
        {
            TotalJudgedCount++;

            int points;

            switch (tier)
            {
                case JudgementTier.Perfect:
                    PerfectCount++;
                    CurrentCombo++;
                    // Track the highest combo reached this session.
                    if (CurrentCombo > MaxCombo) { MaxCombo = CurrentCombo; }
                    points = PointsPerfect;
                    break;

                case JudgementTier.Great:
                    GreatCount++;
                    CurrentCombo++;
                    if (CurrentCombo > MaxCombo) { MaxCombo = CurrentCombo; }
                    points = PointsGreat;
                    break;

                default: // Miss
                    MissCount++;
                    CurrentCombo = 0;  // Combo always resets to 0 on Miss (spec §4.5).
                    points = PointsMiss;
                    break;
            }

            TotalScore += points;

            if (DebugLogScoreEachJudgement)
            {
                Debug.Log(
                    $"[Score] {noteType,-5} {tier,-7} +{points,4}pts | " +
                    $"combo={CurrentCombo,4}  maxCombo={MaxCombo,4}  total={TotalScore,8} | " +
                    $"P={PerfectCount} G={GreatCount} M={MissCount}");
            }
        }

        // -------------------------------------------------------------------
        // Summary
        // -------------------------------------------------------------------

        /// <summary>
        /// Builds and returns an immutable snapshot of the current scoring state.
        /// Called by PlayerAppController at song end; also useful for a results screen.
        /// </summary>
        public SongResults BuildResults()
        {
            return new SongResults
            {
                TotalScore       = TotalScore,
                CurrentCombo     = CurrentCombo,
                MaxCombo         = MaxCombo,
                PerfectCount     = PerfectCount,
                GreatCount       = GreatCount,
                MissCount        = MissCount,
                TotalJudgedCount = TotalJudgedCount,
            };
        }

        /// <summary>
        /// Logs a one-line end-of-song summary to the Unity Console.
        /// Called automatically by PlayerAppController when song ends.
        /// Can also be called manually from a results screen.
        ///
        /// Accuracy formula:
        ///   earned = Perfect * 1000 + Great * 700
        ///   max    = TotalJudged * 1000
        ///   pct    = earned / max * 100
        /// </summary>
        public void LogSummary()
        {
            // Weighted accuracy: Perfect counts full, Great counts 70 %, Miss 0 %.
            float accuracy = TotalJudgedCount > 0
                ? (float)(PerfectCount * PointsPerfect + GreatCount * PointsGreat)
                  / (float)(TotalJudgedCount * PointsPerfect)
                  * 100f
                : 0f;

            Debug.Log(
                $"[Score] ===== Song Complete ===== " +
                $"Score={TotalScore}  MaxCombo={MaxCombo}  " +
                $"Perfect={PerfectCount}  Great={GreatCount}  Miss={MissCount}  " +
                $"Accuracy={accuracy:F2}%");
        }
    }
}
