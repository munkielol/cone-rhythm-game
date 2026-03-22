// ChartRuntimeEvaluator.cs
// Single authoritative per-frame chart geometry evaluator.
//
// BOTH the Player App and the Chart Editor Playfield Preview use this class
// to sample animated arena/lane/camera tracks at a given timeMs.  All
// keyframe interpolation, angle wrap-correction, and enabled-bool decoding
// live here — callers just call Evaluate(timeMs) and read from the output
// arrays.
//
// Design goals:
//   1) Construct ONCE per loaded chart (preallocates all output arrays).
//   2) Evaluate(timeMs) writes into those arrays — ZERO per-frame allocations.
//   3) O(1) lookup by arenaId / laneId via pre-built index dictionaries.
//   4) Angle tracks use FloatTrack.EvaluateAngleDeg (shortest-path wrap, spec §5.9).
//   5) Enabled tracks decoded as bool: value >= 0.5 → true (spec §5.9).
//
// Thread safety: designed for Unity main thread only.
//
// Spec anchors:
//   Player  §3.3  — offsets apply to all geometry evaluation.
//   Player  §5.6  — enabled vs. opacity semantics.
//   Player  §5.9  — keyframe evaluation rules (clamp, angle wrap, enabled).
//   Editor  §3.3  — Playfield Preview must use the same evaluator and math
//                    as the Player App.

using System;
using System.Collections.Generic;

namespace RhythmicFlow.Shared
{
    /// <summary>
    /// Evaluates all animated chart tracks (arenas, lanes, camera) at a given timeMs.
    ///
    /// <para>Create one instance per loaded chart; reuse it every frame by calling
    /// <see cref="Evaluate"/>. Allocation-free during evaluation — all buffers are
    /// preallocated in the constructor.</para>
    ///
    /// <para>Access results via <see cref="GetArena"/>, <see cref="GetLane"/>,
    /// <see cref="TryGetArena"/>, <see cref="TryGetLane"/>, or the index-lookup
    /// helpers. Read <see cref="Camera"/> for the evaluated camera state.</para>
    /// </summary>
    public sealed class ChartRuntimeEvaluator
    {
        // -------------------------------------------------------------------
        // Pre-allocated output arrays (written by Evaluate, read by callers)
        // -------------------------------------------------------------------

        private readonly EvaluatedArena[] _arenas;
        private readonly EvaluatedLane[]  _lanes;
        private          EvaluatedCamera  _camera;

        // Source chart — retained for per-frame evaluation; never mutated.
        private readonly ChartJsonV1 _chart;

        // -------------------------------------------------------------------
        // Index maps: arenaId/laneId → array index
        // Built once in constructor; never reallocated.
        // -------------------------------------------------------------------

        private readonly Dictionary<string, int> _arenaIdToIndex;
        private readonly Dictionary<string, int> _laneIdToIndex;

        // -------------------------------------------------------------------
        // Public read-only interface
        // -------------------------------------------------------------------

        /// <summary>Number of arenas in the chart (0 if chart.arenas is null or empty).</summary>
        public int ArenaCount => _arenas.Length;

        /// <summary>Number of lanes in the chart (0 if chart.lanes is null or empty).</summary>
        public int LaneCount => _lanes.Length;

        /// <summary>Evaluated camera state from the most recent <see cref="Evaluate"/> call.</summary>
        public EvaluatedCamera Camera => _camera;

        /// <summary>
        /// Returns the evaluated arena at array index <paramref name="i"/>
        /// (0 ≤ i &lt; <see cref="ArenaCount"/>). Corresponds to chart.arenas[i].
        /// </summary>
        public EvaluatedArena GetArena(int i) => _arenas[i];

        /// <summary>
        /// Returns the evaluated lane at array index <paramref name="i"/>
        /// (0 ≤ i &lt; <see cref="LaneCount"/>). Corresponds to chart.lanes[i].
        /// </summary>
        public EvaluatedLane GetLane(int i) => _lanes[i];

        /// <summary>
        /// Returns the array index for the given arenaId, or false if not found.
        /// Use <see cref="GetArena"/> to retrieve the value.
        /// </summary>
        public bool TryGetArenaIndex(string arenaId, out int index)
            => _arenaIdToIndex.TryGetValue(arenaId, out index);

        /// <summary>
        /// Returns the array index for the given laneId, or false if not found.
        /// Use <see cref="GetLane"/> to retrieve the value.
        /// </summary>
        public bool TryGetLaneIndex(string laneId, out int index)
            => _laneIdToIndex.TryGetValue(laneId, out index);

        /// <summary>
        /// Retrieves the evaluated arena for the given arenaId.
        /// Returns false (arena = default) if the arenaId is not in the chart.
        /// </summary>
        public bool TryGetArena(string arenaId, out EvaluatedArena arena)
        {
            if (_arenaIdToIndex.TryGetValue(arenaId, out int i))
            {
                arena = _arenas[i];
                return true;
            }
            arena = default;
            return false;
        }

        /// <summary>
        /// Retrieves the evaluated lane for the given laneId.
        /// Returns false (lane = default) if the laneId is not in the chart.
        /// </summary>
        public bool TryGetLane(string laneId, out EvaluatedLane lane)
        {
            if (_laneIdToIndex.TryGetValue(laneId, out int i))
            {
                lane = _lanes[i];
                return true;
            }
            lane = default;
            return false;
        }

        // -------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------

