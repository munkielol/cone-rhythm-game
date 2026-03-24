// ArenaOccupancyEvaluator.cs
// Shared runtime helper that computes the current lane occupancy and fill intervals
// for one arena from evaluated lane state.  Zero per-call GC allocation.
//
// ── What this computes ────────────────────────────────────────────────────────
//
//   Given a current EvaluatedArena and the full ChartRuntimeEvaluator, Compute()
//   derives three sets of angular intervals from current evaluated lane state:
//
//     Lane intervals       One per currently enabled lane in the arena, each
//                          clamped to the arena's angular span.  These are the
//                          raw (possibly overlapping) occupied extents before merging.
//
//     Occupied union       The sorted, merged union of all lane intervals.
//                          Overlapping or adjacent lanes are unified so the occupied
//                          set is non-overlapping and exactly covers the lane footprint.
//
//     Fill intervals       The complement of the occupied union within the arena span:
//                          angular regions not covered by any currently enabled lane.
//                          ArenaSurfaceRenderer draws one mesh sector per fill interval.
//
//   If no enabled lanes exist in the arena, the fill set is the entire arena span
//   (one interval) and the occupied/lane sets are empty.  This preserves the
//   previous full-arena rendering when a chart has no lanes yet.
//
// ── Spec anchor ───────────────────────────────────────────────────────────────
//
//   Player spec §5.5.3 — Arena partition model (lane occupancy and fill intervals).
//
// ── Allocation model ──────────────────────────────────────────────────────────
//
//   All scratch arrays are allocated once in the constructor.  Compute() writes
//   into those pre-allocated arrays — zero heap allocation per call.  Safe to call
//   every frame on the Unity main thread.
//
// ── Limitations / deferred ────────────────────────────────────────────────────
//
//   Wrap-around seam: lanes that straddle the 0°/360° seam of a full-circle arena
//   are not split into two intervals here.  CenterDeg is normalised to [0, 360) by
//   ChartRuntimeEvaluator; lanes near the seam may clamp incorrectly when the arena
//   ArcStartDeg is near 0° and ArcSweepDeg ≈ 360°.  This is the same limitation
//   present in ArenaSurfaceRenderer and LaneSurfaceRenderer and is deferred.
//
// ── Usage ─────────────────────────────────────────────────────────────────────
//
//   Instantiate once per consumer (renderer or system) — or share one instance
//   across consumers within the same update phase if they iterate arenas in order.
//   Compute() overwrites all results for the arena passed to it; results from a
//   previous call are no longer valid after the next Compute() call.
//
//   Typical renderer usage (LateUpdate):
//
//     for (int i = 0; i < evaluator.ArenaCount; i++)
//     {
//         EvaluatedArena ea = evaluator.GetArena(i);
//         if (!_occupancy.Compute(ea, evaluator)) { continue; }
//
//         for (int f = 0; f < _occupancy.FillIntervalCount; f++)
//         {
//             AngularInterval fill = _occupancy.GetFillInterval(f);
//             // ... draw fill.StartDeg .. fill.EndDeg ...
//         }
//     }

using UnityEngine;

namespace RhythmicFlow.Shared
{
    // -------------------------------------------------------------------------
    // AngularInterval — simple value type for one [StartDeg, EndDeg] range
    // -------------------------------------------------------------------------

    /// <summary>
    /// An angular interval [<see cref="StartDeg"/>, <see cref="EndDeg"/>] in degrees.
    ///
    /// <para>Both angles are in the same coordinate space as the evaluated arena/lane
    /// angles (0° = +X axis, increasing CCW, not normalised to [0, 360) when the
    /// arena span straddles 360°).</para>
    ///
    /// <para><see cref="EndDeg"/> is always strictly greater than <see cref="StartDeg"/>;
    /// degenerate (zero-width) intervals are never stored by
    /// <see cref="ArenaOccupancyEvaluator"/>.</para>
    /// </summary>
    public readonly struct AngularInterval
    {
        /// <summary>Start angle in degrees.</summary>
        public readonly float StartDeg;

        /// <summary>End angle in degrees.  Always greater than <see cref="StartDeg"/>.</summary>
        public readonly float EndDeg;

        /// <summary>Angular width in degrees (<see cref="EndDeg"/> − <see cref="StartDeg"/>).</summary>
        public float SweepDeg => EndDeg - StartDeg;

        /// <summary>Creates an angular interval.  Caller is responsible for ensuring endDeg > startDeg.</summary>
        public AngularInterval(float startDeg, float endDeg)
        {
            StartDeg = startDeg;
            EndDeg   = endDeg;
        }
    }

