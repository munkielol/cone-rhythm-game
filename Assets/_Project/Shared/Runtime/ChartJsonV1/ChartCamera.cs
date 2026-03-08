// ChartCamera.cs
// Animated gameplay camera data. All tracks are keyframed by timeMs.
//
// IMPORTANT: camera motion is purely visual (spec §8 / §14).
// Hit-testing always uses the fixed playfield plane via ArcCreate-style
// ray→plane mapping and is never affected by camera animation.

using System;

namespace RhythmicFlow.Shared
{
    [Serializable]
    public class ChartCamera
    {
        // enabled: 0/1 only, easing "hold" only (same rules as arena/lane enabled).
        public FloatTrack enabled = new FloatTrack();

        // World-space position of the camera.
        public FloatTrack posX = new FloatTrack();
        public FloatTrack posY = new FloatTrack();
        public FloatTrack posZ = new FloatTrack();

        // Euler rotation of the camera in degrees.
        public FloatTrack rotPitchDeg = new FloatTrack();
        public FloatTrack rotYawDeg   = new FloatTrack();
        public FloatTrack rotRollDeg  = new FloatTrack();

        // Vertical field of view in degrees.
        public FloatTrack fovDeg = new FloatTrack();
    }
}
