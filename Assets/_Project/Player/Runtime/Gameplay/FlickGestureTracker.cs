// FlickGestureTracker.cs
// Per-touch gesture tracking for flick recognition (spec §7.3 / §7.3.1).
//
// Gesture is measured in NORMALIZED playfield coordinates [0..1] so that
// distance and velocity are directly comparable to FlickMinDistanceNorm and
// FlickMinVelocityNormPerSec settings (spec §8.3).
//
// Usage (called by the game's input layer each frame):
//   BeginTouch(touchId, timeMs, posNorm)     — TouchPhase.Began
//   UpdateTouch(touchId, timeMs, posNorm)    — TouchPhase.Moved / Stationary
//   EndTouch(touchId, timeMs, posNorm)       — TouchPhase.Ended / Cancelled
//   RemoveTouch(touchId)                     — after judgement is resolved
//   TryGetGesture(touchId, out snapshot)     — read accumulated data
//
// All hot-path methods are allocation-free (state objects are pooled).
//
// No UnityEditor APIs used.

using System.Collections.Generic;
using UnityEngine;

namespace RhythmicFlow.Player
{
    // -----------------------------------------------------------------------
    // FlickDir — axis-dominant direction in normalized playfield space
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gesture direction in normalized playfield XY:
    ///   +X = Right, −X = Left, +Y = Up, −Y = Down.
    ///
    /// This is in PLAYFIELD axes, NOT lane-relative axes.
    /// Lane-relative direction validation uses DisplacementNorm with the lane's
    /// center angle in JudgementEngine.TryJudgeFlick (spec §7.3.1).
    /// </summary>
    public enum FlickDir
    {
        None,
        Left,
        Right,
        Up,
        Down,
    }

    // -----------------------------------------------------------------------
    // FlickGestureSnapshot
    // -----------------------------------------------------------------------

    /// <summary>
    /// Read-only snapshot of one touch's accumulated gesture data.
    /// Consumed by JudgementEngine.TryJudgeFlick for threshold evaluation (spec §7.3.1).
    /// </summary>
    public struct FlickGestureSnapshot
    {
        /// <summary>Milliseconds elapsed from BeginTouch to the latest update.</summary>
        public double ElapsedMs;

        /// <summary>
        /// Peak distance from the start position in normalized playfield units.
        /// Compared against PlayerSettingsStore.FlickMinDistanceNorm (spec §8.3).
        /// </summary>
        public float DistanceNorm;

        /// <summary>
        /// Peak instantaneous velocity in normalized playfield units per second.
        /// Compared against PlayerSettingsStore.FlickMinVelocityNormPerSec (spec §8.3).
        /// </summary>
        public float VelocityNormPerSec;

        /// <summary>
        /// (lastPosNorm − startPosNorm): total displacement vector in normalized space.
        /// Used by TryJudgeFlick to project onto the lane-relative basis for direction check.
        /// </summary>
        public Vector2 DisplacementNorm;

        /// <summary>
        /// Axis-dominant direction in normalized playfield space.
        /// Useful for logging / debug display; actual direction validation in TryJudgeFlick
        /// uses DisplacementNorm projected onto lane-relative basis vectors.
        /// </summary>
        public FlickDir Direction;
    }

    // -----------------------------------------------------------------------
    // FlickEvent — discrete gesture event emitted by FlickGestureTracker
    // -----------------------------------------------------------------------

    /// <summary>
    /// Represents a single recognized flick gesture from one touch.
    /// Emitted by FlickGestureTracker.UpdateTouch when distance, velocity, and elapsed-time
    /// thresholds are all satisfied (spec §7.3 event-based model).
    ///
    /// After emission the tracker immediately resets the gesture baseline for that touch,
    /// so a subsequent gesture can produce a new FlickEvent without lifting.
    /// </summary>
    public struct FlickEvent
    {
        /// <summary>Touch that produced this event.</summary>
        public int TouchId;

        /// <summary>Chart time (ms) when the gesture crossed the thresholds.</summary>
        public double EventTimeMs;

        /// <summary>
        /// Touch position in normalized playfield coords at event time.
        /// JudgementEngine converts this to PlayfieldLocal XY for lane hit testing.
        /// </summary>
        public Vector2 PosNorm;