    // -------------------------------------------------------------------------
    // ArenaOccupancyEvaluator
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shared runtime helper that computes, for one arena, the current lane
    /// occupancy intervals and the complementary fill intervals from evaluated
    /// lane state.
    ///
    /// <para>Create one instance per consumer and reuse it every frame.
    /// Call <see cref="Compute"/> once per arena per frame, then read the results
    /// via <see cref="LaneIntervalCount"/>/<see cref="GetLaneInterval"/>,
    /// <see cref="OccupiedIntervalCount"/>/<see cref="GetOccupiedInterval"/>, and
    /// <see cref="FillIntervalCount"/>/<see cref="GetFillInterval"/>.</para>
    ///
    /// <para>Zero per-call GC allocation — all arrays are pre-allocated in the
    /// constructor.  Safe to call every frame on the Unity main thread.</para>
    ///
    /// <para>Spec anchor: player spec §5.5.3 — Arena partition model.</para>
    /// </summary>
    public class ArenaOccupancyEvaluator
    {
        // -------------------------------------------------------------------
        // Public constants
        // -------------------------------------------------------------------

        /// <summary>
        /// Default maximum number of lanes per arena.
        /// Lanes beyond this count are silently ignored during <see cref="Compute"/>.
        /// In practice, lane count per arena is well below this limit.
        /// </summary>
        public const int DefaultMaxLanes = 64;

        // -------------------------------------------------------------------
        // Pre-allocated scratch arrays (zero GC per Compute() call)
        // -------------------------------------------------------------------

        // Raw lane intervals collected for the current arena.
        // One entry per enabled lane in the arena, clamped to the arena span.
        // Sorted by left edge before merging.
        private readonly float[] _laneLeft;
        private readonly float[] _laneRight;
        private          int     _laneCount;

        // Merged occupied intervals — sorted, non-overlapping union of all lane intervals.
        // At most _laneLeft.Length entries (one per lane in the degenerate non-overlapping case).
        private readonly float[] _occupiedLeft;
        private readonly float[] _occupiedRight;
        private          int     _occupiedCount;

        // Fill intervals — complement of occupied union within the arena span.
        // At most (_laneLeft.Length + 1) entries:
        //   one gap before the first occupied interval,
        //   one gap between each pair of adjacent occupied intervals,
        //   one gap after the last occupied interval.
        private readonly float[] _fillLeft;
        private readonly float[] _fillRight;
        private          int     _fillCount;

        // Arena angular span from the most recent Compute() call.
        private float _arenaStartDeg;
        private float _arenaEndDeg;

        // -------------------------------------------------------------------
        // Public result properties (valid after a successful Compute() call)
        // -------------------------------------------------------------------

        /// <summary>
        /// Number of raw lane intervals produced by the last <see cref="Compute"/> call.
        /// Each entry is one currently enabled lane's angular extent, clamped to the
        /// arena's span.  Intervals are sorted by <see cref="AngularInterval.StartDeg"/>
        /// but may overlap (use <see cref="OccupiedIntervalCount"/> for the merged set).
        /// </summary>
        public int LaneIntervalCount => _laneCount;

        /// <summary>
        /// Number of merged occupied intervals after <see cref="Compute"/>.
        /// Overlapping or adjacent lane intervals have been merged so no two
        /// occupied intervals overlap.
        /// </summary>
        public int OccupiedIntervalCount => _occupiedCount;

