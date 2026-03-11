// HoldBodyRenderer.cs
// Scroll-style hold body renderer — behaves like an ArcCreate long note.
//
// ┌──────────────────────────────────────────────────────────────────────┐
// │  APPROACH FORMULA (per endpoint, same as PlayerDebugRenderer §6.1)  │
// │                                                                      │
// │  timeToHitMs  = eventTimeMs  − chartTimeMs                          │
// │  alpha        = 1 − Clamp01( timeToHitMs / noteLeadTimeMs )         │
// │  r            = Lerp( spawnR, judgementR, alpha )                   │
// │                                                                      │
// │  alpha = 0  →  at spawnR      (event far in the future)             │
// │  alpha = 1  →  at judgementR  (event at/past current time)          │
// │                                                                      │
// │  Negative timeToHitMs → Clamp01 gives 0 → alpha = 1 → pinned at    │
// │  judgementR automatically. No special branch required.               │
// └──────────────────────────────────────────────────────────────────────┘
//
// THREE PHASES OF A HOLD NOTE
//
//   A) Before start  (chartTime < startTimeMs)
//      headR = ComputeApproachR( startTimeMs )   < judgementR
//      tailR = ComputeApproachR( endTimeMs )     ≤ headR (further away)
//      Ribbon spans [tailR → headR] approaching together.
//
//   B) During hold  (startTimeMs ≤ chartTime < endTimeMs)
//      headR = judgementR   (pinned — alpha formula clamps naturally)
//      tailR = ComputeApproachR( endTimeMs )  (still approaching)
//      Ribbon shrinks as tail catches up to head.
//
//   C) After end  (chartTime ≥ endTimeMs + greatWindowMs)
//      Hidden — ComputeHoldEndpointsR returns visible=false.
//
// LANE TRANSFORM RULE (v0)
//   Uses the CURRENT evaluated lane.CenterDeg and lane.WidthDeg each frame.
//   The whole ribbon follows the lane without bending (v0; bending is a future upgrade).
//
// MATRIX CONSTRUCTION
//   All geometry is computed in PlayfieldRoot local XY space — the same space
//   that debug lane visuals use.  A "strip local matrix" is built there, then
//   world space is obtained by:
//       worldMatrix = pfRoot.localToWorldMatrix * stripLocalMatrix
//   This matches the playfield orientation exactly and handles arbitrary scale
//   and rotation of the playfieldRoot transform without special-casing.
//
//   The unit strip mesh spans:
//       X ∈ [−0.5, +0.5]   → width direction (tangent, perpendicular to lane radial)
//       Y ∈ [0, 1]          → length direction (Y=0 = tail, Y=1 = head)
//       Z = 0               → surface normal direction (for shading only)
//
// Spec §5.7.1.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Runtime visual ribbon renderer for Hold note bodies (scroll/long-note style).
    /// Attach to any GameObject in the Player scene; assign PlayerAppController and a hold Material.
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/HoldBodyRenderer")]
    public class HoldBodyRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("The PlayerAppController this renderer reads notes and geometry from.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Material for hold-body ribbons. Use an unlit / additive shader with _Color support.")]
        [SerializeField] private Material holdMaterial;

        [Tooltip("Hold ribbon tint color (also sets alpha for transparency).")]
        [SerializeField] private Color holdColor = new Color(0.3f, 0.6f, 1f, 0.85f);

        [Header("Approach")]
        [Tooltip("How many ms before StartTimeMs the ribbon first becomes visible. " +
                 "Match this to noteLeadTimeMs in PlayerDebugRenderer.")]
        [SerializeField] private int noteLeadTimeMs = 2000;

        [Tooltip("Spawn radius as a normalized fraction of the approach path: " +
                 "0 = inner band edge, 1 = judgement ring.")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnRadiusFactor = 0.25f;

        [Header("Ribbon Sizing")]
        [Tooltip("Width of the ribbon as a fraction of the lane's arc-length width at judgementR. " +
                 "1 = full lane width, 0.7 recommended.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float holdLaneWidthRatio = 0.7f;

        [Tooltip("Small Z offset in PlayfieldRoot local units added to the ribbon to prevent " +
                 "z-fighting with the arena surface mesh. Positive = toward camera.")]
        [SerializeField] private float surfaceOffsetLocal = 0.002f;

        [Header("Debug")]
        [Tooltip("When true, draws a Debug.DrawLine between ribbon endpoints each frame " +
                 "to verify strip placement. Visible in Scene view.")]
        [SerializeField] private bool debugDrawEndpoints = false;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        private Mesh                  _quadMesh;
        private MaterialPropertyBlock _propBlock; // initialized in Awake (Unity rule)

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

            _propBlock.SetColor("_Color", holdColor);

            // Cache localToWorldMatrix once per frame — it doesn't change mid-frame.
            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            foreach (RuntimeNote note in allNotes)
            {
                if (note.Type != NoteType.Hold) { continue; }

                // Skip permanently resolved holds.
                if (note.State == NoteState.Hit || note.State == NoteState.Missed) { continue; }

                // Look up geometry.
                if (!laneGeos.TryGetValue(note.LaneId,    out LaneGeometry  lane))  { continue; }
                if (!laneToArena.TryGetValue(note.LaneId, out string arenaId))      { continue; }
                if (!arenaGeos.TryGetValue(arenaId,       out ArenaGeometry arena)) { continue; }

                // -------------------------------------------------------
                // Radii — all in PlayfieldRoot local units
                // -------------------------------------------------------

                float outerLocal = pfTf.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfTf.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Notes approach to the inset judgement ring, not the raw outer edge. VISUAL ONLY.
                float judgementR = outerLocal - PlayerSettingsStore.JudgementInsetNorm * pfTf.MinDimLocal;

                // Spawn radius: a fraction of the path from inner edge toward judgementR.
                float spawnR = Mathf.Lerp(innerLocal, judgementR, spawnRadiusFactor);

                // Compute head and tail radii; check visibility.
                ComputeHoldEndpointsR(
                    note.StartTimeMs, note.EndTimeMs, chartTimeMs, greatWindowMs,
                    spawnR, judgementR,
                    out float headR, out float tailR, out bool visible);

                if (!visible) { continue; }

                // Segment length in local units. headR > tailR always (head is farther out).
                float segLengthLocal = headR - tailR;
                if (segLengthLocal < 0.0001f) { continue; } // degenerate: skip

                // -------------------------------------------------------
                // Local-space axes — all in PlayfieldRoot local XY
                // -------------------------------------------------------

                // Lane center angle defines the radial direction (outward = +r).
                float thetaRad = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                float cosT     = Mathf.Cos(thetaRad);
                float sinT     = Mathf.Sin(thetaRad);

                // Radial outward unit vector in local XY (the "length" axis of the strip).
                var dirLocal = new Vector3(cosT, sinT, 0f);

                // Tangent: 90° CCW from radial, in the local XY plane (the "width" axis of the strip).
                // Cross(localZ, dirLocal) = (-sinT, cosT, 0), which is the CCW tangent.
                var tangLocal = new Vector3(-sinT, cosT, 0f);

                // Normal: PlayfieldRoot local +Z — perpendicular to the playfield surface.
                // Used as column 2 of the strip matrix (controls mesh shading normal direction).
                var normLocal = new Vector3(0f, 0f, 1f);

                // -------------------------------------------------------
                // Strip endpoints in PlayfieldRoot local space
                // -------------------------------------------------------

                // Arena center in local XY.
                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // Tail (inner, smaller r) = strip origin (Y = 0 on the unit mesh).
                // Offset in local Z to sit above the arena surface and avoid z-fighting.
                var tailLocal3 = new Vector3(
                    ctr.x + tailR * cosT,
                    ctr.y + tailR * sinT,
                    surfaceOffsetLocal);

                // Head (outer, larger r) = strip tip (Y = 1 on the unit mesh).
                // Same Z offset — the ribbon is flat and parallel to the XY plane.
                var headLocal3 = new Vector3(
                    ctr.x + headR * cosT,
                    ctr.y + headR * sinT,
                    surfaceOffsetLocal);

                // -------------------------------------------------------
                // Strip width in local units
                // -------------------------------------------------------

                // Arc-length at judgementR: judgementR × widthDeg_rad × ratio.
                // judgementR is already in local units, so this gives a local-unit width.
                float widthLocal = judgementR * lane.WidthDeg * Mathf.Deg2Rad * holdLaneWidthRatio;

                // -------------------------------------------------------
                // Build strip matrix in PlayfieldRoot local space
                // -------------------------------------------------------
                //
                // The unit strip mesh vertex layout:
                //   (−0.5, 0, 0) → tail-left      (X = −half-width, Y = 0 = tail)
                //   (+0.5, 0, 0) → tail-right     (X = +half-width, Y = 0 = tail)
                //   (+0.5, 1, 0) → head-right     (X = +half-width, Y = 1 = head)
                //   (−0.5, 1, 0) → head-left      (X = −half-width, Y = 1 = head)
                //
                // Matrix columns (each maps one local mesh axis to a local-space vector):
                //   Column 0 (X axis) = tangLocal × widthLocal      → scales X to lane width
                //   Column 1 (Y axis) = dirLocal  × segLengthLocal  → scales Y from tail to head
                //   Column 2 (Z axis) = normLocal                   → unit normal (shading only)
                //   Column 3          = tailLocal3                  → origin = tail position
                //
                var stripLocalMatrix = new Matrix4x4(
                    new Vector4(tangLocal.x * widthLocal,    tangLocal.y * widthLocal,    0f, 0f),
                    new Vector4(dirLocal.x  * segLengthLocal, dirLocal.y * segLengthLocal, 0f, 0f),
                    new Vector4(normLocal.x, normLocal.y, normLocal.z, 0f),
                    new Vector4(tailLocal3.x, tailLocal3.y, tailLocal3.z, 1f)
                );

                // Transform the strip from PlayfieldRoot local space into world space.
                // Using localToWorldMatrix correctly handles rotation, scale and translation
                // of playfieldRoot — no manual axis transforms needed.
                Matrix4x4 worldMatrix = localToWorld * stripLocalMatrix;

                // -------------------------------------------------------
                // Optional debug: draw the segment endpoints in world space
                // -------------------------------------------------------
                if (debugDrawEndpoints)
                {
                    // TransformPoint converts from PlayfieldRoot local → world.
                    Vector3 tailWorld = pfRoot.TransformPoint(
                        new Vector3(ctr.x + tailR * cosT, ctr.y + tailR * sinT, surfaceOffsetLocal));
                    Vector3 headWorld = pfRoot.TransformPoint(
                        new Vector3(ctr.x + headR * cosT, ctr.y + headR * sinT, surfaceOffsetLocal));
                    Debug.DrawLine(tailWorld, headWorld, holdColor);
                }

                Graphics.DrawMesh(_quadMesh, worldMatrix, holdMaterial, gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Hold endpoint radii computation
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes the head (outer) and tail (inner) radii for a hold ribbon in PlayfieldLocal units.
        ///
        /// <para>Head = approach radius for startTimeMs; pins at judgementR when chartTime ≥ startTimeMs.</para>
        /// <para>Tail = approach radius for endTimeMs; pins at judgementR when chartTime ≥ endTimeMs.</para>
        /// <para>visible = false when the note is not yet on screen or has fully cleared the window.</para>
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
            double headToHit = startTimeMs - chartTimeMs;
            double tailToHit = endTimeMs   - chartTimeMs;

            // Head is not yet on screen — the whole ribbon is invisible.
            if (headToHit > noteLeadTimeMs)
            {
                headR = spawnR; tailR = spawnR; visible = false;
                return;
            }

            // Tail has fully passed the miss window — hide the ribbon.
            if (tailToHit < -greatWindowMs)
            {
                headR = judgementR; tailR = judgementR; visible = false;
                return;
            }

            headR   = ComputeApproachR((float)headToHit, spawnR, judgementR);
            tailR   = ComputeApproachR((float)tailToHit, spawnR, judgementR);
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
        /// timeToHitMs ≤ 0  → alpha = 1 → pinned at judgementR (event is now or in the past).
        /// timeToHitMs ≥ noteLeadTimeMs → alpha = 0 → pinned at spawnR (far future).
        /// </summary>
        private float ComputeApproachR(float timeToHitMs, float spawnR, float judgementR)
        {
            float alpha = (noteLeadTimeMs > 0)
                ? 1f - Mathf.Clamp01(timeToHitMs / noteLeadTimeMs)
                : 1f;
            return Mathf.Lerp(spawnR, judgementR, alpha);
        }

        // -------------------------------------------------------------------
        // Unit strip mesh
        // -------------------------------------------------------------------

        // Builds a unit strip in local XY:
        //   X ∈ [−0.5, +0.5]  — width axis (tangent)
        //   Y ∈ [0, 1]         — length axis (tail at Y=0, head at Y=1)
        //   Z = 0              — normal axis
        //
        // The strip is NOT centered in Y.  The matrix translation column sets the
        // origin at the tail, and column 1 (Y axis) extends to the head.
        // This matches the Matrix4x4 construction in LateUpdate above.
        private static Mesh BuildUnitStrip()
        {
            var mesh = new Mesh { name = "HoldBodyStrip" };

            mesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, 0f, 0f),  // 0 tail-left
                new Vector3( 0.5f, 0f, 0f),  // 1 tail-right
                new Vector3( 0.5f, 1f, 0f),  // 2 head-right
                new Vector3(-0.5f, 1f, 0f),  // 3 head-left
            };

            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),  // tail-left
                new Vector2(1f, 0f),  // tail-right
                new Vector2(1f, 1f),  // head-right
                new Vector2(0f, 1f),  // head-left
            };

            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