        /// <summary>
        /// Displacement from the current gesture baseline to the event position,
        /// in normalized playfield coords. Used for flick direction matching.
        /// </summary>
        public Vector2 DispNorm;

        /// <summary>Peak distance from the gesture baseline (normalized units) during this gesture.</summary>
        public float DistanceNorm;

        /// <summary>Peak instantaneous velocity (normalized units/sec) during this gesture.</summary>
        public float VelocityNormPerSec;

        /// <summary>
        /// Chart time (ms) when BeginTouch was called for this touch.
        /// Used by JudgementEngine to gate FlickRequireTouchBegin behaviour:
        /// events where (EventTimeMs - TouchBeginTimeMs) &gt; FlickMaxGestureTimeMs
        /// are discarded when the toggle is true.
        /// </summary>
        public double TouchBeginTimeMs;
    }

    // -----------------------------------------------------------------------
    // FlickGestureTracker
    // -----------------------------------------------------------------------

    /// <summary>
    /// Tracks per-touch gesture state for flick recognition.
    /// Maintains one entry per active touchId; state objects are pooled to avoid GC.
    /// Spec §7.3 / §7.3.1.
    /// </summary>
    public class FlickGestureTracker
    {
        // Minimum inter-update time for velocity to be meaningful (avoids /0).
        private const double MinDeltaTimeSec = 1e-4;

        // Below this displacement magnitude, direction = None.
        private const float DirectionEpsilon = 1e-4f;

        // Minimum interval (ms) between consecutive FlickEvents from the same touch.
        // Prevents duplicate events on frames with very high velocity (60fps = ~16ms, so 30ms
        // means at most one event every 2 frames per touch).
        private const double MinEventIntervalMs = 30.0;

        // -------------------------------------------------------------------
        // Internal per-touch state
        // -------------------------------------------------------------------

        private class TouchGestureState
        {
            public int     TouchId;
            public double  StartTimeMs;
            public Vector2 StartPosNorm;
            public Vector2 LastPosNorm;
            public double  LastTimeMs;
            public float   MaxDistanceNorm;
            public float   MaxVelocityNormPerSec;

            // ------- Event-based additions -------

            /// <summary>
            /// Chart time (ms) when BeginTouch was called for this touch.
            /// Set only by Reset (i.e. BeginTouch); NOT reset by ResetGesture.
            /// Carried into each FlickEvent so JudgementEngine can gate
            /// FlickRequireTouchBegin: events are invalid if
            /// (EventTimeMs - TouchBeginTimeMs) > FlickMaxGestureTimeMs.
            /// </summary>
            public double TouchBeginTimeMs;

            /// <summary>
            /// Chart time (ms) of the most recently emitted FlickEvent.
            /// Enforces the MinEventIntervalMs anti-spam guard.
            /// </summary>
            public double LastEventTimeMs;

            /// <summary>
            /// FIFO queue of FlickEvents awaiting consumption by JudgementEngine.
            /// Allocated once per TouchGestureState; cleared on Reset.
            /// </summary>
            public readonly Queue<FlickEvent> EventQueue = new Queue<FlickEvent>(4);

            public void Reset(int id, double timeMs, Vector2 posNorm)
            {
                TouchId               = id;
                StartTimeMs           = timeMs;
                StartPosNorm          = posNorm;
                LastPosNorm           = posNorm;
                LastTimeMs            = timeMs;
                MaxDistanceNorm       = 0f;
                MaxVelocityNormPerSec = 0f;
                TouchBeginTimeMs      = timeMs;
                LastEventTimeMs       = double.NegativeInfinity; // Allow first event immediately.
                EventQueue.Clear();
            }
        }

        // -------------------------------------------------------------------
        // Storage
        // -------------------------------------------------------------------

        private readonly Dictionary<int, TouchGestureState> _states =
            new Dictionary<int, TouchGestureState>();

        // Pooled state objects to avoid per-touch allocation.
        private readonly Stack<TouchGestureState> _pool =
            new Stack<TouchGestureState>(8);

        // -------------------------------------------------------------------
        // Touch lifecycle
        // -------------------------------------------------------------------

