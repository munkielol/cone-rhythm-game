// ChartTrack.cs
// Defines the core building block for all animated chart parameters.
// Every visual/interactive property on arenas, lanes, and camera is stored
// as a FloatTrack (a list of FloatKeyframes sorted by timeMs).

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RhythmicFlow.Shared
{
    // A single timestamped sample on an animated float track.
    // Equivalent to Keyframe<float> — kept concrete to satisfy Unity JsonUtility,
    // which cannot serialize open generic types.
    [Serializable]
    public class FloatKeyframe
    {
        // Timestamp in milliseconds from chart start (must be >= 0).
        public int timeMs;

        // The float value at this keyframe.
        // Meaning depends on the track: e.g. 0..1 for opacity, degrees for angles.
        public float value;

        // How to interpolate from this keyframe to the next one.
        // Valid strings (spec §5.1):
        //   "linear"    – linear interpolation
        //   "easeInOut" – smooth acceleration/deceleration
        //   "hold"      – holds the current value until the next keyframe (step)
        public string easing;
    }

    // An ordered list of FloatKeyframes representing one animated parameter over time.
    // Equivalent to Track<float> — concrete for JsonUtility compatibility.
    //
    // Evaluation rules (spec §5.9):
    //   – Before the first keyframe: hold the first keyframe's value.
    //   – After the last keyframe:   hold the last keyframe's value.
    //   – Between two keyframes:     interpolate using the earlier keyframe's easing.
    //   – Keyframes must be sorted ascending by timeMs; duplicate timeMs is an error.
    [Serializable]
    public class FloatTrack
    {
        public List<FloatKeyframe> keyframes = new List<FloatKeyframe>();

        // -------------------------------------------------------------------
        // Evaluation (spec §5.9)
        // -------------------------------------------------------------------

        /// <summary>
        /// Evaluates this track at the given chart time in milliseconds.
        /// 0 keyframes → returns defaultVal (required tracks should always have ≥1 by validator).
        /// 1 keyframe  → returns its value regardless of timeMs.
        /// N keyframes → clamp-extrapolate at edges; interpolate between surrounding pair.
        ///   easing "hold"   → step: hold left value until next keyframe.
        ///   easing anything else (incl. "linear") → Mathf.Lerp(left, right, t01).
        /// No allocations.
        /// </summary>
        public float Evaluate(int timeMs, float defaultVal = 0f)
        {
            if (keyframes == null || keyframes.Count == 0) { return defaultVal; }
            if (keyframes.Count == 1)                      { return keyframes[0].value; }

            // Clamp to endpoints.
            if (timeMs <= keyframes[0].timeMs)                      { return keyframes[0].value; }
            if (timeMs >= keyframes[keyframes.Count - 1].timeMs)    { return keyframes[keyframes.Count - 1].value; }

            // Find the right-hand keyframe (first whose timeMs > timeMs).
            int right = 1;
            while (right < keyframes.Count && keyframes[right].timeMs <= timeMs) { right++; }
            int left = right - 1;

            FloatKeyframe kfL = keyframes[left];
            FloatKeyframe kfR = keyframes[right];

            if (kfL.easing == "hold") { return kfL.value; }

            float t01 = (float)(timeMs - kfL.timeMs) / (float)(kfR.timeMs - kfL.timeMs);
            return Mathf.Lerp(kfL.value, kfR.value, t01);
        }

        /// <summary>
        /// Evaluates this track as an angle in degrees, using shortest-path interpolation
        /// so values wrap correctly through the 0°/360° boundary.
        /// Result is normalized to [0, 360).
        /// </summary>
        public float EvaluateAngleDeg(int timeMs, float defaultVal = 0f)
        {
            if (keyframes == null || keyframes.Count == 0) { return Normalize360(defaultVal); }
            if (keyframes.Count == 1)                      { return Normalize360(keyframes[0].value); }

            if (timeMs <= keyframes[0].timeMs)                   { return Normalize360(keyframes[0].value); }
            if (timeMs >= keyframes[keyframes.Count - 1].timeMs) { return Normalize360(keyframes[keyframes.Count - 1].value); }

            int right = 1;
            while (right < keyframes.Count && keyframes[right].timeMs <= timeMs) { right++; }
            int left = right - 1;

            FloatKeyframe kfL = keyframes[left];
            FloatKeyframe kfR = keyframes[right];

            if (kfL.easing == "hold") { return Normalize360(kfL.value); }

            float t01   = (float)(timeMs - kfL.timeMs) / (float)(kfR.timeMs - kfL.timeMs);
            float delta = ShortestSignedDeltaDeg(kfL.value, kfR.value);
            return Normalize360(kfL.value + delta * t01);
        }

        // Wraps angle to [0, 360).
        private static float Normalize360(float deg)
        {
            float r = deg % 360f;
            return r < 0f ? r + 360f : r;
        }

        // Shortest signed angular difference from 'from' to 'to' in (-180, 180].
        private static float ShortestSignedDeltaDeg(float from, float to)
        {
            float delta = (to - from) % 360f;
            if (delta >  180f) { delta -= 360f; }
            if (delta < -180f) { delta += 360f; }
            return delta;
        }
    }
}
