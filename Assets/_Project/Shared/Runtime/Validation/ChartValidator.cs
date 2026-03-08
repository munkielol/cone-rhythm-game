// ChartValidator.cs
// Validates a deserialized ChartJsonV1 against the v0 spec rules.
// Call Validate() after a successful ChartJsonReader.TryReadFromText().
//
// Errors (export-blocking, spec §12.1):
//   – formatVersion not supported
//   – tempo segments invalid (missing at 0, unsorted, overlap, bad BPM)
//   – null required arrays (arenas, lanes, notes)
//   – empty or duplicate IDs (arenaId, laneId, noteId)
//   – lane references a missing arenaId
//   – note references a missing laneId
//   – required track has 0 keyframes (all arena/lane tracks are required)
//   – keyframes not sorted ascending by timeMs
//   – duplicate timeMs in a track
//   – invalid easing string
//   – enabled track: value not exactly 0 or 1
//   – enabled track: easing not "hold"
//   – hold note: startTimeMs >= endTimeMs
//   – hold tick: outside [startTimeMs, endTimeMs], unsorted, or duplicate
//   – unknown note type or flick direction
//
// Warnings (non-blocking, spec §12.2):
//   – enabled=1 keyframe alongside opacity≈0 keyframe (interactive but invisible)
//   – arcSweepDeg value outside (0..360]
//   – widthDeg value <= 0
//   – opacity value outside [0..1]
//   – more than MaxSimultaneousEnabledLanesWarning lanes enabled at chart start

using System;
using System.Collections.Generic;

namespace RhythmicFlow.Shared
{
    public static class ChartValidator
    {
        // Only formatVersion 1 is supported in v0.
        private const int SupportedFormatVersion = 1;

        // Opacity values below this threshold are treated as "nearly invisible" for warnings.
        private const float OpacityNearZeroThreshold = 0.05f;

        // Warn when this many or more lanes are enabled from chart start.
        private const int MaxSimultaneousEnabledLanesWarning = 8;

        // All valid easing strings (spec §5.1).
        private static readonly HashSet<string> ValidEasings =
            new HashSet<string>(StringComparer.Ordinal) { "linear", "easeInOut", "hold" };

        // All valid flick direction strings (spec §9.3).
        private static readonly HashSet<string> ValidFlickDirections =
            new HashSet<string>(StringComparer.Ordinal) { "L", "R", "U", "D" };

        // All valid tempo segment type strings (spec §3.2).
        private static readonly HashSet<string> ValidTempoTypes =
            new HashSet<string>(StringComparer.Ordinal) { "constant", "ramp" };

        // ---------------------------------------------------------------------------
        // Public entry point
        // ---------------------------------------------------------------------------

        // Validates the given chart and returns a ChartValidationResult.
        // 'chart' must not be null (pass the output of ChartJsonReader.TryReadFromText).
        public static ChartValidationResult Validate(ChartJsonV1 chart)
        {
            var result = new ChartValidationResult();

            if (chart == null)
            {
                result.AddError("Chart is null.");
                return result;
            }

            ValidateFormatVersion(chart, result);
            ValidateSong(chart, result);
            ValidateTempo(chart, result);

            // Build ID sets while validating, then use them for reference checks.
            HashSet<string> arenaIds = ValidateArenas(chart, result);
            HashSet<string> laneIds  = ValidateLanes(chart, arenaIds, result);
            ValidateNotes(chart, laneIds, result);

            // Camera tracks are optional in v0 (spec §14: "suggested v0").
            // TODO: add camera track validation when camera is promoted to required.

            return result;
        }

        // ---------------------------------------------------------------------------
        // Section: format version
        // ---------------------------------------------------------------------------

        private static void ValidateFormatVersion(ChartJsonV1 chart, ChartValidationResult result)
        {
            if (chart.formatVersion != SupportedFormatVersion)
            {
                result.AddError(
                    $"Unsupported formatVersion: {chart.formatVersion}. " +
                    $"Expected {SupportedFormatVersion}.");
            }
        }

        // ---------------------------------------------------------------------------
        // Section: song metadata
        // ---------------------------------------------------------------------------

