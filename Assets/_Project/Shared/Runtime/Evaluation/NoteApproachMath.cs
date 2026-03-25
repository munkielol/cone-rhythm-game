// NoteApproachMath.cs
// Shared, allocation-free math helpers for note approach visuals and the
// judgement ring.  Single source of truth: all callers (HoldBodyRenderer,
// NoteApproachRenderer, JudgementRingRenderer, future Chart Editor Playfield
// Preview) invoke these instead of re-implementing the formula.
//
// Canonical approach formula  (spec §6.1 / §5.7.1):
//
//   timeToHitMs = eventTimeMs − chartTimeMs
//   alpha       = 1 − Clamp01( timeToHitMs / noteLeadTimeMs )
//   r           = Lerp( spawnR, judgementR, alpha )
//
//   alpha = 0  →  r = spawnR       (note far in the future; at spawn position)
//   alpha = 1  →  r = judgementR   (note at / past chart time; head pinned at ring)
//
//   Negative timeToHitMs → Clamp01 → 0 → alpha = 1 → r = judgementR (natural pin).
//
// Spec anchors:
//   §5.7   Notes occupy full lane width at that radius.
//   §5.7.1 Approach formula, noteLeadTimeMs, spawnRadiusFactor.
//   §5.8   Judgement ring radius formula.
//   §6.1   Approach speed / lead-time.

using UnityEngine;

namespace RhythmicFlow.Shared
{
    /// <summary>
    /// Pure static math helpers for note approach rendering and the judgement ring.
    /// All methods are side-effect-free and produce zero allocations.
    ///
    /// <para>Shared assembly (RhythmicFlow.Shared) so both the Player App and the
    /// Chart Editor Playfield Preview use the same arithmetic.</para>
    /// </summary>
    public static class NoteApproachMath
    {
        // ===================================================================
        // Approach formula  (spec §6.1)
        // ===================================================================

        /// <summary>
        /// Normalised approach progress in [0..1].
        ///
        /// <list type="bullet">
        ///   <item>alpha = 0 → note not yet visible (at spawn position)</item>
        ///   <item>alpha = 1 → note has reached (or passed) the judgement ring</item>
        /// </list>
        ///
        /// <c>alpha = 1 − Clamp01( timeToHitMs / noteLeadTimeMs )</c>
        /// </summary>
        /// <param name="timeToHitMs">eventTimeMs − chartTimeMs (negative when past event time).</param>
        /// <param name="noteLeadTimeMs">Total approach window in ms. Must be &gt; 0; if ≤ 0 returns 1.</param>
        public static float ApproachAlpha(float timeToHitMs, float noteLeadTimeMs)
        {
            if (noteLeadTimeMs <= 0f) { return 1f; }
            return 1f - Mathf.Clamp01(timeToHitMs / noteLeadTimeMs);
        }

        /// <summary>
        /// Approach radius in PlayfieldLocal units at the given time-to-event.
        ///
        /// <c>r = Lerp(spawnR, judgementR, ApproachAlpha(…))</c>
        /// </summary>
        public static float ApproachRadius(float timeToHitMs, float noteLeadTimeMs,
                                           float spawnR, float judgementR)
        {
            float alpha = ApproachAlpha(timeToHitMs, noteLeadTimeMs);
            return Mathf.Lerp(spawnR, judgementR, alpha);
        }

        // ===================================================================
        // Derived radii  (spec §5.8 / §5.7.1)
        // ===================================================================

        /// <summary>
        /// Visual judgement ring radius in PlayfieldLocal units.
        /// Notes land here; the judgement arc is drawn at this radius.
        ///
        /// <c>judgementR = outerLocal − JudgementInsetNorm × minDimLocal</c>
        ///
        /// Visual-only — does not affect hit-testing (spec §5.8).
        /// </summary>
        public static float JudgementRadius(float outerLocal, float minDimLocal,
                                            float judgementInsetNorm)
        {
            return outerLocal - judgementInsetNorm * minDimLocal;
        }

        /// <summary>
        /// Spawn radius where notes first appear.
        /// With <paramref name="spawnRadiusFactor"/> = 0 (v0 default) this equals
        /// <c>innerLocal</c> — notes spawn at the inner band edge and travel outward.
        ///
        /// <c>spawnR = innerLocal + Clamp01(factor) × (judgementR − innerLocal)</c>
        /// </summary>
        public static float SpawnRadius(float innerLocal, float judgementR, float spawnRadiusFactor)
        {
            return innerLocal + Mathf.Clamp01(spawnRadiusFactor) * (judgementR - innerLocal);
        }

        // ===================================================================
        // Lane geometry  (spec §5.7)
        // ===================================================================

        /// <summary>
        /// Chord width of a lane at local radius <paramref name="r"/>.
        ///
        /// Lane borders are radial lines at <c>centerDeg ± halfWidthDeg</c>.
        /// The straight-line chord between those borders at radius r is:
        /// <code>width = 2 · r · sin( halfWidthDeg · Deg2Rad )</code>
        ///
        /// Head and tail of a hold ribbon sit at different radii and therefore
        /// have different chord widths at each row (spec §5.7.1).
        /// </summary>
        public static float LaneChordWidthAtRadius(float r, float halfWidthDeg)
        {
            return 2f * r * Mathf.Sin(halfWidthDeg * Mathf.Deg2Rad);
        }

        // ===================================================================
        // Frustum surface alignment  (spec §5.7.1)
        // ===================================================================

        /// <summary>
        /// PlayfieldRoot local Z for a visual element at radius <paramref name="r"/>,
        /// lifting it onto the frustum cone surface so it sits on the same
        /// depth profile as the arena mesh.
        ///
        /// <code>
        /// s01    = Clamp01( (r − innerLocal) / (outerLocal − innerLocal) )
        /// localZ = Lerp( frustumHeightInner, frustumHeightOuter, s01 )
        /// </code>
        ///
        /// Default heights (matching <c>PlayerDebugArenaSurface</c> defaults):
        /// <list type="bullet">
        ///   <item>frustumHeightInner ≈ 0.001 (near-zero lift at inner band edge)</item>
        ///   <item>frustumHeightOuter ≈ 0.150 (moderate cone slope at outer rim)</item>
        /// </list>
        /// </summary>
        public static float FrustumZAtRadius(float r,
                                             float innerLocal, float outerLocal,
                                             float frustumHeightInner, float frustumHeightOuter)
        {
            float span = outerLocal - innerLocal;
            float s01  = (span > 0f) ? Mathf.Clamp01((r - innerLocal) / span) : 1f;
            return Mathf.Lerp(frustumHeightInner, frustumHeightOuter, s01);
        }
    }
}
