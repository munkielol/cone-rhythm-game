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
//  5-column segmented curved-cap from NoteCapGeometryBuilder.
//  Key invariants:
//    - Lane-width-aware: noteHalfAngleDeg = laneHalfWidthDeg × noteLaneWidthRatio
//    - Frustum-conforming: Z per row = NoteApproachMath.FrustumZAtRadius(r, …)
//    - Centered on the lane at approach radius r
//    - Stable under lane centerDeg / widthDeg / arenaRadius animation
//    - Wrap-safe: cos/sin handle angles outside [0, 360) naturally
//
// ── Skin rendering (v0 step 3: texture-driven, CPU UV) ───────────────────────
//
//  Replaces the placeholder _Color-only path with the texture-driven path
//  described in spec §5.7.3 (identical pattern to TapNoteRenderer step 3):
//
//    • Material template: noteSkinSet.noteBodyMaterial
//      (shared across all note types; do NOT bake a texture into it)
//    • Body texture:      noteSkinSet.GetBodyTexture(NoteBodySkinType.Catch)
//      (assigned per draw call via MaterialPropertyBlock._MainTex)
//    • UV layout:         NoteCapGeometryBuilder.FillCapUVs(…)
//      (fixed-edge + center-anchored tiled-center written into _uvScratch each frame)
//    • Lane width ratio:  noteSkinSet.noteLaneWidthRatio
//    • Radial thickness:  noteSkinSet.noteRadialHalfThicknessLocal
//    • Missed tint:       noteSkinSet.missedTintColor (via _Color)
//    • Normal tint:       Color.white (texture drives the look; no extra tint)
//    • Lead time:         playerAppController.NoteLeadTimeMs   (shared approach setting)
//    • Spawn factor:      playerAppController.SpawnRadiusFactor (shared approach setting)
//
//  Sizing/color parameters are authoritative on NoteSkinSet.
//  Approach settings (noteLeadTimeMs, spawnRadiusFactor) are authoritative on
//  PlayerAppController and shared across all renderers (Tap/Catch/Flick/Hold).
//  The old per-renderer catchMaterial / noteLaneWidthRatio / noteHalfThicknessLocal
//  / catchColor / missedTintColor / noteLeadTimeMs / spawnRadiusFactor fields have been removed.
//
// ── Geometry (v0 step 3a: edge-aware column placement) ───────────────────────
//
//  FillCapVerticesEdgeAware replaces the old uniform FillCapVertices call.
//  Column boundaries are placed at chord positions that match the three skin
//  regions exactly (fixed left border / tiled center / fixed right border),
//  using the same EdgeAwareChordAtColumn helper as FillCapUVs.  Geometry and
//  UV column boundaries are guaranteed to agree — no asymmetry artefacts.
//
// ── Future steps (do not implement here) ─────────────────────────────────────
//
//  Step 4  (Hold):        hold ribbon skin migration — Hold will get its own
//                         sizing parameters in NoteSkinSet (holdLaneWidthRatio,
//                         etc.) rather than reusing the single-interaction family.
//  Step 5  (Shader tile): optional shader-side tiling optimisation.
//
// Spec §5.7.a / §5.7.0 step 2 (geometry) / §5.7.3 step 3 (UV + skin) / step 3a (edge-aware geometry).

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production Catch note head renderer (step 3).
    /// Texture-driven skin via <see cref="NoteSkinSet"/>; CPU-driven per-frame UV
    /// assignment for fixed-edge + center-anchored tiled-center body layout (spec §5.7.3).
    /// Geometry: 5-column edge-aware segmented curved-cap from <see cref="NoteCapGeometryBuilder"/>.
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

        [Tooltip("NoteSkinSet asset driving the body material, body texture, lane width ratio, " +
                 "radial half-thickness, and missed tint for Catch notes.\n\n" +
                 "Create via Assets → Create → RhythmicFlow → Note Skin Set.\n" +
                 "Assign catchBodyTexture and noteBodyMaterial on the asset, then drag it here.")]
        [SerializeField] private NoteSkinSet noteSkinSet;

        // -------------------------------------------------------------------
        // Inspector — Frustum surface alignment (mirrors TapNoteRenderer)
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

        // One mesh per simultaneously visible Catch note. Vertices and UVs overwritten
        // in-place each LateUpdate — zero per-frame GC allocation.
        private const int MaxNotePool = 128;

        private Mesh[] _meshPool;
        private int    _poolUsed;

        // Vertex scratch — filled by NoteCapGeometryBuilder.FillCapVerticesEdgeAware each frame.
        // Length = NoteCapGeometryBuilder.VertexCount (12).
        private readonly Vector3[] _vertScratch = new Vector3[NoteCapGeometryBuilder.VertexCount];

        // UV scratch — filled by NoteCapGeometryBuilder.FillCapUVs each frame.
        // Assigned to the pooled mesh alongside _vertScratch.
        // Length = NoteCapGeometryBuilder.VertexCount (12), matching mesh topology.
        private readonly Vector2[] _uvScratch = new Vector2[NoteCapGeometryBuilder.VertexCount];

        // Pre-allocated property block — reused every DrawMesh call (no per-frame allocation).
        private MaterialPropertyBlock _propBlock;

        // -------------------------------------------------------------------
        // Internals — one-time warning guards (warn once, not every frame)
        // -------------------------------------------------------------------

        // Missing PlayerAppController — always warn; without it nothing can render.
        private bool _hasWarnedMissingController;

        // Missing NoteSkinSet — warn once; without it material/texture/sizing are unknown.
        private bool _hasWarnedMissingSkinSet;

        // Missing noteBodyMaterial on the assigned NoteSkinSet — warn once.
        private bool _hasWarnedMissingMaterial;

        // Missing catch texture (catchBodyTexture and fallbackBodyTexture both null) — warn once.
        // Rendering continues in color-only mode; the texture warning fires just once.
        private bool _hasWarnedMissingTexture;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // Pre-allocate the mesh pool. Each mesh has the curved-cap topology set up
            // once in BuildCapMesh (triangles + placeholder UVs). Vertices and UVs are
            // overwritten in-place every frame — no per-frame GC allocation.
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
            // ── Guard: PlayerAppController ─────────────────────────────────────────────
            if (playerAppController == null)
            {
                if (!_hasWarnedMissingController)
                {
                    _hasWarnedMissingController = true;
                    Debug.LogWarning(
                        "[CatchNoteRenderer] PlayerAppController is not assigned. " +
                        "Assign the PlayerAppController reference in the Inspector.", this);
                }
                return;
            }

            // ── Guard: NoteSkinSet ─────────────────────────────────────────────────────
            // All sizing, texture, and tint parameters come from the skin — skip rendering
            // entirely when the asset is not wired so we don't produce inconsistent output.
            if (noteSkinSet == null)
            {
                if (!_hasWarnedMissingSkinSet)
                {
                    _hasWarnedMissingSkinSet = true;
                    Debug.LogWarning(
                        "[CatchNoteRenderer] NoteSkinSet is not assigned. " +
                        "Create a NoteSkinSet asset and assign it in the Inspector.", this);
                }
                return;
            }

            // ── Guard: body material ───────────────────────────────────────────────────
            // The material provides the shader template. Without it Graphics.DrawMesh
            // would use the error pink material, so skip and warn once.
            Material bodyMaterial = noteSkinSet.noteBodyMaterial;
            if (bodyMaterial == null || _meshPool == null)
            {
                if (bodyMaterial == null && !_hasWarnedMissingMaterial)
                {
                    _hasWarnedMissingMaterial = true;
                    Debug.LogWarning(
                        "[CatchNoteRenderer] NoteSkinSet.noteBodyMaterial is not assigned. " +
                        "Assign a material that supports _MainTex and _Color via " +
                        "MaterialPropertyBlock (e.g. Unlit/Transparent).", this);
                }
                return;
            }

            // ── Resolve catch body texture (warn once if missing; render color-only) ───
            // GetBodyTexture returns catchBodyTexture, falling back to fallbackBodyTexture.
            // If both are null it returns null — we warn once and continue in color-only mode
            // (the material's base color shows through without _MainTex being overridden).
            Texture2D bodyTexture = noteSkinSet.GetBodyTexture(NoteBodySkinType.Catch);
            if (bodyTexture == null && !_hasWarnedMissingTexture)
            {
                _hasWarnedMissingTexture = true;
                Debug.LogWarning(
                    "[CatchNoteRenderer] NoteSkinSet has no catchBodyTexture or fallbackBodyTexture. " +
                    "Catch notes will render in color-only mode until a texture is assigned.", this);
            }

            // ── Read sizing from NoteSkinSet (single-interaction family source of truth) ─
            // noteLaneWidthRatio and noteRadialHalfThicknessLocal are shared by the
            // Tap/Catch/Flick family. Hold uses its own separate sizing parameters.
            float noteLaneWidthRatio     = noteSkinSet.noteLaneWidthRatio;
            float noteHalfThicknessLocal = noteSkinSet.noteRadialHalfThicknessLocal;

            // ── Read approach settings from PlayerAppController (shared for all renderers) ─
            // noteLeadTimeMs and spawnRadiusFactor are authoritative on PlayerAppController.
            // All renderers read these values from the same source — a single Inspector
            // change on the controller propagates to Tap, Catch, Flick, and Hold at once.
            int   noteLeadTimeMs   = playerAppController.NoteLeadTimeMs;
            float spawnRadiusFactor = playerAppController.SpawnRadiusFactor;

            // ── Geometry / playfield data ──────────────────────────────────────────────
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
                if (note.State == NoteState.Hit) { continue; }  // successfully judged — no visual

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

                // ── Arena centre in local XY ───────────────────────────────────────────
                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                // ── Angular occupancy ──────────────────────────────────────────────────
                float laneCenterDeg    = AngleUtil.Normalize360(lane.CenterDeg);
                float halfWidthDeg     = lane.WidthDeg * 0.5f;
                float noteHalfAngleDeg = NoteCapGeometryBuilder.NoteHalfAngleDeg(
                    halfWidthDeg, noteLaneWidthRatio);

                // ── Curved-cap vertex fill (edge-aware) ───────────────────────────────
                // Places the ColumnCount+1 column boundaries at chord positions that
                // match the three skin regions: fixed left border / tiled center /
                // fixed right border.  Uses the same EdgeAwareChordAtColumn helper as
                // FillCapUVs so geometry and UV columns are guaranteed to align.
                NoteCapGeometryBuilder.FillCapVerticesEdgeAware(
                    _vertScratch,
                    ctr,
                    tailR,
                    headR,
                    laneCenterDeg,
                    noteHalfAngleDeg,
                    r,                                      // approach radius (for chord → angle)
                    noteSkinSet.bodyLeftEdgeLocalWidth,
                    noteSkinSet.bodyRightEdgeLocalWidth,
                    innerLocal,
                    outerLocal,
                    hInner,
                    hOuter);

                // ── Per-frame UV fill (fixed-edge + center-anchored tiled-center) ──────
                // Uses the approach radius r (centre of the note band) so that edge/center
                // proportions stay stable as the note travels toward the judgement ring.
                // Writes into _uvScratch; assigned to the mesh below alongside vertices.
                NoteCapGeometryBuilder.FillCapUVs(
                    _uvScratch,
                    r,                  // approach radius — see FillCapUVs for why we use r
                    noteHalfAngleDeg,
                    noteSkinSet,
                    noteSkinSet.flipCatchBodyVertical);

                // ── MaterialPropertyBlock: texture + tint ─────────────────────────────
                //
                // _MainTex: the catch body texture. Assigned here per draw call so the shared
                //           material asset stays clean (no texture baked into it).
                //           Skipped when bodyTexture is null — falls back to color-only.
                //
                // _Color:   for normal notes: Color.white (texture drives the look).
                //           for missed notes: noteSkinSet.missedTintColor (dim grey tint).
                //           The tint multiplies with _MainTex so missed notes appear dim.
                if (bodyTexture != null)
                {
                    _propBlock.SetTexture("_MainTex", bodyTexture);
                }

                Color tint = (note.State == NoteState.Missed)
                    ? noteSkinSet.missedTintColor
                    : Color.white;
                _propBlock.SetColor("_Color", tint);

                // ── Upload mesh data and draw ──────────────────────────────────────────
                Mesh mesh = _meshPool[_poolUsed++];
                mesh.vertices = _vertScratch;
                mesh.uv       = _uvScratch;       // per-frame UV: fixed-edge + center-anchored tiled-center
                mesh.RecalculateBounds();

                Graphics.DrawMesh(mesh, localToWorld, bodyMaterial,
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
                // These show the complete lane angular span. Compare against green note
                // ticks to verify noteLaneWidthRatio occupancy is applied correctly.
                float leftLaneDeg  = AngleUtil.Normalize360(laneCenterDeg - halfWidthDeg);
                float rightLaneDeg = AngleUtil.Normalize360(laneCenterDeg + halfWidthDeg);
                DrawArcTick(pfRoot, ctr, centerR, leftLaneDeg,
                    innerLocal, outerLocal, hInner, hOuter, Color.yellow);
                DrawArcTick(pfRoot, ctr, centerR, rightLaneDeg,
                    innerLocal, outerLocal, hInner, hOuter, Color.yellow);

                // ── Note boundary ticks (green): actual note left/right edges ──────────
                // These show where the curved-cap vertices land. Should sit inset from
                // the yellow lane ticks by (1 − noteLaneWidthRatio)/2 on each side.
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

            // Use the actual frustum Z so ticks sit on the cone surface.
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
