// PlayerDebugRenderer.cs
// DEBUG SCAFFOLDING — remove before shipping.
//
// Draws arena arc bands, lane center rays, active note markers, and the last
// touch hit point in the Game view using LineRenderers.
//
// Geometry points are computed in PlayfieldRoot local XY, then converted to
// world space via:
//   playfieldRoot.TransformPoint(localX, localY, 0)
//
// This is intentionally simple: no fancy materials, no pooling beyond a fixed
// note-marker array, no per-frame allocations for the static arc/lane lines.
//
// Wiring (done in Unity Editor, not here):
//   1) Create an empty GameObject in the PlayerBoot scene named "DebugRenderer".
//   2) Add component PlayerDebugRenderer.
//   3) Assign the existing PlayerAppController to the Inspector field.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RhythmicFlow.Player
{
    /// <summary>
    /// DEBUG SCAFFOLDING: Visual overlay for arenas, lanes, notes, and touch hits.
    /// All geometry uses PlayfieldRoot local XY → world via TransformPoint (spec §5.4).
    /// </summary>
    [AddComponentMenu("RhythmicFlow/Debug/PlayerDebugRenderer")]
    public class PlayerDebugRenderer : MonoBehaviour
    {
        // -------------------------------------------------------------------
        // Inspector
        // -------------------------------------------------------------------

        [Header("DEBUG SCAFFOLDING — remove before shipping")]

        [Tooltip("The PlayerAppController in the scene.")]
        [SerializeField] private PlayerAppController playerAppController;

        [Tooltip("Optional: if assigned, arena/lane outlines are lifted to sit on the frustum " +
                 "surface. If null, all lines are drawn at z=0 (flat interaction plane).")]
        [SerializeField] private PlayerDebugArenaSurface arenaSurface;

        [Tooltip("Number of line segments used to approximate each arc (higher = smoother).")]
        [SerializeField] private int arcSegments = 48;

        [Tooltip("Width of all debug lines in world units.")]
        [SerializeField] private float lineWidth = 0.01f;

        [Tooltip("Half-size of note / touch-hit diamond markers in world units.")]
        [SerializeField] private float markerHalfSize = 0.04f;

        [Tooltip("Maximum simultaneous note markers drawn (pool size).")]
        [SerializeField] private int maxNoteMarkers = 32;

        [Header("Note Approach (DEBUG)")]
        [Tooltip("How many ms before a note's hit time it becomes visible and starts approaching.")]
        [SerializeField] private int noteLeadTimeMs = 2000;

        [Tooltip("Spawn radius as a fraction of band width from the inner edge (0 = inner, 1 = outer).")]
        [Range(0f, 1f)]
        [SerializeField] private float spawnRadiusFactor = 0.25f;

        [Tooltip("If true, show notes within noteLeadTimeMs even when outside the narrow judgement window.")]
        [SerializeField] private bool showNotesOutsideWindow = true;

        [Header("Colors")]
        [SerializeField] private Color arenaColor = Color.cyan;
        [SerializeField] private Color laneColor  = Color.yellow;
        [SerializeField] private Color noteColor  = Color.white;
        [SerializeField] private Color touchColor = Color.red;

        // -------------------------------------------------------------------
        // Internal state
        // -------------------------------------------------------------------

        // True once arena/lane LineRenderers have been built from geometry.
        private bool _geometryBuilt;

        // Shared unlit material — visible in Game view on all platforms.
        private Material _lineMat;

        // Per-arena: 4 LineRenderers — [0] outer arc, [1] inner arc,
        //                              [2] start-angle ray, [3] end-angle ray.
        private readonly Dictionary<string, LineRenderer[]> _arenaLRs =
            new Dictionary<string, LineRenderer[]>(StringComparer.Ordinal);

        // Per-lane: 1 LineRenderer — center ray from inner to outer radius.
        private readonly Dictionary<string, LineRenderer> _laneLRs =
            new Dictionary<string, LineRenderer>(StringComparer.Ordinal);

        // Fixed pool of note-marker LineRenderers (diamond shape).
        // Markers beyond maxNoteMarkers are silently dropped — debug acceptable.
        private LineRenderer[] _notePool;

        // Single touch-hit marker LineRenderer.
        private LineRenderer _touchLR;

        // -------------------------------------------------------------------
        // Unity lifecycle
        // -------------------------------------------------------------------

        private void Awake()
        {
            // "Sprites/Default" is unlit and requires no extra shader setup.
            _lineMat = new Material(Shader.Find("Sprites/Default"));
            _lineMat.hideFlags = HideFlags.HideAndDontSave;
        }

        private void OnDestroy()
        {
            if (_lineMat != null) { Destroy(_lineMat); }
        }

        private void LateUpdate()
        {
            // LateUpdate runs after PlayerAppController.Update(), so note state
            // is already this frame's when we read DebugActiveNotes.

            if (playerAppController == null) { return; }

            // Defer geometry build until PlayerAppController.Start() has run and
            // populated the geometry dictionaries (they are null before then).
            if (!_geometryBuilt)
            {
                TryBuildStaticGeometry();
                return; // nothing to draw yet
            }

            UpdateNoteMarkers();
            UpdateTouchMarker();
        }

        // -------------------------------------------------------------------
        // Static geometry build (one-shot, deferred until controller is ready)
        // -------------------------------------------------------------------

        private void TryBuildStaticGeometry()
        {
            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            IReadOnlyDictionary<string, LaneGeometry>  lanes  = playerAppController.DebugLaneGeometries;
            IReadOnlyDictionary<string, string>        lToA   = playerAppController.DebugLaneToArena;
            PlayfieldTransform                         pfT    = playerAppController.DebugPlayfieldTransform;

            // Not ready yet — controller hasn't finished Start().
            if (arenas == null || lanes == null || lToA == null || pfT == null) { return; }

            Transform pfRoot = playerAppController.playfieldRoot;

            // Arena arc bands — 4 LRs each.
            foreach (KeyValuePair<string, ArenaGeometry> kvp in arenas)
            {
                BuildArenaLineRenderers(kvp.Key, kvp.Value, pfT, pfRoot);
            }

            // Lane center rays — 1 LR each.
            foreach (KeyValuePair<string, LaneGeometry> kvp in lanes)
            {
                if (!lToA.TryGetValue(kvp.Key, out string arenaId))        { continue; }
                if (!arenas.TryGetValue(arenaId, out ArenaGeometry arena))  { continue; }
                BuildLaneLineRenderer(kvp.Key, kvp.Value, arena, pfT, pfRoot);
            }

            // Note marker pool (diamonds, reused each LateUpdate).
            _notePool = new LineRenderer[maxNoteMarkers];
            for (int i = 0; i < maxNoteMarkers; i++)
            {
                _notePool[i] = CreateLineRenderer($"NoteMarker_{i}", noteColor);
                _notePool[i].positionCount = 5;       // diamond: 4 corners + close
                _notePool[i].gameObject.SetActive(false);
            }

            // Touch hit marker (diamond).
            _touchLR = CreateLineRenderer("TouchMarker", touchColor);
            _touchLR.positionCount = 5;
            _touchLR.gameObject.SetActive(false);

            _geometryBuilt = true;
        }

        // -------------------------------------------------------------------
        // Arena builders
        // -------------------------------------------------------------------

        // Builds 4 LineRenderers for one arena:
        //   [0] outer arc polyline
        //   [1] inner arc polyline
        //   [2] radial ray at arcStartDeg     (inner → outer)
        //   [3] radial ray at arcStartDeg + arcSweepDeg  (inner → outer)
        private void BuildArenaLineRenderers(
            string arenaId, ArenaGeometry geo, PlayfieldTransform pfT, Transform pfRoot)
        {
            // Compute local-unit radii (spec §5.5).
            float outerLocal = pfT.NormRadiusToLocal(geo.OuterRadiusNorm);
            float bandLocal  = pfT.NormRadiusToLocal(geo.BandThicknessNorm);
            float innerLocal = outerLocal - bandLocal;

            // Arena center in PlayfieldRoot local XY (spec §5.5).
            Vector2 center = pfT.NormalizedToLocal(new Vector2(geo.CenterXNorm, geo.CenterYNorm));

            var lrs = new LineRenderer[4];

            lrs[0] = CreateLineRenderer($"Arena_{arenaId}_OuterArc", arenaColor);
            SetArcPositions(lrs[0], center, outerLocal, geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: 1f);

            lrs[1] = CreateLineRenderer($"Arena_{arenaId}_InnerArc", arenaColor);
            SetArcPositions(lrs[1], center, innerLocal, geo.ArcStartDeg, geo.ArcSweepDeg, pfRoot, s01: 0f);

            lrs[2] = CreateLineRenderer($"Arena_{arenaId}_StartRay", arenaColor);
            SetRadialPositions(lrs[2], center, innerLocal, outerLocal, geo.ArcStartDeg, pfRoot);

            // Raw un-normalized angle is fine — cos/sin handle any float correctly.
            lrs[3] = CreateLineRenderer($"Arena_{arenaId}_EndRay", arenaColor);
            SetRadialPositions(lrs[3], center, innerLocal, outerLocal,
                               geo.ArcStartDeg + geo.ArcSweepDeg, pfRoot);

            _arenaLRs[arenaId] = lrs;
        }

        // -------------------------------------------------------------------
        // Lane builder
        // -------------------------------------------------------------------

        // Builds 1 LineRenderer: a ray along lane.CenterDeg from inner to outer radius.
        private void BuildLaneLineRenderer(
            string laneId, LaneGeometry lane, ArenaGeometry arena,
            PlayfieldTransform pfT, Transform pfRoot)
        {
            float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
            float bandLocal  = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
            float innerLocal = outerLocal - bandLocal;
            Vector2 center   = pfT.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));

            LineRenderer lr = CreateLineRenderer($"Lane_{laneId}_Center", laneColor);
            SetRadialPositions(lr, center, innerLocal, outerLocal, lane.CenterDeg, pfRoot);
            _laneLRs[laneId] = lr;
        }

        // -------------------------------------------------------------------
        // Per-frame: note markers
        // -------------------------------------------------------------------

        // Draws note diamonds approaching the judgement ring (outerLocal, spec §5.8).
        //
        // Approach formula:
        //   timeToHitMs = note.PrimaryTimeMs - effectiveChartTimeMs
        //   alpha       = 1 - clamp01(timeToHitMs / noteLeadTimeMs)
        //               → 0 at spawn (timeToHitMs == noteLeadTimeMs)
        //               → 1 at hit   (timeToHitMs == 0)
        //   spawnR      = innerLocal + spawnRadiusFactor * (outerLocal - innerLocal)
        //   r           = lerp(spawnR, outerLocal, alpha)
        //
        // With showNotesOutsideWindow=true we read DebugAllNotes (all Active notes up to
        // ActivationLeadMs away) and filter to noteLeadTimeMs ourselves, giving a smooth
        // approach well before the note enters the narrow judgement window.
        private void UpdateNoteMarkers()
        {
            if (_notePool == null) { return; }

            IReadOnlyDictionary<string, ArenaGeometry> arenas = playerAppController.DebugArenaGeometries;
            IReadOnlyDictionary<string, LaneGeometry>  lanes  = playerAppController.DebugLaneGeometries;
            IReadOnlyDictionary<string, string>        lToA   = playerAppController.DebugLaneToArena;
            PlayfieldTransform                         pfT    = playerAppController.DebugPlayfieldTransform;
            Transform                                  pfRoot = playerAppController.playfieldRoot;

            if (arenas == null || lanes == null || lToA == null || pfT == null)
            {
                DisableAllNoteMarkers();
                return;
            }

            // Broader source shows approaching notes; narrow source shows only hittable ones.
            IReadOnlyList<RuntimeNote> noteSource = showNotesOutsideWindow
                ? playerAppController.DebugAllNotes
                : playerAppController.DebugActiveNotes;

            double chartTimeMs   = playerAppController.DebugEffectiveChartTimeMs;
            double greatWindowMs = playerAppController.DebugGreatWindowMs;

            int poolIdx = 0;

            if (noteSource != null)
            {
                for (int i = 0; i < noteSource.Count && poolIdx < _notePool.Length; i++)
                {
                    RuntimeNote note = noteSource[i];

                    // Only draw notes that are in the Active lifecycle state.
                    if (note.State != NoteState.Active) { continue; }

                    // Time from now until the note should be hit (positive = future).
                    double timeToHitMs = note.PrimaryTimeMs - chartTimeMs;

                    // Too far ahead — not yet in the approach window.
                    if (timeToHitMs > noteLeadTimeMs) { continue; }

                    // Past the miss deadline — nothing left to show.
                    if (timeToHitMs < -greatWindowMs) { continue; }

                    if (!lanes.TryGetValue(note.LaneId, out LaneGeometry lane))   { continue; }
                    if (!lToA.TryGetValue(note.LaneId,  out string arenaId))      { continue; }
                    if (!arenas.TryGetValue(arenaId,    out ArenaGeometry arena)) { continue; }

                    float outerLocal = pfT.NormRadiusToLocal(arena.OuterRadiusNorm);
                    float bandLocal  = pfT.NormRadiusToLocal(arena.BandThicknessNorm);
                    float innerLocal = outerLocal - bandLocal;

                    // Spawn radius: a fraction of the band width above the inner edge.
                    float spawnR = innerLocal + spawnRadiusFactor * (outerLocal - innerLocal);

                    // alpha 0→1 as note travels from spawn to judgement radius.
                    // Guard against noteLeadTimeMs == 0 to avoid division by zero.
                    float alpha = (noteLeadTimeMs > 0)
                        ? 1f - Mathf.Clamp01((float)timeToHitMs / noteLeadTimeMs)
                        : 1f;

                    // Current radius along the lane center ray.
                    float r = Mathf.Lerp(spawnR, outerLocal, alpha);

                    Vector2 center   = pfT.NormalizedToLocal(new Vector2(arena.CenterXNorm, arena.CenterYNorm));
                    float thetaRad   = AngleUtil.Normalize360(lane.CenterDeg) * Mathf.Deg2Rad;
                    Vector2 localPt  = center + new Vector2(Mathf.Cos(thetaRad), Mathf.Sin(thetaRad)) * r;

                    // Lift the note marker onto the frustum surface. VISUAL ONLY — s01 derived
                    // from current approach radius r, not used for hit-testing anywhere.
                    float s01Note    = (outerLocal > innerLocal)
                        ? Mathf.Clamp01((r - innerLocal) / (outerLocal - innerLocal))
                        : 1f;
                    Vector3 worldPos = pfRoot.TransformPoint(localPt.x, localPt.y, VisualOnlyLocalZ(s01Note));

                    // Fade from dim (spawn) to full-bright (hit) so far notes don't clutter.
                    Color c = noteColor;
                    c.a = Mathf.Lerp(0.3f, 1.0f, alpha);
                    _notePool[poolIdx].startColor = c;
                    _notePool[poolIdx].endColor   = c;

                    _notePool[poolIdx].gameObject.SetActive(true);
                    SetDiamondPositions(_notePool[poolIdx], worldPos, pfRoot);
                    poolIdx++;
                }
            }

            // Disable unused pool entries.
            DisableNoteMarkersFrom(poolIdx);
        }

        private void DisableAllNoteMarkers()  => DisableNoteMarkersFrom(0);

        private void DisableNoteMarkersFrom(int startIdx)
        {
            for (int i = startIdx; i < _notePool.Length; i++)
            {
                _notePool[i].gameObject.SetActive(false);
            }
        }

        // -------------------------------------------------------------------
        // Per-frame: touch marker
        // -------------------------------------------------------------------

        // Shows a diamond at the last touch hit point in PlayfieldLocal space.
        private void UpdateTouchMarker()
        {
            if (_touchLR == null) { return; }

            if (playerAppController.DebugHasTouchHit)
            {
                Vector2 hitLocal = playerAppController.DebugLastTouchLocalXY;
                Vector3 worldPos = playerAppController.playfieldRoot
                                       .TransformPoint(hitLocal.x, hitLocal.y, 0f);
                _touchLR.gameObject.SetActive(true);
                SetDiamondPositions(_touchLR, worldPos, playerAppController.playfieldRoot);
            }
            else
            {
                _touchLR.gameObject.SetActive(false);
            }
        }

        // -------------------------------------------------------------------
        // LineRenderer position helpers
        // -------------------------------------------------------------------

        // VISUAL ONLY — never use this Z value for hit-testing or judgement.
        // Returns the PlayfieldRoot local Z for a point at normalized band position s01:
        //   s01 = 0  →  inner edge  (FrustumHeightInner)
        //   s01 = 1  →  outer edge  (FrustumHeightOuter)
        // Falls back to 0 when no arenaSurface is assigned or frustum profile is off.
        private float VisualOnlyLocalZ(float s01)
        {
            if (arenaSurface != null && arenaSurface.UseFrustumProfile)
            {
                return Mathf.Lerp(arenaSurface.FrustumHeightInner,
                                  arenaSurface.FrustumHeightOuter, s01);
            }
            return 0f;
        }

        // Sets N world-space positions along a circular arc.
        // startDeg/sweepDeg are raw floats — cos/sin handle any value correctly.
        // s01: normalized band position (0=inner, 1=outer) — used for frustum Z only.
        private void SetArcPositions(
            LineRenderer lr,
            Vector2      centerLocal,
            float        radius,
            float        startDeg,
            float        sweepDeg,
            Transform    pfRoot,
            float        s01 = 0f)   // VISUAL ONLY: 0=inner edge, 1=outer edge
        {
            int   n = Mathf.Max(2, arcSegments);
            float z = VisualOnlyLocalZ(s01); // VISUAL ONLY — not used for hit-testing
            lr.positionCount = n;

            for (int i = 0; i < n; i++)
            {
                float t   = (n > 1) ? (float)i / (n - 1) : 0f;
                float deg = startDeg + t * sweepDeg;
                float rad = deg * Mathf.Deg2Rad;

                Vector2 pt = centerLocal + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;
                lr.SetPosition(i, pfRoot.TransformPoint(pt.x, pt.y, z));
            }
        }

        // Sets 2 world-space positions: inner edge → outer edge along a radial ray.
        // Each endpoint uses its own frustum Z so the ray lies on the cone surface.
        private void SetRadialPositions(
            LineRenderer lr,
            Vector2      centerLocal,
            float        innerLocal,
            float        outerLocal,
            float        angleDeg,
            Transform    pfRoot)
        {
            float rad = angleDeg * Mathf.Deg2Rad;
            Vector2 dir   = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            Vector2 inner = centerLocal + dir * innerLocal;
            Vector2 outer = centerLocal + dir * outerLocal;

            lr.positionCount = 2;
            lr.SetPosition(0, pfRoot.TransformPoint(inner.x, inner.y, VisualOnlyLocalZ(0f))); // VISUAL ONLY
            lr.SetPosition(1, pfRoot.TransformPoint(outer.x, outer.y, VisualOnlyLocalZ(1f))); // VISUAL ONLY
        }

        // Sets 5 world-space positions forming a diamond in the PlayfieldRoot XY plane.
        // pfRoot.right / pfRoot.up are the in-plane axes (spec §5.4: localZ is the normal).
        private void SetDiamondPositions(LineRenderer lr, Vector3 worldCenter, Transform pfRoot)
        {
            Vector3 r = pfRoot.right * markerHalfSize;
            Vector3 u = pfRoot.up   * markerHalfSize;

            lr.positionCount = 5;
            lr.SetPosition(0, worldCenter + u);   // top
            lr.SetPosition(1, worldCenter + r);   // right
            lr.SetPosition(2, worldCenter - u);   // bottom
            lr.SetPosition(3, worldCenter - r);   // left
            lr.SetPosition(4, worldCenter + u);   // top (close loop)
        }

        // -------------------------------------------------------------------
        // LineRenderer factory
        // -------------------------------------------------------------------

        // Creates a child GameObject with a configured LineRenderer.
        private LineRenderer CreateLineRenderer(string goName, Color color)
        {
            var go = new GameObject(goName);
            go.transform.SetParent(transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            lr.material      = _lineMat;
            lr.startColor    = color;
            lr.endColor      = color;
            lr.startWidth    = lineWidth;
            lr.endWidth      = lineWidth;
            lr.useWorldSpace = true;
            lr.positionCount = 0;
            return lr;
        }
    }
}
