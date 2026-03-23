// PlayfieldFrustumProfile.cs
// Shared frustum surface profile — production-safe source of truth for frustum heights.
//
// All five production renderers (TapNoteRenderer, CatchNoteRenderer, FlickNoteRenderer,
// HoldBodyRenderer, JudgementRingRenderer) read frustum heights from this component
// rather than from PlayerDebugArenaSurface, so the visual surface shape is correct
// whether or not the debug scaffold is present in the scene.
//
// PlayerDebugArenaSurface can optionally reference this component to stay visually aligned.
//
// Wiring:
//   1. Create an empty GO "FrustumProfile" in the Player scene.
//   2. Add this component.
//   3. Assign it to the FrustumProfile field on each production renderer.
//   4. (Optional) assign it to PlayerDebugArenaSurface to keep the debug surface in sync.

using UnityEngine;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Shared frustum surface profile. Holds the Z heights used by all production
    /// renderers to match the cone/frustum arena shape (spec §5.7.x).
    ///
    /// <para>This is the single source of truth for frustum settings. Renderers read from
    /// here; <see cref="PlayerDebugArenaSurface"/> optionally delegates to this too.</para>
    ///
    /// Attach to any GameObject in the Player scene and assign to every production renderer.
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/PlayfieldFrustumProfile")]
    public class PlayfieldFrustumProfile : MonoBehaviour
    {
        [Tooltip("If true, renderers lift geometry onto the frustum cone surface using the heights below. " +
                 "If false, all renderers fall back to their local surfaceOffsetLocal (flat Z).")]
        [SerializeField] private bool useFrustumProfile = true;

        [Tooltip("PlayfieldRoot local Z at the inner arc edge. " +
                 "A small positive value avoids z-fighting with the z=0 interaction plane. " +
                 "Default: 0.001.")]
        [SerializeField] private float frustumHeightInner = 0.001f;

        [Tooltip("PlayfieldRoot local Z at the outer arc edge. " +
                 "Larger values create a steeper cone tilt. Default: 0.15.")]
        [SerializeField] private float frustumHeightOuter = 0.15f;

        /// <summary>Whether the frustum cone profile is active.</summary>
        public bool  UseFrustumProfile  => useFrustumProfile;

        /// <summary>PlayfieldRoot local Z at the inner arc edge.</summary>
        public float FrustumHeightInner => frustumHeightInner;

        /// <summary>PlayfieldRoot local Z at the outer arc edge.</summary>
        public float FrustumHeightOuter => frustumHeightOuter;
    }
}
