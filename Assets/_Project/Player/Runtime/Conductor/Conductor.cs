// Conductor.cs
// DSP-time driven song clock for the gameplay scene.
//
// Design (spec §3.3):
//   SongDspTimeMs        = (AudioSettings.dspTime - _startDspTime) * 1000.0
//   EffectiveChartTimeMs = SongDspTimeMs + chart.song.audioOffsetMs + UserOffsetMs
//
// The "effective" time is used for ALL chart evaluation:
//   – judgement timing
//   – note spawn / approach timing
//   – arena/lane/camera keyframe sampling
//   – hold tick processing
//
// Sign convention (locked, spec §3.3):
//   Positive offset = judge LATER (chart events occur later relative to audio).
//   Audio playback is always DSP-locked; offsets never shift audio.
//
// Usage:
//   1. Call Start(audioSource, audioOffsetMs) when gameplay begins.
//   2. Each frame, read EffectiveChartTimeMs for chart evaluation.
//   3. Call Stop() on pause/restart; Reset() before reuse.

using UnityEngine;

namespace RhythmicFlow.Player
{
    public class Conductor
    {
        // -----------------------------------------------------------------------
        // State
        // -----------------------------------------------------------------------

        // DSP time (seconds) recorded when StartPlaying was called.
        private double _startDspTimeSec;

        // Whether the conductor is actively running.
        private bool _isPlaying;

        // Chart-level audio offset from song metadata (spec §3.3).
        private int _chartAudioOffsetMs;

        // -----------------------------------------------------------------------
        // Public properties
        // -----------------------------------------------------------------------

        /// <summary>
        /// True between StartPlaying() and Stop().
        /// </summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>
        /// Milliseconds of song audio elapsed since StartPlaying(), based on DSP time.
        /// Returns 0 when not playing.
        /// Spec: DSP-time driven (spec §3.3 / §1 "Audio clock: DSP-time driven conductor").
        /// </summary>
        public double SongDspTimeMs
        {
            get
            {
                if (!_isPlaying) { return 0.0; }
                return (AudioSettings.dspTime - _startDspTimeSec) * 1000.0;
            }
        }

        /// <summary>
        /// The chart time used for ALL evaluation: judgement, keyframes, note spawn, ticks.
        /// Formula (locked, spec §3.3):
        ///   effectiveChartTimeMs = songDspTimeMs + audioOffsetMs + UserOffsetMs
        /// </summary>
        public double EffectiveChartTimeMs =>
            SongDspTimeMs + _chartAudioOffsetMs + PlayerSettingsStore.UserOffsetMs;

        // -----------------------------------------------------------------------
        // Control
        // -----------------------------------------------------------------------

        /// <summary>
        /// Begins the conductor clock. Call this at the same moment the AudioSource starts playing.
        /// <paramref name="chartAudioOffsetMs"/> is taken from chart.song.audioOffsetMs (spec §3.3).
        /// </summary>
        public void StartPlaying(int chartAudioOffsetMs)
        {
            _chartAudioOffsetMs = chartAudioOffsetMs;
            _startDspTimeSec    = AudioSettings.dspTime;
            _isPlaying          = true;
        }

        /// <summary>
        /// Stops the conductor. SongDspTimeMs and EffectiveChartTimeMs freeze at last value.
        /// Call Reset() before reuse.
        /// </summary>
        public void Stop()
        {
            _isPlaying = false;
        }

        /// <summary>
        /// Resets the conductor to its initial state (ready for a new StartPlaying call).
        /// Call this before replaying the same song or starting a new one (spec §9
        /// "on resume, restart the song from 0 and reset all judgement state").
        /// </summary>
        public void Reset()
        {
            _isPlaying          = false;
            _startDspTimeSec    = 0.0;
            _chartAudioOffsetMs = 0;
        }
    }
}
