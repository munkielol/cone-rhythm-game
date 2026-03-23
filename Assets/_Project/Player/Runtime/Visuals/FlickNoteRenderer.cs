// FlickNoteRenderer.cs
// Production renderer for Flick note heads: body + arrow overlay.
//
// ── Architecture ─────────────────────────────────────────────────────────────
//
//  Centralized renderer: NOT attached to individual notes. Reads the global
//  note list from PlayerAppController and draws one mesh per visible Flick note
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
// ── Skin rendering (v0 step 3: texture-driven, CPU UV) ────────────────────────
//
//  Replaces the placeholder _Color-only path with the texture-driven body path
//  described in spec §5.7.3 (identical pattern to TapNoteRenderer / CatchNoteRenderer):
//
//    • Material template: noteSkinSet.noteBodyMaterial
//      (shared across all note types; do NOT bake a texture into it)
//    • Body texture:      noteSkinSet.GetFlickBodyTexture(note.FlickDirection)
//      (per-note direction-specific lookup; direction override → generic flick → fallback;
//       assigned per draw call via MaterialPropertyBlock._MainTex)
//    • UV layout:         NoteCapGeometryBuilder.FillCapUVs(…)
//      (fixed-edge + center-anchored tiled-center written into _uvScratch each frame)
//    • Vertical flip:     noteSkinSet.flipFlickBodyVertical
//    • Lane width ratio:  noteSkinSet.noteLaneWidthRatio
//    • Radial thickness:  noteSkinSet.noteRadialHalfThicknessLocal
//    • Missed tint:       noteSkinSet.missedTintColor (via _Color)
//    • Normal tint:       Color.white (texture drives the look; no extra tint)
//
//  All sizing and color parameters are now authoritative on NoteSkinSet.
//  The old per-renderer flickMaterial / noteLaneWidthRatio / noteHalfThicknessLocal
//  / flickColor / missedTintColor fields have been removed.
//
// ── Geometry (v0 step 3a: edge-aware column placement) ───────────────────────
//
//  FillCapVerticesEdgeAware replaces the old uniform FillCapVertices call.
//  Column boundaries are placed at chord positions that match the three skin
//  regions exactly (fixed left border / tiled center / fixed right border),
//  using the same EdgeAwareChordAtColumn helper as FillCapUVs.
//
// ── Flick arrow overlay (v0 step 3d) ─────────────────────────────────────────
//
//  A second Graphics.DrawMesh call per visible flick note draws a camera-facing
//  direction arrow on top of the body, implemented per spec §5.7.2:
//
//    • Material template: noteSkinSet.flickArrowMaterial
//      (shared across all four directions; do NOT bake a texture into it)
//    • Arrow texture:     noteSkinSet.GetFlickArrowTexture(note.FlickDirection)
//      (direction-specific; fallback chain mirrors body texture pattern;
//       assigned per draw call via MaterialPropertyBlock._MainTex on _arrowPropBlock)
//    • Mesh:       _arrowMesh — single shared unit-square quad; never modified per-frame
//    • Size:       arrowWidthLocal × arrowHeightLocal (independent W/H control); each axis
//      falls back to arrowSizeLocal when its dedicated field is 0. Constant — does NOT scale
//      with lane width.
//    • Z offset:   noteSkinSet.arrowSurfaceOffsetLocal — lifts quad above note body
//    • Placement:  note centre + radialOffsetLocal along (cosθ,sinθ) + tangentialOffsetLocal
//      along (-sinθ,cosθ), then arrowSurfaceOffsetLocal in Z above the body surface.
//    • Orientation: camera-facing billboard, baseline up-axis = U direction (radial inward)
//      in world space — same for every direction.  Per-direction art is handled by:
//        exact textures (dedicated slot assigned): used as-is, authored for that direction.
//        fallback textures (D/R resolve to U/L family): rotated 180° around arrowNorm so a
//        single up-pointing arrow art asset works for the opposite-axis direction.
//      "U" and "L" are always exact (they are family roots and carry no rotation).
//    • Billboard matrix: LookRotation(forward=arrowNorm, up=arrowUp) where arrowNorm
//      is derived from Cross(arrowRight, arrowUp) with arrowRight = Cross(arrowUp, -camForward).
//    • If flickArrowMaterial is null: arrows skipped, one-time warning logged.
//    • If arrow texture resolves to null: arrow skipped silently for that note.
//
// ── Future steps (do not implement here) ─────────────────────────────────────
//
//  Step 4  (Hold):        hold ribbon skin migration — Hold will get its own
//                         sizing parameters in NoteSkinSet (holdLaneWidthRatio,
//                         etc.) rather than reusing the single-interaction family.
//  Step 5  (Shader tile): optional shader-side tiling optimisation.
//
// Spec §5.7.a / §5.7.0 step 2 (geometry) / §5.7.3 step 3 (UV + skin) /
//      §5.7.2 step 3d (arrow overlay).

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production Flick note head renderer — body + arrow overlay (spec §5.7.3 + §5.7.2).
    /// Texture-driven skin via <see cref="NoteSkinSet"/>; CPU-driven per-frame UV
    /// assignment for fixed-edge + center-anchored tiled-center body layout (spec §5.7.3).
    /// Arrow overlay: camera-facing billboard per note, direction-aware via NoteSkinSet
    /// arrow materials; constant size regardless of lane width (spec §5.7.2).
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

        [Tooltip("NoteSkinSet asset driving the body material, body texture, arrow material template, " +
                 "arrow textures, lane width ratio, radial half-thickness, arrow size, missed tint, " +
                 "and vertical flip for Flick notes.\n\n" +
                 "Create via Assets → Create → RhythmicFlow → Note Skin Set.\n" +
                 "Assign noteBodyMaterial, flickBodyTexture, flickArrowMaterial, and " +
                 "flickArrowTexture* on the asset, then drag it here.")]
        [SerializeField] private NoteSkinSet noteSkinSet;

        // -------------------------------------------------------------------
        // Inspector — Frustum surface alignment (mirrors TapNoteRenderer)
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
        // Internals — body mesh pool
        // -------------------------------------------------------------------

        // One mesh per simultaneously visible Flick note. Vertices and UVs overwritten
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

        // Pre-allocated property block — reused for every body DrawMesh call (no per-frame allocation).
        private MaterialPropertyBlock _propBlock;

        // Separate property block for arrow overlay draws. Kept distinct from _propBlock so
        // that _MainTex set for the body texture never leaks into the arrow draw call and vice versa.
        private MaterialPropertyBlock _arrowPropBlock;

        // -------------------------------------------------------------------
        // Internals — arrow overlay
        // -------------------------------------------------------------------

        // Single shared unit-square quad mesh used for all arrow overlays. Never modified
        // per frame — orientation is encoded entirely in the per-note Matrix4x4 passed to
        // Graphics.DrawMesh (spec §5.7.2 "billboard construction").
        private Mesh _arrowMesh;

        // -------------------------------------------------------------------
        // Internals — one-time warning guards (warn once, not every frame)
        // -------------------------------------------------------------------

        // Missing PlayerAppController — always warn; without it nothing can render.
        private bool _hasWarnedMissingController;

        // Missing NoteSkinSet — warn once; without it material/texture/sizing are unknown.
        private bool _hasWarnedMissingSkinSet;

        // Missing noteBodyMaterial on the assigned NoteSkinSet — warn once.
        private bool _hasWarnedMissingMaterial;

        // Missing flick texture (all direction slots, flickBodyTexture, and fallbackBodyTexture null)
        // — warn once when the first note with a null resolved texture is encountered.
        // Rendering continues in color-only mode for that note.
        private bool _hasWarnedMissingTexture;

        // Missing flickArrowMaterial on the assigned NoteSkinSet — warn once.
        // Arrow overlays are skipped entirely when the template material is absent.
        private bool _hasWarnedMissingArrowMaterial;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // Pre-allocate the body mesh pool. Each mesh has the curved-cap topology set up
            // once in BuildCapMesh (triangles + placeholder UVs). Vertices and UVs are
            // overwritten in-place every frame — no per-frame GC allocation.
            _meshPool = new Mesh[MaxNotePool];
            for (int i = 0; i < MaxNotePool; i++)
            {
                _meshPool[i] = NoteCapGeometryBuilder.BuildCapMesh("FlickNoteCurvedCap");
            }
            _propBlock      = new MaterialPropertyBlock();
            _arrowPropBlock = new MaterialPropertyBlock();

            // Build the shared arrow overlay quad. This mesh is the same unit-square for every
            // note and every frame — per-note orientation is driven entirely by the DrawMesh
            // matrix. No per-frame allocation needed.
            _arrowMesh = BuildArrowQuadMesh();
        }

        private void OnDestroy()
        {
            if (_meshPool != null)
            {
                for (int i = 0; i < _meshPool.Length; i++)
                {
                    if (_meshPool[i] != null) { Destroy(_meshPool[i]); _meshPool[i] = null; }
                }
            }

            if (_arrowMesh != null) { Destroy(_arrowMesh); _arrowMesh = null; }
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
                        "[FlickNoteRenderer] PlayerAppController is not assigned. " +
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
                        "[FlickNoteRenderer] NoteSkinSet is not assigned. " +
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
                        "[FlickNoteRenderer] NoteSkinSet.noteBodyMaterial is not assigned. " +
                        "Assign a material that supports _MainTex and _Color via " +
                        "MaterialPropertyBlock (e.g. Unlit/Transparent).", this);
                }
                return;
            }

            // ── Read sizing from NoteSkinSet (single-interaction family source of truth) ─
            // noteLaneWidthRatio and noteRadialHalfThicknessLocal are shared by the
            // Tap/Catch/Flick family. Hold uses its own separate sizing parameters.
            float noteLaneWidthRatio     = noteSkinSet.noteLaneWidthRatio;
            float noteHalfThicknessLocal = noteSkinSet.noteRadialHalfThicknessLocal;

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

            // ── Camera forward for arrow billboard ─────────────────────────────────────
            // Resolved once per frame; passed into the per-note arrow matrix calculation.
            // Falls back to Vector3.back (0,0,-1) if no main camera is present, which
            // makes the arrow face +Z — reasonable when rendering without a camera.
            Camera cam = Camera.main;
            Vector3 camForward = cam != null ? cam.transform.forward : Vector3.back;

            _poolUsed = 0;

            foreach (RuntimeNote note in allNotes)
            {
                // ── Type and state filter ──────────────────────────────────────────────
                if (note.Type != NoteType.Flick) { continue; }
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
                // flipFlickBodyVertical swaps V so artists can orient art without re-exporting.
                // Writes into _uvScratch; assigned to the mesh below alongside vertices.
                NoteCapGeometryBuilder.FillCapUVs(
                    _uvScratch,
                    r,                  // approach radius — see FillCapUVs for why we use r
                    noteHalfAngleDeg,
                    noteSkinSet,
                    noteSkinSet.flipFlickBodyVertical);

                // ── Resolve body texture per note (direction-specific) ────────────────
                // note.FlickDirection is the chart-authored direction string ("U"/"D"/"L"/"R"
                // or "" for undirected). GetFlickBodyTexture applies the fallback chain:
                //   direction override → flickBodyTexture → fallbackBodyTexture.
                // Returns null when all slots are unassigned — rendering falls back to color-only.
                Texture2D bodyTexture = noteSkinSet.GetFlickBodyTexture(note.FlickDirection);
                if (bodyTexture == null && !_hasWarnedMissingTexture)
                {
                    _hasWarnedMissingTexture = true;
                    Debug.LogWarning(
                        "[FlickNoteRenderer] NoteSkinSet has no body texture for direction " +
                        $"'{note.FlickDirection}' (and no flickBodyTexture / fallbackBodyTexture). " +
                        "Flick notes will render in color-only mode until a texture is assigned.", this);
                }

                // ── MaterialPropertyBlock: texture + tint ─────────────────────────────
                //
                // _MainTex: direction-specific flick body texture resolved above.
                //           Assigned per draw call so the shared material asset stays clean.
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

                // ── Upload mesh data and draw body ─────────────────────────────────────
                Mesh mesh = _meshPool[_poolUsed++];
                mesh.vertices = _vertScratch;
                mesh.uv       = _uvScratch;       // per-frame UV: fixed-edge + center-anchored tiled-center
                mesh.RecalculateBounds();

                Graphics.DrawMesh(mesh, localToWorld, bodyMaterial,
                    gameObject.layer, null, 0, _propBlock);

                // ── Arrow overlay ──────────────────────────────────────────────────────
                // Separate DrawMesh call on top of the body (spec §5.7.2).
                // The arrow is a camera-facing billboard: its local +Y axis points in the
                // flick gesture direction; its local +Z faces the camera.
                // Arrow size is constant — does NOT scale with lane width (readability rule).
                // Uses flickArrowMaterial template + per-direction texture via _arrowPropBlock.
                DrawArrowOverlay(note.FlickDirection, laneCenterDeg,
                    ctr, r, innerLocal, outerLocal, hInner, hOuter,
                    pfRoot, camForward);

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
        // Arrow overlay rendering
        // -------------------------------------------------------------------

        /// <summary>
        /// Draws the flick arrow overlay quad for one note.
        /// Camera-facing billboard: baseline up-axis is always the "U" (radial inward) direction;
        /// exact per-direction textures are used as-is; fallback textures (D/R resolving to the
        /// U/L family root) receive a 180° rotation around arrowNorm so a single art asset covers
        /// both axis directions without re-authoring.
        /// Arrow position is offset from the note centre by the radial and tangential offset
        /// fields on <see cref="NoteSkinSet"/>, then lifted by <c>arrowSurfaceOffsetLocal</c>.
        /// Arrow scale uses <c>arrowWidthLocal</c> (quad X) and <c>arrowHeightLocal</c> (quad Y)
        /// when non-zero; otherwise falls back to <c>arrowSizeLocal</c> for that axis.
        /// Uses the shared <c>flickArrowMaterial</c> template with a per-note arrow texture
        /// assigned via <c>_arrowPropBlock</c> — same MaterialPropertyBlock pattern as body rendering.
        /// Skips (with a one-time warning) if the arrow material template is missing.
        /// Skips silently if the resolved arrow texture is null.
        /// </summary>
        private void DrawArrowOverlay(
            string    flickDirection,
            float     laneCenterDeg,
            Vector2   ctr,
            float     r,
            float     innerLocal,
            float     outerLocal,
            float     hInner,
            float     hOuter,
            Transform pfRoot,
            Vector3   camForward)
        {
            // Arrow material template — shared across all directions. Without it we cannot draw.
            Material arrowMaterial = noteSkinSet.flickArrowMaterial;
            if (arrowMaterial == null)
            {
                if (!_hasWarnedMissingArrowMaterial)
                {
                    _hasWarnedMissingArrowMaterial = true;
                    Debug.LogWarning(
                        "[FlickNoteRenderer] NoteSkinSet.flickArrowMaterial is not assigned. " +
                        "Assign a material that supports _MainTex via MaterialPropertyBlock " +
                        "(e.g. Unlit/Transparent). Arrow overlays will be skipped until assigned.", this);
                }
                return;
            }
            if (_arrowMesh == null) { return; }

            // Arrow texture — resolved per note via direction-specific fallback chain.
            // GetFlickArrowTexture: direction slot → generic flickArrowTexture → null.
            // Returns null when all slots are unassigned — skip this arrow silently.
            Texture2D arrowTexture = noteSkinSet.GetFlickArrowTexture(flickDirection);
            if (arrowTexture == null) { return; }

            // ── Note centre in PlayfieldRoot local space ───────────────────────────────
            // Base position: note body centre at approach radius.
            // Radial offset:     along (cosθ, sinθ)   — outward from arena centre.
            // Tangential offset: along (-sinθ, cosθ)  — CCW perpendicular (positive = "R" direction).
            // Surface Z offset:  arrowSurfaceOffsetLocal lifts the quad above the body surface.
            float centreRad  = laneCenterDeg * Mathf.Deg2Rad;
            float cosA       = Mathf.Cos(centreRad);
            float sinA       = Mathf.Sin(centreRad);
            float radialOff  = noteSkinSet.arrowRadialOffsetLocal;
            float tangOff    = noteSkinSet.arrowTangentialOffsetLocal;
            float bodyZ      = NoteApproachMath.FrustumZAtRadius(r, innerLocal, outerLocal, hInner, hOuter);
            Vector3 noteCentreLocal = new Vector3(
                ctr.x + r * cosA + radialOff * cosA + tangOff * (-sinA),
                ctr.y + r * sinA + radialOff * sinA + tangOff * (  cosA),
                bodyZ + noteSkinSet.arrowSurfaceOffsetLocal);

            // ── Billboard orientation ──────────────────────────────────────────────────
            //
            // Baseline arrowUp is always the "U" (radial inward) direction, regardless of
            // flickDirection. This means:
            //
            //   • "U" textures are authored pointing toward V=1 (top of texture) and are
            //     used as-is — no rotation needed.
            //   • "D" textures authored pointing toward V=1 are used as-is when a dedicated
            //     flickArrowTextureDown is assigned (exact). When falling back to the "U"
            //     texture family (no dedicated D texture), a 180° rotation around arrowNorm
            //     flips the arrow to point radially outward — so a single "U" art asset
            //     works for both directions without re-authoring.
            //   • Same rule for "L" / "R": "L" is always exact (it is the family root);
            //     "R" gets 180° when it falls back to the "L" texture.
            //
            // isFallback: true when the direction is "D" or "R" AND no exact texture is
            //   assigned for that direction (i.e. the resolved texture comes from the
            //   "U"/"L" family root via the fallback chain).
            //
            // Exact textures: authored to face the correct direction already → no rotation.
            // Fallback textures: face the opposite axis → 180° AngleAxis(arrowNorm) corrects.
            Vector3 baseDir2DLocal = FlickDirToLocal("U", laneCenterDeg);  // radial inward baseline
            Vector3 arrowUp        = pfRoot.TransformDirection(baseDir2DLocal);  // world space

            // arrowRight: stable horizontal-ish axis in the plane perpendicular to camForward.
            // Cross(arrowUp, -camForward) gives a vector perpendicular to both.
            Vector3 arrowRight = Vector3.Cross(arrowUp, -camForward);
            // Guard: degenerate case — arrowUp parallel to camForward (camera looking straight
            // along the radial direction).  Extremely unlikely in normal play; skip the arrow.
            if (arrowRight.sqrMagnitude < 1e-6f) { return; }
            arrowRight.Normalize();

            Vector3    arrowNorm = Vector3.Cross(arrowRight, arrowUp);  // faces camera
            Quaternion baseRot   = Quaternion.LookRotation(arrowNorm, arrowUp);

            // Apply 180° flip for D/R directions when the resolved texture is from the
            // opposite-axis family root (fallback). Exact per-direction textures are assumed
            // to be authored already facing the correct direction, so no rotation is applied.
            bool isFallback = (flickDirection == "D" || flickDirection == "R")
                && !noteSkinSet.IsFlickArrowTextureExact(flickDirection);
            Quaternion arrowRot = isFallback
                ? Quaternion.AngleAxis(180f, arrowNorm) * baseRot
                : baseRot;

            // ── Arrow matrix ───────────────────────────────────────────────────────────
            // Position is in world space (pfRoot.TransformPoint promotes local → world).
            // Scale uses independent width/height when set (> 0); falls back to the legacy
            // arrowSizeLocal for any axis whose dedicated field is left at 0.
            // Width = tangential extent (quad X axis after billboard LookRotation).
            // Height = radial/gesture extent (quad Y axis = arrowUp direction).
            // Neither axis scales with lane width — arrows are constant-size readability elements.
            float arrowW = noteSkinSet.arrowWidthLocal  > 0f
                ? noteSkinSet.arrowWidthLocal
                : noteSkinSet.arrowSizeLocal;
            float arrowH = noteSkinSet.arrowHeightLocal > 0f
                ? noteSkinSet.arrowHeightLocal
                : noteSkinSet.arrowSizeLocal;
            Matrix4x4 arrowMatrix = Matrix4x4.TRS(
                pfRoot.TransformPoint(noteCentreLocal),
                arrowRot,
                new Vector3(arrowW, arrowH, 1f));

            // Assign the direction-specific arrow texture to the shared material template via
            // _arrowPropBlock, exactly mirroring the body texture assignment pattern.
            // _arrowPropBlock is kept separate from _propBlock so body _MainTex never leaks in.
            _arrowPropBlock.SetTexture("_MainTex", arrowTexture);

            Graphics.DrawMesh(_arrowMesh, arrowMatrix, arrowMaterial,
                gameObject.layer, null, 0, _arrowPropBlock);
        }

        // -------------------------------------------------------------------
        // Arrow static helpers (allocation-free, called per-note)
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the flick gesture direction as a unit vector in PlayfieldRoot local XY,
        /// matching the table in spec §5.7.2.
        /// θ = laneCenterDeg in radians.
        /// </summary>
        private static Vector3 FlickDirToLocal(string flickDirection, float laneCenterDeg)
        {
            float rad  = laneCenterDeg * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(rad);
            float sinA = Mathf.Sin(rad);

            // Each case matches the spec §5.7.2 "dir2DLocal in PlayfieldRoot XY" column.
            return flickDirection switch
            {
                "U" => new Vector3(-cosA, -sinA, 0f),   // radial inward  (toward arena centre)
                "D" => new Vector3( cosA,  sinA, 0f),   // radial outward (away from centre)
                "L" => new Vector3( sinA, -cosA, 0f),   // CW tangent
                "R" => new Vector3(-sinA,  cosA, 0f),   // CCW tangent
                _   => new Vector3(-cosA, -sinA, 0f),   // unknown / undirected: default to U
            };
        }

        /// <summary>
        /// Builds the shared unit-square quad mesh used for all arrow overlays (spec §5.7.2).
        /// The quad is in the local XY plane, centred at the origin, with extents ±0.5.
        /// Local +Y = "arrow points here" = the direction set by the billboard LookRotation.
        /// Arrow texture should be authored with the arrow graphic pointing toward V=1 (+Y).
        /// Called once in Awake; the resulting mesh is never modified per-frame.
        /// </summary>
        private static Mesh BuildArrowQuadMesh()
        {
            var mesh = new Mesh();
            mesh.name = "FlickArrowQuad";

            // Four corners of a unit quad centred at origin in the local XY plane.
            mesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, 0f),  // 0  bottom-left
                new Vector3( 0.5f, -0.5f, 0f),  // 1  bottom-right
                new Vector3( 0.5f,  0.5f, 0f),  // 2  top-right
                new Vector3(-0.5f,  0.5f, 0f),  // 3  top-left
            };

            // UV: (0,0) at bottom-left → (1,1) at top-right.
            // Arrow texture should point toward V=1 (top of texture = direction arrow points).
            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),  // 0
                new Vector2(1f, 0f),  // 1
                new Vector2(1f, 1f),  // 2
                new Vector2(0f, 1f),  // 3
            };

            // Two triangles, winding CCW from +Z → normal faces +Z in local space.
            // The billboard LookRotation will orient +Z toward the camera at runtime.
            mesh.triangles = new int[] { 0, 1, 2,  0, 2, 3 };

            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
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