        /// <summary>
        /// Number of fill intervals after <see cref="Compute"/>.
        /// Fill intervals are the complement of the occupied union within the arena
        /// span — the angular regions not currently covered by any enabled lane.
        /// <see cref="ArenaSurfaceRenderer"/> draws one mesh sector per fill interval.
        /// </summary>
        public int FillIntervalCount => _fillCount;

        /// <summary>Arena start angle in degrees from the last <see cref="Compute"/> call.</summary>
        public float ArenaStartDeg => _arenaStartDeg;

        /// <summary>Arena end angle in degrees from the last <see cref="Compute"/> call.</summary>
        public float ArenaEndDeg => _arenaEndDeg;

        // -------------------------------------------------------------------
        // Constructor
        // -------------------------------------------------------------------

        /// <summary>
        /// Creates a new <see cref="ArenaOccupancyEvaluator"/> with pre-allocated
        /// scratch arrays.  All allocation happens here — none during
        /// <see cref="Compute"/>.
        /// </summary>
        /// <param name="maxLanesPerArena">
        /// Maximum number of lanes per arena to support.  Lanes beyond this limit
        /// are silently ignored during <see cref="Compute"/>.
        /// Default: <see cref="DefaultMaxLanes"/> (64).
        /// </param>
        public ArenaOccupancyEvaluator(int maxLanesPerArena = DefaultMaxLanes)
        {
            int cap = Mathf.Max(1, maxLanesPerArena);

            _laneLeft  = new float[cap];
            _laneRight = new float[cap];

            _occupiedLeft  = new float[cap];
            _occupiedRight = new float[cap];

            // The complement of N non-overlapping intervals in a span has at most
            // N + 1 gaps (one leading, one between each pair, one trailing).
            _fillLeft  = new float[cap + 1];
            _fillRight = new float[cap + 1];
        }

        // -------------------------------------------------------------------
        // Main computation
        // -------------------------------------------------------------------

        /// <summary>
        /// Computes lane intervals, occupied union, and fill intervals for
        /// <paramref name="arena"/> by scanning all lanes in
        /// <paramref name="evaluator"/>.
        ///
        /// <para>Returns <c>true</c> on success.  Returns <c>false</c> (and leaves
        /// all counts at zero) if the arena is disabled, has an empty or null
        /// <c>ArenaId</c>, or has a degenerate (near-zero) arc sweep.</para>
        ///
        /// <para>Results from a prior call are overwritten — read them before
        /// calling <see cref="Compute"/> again on any arena.</para>
        /// </summary>
        /// <param name="arena">The evaluated arena to compute occupancy for.</param>
        /// <param name="evaluator">
        /// The evaluator whose current lane state is scanned.
        /// Must have had <c>Evaluate(timeMs)</c> called this frame already.
        /// </param>
        public bool Compute(in EvaluatedArena arena, ChartRuntimeEvaluator evaluator)
        {
            // Reset all output counts — results are meaningless if we return false.
            _laneCount     = 0;
            _occupiedCount = 0;
            _fillCount     = 0;

            // Validate arena before doing any work.
            if (!arena.EnabledBool || string.IsNullOrEmpty(arena.ArenaId)) { return false; }

            float arcSweep = Mathf.Clamp(arena.ArcSweepDeg, 0f, 360f);
            if (arcSweep < 0.1f) { return false; } // Degenerate sweep — nothing to draw.

            _arenaStartDeg = arena.ArcStartDeg;
            _arenaEndDeg   = arena.ArcStartDeg + arcSweep;

            // ── Step 1: Collect enabled lane intervals for this arena ─────────────
            //
            // For each enabled lane that belongs to this arena, compute its angular
            // interval [leftDeg, rightDeg] and clamp it to the arena's span.
            // Intervals that collapse to zero width after clamping are discarded.
            //
            // Deferred: lanes that straddle the 0°/360° seam of a full-circle arena
            // are not split — see file header for details.
            for (int l = 0; l < evaluator.LaneCount; l++)
            {
                EvaluatedLane lane = evaluator.GetLane(l);

                // Only collect lanes that belong to this arena and are currently enabled.
                if (!lane.EnabledBool || lane.ArenaId != arena.ArenaId) { continue; }

                // Silently ignore lanes beyond the pre-allocated capacity.
                if (_laneCount >= _laneLeft.Length) { break; }

                float laneLeft  = lane.CenterDeg - lane.WidthDeg * 0.5f;
                float laneRight = lane.CenterDeg + lane.WidthDeg * 0.5f;

                // Clamp to the arena's angular span so lane sectors never extend
                // outside the arena boundary.
                laneLeft  = Mathf.Max(laneLeft,  _arenaStartDeg);
                laneRight = Mathf.Min(laneRight, _arenaEndDeg);

                // Discard intervals that collapsed to zero or negative width after clamping
                // (happens when a lane is entirely outside the arena's angular span).
                if (laneRight <= laneLeft) { continue; }

                _laneLeft[_laneCount]  = laneLeft;
                _laneRight[_laneCount] = laneRight;
                _laneCount++;
            }

            // ── Step 2: Sort lane intervals by left edge ──────────────────────────
            //
            // MergeIntervals requires the input to be sorted by left edge.
            // Insertion sort: O(N²) worst case, O(N) on already-sorted input (the
            // common case when lanes are authored in angular order).  No allocation.
            SortByLeft(_laneLeft, _laneRight, _laneCount);

            // ── Step 3: Merge overlapping / adjacent intervals into occupied union ─
            //
            // Two intervals overlap or touch when left[i] <= right of last merged.
            // They are unified by extending the merged interval's right edge.
            // Result: non-overlapping intervals covering exactly the lane footprint.
            _occupiedCount = MergeIntervals(
                _laneLeft, _laneRight, _laneCount,
                _occupiedLeft, _occupiedRight);

            // ── Step 4: Compute fill intervals (complement within arena span) ──────
            //
            // Walk the arena span left to right.  Any portion not covered by the
            // occupied union is a fill interval.  If no lanes exist (occupiedCount = 0),
            // the entire arena span is a single fill interval.
            _fillCount = ComputeComplement(
                _occupiedLeft, _occupiedRight, _occupiedCount,
                _arenaStartDeg, _arenaEndDeg,
                _fillLeft, _fillRight);

            return true;
        }