        /// <summary>
        /// Registers the start of a new touch gesture.
        /// Call when TouchPhase.Began.
        /// <paramref name="posNorm"/> must be in normalized playfield coordinates [0..1].
        /// </summary>
        public void BeginTouch(int touchId, double timeMs, Vector2 posNorm)
        {
            if (_states.TryGetValue(touchId, out TouchGestureState existing))
            {
                // Same id reused without an End (edge case) — reset in place.
                existing.Reset(touchId, timeMs, posNorm);
                return;
            }

            TouchGestureState state = _pool.Count > 0 ? _pool.Pop() : new TouchGestureState();
            state.Reset(touchId, timeMs, posNorm);
            _states[touchId] = state;
        }

        /// <summary>
        /// Updates an ongoing touch with its latest position.
        /// Call each frame for TouchPhase.Moved / Stationary.
        /// <paramref name="posNorm"/> must be in normalized playfield coordinates.
        /// </summary>
        public void UpdateTouch(int touchId, double timeMs, Vector2 posNorm)
        {
            if (!_states.TryGetValue(touchId, out TouchGestureState state)) { return; }

            // Accumulate maximum distance from start.
            float distance = (posNorm - state.StartPosNorm).magnitude;
            if (distance > state.MaxDistanceNorm)
            {
                state.MaxDistanceNorm = distance;
            }

            // Compute instantaneous velocity from the previous update.
            double deltaSec = (timeMs - state.LastTimeMs) * 0.001;
            if (deltaSec >= MinDeltaTimeSec)
            {
                float frameDist = (posNorm - state.LastPosNorm).magnitude;
                float velocity  = (float)(frameDist / deltaSec);

                if (velocity > state.MaxVelocityNormPerSec)
                {
                    state.MaxVelocityNormPerSec = velocity;
                }
            }

            state.LastPosNorm = posNorm;
            state.LastTimeMs  = timeMs;

            // ------- Event emission (event-based flick model, spec §7.3) -------
            // If all three gesture thresholds are met AND the anti-spam guard passes,
            // enqueue a FlickEvent and immediately reset the baseline so the next
            // motion can produce a new event (enables U→D patterns without lifting).

            double elapsedMs   = timeMs - state.StartTimeMs;
            bool   timeOk      = elapsedMs           <= PlayerSettingsStore.FlickMaxGestureTimeMs;
            bool   distOk      = state.MaxDistanceNorm        >= PlayerSettingsStore.FlickMinDistanceNorm;
            bool   velOk       = state.MaxVelocityNormPerSec  >= PlayerSettingsStore.FlickMinVelocityNormPerSec;
            bool   antiSpamOk  = (timeMs - state.LastEventTimeMs) >= MinEventIntervalMs;

            if (timeOk && distOk && velOk && antiSpamOk)
            {
                Vector2 disp = posNorm - state.StartPosNorm;

                var newEvt = new FlickEvent
                {
                    TouchId            = touchId,
                    EventTimeMs        = timeMs,
                    PosNorm            = posNorm,
                    DispNorm           = disp,
                    DistanceNorm       = state.MaxDistanceNorm,
                    VelocityNormPerSec = state.MaxVelocityNormPerSec,
                    TouchBeginTimeMs   = state.TouchBeginTimeMs,
                };
                state.EventQueue.Enqueue(newEvt);
                state.LastEventTimeMs = timeMs;

                // Reset baseline so next motion is measured fresh.
                // (LastPosNorm and LastTimeMs are already current; only clear accumulations.)
                state.StartTimeMs           = timeMs;
                state.StartPosNorm          = posNorm;
                state.MaxDistanceNorm       = 0f;
                state.MaxVelocityNormPerSec = 0f;

                Debug.Log(
                    $"[FlickEvent] touchId={touchId}  t={timeMs:F0}ms" +
                    $"  dist={newEvt.DistanceNorm:F4}norm" +
                    $"  vel={newEvt.VelocityNormPerSec:F3}norm/s" +
                    $"  elapsed={elapsedMs:F0}ms" +
                    $"  disp=({disp.x:F4},{disp.y:F4})");
            }
        }

        /// <summary>
        /// Records the final position of a touch (TouchPhase.Ended / Cancelled).
        /// The snapshot remains accessible via TryGetGesture until RemoveTouch is called.
        /// </summary>
        public void EndTouch(int touchId, double timeMs, Vector2 posNorm)
        {
            // Delegate to UpdateTouch — the final update captures peak velocity/distance.
            UpdateTouch(touchId, timeMs, posNorm);
        }

