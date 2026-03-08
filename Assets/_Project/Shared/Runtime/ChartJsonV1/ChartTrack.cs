// ChartTrack.cs
// Defines the core building block for all animated chart parameters.
// Every visual/interactive property on arenas, lanes, and camera is stored
// as a FloatTrack (a list of FloatKeyframes sorted by timeMs).

using System;
using System.Collections.Generic;

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
    }
}