        // -------------------------------------------------------------------
        // Result accessors
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns the i-th raw lane interval (0 ≤ i &lt; <see cref="LaneIntervalCount"/>).
        /// The interval is clamped to the arena's angular span and is sorted by
        /// <see cref="AngularInterval.StartDeg"/>, but intervals may still overlap
        /// (use <see cref="GetOccupiedInterval"/> for the merged set).
        /// </summary>
        public AngularInterval GetLaneInterval(int i)
            => new AngularInterval(_laneLeft[i], _laneRight[i]);

        /// <summary>
        /// Returns the i-th merged occupied interval (0 ≤ i &lt; <see cref="OccupiedIntervalCount"/>).
        /// Overlapping and adjacent lanes have been unified — no two occupied intervals overlap.
        /// These represent the angular extents currently covered by at least one enabled lane.
        /// </summary>
        public AngularInterval GetOccupiedInterval(int i)
            => new AngularInterval(_occupiedLeft[i], _occupiedRight[i]);

        /// <summary>
        /// Returns the i-th fill interval (0 ≤ i &lt; <see cref="FillIntervalCount"/>).
        /// Fill intervals are the complement of the occupied union within the arena span:
        /// angular regions not currently covered by any enabled lane body.
        /// <see cref="ArenaSurfaceRenderer"/> draws one filled mesh sector per fill interval.
        /// </summary>
        public AngularInterval GetFillInterval(int i)
            => new AngularInterval(_fillLeft[i], _fillRight[i]);

        // -------------------------------------------------------------------
        // Core static math helpers (private)
        // -------------------------------------------------------------------

