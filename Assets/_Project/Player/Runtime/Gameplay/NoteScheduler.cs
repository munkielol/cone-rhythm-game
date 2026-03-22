// NoteScheduler.cs
// Builds and queries the runtime list of notes from a loaded chart.
//
// Responsibilities:
//   1. Convert chart.notes[] → List<RuntimeNote>, preserving file order (noteIndex).
//   2. Provide efficient active-note queries by effective chart time window.
//   3. Provide hold-tick evaluation that is safe across variable frame gaps.
//
// Performance note (spec §9):
//   "Efficient active-window evaluation (don't iterate all notes each frame)."
//   The scheduler maintains a _pendingStartIndex cursor so each Update() only
//   scans notes that haven't yet entered their active window.
//
// Hold-tick rule (spec §7.5.1):
//   "Maintain prevSongTimeMs. Each update, process all ticks where
//    prevSongTimeMs < tickTimeMs <= currentSongTimeMs."

using System;
using System.Collections.Generic;
using RhythmicFlow.Shared;
using UnityEngine;

// Scoring note (spec §4.5):
//   SweepMissed now accepts an optional onMissed callback so callers (e.g.
//   PlayerAppController) can react to each newly-swept note for scoring without
//   any additional list allocation. The callback receives the RuntimeNote whose
//   State was just set to Missed. Called after State is set.

namespace RhythmicFlow.Player
{
    public class NoteScheduler
    {
        // -------------------------------------------------------------------
        // State
        // -------------------------------------------------------------------

        // All notes sorted by PrimaryTimeMs (stable: preserves noteIndex for equal times).
        private readonly List<RuntimeNote> _allNotes;

        // Cursor: the first note index in _allNotes that has not yet become Active.
        // Notes before this index are either Active, Hit, or Missed.
        private int _pendingStartIndex;

        // -------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------

        /// <summary>
        /// Builds the runtime note list from a validated chart.
        /// Call this once after loading a chart, before any Update calls.
        /// </summary>
        public NoteScheduler(ChartJsonV1 chart)
        {
            if (chart == null) { throw new ArgumentNullException(nameof(chart)); }

            _allNotes = new List<RuntimeNote>(chart.notes?.Count ?? 0);

            if (chart.notes != null)
            {
                for (int i = 0; i < chart.notes.Count; i++)
                {
                    ChartNote source = chart.notes[i];
                    if (source == null) { continue; }

                    _allNotes.Add(new RuntimeNote(source, noteIndex: i));
                }
            }

            // Sort by PrimaryTimeMs (stable sort: equal times keep original file order).
            // List.Sort is not guaranteed stable in all Unity versions; use index as tiebreak.
            _allNotes.Sort((a, b) =>
            {
                int timeDiff = a.PrimaryTimeMs.CompareTo(b.PrimaryTimeMs);
                return timeDiff != 0 ? timeDiff : a.NoteIndex.CompareTo(b.NoteIndex);
            });

            _pendingStartIndex = 0;
        }

        // -------------------------------------------------------------------
        // Read-only access
        // -------------------------------------------------------------------

        /// <summary>Total number of runtime notes.</summary>
        public int Count => _allNotes.Count;

        /// <summary>Read-only view of all runtime notes (sorted by PrimaryTimeMs).</summary>
        public IReadOnlyList<RuntimeNote> AllNotes => _allNotes;

        // -------------------------------------------------------------------
        // Update: activate notes entering the window
        // -------------------------------------------------------------------

        /// <summary>
        /// Advances notes from Pending to Active as the effective chart time approaches.
        /// Call once per frame before querying active notes.
        ///
        /// <paramref name="effectiveChartTimeMs"/> — current value from Conductor.
        /// <paramref name="activationLeadMs"/> — how many ms before a note's PrimaryTimeMs
        ///   it should become Active (should be >= the largest judgement window).
        /// </summary>
        public void AdvanceActive(double effectiveChartTimeMs, double activationLeadMs)
        {
            // Walk from _pendingStartIndex forward, activating notes whose window has started.
            while (_pendingStartIndex < _allNotes.Count)
            {
                RuntimeNote note = _allNotes[_pendingStartIndex];

                if (note.State != NoteState.Pending)
                {
                    // Already advanced by a previous call; skip.
                    _pendingStartIndex++;
                    continue;
                }

                double noteActivationTime = note.PrimaryTimeMs - activationLeadMs;

                if (effectiveChartTimeMs >= noteActivationTime)
                {
                    note.State = NoteState.Active;
                    _pendingStartIndex++;
                }
                else
                {
                    // Notes are sorted by PrimaryTimeMs; no later note can be activated yet.
                    break;
                }
            }
        }

        // -------------------------------------------------------------------
        // Active note query
        // -------------------------------------------------------------------

