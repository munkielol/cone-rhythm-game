// ArenaOccupancyEvaluator.cs
// Shared runtime helper that computes the current lane occupancy and fill intervals
// for one arena from evaluated lane state.  Zero per-call GC allocation.
//
// ── What this computes ────────────────────────────────────────────────────────
//
//   Given a current EvaluatedArena and the full ChartRuntimeEvaluator, Compute()
//   derives three sets of angular intervals from current evaluated lane state:
//
//     Lane segments        Up to two segments per currently enabled lane in the
//                          arena, each clipped (and split at the seam if needed)
//                          to the arena's current angular span.  These are the raw
//                          (possibly overlapping) occupied extents before merging.
//                          Most lanes produce one segment; a lane that crosses the
//                          current arena seam produces two.
//
//     Occupied union       The sorted, merged union of all lane segments.
//                          Overlapping or adjacent segments are unified so the occupied
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
// ── Arena-local angular domain ────────────────────────────────────────────────
//
//   All occupancy computation is performed in arena-local angular space:
//
//     local 0°    = current arena ArcStartDeg (the seam, in global degrees)
//     local sweep = ArcSweepDeg (the arena's total angular span)
//
//   Why this is required:
//
//   (1) Rotating arenas: the arena's ArcStartDeg animates each frame, so the
//       global position of the seam changes.  A lane that is "near the seam" in
//       local terms stays near the seam regardless of where the arena is pointing.
//       Naive global-degree clamping to [ArcStartDeg, ArcStartDeg + ArcSweep] fails
//       because it treats e.g. lane CenterDeg=180 as "far from" arena-start=350
//       even when the arena sweep covers that global angle via wrap.
//
//   (2) Full-circle arenas: when ArcSweepDeg = 360, the arena is a full ring.
//       A lane at CenterDeg=5 when arenaStart=350 has localCenter=15 — clearly
//       inside the ring — but globalInterval=[−9, 19] does not overlap with the
//       global range [350, 710] without modular arithmetic.  Arena-local conversion
//       normalises the center first (15°), so the clip/split is applied in
//       [0, 360] local space where the arithmetic is straightforward.
//
//   Conversion: localCenter = Mathf.Repeat(laneCenterDeg − arenaStartDeg, 360)
//
//   For partial arenas (ArcSweep < 360), the local domain is [0, arcSweep] with
//   hard boundaries — lanes that extend beyond are simply clipped; no seam wrap.
//
//   For full-circle arenas (ArcSweep ≥ 360), local [0, 360] is a cyclic ring.
//   A lane whose local interval [localCenter − halfWidth, localCenter + halfWidth]
//   crosses local 0°/360° is split into two segments so both visible portions are
//   represented and rendered individually.
//
//   All returned intervals (lane segments, occupied, fill) are in global-extended
//   degrees: [ArcStartDeg + localStart, ArcStartDeg + localEnd].  Values above 360
//   are valid (e.g. 380°) — Unity's Mathf.Cos/Sin accept them unchanged, and the
//   renderers lerp across start→end which produces correct geometry.
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

        // Raw lane segments collected for the current arena.
        // Each enabled lane contributes 0, 1, or 2 segments (2 when it crosses the seam
        // of a full-circle arena).  Capacity = 2 × maxLanesPerArena.
        // Sorted by left edge (in global-extended degrees) before merging.
        //
        // _laneEvalIndex[i] stores the ChartRuntimeEvaluator lane array index that
        // produced segment i.  It is sorted in parallel with _laneLeft/_laneRight so
        // that callers can perform identity-stable lookups via GetLaneVisibleSegments().
        //
        // Why track this separately from the sort key?
        //   Sorting is required for the merge step (MergeIntervals needs sorted input).
        //   But sorted spatial order is NOT a stable identity: when two lanes cross,
        //   their left-edge order swaps, and any code that indexed by sort position
        //   would silently reassign which authored-lane body each slot represents.
        //   One evalIndex can appear up to twice (seam-split lane).  Storing the index
        //   in parallel lets GetLaneVisibleSegments() collect all segments for a lane.
        private readonly float[] _laneLeft;
        private readonly float[] _laneRight;
        private readonly int[]   _laneEvalIndex;  // evaluator lane index per sorted segment
        private          int     _laneCount;       // total raw segments (not lanes)

        // Merged occupied intervals — sorted, non-overlapping union of all lane segments.
        // Capacity = _laneLeft.Length (upper bound when no merging occurs).
        private readonly float[] _occupiedLeft;
        private readonly float[] _occupiedRight;
        private          int     _occupiedCount;

        // Fill intervals — complement of occupied union within the arena span.
        // Capacity = _laneLeft.Length + 1:
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
            int cap    = Mathf.Max(1, maxLanesPerArena);
            // Each lane can produce up to 2 segments (seam-split full-circle case),
            // so the raw lane arrays need 2× the authored lane capacity.
            int rawCap = cap * 2;

            _laneLeft      = new float[rawCap];
            _laneRight     = new float[rawCap];
            _laneEvalIndex = new int[rawCap];

            // Occupied and fill arrays are bounded by the raw segment count.
            _occupiedLeft  = new float[rawCap];
            _occupiedRight = new float[rawCap];

            // The complement of N non-overlapping intervals in a span has at most
            // N + 1 gaps (one leading, one between each pair, one trailing).
            _fillLeft  = new float[rawCap + 1];
            _fillRight = new float[rawCap + 1];
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

            // ── Step 1: Collect enabled lane segments in arena-local space ──────────
            //
            // All lane coverage is computed in arena-local angular space:
            //
            //   localCenter = Mathf.Repeat(laneCenterDeg − arenaStartDeg, 360)
            //
            // This converts any global lane center to its position relative to the
            // arena seam (local 0°), correctly handling both wrapped arenas and
            // rotating arenas whose seam moves frame-to-frame.
            //
            // Partial arenas (arcSweep < 360): clip local interval to [0, arcSweep].
            // Full-circle arenas (arcSweep ≥ 360): the local domain is [0, 360].
            //   A lane whose local interval straddles local 0°/360° is split into
            //   two segments so both visible halves are represented and rendered.
            //
            // All stored intervals are returned in global-extended degrees:
            //   globalStart = arenaStartDeg + localStart
            //   globalEnd   = arenaStartDeg + localEnd
            // Values > 360° are valid — Mathf.Cos/Sin accept them, renderers lerp.
            bool isFull = (arcSweep >= 360f);

            for (int l = 0; l < evaluator.LaneCount; l++)
            {
                EvaluatedLane lane = evaluator.GetLane(l);

                // Only collect lanes that belong to this arena and are currently enabled.
                if (!lane.EnabledBool || lane.ArenaId != arena.ArenaId) { continue; }

                // Stop if raw segment capacity is exhausted (2× maxLanes — never hit
                // in practice, but guards against degenerate charts).
                if (_laneCount >= _laneLeft.Length) { break; }

                float halfWidth   = lane.WidthDeg * 0.5f;
                // Convert global lane center to arena-local: always in [0, 360).
                float localCenter = Mathf.Repeat(lane.CenterDeg - _arenaStartDeg, 360f);
                float localLeft   = localCenter - halfWidth;
                float localRight  = localCenter + halfWidth;

                if (isFull)
                {
                    // ── Full-circle arena: seam-aware segment collection ─────────────
                    //
                    // The local domain is [0, 360] (cyclic).  A lane that crosses
                    // local 0° or local 360° (the same seam) is split into two segments:
                    //   Lower-seam crossing (localLeft < 0):
                    //     Seg A: local [0, localRight]          → "right" side of seam
                    //     Seg B: local [360+localLeft, 360]     → "left"  side of seam
                    //   Upper-seam crossing (localRight > 360):
                    //     Seg A: local [localLeft, 360]         → "left"  side of seam
                    //     Seg B: local [0, localRight−360]      → "right" side of seam
                    //   No crossing:
                    //     Seg A: local [localLeft, localRight]  → one segment
                    //
                    // Each seg stored in global-extended = arenaStartDeg + local.
                    if (localLeft < 0f)
                    {
                        // Seg A — right side (from local 0 to localRight)
                        if (localRight > 0f && _laneCount < _laneLeft.Length)
                        {
                            _laneLeft[_laneCount]      = _arenaStartDeg;
                            _laneRight[_laneCount]     = _arenaStartDeg + Mathf.Min(localRight, 360f);
                            _laneEvalIndex[_laneCount] = l;
                            _laneCount++;
                        }
                        // Seg B — left side (from 360+localLeft to 360)
                        if (_laneCount < _laneLeft.Length)
                        {
                            _laneLeft[_laneCount]      = _arenaStartDeg + 360f + localLeft;
                            _laneRight[_laneCount]     = _arenaStartDeg + 360f;
                            _laneEvalIndex[_laneCount] = l;
                            _laneCount++;
                        }
                    }
                    else if (localRight > 360f)
                    {
                        // Seg A — left side (from localLeft to 360)
                        if (_laneCount < _laneLeft.Length)
                        {
                            _laneLeft[_laneCount]      = _arenaStartDeg + localLeft;
                            _laneRight[_laneCount]     = _arenaStartDeg + 360f;
                            _laneEvalIndex[_laneCount] = l;
                            _laneCount++;
                        }
                        // Seg B — right side (overflow mapped back to beginning)
                        float overflow = localRight - 360f;
                        if (overflow > 0f && _laneCount < _laneLeft.Length)
                        {
                            _laneLeft[_laneCount]      = _arenaStartDeg;
                            _laneRight[_laneCount]     = _arenaStartDeg + overflow;
                            _laneEvalIndex[_laneCount] = l;
                            _laneCount++;
                        }
                    }
                    else
                    {
                        // No seam crossing — single segment.
                        _laneLeft[_laneCount]      = _arenaStartDeg + localLeft;
                        _laneRight[_laneCount]     = _arenaStartDeg + localRight;
                        _laneEvalIndex[_laneCount] = l;
                        _laneCount++;
                    }
                }
                else
                {
                    // ── Partial arena: clip local interval to [0, arcSweep] ─────────
                    //
                    // No seam wrapping: the local domain has hard boundaries at 0 and
                    // arcSweep.  A lane extending past either boundary is simply clipped.
                    float clampedLeft  = Mathf.Max(localLeft,  0f);
                    float clampedRight = Mathf.Min(localRight, arcSweep);

                    // Discard lanes entirely outside the arena's angular span.
                    if (clampedRight <= clampedLeft) { continue; }

                    _laneLeft[_laneCount]      = _arenaStartDeg + clampedLeft;
                    _laneRight[_laneCount]     = _arenaStartDeg + clampedRight;
                    _laneEvalIndex[_laneCount] = l;
                    _laneCount++;
                }
            }

            // ── Step 2: Sort lane intervals by left edge ──────────────────────────
            //
            // MergeIntervals requires the input to be sorted by left edge.
            // Insertion sort: O(N²) worst case, O(N) on already-sorted input (the
            // common case when lanes are authored in angular order).  No allocation.
            SortByLeft(_laneLeft, _laneRight, _laneEvalIndex, _laneCount);

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
        ///
        /// <para>Note: do not assume that spatial index i corresponds to authored lane i.
        /// When two lanes cross, their sorted positions swap without changing which lane
        /// body each authored identity represents.  Use <see cref="TryGetLaneInterval"/>
        /// for identity-stable access instead.</para>
        /// </summary>
        public AngularInterval GetLaneInterval(int i)
            => new AngularInterval(_laneLeft[i], _laneRight[i]);

        /// <summary>
        /// Returns the <see cref="ChartRuntimeEvaluator"/> lane array index that produced
        /// the i-th sorted lane interval (0 ≤ i &lt; <see cref="LaneIntervalCount"/>).
        ///
        /// <para>Sorted spatial order is valid for fill-interval math (merge requires
        /// sorted input) but is not a stable identity.  This accessor gives callers the
        /// original evaluator index so they can round-trip to authored-lane properties
        /// (opacity, LaneId, etc.) via <see cref="ChartRuntimeEvaluator.GetLane"/>.</para>
        /// </summary>
        public int GetLaneEvalIndex(int i) => _laneEvalIndex[i];

        /// <summary>
        /// Returns the number of visible segments (0, 1, or 2) for the lane at evaluator
        /// array index <paramref name="evalIndex"/> from the last <see cref="Compute"/> call,
        /// and fills <paramref name="seg0"/> / <paramref name="seg1"/> with the segment data.
        ///
        /// <para><strong>0 segments</strong> — the lane is disabled, not in this arena, or
        /// its entire angular extent is outside the arena span.</para>
        ///
        /// <para><strong>1 segment</strong> — the normal case.  The lane is fully inside the
        /// arena or clipped to one contiguous piece at a partial-arena boundary.</para>
        ///
        /// <para><strong>2 segments</strong> — the lane crosses the current arena seam in a
        /// full-circle (ArcSweep ≥ 360) arena.  Both halves must be drawn independently
        /// to avoid a visual gap at the seam.</para>
        ///
        /// <para>All interval angles are in global-extended degrees
        /// [<c>ArenaStartDeg</c>, <c>ArenaEndDeg</c>]; values above 360° are valid.</para>
        ///
        /// <para><strong>Why not index by spatial sort position?</strong>  Sorted spatial
        /// order is required for the merge/complement math, but is not a stable per-lane
        /// identity.  When lanes cross, the sort index assignment changes.  This method
        /// bypasses spatial ordering and provides an identity-keyed lookup.</para>
        /// </summary>
        /// <param name="evalIndex">
        /// The lane array index from <see cref="ChartRuntimeEvaluator"/> — i.e., the value
        /// of <c>i</c> passed to <c>evaluator.GetLane(i)</c>.
        /// </param>
        /// <param name="seg0">First segment on success; <c>default</c> otherwise.</param>
        /// <param name="seg1">Second segment if count == 2; <c>default</c> otherwise.</param>
        /// <returns>0, 1, or 2.</returns>
        public int GetLaneVisibleSegments(int evalIndex,
                                          out AngularInterval seg0,
                                          out AngularInterval seg1)
        {
            // Linear scan over _laneCount (up to 2× authored lane count, still tiny).
            // O(N), zero allocation.
            int count = 0;
            seg0 = default;
            seg1 = default;
            for (int i = 0; i < _laneCount; i++)
            {
                if (_laneEvalIndex[i] == evalIndex)
                {
                    if (count == 0) { seg0 = new AngularInterval(_laneLeft[i], _laneRight[i]); }
                    else            { seg1 = new AngularInterval(_laneLeft[i], _laneRight[i]); }
                    count++;
                    if (count == 2) { break; } // at most 2 segments per lane
                }
            }
            return count;
        }

        /// <summary>
        /// Looks up the first visible segment for the lane at evaluator array index
        /// <paramref name="evalIndex"/> from the last <see cref="Compute"/> call.
        ///
        /// <para>Returns <c>true</c> when at least one segment exists.  For seam-split
        /// lanes (full-circle arenas) the second segment is silently ignored — use
        /// <see cref="GetLaneVisibleSegments"/> to retrieve both.</para>
        /// </summary>
        public bool TryGetLaneInterval(int evalIndex, out AngularInterval interval)
        {
            AngularInterval dummy;
            return GetLaneVisibleSegments(evalIndex, out interval, out dummy) > 0;
        }

        /// <summary>
        /// Returns the two logical guide boundary angles (left and right) for the lane at
        /// evaluator array index <paramref name="evalIndex"/> from the last
        /// <see cref="Compute"/> call.
        ///
        /// <para>
        /// <strong>Why this is different from <see cref="GetLaneVisibleSegments"/>:</strong>
        /// Body segments split at the arena seam so both halves can be rendered as
        /// separate meshes.  Guide boundaries do not split — they represent the lane's
        /// actual angular extent and must never appear at the seam itself.
        /// </para>
        ///
        /// <para>
        /// A seam-split lane (full-circle arena, lane crossing the seam) produces two body
        /// segments from <see cref="GetLaneVisibleSegments"/>, but still has only two
        /// logical boundaries (left edge and right edge).  This method always returns those
        /// two logical angles regardless of how many body segments exist.
        /// </para>
        ///
        /// <para>
        /// After <see cref="Compute"/> sorts segments by <c>StartDeg</c>, the two segments
        /// of a seam-split lane are always ordered so that:
        /// <list type="bullet">
        ///   <item><c>seg0.StartDeg</c> == arena seam lower end — artificial, NOT a boundary</item>
        ///   <item><c>seg1.EndDeg</c>   == arena seam upper end — artificial, NOT a boundary</item>
        ///   <item><c>seg1.StartDeg</c> == true left  boundary (returned as <paramref name="leftBoundaryDeg"/>)</item>
        ///   <item><c>seg0.EndDeg</c>   == true right boundary (returned as <paramref name="rightBoundaryDeg"/>)</item>
        /// </list>
        /// For a non-seam-split lane (1 segment) the boundaries are trivially
        /// <c>seg0.StartDeg</c> and <c>seg0.EndDeg</c>.
        /// </para>
        ///
        /// <para>Returns <c>false</c> when the lane has no visible segments (disabled,
        /// or entirely outside the arena span after clamping).</para>
        /// </summary>
        /// <param name="evalIndex">
        /// The lane array index from <see cref="ChartRuntimeEvaluator"/> — the value of
        /// <c>i</c> passed to <c>evaluator.GetLane(i)</c>.
        /// </param>
        /// <param name="leftBoundaryDeg">
        /// Left guide angle in global-extended degrees on success; 0 otherwise.
        /// </param>
        /// <param name="rightBoundaryDeg">
        /// Right guide angle in global-extended degrees on success; 0 otherwise.
        /// </param>
        /// <returns><c>true</c> when at least one segment exists; <c>false</c> otherwise.</returns>
        public bool TryGetLaneGuideBoundaries(
            int evalIndex, out float leftBoundaryDeg, out float rightBoundaryDeg)
        {
            int segCount = GetLaneVisibleSegments(
                evalIndex, out AngularInterval seg0, out AngularInterval seg1);

            if (segCount == 0)
            {
                leftBoundaryDeg  = 0f;
                rightBoundaryDeg = 0f;
                return false;
            }

            if (segCount == 1)
            {
                // Normal case: one contiguous visible body.
                // Left boundary = segment start, right boundary = segment end.
                leftBoundaryDeg  = seg0.StartDeg;
                rightBoundaryDeg = seg0.EndDeg;
                return true;
            }

            // segCount == 2: seam-split lane in a full-circle arena.
            //
            // After sorting by StartDeg, the two segments are ordered so that:
            //   seg0.StartDeg == arena seam lower end (_arenaStartDeg)  — NOT a guide edge
            //   seg1.EndDeg   == arena seam upper end (_arenaEndDeg)    — NOT a guide edge
            //   seg1.StartDeg == true left  boundary of the lane
            //   seg0.EndDeg   == true right boundary of the lane
            //
            // The body renderer draws both halves separately so the lane body spans the
            // seam without a gap.  Guides represent the logical lane — two lines total,
            // neither of which appears at the seam.
            leftBoundaryDeg  = seg1.StartDeg;
            rightBoundaryDeg = seg0.EndDeg;
            return true;
        }

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

        // Sorts three parallel arrays (leftArr, rightArr, evalIndexArr, length = count)
        // in-place by ascending leftArr value, using insertion sort.
        //
        // evalIndexArr is sorted in tandem so that after sorting, evalIndexArr[i] still
        // identifies which ChartRuntimeEvaluator lane index produced interval i.
        // This allows identity-stable lookup via TryGetLaneInterval() even after reordering.
        //
        // Chosen because:
        //   • In-place, zero allocation.
        //   • O(N²) worst case; O(N) on already-sorted input (the typical case when
        //     lanes are authored in angular order around the arena).
        //   • Stable — equal left edges preserve relative order.
        //   • N is tiny in practice (1–8 lanes per arena).
        private static void SortByLeft(float[] leftArr, float[] rightArr, int[] evalIndexArr, int count)
        {
            for (int i = 1; i < count; i++)
            {
                float keyLeft      = leftArr[i];
                float keyRight     = rightArr[i];
                int   keyEvalIndex = evalIndexArr[i];
                int   j            = i - 1;

                while (j >= 0 && leftArr[j] > keyLeft)
                {
                    leftArr[j + 1]      = leftArr[j];
                    rightArr[j + 1]     = rightArr[j];
                    evalIndexArr[j + 1] = evalIndexArr[j];
                    j--;
                }

                leftArr[j + 1]      = keyLeft;
                rightArr[j + 1]     = keyRight;
                evalIndexArr[j + 1] = keyEvalIndex;
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