        // Sorts two parallel float arrays (leftArr, rightArr, length = count) in-place
        // by ascending leftArr value, using insertion sort.
        //
        // Chosen because:
        //   • In-place, zero allocation.
        //   • O(N²) worst case; O(N) on already-sorted input (the typical case when
        //     lanes are authored in angular order around the arena).
        //   • Stable — equal left edges preserve relative order.
        //   • N is tiny in practice (1–8 lanes per arena).
        private static void SortByLeft(float[] leftArr, float[] rightArr, int count)
        {
            for (int i = 1; i < count; i++)
            {
                float keyLeft  = leftArr[i];
                float keyRight = rightArr[i];
                int   j        = i - 1;

                while (j >= 0 && leftArr[j] > keyLeft)
                {
                    leftArr[j + 1]  = leftArr[j];
                    rightArr[j + 1] = rightArr[j];
                    j--;
                }

                leftArr[j + 1]  = keyLeft;
                rightArr[j + 1] = keyRight;
            }
        }

        // Merges overlapping or adjacent intervals from (srcLeft, srcRight) of length
        // srcCount into (dstLeft, dstRight), returning the count of merged intervals.
        //
        // Precondition: srcLeft must be sorted ascending (call SortByLeft first).
        //
        // Merge rule: if the next source interval starts at or before the right edge of
        // the last merged interval (srcLeft[i] <= dstRight[last]), they overlap or touch
        // and are merged by extending the last merged right edge to max(dstRight[last],
        // srcRight[i]).  Otherwise a new merged interval is started.
        //
        // Example:
        //   Input:  [10,40], [20,50], [80,90], [90,100]
        //   Output: [10,50], [80,100]        mergedCount = 2
        private static int MergeIntervals(
            float[] srcLeft, float[] srcRight, int srcCount,
            float[] dstLeft, float[] dstRight)
        {
            if (srcCount == 0) { return 0; }

            dstLeft[0]  = srcLeft[0];
            dstRight[0] = srcRight[0];
            int dstCount = 1;

            for (int i = 1; i < srcCount; i++)
            {
                if (srcLeft[i] <= dstRight[dstCount - 1])
                {
                    // Overlap or adjacency — extend the current merged interval's right edge.
                    dstRight[dstCount - 1] = Mathf.Max(dstRight[dstCount - 1], srcRight[i]);
                }
                else
                {
                    // No overlap — start a new merged interval.
                    dstLeft[dstCount]  = srcLeft[i];
                    dstRight[dstCount] = srcRight[i];
                    dstCount++;
                }
            }

            return dstCount;
        }

        // Computes the complement of the occupied intervals within [arenaStart, arenaEnd].
        // Writes gap intervals (regions not covered by any occupied interval) into
        // (fillLeft, fillRight) and returns the count of fill intervals written.
        //
        // Precondition: occupiedLeft/Right are sorted and non-overlapping (output of
        // MergeIntervals).
        //
        // A cursor walks the arena span left to right:
        //   – If the cursor is before the next occupied interval, a fill gap is recorded
        //     covering [cursor, occupiedLeft[m]].
        //   – The cursor advances to occupiedRight[m] (never moves backward).
        // After all occupied intervals are consumed, any remaining span [cursor, arenaEnd]
        // is a trailing fill gap.
        //
        // If occupiedCount is zero (no lanes in the arena), the entire arena span is
        // returned as one fill interval — preserving the full-arena draw behaviour when
        // a chart has no lanes yet.
        private static int ComputeComplement(
            float[] occupiedLeft, float[] occupiedRight, int occupiedCount,
            float arenaStart, float arenaEnd,
            float[] fillLeft, float[] fillRight)
        {
            int   fillCount = 0;
            float cursor    = arenaStart;

            for (int m = 0; m < occupiedCount; m++)
            {
                // Record gap before this occupied interval (if the cursor hasn't reached it yet).
                if (cursor < occupiedLeft[m])
                {
                    fillLeft[fillCount]  = cursor;
                    fillRight[fillCount] = occupiedLeft[m];
                    fillCount++;
                }

                // Advance cursor past this occupied interval (never move backward).
                cursor = Mathf.Max(cursor, occupiedRight[m]);
            }

            // Record the trailing gap after the last occupied interval (if any span remains).
            if (cursor < arenaEnd)
            {
                fillLeft[fillCount]  = cursor;
                fillRight[fillCount] = arenaEnd;
                fillCount++;
            }

            return fillCount;
        }
    }
}
