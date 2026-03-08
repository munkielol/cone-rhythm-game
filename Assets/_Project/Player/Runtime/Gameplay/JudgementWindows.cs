// JudgementWindows.cs
// Defines the two gameplay modes and their timing windows.
//
// Spec §4.1 (v0 locked values):
//   Standard:
//     PerfectWindowMs   = 30
//     GreatWindowMs     = 90
//     PerfectPlusWindowMs = 15  (display-only, no score change)
//
//   Challenger:
//     PerfectWindowMs   = 22
//     GreatWindowMs     = 60
//     PerfectPlusWindowMs = 10  (display-only)
//
// Spec §4.2 — Judgement tiers: Perfect, Great, Miss
// Spec §4.3 — Perfect+ is a sub-window inside Perfect for display/stats only
// Spec §4.4 — Hold ticks and Catch notes: Perfect-or-Miss only
// Spec §7.3 — Flick: Perfect-or-Miss only

namespace RhythmicFlow.Player
{
    // -------------------------------------------------------------------
    // Gameplay mode
    // -------------------------------------------------------------------

    /// <summary>
    /// The two judgement modes available in v0 (spec §4.1 / §8.2).
    /// </summary>
    public enum GameplayMode
    {
        Standard,
        Challenger
    }

    // -------------------------------------------------------------------
    // Judgement result
    // -------------------------------------------------------------------

    /// <summary>
    /// Judgement tier result for a note (spec §4.2).
    /// </summary>
    public enum JudgementTier
    {
        /// <summary>Note was not in any hit window (passed without input).</summary>
        Miss,
        /// <summary>Hit within GreatWindowMs but outside PerfectWindowMs.</summary>
        Great,
        /// <summary>Hit within PerfectWindowMs.</summary>
        Perfect,
    }

    // -------------------------------------------------------------------
    // Judgement record
    // -------------------------------------------------------------------

    /// <summary>
    /// Full result of one note judgement event.
    /// </summary>
    public struct JudgementRecord
    {
        /// <summary>The note that was judged.</summary>
        public RuntimeNote Note;

        /// <summary>The tier result (Perfect, Great, Miss).</summary>
        public JudgementTier Tier;

        /// <summary>
        /// True when Tier == Perfect AND the hit was within PerfectPlusWindowMs.
        /// Display-only; no score change (spec §4.3).
        /// </summary>
        public bool IsPerfectPlus;

        /// <summary>
        /// Signed timing error in ms: positive = late, negative = early (spec §3.3 sign convention).
        /// effectiveChartTimeMs - noteTimeMs
        /// </summary>
        public double TimingErrorMs;
    }

    // -------------------------------------------------------------------
    // JudgementWindows
    // -------------------------------------------------------------------

    /// <summary>
    /// Provides the hit windows for a given gameplay mode and evaluates
    /// whether a timing error qualifies as Perfect/Great/Miss.
    /// Spec §4.1 — values locked for v0.
    /// </summary>
    public struct JudgementWindows
    {
        // Spec §4.1
        public readonly int PerfectWindowMs;
        public readonly int GreatWindowMs;
        public readonly int PerfectPlusWindowMs;

        // -------------------------------------------------------------------
        // Factory
        // -------------------------------------------------------------------

        public static JudgementWindows ForMode(GameplayMode mode)
        {
            switch (mode)
            {
                case GameplayMode.Challenger:
                    return new JudgementWindows(
                        perfectWindowMs:    22,
                        greatWindowMs:      60,
                        perfectPlusWindowMs: 10);

                default: // Standard
                    return new JudgementWindows(
                        perfectWindowMs:    30,
                        greatWindowMs:      90,
                        perfectPlusWindowMs: 15);
            }
        }

        private JudgementWindows(int perfectWindowMs, int greatWindowMs, int perfectPlusWindowMs)
        {
            PerfectWindowMs     = perfectWindowMs;
            GreatWindowMs       = greatWindowMs;
            PerfectPlusWindowMs = perfectPlusWindowMs;
        }

        // -------------------------------------------------------------------
        // Window evaluation
        // -------------------------------------------------------------------

        /// <summary>
        /// Evaluates a timing error (in ms) against the windows.
        /// Returns (tier, isPerfectPlus).
        ///
        /// Spec §4.1: |timingErrorMs| &lt;= PerfectWindowMs → Perfect
        ///             |timingErrorMs| &lt;= GreatWindowMs   → Great
        ///             otherwise                           → Miss
        /// </summary>
        public (JudgementTier tier, bool isPerfectPlus) Evaluate(double timingErrorMs)
        {
            double absError = System.Math.Abs(timingErrorMs);

            if (absError <= PerfectWindowMs)
            {
                bool isPlus = absError <= PerfectPlusWindowMs;
                return (JudgementTier.Perfect, isPlus);
            }

            if (absError <= GreatWindowMs)
            {
                return (JudgementTier.Great, false);
            }

            return (JudgementTier.Miss, false);
        }

        /// <summary>
        /// Returns true if |timingErrorMs| is within the Great window (and thus hittable).
        /// </summary>
        public bool IsHittable(double timingErrorMs)
        {
            return System.Math.Abs(timingErrorMs) <= GreatWindowMs;
        }
    }
}
