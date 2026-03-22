// NoteApproachRenderer.cs
//
// ┌─────────────────────────────────────────────────────────────────────────┐
// │  TRANSITIONAL / DEBUG RENDERER — NOT THE PRODUCTION PATH               │
// │                                                                         │
// │  This renderer is superseded by the per-type production renderers:      │
// │    TapNoteRenderer   (spec §5.7.a)                                      │
// │    CatchNoteRenderer (spec §5.7.a)                                      │
// │    FlickNoteRenderer (spec §5.7.a)                                      │
// │                                                                         │
// │  It may remain in the scene as a debug / prototyping aid while the      │
// │  production renderers are being developed, but it must NOT be treated   │
// │  as a production code path. Disable or remove it once the production    │
// │  renderers have been verified in the Inspector.                         │
// │                                                                         │
// │  Per spec §5.7.a: "NoteApproachRenderer is transitional/debug only,    │
// │  not production path."                                                  │
// └─────────────────────────────────────────────────────────────────────────┘
//
// Renders Tap, Flick, and Catch notes as thin trapezoid quads approaching the
// judgement ring.  Uses the same canonical approach formula and lane-width-at-
// radius math as HoldBodyRenderer — both delegate to NoteApproachMath.
//
// ══════════════════════════════════════════════════════════════════════
//  APPROACH FORMULA  (spec §6.1 / §5.7.1 — NoteApproachMath)
//
//   timeToHitMs = noteTimeMs − chartTimeMs
//   alpha       = 1 − Clamp01( timeToHitMs / noteLeadTimeMs )
//   r           = Lerp( spawnR, judgementR, alpha )
//
//   Note head sits at radius r with a small radial thickness:
//     headR = r + noteHalfThicknessLocal
//     tailR = r − noteHalfThicknessLocal
//   Width at each radius = 2 · r · sin(halfWidthDeg) · noteWidthRatio
// ══════════════════════════════════════════════════════════════════════
//
//  RENDERING
//   Each note head = one pre-allocated trapezoid mesh (same pool pattern as
//   HoldBodyRenderer).  Drawn via Graphics.DrawMesh so it appears in the Game
//   view without Gizmos.  MaterialPropertyBlock used for per-note color.
//
//  NOTE VISIBILITY FILTER
//   • Hidden if note is not yet active (timeToHit > noteLeadTimeMs).
//   • Hidden once note is Hit (judged successfully).
//   • Missed notes still visible (dim) until chartTime > noteTimeMs + greatWindowMs.
//   • Tap/Flick/Catch notes are instantaneous — no "hold-like" extended lifetime.
//
// Spec §5.7 / §5.7.1 / §6.1.