        /// <summary>
        /// Constructs the evaluator for the given chart.  Preallocates output arrays
        /// and populates immutable identity fields (ArenaId, LaneId, etc.).
        /// Also performs an initial <see cref="Evaluate(int)">Evaluate(0)</see> so the
        /// arrays are populated with valid data before the first gameplay frame.
        /// </summary>
        /// <param name="chart">A parsed, validated chart.  Must not be null.</param>
        /// <exception cref="ArgumentNullException">Thrown if chart is null.</exception>
        public ChartRuntimeEvaluator(ChartJsonV1 chart)
        {
            _chart = chart ?? throw new ArgumentNullException(nameof(chart));

            int arenaCount = chart.arenas?.Count ?? 0;
            int laneCount  = chart.lanes?.Count  ?? 0;

            _arenas = new EvaluatedArena[arenaCount];
            _lanes  = new EvaluatedLane[laneCount];

            _arenaIdToIndex = new Dictionary<string, int>(arenaCount, StringComparer.Ordinal);
            _laneIdToIndex  = new Dictionary<string, int>(laneCount,  StringComparer.Ordinal);

            // Pre-populate immutable identity fields and build lookup maps.
            // These never change after construction; only animated values change.
            for (int i = 0; i < arenaCount; i++)
            {
                ChartArena src = chart.arenas[i];
                if (src == null || string.IsNullOrEmpty(src.arenaId)) { continue; }

                _arenas[i].ArenaId = src.arenaId;
                _arenaIdToIndex[src.arenaId] = i;
            }

            for (int i = 0; i < laneCount; i++)
            {
                ChartLane src = chart.lanes[i];
                if (src == null || string.IsNullOrEmpty(src.laneId)) { continue; }

                _lanes[i].LaneId   = src.laneId;
                _lanes[i].ArenaId  = src.arenaId ?? string.Empty;
                _lanes[i].Priority = src.priority;
                _laneIdToIndex[src.laneId] = i;
            }

            // Initial evaluation: populate animated values before first Update.
            Evaluate(0);
        }

        // -------------------------------------------------------------------
        // Per-frame evaluation  (allocation-free)
        // -------------------------------------------------------------------

        /// <summary>
        /// Evaluates all animated tracks at <paramref name="timeMs"/> and writes the
        /// results into the internal arrays.
        ///
        /// <para>Call once per frame before reading any geometry.
        /// Performs zero heap allocations.</para>
        /// </summary>
        /// <param name="timeMs">Current effective chart time in ms (spec §3.3 — includes all offsets).</param>
        public void Evaluate(int timeMs)
        {
            EvaluateArenas(timeMs);
            EvaluateLanes(timeMs);
            EvaluateCameraInternal(timeMs);
        }

        // -------------------------------------------------------------------
        // Private evaluation workers
        // -------------------------------------------------------------------

        private void EvaluateArenas(int timeMs)
        {
            int count = _chart.arenas?.Count ?? 0;
            for (int i = 0; i < count && i < _arenas.Length; i++)
            {
                ChartArena src = _chart.arenas[i];
                if (src == null) { continue; }

                // Enabled: decoded from 0/1 float track.
                // Spec §5.9: "enabledBool = (value >= 0.5)"
                _arenas[i].EnabledBool      = src.enabled.Evaluate(timeMs, 1f) >= 0.5f;
                _arenas[i].Opacity          = src.opacity.Evaluate(timeMs, 1f);
                _arenas[i].CenterXNorm      = src.centerX.Evaluate(timeMs, 0.5f);
                _arenas[i].CenterYNorm      = src.centerY.Evaluate(timeMs, 0.5f);
                _arenas[i].OuterRadiusNorm  = src.outerRadius.Evaluate(timeMs, 0.4f);
                _arenas[i].BandThicknessNorm = src.bandThickness.Evaluate(timeMs, 0.1f);

                // Angle tracks: shortest-path wrap interpolation (spec §5.9).
                _arenas[i].ArcStartDeg = src.arcStartDeg.EvaluateAngleDeg(timeMs, 0f);

                // arcSweepDeg is a scalar span (0..360), not a cyclic angle — use Evaluate.
                _arenas[i].ArcSweepDeg = src.arcSweepDeg.Evaluate(timeMs, 360f);
            }
        }

        private void EvaluateLanes(int timeMs)
        {
            int count = _chart.lanes?.Count ?? 0;
            for (int i = 0; i < count && i < _lanes.Length; i++)
            {
                ChartLane src = _chart.lanes[i];
                if (src == null) { continue; }

                _lanes[i].EnabledBool = src.enabled.Evaluate(timeMs, 1f) >= 0.5f;
                _lanes[i].Opacity     = src.opacity.Evaluate(timeMs, 1f);

                // Angle tracks: wrap-aware (spec §5.9).
                _lanes[i].CenterDeg = src.centerDeg.EvaluateAngleDeg(timeMs, 0f);
                _lanes[i].WidthDeg  = src.widthDeg.Evaluate(timeMs, 30f);

                // LaneId / ArenaId / Priority are immutable — already set in constructor.
            }
        }

        private void EvaluateCameraInternal(int timeMs)
        {
            if (_chart.camera == null)
            {
                _camera = default;
                return;
            }

            ChartCamera src = _chart.camera;

            _camera.EnabledBool = src.enabled.Evaluate(timeMs, 1f) >= 0.5f;
            _camera.PosX        = src.posX.Evaluate(timeMs, 0f);
            _camera.PosY        = src.posY.Evaluate(timeMs, 0f);
            _camera.PosZ        = src.posZ.Evaluate(timeMs, 5f);

            // Camera rotation: angle-aware evaluation (spec §5.9).
            _camera.RotPitchDeg = src.rotPitchDeg.EvaluateAngleDeg(timeMs, 0f);
            _camera.RotYawDeg   = src.rotYawDeg.EvaluateAngleDeg(timeMs, 0f);
            _camera.RotRollDeg  = src.rotRollDeg.EvaluateAngleDeg(timeMs, 0f);

            _camera.FovDeg = src.fovDeg.Evaluate(timeMs, 60f);
        }
    }
}