        private static void ValidateSong(ChartJsonV1 chart, ChartValidationResult result)
        {
            if (chart.song == null)
            {
                result.AddError("song is null.");
                return;
            }

            if (string.IsNullOrEmpty(chart.song.songId))
            {
                result.AddError("song.songId is empty or null.");
            }

            if (string.IsNullOrEmpty(chart.song.difficultyId))
            {
                result.AddError("song.difficultyId is empty or null.");
            }

            if (string.IsNullOrEmpty(chart.song.audioFile))
            {
                result.AddError("song.audioFile is empty or null.");
            }
        }

        // ---------------------------------------------------------------------------
        // Section: tempo map
        // ---------------------------------------------------------------------------

        private static void ValidateTempo(ChartJsonV1 chart, ChartValidationResult result)
        {
            if (chart.tempo == null)
            {
                result.AddError("tempo is null.");
                return;
            }

            List<TempoSegment> segs = chart.tempo.segments;

            if (segs == null || segs.Count == 0)
            {
                result.AddError(
                    "tempo.segments is empty or null. " +
                    "At least one segment starting at timeMs=0 is required.");
                return;
            }

            // First segment must start at 0 (spec §12.1 / §2.2).
            if (segs[0].startTimeMs != 0)
            {
                result.AddError(
                    $"First tempo segment must have startTimeMs=0, " +
                    $"but found startTimeMs={segs[0].startTimeMs}.");
            }

            for (int i = 0; i < segs.Count; i++)
            {
                TempoSegment seg = segs[i];
                string segCtx = $"tempo.segments[{i}]";

                if (seg == null)
                {
                    result.AddError($"{segCtx} is null.");
                    continue;
                }

                // Validate type string.
                if (!ValidTempoTypes.Contains(seg.type ?? ""))
                {
                    result.AddError(
                        $"{segCtx}: unknown type '{seg.type}'. " +
                        $"Expected 'constant' or 'ramp'.");
                    continue; // Can't check type-specific fields without knowing the type.
                }

                if (seg.type == "constant")
                {
                    if (seg.bpm <= 0f)
                    {
                        result.AddError(
                            $"{segCtx} (constant): bpm must be > 0, got {seg.bpm}.");
                    }
                }
                else // "ramp"
                {
                    if (seg.endTimeMs <= seg.startTimeMs)
                    {
                        result.AddError(
                            $"{segCtx} (ramp): endTimeMs ({seg.endTimeMs}) must be > " +
                            $"startTimeMs ({seg.startTimeMs}).");
                    }

                    if (seg.startBpm <= 0f)
                    {
                        result.AddError(
                            $"{segCtx} (ramp): startBpm must be > 0, got {seg.startBpm}.");
                    }

                    if (seg.endBpm <= 0f)
                    {
                        result.AddError(
                            $"{segCtx} (ramp): endBpm must be > 0, got {seg.endBpm}.");
                    }
                }

                // Check ordering: each segment must start strictly after the previous.
                if (i > 0)
                {
                    TempoSegment prev = segs[i - 1];

                    if (seg.startTimeMs <= prev.startTimeMs)
                    {
                        result.AddError(
                            $"{segCtx}: startTimeMs ({seg.startTimeMs}) must be strictly greater " +
                            $"than previous segment's startTimeMs ({prev.startTimeMs}). " +
                            $"Segments must be sorted and non-overlapping.");
                    }

                    // Ramp segments have an explicit end; check they don't overlap the next start.
                    if (prev.type == "ramp" && prev.endTimeMs > seg.startTimeMs)
                    {
                        result.AddError(
                            $"tempo.segments[{i - 1}] (ramp): endTimeMs ({prev.endTimeMs}) " +
                            $"overlaps the start of segment[{i}] (startTimeMs={seg.startTimeMs}).");
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------
        // Section: arenas
        // ---------------------------------------------------------------------------

        // Validates all arenas and returns the set of all valid arenaIds found.
        private static HashSet<string> ValidateArenas(
            ChartJsonV1 chart,
            ChartValidationResult result)
        {
            var arenaIds = new HashSet<string>(StringComparer.Ordinal);

            if (chart.arenas == null)
            {
                result.AddError("arenas list is null.");
                return arenaIds;
            }

            for (int i = 0; i < chart.arenas.Count; i++)
            {
                ChartArena arena = chart.arenas[i];
                string arenaRef = $"arenas[{i}]";

                if (arena == null)
                {
                    result.AddError($"{arenaRef} is null.");
                    continue;
                }

                // Validate ID uniqueness.
                if (string.IsNullOrEmpty(arena.arenaId))
                {
                    result.AddError($"{arenaRef}: arenaId is empty or null.");
                }
                else if (!arenaIds.Add(arena.arenaId))
                {
                    result.AddError($"{arenaRef}: duplicate arenaId '{arena.arenaId}'.");
                }

                string ctx = $"arenas[{i}](id='{arena.arenaId}')";

                // All eight arena tracks are required (spec §5.9 / §12.1).
                ValidateRequiredTrack(arena.enabled,       ctx, "enabled",       isEnabledTrack: true,  result);
                ValidateRequiredTrack(arena.opacity,       ctx, "opacity",        isEnabledTrack: false, result);
                ValidateRequiredTrack(arena.centerX,       ctx, "centerX",        isEnabledTrack: false, result);
                ValidateRequiredTrack(arena.centerY,       ctx, "centerY",        isEnabledTrack: false, result);
                ValidateRequiredTrack(arena.outerRadius,   ctx, "outerRadius",    isEnabledTrack: false, result);
                ValidateRequiredTrack(arena.bandThickness, ctx, "bandThickness",  isEnabledTrack: false, result);
                ValidateRequiredTrack(arena.arcStartDeg,   ctx, "arcStartDeg",    isEnabledTrack: false, result);
                ValidateRequiredTrack(arena.arcSweepDeg,   ctx, "arcSweepDeg",    isEnabledTrack: false, result);

                // Range warnings.
                WarnIfOpacityOutOfRange(arena.opacity,     ctx, "opacity",     result);
                WarnIfArcSweepInvalid  (arena.arcSweepDeg, ctx, "arcSweepDeg", result);
                WarnIfEnabledInvisible (arena.enabled, arena.opacity, ctx,      result);
            }

            return arenaIds;
        }

        // ---------------------------------------------------------------------------
        // Section: lanes
        // ---------------------------------------------------------------------------

        // Validates all lanes and returns the set of all valid laneIds found.
        private static HashSet<string> ValidateLanes(
            ChartJsonV1 chart,
            HashSet<string> arenaIds,
            ChartValidationResult result)
        {
            var laneIds = new HashSet<string>(StringComparer.Ordinal);

            if (chart.lanes == null)
            {
                result.AddError("lanes list is null.");
                return laneIds;
            }

            int enabledAtStartCount = 0;

            for (int i = 0; i < chart.lanes.Count; i++)
            {
                ChartLane lane = chart.lanes[i];
                string laneRef = $"lanes[{i}]";

                if (lane == null)
                {
                    result.AddError($"{laneRef} is null.");
                    continue;
                }

                // Validate ID uniqueness.
                if (string.IsNullOrEmpty(lane.laneId))
                {
                    result.AddError($"{laneRef}: laneId is empty or null.");
                }
                else if (!laneIds.Add(lane.laneId))
                {
                    result.AddError($"{laneRef}: duplicate laneId '{lane.laneId}'.");
                }

                // Validate arenaId reference.
                if (string.IsNullOrEmpty(lane.arenaId))
                {
                    result.AddError($"{laneRef}(id='{lane.laneId}'): arenaId is empty or null.");
                }
                else if (!arenaIds.Contains(lane.arenaId))
                {
                    result.AddError(
                        $"{laneRef}(id='{lane.laneId}'): arenaId '{lane.arenaId}' " +
                        $"does not match any existing arena.");
                }

                string ctx = $"lanes[{i}](id='{lane.laneId}')";

                // All four lane tracks are required (spec §7 / §12.1).
                ValidateRequiredTrack(lane.enabled,   ctx, "enabled",   isEnabledTrack: true,  result);
                ValidateRequiredTrack(lane.opacity,   ctx, "opacity",   isEnabledTrack: false, result);
                ValidateRequiredTrack(lane.centerDeg, ctx, "centerDeg", isEnabledTrack: false, result);
                ValidateRequiredTrack(lane.widthDeg,  ctx, "widthDeg",  isEnabledTrack: false, result);

                // Range warnings.
                WarnIfOpacityOutOfRange(lane.opacity,  ctx, "opacity",  result);
                WarnIfWidthDegInvalid  (lane.widthDeg, ctx, "widthDeg", result);
                WarnIfEnabledInvisible (lane.enabled, lane.opacity, ctx, result);

                // Count how many lanes start in the enabled state.
                if (IsInitiallyEnabled(lane.enabled))
                {
                    enabledAtStartCount++;
                }
            }

            // Warn when many lanes are simultaneously enabled from the start.
            if (enabledAtStartCount > MaxSimultaneousEnabledLanesWarning)
            {
                result.AddWarning(
                    $"{enabledAtStartCount} lanes are enabled at chart start, which exceeds the " +
                    $"recommended maximum of {MaxSimultaneousEnabledLanesWarning}. " +
                    $"Consider disabling lanes that are not yet needed (readability/perf risk).");
            }

            return laneIds;
        }

        // ---------------------------------------------------------------------------
        // Section: notes
        // ---------------------------------------------------------------------------

        private static void ValidateNotes(
            ChartJsonV1 chart,
            HashSet<string> laneIds,
            ChartValidationResult result)
        {
            if (chart.notes == null)
            {
                result.AddError("notes list is null.");
                return;
            }

            var noteIds = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < chart.notes.Count; i++)
            {
                ChartNote note = chart.notes[i];
                string noteRef = $"notes[{i}]";

                if (note == null)
                {
                    result.AddError($"{noteRef} is null.");
                    continue;
                }

                // Validate noteId uniqueness.
                if (string.IsNullOrEmpty(note.noteId))
                {
                    result.AddError($"{noteRef}: noteId is empty or null.");
                }
                else if (!noteIds.Add(note.noteId))
                {
                    result.AddError($"{noteRef}: duplicate noteId '{note.noteId}'.");
                }

                string ctx = $"notes[{i}](id='{note.noteId}')";

                // Validate laneId reference.
                if (string.IsNullOrEmpty(note.laneId))
                {
                    result.AddError($"{ctx}: laneId is empty or null.");
                }
                else if (!laneIds.Contains(note.laneId))
                {
                    result.AddError(
                        $"{ctx}: laneId '{note.laneId}' does not match any existing lane.");
                }

                // Validate type-specific fields.
                switch (note.type)
                {
                    case NoteType.Tap:
                    case NoteType.Catch:
                        // timeMs is the only type-specific field; no extra range rules in v0.
                        break;

                    case NoteType.Flick:
                        // timeMs is used (shared field). Validate direction.
                        if (!ValidFlickDirections.Contains(note.direction ?? ""))
                        {
                            result.AddError(
                                $"{ctx}: invalid flick direction '{note.direction}'. " +
                                $"Expected one of: L, R, U, D.");
                        }
                        break;

                    case NoteType.Hold:
                        ValidateHoldNote(note, ctx, result);
                        break;

                    default:
                        result.AddError(
                            $"{ctx}: unknown note type '{note.type}'. " +
                            $"Expected: tap, flick, catch, hold.");
                        break;
                }
            }
        }

        private static void ValidateHoldNote(
            ChartNote note,
            string ctx,
            ChartValidationResult result)
        {
            // start must be strictly before end.
            if (note.startTimeMs >= note.endTimeMs)
            {
                result.AddError(
                    $"{ctx}(hold): startTimeMs ({note.startTimeMs}) must be less than " +
                    $"endTimeMs ({note.endTimeMs}).");
            }

            List<int> ticks = note.tickTimesMs;

            if (ticks == null)
            {
                result.AddError($"{ctx}(hold): tickTimesMs is null.");
                return;
            }

            // Each tick must be in bounds and strictly increasing (no duplicates).
            int prevTick = int.MinValue;

            for (int t = 0; t < ticks.Count; t++)
            {
                int tick = ticks[t];

                if (tick < note.startTimeMs || tick > note.endTimeMs)
                {
                    result.AddError(
                        $"{ctx}(hold): tickTimesMs[{t}]={tick} is outside " +
                        $"[startTimeMs={note.startTimeMs}, endTimeMs={note.endTimeMs}].");
                }

                if (tick <= prevTick)
                {
                    // <= catches both equal (duplicate) and descending (unsorted).
                    result.AddError(
                        $"{ctx}(hold): tickTimesMs[{t}]={tick} is not strictly greater than " +
                        $"the previous tick ({prevTick}). " +
                        $"tickTimesMs must be strictly increasing.");
                }

                prevTick = tick;
            }
        }

        // ---------------------------------------------------------------------------
        // Track validation helpers
        // ---------------------------------------------------------------------------

        // Validates a single FloatTrack for:
        //   – non-null with at least 1 keyframe (required tracks)
        //   – keyframes sorted ascending by timeMs
        //   – no duplicate timeMs values
        //   – valid easing strings
        //   – if isEnabledTrack: values must be exactly 0 or 1, easing must be "hold"
        private static void ValidateRequiredTrack(
            FloatTrack track,
            string ownerCtx,
            string trackName,
            bool isEnabledTrack,
            ChartValidationResult result)
        {
            string fullCtx = $"{ownerCtx}.{trackName}";

            if (track == null)
            {
                result.AddError(
                    $"{fullCtx}: track is null. All arena and lane tracks are required.");
                return;
            }

            List<FloatKeyframe> kfs = track.keyframes;

            if (kfs == null || kfs.Count == 0)
            {
                result.AddError(
                    $"{fullCtx}: 0 keyframes. Required tracks must have at least 1 keyframe " +
                    $"(spec §5.9). The chart is invalid and cannot be exported.");
                return;
            }

            // Sentinel: all legitimate timeMs values are >= 0, so int.MinValue is safe.
            int prevTime = int.MinValue;

            for (int k = 0; k < kfs.Count; k++)
            {
                FloatKeyframe kf = kfs[k];
                string kfCtx = $"{fullCtx}.keyframes[{k}]";

                if (kf == null)
                {
                    result.AddError($"{kfCtx}: keyframe is null.");
                    continue;
                }

                // Sort check.
                if (kf.timeMs < prevTime)
                {
                    result.AddError(
                        $"{kfCtx}: timeMs={kf.timeMs} is less than previous keyframe " +
                        $"timeMs={prevTime}. Keyframes must be sorted ascending.");
                }

                // Duplicate check (export-blocking, spec §5.9).
                if (kf.timeMs == prevTime)
                {
                    result.AddError(
                        $"{kfCtx}: duplicate timeMs={kf.timeMs}. " +
                        $"Two keyframes share the same timestamp, which is export-blocking " +
                        $"(spec §5.9).");
                }

                prevTime = kf.timeMs;

                // Easing validity check.
                if (!ValidEasings.Contains(kf.easing ?? ""))
                {
                    result.AddError(
                        $"{kfCtx}: unknown easing '{kf.easing}'. " +
                        $"Expected: linear, easeInOut, hold.");
                }

                // Enabled track: extra rules (spec §5.6 / §12.1).
                if (isEnabledTrack)
                {
                    // Value must be exactly 0 or 1. Float equality is safe here because
                    // JSON integers 0 and 1 parse to exactly 0.0f and 1.0f in IEEE 754.
                    if (kf.value != 0f && kf.value != 1f)
                    {
                        result.AddError(
                            $"{kfCtx}: enabled track value must be exactly 0 or 1, " +
                            $"got {kf.value} (spec §5.6).");
                    }

                    if (kf.easing != "hold")
                    {
                        result.AddError(
                            $"{kfCtx}: enabled track easing must be 'hold', " +
                            $"got '{kf.easing}' (spec §5.6).");
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------
        // Warning helpers
        // ---------------------------------------------------------------------------

        // Warns if any opacity keyframe value is outside [0..1].
        private static void WarnIfOpacityOutOfRange(
            FloatTrack track,
            string ctx,
            string trackName,
            ChartValidationResult result)
        {
            if (track?.keyframes == null) { return; }

            for (int k = 0; k < track.keyframes.Count; k++)
            {
                FloatKeyframe kf = track.keyframes[k];
                if (kf == null) { continue; }

                if (kf.value < 0f || kf.value > 1f)
                {
                    result.AddWarning(
                        $"{ctx}.{trackName}.keyframes[{k}]: " +
                        $"value {kf.value} is outside the expected range [0..1].");
                }
            }
        }

        // Warns if any arcSweepDeg keyframe value is outside (0..360].
        private static void WarnIfArcSweepInvalid(
            FloatTrack track,
            string ctx,
            string trackName,
            ChartValidationResult result)
        {
            if (track?.keyframes == null) { return; }

            for (int k = 0; k < track.keyframes.Count; k++)
            {
                FloatKeyframe kf = track.keyframes[k];
                if (kf == null) { continue; }

                if (kf.value <= 0f || kf.value > 360f)
                {
                    result.AddWarning(
                        $"{ctx}.{trackName}.keyframes[{k}]: " +
                        $"arcSweepDeg value {kf.value} is outside (0..360]. " +
                        $"360 = full ring; value must be positive.");
                }
            }
        }

        // Warns if any widthDeg keyframe value is <= 0 (degenerate lane).
        private static void WarnIfWidthDegInvalid(
            FloatTrack track,
            string ctx,
            string trackName,
            ChartValidationResult result)
        {
            if (track?.keyframes == null) { return; }

            for (int k = 0; k < track.keyframes.Count; k++)
            {
                FloatKeyframe kf = track.keyframes[k];
                if (kf == null) { continue; }

                if (kf.value <= 0f)
                {
                    result.AddWarning(
                        $"{ctx}.{trackName}.keyframes[{k}]: " +
                        $"widthDeg value {kf.value} must be > 0 for a non-degenerate lane.");
                }
            }
        }

        // Warns when an enabled track has any keyframe with value=1 while an opacity
        // track has any keyframe with value near zero. This is an approximation — full
        // cross-track evaluation is not performed to keep the validator stateless.
        // (spec §12.2: "interactive but invisible")
        private static void WarnIfEnabledInvisible(
            FloatTrack enabledTrack,
            FloatTrack opacityTrack,
            string ctx,
            ChartValidationResult result)
        {
            if (enabledTrack?.keyframes == null || opacityTrack?.keyframes == null) { return; }

            bool anyEnabledOn       = false;
            bool anyNearZeroOpacity = false;

            foreach (FloatKeyframe kf in enabledTrack.keyframes)
            {
                if (kf != null && kf.value >= 0.5f)
                {
                    anyEnabledOn = true;
                    break;
                }
            }

            foreach (FloatKeyframe kf in opacityTrack.keyframes)
            {
                if (kf != null && kf.value < OpacityNearZeroThreshold)
                {
                    anyNearZeroOpacity = true;
                    break;
                }
            }

            if (anyEnabledOn && anyNearZeroOpacity)
            {
                result.AddWarning(
                    $"{ctx}: has enabled=1 keyframe(s) and opacity≈0 keyframe(s). " +
                    $"This means the object may be interactive while nearly invisible " +
                    $"(spec §12.2). Verify this is intentional.");
            }
        }

        // Returns true when the enabled track's first keyframe is in the enabled state
        // (value >= 0.5, which the runtime interprets as true per spec §5.9).
        private static bool IsInitiallyEnabled(FloatTrack enabledTrack)
        {
            if (enabledTrack?.keyframes == null || enabledTrack.keyframes.Count == 0)
            {
                return false;
            }

            FloatKeyframe first = enabledTrack.keyframes[0];
            return first != null && first.value >= 0.5f;
        }
    }
}
