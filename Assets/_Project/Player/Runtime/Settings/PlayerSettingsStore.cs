// PlayerSettingsStore.cs
// Persists player-configurable settings across sessions using Unity PlayerPrefs.
//
// Settings exposed in v0 (spec §8.3):
//   UserOffsetMs         — timing offset applied on top of chart audioOffsetMs (spec §3.3)
//   PlayerSpeedMultiplier — visual note approach speed scale factor (spec §6.1)
//   FlickMinDistanceNorm  — minimum playfield-plane distance for flick gesture (spec §8.3)
//   FlickMinVelocityNormPerSec — minimum velocity for flick gesture (spec §8.3)
//   FlickMaxGestureTimeMs — maximum gesture duration for flick recognition (spec §8.3)
//
// Sign convention for UserOffsetMs (locked, spec §3.3):
//   Positive = judge LATER (notes occur later relative to audio).
//   effectiveChartTimeMs = songDspTimeMs + audioOffsetMs + UserOffsetMs

using UnityEngine;

namespace RhythmicFlow.Player
{
    public static class PlayerSettingsStore
    {
        // -------------------------------------------------------------------
        // PlayerPrefs keys
        // -------------------------------------------------------------------

        private const string KeyUserOffsetMs           = "rf.UserOffsetMs";
        private const string KeyPlayerSpeedMultiplier  = "rf.PlayerSpeedMultiplier";
        private const string KeyFlickMinDistNorm       = "rf.FlickMinDistNorm";
        private const string KeyFlickMinVelNormPerSec  = "rf.FlickMinVelNormPerSec";
        private const string KeyFlickMaxGestureTimeMs  = "rf.FlickMaxGestureTimeMs";

        // -------------------------------------------------------------------
        // Defaults (locked for v0, spec §8.3)
        // -------------------------------------------------------------------

        // Spec §3.3: default timing offset is 0.
        public const int   DefaultUserOffsetMs          = 0;

        // Spec §6.1: speed multiplier default (1.0 = normal speed).
        public const float DefaultPlayerSpeedMultiplier = 1.0f;

        // Spec §8.3: flick recognition defaults (locked for v0).
        public const float DefaultFlickMinDistanceNorm      = 0.03f;
        public const float DefaultFlickMinVelocityNormPerSec = 0.8f;
        public const int   DefaultFlickMaxGestureTimeMs     = 120;

        // -------------------------------------------------------------------
        // UserOffsetMs  (int, range -1000..+1000 ms in practice; UI clips to -200..+200)
        // -------------------------------------------------------------------

        // Spec §3.3: timing offset applied on top of chart audioOffsetMs.
        public static int UserOffsetMs
        {
            get => PlayerPrefs.GetInt(KeyUserOffsetMs, DefaultUserOffsetMs);
            set
            {
                PlayerPrefs.SetInt(KeyUserOffsetMs, value);
                PlayerPrefs.Save();
            }
        }

        // -------------------------------------------------------------------
        // PlayerSpeedMultiplier  (float, > 0)
        // -------------------------------------------------------------------

        // Spec §6.1: scales BaseApproachSpeed for visual note approach.
        public static float PlayerSpeedMultiplier
        {
            get => PlayerPrefs.GetFloat(KeyPlayerSpeedMultiplier, DefaultPlayerSpeedMultiplier);
            set
            {
                // Clamp to a safe positive range to prevent degenerate values.
                float clamped = Mathf.Max(0.1f, value);
                PlayerPrefs.SetFloat(KeyPlayerSpeedMultiplier, clamped);
                PlayerPrefs.Save();
            }
        }

        // -------------------------------------------------------------------
        // Flick recognition thresholds (spec §8.3, locked defaults for v0)
        // -------------------------------------------------------------------

        // Minimum normalized playfield-plane distance the finger must travel.
        public static float FlickMinDistanceNorm
        {
            get => PlayerPrefs.GetFloat(KeyFlickMinDistNorm, DefaultFlickMinDistanceNorm);
            set
            {
                PlayerPrefs.SetFloat(KeyFlickMinDistNorm, Mathf.Max(0f, value));
                PlayerPrefs.Save();
            }
        }

        // Minimum velocity in normalized playfield units per second.
        public static float FlickMinVelocityNormPerSec
        {
            get => PlayerPrefs.GetFloat(KeyFlickMinVelNormPerSec, DefaultFlickMinVelocityNormPerSec);
            set
            {
                PlayerPrefs.SetFloat(KeyFlickMinVelNormPerSec, Mathf.Max(0f, value));
                PlayerPrefs.Save();
            }
        }

        // Maximum duration (ms) from first movement to gesture completion.
        public static int FlickMaxGestureTimeMs
        {
            get => PlayerPrefs.GetInt(KeyFlickMaxGestureTimeMs, DefaultFlickMaxGestureTimeMs);
            set
            {
                PlayerPrefs.SetInt(KeyFlickMaxGestureTimeMs, Mathf.Max(1, value));
                PlayerPrefs.Save();
            }
        }

        // -------------------------------------------------------------------
        // v0 debug/playtest toggles (not persisted; set in code or Inspector)
        // -------------------------------------------------------------------

        /// <summary>
        /// DEBUG: When true, PlayerDebugRenderer draws an OnGUI line showing input projection info:
        /// whether the last touch used the visual surface raycast or the flat plane, both projected
        /// positions, and the XY delta between them.  Requires useVisualSurfaceRaycast enabled on
        /// PlayerAppController.  Default: false.
        /// </summary>
        public static bool DebugShowInputProjection = true;

        /// <summary>
        /// DEBUG: When true, PlayerDebugRenderer draws a live OnGUI overlay for the current touch:
        /// touch radius r, hit-band bounds [hitInner..hitOuter], judgement radius, radial/arc
        /// pass-fail flags, and matched lane IDs.  Requires PlayerDebugRenderer in the scene.
        /// Default: false.
        /// </summary>
        public static bool DebugShowTouchBand = true;

