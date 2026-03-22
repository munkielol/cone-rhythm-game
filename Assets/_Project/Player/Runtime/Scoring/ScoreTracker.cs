// ScoreTracker.cs
// Accumulates score, combo, and judgement counts for one play session.
//
// Scoring rules (v0, spec §4.4 / §4.5 — per-note-or-tick point scheme):
//   Tap / Flick / Catch:
//     Perfect = 1000 pts  |  Great = 700 pts  |  Miss = 0 pts
//     Combo increments on Perfect/Great; resets to 0 on Miss.
//
//   Hold — tick-based scoring (spec §4.4 / §4.5):
//     Each baked tick is a judged event via OnHoldTick:
//       TickPerfect = 1000 pts, combo++
//       TickMiss    = 0 pts,    combo reset, hold fails immediately (no spam)
//     Hold START:
//       The hold-bind event on OnJudgement is IGNORED — it does not affect score/combo.
//     Hold FINAL RESOLVE (OnHoldResolved):
//       Non-scoring EXCEPT for Unbound holds (player never pressed the hold at all),
//       which are treated as one Miss to break the combo exactly once.
//     This design prevents double-counting: ticks carry all the scoring weight.
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
        /// <summary>Accumulated point score (sum of per-note/per-tick awards).</summary>
        public long TotalScore;

        /// <summary>Combo count at the moment results were captured.</summary>
        public int CurrentCombo;

        /// <summary>Highest unbroken combo reached during the session.</summary>
        public int MaxCombo;

        /// <summary>
        /// Notes judged Perfect this session; includes hold ticks that were Perfect.
        /// </summary>
        public int PerfectCount;

        /// <summary>Notes judged Great this session (taps/flicks/catches only — ticks are P/M).</summary>
        public int GreatCount;

        /// <summary>
        /// Notes/ticks judged Miss this session; includes hold tick misses and hold start misses.
        /// </summary>
        public int MissCount;

        /// <summary>Total judged events (notes + hold ticks) this session.</summary>
        public int TotalJudgedCount;

        /// <summary>Hold ticks judged Perfect this session.</summary>
        public int HoldTickPerfectCount;

        /// <summary>Hold tick misses (includes exactly-one-miss-per-failed-hold rule).</summary>
        public int HoldTickMissCount;
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
    /// <para>Scoring rules: spec §4.4 / §4.5.</para>
    /// </summary>
    public class ScoreTracker
    {
        // -------------------------------------------------------------------
        // Scoring constants (locked, spec §4.5)
        // -------------------------------------------------------------------

        /// <summary>Points for a Perfect judgement — also applied to hold tick Perfects (spec §4.5).</summary>
        public const int PointsPerfect = 1000;

        /// <summary>Points for a Great judgement (taps/flicks/catches only; ticks are P/M).</summary>
        public const int PointsGreat = 700;

        /// <summary>Points for a Miss judgement (0).</summary>
        public const int PointsMiss = 0;

        // -------------------------------------------------------------------
        // Public read-only state (safe to read from UI / results screen)
        // -------------------------------------------------------------------

        /// <summary>Current unbroken combo. Increments on Perfect/Great; resets to 0 on Miss.</summary>
        public int CurrentCombo { get; private set; }

        /// <summary>Highest combo achieved so far this session.</summary>
        public int MaxCombo { get; private set; }

        /// <summary>
        /// Events judged Perfect this session (includes hold tick Perfects).
        /// </summary>
        public int PerfectCount { get; private set; }

        /// <summary>Events judged Great this session (taps/flicks/catches only).</summary>
        public int GreatCount { get; private set; }

        /// <summary>
        /// Events judged Miss this session (includes hold start misses and tick misses).
        /// </summary>
        public int MissCount { get; private set; }

        /// <summary>Total judged events (notes + hold ticks); denominator for accuracy.</summary>
        public int TotalJudgedCount { get; private set; }

        /// <summary>Accumulated point score; sum of per-note and per-tick awards.</summary>
        public long TotalScore { get; private set; }

        /// <summary>Hold ticks judged Perfect (subset of PerfectCount).</summary>
        public int HoldTickPerfectCount { get; private set; }

        /// <summary>
        /// Hold tick misses fired this session.
        /// One is emitted per failed hold (first bad tick or early release),
        /// so this equals the number of hold failures. (spec §4.5 — "no spam")
        /// </summary>
        public int HoldTickMissCount { get; private set; }

        // -------------------------------------------------------------------
        // Debug toggle
        // -------------------------------------------------------------------

        /// <summary>
        /// When true, logs each individual judgement result with updated score/combo.
        /// Disabled by default. Toggle via PlayerAppController.debugLogScoreEachJudgement.
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
            _app.OnHoldTick     += HandleHoldTick;
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
            _app.OnHoldTick     -= HandleHoldTick;
            _app = null;
        }

        // -------------------------------------------------------------------
        // Event handlers
        // -------------------------------------------------------------------

        // Receives tap/catch/flick hits and sweep-misses of non-hold notes.
        // Also receives hold-bind events (TryBindHold fires OnJudgement with a
        // hold note) — those are filtered out here; hold scoring uses OnHoldTick.
        private void HandleJudgement(JudgementRecord r)
        {
            // Hold-bind events (fired at hold START) must not be scored here.
            // All hold-related score/combo is driven by HandleHoldTick and
            // HandleHoldResolved (spec §4.5 — prevents double-counting).
            if (r.Note.Type == NoteType.Hold) { return; }

            ApplyJudgement(r.Tier, r.Note.Type, isTick: false);
        }

        // Receives each baked tick result (Perfect or Miss) from an active hold.
        // Fired by PlayerAppController.OnHoldTick.
        //
        // Exactly one Miss is emitted when a hold fails (first bad tick or early
        // release), then no further tick events are emitted for that hold —
        // spec §4.5 "no spam" guarantee.
        private void HandleHoldTick(JudgementRecord r)
        {
            // Track tick-specific sub-counts for the song summary.
            if (r.Tier == JudgementTier.Perfect)
            {
                HoldTickPerfectCount++;
            }
            else
            {
                HoldTickMissCount++;
            }

            ApplyJudgement(r.Tier, r.Note.Type, isTick: true);
        }

        // Receives hold final-resolve events from PlayerAppController.OnHoldResolved.
        //
        // This event is now LIFECYCLE-ONLY for most cases.  The only case that
        // still drives scoring is HoldBind == Unbound (player never pressed the
        // hold at all): that counts as one Miss to break the combo exactly once
        // (spec §4.5 — "hold start miss yields one combo break").
        //
        // All other states (Finished = early release / tick failure, Hit = complete)
        // are non-scoring here because ticks already accumulated the score.
        private void HandleHoldResolved(JudgementRecord r)
        {
            // Unbound → player never started the hold; score as one Miss.
            if (r.Note.HoldBind == HoldBindState.Unbound)
            {
                ApplyJudgement(JudgementTier.Miss, r.Note.Type, isTick: false);
                return;
            }

            // Finished (early release or tick failure) or Hit (natural completion):
            // ticks already handled all scoring and combo — nothing to do here.
        }

        // -------------------------------------------------------------------
        // Core scoring — allocation-free, no LINQ
        // -------------------------------------------------------------------

        // isTick: true when this event comes from a hold tick (affects debug label).
        private void ApplyJudgement(JudgementTier tier, string noteType, bool isTick)
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
                    CurrentCombo = 0;  // Combo resets to 0 on any Miss (spec §4.5).
                    points = PointsMiss;
                    break;
            }

            TotalScore += points;

            if (DebugLogScoreEachJudgement)
            {
                string label = isTick ? $"{noteType}[tick]" : noteType;
                Debug.Log(
                    $"[Score] {label,-12} {tier,-7} +{points,4}pts | " +
                    $"combo={CurrentCombo,4}  maxCombo={MaxCombo,4}  total={TotalScore,8} | " +
                    $"P={PerfectCount} G={GreatCount} M={MissCount} " +
                    $"tP={HoldTickPerfectCount} tM={HoldTickMissCount}");
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
                TotalScore            = TotalScore,
                CurrentCombo          = CurrentCombo,
                MaxCombo              = MaxCombo,
                PerfectCount          = PerfectCount,
                GreatCount            = GreatCount,
                MissCount             = MissCount,
                TotalJudgedCount      = TotalJudgedCount,
                HoldTickPerfectCount  = HoldTickPerfectCount,
                HoldTickMissCount     = HoldTickMissCount,
            };
        }

        /// <summary>
        /// Logs a one-line end-of-song summary to the Unity Console.
        /// Called automatically by PlayerAppController when song ends.
        /// Can also be called manually from a results screen.
        ///
        /// Accuracy formula (spec §4.5):
        ///   earned = Perfect * 1000 + Great * 700
        ///   max    = TotalJudged * 1000
        ///   pct    = earned / max * 100
        /// Both hold ticks and note judgements contribute to Perfect/Great/Miss/Total.
        /// </summary>
        public void LogSummary()
        {
            // Weighted accuracy: Perfect = full weight, Great = 70 %, Miss = 0 %.
            float accuracy = TotalJudgedCount > 0
                ? (float)(PerfectCount * PointsPerfect + GreatCount * PointsGreat)
                  / (float)(TotalJudgedCount * PointsPerfect)
                  * 100f
                : 0f;

            Debug.Log(
                $"[Score] ===== Song Complete ===== " +
                $"Score={TotalScore}  MaxCombo={MaxCombo}  " +
                $"Perfect={PerfectCount}  Great={GreatCount}  Miss={MissCount}  " +
                $"HoldTicks(P={HoldTickPerfectCount}/M={HoldTickMissCount})  " +
                $"Accuracy={accuracy:F2}%");
        }
    }
}
