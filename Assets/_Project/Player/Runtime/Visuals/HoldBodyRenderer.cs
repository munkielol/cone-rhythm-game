// HoldBodyRenderer.cs
// Runtime visual renderer for Hold note body ribbons.
//
// For each active hold note, draws a stretched quad mesh ("ribbon") between
// the hold head position and the hold tail position, both computed using the
// same approach formula as PlayerDebugRenderer (spec §6.1).
//
// Approach formula (per endpoint):
//   approachFrac = Clamp01(1 − timeToHitMs / noteLeadTimeMs)
//   r = Lerp(innerLocal, outerLocal, spawnRadiusFactor + (1 − spawnRadiusFactor) × approachFrac)
//
// When HoldBind == Bound (touch held), the head is clamped to outerLocal
// (i.e. approach complete — head is at the judgement ring and does not retreat).
//
// The ribbon is drawn in lane-center direction (thetaDeg = lane.CenterDeg).
// Width = rMid × lane.WidthDeg × Deg2Rad × holdLaneWidthRatio (arc length at midpoint).
//
// Rendering uses Graphics.DrawMesh with a procedural unit quad — no GameObjects per note,
// no per-frame allocation (one MaterialPropertyBlock reused each draw).
//
// Spec §5.7.1.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Runtime visual ribbon renderer for Hold note bodies.
    /// Add to any GameObject in the Player scene; assign PlayerAppController and a Hold material.
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
        [SerializeField] private Color holdColor = new Color(0.3f, 0.6f, 1f, 0.8f);

        [Header("Approach")]
        [Tooltip("How many ms before StartTimeMs the ribbon becomes visible (should match debug renderer).")]
        [SerializeField] private int noteLeadTimeMs = 2000;

        [Tooltip("Spawn radius as a fraction of band width from the inner edge (0=inner, 1=outer).")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnRadiusFactor = 0.25f;

        [Header("Ribbon Sizing")]
        [Tooltip("Fraction of the arc-length lane width to use for ribbon width (1 = full lane).")]
        [Range(0.1f, 1f)]
        [SerializeField] private float holdLaneWidthRatio = 0.7f;

        // -------------------------------------------------------------------
        // Unit quad mesh (built once, destroyed on component destruction)
        // -------------------------------------------------------------------

        private Mesh _quadMesh;

        // Reused each DrawMesh call to set per-instance color without material instancing.
        // Initialized in Awake — Unity prohibits MaterialPropertyBlock construction in field initializers.
        private MaterialPropertyBlock _propBlock;

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
            if (_quadMesh != null)
            {
                Destroy(_quadMesh);
                _quadMesh = null;
            }
        }

        private void LateUpdate()
        {
            if (playerAppController == null || _quadMesh == null || holdMaterial == null)
            {
                return;
            }

            var allNotes = playerAppController.NotesAll;
            if (allNotes == null) { return; }

            var arenaGeos   = playerAppController.ArenaGeometries;
            var laneGeos    = playerAppController.LaneGeometries;
            var laneToArena = playerAppController.LaneToArena;
            var pfTf        = playerAppController.PlayfieldTf;
            Transform pfRoot = playerAppController.playfieldRoot;

            if (arenaGeos == null || laneGeos == null || laneToArena == null
                || pfTf == null || pfRoot == null)
            {
                return;
            }

            double chartTimeMs = playerAppController.EffectiveChartTimeMs;

            _propBlock.SetColor("_Color", holdColor);

            foreach (RuntimeNote note in allNotes)
            {
                if (note.Type != NoteType.Hold) { continue; }

                // Do not draw resolved holds.
                if (note.State == NoteState.Hit || note.State == NoteState.Missed) { continue; }

                // Cull holds whose tail has already passed the screen.
                double tailToHitMs = note.EndTimeMs   - chartTimeMs;
                double headToHitMs = note.StartTimeMs - chartTimeMs;
                if (tailToHitMs < -200.0)         { continue; } // 200 ms grace after tail crosses judgement ring
                if (headToHitMs > noteLeadTimeMs)  { continue; } // head not yet on screen

                // Look up geometry.
                if (!laneGeos.TryGetValue(note.LaneId,  out LaneGeometry  lane))   { continue; }
                if (!laneToArena.TryGetValue(note.LaneId, out string arenaId))     { continue; }
                if (!arenaGeos.TryGetValue(arenaId,     out ArenaGeometry arena))  { continue; }

                // Compute chart band radii in PlayfieldLocal units.
                float outerLocal = pfTf.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfTf.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                // Approach fractions: 0 = note at spawn radius, 1 = note at outerLocal (judgement).
                float headFrac = Mathf.Clamp01(1f - (float)(headToHitMs / noteLeadTimeMs));
                float tailFrac = Mathf.Clamp01(1f - (float)(tailToHitMs / noteLeadTimeMs));

                // Head radius: once the hold is bound (touch held), clamp head at outerLocal.
                float headR = (note.HoldBind == HoldBindState.Bound)
                    ? outerLocal
                    : Mathf.Lerp(innerLocal, outerLocal,
                        spawnRadiusFactor + (1f - spawnRadiusFactor) * headFrac);

                float tailR = Mathf.Lerp(innerLocal, outerLocal,
                    spawnRadiusFactor + (1f - spawnRadiusFactor) * tailFrac);

                // Degenerate ribbon: skip.
                if (Mathf.Abs(tailR - headR) < 0.0001f) { continue; }

                // Arena center in local XY.
                Vector2 centerLocal = pfTf.NormalizedToLocal(
                    new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // Lane center angle: radial direction of the ribbon.
                float thetaRad = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                float cosT = Mathf.Cos(thetaRad);
                float sinT = Mathf.Sin(thetaRad);

                // Head and tail positions in PlayfieldRoot local space.
                var headLocal3 = new Vector3(centerLocal.x + headR * cosT,
                                             centerLocal.y + headR * sinT, 0f);
                var tailLocal3 = new Vector3(centerLocal.x + tailR * cosT,
                                             centerLocal.y + tailR * sinT, 0f);

                // Convert to world space.
                Vector3 headWorld = pfRoot.TransformPoint(headLocal3);
                Vector3 tailWorld = pfRoot.TransformPoint(tailLocal3);

                float ribbonLength = (headWorld - tailWorld).magnitude;
                if (ribbonLength < 0.0001f) { continue; }

                // Ribbon width: arc length at midpoint radius × ratio.
                float rMid = (headR + tailR) * 0.5f;
                // Local arc length → world arc length (assumes uniform scale on x axis).
                float ribbonWidthLocal = rMid * lane.WidthDeg * Mathf.Deg2Rad * holdLaneWidthRatio;
                float ribbonWidth      = ribbonWidthLocal * pfRoot.lossyScale.x;

                // Build world-space ribbon axes.
                Vector3 worldDir  = (headWorld - tailWorld).normalized; // radially outward
                Vector3 worldNorm = pfRoot.TransformDirection(Vector3.forward);
                Vector3 worldTang = Vector3.Cross(worldNorm, worldDir).normalized;
                Vector3 midWorld  = (headWorld + tailWorld) * 0.5f;

                // Construct matrix: unit quad [-0.5,0.5]×[-0.5,0.5] is scaled to ribbon dimensions.
                // Column 0 → tangent axis × width
                // Column 1 → radial-outward axis × length
                // Column 2 → surface normal (no scale)
                // Column 3 → world center position
                var matrix = new Matrix4x4(
                    new Vector4(worldTang.x * ribbonWidth,  worldTang.y * ribbonWidth,
                                worldTang.z * ribbonWidth,  0f),
                    new Vector4(worldDir.x  * ribbonLength, worldDir.y  * ribbonLength,
                                worldDir.z  * ribbonLength, 0f),
                    new Vector4(worldNorm.x,  worldNorm.y,  worldNorm.z, 0f),
                    new Vector4(midWorld.x,   midWorld.y,   midWorld.z,  1f)
                );

                Graphics.DrawMesh(_quadMesh, matrix, holdMaterial, gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Unit quad mesh construction
        // -------------------------------------------------------------------

        // Builds a centered unit quad in local XY, spanning [-0.5,0.5] × [-0.5,0.5].
        // Destroyed in OnDestroy — not shared across instances.
        private static Mesh BuildUnitQuad()
        {
            var mesh = new Mesh { name = "HoldBodyQuad" };

            mesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, 0f),  // 0 bottom-left  (tail-left)
                new Vector3( 0.5f, -0.5f, 0f),  // 1 bottom-right (tail-right)
                new Vector3( 0.5f,  0.5f, 0f),  // 2 top-right    (head-right)
                new Vector3(-0.5f,  0.5f, 0f),  // 3 top-left     (head-left)
            };

            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };

            // Two triangles for a quad, both faces visible requires:
            // CW for front, and add CCW for back — or use a double-sided shader.
            // Single-sided (front-face CW from camera perspective on flat Z=0 plane):
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }
    }
}
