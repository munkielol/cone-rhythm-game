// FlickNoteRenderer.cs
// Production renderer for Flick note heads.
//
// ── Architecture ─────────────────────────────────────────────────────────────
//
//  Centralized renderer: NOT attached to individual notes. Reads the global
//  note list from PlayerAppController and draws one mesh per visible Flick note
//  each LateUpdate — the same pattern as HoldBodyRenderer.
//
// ── Geometry (v0 step 1) ─────────────────────────────────────────────────────
//
//  Current step: single-segment trapezoid (spec §5.7.0 step 1).
//  The note head spans [r − noteHalfThicknessLocal, r + noteHalfThicknessLocal]
//  radially, centred on the approach radius r.
//
//  Width is lane-width-aware:
//    width(r) = 2 · r · sin(halfWidthDeg) · noteLaneWidthRatio
//  Both inner and outer edges use their own radius so the head is a true
//  trapezoid (wider at outerR, narrower at innerR).
//
//  Z placement is frustum-conforming:
//    z = NoteApproachMath.FrustumZAtRadius(r, innerLocal, outerLocal, hInner, hOuter)
//
// ── Flick arrow overlay ───────────────────────────────────────────────────────
//
//  NOT included in this step. Arrow direction billboard (spec §5.7.2) will be
//  added in a future step as a separate overlay pass once the base trapezoid
//  geometry is verified. Arrow directions follow spec §5.7.2 / §7.3.1:
//    U = radialIn (−cosθ, −sinθ)   D = radialOut (+cosθ, +sinθ)
//    L = CW tangent (+sinθ, −cosθ) R = CCW tangent (−sinθ, +cosθ)
//
// ── Future steps (do not implement here) ─────────────────────────────────────
//
//  Step 2: Add flick arrow overlay billboard per spec §5.7.2.
//          Extract BuildNoteHeadVertices() to shared geometry builder;
//          replace single-segment trapezoid with N-segment curved-cap.
//  Step 3: Assign UV layout on the mesh for fixed-edge + tiled-center texture
//          mapping (spec §5.7.3).  Replace placeholder color with NoteSkinSet.
//
// ── This step ────────────────────────────────────────────────────────────────
//
//  Geometry only. Placeholder color applied via MaterialPropertyBlock._Color.
//  No arrow overlay, no NoteSkinSet, no PNG skin, no UV layout.
//
// Spec §5.7.a / §5.7.0.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production Flick note head renderer. Geometry-first (v0 step 1):
    /// lane-width-aware, frustum-conforming single-segment trapezoid.
    /// Flick arrow overlay is NOT included in this step (see file header).
    /// Assign a simple unlit material with <c>_Color</c> support.
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/FlickNoteRenderer")]
    public class FlickNoteRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector — Wiring
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("The PlayerAppController this renderer reads notes and geometry from.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Unlit material with _Color property support. " +
                 "Replace with texture-driven flick skin material in a later step.")]
        [SerializeField] private Material flickMaterial;

        // -------------------------------------------------------------------
        // Inspector — Frustum surface alignment (mirrors HoldBodyRenderer)
        // -------------------------------------------------------------------

        [Header("Frustum Surface Alignment")]
        [Tooltip("Optional: when assigned, frustum heights are read from this component " +
                 "so flick heads match the hold ribbon depth profile exactly. " +
                 "If null, the manual values below are used.")]
        [SerializeField] private PlayerDebugArenaSurface arenaSurface;

        [Tooltip("When true (and no arenaSurface assigned), flick heads are lifted onto " +
                 "the frustum cone surface. False = flat Z at surfaceOffsetLocal.")]
        [SerializeField] private bool useFrustumProfile = true;

        [Tooltip("Local Z at inner ring edge. Only used when arenaSurface is null. Default: 0.001.")]
        [SerializeField] private float frustumHeightInner = 0.001f;

        [Tooltip("Local Z at outer ring edge. Only used when arenaSurface is null. Default: 0.15.")]
        [SerializeField] private float frustumHeightOuter = 0.15f;

        [Tooltip("Flat Z offset (no frustum profile). Prevents z-fighting. Default: 0.002.")]
        [SerializeField] private float surfaceOffsetLocal = 0.002f;

        // -------------------------------------------------------------------
        // Inspector — Approach
        // -------------------------------------------------------------------

        [Header("Approach")]
        [Tooltip("How many ms before the note's hit time it first becomes visible. " +
                 "Must match HoldBodyRenderer.noteLeadTimeMs. Default: 2000.")]
        [SerializeField] private int noteLeadTimeMs = 2000;

        [Tooltip("Spawn radius as fraction of approach path (0 = inner arc, 1 = judgement ring). " +
                 "Keep 0 for v0 to match hold ribbon and debug renderer behaviour.")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnRadiusFactor = 0f;

        // -------------------------------------------------------------------
        // Inspector — Note head sizing
        // -------------------------------------------------------------------

        [Header("Note Head Sizing")]
        [Tooltip("Radial half-thickness of the flick head trapezoid in PlayfieldLocal units. " +
                 "The head spans [r − half, r + half] radially. Default: 0.022.")]
        [SerializeField] private float noteHalfThicknessLocal = 0.022f;

        [Tooltip("Note head width as a fraction of the lane chord width at the note's radius. " +
                 "1.0 = full lane width. Default: 0.9.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float noteLaneWidthRatio = 0.9f;

        // -------------------------------------------------------------------
        // Inspector — Placeholder color
        // -------------------------------------------------------------------

        [Header("Placeholder Color")]
        [Tooltip("Base color applied via MaterialPropertyBlock._Color for active flick notes. " +
                 "Replace with texture-driven NoteSkinSet in a later step.")]
        [SerializeField] private Color flickColor = new Color(1f, 0.65f, 0.1f, 0.95f);

        [Tooltip("Color applied to Missed flick notes (dim ghost visible until past miss window).")]
        [SerializeField] private Color missedTintColor = new Color(0.4f, 0.4f, 0.4f, 0.55f);

        // -------------------------------------------------------------------
        // Inspector — Debug geometry verification
        // -------------------------------------------------------------------

        [Header("Debug — Geometry Verification")]
        [Tooltip("Draw note head outline in world space (blue lines: left edge, right edge; " +
                 "white line: centerline). Use to verify frustum placement and radial sizing.")]
        [SerializeField] private bool debugDrawNoteOutline = false;

        [Tooltip("Draw lane boundaries at the note's approach radius (yellow lines at " +
                 "centerDeg ± widthDeg/2). Compare against note outline to verify " +
                 "lane occupancy during animated lane width / angle / radius tests.")]
        [SerializeField] private bool debugDrawLaneBoundary = false;

        // -------------------------------------------------------------------
        // Internals — mesh pool
        // -------------------------------------------------------------------

        // One mesh per simultaneously visible Flick note. Vertices overwritten
        // in-place each LateUpdate — zero per-frame GC allocation.
        private const int MaxNotePool = 128;

        private Mesh[]  _meshPool;
        private int     _poolUsed;

        // Shared vertex scratch — written then copied into pooled mesh each note.
        private readonly Vector3[] _vertScratch = new Vector3[4];

        // Pre-allocated property block — reused every DrawMesh call.
        private MaterialPropertyBlock _propBlock;

        // Guard: log a warning at most once if the Inspector reference is missing.
        private bool _hasWarnedMissingController;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            _meshPool = new Mesh[MaxNotePool];
            for (int i = 0; i < MaxNotePool; i++)
            {
                _meshPool[i] = BuildTrapezoidMesh("FlickNoteTrapezoid");
            }
            _propBlock = new MaterialPropertyBlock();
        }

        private void OnDestroy()
        {
            if (_meshPool == null) { return; }
            for (int i = 0; i < _meshPool.Length; i++)
            {
                if (_meshPool[i] != null) { Destroy(_meshPool[i]); _meshPool[i] = null; }
            }
        }

        private void LateUpdate()
        {
            // Require wiring. Missing controller = warn once; missing material = skip silently
            // (material may not be assigned yet in a fresh scene setup).
            if (playerAppController == null)
            {
                if (!_hasWarnedMissingController)
                {
                    _hasWarnedMissingController = true;
                    Debug.LogWarning(
                        "[FlickNoteRenderer] PlayerAppController is not assigned. " +
                        "Add this component to a scene GameObject and assign the " +
                        "PlayerAppController reference in the Inspector.", this);
                }
                return;
            }
            if (flickMaterial == null || _meshPool == null) { return; }

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

            // localToWorld: vertices are in PlayfieldRoot local space; this matrix
            // promotes them to world space for Graphics.DrawMesh.
            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            // Precompute frustum heights once per frame (same for all notes).
            float hInner = ReadFrustumHeightInner();
            float hOuter = ReadFrustumHeightOuter();

            _poolUsed = 0;

            foreach (RuntimeNote note in allNotes)
            {
                // ── Type and state filter ─────────────────────────────────────────────────
                if (note.Type != NoteType.Flick) { continue; }
                if (note.State == NoteState.Hit) { continue; }  // successfully judged

                if (_poolUsed >= MaxNotePool) { break; } // pool full

                // ── Visibility window ────────────────────────────────────────────────────
                double timeToHit = note.PrimaryTimeMs - chartTimeMs;
                if (timeToHit > noteLeadTimeMs)    { continue; } // not yet on screen
                if (timeToHit < -(double)greatWindowMs) { continue; } // past miss window

                // ── Geometry lookup ──────────────────────────────────────────────────────
                if (!laneGeos.TryGetValue(note.LaneId,    out LaneGeometry  lane))  { continue; }
                if (!laneToArena.TryGetValue(note.LaneId, out string arenaId))      { continue; }
                if (!arenaGeos.TryGetValue(arenaId,       out ArenaGeometry arena)) { continue; }

                // ── Band radii (PlayfieldLocal units) ────────────────────────────────────
                float outerLocal = pfTf.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfTf.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                float judgementR = NoteApproachMath.JudgementRadius(
                    outerLocal, pfTf.MinDimLocal, PlayerSettingsStore.JudgementInsetNorm);
                float spawnR = NoteApproachMath.SpawnRadius(innerLocal, judgementR, spawnRadiusFactor);

                // ── Approach radius at this frame ────────────────────────────────────────
                float r = NoteApproachMath.ApproachRadius(
                    (float)timeToHit, noteLeadTimeMs, spawnR, judgementR);

                // ── Note head radial extent ──────────────────────────────────────────────
                // The head is a thin band centred on r, spanning [tailR, headR] radially.
                // Clamped so it never extends past spawn or judgement ring.
                float headR = Mathf.Min(r + noteHalfThicknessLocal, judgementR);
                float tailR = Mathf.Max(r - noteHalfThicknessLocal, spawnR);
                if (headR - tailR < 0.0001f) { continue; } // degenerate — skip

                // ── Color (placeholder — replaced with NoteSkinSet in a later step) ──────
                Color noteColor = (note.State == NoteState.Missed) ? missedTintColor : flickColor;
                _propBlock.SetColor("_Color", noteColor);

                // ── Local-space axes ─────────────────────────────────────────────────────
                float thetaRad = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                float cosT     = Mathf.Cos(thetaRad);
                float sinT     = Mathf.Sin(thetaRad);

                // tangLocal: 90° CCW from radial direction in local XY — the width axis.
                var tangLocal = new Vector3(-sinT, cosT, 0f);

                // ── 3D endpoints (local XY + frustum-lifted Z) ───────────────────────────
                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                var tailLocal3 = new Vector3(
                    ctr.x + tailR * cosT,
                    ctr.y + tailR * sinT,
                    NoteApproachMath.FrustumZAtRadius(tailR, innerLocal, outerLocal, hInner, hOuter));

                var headLocal3 = new Vector3(
                    ctr.x + headR * cosT,
                    ctr.y + headR * sinT,
                    NoteApproachMath.FrustumZAtRadius(headR, innerLocal, outerLocal, hInner, hOuter));

                // ── Trapezoid width (lane-width-aware per spec §5.7.0) ───────────────────
                //
                // ── FUTURE: segmented curved-cap upgrade goes here ───────────────────────
                // Replace BuildSingleSegmentVertices() with BuildCurvedCapVertices(nSegments)
                // which subdivides each cap edge into N arc segments. All other geometry
                // (frustum Z, approach radius, width ratio) stays identical.
                // ────────────────────────────────────────────────────────────────────────

                float halfWidthDeg = lane.WidthDeg * 0.5f;
                float widthHead = NoteApproachMath.LaneChordWidthAtRadius(headR, halfWidthDeg) * noteLaneWidthRatio;
                float widthTail = NoteApproachMath.LaneChordWidthAtRadius(tailR, halfWidthDeg) * noteLaneWidthRatio;

                // Vertex layout (matches HoldBodyRenderer / BuildTrapezoidMesh):
                //   [0] tail-left   [1] tail-right
                //   [3] head-left   [2] head-right
                _vertScratch[0] = tailLocal3 - tangLocal * (widthTail * 0.5f); // tail-left
                _vertScratch[1] = tailLocal3 + tangLocal * (widthTail * 0.5f); // tail-right
                _vertScratch[2] = headLocal3 + tangLocal * (widthHead * 0.5f); // head-right
                _vertScratch[3] = headLocal3 - tangLocal * (widthHead * 0.5f); // head-left

                Mesh mesh = _meshPool[_poolUsed++];
                mesh.vertices = _vertScratch;
                mesh.RecalculateBounds();

                Graphics.DrawMesh(mesh, localToWorld, flickMaterial,
                    gameObject.layer, null, 0, _propBlock);

                // ── Debug geometry verification (no GC — only Debug.DrawLine) ────────────
                if (debugDrawNoteOutline || debugDrawLaneBoundary)
                {
                    DrawDebugGeometry(pfRoot, ctr, r, tailR, headR, innerLocal, outerLocal,
                        lane.CenterDeg, halfWidthDeg, tangLocal, tailLocal3, headLocal3);
                }
            }
        }

        // -------------------------------------------------------------------
        // Debug geometry draw (no allocations — only Debug.DrawLine)
        // -------------------------------------------------------------------

        /// <summary>
        /// Draws note outline and/or lane boundary lines in world space.
        /// Called only when the corresponding debug toggle is enabled.
        /// No allocations: uses only Debug.DrawLine which is allocation-free.
        /// </summary>
        private void DrawDebugGeometry(
            Transform pfRoot,
            Vector2   ctr,
            float     centerR,       // note center radius (for lane boundary ticks)
            float     tailR,
            float     headR,
            float     innerLocal,
            float     outerLocal,
            float     laneCenterDeg, // lane.CenterDeg — needed for left/right edge angles
            float     halfWidthDeg,
            Vector3   tangLocal,
            Vector3   tailLocal3,
            Vector3   headLocal3)
        {
            if (debugDrawNoteOutline)
            {
                // ── Note head outline ─────────────────────────────────────────────────────
                // Recompute corners in world space for drawing (uses last-written _vertScratch).
                Vector3 p0 = pfRoot.TransformPoint(_vertScratch[0]); // tail-left
                Vector3 p1 = pfRoot.TransformPoint(_vertScratch[1]); // tail-right
                Vector3 p2 = pfRoot.TransformPoint(_vertScratch[2]); // head-right
                Vector3 p3 = pfRoot.TransformPoint(_vertScratch[3]); // head-left

                Color outlineColor = Color.blue;
                // Left edge (tail-left to head-left) — verifies frustum Z lift along this edge
                Debug.DrawLine(p0, p3, outlineColor);
                // Right edge (tail-right to head-right) — verifies radial thickness
                Debug.DrawLine(p1, p2, outlineColor);
                // Tail edge (tail-left to tail-right) — verifies inner note width
                Debug.DrawLine(p0, p1, Color.cyan);
                // Head edge (head-left to head-right) — verifies outer note width
                Debug.DrawLine(p3, p2, Color.cyan);
                // Centerline (tail center to head center) — track approach path
                Debug.DrawLine(pfRoot.TransformPoint(tailLocal3), pfRoot.TransformPoint(headLocal3), Color.white);
            }

            if (debugDrawLaneBoundary)
            {
                // ── Lane edge ticks at note center radius ─────────────────────────────────
                // Draw short arcs at the left and right lane edges at centerR.
                // Compare against note outline to verify noteLaneWidthRatio occupancy.
                float leftEdgeDeg  = AngleUtil.Normalize360(laneCenterDeg - halfWidthDeg);
                float rightEdgeDeg = AngleUtil.Normalize360(laneCenterDeg + halfWidthDeg);

                // Left lane edge tick (yellow)
                DrawArcTick(pfRoot, ctr, centerR, leftEdgeDeg,  innerLocal, outerLocal, Color.yellow);
                // Right lane edge tick (yellow)
                DrawArcTick(pfRoot, ctr, centerR, rightEdgeDeg, innerLocal, outerLocal, Color.yellow);
            }
        }

        // -------------------------------------------------------------------
        // Debug draw helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Draws a short arc tick at the given angle and radius to mark a lane edge.
        /// Allocation-free: only Debug.DrawLine calls.
        /// </summary>
        private static void DrawArcTick(
            Transform pfRoot,
            Vector2   center,
            float     radius,
            float     angleDeg,
            float     innerLocal,
            float     outerLocal,
            Color     color)
        {
            const float HalfTickDeg = 2f;
            float span  = (outerLocal > innerLocal) ? (outerLocal - innerLocal) : 1f;
            float s01   = Mathf.Clamp01((radius - innerLocal) / span);
            float localZ = Mathf.Lerp(0.001f, 0.15f, s01);

            int    segments = 4;
            float  startDeg = angleDeg - HalfTickDeg;
            float  step     = (HalfTickDeg * 2f) / segments;
            float  a0       = startDeg * Mathf.Deg2Rad;
            Vector3 prev    = pfRoot.TransformPoint(
                center.x + radius * Mathf.Cos(a0),
                center.y + radius * Mathf.Sin(a0),
                localZ);
            for (int i = 1; i <= segments; i++)
            {
                float   a    = (startDeg + i * step) * Mathf.Deg2Rad;
                Vector3 curr = pfRoot.TransformPoint(
                    center.x + radius * Mathf.Cos(a),
                    center.y + radius * Mathf.Sin(a),
                    localZ);
                Debug.DrawLine(prev, curr, color);
                prev = curr;
            }
        }

        // -------------------------------------------------------------------
        // Frustum height helpers
        // -------------------------------------------------------------------

        private float ReadFrustumHeightInner()
        {
            if (arenaSurface != null && arenaSurface.UseFrustumProfile) { return arenaSurface.FrustumHeightInner; }
            return useFrustumProfile ? frustumHeightInner : surfaceOffsetLocal;
        }

        private float ReadFrustumHeightOuter()
        {
            if (arenaSurface != null && arenaSurface.UseFrustumProfile) { return arenaSurface.FrustumHeightOuter; }
            return useFrustumProfile ? frustumHeightOuter : surfaceOffsetLocal;
        }

        // -------------------------------------------------------------------
        // Trapezoid mesh template
        // -------------------------------------------------------------------

        /// <summary>
        /// Builds a 4-vertex mesh template with stable triangle topology and UVs.
        /// Vertices are placeholder zeros — LateUpdate overwrites them each frame.
        /// UV layout: [0](0,0) [1](1,0) [2](1,1) [3](0,1) — ready for future texture-driven skins.
        /// </summary>
        private static Mesh BuildTrapezoidMesh(string meshName)
        {
            var mesh = new Mesh { name = meshName };
            mesh.vertices  = new Vector3[4];
            mesh.uv        = new Vector2[]
            {
                new Vector2(0f, 0f),  // [0] tail-left  — future: left border start
                new Vector2(1f, 0f),  // [1] tail-right — future: right border start
                new Vector2(1f, 1f),  // [2] head-right — future: right border end
                new Vector2(0f, 1f),  // [3] head-left  — future: left border end
            };
            // Two CCW triangles covering the quad.
            mesh.triangles = new int[] { 0, 1, 2,  0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
