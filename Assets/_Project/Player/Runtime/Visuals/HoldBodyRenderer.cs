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
//   alpha = 0  →  r = spawnR      (event far in the future)
//   alpha = 1  →  r = judgementR  (event at/past current time)
//
//   Negative timeToHitMs → Clamp01=0 → alpha=1 → r=judgementR.
//   Head naturally pins at judgementR once startTimeMs passes.
// ══════════════════════════════════════════════════════════════════════
//
//  THREE PHASES OF A HOLD NOTE
//
//   A)  Before start  (chartTime < startTimeMs)
//       Head and tail both approach; ribbon spans tail→head.
//
//   B)  During hold  (startTimeMs ≤ chartTime ≤ endTimeMs)
//       headR = judgementR (pinned by Clamp01).
//       tailR still approaching → ribbon shrinks (consumption).
//
//   C)  After end  (chartTime > endTimeMs + greatWindowMs)
//       Ribbon hidden (ComputeHoldEndpointsR returns visible=false).
//
// ══════════════════════════════════════════════════════════════════════
//  Z COMPUTATION — FRUSTUM SURFACE ALIGNMENT
//
//   The debug hold rail (PlayerDebugRenderer) places each endpoint in
//   PlayfieldRoot local XY, then lifts it in local Z using:
//
//     s01   = Clamp01( (r − innerLocal) / (outerLocal − innerLocal) )
//     localZ = Lerp( frustumHeightInner, frustumHeightOuter, s01 )
//            ≈ Lerp( 0.001, 0.15, s01 )
//
//   At the outer ring (r ≈ outerLocal, s01 ≈ 1.0) this gives z ≈ 0.15.
//   The old ribbon used a flat constant surfaceOffsetLocal = 0.002, so
//   the ribbon sat ~0.148 local units below the debug line at judgementR.
//
//   FIX: ComputeEndpointLocalZ() replicates the same formula as
//   PlayerDebugRenderer.VisualOnlyLocalZ(), keyed from an optional
//   PlayerDebugArenaSurface reference (auto-syncs height values).
//   When no arenaSurface is assigned, the manual frustum* fields below
//   are used as fallback.
//
// ══════════════════════════════════════════════════════════════════════
//  MATRIX CONSTRUCTION
//
//   All geometry is in PlayfieldRoot-local space.  World space is
//   obtained by:
//       worldMatrix = pfRoot.localToWorldMatrix * stripLocalMatrix
//
//   Unit strip mesh layout:
//       X ∈ [−0.5, +0.5]  — width axis  (tangent, ⊥ to lane radial)
//       Y ∈ [0, 1]         — length axis  (Y=0 = tail, Y=1 = head)
//       Z = 0              — surface normal
//
//   Matrix columns:
//       Col 0 = tangLocal × widthLocal         (width direction)
//       Col 1 = headLocal3 − tailLocal3         (3D segment vector)
//       Col 2 = local surface normal            (shading only)
//       Col 3 = tailLocal3                      (strip origin)
//
//   Col 1 is now a full 3D vector so the strip follows the frustum tilt.
//
// Spec §5.7.1.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Runtime visual ribbon renderer for Hold note bodies (scroll/long-note style).
    /// Draws in PlayfieldRoot-local space; promotes to world via localToWorldMatrix.
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
        // Inspector — Frustum surface alignment
        // -------------------------------------------------------------------

        [Header("Frustum Surface Alignment")]
        [Tooltip("Optional: assign the PlayerDebugArenaSurface in the scene. " +
                 "When set, frustum height values are read automatically from it (matching " +
                 "the debug hold-rail exactly). If null, the manual values below are used.")]
        [SerializeField] private PlayerDebugArenaSurface arenaSurface;

        [Tooltip("When true and no arenaSurface is assigned, the ribbon endpoints are lifted " +
                 "onto the frustum cone surface using the manual height values below. " +
                 "Set to false to use a flat ribbon at surfaceOffsetLocal.")]
        [SerializeField] private bool useFrustumProfile = true;

        [Tooltip("Local Z at the inner ring edge (matches PlayerDebugArenaSurface.frustumHeightInner). " +
                 "Only used when arenaSurface is not assigned. Default: 0.001.")]
        [SerializeField] private float frustumHeightInner = 0.001f;

        [Tooltip("Local Z at the outer ring edge (matches PlayerDebugArenaSurface.frustumHeightOuter). " +
                 "Only used when arenaSurface is not assigned. Default: 0.15.")]
        [SerializeField] private float frustumHeightOuter = 0.15f;

        [Tooltip("Additional Z offset added on top of the computed frustum height to prevent " +
                 "z-fighting when useFrustumProfile is false. Ignored when frustum profile is active " +
                 "(frustumHeightInner already provides z-fight clearance from z=0).")]
        [SerializeField] private float surfaceOffsetLocal = 0.002f;

        // -------------------------------------------------------------------
        // Inspector — Phase colors
        // -------------------------------------------------------------------

        [Header("Phase Colors")]
        [Tooltip("Ribbon color while the hold is approaching (before startTimeMs is reached).")]
        [SerializeField] private Color holdColorApproaching = new Color(0.3f, 0.6f, 1f, 0.75f);

        [Tooltip("Ribbon color while the hold is actively being held (HoldBind == Bound). Typically brighter.")]
        [SerializeField] private Color holdColorActive = new Color(0.5f, 0.85f, 1f, 1.0f);

        [Tooltip("Ribbon color after the hold was released early or missed. Typically dim red.")]
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
        // Inspector — Ribbon sizing
        // -------------------------------------------------------------------

        [Header("Ribbon Sizing")]
        [Tooltip("Width of the ribbon as a fraction of the lane's arc-length at judgementR. " +
                 "1 = full lane width, 0.7 is recommended.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float holdLaneWidthRatio = 0.7f;

        // -------------------------------------------------------------------
        // Inspector — Debug
        // -------------------------------------------------------------------

        [Header("Debug")]
        [Tooltip("Draws Debug.DrawLine between the ribbon's two endpoints in world space. " +
                 "Use this to compare the ribbon centerline against the debug hold rail.")]
        [SerializeField] private bool debugDrawEndpoints = false;

        [Tooltip("Draws the ribbon mesh quad outline (all 4 edges + centerline) in world space " +
                 "by transforming the unit-strip vertices with the same worldMatrix used for DrawMesh. " +
                 "Lets you verify the mesh exactly covers the expected area.")]
        [SerializeField] private bool debugDrawMeshOutline = false;

        // -------------------------------------------------------------------
        // Internals
        // -------------------------------------------------------------------

        private Mesh                  _quadMesh;
        // Reused every DrawMesh call — no per-frame allocation.
        // Must be created in Awake; Unity forbids engine-object ctor in field initializers.
        private MaterialPropertyBlock _propBlock;

        // Reusable array for transforming unit-strip corners when debugDrawMeshOutline is true.
        // 4 corners + center-tail + center-head = 6 scratch vectors. Allocated once in Awake.
        private readonly Vector3[] _outlineWorldPts = new Vector3[6];

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

            // Cache localToWorldMatrix once — unchanged within a frame.
            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            foreach (RuntimeNote note in allNotes)
            {
                if (note.Type != NoteType.Hold) { continue; }

                // Skip fully resolved holds.
                // Finished-but-tail-still-visible holds keep drawing (dim red via holdColorReleased).
                if (note.State == NoteState.Hit || note.State == NoteState.Missed) { continue; }

                // Look up geometry for this note's lane and arena.
                if (!laneGeos.TryGetValue(note.LaneId,    out LaneGeometry  lane))  { continue; }
                if (!laneToArena.TryGetValue(note.LaneId, out string arenaId))      { continue; }
                if (!arenaGeos.TryGetValue(arenaId,       out ArenaGeometry arena)) { continue; }

                // ── Step 1: Compute band radii (PlayfieldRoot local units) ───────────────

                float outerLocal = pfTf.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfTf.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // judgementR: visual inset from chart outer edge. VISUAL ONLY.
                float judgementR = outerLocal
                    - PlayerSettingsStore.JudgementInsetNorm * pfTf.MinDimLocal;

                // spawnR: where notes first appear (fraction of path from inner edge to judgementR).
                // Must match PlayerDebugRenderer: innerLocal + factor * (judgementR - innerLocal).
                float spawnR = innerLocal + spawnRadiusFactor * (judgementR - innerLocal);

                // ── Step 2: Map start/end times to local radii (with visibility check) ──

                ComputeHoldEndpointsR(
                    note.StartTimeMs, note.EndTimeMs,
                    chartTimeMs, greatWindowMs,
                    spawnR, judgementR,
                    out float headR, out float tailR, out bool visible);

                if (!visible) { continue; }

                // headR ≥ tailR always (head is farther out along the radial direction).
                // Degenerate: skip to avoid NaN in matrix.
                if (headR - tailR < 0.0001f) { continue; }

                // ── Step 3: Phase-based color ─────────────────────────────────────────────

                Color ribbonColor;
                switch (note.HoldBind)
                {
                    case HoldBindState.Bound:    ribbonColor = holdColorActive;    break;
                    case HoldBindState.Finished: ribbonColor = holdColorReleased;  break;
                    default:                     ribbonColor = holdColorApproaching; break;
                }
                _propBlock.SetColor("_Color", ribbonColor);

                // ── Step 4: Local-space axes ──────────────────────────────────────────────

                float thetaRad = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                float cosT     = Mathf.Cos(thetaRad);
                float sinT     = Mathf.Sin(thetaRad);

                // dirLocal: unit radial-outward vector in local XY — the strip length axis.
                var dirLocal = new Vector3(cosT, sinT, 0f);

                // tangLocal: 90° CCW from dirLocal, in local XY — the strip width axis.
                // Cross(localZ, dirLocal) = (-sinT, cosT, 0).
                var tangLocal = new Vector3(-sinT, cosT, 0f);

                // ── Step 5: Compute 3D local-space endpoints (XY + frustum Z) ────────────
                //
                // THIS IS THE KEY FIX.
                // Old code: localZ = surfaceOffsetLocal (constant 0.002) for both endpoints.
                // Correct:  localZ = Lerp(frustumHeightInner, frustumHeightOuter, s01)
                //           where s01 = Clamp01((r - inner) / (outer - inner)).
                // This exactly replicates PlayerDebugRenderer.VisualOnlyLocalZ(s01),
                // so the ribbon endpoints land on the same frustum surface as the debug rail.

                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // Tail: Y=0 on the unit strip mesh (strip origin).
                var tailLocal3 = new Vector3(
                    ctr.x + tailR * cosT,
                    ctr.y + tailR * sinT,
                    ComputeEndpointLocalZ(tailR, innerLocal, outerLocal));

                // Head: Y=1 on the unit strip mesh (strip tip).
                var headLocal3 = new Vector3(
                    ctr.x + headR * cosT,
                    ctr.y + headR * sinT,
                    ComputeEndpointLocalZ(headR, innerLocal, outerLocal));

                // ── Step 6: Width in local units ──────────────────────────────────────────

                // Arc-length at judgementR: judgementR × widthDeg_rad × ratio.
                float widthLocal = judgementR * lane.WidthDeg * Mathf.Deg2Rad * holdLaneWidthRatio;

                // ── Step 7: Build strip-local matrix ──────────────────────────────────────
                //
                // Unit strip vertex layout:
                //   (−0.5, 0, 0) → tail-left    (Y=0 = tail end)
                //   (+0.5, 0, 0) → tail-right
                //   (+0.5, 1, 0) → head-right   (Y=1 = head end)
                //   (−0.5, 1, 0) → head-left
                //
                // Column 0 (X): tangent × width → maps X ∈ [−0.5,+0.5] to lane width
                // Column 1 (Y): headLocal3 − tailLocal3 → full 3D segment vector
                //               (includes frustum Z component, tilting the strip correctly)
                // Column 2 (Z): local surface normal (shading; unlit shaders ignore this)
                // Column 3   : tailLocal3 → strip origin = tail world position
                //
                // IMPORTANT: Col 1 is the FULL vector, not normalize(dir) × length.
                // This ensures Y=1 maps exactly to headLocal3 with correct frustum Z.

                Vector3 segVec = headLocal3 - tailLocal3; // 3D: includes Z tilt from frustum

                var stripLocalMatrix = new Matrix4x4(
                    // Column 0 — X axis: tangent scaled to ribbon width (stays in XY)
                    new Vector4(tangLocal.x * widthLocal, tangLocal.y * widthLocal, 0f, 0f),
                    // Column 1 — Y axis: full 3D segment from tail to head
                    new Vector4(segVec.x, segVec.y, segVec.z, 0f),
                    // Column 2 — Z axis: local +Z (surface normal for shading; no effect on positions)
                    new Vector4(0f, 0f, 1f, 0f),
                    // Column 3 — Translation: strip origin at tail
                    new Vector4(tailLocal3.x, tailLocal3.y, tailLocal3.z, 1f)
                );

                // Promote local strip to world space using playfieldRoot's full transform.
                Matrix4x4 worldMatrix = localToWorld * stripLocalMatrix;

                // ── Step 8: Debug visualizations ─────────────────────────────────────────

                if (debugDrawEndpoints || debugDrawMeshOutline)
                {
                    // Tail and head in world space — same TransformPoint as debug renderer uses.
                    Vector3 tailWorld = pfRoot.TransformPoint(tailLocal3);
                    Vector3 headWorld = pfRoot.TransformPoint(headLocal3);

                    if (debugDrawEndpoints)
                    {
                        // Centerline: matches the debug hold rail line exactly (when frustum
                        // heights match). Compare in Scene view to verify overlap.
                        Debug.DrawLine(tailWorld, headWorld, ribbonColor);
                    }

                    if (debugDrawMeshOutline)
                    {
                        // Transform the 4 unit-strip corners + 2 centerline points through the
                        // same worldMatrix that Graphics.DrawMesh receives.  The resulting
                        // quad outline should overlap the mesh edges exactly.
                        //
                        // _outlineWorldPts layout:
                        //   [0] tail-left   (−0.5, 0, 0)
                        //   [1] tail-right  (+0.5, 0, 0)
                        //   [2] head-right  (+0.5, 1, 0)
                        //   [3] head-left   (−0.5, 1, 0)
                        //   [4] center-tail ( 0.0, 0, 0)
                        //   [5] center-head ( 0.0, 1, 0)

                        _outlineWorldPts[0] = worldMatrix.MultiplyPoint3x4(new Vector3(-0.5f, 0f, 0f));
                        _outlineWorldPts[1] = worldMatrix.MultiplyPoint3x4(new Vector3( 0.5f, 0f, 0f));
                        _outlineWorldPts[2] = worldMatrix.MultiplyPoint3x4(new Vector3( 0.5f, 1f, 0f));
                        _outlineWorldPts[3] = worldMatrix.MultiplyPoint3x4(new Vector3(-0.5f, 1f, 0f));
                        _outlineWorldPts[4] = worldMatrix.MultiplyPoint3x4(new Vector3( 0.0f, 0f, 0f));
                        _outlineWorldPts[5] = worldMatrix.MultiplyPoint3x4(new Vector3( 0.0f, 1f, 0f));

                        Color outlineColor = Color.cyan;
                        Color centerColor  = Color.yellow;

                        // 4 quad edges (white/cyan outline)
                        Debug.DrawLine(_outlineWorldPts[0], _outlineWorldPts[1], outlineColor); // tail edge
                        Debug.DrawLine(_outlineWorldPts[2], _outlineWorldPts[3], outlineColor); // head edge
                        Debug.DrawLine(_outlineWorldPts[0], _outlineWorldPts[3], outlineColor); // left edge
                        Debug.DrawLine(_outlineWorldPts[1], _outlineWorldPts[2], outlineColor); // right edge

                        // Centerline (yellow) — compare this against the debug hold rail.
                        // They should overlap when frustum heights are correctly matched.
                        Debug.DrawLine(_outlineWorldPts[4], _outlineWorldPts[5], centerColor);
                    }
                }

                Graphics.DrawMesh(
                    _quadMesh, worldMatrix, holdMaterial,
                    gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Hold endpoint radii
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes headR (outer) and tailR (inner) in PlayfieldLocal units.
        /// Consumption: headR pins to judgementR once chartTime ≥ startTimeMs (Clamp01 natural behaviour).
        /// visible = false when note is not on screen or tail has cleared the miss window.
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

            if (headToHit > noteLeadTimeMs)
            {
                // Head not yet on screen — whole ribbon invisible.
                headR = spawnR; tailR = spawnR; visible = false; return;
            }

            if (tailToHit < -greatWindowMs)
            {
                // Tail has cleared the miss window — ribbon done.
                headR = judgementR; tailR = judgementR; visible = false; return;
            }

            // headApproachParam: when headToHit ≤ 0, Clamp01 gives alpha=1 → headR = judgementR (pinned).
            headR   = ComputeApproachR((float)headToHit, spawnR, judgementR);
            tailR   = ComputeApproachR((float)tailToHit, spawnR, judgementR);
            visible = true;
        }

        /// <summary>
        /// Maps time-to-event to a local radius (spec §6.1).
        /// alpha = 1 − Clamp01(timeToHitMs / noteLeadTimeMs)
        /// r     = Lerp(spawnR, judgementR, alpha)
        /// </summary>
        private float ComputeApproachR(float timeToHitMs, float spawnR, float judgementR)
        {
            float alpha = (noteLeadTimeMs > 0)
                ? 1f - Mathf.Clamp01(timeToHitMs / noteLeadTimeMs)
                : 1f;
            return Mathf.Lerp(spawnR, judgementR, alpha);
        }

        // -------------------------------------------------------------------
        // Frustum Z helper — matches PlayerDebugRenderer.VisualOnlyLocalZ
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes the PlayfieldRoot local Z for a ribbon endpoint at radius <paramref name="r"/>.
        ///
        /// Replicates <c>PlayerDebugRenderer.VisualOnlyLocalZ(s01)</c> exactly:
        /// <code>
        ///   s01    = Clamp01( (r − innerLocal) / (outerLocal − innerLocal) )
        ///   localZ = Lerp( frustumHeightInner, frustumHeightOuter, s01 )
        /// </code>
        ///
        /// When <paramref name="arenaSurface"/> is assigned its values are used automatically,
        /// keeping the ribbon in sync with <see cref="PlayerDebugArenaSurface"/> without manual entry.
        /// Falls back to the inspector <c>useFrustumProfile</c> / <c>frustumHeightInner/Outer</c>
        /// fields when arenaSurface is null.
        /// When the frustum profile is disabled, returns <c>surfaceOffsetLocal</c>.
        /// </summary>
        private float ComputeEndpointLocalZ(float r, float innerLocal, float outerLocal)
        {
            // Determine whether to use the frustum profile at all.
            bool useProfile = (arenaSurface != null)
                ? arenaSurface.UseFrustumProfile
                : useFrustumProfile;

            if (!useProfile)
            {
                // Flat ribbon: tiny constant lift just above the interaction plane.
                return surfaceOffsetLocal;
            }

            // Read frustum heights — prefer live values from arenaSurface.
            float hInner = (arenaSurface != null) ? arenaSurface.FrustumHeightInner : frustumHeightInner;
            float hOuter = (arenaSurface != null) ? arenaSurface.FrustumHeightOuter : frustumHeightOuter;

            // s01: normalised band position (0 = inner edge, 1 = outer edge).
            float s01 = (outerLocal > innerLocal)
                ? Mathf.Clamp01((r - innerLocal) / (outerLocal - innerLocal))
                : 1f;

            return Mathf.Lerp(hInner, hOuter, s01);
        }

        // -------------------------------------------------------------------
        // Unit strip mesh
        // -------------------------------------------------------------------

        // Centered unit strip in XY:
        //   X ∈ [−0.5, +0.5]  — width axis (tangent)
        //   Y ∈ [0,    1  ]   — length axis (tail at Y=0, head at Y=1)
        //   Z = 0              — vertices are in the XY plane
        //
        // The origin is at the TAIL (Y=0). The matrix translation column (Col 3)
        // positions the tail; Col 1 (segment vector) extends to the head.
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

            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
