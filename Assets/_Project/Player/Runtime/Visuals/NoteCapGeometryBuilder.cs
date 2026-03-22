// NoteCapGeometryBuilder.cs
// Shared static geometry builder for N-segment curved-cap note head meshes.
// Used by TapNoteRenderer, CatchNoteRenderer, and FlickNoteRenderer.
//
// ── Purpose ──────────────────────────────────────────────────────────────────
//
//  Centralises curved-cap mesh template creation and per-frame vertex fill so
//  all three production note renderers share identical geometry code without
//  duplicating logic.  All methods are allocation-free at runtime.
//
// ── Geometry: segmented curved-cap (spec §5.7.0 step 2) ─────────────────────
//
//  Replaces the single-segment trapezoid (step 1) with a curved cap that
//  arc-samples across the note's angular span.  N = ColumnCount columns →
//  N+1 column boundaries, N column quads.
//
//  Default: ColumnCount = 5 → visibly follows lane curvature with only 12
//  vertices total (no excessive subdivision).
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
//    Total: N×2 = 10 triangles, N×6 = 30 indices  (for N = 5)
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
// ── Angular occupancy ────────────────────────────────────────────────────────
//
//  noteHalfAngleDeg = laneHalfWidthDeg × noteLaneWidthRatio
//
//  For typical lane half-widths (5–25°), sin(k·x) ≈ k·sin(x), so this matches
//  the step-1 chord-width formula (2r·sin(halfWidthDeg)·ratio) with <1% error
//  while avoiding a per-frame arcsin call.
//
// ── No per-frame allocations ─────────────────────────────────────────────────
//
//  Mesh templates are built once in Awake.  Per-frame work writes into
//  pre-allocated scratch buffers (Vector3[VertexCount] for verts,
//  Vector2[VertexCount] for UVs); the scratches are then assigned to the pooled
//  mesh with mesh.vertices = vertScratch; mesh.uv = uvScratch.
//
// ── Isolation note ───────────────────────────────────────────────────────────
//
//  Currently lives in the Player assembly.  When the Chart Editor Playfield
//  Preview (spec §chart_editor §3.3) needs note head geometry, move this file
//  to Assets/_Project/Shared/ so both assemblies share it (spec §5.7.0).
//
// Spec §5.7.a / §5.7.0 step 2 (geometry) / step 3 (UV).

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Shared static builder for N-segment curved-cap note head meshes (step 2).
    /// Allocation-free at runtime: mesh templates built once in Awake; vertices
    /// overwritten via a pre-allocated scratch buffer each LateUpdate.
    /// </summary>
    internal static class NoteCapGeometryBuilder
    {
        // -------------------------------------------------------------------
        // Subdivision constants
        // -------------------------------------------------------------------

        /// <summary>
        /// Number of angular column quads across the note cap.
        /// 5 columns → 6 column boundaries → visibly follows lane curvature.
        /// Increase if very wide lanes show stepping artefacts.
        /// </summary>
        public const int ColumnCount = 5;

        /// <summary>Total vertex count: 2 rows × (ColumnCount + 1) column lines.</summary>
        public const int VertexCount = (ColumnCount + 1) * 2; // = 12

        /// <summary>Total triangle-index count: ColumnCount quads × 2 tris × 3 verts.</summary>
        public const int IndexCount = ColumnCount * 6; // = 30

        /// <summary>
        /// Start index of the tail row (inner note edge) in the flat vertex array.
        /// Tail row occupies indices [TailRow .. TailRow + ColumnCount].
        /// </summary>
        public const int TailRow = 0;

        /// <summary>
        /// Start index of the head row (outer note edge / front cap) in the flat vertex array.
        /// Head row occupies indices [HeadRow .. HeadRow + ColumnCount].
        /// </summary>
        public const int HeadRow = ColumnCount + 1; // = 6

        // -------------------------------------------------------------------
        // Mesh template builder  (called once in Awake — zero runtime cost)
        // -------------------------------------------------------------------

        /// <summary>
        /// Builds a mesh template with stable triangle topology and UV layout.
        /// Vertex positions are placeholder zeros — overwritten each frame by
        /// <see cref="FillCapVertices"/>.
        /// </summary>
        /// <param name="meshName">Debug name shown in the Unity Profiler / Inspector.</param>
        public static Mesh BuildCapMesh(string meshName)
        {
            var mesh = new Mesh { name = meshName };

            // Placeholder zero vertices — overwritten per frame in LateUpdate.
            mesh.vertices = new Vector3[VertexCount];

            // UV: U sweeps left→right (0..1) across cap width; V = 0 on tail, 1 on head.
            // Compatible with fixed-edge + tiled-center UV layout (spec §5.7.3).
            var uvs = new Vector2[VertexCount];
            for (int i = 0; i <= ColumnCount; i++)
            {
                float u = (float)i / ColumnCount;
                uvs[TailRow + i] = new Vector2(u, 0f); // tail column i
                uvs[HeadRow + i] = new Vector2(u, 1f); // head column i
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
            var tris = new int[IndexCount];
            for (int i = 0; i < ColumnCount; i++)
            {
                int tailI  = TailRow + i;
                int tailI1 = TailRow + i + 1;
                int headI  = HeadRow + i;
                int headI1 = HeadRow + i + 1;

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
        /// Writes curved-cap vertex positions into a pre-allocated scratch buffer.
        /// Call once per visible note each LateUpdate, then assign the buffer to
        /// the pooled mesh: <c>mesh.vertices = verts; mesh.RecalculateBounds();</c>
        ///
        /// <para><b>Vertex layout:</b>
        ///   Tail row = verts[<see cref="TailRow"/> .. TailRow+ColumnCount] at <paramref name="tailR"/>.
        ///   Head row = verts[<see cref="HeadRow"/> .. HeadRow+ColumnCount] at <paramref name="headR"/>.
        /// </para>
        ///
        /// <para>Frustum Z is computed per-row (not per-vertex) because all vertices
        /// in a row share the same radius, and FrustumZAtRadius depends only on radius.</para>
        ///
        /// <para>Arc angles use cos/sin which handle negative and >360° inputs correctly
        /// — no per-call normalisation is required for correctness.</para>
        /// </summary>
        /// <param name="verts">Pre-allocated scratch, length ≥ <see cref="VertexCount"/> (12).</param>
        /// <param name="ctr">PlayfieldRoot local-space XY position of the arena centre.</param>
        /// <param name="tailR">Inner radius of the note band (PlayfieldLocal units).</param>
        /// <param name="headR">Outer radius of the note band (PlayfieldLocal units).</param>
        /// <param name="centerDeg">Lane centre angle in degrees (may be unnormalised; cos/sin handle wrap).</param>
        /// <param name="noteHalfAngleDeg">Note cap half-span in degrees. Use <see cref="NoteHalfAngleDeg"/>.</param>
        /// <param name="innerLocal">Arena inner band radius (PlayfieldLocal) — for frustum Z.</param>
        /// <param name="outerLocal">Arena outer band radius (PlayfieldLocal) — for frustum Z.</param>
        /// <param name="hInner">Frustum cone height at the inner band edge.</param>
        /// <param name="hOuter">Frustum cone height at the outer band edge.</param>
        public static void FillCapVertices(
            Vector3[] verts,
            Vector2   ctr,
            float     tailR,
            float     headR,
            float     centerDeg,
            float     noteHalfAngleDeg,
            float     innerLocal,
            float     outerLocal,
            float     hInner,
            float     hOuter)
        {
            // Frustum Z depends only on radius — compute once per row, not per vertex.
            float zTail = NoteApproachMath.FrustumZAtRadius(tailR, innerLocal, outerLocal, hInner, hOuter);
            float zHead = NoteApproachMath.FrustumZAtRadius(headR, innerLocal, outerLocal, hInner, hOuter);

            // Angular sweep across the note cap: left boundary to right boundary.
            float leftDeg = centerDeg - noteHalfAngleDeg;
            float stepDeg = (ColumnCount > 0) ? (2f * noteHalfAngleDeg / ColumnCount) : 0f;

            for (int i = 0; i <= ColumnCount; i++)
            {
                // Arc angle for this column boundary.
                // Negative angles and angles > 360° are handled correctly by cos/sin.
                float colRad = (leftDeg + i * stepDeg) * Mathf.Deg2Rad;
                float cosA   = Mathf.Cos(colRad);
                float sinA   = Mathf.Sin(colRad);

                // Tail-row vertex: inner radius, note trailing edge.
                verts[TailRow + i] = new Vector3(
                    ctr.x + tailR * cosA,
                    ctr.y + tailR * sinA,
                    zTail);

                // Head-row vertex: outer radius, note front cap.
                verts[HeadRow + i] = new Vector3(
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
        /// Call once per visible note each LateUpdate immediately after
        /// <see cref="FillCapVertices"/>, then assign both buffers to the pooled mesh:
        /// <c>mesh.vertices = vertScratch; mesh.uv = uvScratch; mesh.RecalculateBounds();</c>
        ///
        /// <para><b>Three-region layout (spec §5.7.3):</b>
        ///
        /// <code>
        ///   [ left border | ← tiled center → | right border ]
        ///   ← fixed UV  →←  tiles with width →←  fixed UV  →
        /// </code>
        ///
        /// The left and right decorative border regions occupy fixed physical widths in
        /// PlayfieldLocal units (<see cref="NoteSkinSet.bodyLeftEdgeLocalWidth"/> /
        /// <see cref="NoteSkinSet.bodyRightEdgeLocalWidth"/>).  They map to fixed UV-space
        /// fractions (<see cref="NoteSkinSet.bodyLeftEdgeU"/> /
        /// <see cref="NoteSkinSet.bodyRightEdgeU"/>) and are never distorted by lane width
        /// changes.
        ///
        /// The center region occupies the remaining physical width and tiles horizontally at
        /// <see cref="NoteSkinSet.bodyCenterTileRatePerUnit"/> repetitions per PlayfieldLocal
        /// unit of chord width.
        /// </para>
        ///
        /// <para><b>Narrow-width fallback:</b>
        /// When the total note chord is narrower than the combined edge local widths, both
        /// edge widths are scaled down proportionally so they still fit, and the center
        /// collapses to zero.  UVs remain valid with no inversion.
        /// </para>
        ///
        /// <para><b>V convention:</b>
        /// V = 0 on the tail row (inner edge), V = 1 on the head row (outer edge).
        /// This matches the placeholder UVs written by <see cref="BuildCapMesh"/>.
        /// </para>
        ///
        /// <para><b>Radius choice:</b>
        /// <paramref name="noteRadiusLocal"/> is the note's approach radius (centre of the
        /// note band).  Using the approach centre rather than tailR or headR keeps the ratio
        /// of left-edge : center : right-edge stable as the note travels — the border widths
        /// do not appear to grow or shrink relative to the body as the note approaches.
        /// </para>
        /// </summary>
        /// <param name="uvs">Pre-allocated scratch, length ≥ <see cref="VertexCount"/> (12).</param>
        /// <param name="noteRadiusLocal">Note approach radius in PlayfieldLocal units.
        /// Used to convert <paramref name="noteHalfAngleDeg"/> into a chord width and to
        /// map physical edge widths to column positions.</param>
        /// <param name="noteHalfAngleDeg">Note cap half-span in degrees.  Must be the same
        /// value passed to <see cref="FillCapVertices"/> for this note on the same frame.</param>
        /// <param name="skin">Active <see cref="NoteSkinSet"/> providing UV region fractions,
        /// physical edge widths, and tile rate.  Must not be null.</param>
        public static void FillCapUVs(
            Vector2[]   uvs,
            float       noteRadiusLocal,
            float       noteHalfAngleDeg,
            NoteSkinSet skin)
        {
            // ── Total chord width at the approach radius ──────────────────────────
            //
            // The cap spans 2 × noteHalfAngleDeg of arc at noteRadiusLocal.
            // The chord (straight-line distance) subtending that full angular span is:
            //
            //   totalChord = 2 × r × sin(noteHalfAngleDeg × Deg2Rad)
            //
            // This is exact — the same formula used by NoteApproachMath.LaneChordWidthAtRadius,
            // applied here to the note's own angular half-span.
            float totalChord = 2f * noteRadiusLocal * Mathf.Sin(noteHalfAngleDeg * Mathf.Deg2Rad);

            // ── Physical edge widths with narrow-width fallback ───────────────────
            //
            // Left and right decorative borders each occupy a fixed physical width
            // (PlayfieldLocal units) that does NOT scale with lane width.  If the total
            // chord is narrower than their combined width, scale both down proportionally
            // so they still fill the note without inverting.  Center collapses to zero.
            float leftW  = skin.bodyLeftEdgeLocalWidth;
            float rightW = skin.bodyRightEdgeLocalWidth;

            float sumEdgeW = leftW + rightW;
            if (sumEdgeW > totalChord && sumEdgeW > 0f)
            {
                // Proportional scale: leftW/rightW maintain their ratio to each other
                // but together now exactly fill totalChord.
                float scale = totalChord / sumEdgeW;
                leftW  *= scale;
                rightW *= scale;
            }

            // Physical width of the tiled center after edges are reserved.
            // Mathf.Max guards against tiny negative values from floating-point drift.
            float centerW = Mathf.Max(0f, totalChord - leftW - rightW);

            // Chord distance at which the right decorative border begins.
            float rightEdgeStartChord = totalChord - rightW;

            // ── UV region boundaries (from NoteSkinSet) ───────────────────────────
            //
            // Texture layout:
            //   [0 .. bodyLeftEdgeU]              ← left decorative border
            //   [bodyLeftEdgeU .. 1-bodyRightEdgeU] ← tiled center (CenterUStart..CenterUEnd)
            //   [1-bodyRightEdgeU .. 1]            ← right decorative border
            float leftEdgeU    = skin.bodyLeftEdgeU;
            float rightEdgeU   = skin.bodyRightEdgeU;
            float centerUStart = skin.CenterUStart;  // == bodyLeftEdgeU
            float centerUWidth = skin.CenterUWidth;  // == 1 - bodyRightEdgeU - bodyLeftEdgeU

            // Tile rate: repetitions per PlayfieldLocal unit of center chord width.
            // NoteSkinSet.OnValidate guarantees > 0, but guard defensively.
            float tileRate = Mathf.Max(0.01f, skin.bodyCenterTileRatePerUnit);

            // ── Per-column UV assignment ──────────────────────────────────────────
            //
            // Column i (0..ColumnCount) is at angular offset i × stepDeg from the left
            // cap boundary, where stepDeg = 2 × noteHalfAngleDeg / ColumnCount.
            //
            // The chord distance from the left cap edge to column i is derived from
            // the arc geometry:  the angular separation between column 0 and column i
            // is i × stepDeg, so the subtended chord is:
            //
            //   chordToI = 2 × r × sin( i × noteHalfAngleDeg / ColumnCount × Deg2Rad )
            //            = 2 × r × sin( i × stepHalfAngleDeg × Deg2Rad )
            //
            // where stepHalfAngleDeg = noteHalfAngleDeg / ColumnCount.
            //
            // Verification:
            //   i = 0  → sin(0)                 = 0           = left cap edge   ✓
            //   i = N  → sin(noteHalfAngleDeg)  = totalChord/2r → totalChord    ✓
            //
            // We use noteRadiusLocal (approach centre) for all columns.  The tail and
            // head rows share the same U — only V differs (0 vs 1).
            float stepHalfAngleDeg = noteHalfAngleDeg / ColumnCount;

            for (int i = 0; i <= ColumnCount; i++)
            {
                // Chord distance from the left cap boundary to this column boundary.
                float chordToI = 2f * noteRadiusLocal
                    * Mathf.Sin(i * stepHalfAngleDeg * Mathf.Deg2Rad);

                float u;

                if (chordToI <= leftW)
                {
                    // ── Left decorative border ────────────────────────────────────
                    // Linear ramp: U goes from 0 (left cap edge) to bodyLeftEdgeU
                    // (border/center boundary) as chordToI goes from 0 to leftW.
                    // Guard: if leftW was collapsed to 0 (zero-width cap), map to U=0.
                    u = (leftW > 0f)
                        ? (chordToI / leftW) * leftEdgeU
                        : 0f;
                }
                else if (chordToI >= rightEdgeStartChord)
                {
                    // ── Right decorative border ───────────────────────────────────
                    // Linear ramp: U goes from (1-bodyRightEdgeU) to 1 as chordToI
                    // goes from rightEdgeStartChord to totalChord.
                    // Guard: if rightW was collapsed to 0, map to U=1.
                    u = (rightW > 0f)
                        ? (1f - rightEdgeU) + ((chordToI - rightEdgeStartChord) / rightW) * rightEdgeU
                        : 1f;
                }
                else
                {
                    // ── Tiled center region ───────────────────────────────────────
                    // Position within the center in PlayfieldLocal units.
                    float posInCenter = chordToI - leftW;

                    // Tile the center UV region horizontally.
                    // One tile = (1 / tileRate) PlayfieldLocal units.
                    // Mathf.Repeat(x, 1f) = fractional part of x — allocation-free.
                    // The result maps into [CenterUStart .. CenterUStart + CenterUWidth].
                    float tileFrac = Mathf.Repeat(posInCenter * tileRate, 1f);
                    u = centerUStart + tileFrac * centerUWidth;
                }

                // Both rows share the same U value; V separates tail (0) from head (1).
                uvs[TailRow + i] = new Vector2(u, 0f);
                uvs[HeadRow + i] = new Vector2(u, 1f);
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
        /// <c>arcsin(sin(laneHalf) × ratio)</c> formula is under 1%, and the
        /// result matches step 1's chord-width scaling behaviour.</para>
        /// </summary>
        /// <param name="laneHalfWidthDeg">Lane half-width = WidthDeg × 0.5.</param>
        /// <param name="noteLaneWidthRatio">Note occupancy fraction [0.1..1.0].</param>
        public static float NoteHalfAngleDeg(float laneHalfWidthDeg, float noteLaneWidthRatio)
        {
            return laneHalfWidthDeg * noteLaneWidthRatio;
        }
    }
}
