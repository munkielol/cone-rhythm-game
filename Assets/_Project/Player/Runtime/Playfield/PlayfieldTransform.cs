// PlayfieldTransform.cs
// Maps normalized playfield coordinates (0..1) to PlayfieldRoot local XY and back.
//
// Definition (spec §5.4):
//   PlayfieldLocalMin / PlayfieldLocalMax define the playable safe-area rectangle
//   in PlayfieldRoot's local XY plane (localZ = 0).
//
//   NormalizedToLocal(p) = lerp(PlayfieldLocalMin, PlayfieldLocalMax, p)
//   LocalToNormalized(q) = inverseLerp(PlayfieldLocalMin, PlayfieldLocalMax, q)
//
//   minDimLocal = min(PlayfieldLocalMax.x - PlayfieldLocalMin.x,
//                    PlayfieldLocalMax.y - PlayfieldLocalMin.y)
//
// All radius math (outerRadius, bandThickness) must multiply by minDimLocal to stay aspect-safe.
// This class is pure math — no MonoBehaviour, no scene dependencies.

using UnityEngine;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Pure-math playfield coordinate converter.
    /// Instantiated by whatever manages the playfield (e.g., a PlayfieldManager MonoBehaviour).
    /// </summary>
    public class PlayfieldTransform
    {
        // -------------------------------------------------------------------
        // Configuration
        // -------------------------------------------------------------------

        /// <summary>
        /// Minimum corner of the playable safe-area rectangle in PlayfieldRoot local XY.
        /// Corresponds to normalized (0, 0) — bottom-left (spec §5.3 / §5.4).
        /// </summary>
        public Vector2 LocalMin { get; private set; }

        /// <summary>
        /// Maximum corner of the playable safe-area rectangle in PlayfieldRoot local XY.
        /// Corresponds to normalized (1, 1) — top-right (spec §5.3 / §5.4).
        /// </summary>
        public Vector2 LocalMax { get; private set; }

        /// <summary>
        /// Minimum dimension of the playable rectangle in local units.
        /// Used to convert normalized radii to local-unit radii in aspect-safe math (spec §5.5).
        /// minDimLocal = min(width, height)
        /// </summary>
        public float MinDimLocal { get; private set; }

        // -------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------

        /// <param name="localMin">PlayfieldRoot local XY of the bottom-left safe-area corner.</param>
        /// <param name="localMax">PlayfieldRoot local XY of the top-right safe-area corner.</param>
        public PlayfieldTransform(Vector2 localMin, Vector2 localMax)
        {
            LocalMin   = localMin;
            LocalMax   = localMax;
            MinDimLocal = Mathf.Min(localMax.x - localMin.x, localMax.y - localMin.y);
        }

        // -------------------------------------------------------------------
        // Coordinate conversion
        // -------------------------------------------------------------------

        /// <summary>
        /// Converts a normalized playfield coordinate (0..1, 0..1) to PlayfieldRoot local XY.
        /// Spec §5.4: NormalizedToLocal(p) = lerp(PlayfieldLocalMin, PlayfieldLocalMax, p)
        /// </summary>
        public Vector2 NormalizedToLocal(Vector2 normalized)
        {
            return new Vector2(
                Mathf.Lerp(LocalMin.x, LocalMax.x, normalized.x),
                Mathf.Lerp(LocalMin.y, LocalMax.y, normalized.y)
            );
        }

        /// <summary>
        /// Converts a PlayfieldRoot local XY point to normalized (0..1, 0..1) playfield coordinates.
        /// Spec §5.4: LocalToNormalized(q) = inverseLerp(PlayfieldLocalMin, PlayfieldLocalMax, q)
        /// Note: result may be outside [0,1] if the point is outside the safe area.
        /// </summary>
        public Vector2 LocalToNormalized(Vector2 local)
        {
            float sizeX = LocalMax.x - LocalMin.x;
            float sizeY = LocalMax.y - LocalMin.y;

            return new Vector2(
                sizeX != 0f ? (local.x - LocalMin.x) / sizeX : 0f,
                sizeY != 0f ? (local.y - LocalMin.y) / sizeY : 0f
            );
        }

        // -------------------------------------------------------------------
        // Arena geometry helpers (aspect-safe, spec §5.5)
        // -------------------------------------------------------------------

        /// <summary>
        /// Converts a normalized outer radius to PlayfieldLocal units.
        /// outerLocal = outerRadiusNorm * minDimLocal  (spec §5.5)
        /// </summary>
        public float NormRadiusToLocal(float normalizedRadius)
        {
            return normalizedRadius * MinDimLocal;
        }

        // -------------------------------------------------------------------
        // Cone-frustum visual mapping helpers (math only, no meshes, spec §5.5)
        // -------------------------------------------------------------------

        /// <summary>
        /// Given an arena-local polar coordinate (theta in degrees, s in [0,1]),
        /// returns the 3D position on the frustum surface using visual-only scale parameters.
        ///
        /// Spec §5.5 rendering mapping (locked):
        ///   R(s)   = lerp(innerLocal, outerLocal, s) * visualRadiusScale
        ///   Y(s)   = lerp(visualHeightInner, visualHeightOuter, s)
        ///   pos3D  = (R(s)*cos(theta), Y(s), R(s)*sin(theta))
        ///
        /// IMPORTANT: this is visual-only. Hit-testing must use (r, theta) from PlayfieldLocal,
        /// not this 3D position (spec §5.5 "Authoritative rule").
        /// </summary>
        /// <param name="thetaDeg">Angle in degrees (0° = +X, CCW positive).</param>
        /// <param name="s">Normalized band position: 0 = inner edge, 1 = outer edge.</param>
        /// <param name="innerLocal">Inner radius in PlayfieldLocal units.</param>
        /// <param name="outerLocal">Outer radius in PlayfieldLocal units.</param>
        /// <param name="visualRadiusScale">
        /// Visual-only multiplier applied to PlayfieldLocal radii (skin constant, spec §5.5).
        /// Does NOT affect hit-testing.
        /// </param>
        /// <param name="visualHeightInner">Visual height at inner edge (skin constant).</param>
        /// <param name="visualHeightOuter">Visual height at outer edge (skin constant).</param>
        public static Vector3 FrustumSurfacePoint(
            float thetaDeg,
            float s,
            float innerLocal,
            float outerLocal,
            float visualRadiusScale,
            float visualHeightInner,
            float visualHeightOuter)
        {
            float thetaRad = thetaDeg * Mathf.Deg2Rad;
            float r = Mathf.Lerp(innerLocal, outerLocal, s) * visualRadiusScale;
            float y = Mathf.Lerp(visualHeightInner, visualHeightOuter, s);

            return new Vector3(r * Mathf.Cos(thetaRad), y, r * Mathf.Sin(thetaRad));
        }
    }
}
