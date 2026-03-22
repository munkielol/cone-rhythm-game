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
// ── UV layout (ready for future skin mapping) ────────────────────────────────
//
//    Tail row column i: U = i/N,  V = 0
//    Head row column i: U = i/N,  V = 1
//
//  This is compatible with fixed-edge + tiled-center UV conventions (spec
//  §5.7.3): the next step assigns edge/center UV regions without changing
//  the geometry pipeline.
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
//  Mesh templates are built once in Awake.  Per-frame work writes into a
//  pre-allocated scratch buffer (Vector3[VertexCount]); the scratch is then
//  assigned to the pooled mesh with mesh.vertices = scratch.
//
// ── Isolation note ───────────────────────────────────────────────────────────
//
//  Currently lives in the Player assembly.  When the Chart Editor Playfield
//  Preview (spec §chart_editor §3.3) needs note head geometry, move this file
//  to Assets/_Project/Shared/ so both assemblies share it (spec §5.7.0).
//
// Spec §5.7.a / §5.7.0 step 2.

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