        /// <summary>
        /// Fills <paramref name="outNotes"/> with all currently Active notes whose
        /// PrimaryTimeMs falls within [effectiveChartTimeMs - windowMs, effectiveChartTimeMs + windowMs].
        /// Clears outNotes before filling.
        ///
        /// For hold notes, the window is relative to StartTimeMs (the binding start).
        /// </summary>
        public void GetActiveInWindow(
            double            effectiveChartTimeMs,
            double            windowMs,
            List<RuntimeNote> outNotes)
        {
            outNotes.Clear();

            double lo = effectiveChartTimeMs - windowMs;
            double hi = effectiveChartTimeMs + windowMs;

            foreach (RuntimeNote note in _allNotes)
            {
                if (note.State != NoteState.Active) { continue; }
                if (note.PrimaryTimeMs < lo)         { continue; }
                if (note.PrimaryTimeMs > hi)         { continue; }

                outNotes.Add(note);
            }
        }

        // -------------------------------------------------------------------
        // Miss sweep: mark notes that passed their window as Missed
        // -------------------------------------------------------------------

        /// <summary>
        /// Marks all Active notes whose PrimaryTimeMs + missWindowMs &lt; effectiveChartTimeMs
        /// as Missed. Call this each frame after AdvanceActive.
        ///
        /// <para><paramref name="onMissed"/> is optional. When provided, it is invoked
        /// for each note immediately after its State is set to Missed — useful for
        /// scoring/event systems that need to react without an extra list allocation.
        /// The callback receives the RuntimeNote with State already == Missed.</para>
        /// </summary>
        public void SweepMissed(double effectiveChartTimeMs, double missWindowMs,
                                Action<RuntimeNote> onMissed = null)
        {
            foreach (RuntimeNote note in _allNotes)
            {
                if (note.State != NoteState.Active) { continue; }

                double expiry = note.PrimaryTimeMs + missWindowMs;

                if (effectiveChartTimeMs > expiry)
                {
                    note.State = NoteState.Missed;
                    Debug.Log($"[NoteScheduler] MISS note {note.NoteId} (type={note.Type})");

                    // Notify scoring/event systems that this note was swept (spec §4.5).
                    onMissed?.Invoke(note);
                }
            }
        }

        // -------------------------------------------------------------------
        // Hold tick evaluation (spec §7.5.1)
        // -------------------------------------------------------------------

        /// <summary>
        /// Processes hold ticks that fall between prevTimeMs and currentTimeMs (exclusive/inclusive).
        /// Must be called every frame for all Bound holds.
        ///
        /// For each tick where prevTimeMs &lt; tickTimeMs &lt;= currentTimeMs:
        ///   - If the bound touch is inside the lane → Perfect tick (reported via callback).
        ///   - Else → Miss tick.
        ///
        /// Spec §7.5.1: "process all hold ticks where prevSongTimeMs &lt; tickTimeMs &lt;= currentSongTimeMs"
        /// </summary>
        /// <param name="hold">The hold note to evaluate.</param>
        /// <param name="prevTimeMs">Previous frame's effectiveChartTimeMs.</param>
        /// <param name="currentTimeMs">This frame's effectiveChartTimeMs.</param>
        /// <param name="isTouchInsideLane">
        /// True if the bound touch is currently inside the lane at this frame.
        /// </param>
        /// <param name="onTickResult">
        /// Called for each processed tick: (tickTimeMs, isPerfect).
        /// </param>
        public static void EvaluateHoldTicks(
            RuntimeNote     hold,
            double          prevTimeMs,
            double          currentTimeMs,
            bool            isTouchInsideLane,
            Action<int, bool> onTickResult)
        {
            if (hold.Type != NoteType.Hold) { return; }
            if (hold.HoldBind != HoldBindState.Bound) { return; }

            IReadOnlyList<int> ticks = hold.TickTimesMs;

            while (hold.NextTickIndex < ticks.Count)
            {
                int tickMs = ticks[hold.NextTickIndex];

                // Spec §7.5.1: prevTime < tickTime <= currentTime
                if (tickMs <= prevTimeMs) { hold.NextTickIndex++; continue; }
                if (tickMs > currentTimeMs) { break; }

                // This tick falls in the processing window.
                bool isPerfect = isTouchInsideLane;
                onTickResult?.Invoke(tickMs, isPerfect);
                hold.NextTickIndex++;

                // The callback may have set HoldBind = Finished (first missed tick fails
                // the hold — spec §7.5).  Stop processing further ticks immediately so we
                // do not emit multiple miss events for the same hold (spec §4.5 — "no spam").
                if (hold.HoldBind != HoldBindState.Bound) { break; }
            }

            // Check if hold has ended (current time passed endTimeMs).
            if (currentTimeMs >= hold.EndTimeMs && hold.HoldBind == HoldBindState.Bound)
            {
                hold.HoldBind = HoldBindState.Finished;
                hold.State    = NoteState.Hit;
            }
        }
    }
}