using UnityEngine;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// Production gameplay renderer for Tap, Flick, and Catch note heads.
    /// Draws an approaching arc-quad per note using the spec-canonical approach
    /// formula.  Not a Gizmo; visible in the Game view at all times.
    ///
    /// <para>Attach to any GameObject in the Player scene.
    /// Assign <see cref="playerAppController"/> and one or more Materials in the Inspector.
    /// No prefab edits required.</para>
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Visuals/NoteApproachRenderer")]
    public class NoteApproachRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector — Wiring
        // -------------------------------------------------------------------

        [Header("Wiring")]
        [Tooltip("The PlayerAppController this renderer reads from.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Material for note heads.  Use an unlit shader with _Color support.")]
        [SerializeField] private Material noteHeadMaterial;

        // -------------------------------------------------------------------
        // Inspector — Phase colors
        // -------------------------------------------------------------------

        [Header("Note Colors")]
        [Tooltip("Color for Tap note heads.")]
        [SerializeField] private Color tapColor = new Color(1.0f, 1.0f, 1.0f, 0.95f);

        [Tooltip("Color for Flick note heads.")]
        [SerializeField] private Color flickColor = new Color(1.0f, 0.65f, 0.1f, 0.95f);

        [Tooltip("Color for Catch note heads.")]
        [SerializeField] private Color catchColor = new Color(0.3f, 1.0f, 0.4f, 0.95f);

        [Tooltip("Color tint applied when a note is Missed (dim, non-judging ghost).")]
        [SerializeField] private Color missedTintColor = new Color(0.4f, 0.4f, 0.4f, 0.55f);

        // -------------------------------------------------------------------
        // Inspector — Frustum surface alignment (matches HoldBodyRenderer)
        // -------------------------------------------------------------------

        [Header("Frustum Surface Alignment")]
        [Tooltip("Optional: when assigned, frustum height values are read from this component " +
                 "automatically (keeps note heads on the same surface as hold ribbons).")]
        [SerializeField] private PlayerDebugArenaSurface arenaSurface;

        [Tooltip("When true and no arenaSurface is assigned, note heads are lifted onto the " +
                 "frustum cone using the manual height values below.")]
        [SerializeField] private bool useFrustumProfile = true;

        [Tooltip("Local Z at the inner ring edge.  Default: 0.001.")]
        [SerializeField] private float frustumHeightInner = 0.001f;

        [Tooltip("Local Z at the outer ring edge.  Default: 0.15.")]
        [SerializeField] private float frustumHeightOuter = 0.15f;

        // -------------------------------------------------------------------
        // Inspector — Approach
        // -------------------------------------------------------------------

        [Header("Approach")]
        [Tooltip("How many ms before the note's timeMs it first becomes visible.\n\n" +
                 "MUST match noteLeadTimeMs in HoldBodyRenderer and PlayerDebugRenderer " +
                 "so all note types appear consistently.  Default: 2000.")]
        [SerializeField] private int noteLeadTimeMs = 2000;

        [Tooltip("Spawn radius fraction within the approach path (0 = inner arc, 1 = judgement ring). " +
                 "Keep at 0 for v0 to match hold ribbon behaviour.")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnRadiusFactor = 0f;

        // -------------------------------------------------------------------
        // Inspector — Sizing
        // -------------------------------------------------------------------

        [Header("Note Head Sizing")]
        [Tooltip("Radial half-thickness of the note head quad in PlayfieldLocal units.  " +
                 "The note head spans [r − half, r + half] radially.  Default: 0.022.")]
        [SerializeField] private float noteHalfThicknessLocal = 0.022f;

        [Tooltip("Note head width as a fraction of the lane chord width at the note's radius.  " +
                 "1.0 = full lane width.  Default: 0.85.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float noteWidthRatio = 0.85f;

        // -------------------------------------------------------------------
        // Internals — mesh pool
        // -------------------------------------------------------------------

        // Pre-allocated trapezoid meshes — one per simultaneously visible note.
        // Pool is filled in Awake; vertices overwritten per-frame (no GC).
        private const int MaxNotePool = 128;

        private Mesh[]  _meshPool;
        private int     _poolUsed;

        private readonly Vector3[] _vertScratch = new Vector3[4];

        // Reused every DrawMesh call.
        private MaterialPropertyBlock _propBlock;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            _meshPool = new Mesh[MaxNotePool];
            for (int i = 0; i < MaxNotePool; i++)
            {
                _meshPool[i] = BuildTrapezoidMesh();
            }
            _propBlock = new MaterialPropertyBlock();
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
        }

        private void LateUpdate()
        {
            if (playerAppController == null || noteHeadMaterial == null || _meshPool == null)
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
                || pfTf == null || pfRoot == null) { return; }

            double chartTimeMs   = playerAppController.EffectiveChartTimeMs;
            double greatWindowMs = playerAppController.GreatWindowMs;
            Matrix4x4 localToWorld = pfRoot.localToWorldMatrix;

            _poolUsed = 0;

            foreach (RuntimeNote note in allNotes)
            {
                // Only handle tap, flick, catch — holds are rendered by HoldBodyRenderer.
                if (note.Type != NoteType.Tap   &&
                    note.Type != NoteType.Flick  &&
                    note.Type != NoteType.Catch) { continue; }

                // Skip successfully judged notes.
                if (note.State == NoteState.Hit) { continue; }

                // Compute time-to-event.
                double timeToHit = note.PrimaryTimeMs - chartTimeMs;

                // Not yet on screen.
                if (timeToHit > noteLeadTimeMs) { continue; }

                // Past the miss window — stop drawing.
                if (timeToHit < -(double)greatWindowMs) { continue; }

                // Pool exhausted.
                if (_poolUsed >= MaxNotePool) { break; }

                // Look up lane and arena geometry.
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

                // Note head spans [r − half, r + half] radially.
                float headR = Mathf.Min(r + noteHalfThicknessLocal, judgementR);
                float tailR = Mathf.Max(r - noteHalfThicknessLocal, spawnR);

                if (headR - tailR < 0.0001f) { continue; } // degenerate, skip

                // ── Color (type + state) ─────────────────────────────────────────────────
                Color baseColor;
                switch (note.Type)
                {
                    case NoteType.Flick: baseColor = flickColor;  break;
                    case NoteType.Catch: baseColor = catchColor;  break;
                    default:             baseColor = tapColor;    break;
                }

                // Dim missed notes to a ghost color.
                Color noteColor = (note.State == NoteState.Missed)
                    ? new Color(baseColor.r * missedTintColor.r,
                                baseColor.g * missedTintColor.g,
                                baseColor.b * missedTintColor.b,
                                baseColor.a * missedTintColor.a)
                    : baseColor;

                _propBlock.SetColor("_Color", noteColor);

                // ── Local-space axes ─────────────────────────────────────────────────────
                float thetaRad = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                float cosT     = Mathf.Cos(thetaRad);
                float sinT     = Mathf.Sin(thetaRad);

                // Tangential direction (perpendicular to radial, in local XY).
                var tangLocal = new Vector3(-sinT, cosT, 0f);

                // ── 3D endpoints (XY + frustum Z) ────────────────────────────────────────
                Vector2 ctr = pfTf.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

                float hInner = ReadFrustumHeightInner();
                float hOuter = ReadFrustumHeightOuter();

                var tailLocal3 = new Vector3(
                    ctr.x + tailR * cosT,
                    ctr.y + tailR * sinT,
                    NoteApproachMath.FrustumZAtRadius(tailR, innerLocal, outerLocal, hInner, hOuter));

                var headLocal3 = new Vector3(
                    ctr.x + headR * cosT,
                    ctr.y + headR * sinT,
                    NoteApproachMath.FrustumZAtRadius(headR, innerLocal, outerLocal, hInner, hOuter));

                // ── Trapezoid width (spec §5.7 — lane width at radius) ───────────────────
                float halfWidthDeg = lane.WidthDeg * 0.5f;
                float widthHead = NoteApproachMath.LaneChordWidthAtRadius(headR, halfWidthDeg) * noteWidthRatio;
                float widthTail = NoteApproachMath.LaneChordWidthAtRadius(tailR, halfWidthDeg) * noteWidthRatio;

                // ── Vertices (same layout as HoldBodyRenderer) ───────────────────────────
                _vertScratch[0] = tailLocal3 - tangLocal * (widthTail * 0.5f); // tail-left
                _vertScratch[1] = tailLocal3 + tangLocal * (widthTail * 0.5f); // tail-right
                _vertScratch[2] = headLocal3 + tangLocal * (widthHead * 0.5f); // head-right
                _vertScratch[3] = headLocal3 - tangLocal * (widthHead * 0.5f); // head-left

                Mesh mesh = _meshPool[_poolUsed++];
                mesh.vertices = _vertScratch;
                mesh.RecalculateBounds();

                Graphics.DrawMesh(mesh, localToWorld, noteHeadMaterial,
                    gameObject.layer, null, 0, _propBlock);
            }
        }

        // -------------------------------------------------------------------
        // Frustum height helpers (read from arenaSurface if assigned)
        // -------------------------------------------------------------------

        private float ReadFrustumHeightInner()
        {
            if (arenaSurface != null && arenaSurface.UseFrustumProfile)
            {
                return arenaSurface.FrustumHeightInner;
            }
            return useFrustumProfile ? frustumHeightInner : 0.002f;
        }

        private float ReadFrustumHeightOuter()
        {
            if (arenaSurface != null && arenaSurface.UseFrustumProfile)
            {
                return arenaSurface.FrustumHeightOuter;
            }
            return useFrustumProfile ? frustumHeightOuter : 0.002f;
        }

        // -------------------------------------------------------------------
        // Trapezoid mesh template (same pattern as HoldBodyRenderer)
        // -------------------------------------------------------------------

        private static Mesh BuildTrapezoidMesh()
        {
            var mesh = new Mesh { name = "NoteHeadTrapezoid" };

            mesh.vertices = new Vector3[4];

            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f), // [0] tail-left
                new Vector2(1f, 0f), // [1] tail-right
                new Vector2(1f, 1f), // [2] head-right
                new Vector2(0f, 1f), // [3] head-left
            };

            // Two CCW triangles covering the quad.
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