        /// <summary>
        /// DEBUG: When true, the Perfect tier window is extended to cover the full GreatWindowMs.
        /// Great tier is suppressed — every in-window hit becomes Perfect (or Perfect+ if within
        /// PerfectPlusWindowMs). Perfect+ sub-window is NOT enlarged.
        /// Default: false.
        /// </summary>
        public static bool PerfectWindowCoversGreatWindow = false;

        /// <summary>
        /// DEBUG: When true, flick notes can only be triggered by a new touch (IsNew/TouchBegin).
        /// When false, any active touch can arm a flick note; the gesture baseline is reset
        /// the first time the touch becomes eligible (in-window + in-lane) for each note.
        /// Default: false.
        /// </summary>
        public static bool FlickRequireTouchBegin = false;

        /// <summary>
        /// DEBUG: When true, the flick Perfect window expands to cover the full GreatWindowMs.
        /// Great tier is suppressed for flick — every in-window flick becomes Perfect (or Perfect+
        /// if within PerfectPlusWindowMs). Perfect+ sub-window is NOT enlarged.
        /// When false (default), flick timing is evaluated normally: Perfect inside PerfectWindowMs,
        /// Great inside GreatWindowMs, Miss outside.
        /// Applied to flick judgement only; does NOT affect tap/hold (use PerfectWindowCoversGreatWindow).
        /// Default: false.
        /// </summary>
        public static bool FlickPerfectWindowCoversGreatWindow = false;

        /// <summary>
        /// Visual/skin inset of the judgement ring inside the chart outerRadius (spec §5.5.2 / §8.3.1).
        ///
        /// judgementRadiusLocal = outerLocal − (JudgementInsetNorm × minDimLocal)
        ///
        /// This is where notes land visually (approach ends here) and where the judgement line
        /// is drawn.  Chart outerLocal remains the geometry reference for hit-testing and charting.
        /// Default: 0.03 (3 % of minDimLocal inward from the chart outer edge).
        /// </summary>
        public static float JudgementInsetNorm = 0.003f;

        /// <summary>
        /// Visual/skin expansion of the arena mesh rim beyond the chart outerRadius (spec §5.5.2 / §8.3.1).
        ///
        /// visualOuterLocal = outerLocal + (VisualOuterExpandNorm × minDimLocal)
        ///
        /// The arena surface mesh and outer arc line extend to visualOuterLocal so the track
        /// looks thick beyond the judgement ring.  Does NOT affect hit-testing, charting, or
        /// the judgement ring position.
        /// Default: 0.00 (no extra visual rim — mesh matches chart outerLocal).
        /// </summary>
        public static float VisualOuterExpandNorm = 0.00f;

        /// <summary>
        /// Hit Band — inner inset from the judgement ring (input-only, spec §5.5.2 / §8.3.1).
        ///
        /// hitInnerLocal = max(judgementRadiusLocal − HitBandInnerInsetNorm × minDimLocal, chartInnerLocal)
        ///
        /// A touch must be at or outward of hitInnerLocal to count as "inside lane".
        /// Clamped to chartInnerLocal so the hit band never extends past the chart inner edge.
        /// Default: 0.02 (2 % of minDimLocal inward from the judgement ring).
        /// </summary>
        public static float HitBandInnerInsetNorm = 0.004f;

        /// <summary>
        /// Hit Band — outer inset from the judgement ring (input-only, spec §5.5.2 / §8.3.1).
        ///
        /// hitOuterLocal = judgementRadiusLocal + HitBandOuterInsetNorm × minDimLocal
        ///
        /// A touch must be at or inward of hitOuterLocal to count as "inside lane".
        /// More tolerant outward than inward — designed for outer-rim finger comfort.
        /// Default: 0.04 (4 % of minDimLocal outward from the judgement ring).
        /// </summary>
        public static float HitBandOuterInsetNorm = 0.04f;

        /// <summary>
        /// Additional inner expansion applied on top of the hit band (input-only, spec §5.5.1 / §8.3.1).
        ///
        /// finalHitInner = max(judgementRadius − (HitBandInnerInsetNorm + InputBandExpandInnerNorm) × minDimLocal, chartInner)
        ///
        /// Kept for fine-tuning; with the hit band system the primary tolerance is HitBandInnerInsetNorm.
        /// Default: 0.00 (no extra inner expansion beyond the hit band).
        /// </summary>
        public static float InputBandExpandInnerNorm = 0.00f;

        /// <summary>
        /// Additional outer expansion applied on top of the hit band (input-only, spec §5.5.1 / §8.3.1).
        ///
        /// finalHitOuter = judgementRadius + (HitBandOuterInsetNorm + InputBandExpandOuterNorm) × minDimLocal
        ///
        /// Kept for fine-tuning; with the hit band system the primary tolerance is HitBandOuterInsetNorm.
        /// Default: 0.03 (3 % of minDimLocal extra outward beyond the hit band).
        /// </summary>
        public static float InputBandExpandOuterNorm = 0.03f;

        // -------------------------------------------------------------------
        // Convenience: reset all settings to defaults
        // -------------------------------------------------------------------

        public static void ResetToDefaults()
        {
            UserOffsetMs              = DefaultUserOffsetMs;
            PlayerSpeedMultiplier     = DefaultPlayerSpeedMultiplier;
            FlickMinDistanceNorm      = DefaultFlickMinDistanceNorm;
            FlickMinVelocityNormPerSec = DefaultFlickMinVelocityNormPerSec;
            FlickMaxGestureTimeMs     = DefaultFlickMaxGestureTimeMs;
        }
    }
}
