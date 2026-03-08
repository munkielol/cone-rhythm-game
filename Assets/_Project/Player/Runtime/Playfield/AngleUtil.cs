// AngleUtil.cs
// Pure-function angle helpers used by hit-testing and keyframe interpolation.
//
// Angle convention (locked, spec §5.5 / §6):
//   0° = +X (right), angles increase CCW, values normalized to [0, 360).
//
// All methods are static and allocation-free (safe for hot-path use).

using UnityEngine;

namespace RhythmicFlow.Player
{
    public static class AngleUtil
    {
        // -------------------------------------------------------------------
        // normalize360
        // Spec §5.5: "normalize360(a) returns angle in [0, 360)."
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the equivalent angle in [0, 360).
        /// Works correctly for negative values and values >= 360.
        /// </summary>
        public static float Normalize360(float angleDeg)
        {
            // fmod can return negative values in C++, but Unity's Mathf.Repeat handles [0, length).
            return Mathf.Repeat(angleDeg, 360f);
        }

        // -------------------------------------------------------------------
        // shortestSignedAngleDeltaDeg
        // Spec §5.5: "shortestSignedAngleDeltaDeg(a, b) returns the signed delta
        //             from b to a on the shortest path (range [-180, +180])."
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the signed angle from <paramref name="fromDeg"/> to <paramref name="toDeg"/>
        /// on the shortest wrap-aware path, in the range [-180, +180].
        /// Positive = CCW (increasing angle direction).
        /// </summary>
        public static float ShortestSignedAngleDeltaDeg(float fromDeg, float toDeg)
        {
            // Spec §5.5: shortestSignedAngleDeltaDeg(a, b) — delta from b to a.
            // Calling convention here: fromDeg=b, toDeg=a → delta = toDeg - fromDeg.
            float delta = Normalize360(toDeg - fromDeg);

            // Map from [0, 360) to (-180, +180].
            if (delta > 180f) { delta -= 360f; }

            return delta;
        }

        // -------------------------------------------------------------------
        // IsAngleInArc (wrap-safe arc containment)
        // Spec §5.5 §4 "Arena arc test (wrap-safe)"
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns true if <paramref name="angleDeg"/> lies within the arc starting at
        /// <paramref name="arcStartDeg"/> and spanning <paramref name="arcSweepDeg"/> degrees CCW.
        /// Handles wrap-around correctly (e.g., arc from 350° spanning 40° → covers 350°–30°).
        /// </summary>
        public static bool IsAngleInArc(float angleDeg, float arcStartDeg, float arcSweepDeg)
        {
            // Spec §5.5 §4: "If arcSweepDeg >= 360, arc test passes."
            if (arcSweepDeg >= 360f) { return true; }

            float start = Normalize360(arcStartDeg);
            float angle = Normalize360(angleDeg);

            // Signed delta from start to the test angle, range [-180, +180].
            float delta = ShortestSignedAngleDeltaDeg(start, angle);

            // The arc spans CCW from 0 to arcSweepDeg.
            // A point is inside if delta >= 0 AND delta <= arcSweepDeg.
            // We clamp arcSweepDeg to (0, 360] per validation rules.
            return delta >= 0f && delta <= arcSweepDeg;
        }
    }
}
