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

        // Last computed SongDspTimeMs captured just before Stop() (F5 fix).
        // Returned by SongDspTimeMs when !_isPlaying so callers see a frozen
        // value rather than 0. Reset to 0 by Reset().
        private double _lastKnownSongDspTimeMs;

        // -----------------------------------------------------------------------
        // Public properties
        // -----------------------------------------------------------------------

        /// <summary>
        /// True between StartPlaying() and Stop().
        /// </summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>
        /// Milliseconds of song audio elapsed since StartPlaying(), based on DSP time.
        /// Frozen at the last known value when not playing (i.e. after Stop()).
        /// Returns 0 only before the first StartPlaying call (or after Reset()).
        /// Spec: DSP-time driven (spec §3.3 / §1 "Audio clock: DSP-time driven conductor").
        ///
        /// IMPORTANT: callers must ensure the AudioSource is stopped before reading
        /// this after Stop() — the conductor clock and the audio output are independent
        /// and must be halted together to stay in sync.
        /// </summary>
        public double SongDspTimeMs
        {
            get
            {
                if (!_isPlaying) { return _lastKnownSongDspTimeMs; }

                _lastKnownSongDspTimeMs = (AudioSettings.dspTime - _startDspTimeSec) * 1000.0;
                return _lastKnownSongDspTimeMs;
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
            _isPlaying               = false;
            _startDspTimeSec         = 0.0;
            _chartAudioOffsetMs      = 0;
            _lastKnownSongDspTimeMs  = 0.0;
        }
    }
}
