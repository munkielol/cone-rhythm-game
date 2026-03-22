// CatchNoteRenderer.cs
// Production renderer for Catch note heads.
//
// ── Architecture ─────────────────────────────────────────────────────────────
//
//  Centralized renderer: NOT attached to individual notes. Reads the global
//  note list from PlayerAppController and draws one mesh per visible Catch note
//  each LateUpdate — the same pattern as HoldBodyRenderer.
//
// ── Geometry (v0 step 2: segmented curved-cap) ───────────────────────────────
//
//  Replaces the single-segment trapezoid (step 1) with a curved cap that
//  arc-samples across the note's angular span using NoteCapGeometryBuilder.
//
//  ColumnCount = 5 columns → 6 column boundaries → 10 triangles per note.
//  The note body visibly follows the lane arc rather than using a straight
//  chord approximation.
//
//  Key invariants (unchanged from step 1):
//    - Lane-width-aware: noteHalfAngleDeg = laneHalfWidthDeg × noteLaneWidthRatio
//    - Frustum-conforming: Z per row = NoteApproachMath.FrustumZAtRadius(r, …)
//    - Centered on the lane at approach radius r
//    - Stable under lane centerDeg / widthDeg / arenaRadius animation
//    - Wrap-safe: cos/sin handle angles outside [0, 360) naturally
//
// ── Future steps (do not implement here) ─────────────────────────────────────
//
//  Step 3: Assign UV regions on the cap mesh for fixed-edge + tiled-center
//          texture mapping (spec §5.7.3). Replace placeholder color with
//          NoteSkinSet — geometry pipeline stays the same.
//
// ── This step ────────────────────────────────────────────────────────────────
//
//  Geometry only. Placeholder color applied via MaterialPropertyBlock._Color.
//  No NoteSkinSet, no PNG skin, no UV region assignment.
//
// Spec §5.7.a / §5.7.0 step 2.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production Catch note head renderer. Geometry step 2:
    /// lane-width-aware, frustum-conforming segmented curved-cap
    /// (NoteCapGeometryBuilder, ColumnCount = 5).
    /// Assign a simple unlit material with <c>_Color</c> property support.
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/CatchNoteRenderer")]
    public class CatchNoteRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector — Wiring
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("The PlayerAppController this renderer reads notes and geometry from.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Unlit material with _Color property support. " +
                 "Replace with texture-driven catch skin material in a later step.")]
        [SerializeField] private Material catchMaterial;

        // -------------------------------------------------------------------
        // Inspector — Frustum surface alignment (mirrors HoldBodyRenderer)
        // -------------------------------------------------------------------

        [Header("Frustum Surface Alignment")]
        [Tooltip("Optional: when assigned, frustum heights are read from this component " +
                 "so catch heads match the hold ribbon depth profile exactly. " +
                 "If null, the manual values below are used.")]
        [SerializeField] private PlayerDebugArenaSurface arenaSurface;

        [Tooltip("When true (and no arenaSurface assigned), catch heads are lifted onto " +
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
        [Tooltip("Radial half-thickness of the catch head band in PlayfieldLocal units. " +
                 "The head spans [r − half, r + half] radially. Default: 0.022.")]
        [SerializeField] private float noteHalfThicknessLocal = 0.022f;

        [Tooltip("Note head width as a fraction of the lane angular span at the note's radius. " +
                 "1.0 = full lane width. Default: 0.9.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float noteLaneWidthRatio = 0.9f;

        // -------------------------------------------------------------------
        // Inspector — Placeholder color
        // -------------------------------------------------------------------

        [Header("Placeholder Color")]
        [Tooltip("Base color applied via MaterialPropertyBlock._Color for active catch notes. " +
                 "Replace with texture-driven NoteSkinSet in a later step.")]
        [SerializeField] private Color catchColor = new Color(0.3f, 1f, 0.4f, 0.95f);

        [Tooltip("Color applied to Missed catch notes (dim ghost visible until past miss window).")]
        [SerializeField] private Color missedTintColor = new Color(0.4f, 0.4f, 0.4f, 0.55f);

        // -------------------------------------------------------------------
        // Inspector — Debug geometry verification
        // -------------------------------------------------------------------

        [Header("Debug — Geometry Verification")]
        [Tooltip("Draw note cap edges in world space:\n" +
                 "  cyan  — tail arc + head arc (shows curved following)\n" +
                 "  blue  — left/right radial edges\n" +
                 "  white — centre radial sample line\n" +
                 "Use to verify frustum placement and arc-following.")]
        [SerializeField] private bool debugDrawNoteOutline = false;

        [Tooltip("Draw lane and note boundary ticks at the note's approach radius:\n" +
                 "  yellow — full lane left/right edges (centerDeg ± widthDeg/2)\n" +
                 "  green  — note left/right edges (centerDeg ± noteHalfAngleDeg)\n" +
                 "Compare yellow vs green to verify noteLaneWidthRatio occupancy.")]
        [SerializeField] private bool debugDrawLaneBoundary = false;

        // -------------------------------------------------------------------
        // Internals — mesh pool
        // -------------------------------------------------------------------

        // One mesh per simultaneously visible Catch note. Vertices overwritten
        // in-place each LateUpdate — zero per-frame GC allocation.
        private const int MaxNotePool = 128;

        private Mesh[] _meshPool;
        private int    _poolUsed;

        // Vertex scratch — length NoteCapGeometryBuilder.VertexCount (12).
        // Written by NoteCapGeometryBuilder.FillCapVertices then copied to
        // the pooled mesh via mesh.vertices = _vertScratch.
        private readonly Vector3[] _vertScratch = new Vector3[NoteCapGeometryBuilder.VertexCount];

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
                _meshPool[i] = NoteCapGeometryBuilder.BuildCapMesh("CatchNoteCurvedCap");
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
            // Require wiring. Missing controller = warn once; missing material = skip
            // silently (material may not be assigned yet in a fresh scene setup).
            if (playerAppController == null)
            {
                if (!_hasWarnedMissingController)
                {
                    _hasWarnedMissingController = true;
                    Debug.LogWarning(
                        "[CatchNoteRenderer] PlayerAppController is not assigned. " +
                        "Add this component to a scene GameObject and assign the " +
                        "PlayerAppController reference in the Inspector.", this);
                }
                return;
            }
            if (catchMaterial == null || _meshPool == null) { return; }

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

            // Precompute frustum heights once per frame — same for all notes this frame.
            float hInner = ReadFrustumHeightInner();
            float hOuter = ReadFrustumHeightOuter();

            _poolUsed = 0;

            foreach (RuntimeNote note in allNotes)
            {
                // ── Type and state filter ──────────────────────────────────────────────
                if (note.Type != NoteType.Catch) { continue; }
                if (note.State == NoteState.Hit) { continue; }  // successfully judged

                if (_poolUsed >= MaxNotePool) { break; } // pool exhausted

                // ── Visibility window ──────────────────────────────────────────────────
                double timeToHit = note.PrimaryTimeMs - chartTimeMs;
                if (timeToHit > noteLeadTimeMs)         { continue; } // not yet on screen
                if (timeToHit < -(double)greatWindowMs) { continue; } // past miss window

                // ── Geometry lookup ────────────────────────────────────────────────────
                if (!laneGeos.TryGetValue(note.LaneId,    out LaneGeometry  lane))  { continue; }
                if (!laneToArena.TryGetValue(note.LaneId, out string arenaId))      { continue; }
                if (!arenaGeos.TryGetValue(arenaId,       out ArenaGeometry arena)) { continue; }

                // ── Band radii (PlayfieldLocal units) ──────────────────────────────────
                float outerLocal = pfTf.NormRadiusToLocal(arena.OuterRadiusNorm);
                float bandLocal  = pfTf.NormRadiusToLocal(arena.BandThicknessNorm);
                float innerLocal = outerLocal - bandLocal;

                float judgementR = NoteApproachMath.JudgementRadius(
                    outerLocal, pfTf.MinDimLocal, PlayerSettingsStore.JudgementInsetNorm);
                float spawnR = NoteApproachMath.SpawnRadius(innerLocal, judgementR, spawnRadiusFactor);

                // ── Approach radius at this frame ──────────────────────────────────────
                float r = NoteApproachMath.ApproachRadius(
                    (float)timeToHit, noteLeadTimeMs, spawnR, judgementR);

                // ── Note head radial extent ────────────────────────────────────────────
                // The head is a thin band centred on r, spanning [tailR, headR] radially.
                // Clamped so it never extends past spawn or judgement ring.
                float headR = Mathf.Min(r + noteHalfThicknessLocal, judgementR);
                float tailR = Mathf.Max(r - noteHalfThicknessLocal, spawnR);
                if (headR - tailR < 0.0001f) { continue; } // degenerate — skip

                // ── Color (placeholder — replaced with NoteSkinSet in a later step) ────
                Color noteColor = (note.State == NoteState.Missed) ? missedTintColor : catchColor;
                _propBlock.SetColor("_Color", noteColor);

                // ── Arena centre in local XY ───────────────────────────────────────────
                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // ── Curved-cap vertex fill ─────────────────────────────────────────────
                // NoteCapGeometryBuilder.FillCapVertices arc-samples across the lane's
                // angular span, placing ColumnCount+1 column boundaries on the actual
                // arc (not a single chord). Each column boundary has a tail vertex and a
                // head vertex — the note body visibly follows the lane curve.
                float laneCenterDeg    = AngleUtil.Normalize360(lane.CenterDeg);
                float halfWidthDeg     = lane.WidthDeg * 0.5f;
                float noteHalfAngleDeg = NoteCapGeometryBuilder.NoteHalfAngleDeg(
                    halfWidthDeg, noteLaneWidthRatio);

                NoteCapGeometryBuilder.FillCapVertices(
                    _vertScratch,
                    ctr,
                    tailR,
                    headR,
                    laneCenterDeg,
                    noteHalfAngleDeg,
                    innerLocal,
                    outerLocal,
                    hInner,
                    hOuter);

                Mesh mesh = _meshPool[_poolUsed++];
                mesh.vertices = _vertScratch;
                mesh.RecalculateBounds();

                Graphics.DrawMesh(mesh, localToWorld, catchMaterial,
                    gameObject.layer, null, 0, _propBlock);

                // ── Debug geometry verification (no GC — only Debug.DrawLine) ──────────
                if (debugDrawNoteOutline || debugDrawLaneBoundary)
                {
                    DrawDebugGeometry(pfRoot, ctr, r, tailR, headR,
                        innerLocal, outerLocal,
                        laneCenterDeg, halfWidthDeg, noteHalfAngleDeg,
                        hInner, hOuter);
                }
            }
        }

        // -------------------------------------------------------------------
        // Debug geometry draw (no allocations — only Debug.DrawLine)
        // -------------------------------------------------------------------

        /// <summary>
        /// Draws note cap outline and/or lane+note boundary ticks in world space.
        /// Called only when a debug toggle is enabled.  No allocations.
        /// </summary>
        private void DrawDebugGeometry(
            Transform pfRoot,
            Vector2   ctr,
            float     centerR,          // note approach radius — for boundary ticks
            float     tailR,
            float     headR,
            float     innerLocal,
            float     outerLocal,
            float     laneCenterDeg,    // Normalize360 already applied
            float     halfWidthDeg,     // full lane half-width (for lane boundary ticks)
            float     noteHalfAngleDeg, // note angular half-span (for note boundary ticks)
            float     hInner,
            float     hOuter)
        {
            if (debugDrawNoteOutline)
            {
                // ── Tail arc (cyan): left→right across inner note edge ─────────────────
                for (int i = 0; i < NoteCapGeometryBuilder.ColumnCount; i++)
                {
                    Debug.DrawLine(
                        pfRoot.TransformPoint(_vertScratch[NoteCapGeometryBuilder.TailRow + i]),
                        pfRoot.TransformPoint(_vertScratch[NoteCapGeometryBuilder.TailRow + i + 1]),
                        Color.cyan);
                }

                // ── Head arc (cyan): left→right across outer note edge (front cap) ─────
                for (int i = 0; i < NoteCapGeometryBuilder.ColumnCount; i++)
                {
                    Debug.DrawLine(
                        pfRoot.TransformPoint(_vertScratch[NoteCapGeometryBuilder.HeadRow + i]),
                        pfRoot.TransformPoint(_vertScratch[NoteCapGeometryBuilder.HeadRow + i + 1]),
                        Color.cyan);
                }

                // ── Left radial edge (blue): column 0, tail→head ──────────────────────
                Debug.DrawLine(
                    pfRoot.TransformPoint(_vertScratch[NoteCapGeometryBuilder.TailRow]),
                    pfRoot.TransformPoint(_vertScratch[NoteCapGeometryBuilder.HeadRow]),
                    Color.blue);

                // ── Right radial edge (blue): column N, tail→head ─────────────────────
                Debug.DrawLine(
                    pfRoot.TransformPoint(
                        _vertScratch[NoteCapGeometryBuilder.TailRow + NoteCapGeometryBuilder.ColumnCount]),
                    pfRoot.TransformPoint(
                        _vertScratch[NoteCapGeometryBuilder.HeadRow + NoteCapGeometryBuilder.ColumnCount]),
                    Color.blue);

                // ── Centre radial sample line (white): exact laneCenterDeg, tail→head ──
                // N=5 has no column exactly at centre, so we sample it independently.
                float centreRad = laneCenterDeg * Mathf.Deg2Rad;
                float zTail = NoteApproachMath.FrustumZAtRadius(tailR, innerLocal, outerLocal, hInner, hOuter);
                float zHead = NoteApproachMath.FrustumZAtRadius(headR, innerLocal, outerLocal, hInner, hOuter);
                Debug.DrawLine(
                    pfRoot.TransformPoint(new Vector3(
                        ctr.x + tailR * Mathf.Cos(centreRad),
                        ctr.y + tailR * Mathf.Sin(centreRad),
                        zTail)),
                    pfRoot.TransformPoint(new Vector3(
                        ctr.x + headR * Mathf.Cos(centreRad),
                        ctr.y + headR * Mathf.Sin(centreRad),
                        zHead)),
                    Color.white);
            }

            if (debugDrawLaneBoundary)
            {
                // ── Lane boundary ticks (yellow): full lane left/right ─────────────────
                float leftLaneDeg  = AngleUtil.Normalize360(laneCenterDeg - halfWidthDeg);
                float rightLaneDeg = AngleUtil.Normalize360(laneCenterDeg + halfWidthDeg);
                DrawArcTick(pfRoot, ctr, centerR, leftLaneDeg,
                    innerLocal, outerLocal, hInner, hOuter, Color.yellow);
                DrawArcTick(pfRoot, ctr, centerR, rightLaneDeg,
                    innerLocal, outerLocal, hInner, hOuter, Color.yellow);

                // ── Note boundary ticks (green): actual note left/right edges ──────────
                float leftNoteDeg  = AngleUtil.Normalize360(laneCenterDeg - noteHalfAngleDeg);
                float rightNoteDeg = AngleUtil.Normalize360(laneCenterDeg + noteHalfAngleDeg);
                DrawArcTick(pfRoot, ctr, centerR, leftNoteDeg,
                    innerLocal, outerLocal, hInner, hOuter, Color.green);
                DrawArcTick(pfRoot, ctr, centerR, rightNoteDeg,
                    innerLocal, outerLocal, hInner, hOuter, Color.green);
            }
        }

        // -------------------------------------------------------------------
        // Debug draw helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Draws a short arc tick at <paramref name="angleDeg"/> and <paramref name="radius"/>
        /// to mark a lane or note boundary.  Allocation-free: only Debug.DrawLine calls.
        /// Frustum Z uses the actual frustum profile (not a hardcoded constant).
        /// </summary>
        private static void DrawArcTick(
            Transform pfRoot,
            Vector2   center,
            float     radius,
            float     angleDeg,
            float     innerLocal,
            float     outerLocal,
            float     hInner,
            float     hOuter,
            Color     color)
        {
            const float HalfTickDeg = 2f;

            float localZ = NoteApproachMath.FrustumZAtRadius(
                radius, innerLocal, outerLocal, hInner, hOuter);

            const int Segments = 4;
            float startDeg = angleDeg - HalfTickDeg;
            float step     = (HalfTickDeg * 2f) / Segments;

            float   a0   = startDeg * Mathf.Deg2Rad;
            Vector3 prev = pfRoot.TransformPoint(
                center.x + radius * Mathf.Cos(a0),
                center.y + radius * Mathf.Sin(a0),
                localZ);

            for (int i = 1; i <= Segments; i++)
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
    }
}
