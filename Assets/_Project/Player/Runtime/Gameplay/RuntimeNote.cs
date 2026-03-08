// RuntimeNote.cs
// Runtime note objects derived from ChartJsonV1 note data.
//
// All four note types use the common RuntimeNote class, with type-specific
// fields populated according to the "type" discriminator (matching ChartNote).
//
// File order (noteIndex) is preserved as the stable final tie-break in overlap
// arbitration (spec §7.6 criterion 4). noteIndex = position in chart.notes[].
//
// Spec references:
//   §9   — note types and fields
//   §7.6 — tie-break order (noteIndex = file order stable fallback)
//   §4.4 — hold tick scoring (Perfect-or-Miss only)
//   §4.2 — judgement tiers

using System.Collections.Generic;
using RhythmicFlow.Shared;

namespace RhythmicFlow.Player
{
    // -----------------------------------------------------------------------
    // Note judgement state (modified at runtime, not a spec field)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Lifecycle state of a runtime note object.
    /// </summary>
    public enum NoteState
    {
        /// <summary>Note is upcoming; not yet hittable.</summary>
        Pending,
        /// <summary>Note is within its active timing window and awaiting input.</summary>
        Active,
        /// <summary>Note has been successfully judged (Perfect/Great).</summary>
        Hit,
        /// <summary>Note passed its window without a hit (Miss).</summary>
        Missed,
    }

    // -----------------------------------------------------------------------
    // HoldBindState
    // -----------------------------------------------------------------------

    /// <summary>
    /// Binding state of a HoldNote. A hold's start must be hit before ticks are evaluated.
    /// Spec §7.5.
    /// </summary>
    public enum HoldBindState
    {
        /// <summary>Hold start not yet hit; ticks cannot score.</summary>
        Unbound,
        /// <summary>Hold start was hit; a touch is bound and ticks are being evaluated.</summary>
        Bound,
        /// <summary>Hold ended (all ticks processed or touch released early).</summary>
        Finished,
    }

    // -----------------------------------------------------------------------
    // RuntimeNote
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runtime representation of one note. Derived from ChartNote at load time.
    /// Holds all data needed for judgement, scheduling, and rendering.
    /// </summary>
    public class RuntimeNote
    {
        // -------------------------------------------------------------------
        // Identity (from authoring data)
        // -------------------------------------------------------------------

        /// <summary>
        /// Position of this note in the chart.notes[] list.
        /// Used as stable final tie-break in simultaneous-judgement arbitration (spec §7.6 §4).
        /// </summary>
        public int NoteIndex { get; }

        /// <summary>Stable GUID from the authoring tool (spec §9.1).</summary>
        public string NoteId { get; }

        /// <summary>The lane this note belongs to.</summary>
        public string LaneId { get; }

        /// <summary>Note type: tap | flick | catch | hold (spec §9.1).</summary>
        public string Type { get; }

        /// <summary>Whether this note requires player input (spec §9.1, default true).</summary>
        public bool Judging { get; }

        // -------------------------------------------------------------------
        // Tap / Flick / Catch timing
        // -------------------------------------------------------------------

        /// <summary>
        /// Hit time in milliseconds (Tap, Flick, Catch).
        /// For Hold, use StartTimeMs / EndTimeMs instead.
        /// </summary>
        public int TimeMs { get; }

        // -------------------------------------------------------------------
        // Flick
        // -------------------------------------------------------------------

        /// <summary>Lane-relative flick direction: L | R | U | D (spec §9.3).</summary>
        public string FlickDirection { get; }

        // -------------------------------------------------------------------
        // Hold
        // -------------------------------------------------------------------

        /// <summary>Hold start timestamp in ms (spec §9.5).</summary>
        public int StartTimeMs { get; }

        /// <summary>Hold end timestamp in ms (spec §9.5).</summary>
        public int EndTimeMs { get; }

        /// <summary>
        /// Baked tick times in ms, strictly increasing, all within [StartTimeMs, EndTimeMs].
        /// Authoritative — not re-derived at runtime (spec §3.2 / §9.5).
        /// </summary>
        public IReadOnlyList<int> TickTimesMs { get; }

        // -------------------------------------------------------------------
        // Runtime mutable state
        // -------------------------------------------------------------------

        /// <summary>Current lifecycle state of this note.</summary>
        public NoteState State { get; set; } = NoteState.Pending;

        /// <summary>
        /// Hold-specific binding state. Only meaningful when Type == "hold".
        /// </summary>
        public HoldBindState HoldBind { get; set; } = HoldBindState.Unbound;

        /// <summary>
        /// The touchId currently bound to this hold note (spec §7.5).
        /// -1 when unbound.
        /// </summary>
        public int BoundTouchId { get; set; } = -1;

        /// <summary>
        /// Index of the next tick to evaluate (index into TickTimesMs).
        /// Advances as ticks are processed during hold playback.
        /// </summary>
        public int NextTickIndex { get; set; } = 0;

        // -------------------------------------------------------------------
        // Construction
        // -------------------------------------------------------------------

        /// <summary>
        /// Creates a RuntimeNote from a ChartNote and its file-order index.
        /// </summary>
        public RuntimeNote(ChartNote source, int noteIndex)
        {
            NoteIndex     = noteIndex;
            NoteId        = source.noteId        ?? "";
            LaneId        = source.laneId        ?? "";
            Type          = source.type          ?? "";
            Judging       = source.judging;
            TimeMs        = source.timeMs;
            FlickDirection = source.direction    ?? "";
            StartTimeMs   = source.startTimeMs;
            EndTimeMs     = source.endTimeMs;

            // Defensive copy of tick times so runtime mutation cannot affect authoring data.
            TickTimesMs = source.tickTimesMs != null
                ? source.tickTimesMs.AsReadOnly()
                : System.Array.AsReadOnly(System.Array.Empty<int>());
        }

        // -------------------------------------------------------------------
        // Convenience properties
        // -------------------------------------------------------------------

        /// <summary>
        /// The "primary" time used for scheduling and window calculations.
        /// For hold notes this is StartTimeMs; for all others it is TimeMs.
        /// </summary>
        public int PrimaryTimeMs =>
            Type == NoteType.Hold ? StartTimeMs : TimeMs;

        /// <summary>True when this note is in the Hit or Missed terminal state.</summary>
        public bool IsResolved => State == NoteState.Hit || State == NoteState.Missed;
    }
}
