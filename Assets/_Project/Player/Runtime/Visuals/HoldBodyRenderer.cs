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

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        private Mesh                 _quadMesh;
        private MaterialPropertyBlock _propBlock; // initialized in Awake (Unity rule)

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            _quadMesh  = BuildUnitQuad();
            _propBlock = new MaterialPropertyBlock();
        }

        private void OnDestroy()
        {
            if (_quadMesh != null) { Destroy(_quadMesh); _quadMesh = null; }
        }

        private void LateUpdate()
        {
            if (playerAppController == null || _quadMesh == null || holdMaterial == null) { return; }

            var allNotes    = playerAppController.NotesAll;
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

            foreach (RuntimeNote note in allNotes)
            {
                if (note.Type != NoteType.Hold) { continue; }

                // Skip permanently resolved holds.
                if (note.State == NoteState.Hit || note.State == NoteState.Missed) { continue; }

                // Look up geometry.
                if (!laneGeos.TryGetValue(note.LaneId,    out LaneGeometry  lane))  { continue; }
                if (!laneToArena.TryGetValue(note.LaneId, out string arenaId))      { continue; }
                if (!arenaGeos.TryGetValue(arenaId,       out ArenaGeometry arena)) { continue; }

                // Compute chart band and approach radii (PlayfieldLocal units).
                float outerLocal = pfTf.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfTf.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Notes approach to the inset judgement ring, not the raw outer edge. VISUAL ONLY.
                float judgementR = outerLocal - PlayerSettingsStore.JudgementInsetNorm * pfTf.MinDimLocal;

                // Spawn radius: fraction of the approach path from inner edge toward judgementR.
                float spawnR = Mathf.Lerp(innerLocal, judgementR, spawnRadiusFactor);

                // Compute head and tail radii; check visibility.
                ComputeHoldEndpointsR(
                    note.StartTimeMs, note.EndTimeMs, chartTimeMs, greatWindowMs,
                    spawnR, judgementR,
                    out float headR, out float tailR, out bool visible);

                if (!visible) { continue; }

                // Degenerate ribbon: skip (avoids NaN in matrix).
                if (Mathf.Abs(headR - tailR) < 0.0001f) { continue; }

                // ----------------------------------------------------------
                // World-space ribbon geometry
                // ----------------------------------------------------------

                // Lane center direction (current frame — follows animated lane).
                float thetaRad = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                float cosT     = Mathf.Cos(thetaRad);
                float sinT     = Mathf.Sin(thetaRad);

                // Arena center in PlayfieldRoot local XY.
                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // Head (outer, larger r) and tail (inner, smaller r) in local space.
                var headLocal = new Vector3(ctr.x + headR * cosT, ctr.y + headR * sinT, 0f);
                var tailLocal = new Vector3(ctr.x + tailR * cosT, ctr.y + tailR * sinT, 0f);

                Vector3 headWorld = pfRoot.TransformPoint(headLocal);
                Vector3 tailWorld = pfRoot.TransformPoint(tailLocal);

                float ribbonLength = (headWorld - tailWorld).magnitude;
                if (ribbonLength < 0.0001f) { continue; }

                // Width: arc-length at judgementR × ratio (v0 approximation, uniform per ribbon).
                float ribbonWidthLocal = judgementR * lane.WidthDeg * Mathf.Deg2Rad * holdLaneWidthRatio;
                float ribbonWidth      = ribbonWidthLocal * pfRoot.lossyScale.x;

                // Build world-space axes for the stretched quad.
                Vector3 worldDir  = (headWorld - tailWorld).normalized;           // radially outward
                Vector3 worldNorm = pfRoot.TransformDirection(Vector3.forward);    // plane normal
                Vector3 worldTang = Vector3.Cross(worldNorm, worldDir).normalized; // ribbon width axis
                Vector3 midWorld  = (headWorld + tailWorld) * 0.5f;

                // Matrix columns: tangent×width | radial×length | normal | center
                var matrix = new Matrix4x4(
                    new Vector4(worldTang.x * ribbonWidth,  worldTang.y * ribbonWidth,
                                worldTang.z * ribbonWidth,  0f),
                    new Vector4(worldDir.x  * ribbonLength, worldDir.y  * ribbonLength,
                                worldDir.z  * ribbonLength, 0f),
                    new Vector4(worldNorm.x, worldNorm.y, worldNorm.z, 0f),
                    new Vector4(midWorld.x,  midWorld.y,  midWorld.z,  1f)
                );

                Graphics.DrawMesh(_quadMesh, matrix, holdMaterial, gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Hold endpoint computation
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes the head (outer) and tail (inner) radii for a hold ribbon in PlayfieldLocal units.
        ///
        /// <para>Head  = approach radius for startTimeMs → pins at judgementR once chartTime ≥ startTimeMs.</para>
        /// <para>Tail  = approach radius for endTimeMs   → pins at judgementR once chartTime ≥ endTimeMs.</para>
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

            // Head not yet on screen.
            if (headToHit > noteLeadTimeMs)
            {
                headR = spawnR; tailR = spawnR; visible = false;
                return;
            }

            // Tail has fully passed the miss window.
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
        /// timeToHitMs ≤ 0  → alpha = 1 → pinned at judgementR  (event is now or in the past).
        /// timeToHitMs ≥ lead → alpha = 0 → pinned at spawnR  (event is at or beyond spawn).
        /// </summary>
        private float ComputeApproachR(float timeToHitMs, float spawnR, float judgementR)
        {
            float alpha = (noteLeadTimeMs > 0)
                ? 1f - Mathf.Clamp01(timeToHitMs / noteLeadTimeMs)
                : 1f;
            return Mathf.Lerp(spawnR, judgementR, alpha);
        }

        // -------------------------------------------------------------------
        // Unit quad mesh
        // -------------------------------------------------------------------

        // Centered unit quad in XY spanning [−0.5, 0.5] × [−0.5, 0.5].
        // −Y = tail end, +Y = head end (radially outward in world space after matrix transform).
        private static Mesh BuildUnitQuad()
        {
            var mesh = new Mesh { name = "HoldBodyQuad" };

            mesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, 0f),  // 0 tail-left
                new Vector3( 0.5f, -0.5f, 0f),  // 1 tail-right
                new Vector3( 0.5f,  0.5f, 0f),  // 2 head-right
                new Vector3(-0.5f,  0.5f, 0f),  // 3 head-left
            };

            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };

            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
