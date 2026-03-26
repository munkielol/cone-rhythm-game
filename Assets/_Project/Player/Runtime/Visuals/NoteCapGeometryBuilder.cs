// NoteCapGeometryBuilder.cs
// Shared static geometry builder for N-segment curved-cap note head meshes.
// Used by TapNoteRenderer, CatchNoteRenderer, FlickNoteRenderer, and HoldBodyRenderer (head cap).
//
// ── Purpose ──────────────────────────────────────────────────────────────────
//
//  Centralises curved-cap mesh template creation and per-frame vertex fill so
//  all four production note cap renderers share identical geometry code without
//  duplicating logic.  All fill methods are allocation-free at runtime.
//
// ── Global visual-quality setting (spec §8.3.1) ──────────────────────────────
//
//  The arc column count is no longer hardcoded.  It is read once at startup from
//  PlayerSettingsStore.NoteCapArcSegments (default 5, min 3).
//
//  Usage pattern in each renderer's Awake():
//
//    1.  _capLayout   = NoteCapGeometryBuilder.CreateLayout(
//                           PlayerSettingsStore.NoteCapArcSegments);
//    2.  _vertScratch = new Vector3[_capLayout.VertexCount];
//        _uvScratch   = new Vector2[_capLayout.VertexCount];
//    3.  For each mesh in the pool:
//            pool[i] = NoteCapGeometryBuilder.BuildCapMesh("Name", _capLayout);
//
//  In LateUpdate, pass _capLayout as the second argument to each Fill method.
//
//  Changing NoteCapArcSegments after Awake has no effect on existing pools — the
//  change takes effect only on next startup (scene reload / re-enter Play mode).
//
// ── Geometry: segmented curved-cap (spec §5.7.0 step 2) ─────────────────────
//
//  Each cap edge is subdivided into N = layout.ColumnCount columns →
//  N+1 column boundaries, N column quads.
//
//  Vertex layout (row-major, left-to-right per row):
//
//    Tail row (at tailR):  verts[0 .. N]      — inner note edge
//    Head row (at headR):  verts[N+1 .. 2N+1] — outer note edge (front cap)
//
//    Column 0 = left angular boundary  (centerDeg − noteHalfAngleDeg)
//    Column N = right angular boundary (centerDeg + noteHalfAngleDeg)
//
//  Triangle layout per quad i (0..N-1):
//    Tri A (CCW): tail_i,  tail_i+1, head_i+1
//    Tri B (CCW): tail_i,  head_i+1, head_i
//    Total: N×2 triangles, N×6 indices  (10 triangles / 30 indices at N=5)
//
// ── UV layout ────────────────────────────────────────────────────────────────
//
//  BuildCapMesh sets placeholder UVs (U = i/N, V = 0/1) used until a
//  NoteSkinSet is wired.  FillCapUVs (step 3) overwrites these each frame
//  with the three-region fixed-edge + tiled-center layout (spec §5.7.3):
//
//    [ left border | ← tiled center → | right border ]
//    ←  fixed UV  →←  tiles with width →←  fixed UV  →
//
//  V = 0 on the tail row, V = 1 on the head row in both layouts.
//
// ── Edge-aware column allocation (1 : N-2 : 1) ───────────────────────────────
//
//  FillCapVerticesEdgeAware + FillCapUVs place column boundaries at chord
//  positions that match the three skin regions exactly:
//    LeftEdgeCols  = 1 column          for the left decorative border
//    CenterCols    = ColumnCount − 2   for the tiled center
//    RightEdgeCols = 1 column          for the right decorative border
//
//  This 1:(N-2):1 split holds for all ColumnCount ≥ 3 (the enforced minimum).
//
// ── No per-frame allocations ─────────────────────────────────────────────────
//
//  Mesh templates are built once in Awake.  Per-frame work writes into
//  pre-allocated scratch buffers (Vector3[layout.VertexCount] for verts,
//  Vector2[layout.VertexCount] for UVs); the scratches are then assigned to the
//  pooled mesh: mesh.vertices = vertScratch; mesh.uv = uvScratch.
//
// ── Isolation note ───────────────────────────────────────────────────────────
//
//  Currently lives in the Player assembly.  When the Chart Editor Playfield
//  Preview (spec §chart_editor §3.3) needs note head geometry, move this file
//  to Assets/_Project/Shared/ so both assemblies share it (spec §5.7.0).
//
// Spec §5.7.a / §5.7.0 step 2 (geometry) / step 3 (UV) / §8.3.1 (NoteCapArcSegments).

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    // =========================================================================
    //  NoteCapLayout — per-session geometry constants (created once in Awake)
    // =========================================================================

    /// <summary>
    /// Holds the per-session layout constants derived from
    /// <see cref="PlayerSettingsStore.NoteCapArcSegments"/>.
    ///
    /// <para>Create one in <c>Awake</c> via
    /// <see cref="NoteCapGeometryBuilder.CreateLayout"/>.  Cache it on the renderer.
    /// Use <see cref="VertexCount"/> to allocate scratch arrays, and pass the layout
    /// to every <see cref="NoteCapGeometryBuilder"/> method.</para>
    ///
    /// <para><b>Startup-only:</b> the layout and the pool/scratch allocations using it
    /// are fixed for the session.  Changing <c>NoteCapArcSegments</c> mid-session has
    /// no effect on existing renderers.</para>
    /// </summary>
    public readonly struct NoteCapLayout
    {
        /// <summary>
        /// Number of angular column quads across the note cap.
        /// Equals <c>max(3, NoteCapArcSegments)</c>.
        /// </summary>
        public readonly int ColumnCount;

        /// <summary>Total vertex count: 2 rows × (ColumnCount + 1) column boundaries.</summary>
        public readonly int VertexCount;

        /// <summary>Total triangle-index count: ColumnCount quads × 2 tris × 3 verts.</summary>
        public readonly int IndexCount;

        /// <summary>
        /// Start index of the tail row (inner note edge) in the flat vertex array.
        /// Always 0.  Provided here so callers can use <c>layout.TailRow</c> for clarity.
        /// </summary>
        public readonly int TailRow;

        /// <summary>
        /// Start index of the head row (outer note edge / front cap) in the flat vertex array.
        /// Head row occupies indices [HeadRow .. HeadRow + ColumnCount].
        /// </summary>
        public readonly int HeadRow;

        // Edge-aware column allocation — used only inside NoteCapGeometryBuilder.
        // External callers do not need these; they are internal to the builder logic.
        internal readonly int LeftEdgeCols;  // always 1
        internal readonly int CenterCols;    // ColumnCount − 2  (symmetric 1:N:1 split)

        internal NoteCapLayout(int columnCount)
        {
            ColumnCount  = Mathf.Max(3, columnCount); // enforce minimum of 3
            VertexCount  = (ColumnCount + 1) * 2;
            IndexCount   = ColumnCount * 6;
            TailRow      = 0;
            HeadRow      = ColumnCount + 1;
            LeftEdgeCols = 1;
            CenterCols   = ColumnCount - 2; // always ≥ 1 since ColumnCount ≥ 3
        }
    }

    // =========================================================================
    //  NoteCapGeometryBuilder — static builder
    // =========================================================================

    /// <summary>
    /// Shared static builder for N-segment curved-cap note head meshes (spec §5.7.0 step 2).
    /// Allocation-free at runtime: mesh templates built once in Awake; vertices
    /// overwritten via a pre-allocated scratch buffer each LateUpdate.
    ///
    /// <para>Call <see cref="CreateLayout"/> once in <c>Awake</c> to get a
    /// <see cref="NoteCapLayout"/> that drives the column count for all methods.
    /// Pass the cached layout to <see cref="BuildCapMesh"/>, <see cref="FillCapVerticesEdgeAware"/>,
    /// and <see cref="FillCapUVs"/> on every call.</para>
    /// </summary>
    internal static class NoteCapGeometryBuilder
    {
        // -------------------------------------------------------------------
        // Default column count (fallback when the settings store is unavailable)
        // -------------------------------------------------------------------

        /// <summary>
        /// Default arc column count — used when <see cref="PlayerSettingsStore"/>
        /// cannot be reached.  Equals the v0 shipped default (spec §8.3.1).
        /// </summary>
        public const int DefaultColumnCount = 5;

        // -------------------------------------------------------------------
        // Layout factory  (called once in Awake — zero runtime cost)
        // -------------------------------------------------------------------

        /// <summary>
        /// Creates a <see cref="NoteCapLayout"/> from the given column count.
        ///
        /// <para>Typical Awake() usage:
        /// <code>
        ///   _capLayout   = NoteCapGeometryBuilder.CreateLayout(
        ///                      PlayerSettingsStore.NoteCapArcSegments);
        ///   _vertScratch = new Vector3[_capLayout.VertexCount];
        ///   _uvScratch   = new Vector2[_capLayout.VertexCount];
        ///   for (int i = 0; i &lt; poolSize; i++)
        ///       pool[i] = NoteCapGeometryBuilder.BuildCapMesh("Name", _capLayout);
        /// </code>
        /// </para>
        ///
        /// <para>Values below the minimum of 3 are silently clamped — the builder
        /// never produces degenerate mesh topology.</para>
        /// </summary>
        /// <param name="columnCount">
        /// Desired arc column count.  Pass
        /// <see cref="PlayerSettingsStore.NoteCapArcSegments"/> (default 5).
        /// </param>
        public static NoteCapLayout CreateLayout(int columnCount)
        {
            return new NoteCapLayout(columnCount);
        }

        // -------------------------------------------------------------------
        // Mesh template builder  (called once in Awake — zero runtime cost)
        // -------------------------------------------------------------------

        /// <summary>
        /// Builds a mesh template with stable triangle topology and placeholder UVs.
        /// Vertex positions are zeros — overwritten each frame by
        /// <see cref="FillCapVerticesEdgeAware"/>.
        /// </summary>
        /// <param name="meshName">Debug name shown in the Unity Profiler / Inspector.</param>
        /// <param name="layout">Layout from <see cref="CreateLayout"/>; controls column count
        /// and therefore vertex / index counts.</param>
        public static Mesh BuildCapMesh(string meshName, NoteCapLayout layout)
        {
            var mesh = new Mesh { name = meshName };

            // Placeholder zero vertices — overwritten per frame in LateUpdate.
            mesh.vertices = new Vector3[layout.VertexCount];

            // UV: U sweeps left→right (0..1) across cap width; V = 0 on tail, 1 on head.
            // Compatible with fixed-edge + tiled-center UV layout (spec §5.7.3).
            var uvs = new Vector2[layout.VertexCount];
            for (int i = 0; i <= layout.ColumnCount; i++)
            {
                float u = (float)i / layout.ColumnCount;
                uvs[layout.TailRow + i] = new Vector2(u, 0f); // tail column i
                uvs[layout.HeadRow + i] = new Vector2(u, 1f); // head column i
            }
            mesh.uv = uvs;

            // Triangles: N quads, each as two CCW triangles when viewed from front.
            //
            //  Quad column i:
            //    tail_i   = TailRow + i
            //    tail_i1  = TailRow + i + 1
            //    head_i   = HeadRow + i
            //    head_i1  = HeadRow + i + 1
            //
            //  Tri A: tail_i, tail_i1, head_i1  (bottom-left, bottom-right, top-right)
            //  Tri B: tail_i, head_i1, head_i   (bottom-left, top-right,    top-left)
            var tris = new int[layout.IndexCount];
            for (int i = 0; i < layout.ColumnCount; i++)
            {
                int tailI  = layout.TailRow + i;
                int tailI1 = layout.TailRow + i + 1;
                int headI  = layout.HeadRow + i;
                int headI1 = layout.HeadRow + i + 1;

                int t = i * 6;
                tris[t + 0] = tailI;   tris[t + 1] = tailI1;  tris[t + 2] = headI1; // Tri A
                tris[t + 3] = tailI;   tris[t + 4] = headI1;  tris[t + 5] = headI;  // Tri B
            }
            mesh.triangles = tris;

            mesh.RecalculateBounds();
            return mesh;
        }

        // -------------------------------------------------------------------
        // Per-frame vertex fill  (zero allocations)
        // -------------------------------------------------------------------

        /// <summary>
        /// Writes curved-cap vertex positions into a pre-allocated scratch buffer
        /// using <b>uniform angular steps</b> across the note cap.
        ///
        /// <para>This is the uniform-angle variant.  The edge-aware variant
        /// (<see cref="FillCapVerticesEdgeAware"/>) is preferred for production
        /// renderers — it places column boundaries at chord positions matching the
        /// three skin regions so that geometry and UV boundaries agree exactly.</para>
        ///
        /// <para><b>Vertex layout:</b>
        ///   Tail row = verts[<c>layout.TailRow</c> .. TailRow+ColumnCount] at <paramref name="tailR"/>.
        ///   Head row = verts[<c>layout.HeadRow</c> .. HeadRow+ColumnCount] at <paramref name="headR"/>.
        /// </para>
        /// </summary>
        /// <param name="verts">Pre-allocated scratch, length ≥ <c>layout.VertexCount</c>.</param>
        /// <param name="layout">Layout from <see cref="CreateLayout"/>.</param>
        /// <param name="ctr">PlayfieldRoot local-space XY position of the arena centre.</param>
        /// <param name="tailR">Inner radius of the note band (PlayfieldLocal units).</param>
        /// <param name="headR">Outer radius of the note band (PlayfieldLocal units).</param>
        /// <param name="centerDeg">Lane centre angle in degrees.</param>
        /// <param name="noteHalfAngleDeg">Note cap half-span in degrees.</param>
        /// <param name="innerLocal">Arena inner band radius — for frustum Z.</param>
        /// <param name="outerLocal">Arena outer band radius — for frustum Z.</param>
        /// <param name="hInner">Frustum cone height at the inner band edge.</param>
        /// <param name="hOuter">Frustum cone height at the outer band edge.</param>
        /// <param name="zOffset">Additional Z lift above the frustum surface.</param>
        public static void FillCapVertices(
            Vector3[]     verts,
            NoteCapLayout layout,
            Vector2       ctr,
            float         tailR,
            float         headR,
            float         centerDeg,
            float         noteHalfAngleDeg,
            float         innerLocal,
            float         outerLocal,
            float         hInner,
            float         hOuter,
            float         zOffset = 0f)
        {
            // Frustum Z depends only on radius — compute once per row, not per vertex.
            float zTail = NoteApproachMath.FrustumZAtRadius(tailR, innerLocal, outerLocal, hInner, hOuter) + zOffset;
            float zHead = NoteApproachMath.FrustumZAtRadius(headR, innerLocal, outerLocal, hInner, hOuter) + zOffset;

            // Angular sweep: left boundary to right boundary, uniform steps.
            float leftDeg = centerDeg - noteHalfAngleDeg;
            float stepDeg = (layout.ColumnCount > 0) ? (2f * noteHalfAngleDeg / layout.ColumnCount) : 0f;

            for (int i = 0; i <= layout.ColumnCount; i++)
            {
                float colRad = (leftDeg + i * stepDeg) * Mathf.Deg2Rad;
                float cosA   = Mathf.Cos(colRad);
                float sinA   = Mathf.Sin(colRad);

                verts[layout.TailRow + i] = new Vector3(
                    ctr.x + tailR * cosA,
                    ctr.y + tailR * sinA,
                    zTail);

                verts[layout.HeadRow + i] = new Vector3(
                    ctr.x + headR * cosA,
                    ctr.y + headR * sinA,
                    zHead);
            }
        }

        // -------------------------------------------------------------------
        // Edge-aware helpers  (shared by FillCapVerticesEdgeAware + FillCapUVs)
        // -------------------------------------------------------------------

        /// <summary>
        /// Resolves effective physical edge widths after the narrow-width fallback.
        ///
        /// <para>When <paramref name="totalChord"/> is narrower than the sum of the
        /// two skin edge widths, both edges are scaled down proportionally so they
        /// still fit inside the note without inverting.  The center collapses to zero.</para>
        /// </summary>
        private static void ComputeEdgeWidths(
            float  totalChord,
            float  skinLeftW,
            float  skinRightW,
            out float leftW,
            out float centerW,
            out float rightW)
        {
            leftW  = skinLeftW;
            rightW = skinRightW;

            float sumEdgeW = leftW + rightW;
            if (sumEdgeW > totalChord && sumEdgeW > 0f)
            {
                float scale = totalChord / sumEdgeW;
                leftW  *= scale;
                rightW *= scale;
            }
            centerW = Mathf.Max(0f, totalChord - leftW - rightW);
        }

        /// <summary>
        /// Returns the chord distance from the left cap boundary to column boundary
        /// <paramref name="i"/>, using the 1:(N-2):1 edge-aware column allocation from
        /// <paramref name="layout"/>.
        ///
        /// <para>Column boundaries (for ColumnCount = 5 as example):
        /// <list type="bullet">
        ///   <item><c>i = 0</c> → 0 (left cap edge)</item>
        ///   <item><c>i = 1</c> → leftW (end of left border)</item>
        ///   <item><c>i = 2..4</c> → subdivides centerW uniformly</item>
        ///   <item><c>i = 5</c> → totalChord (right cap edge)</item>
        /// </list>
        /// </para>
        /// </summary>
        private static float EdgeAwareChordAtColumn(
            int i, NoteCapLayout layout, float leftW, float centerW, float rightW)
        {
            // Left decorative border: columns 0 .. LeftEdgeCols
            if (i <= layout.LeftEdgeCols)
                return (i * leftW) / layout.LeftEdgeCols;

            int ci = i - layout.LeftEdgeCols;

            // Tiled center: columns LeftEdgeCols .. LeftEdgeCols + CenterCols
            if (ci <= layout.CenterCols)
                return leftW + (ci * centerW) / layout.CenterCols;

            // Right decorative border: columns LeftEdgeCols+CenterCols .. ColumnCount
            int ri        = i - layout.LeftEdgeCols - layout.CenterCols;
            int rightCols = layout.ColumnCount - layout.LeftEdgeCols - layout.CenterCols; // always 1
            return leftW + centerW + (ri * rightW) / rightCols;
        }

        // -------------------------------------------------------------------
        // Per-frame vertex fill — edge-aware  (zero allocations)
        // -------------------------------------------------------------------

        /// <summary>
        /// Writes curved-cap vertex positions using <b>edge-aware column placement</b>.
        ///
        /// <para>Column boundaries are placed at chord distances matching the three skin
        /// regions (fixed left border / tiled center / fixed right border), using the
        /// same <see cref="EdgeAwareChordAtColumn"/> helper as <see cref="FillCapUVs"/>.
        /// This guarantees that mesh column edges and UV region boundaries agree —
        /// no asymmetry artefacts possible.</para>
        ///
        /// <para>This is the preferred path for all four production note cap renderers
        /// (Tap, Catch, Flick, and Hold head cap).</para>
        ///
        /// <para>Column angular positions are derived from chord distances via the
        /// inverse chord formula:
        /// <code>
        ///   chord = EdgeAwareChordAtColumn(i, layout, leftW, centerW, rightW)
        ///   deltaAngleDeg = 2 × Asin(chord / (2 × noteRadiusLocal)) × Rad2Deg
        ///   colDeg = (centerDeg − noteHalfAngleDeg) + deltaAngleDeg
        /// </code>
        /// </para>
        /// </summary>
        /// <param name="verts">Pre-allocated scratch, length ≥ <c>layout.VertexCount</c>.</param>
        /// <param name="layout">Layout from <see cref="CreateLayout"/>.</param>
        /// <param name="ctr">PlayfieldRoot local-space XY position of the arena centre.</param>
        /// <param name="tailR">Inner radius of the note band.</param>
        /// <param name="headR">Outer radius of the note band.</param>
        /// <param name="centerDeg">Lane centre angle in degrees.</param>
        /// <param name="noteHalfAngleDeg">Note cap half-span in degrees.</param>
        /// <param name="noteRadiusLocal">Note approach radius — converts chord to arc angle.
        /// Must match the value passed to <see cref="FillCapUVs"/>.</param>
        /// <param name="leftEdgeLocalWidth">Physical left border width (PlayfieldLocal units).</param>
        /// <param name="rightEdgeLocalWidth">Physical right border width (PlayfieldLocal units).</param>
        /// <param name="innerLocal">Arena inner band radius — for frustum Z.</param>
        /// <param name="outerLocal">Arena outer band radius — for frustum Z.</param>
        /// <param name="hInner">Frustum cone height at the inner band edge.</param>
        /// <param name="hOuter">Frustum cone height at the outer band edge.</param>
        /// <param name="zOffset">Additional Z lift above the frustum surface.</param>
        public static void FillCapVerticesEdgeAware(
            Vector3[]     verts,
            NoteCapLayout layout,
            Vector2       ctr,
            float         tailR,
            float         headR,
            float         centerDeg,
            float         noteHalfAngleDeg,
            float         noteRadiusLocal,
            float         leftEdgeLocalWidth,
            float         rightEdgeLocalWidth,
            float         innerLocal,
            float         outerLocal,
            float         hInner,
            float         hOuter,
            float         zOffset = 0f)
        {
            // Frustum Z per row — same as FillCapVertices (depends only on radius).
            float zTail = NoteApproachMath.FrustumZAtRadius(tailR, innerLocal, outerLocal, hInner, hOuter) + zOffset;
            float zHead = NoteApproachMath.FrustumZAtRadius(headR, innerLocal, outerLocal, hInner, hOuter) + zOffset;

            // Total chord of the note cap at the approach radius.
            float totalChord = 2f * noteRadiusLocal * Mathf.Sin(noteHalfAngleDeg * Mathf.Deg2Rad);

            // Resolve effective physical edge widths (with narrow-width fallback).
            ComputeEdgeWidths(totalChord, leftEdgeLocalWidth, rightEdgeLocalWidth,
                out float leftW, out float centerW, out float rightW);

            // Left angular boundary of the note cap.
            float leftDeg = centerDeg - noteHalfAngleDeg;

            for (int i = 0; i <= layout.ColumnCount; i++)
            {
                // Chord from the left cap boundary to this column boundary.
                float chordToI = EdgeAwareChordAtColumn(i, layout, leftW, centerW, rightW);

                // Convert chord back to arc angle at the approach radius.
                // chordToI = 2r × sin(deltaAngle/2)  →  deltaAngle = 2 × Asin(chordToI / 2r)
                //
                // Clamp the Asin argument to [−1, 1] to guard against floating-point
                // overrun (chordToI can briefly exceed totalChord by tiny FP amounts).
                float deltaAngleDeg = 0f;
                if (noteRadiusLocal > 0f)
                {
                    float sinArg = Mathf.Clamp(chordToI / (2f * noteRadiusLocal), -1f, 1f);
                    deltaAngleDeg = 2f * Mathf.Asin(sinArg) * Mathf.Rad2Deg;
                }

                float colRad = (leftDeg + deltaAngleDeg) * Mathf.Deg2Rad;
                float cosA   = Mathf.Cos(colRad);
                float sinA   = Mathf.Sin(colRad);

                verts[layout.TailRow + i] = new Vector3(
                    ctr.x + tailR * cosA,
                    ctr.y + tailR * sinA,
                    zTail);

                verts[layout.HeadRow + i] = new Vector3(
                    ctr.x + headR * cosA,
                    ctr.y + headR * sinA,
                    zHead);
            }
        }

        // -------------------------------------------------------------------
        // Per-frame UV fill  (zero allocations — spec §5.7.3 step 3)
        // -------------------------------------------------------------------

        /// <summary>
        /// Writes fixed-edge + tiled-center UV coordinates into a pre-allocated scratch buffer.
        ///
        /// <para><b>Three-region layout (spec §5.7.3):</b>
        /// <code>
        ///   [ left border | ← tiled center → | right border ]
        ///   ← fixed UV  →←  tiles with width →←  fixed UV  →
        /// </code>
        /// </para>
        ///
        /// <para>Uses <see cref="EdgeAwareChordAtColumn"/> (the same helper used by
        /// <see cref="FillCapVerticesEdgeAware"/>) so UV region boundaries land exactly
        /// on the same column edges as the mesh vertices — geometry and UV cannot disagree.</para>
        ///
        /// <para><b>V convention:</b>
        /// Normal (<paramref name="flipBodyVertical"/> = false): V = 0 on tail, V = 1 on head.
        /// Flipped: V = 1 on tail, V = 0 on head — inverts the texture vertically.</para>
        ///
        /// <para><b>Center-anchored tiling:</b>
        /// Phase is measured from the midpoint of the center region so the visual pattern
        /// stays anchored at the note midpoint as the note width changes.</para>
        /// </summary>
        /// <param name="uvs">Pre-allocated scratch, length ≥ <c>layout.VertexCount</c>.</param>
        /// <param name="layout">Layout from <see cref="CreateLayout"/>.</param>
        /// <param name="noteRadiusLocal">Note approach radius in PlayfieldLocal units.
        /// Must match the value passed to <see cref="FillCapVerticesEdgeAware"/> for
        /// this note on the same frame.</param>
        /// <param name="noteHalfAngleDeg">Note cap half-span in degrees.</param>
        /// <param name="skin">Active <see cref="NoteSkinSet"/> — must not be null.</param>
        /// <param name="flipBodyVertical">When true, swaps V so tail = 1, head = 0.</param>
        public static void FillCapUVs(
            Vector2[]     uvs,
            NoteCapLayout layout,
            float         noteRadiusLocal,
            float         noteHalfAngleDeg,
            NoteSkinSet   skin,
            bool          flipBodyVertical = false)
        {
            // Total chord width at the approach radius.
            float totalChord = 2f * noteRadiusLocal * Mathf.Sin(noteHalfAngleDeg * Mathf.Deg2Rad);

            // Physical edge widths with narrow-width fallback — identical resolution to
            // FillCapVerticesEdgeAware so both methods agree on leftW/centerW/rightW.
            ComputeEdgeWidths(totalChord,
                skin.bodyLeftEdgeLocalWidth, skin.bodyRightEdgeLocalWidth,
                out float leftW, out float centerW, out float rightW);

            // Chord position where the right decorative border begins.
            float rightEdgeStartChord = totalChord - rightW;

            // UV region boundaries from NoteSkinSet.
            float leftEdgeU    = skin.bodyLeftEdgeU;
            float rightEdgeU   = skin.bodyRightEdgeU;
            float centerUStart = skin.CenterUStart;  // == bodyLeftEdgeU
            float centerUWidth = skin.CenterUWidth;  // == 1 - bodyRightEdgeU - bodyLeftEdgeU

            // Tile rate: repetitions per PlayfieldLocal unit. Guard against ≤ 0.
            float tileRate = Mathf.Max(0.01f, skin.bodyCenterTileRatePerUnit);

            // Per-column UV assignment.
            // Column chord positions come from EdgeAwareChordAtColumn — the same helper
            // used by FillCapVerticesEdgeAware.  UV region transitions therefore land
            // exactly on mesh column edges: no UV/geometry mismatch.
            for (int i = 0; i <= layout.ColumnCount; i++)
            {
                float chordToI = EdgeAwareChordAtColumn(i, layout, leftW, centerW, rightW);

                float u;

                if (chordToI <= leftW)
                {
                    // Left decorative border: linear ramp 0 → bodyLeftEdgeU.
                    u = (leftW > 0f) ? (chordToI / leftW) * leftEdgeU : 0f;
                }
                else if (chordToI >= rightEdgeStartChord)
                {
                    // Right decorative border: linear ramp (1-bodyRightEdgeU) → 1.
                    u = (rightW > 0f)
                        ? (1f - rightEdgeU) + ((chordToI - rightEdgeStartChord) / rightW) * rightEdgeU
                        : 1f;
                }
                else
                {
                    // Tiled center (center-anchored).
                    // Phase is measured from the center midpoint so the visual pattern
                    // stays anchored at the note midpoint as centerW grows and shrinks.
                    float signedDistFromCenter = chordToI - (leftW + centerW * 0.5f);
                    float tileFrac = Mathf.Repeat(signedDistFromCenter * tileRate + 0.5f, 1f);
                    u = centerUStart + tileFrac * centerUWidth;
                }

                // Both rows share the same U; V separates tail from head.
                float vTail = flipBodyVertical ? 1f : 0f;
                float vHead = flipBodyVertical ? 0f : 1f;
                uvs[layout.TailRow + i] = new Vector2(u, vTail);
                uvs[layout.HeadRow + i] = new Vector2(u, vHead);
            }
        }

        // -------------------------------------------------------------------
        // Hold ribbon width-side UV helper (fixed-edge + tiled-center, U axis only)
        // V mapping (length axis) is intentionally deferred — not computed here.
        // Note: this helper has no dependency on column count (no NoteCapLayout needed).
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes the U coordinate for a point at chord distance
        /// <paramref name="chordFromLeft"/> from the left edge of a hold ribbon,
        /// using the fixed-left-edge + tiled-center + fixed-right-edge UV layout.
        ///
        /// <para>This method is independent of note cap column count — it operates on
        /// the hold ribbon width axis only and does not require a <see cref="NoteCapLayout"/>.</para>
        ///
        /// <para>Three-region layout (U axis = ribbon width direction):
        /// <code>
        ///   [ left border | ← tiled center → | right border ]
        ///   ← fixed UV  →←  tiles with chord →←  fixed UV  →
        /// </code>
        /// </para>
        ///
        /// <para><b>Narrow-width fallback:</b>
        /// When <paramref name="totalChord"/> is less than
        /// <c>holdLeftEdgeLocalWidth + holdRightEdgeLocalWidth</c>, both edge widths
        /// are scaled proportionally so they still fit, and the center collapses to zero.</para>
        /// </summary>
        /// <param name="chordFromLeft">Chord distance from the left ribbon edge [0..totalChord].</param>
        /// <param name="totalChord">Full chord width of the ribbon in PlayfieldLocal units.</param>
        /// <param name="skin">Active <see cref="NoteSkinSet"/> — must not be null.</param>
        /// <returns>U value in UV space for the given chord position.</returns>
        public static float ComputeHoldWidthU(float chordFromLeft, float totalChord, NoteSkinSet skin)
        {
            // Effective physical edge widths (narrow-width fallback via same helper).
            ComputeEdgeWidths(totalChord,
                skin.holdLeftEdgeLocalWidth, skin.holdRightEdgeLocalWidth,
                out float leftW, out float centerW, out float rightW);

            float rightEdgeStartChord = totalChord - rightW;

            float leftEdgeU    = skin.holdLeftEdgeU;
            float rightEdgeU   = skin.holdRightEdgeU;
            float centerUStart = skin.HoldCenterUStart;
            float centerUWidth = skin.HoldCenterUWidth;
            float tileRate     = Mathf.Max(0.01f, skin.holdCenterTileRatePerUnit);

            if (chordFromLeft <= leftW)
            {
                return (leftW > 0f) ? (chordFromLeft / leftW) * leftEdgeU : 0f;
            }
            else if (chordFromLeft >= rightEdgeStartChord)
            {
                return (rightW > 0f)
                    ? (1f - rightEdgeU) + ((chordFromLeft - rightEdgeStartChord) / rightW) * rightEdgeU
                    : 1f;
            }
            else
            {
                // Center-anchored tiling — same phase convention as FillCapUVs.
                float signedDistFromCenter = chordFromLeft - (leftW + centerW * 0.5f);
                float tileFrac = Mathf.Repeat(signedDistFromCenter * tileRate + 0.5f, 1f);
                return centerUStart + tileFrac * centerUWidth;
            }
        }

        // -------------------------------------------------------------------
        // Angular occupancy helper
        // -------------------------------------------------------------------

        /// <summary>
        /// Converts lane half-width and note occupancy ratio into the note's
        /// angular half-span in degrees.
        ///
        /// <para>Linear approximation: <c>noteHalf ≈ laneHalf × ratio</c>.
        /// For typical lane half-widths (5–25°) the error versus the exact
        /// arcsin formula is under 1%.</para>
        /// </summary>
        /// <param name="laneHalfWidthDeg">Lane half-width = WidthDeg × 0.5.</param>
        /// <param name="noteLaneWidthRatio">Note occupancy fraction [0.1..1.0].</param>
        public static float NoteHalfAngleDeg(float laneHalfWidthDeg, float noteLaneWidthRatio)
        {
            return laneHalfWidthDeg * noteLaneWidthRatio;
        }
    }
}
