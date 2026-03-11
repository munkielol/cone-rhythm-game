// HoldBodyRenderer.cs
// Scroll-style hold body renderer — behaves like an ArcCreate long note.
//
// ══════════════════════════════════════════════════════════════════════
//  APPROACH FORMULA  (shared with PlayerDebugRenderer §6.1)
//
//   timeToHitMs = eventTimeMs − chartTimeMs
//   alpha       = 1 − Clamp01( timeToHitMs / noteLeadTimeMs )
//   r           = Lerp( spawnR, judgementR, alpha )
//
//   alpha = 0  →  r = spawnR      (event is far in the future)
//   alpha = 1  →  r = judgementR  (event is at or past current time)
//
//   KEY PROPERTY: when timeToHitMs ≤ 0 (event is in the past),
//   Clamp01 returns 0, so alpha = 1, so r = judgementR.
//   This naturally pins any endpoint at the judgement ring once its
//   event time has passed — no special branch required.
// ══════════════════════════════════════════════════════════════════════
//
//  THREE PHASES OF A HOLD NOTE
//  ────────────────────────────
//
//  A)  Before start  (chartTime < startTimeMs)
//      headApproachParam → approach(startTimeMs)  < 1 (head approaching)
//      tailApproachParam → approach(endTimeMs)    < headParam (further)
//      Both approaching; ribbon spans tail → head, getting longer until
//      head reaches judgement ring.
//
//  B)  During hold  (startTimeMs ≤ chartTime ≤ endTimeMs)
//      headApproachParam → 1.0 (PINNED at judgementR by Clamp01)
//      tailApproachParam → approach(endTimeMs), still < 1
//      Head is fixed at judgementR; ribbon SHRINKS as tail approaches.
//      This is the "consumption" effect — the ribbon is consumed from
//      the judgement line inward as the player holds.
//
//  C)  After end  (chartTime > endTimeMs + greatWindowMs)
//      Ribbon is hidden by ComputeHoldEndpointsR returning visible=false.
//
// ══════════════════════════════════════════════════════════════════════
//  MATRIX CONSTRUCTION
//
//   All geometry is computed in PlayfieldRoot-local space (same space
//   as the debug lane visuals and ArenaHitTester).  A strip-local
//   matrix is built there, then promoted to world space via:
//
//       worldMatrix = pfRoot.localToWorldMatrix * stripLocalMatrix
//
//   This correctly handles any rotation, scale, or translation of the
//   playfieldRoot without special-casing.
//
//   Unit strip mesh layout:
//       X ∈ [−0.5, +0.5]  — width axis  (tangent, ⊥ to lane radial)
//       Y ∈ [0,    1  ]   — length axis (Y=0 = tail, Y=1 = head)
//       Z = 0              — surface normal (shading only)
//
// Spec §5.7.1.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Runtime visual ribbon renderer for Hold note bodies (scroll/long-note style).
    /// Draws the body in PlayfieldRoot-local space; promotes to world via localToWorldMatrix.
    /// Attach to any GameObject in the Player scene; assign PlayerAppController and a hold Material.
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/HoldBodyRenderer")]
    public class HoldBodyRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector — Wiring
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("The PlayerAppController this renderer reads notes and geometry from.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Material for hold-body ribbons. Use an unlit / additive shader with _Color support.")]
        [SerializeField] private Material holdMaterial;

        // -------------------------------------------------------------------
        // Inspector — Colors (one per hold phase)
        // -------------------------------------------------------------------

        [Header("Phase Colors")]
        [Tooltip("Ribbon color while the hold is approaching (before startTimeMs is reached).")]
        [SerializeField] private Color holdColorApproaching = new Color(0.3f, 0.6f, 1f, 0.75f);

        [Tooltip("Ribbon color while the hold is actively being held (HoldBind == Bound). " +
                 "Typically brighter than the approaching color.")]
        [SerializeField] private Color holdColorActive = new Color(0.5f, 0.85f, 1f, 1.0f);

        [Tooltip("Ribbon color after the hold was released early or missed. " +
                 "Typically a dim red to indicate failure.")]
        [SerializeField] private Color holdColorReleased = new Color(0.8f, 0.2f, 0.2f, 0.55f);

        // -------------------------------------------------------------------
        // Inspector — Approach
        // -------------------------------------------------------------------

        [Header("Approach")]
        [Tooltip("How many ms before StartTimeMs the ribbon first becomes visible. " +
                 "Match this to noteLeadTimeMs in PlayerDebugRenderer.")]
        [SerializeField] private int noteLeadTimeMs = 2000;

        [Tooltip("Spawn radius as a normalized fraction of the approach path: " +
                 "0 = inner band edge, 1 = judgement ring.")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnRadiusFactor = 0.25f;

        // -------------------------------------------------------------------
        // Inspector — Ribbon Sizing
        // -------------------------------------------------------------------

        [Header("Ribbon Sizing")]
        [Tooltip("Width of the ribbon as a fraction of the lane's arc-length at judgementR. " +
                 "1 = full lane width, 0.7 is recommended.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float holdLaneWidthRatio = 0.7f;

        [Tooltip("Small Z offset in PlayfieldRoot local units added to the ribbon to prevent " +
                 "z-fighting with the arena surface mesh. Positive = toward camera.")]
        [SerializeField] private float surfaceOffsetLocal = 0.002f;

        // -------------------------------------------------------------------
        // Inspector — Debug
        // -------------------------------------------------------------------

        [Header("Debug")]
        [Tooltip("When true, draws a Debug.DrawLine between the ribbon's two endpoints (in world " +
                 "space) each frame. Visible in the Scene view as a sanity check.")]
        [SerializeField] private bool debugDrawEndpoints = false;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        private Mesh                  _quadMesh;
        // Reused every DrawMesh call so we never allocate a new MaterialPropertyBlock per frame.
        // Must be created in Awake — Unity prohibits engine-object construction in field initializers.
        private MaterialPropertyBlock _propBlock;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            _quadMesh  = BuildUnitStrip();
            _propBlock = new MaterialPropertyBlock();
        }

        private void OnDestroy()
        {
            if (_quadMesh != null) { Destroy(_quadMesh); _quadMesh = null; }
        }

        private void LateUpdate()
        {
            if (playerAppController == null || _quadMesh == null || holdMaterial == null) { return; }

            var allNotes = playerAppController.NotesAll;
            if (allNotes == null) { return; }

            var arenaGeos   = playerAppController.ArenaGeometries;
            var laneGeos    = playerAppController.LaneGeometries;
            var laneToArena = playerAppController.LaneToArena;
            var pfTf        = playerAppController.PlayfieldTf;
            Transform pfRoot = playerAppController.playfieldRoot;

            if (arenaGeos == null || laneGeos == null || laneToArena == null
                || pfTf == null || pfRoot == null) { return; }

            double chartTimeMs   = playerAppController.EffectiveChartTimeMs;
            double greatWindowMs = playerAppController.GreatWindowMs;

            // Cache localToWorldMatrix once — it doesn't change mid-frame.
            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            foreach (RuntimeNote note in allNotes)
            {
                if (note.Type != NoteType.Hold) { continue; }

                // Skip holds that are fully resolved (entire hold judged as Hit or Missed).
                // Holds in Finished state (early release) still have their tail on screen, so
                // we keep drawing them until the tail clears the miss window.
                if (note.State == NoteState.Hit || note.State == NoteState.Missed) { continue; }

                // Look up geometry for this note's lane and arena.
                if (!laneGeos.TryGetValue(note.LaneId,    out LaneGeometry  lane))  { continue; }
                if (!laneToArena.TryGetValue(note.LaneId, out string arenaId))      { continue; }
                if (!arenaGeos.TryGetValue(arenaId,       out ArenaGeometry arena)) { continue; }

                // ───────────────────────────────────────────────────────────
                // Step 1: Compute approach radii (all in PlayfieldRoot local units)
                // ───────────────────────────────────────────────────────────

                float outerLocal = pfTf.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfTf.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // judgementR: where notes land visually — inset from the chart outer edge.
                // VISUAL ONLY; does not affect hit-testing.
                float judgementR = outerLocal
                    - PlayerSettingsStore.JudgementInsetNorm * pfTf.MinDimLocal;

                // spawnR: where notes appear at the start of the lead window.
                float spawnR = Mathf.Lerp(innerLocal, judgementR, spawnRadiusFactor);

                // ───────────────────────────────────────────────────────────
                // Step 2: Map startTimeMs and endTimeMs to local radii.
                //
                // headApproachParam = approach(startTimeMs, chartTimeMs)
                // tailApproachParam = approach(endTimeMs,   chartTimeMs)
                //
                // CLAMPING: the Clamp01 inside ComputeApproachR means that once
                // chartTimeMs >= startTimeMs, headR is automatically pinned at
                // judgementR (phase B — consumption).  No explicit branch needed.
                // ───────────────────────────────────────────────────────────

                ComputeHoldEndpointsR(
                    note.StartTimeMs, note.EndTimeMs,
                    chartTimeMs, greatWindowMs,
                    spawnR, judgementR,
                    out float headR, out float tailR, out bool visible);

                if (!visible) { continue; }

                // segLengthLocal: radial distance from tail to head in local units.
                // headR is always >= tailR (head is farther out along the radial).
                float segLengthLocal = headR - tailR;
                if (segLengthLocal < 0.0001f) { continue; } // degenerate strip: skip

                // ───────────────────────────────────────────────────────────
                // Step 3: Determine ribbon color from hold phase.
                //
                //   Unbound   → approaching, not yet started     → holdColorApproaching
                //   Bound     → actively being held               → holdColorActive
                //   Finished  → released early / ticks exhausted  → holdColorReleased
                // ───────────────────────────────────────────────────────────

                Color ribbonColor;
                switch (note.HoldBind)
                {
                    case HoldBindState.Bound:
                        ribbonColor = holdColorActive;
                        break;
                    case HoldBindState.Finished:
                        ribbonColor = holdColorReleased;
                        break;
                    default: // Unbound — approaching
                        ribbonColor = holdColorApproaching;
                        break;
                }
                _propBlock.SetColor("_Color", ribbonColor);

                // ───────────────────────────────────────────────────────────
                // Step 4: Build local-space axes for the strip.
                //
                // All vectors are in PlayfieldRoot local space so that
                //   worldMatrix = localToWorld * stripLocalMatrix
                // lands the ribbon on the correct playfield surface.
                // ───────────────────────────────────────────────────────────

                // Lane center angle: the radial direction (outward = +r).
                float thetaRad = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                float cosT     = Mathf.Cos(thetaRad);
                float sinT     = Mathf.Sin(thetaRad);

                // dirLocal: unit vector pointing radially outward in local XY.
                //           This is the strip's Y (length) axis.
                var dirLocal  = new Vector3(cosT, sinT, 0f);

                // tangLocal: unit vector perpendicular to dirLocal, in local XY (90° CCW).
                //            Cross(localZ, dirLocal) = (-sinT, cosT, 0).
                //            This is the strip's X (width) axis.
                var tangLocal = new Vector3(-sinT, cosT, 0f);

                // normLocal: PlayfieldRoot local +Z — the playfield surface normal.
                //            Used only for mesh shading; not scaled by the matrix.
                var normLocal = new Vector3(0f, 0f, 1f);

                // ───────────────────────────────────────────────────────────
                // Step 5: Compute strip endpoints in PlayfieldRoot local space.
                //
                //   tailLocal3 = arena center + tailR * dir   (Y=0 on the unit strip)
                //   headLocal3 = arena center + headR * dir   (Y=1 on the unit strip)
                //
                // Both are lifted by surfaceOffsetLocal in local Z to sit above the
                // arena mesh and avoid z-fighting.
                // ───────────────────────────────────────────────────────────

                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                var tailLocal3 = new Vector3(
                    ctr.x + tailR * cosT,
                    ctr.y + tailR * sinT,
                    surfaceOffsetLocal);          // strip origin (Y=0)

                var headLocal3 = new Vector3(
                    ctr.x + headR * cosT,
                    ctr.y + headR * sinT,
                    surfaceOffsetLocal);          // strip tip   (Y=1)

                // ───────────────────────────────────────────────────────────
                // Step 6: Width in local units.
                //
                // Arc-length at judgementR: judgementR × widthDeg_rad × ratio.
                // judgementR is already in local units, so this is a local-unit width.
                // ───────────────────────────────────────────────────────────

                float widthLocal = judgementR * lane.WidthDeg * Mathf.Deg2Rad * holdLaneWidthRatio;

                // ───────────────────────────────────────────────────────────
                // Step 7: Assemble the strip-local matrix.
                //
                // Unit strip mesh vertex layout:
                //   (−0.5, 0, 0) → tail-left    (X = −half-width, Y=0 = tail)
                //   (+0.5, 0, 0) → tail-right   (X = +half-width, Y=0 = tail)
                //   (+0.5, 1, 0) → head-right   (X = +half-width, Y=1 = head)
                //   (−0.5, 1, 0) → head-left    (X = −half-width, Y=1 = head)
                //
                // Each matrix column maps one mesh axis to a local-space vector:
                //   Col 0 (X) = tangLocal × widthLocal       → strip width direction
                //   Col 1 (Y) = dirLocal  × segLengthLocal   → strip length direction
                //   Col 2 (Z) = normLocal (unit)             → surface normal
                //   Col 3     = tailLocal3                   → origin at tail (Y=0)
                // ───────────────────────────────────────────────────────────

                var stripLocalMatrix = new Matrix4x4(
                    // Column 0 — X axis: tangent scaled to ribbon width
                    new Vector4(tangLocal.x * widthLocal,     tangLocal.y * widthLocal,     0f, 0f),
                    // Column 1 — Y axis: radial direction scaled to segment length
                    new Vector4(dirLocal.x  * segLengthLocal, dirLocal.y  * segLengthLocal, 0f, 0f),
                    // Column 2 — Z axis: surface normal (unit, for shading)
                    new Vector4(normLocal.x, normLocal.y, normLocal.z, 0f),
                    // Column 3 — Translation: strip origin = tail position (with Z offset)
                    new Vector4(tailLocal3.x, tailLocal3.y, tailLocal3.z, 1f)
                );

                // Apply playfieldRoot transform: strips are in local space;
                // localToWorldMatrix promotes them to world space in one multiply.
                Matrix4x4 worldMatrix = localToWorld * stripLocalMatrix;

                // ───────────────────────────────────────────────────────────
                // Step 8: Optional debug — draw segment endpoints in world space.
                // Visible in the Scene view; zero cost in production (flag is false by default).
                // ───────────────────────────────────────────────────────────
                if (debugDrawEndpoints)
                {
                    // pfRoot.TransformPoint: PlayfieldRoot local → world.
                    Vector3 tailWorld = pfRoot.TransformPoint(tailLocal3);
                    Vector3 headWorld = pfRoot.TransformPoint(headLocal3);
                    Debug.DrawLine(tailWorld, headWorld, ribbonColor);
                }

                Graphics.DrawMesh(
                    _quadMesh, worldMatrix, holdMaterial,
                    gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Hold endpoint radii computation
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes <paramref name="headR"/> and <paramref name="tailR"/> for one hold note,
        /// both in PlayfieldRoot local units, and sets <paramref name="visible"/> to false when
        /// the ribbon should not be drawn.
        ///
        /// <para>
        /// <b>Consumption mechanism:</b> headR is computed from <c>startTimeMs</c>.
        /// Once <c>chartTimeMs ≥ startTimeMs</c>, the approach formula naturally clamps
        /// headR to <c>judgementR</c> (alpha = 1 because timeToHitMs ≤ 0).
        /// tailR continues approaching from <c>endTimeMs</c>, so the ribbon length
        /// decreases — the "consumed" effect.
        /// </para>
        ///
        /// <para>Visibility rules:</para>
        /// <list type="bullet">
        ///   <item>Hide if <c>startTimeMs − chartTime &gt; noteLeadTimeMs</c> (head not on screen).</item>
        ///   <item>Hide if <c>endTimeMs − chartTime &lt; −greatWindowMs</c> (tail past miss window).</item>
        /// </list>
        /// </summary>
        private void ComputeHoldEndpointsR(
            int    startTimeMs,
            int    endTimeMs,
            double chartTimeMs,
            double greatWindowMs,
            float  spawnR,
            float  judgementR,
            out float headR,
            out float tailR,
            out bool  visible)
        {
            // Time remaining until each event (positive = in the future).
            double headToHit = startTimeMs - chartTimeMs;
            double tailToHit = endTimeMs   - chartTimeMs;

            // Head is not yet on screen: the whole ribbon is invisible.
            if (headToHit > noteLeadTimeMs)
            {
                headR = spawnR; tailR = spawnR; visible = false;
                return;
            }

            // Tail has fully passed the miss window: ribbon is hidden.
            if (tailToHit < -greatWindowMs)
            {
                headR = judgementR; tailR = judgementR; visible = false;
                return;
            }

            // headApproachParam: approach formula applied to startTimeMs.
            // When chartTimeMs >= startTimeMs (headToHit <= 0), Clamp01 pins alpha = 1
            // → headR = judgementR.  Phase B "consumption" is achieved automatically.
            headR = ComputeApproachR((float)headToHit, spawnR, judgementR);

            // tailApproachParam: approach formula applied to endTimeMs.
            // Continues approaching throughout the hold duration.
            tailR = ComputeApproachR((float)tailToHit, spawnR, judgementR);

            visible = true;
        }

        /// <summary>
        /// Maps a time-to-event value to a local radius using the approach formula (spec §6.1).
        ///
        /// <code>
        ///   alpha = 1 − Clamp01( timeToHitMs / noteLeadTimeMs )
        ///   r     = Lerp( spawnR, judgementR, alpha )
        /// </code>
        ///
        /// <list type="bullet">
        ///   <item><c>timeToHitMs ≤ 0</c>              → alpha = 1 → <c>r = judgementR</c> (pinned)</item>
        ///   <item><c>timeToHitMs = noteLeadTimeMs</c>  → alpha = 0 → <c>r = spawnR</c></item>
        ///   <item>Between: linear interpolation.</item>
        /// </list>
        /// </summary>
        private float ComputeApproachR(float timeToHitMs, float spawnR, float judgementR)
        {
            float alpha = (noteLeadTimeMs > 0)
                ? 1f - Mathf.Clamp01(timeToHitMs / noteLeadTimeMs)
                : 1f; // guard against divide-by-zero if noteLeadTimeMs is set to 0
            return Mathf.Lerp(spawnR, judgementR, alpha);
        }

        // -------------------------------------------------------------------
        // Unit strip mesh
        // -------------------------------------------------------------------

        // Builds the unit strip in local XY:
        //
        //   X ∈ [−0.5, +0.5]  — width axis  (tangent, ⊥ to lane radial)
        //   Y ∈ [0,    1  ]   — length axis  (Y=0 = tail, Y=1 = head)
        //   Z = 0              — mesh sits on local XY plane (normal = +Z for shading)
        //
        // The strip is anchored at its TAIL (Y=0 = origin).  The matrix
        // translation column (Col 3) sets the tail in world space; Col 1
        // (radial direction × segment length) extends it to the head.
        private static Mesh BuildUnitStrip()
        {
            var mesh = new Mesh { name = "HoldBodyStrip" };

            mesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, 0f, 0f),  // 0  tail-left
                new Vector3( 0.5f, 0f, 0f),  // 1  tail-right
                new Vector3( 0.5f, 1f, 0f),  // 2  head-right
                new Vector3(-0.5f, 1f, 0f),  // 3  head-left
            };

            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),  // tail-left
                new Vector2(1f, 0f),  // tail-right
                new Vector2(1f, 1f),  // head-right
                new Vector2(0f, 1f),  // head-left
            };

            // Single-sided; if the material is double-sided this is fine.
            // Winding: CCW from the +Z normal direction.
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