        /// <summary>
        /// Releases the touch entry back to the pool.
        /// Call after judgement for this touch has been resolved (hit or miss).
        /// </summary>
        public void RemoveTouch(int touchId)
        {
            if (!_states.TryGetValue(touchId, out TouchGestureState state)) { return; }
            _states.Remove(touchId);
            _pool.Push(state);
        }

        /// <summary>
        /// Resets the gesture baseline for a tracked touch to the given time and position.
        /// All accumulated metrics (max distance, max velocity) are cleared.
        ///
        /// Called by JudgementEngine when a touch first becomes eligible for a flick note
        /// in free-touch mode (FlickRequireTouchBegin = false), so that ElapsedMs is measured
        /// from the moment of eligibility rather than the original touch-down time.
        ///
        /// No-op if <paramref name="touchId"/> is not currently tracked.
        /// </summary>
        public void ResetGesture(int touchId, double timeMs, Vector2 posNorm)
        {
            if (!_states.TryGetValue(touchId, out TouchGestureState state)) { return; }

            state.StartTimeMs           = timeMs;
            state.StartPosNorm          = posNorm;
            state.LastPosNorm           = posNorm;
            state.LastTimeMs            = timeMs;
            state.MaxDistanceNorm       = 0f;
            state.MaxVelocityNormPerSec = 0f;
        }

        // -------------------------------------------------------------------
        // Snapshot query
        // -------------------------------------------------------------------

        /// <summary>
        /// Returns true and fills <paramref name="beginTimeMs"/> with the chart time (ms) at which
        /// BeginTouch was called for this touch. Returns false if touchId is not tracked.
        /// Used by JudgementEngine to gate FlickRequireTouchBegin arming without relying on
        /// the single-frame IsNew flag.
        /// </summary>
        public bool TryGetTouchBeginTimeMs(int touchId, out double beginTimeMs)
        {
            if (_states.TryGetValue(touchId, out TouchGestureState state))
            {
                beginTimeMs = state.TouchBeginTimeMs;
                return true;
            }
            beginTimeMs = 0;
            return false;
        }

        /// <summary>
        /// Returns true and fills <paramref name="snapshot"/> if the touchId is tracked.
        /// Returns false if the touchId is unknown (BeginTouch was never called).
        /// </summary>
        public bool TryGetGesture(int touchId, out FlickGestureSnapshot snapshot)
        {
            snapshot = default;

            if (!_states.TryGetValue(touchId, out TouchGestureState state)) { return false; }

            Vector2 displacement = state.LastPosNorm - state.StartPosNorm;

            snapshot = new FlickGestureSnapshot
            {
                ElapsedMs          = state.LastTimeMs - state.StartTimeMs,
                DistanceNorm       = state.MaxDistanceNorm,
                VelocityNormPerSec = state.MaxVelocityNormPerSec,
                DisplacementNorm   = displacement,
                Direction          = ClassifyDirection(displacement),
            };

            return true;
        }

        // -------------------------------------------------------------------
        // Event dequeue API
        // -------------------------------------------------------------------

        /// <summary>
        /// Removes and returns the oldest queued <see cref="FlickEvent"/> for the given touch.
        /// Returns true if an event was available; false if the touch has no queued events
        /// or is not currently tracked.
        ///
        /// Call from JudgementEngine once per TryJudgeFlick invocation to consume events
        /// one at a time (the caller loops until this returns false to handle rapid sequences).
        /// </summary>
        public bool TryDequeueEvent(int touchId, out FlickEvent evt)
        {
            evt = default;
            if (!_states.TryGetValue(touchId, out TouchGestureState state)) { return false; }
            if (state.EventQueue.Count == 0) { return false; }
            evt = state.EventQueue.Dequeue();
            return true;
        }

        // -------------------------------------------------------------------
        // Internal: axis-dominant direction classification
        // -------------------------------------------------------------------

        private static FlickDir ClassifyDirection(Vector2 displacement)
        {
            if (displacement.magnitude < DirectionEpsilon) { return FlickDir.None; }

            float dx = displacement.x;
            float dy = displacement.y;

            // Horizontal dominates ties.
            if (Mathf.Abs(dx) >= Mathf.Abs(dy))
            {
                return dx >= 0f ? FlickDir.Right : FlickDir.Left;
            }

            return dy >= 0f ? FlickDir.Up : FlickDir.Down;
        }
    }
}
