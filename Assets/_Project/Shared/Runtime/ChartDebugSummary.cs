// ChartDebugSummary.cs
// Developer utility: builds a readable text summary of a loaded ChartJsonV1.
// Useful for quick sanity-checks after loading or in Editor scripts.
// Not used in gameplay; no Unity dependencies.

using System.Text;

namespace RhythmicFlow.Shared
{
    public static class ChartDebugSummary
    {
        // Returns a formatted multi-line summary of the chart.
        // Covers song metadata, tempo segment count, and per-type note counts.
        public static string BuildSummary(ChartJsonV1 chart)
        {
            if (chart == null)
            {
                return "[ChartDebugSummary] chart is null.";
            }

            var sb = new StringBuilder();

            sb.AppendLine("=== ChartDebugSummary ===");
            sb.AppendLine($"formatVersion : {chart.formatVersion}");

            // Song metadata.
            if (chart.song != null)
            {
                sb.AppendLine($"songId        : {chart.song.songId}");
                sb.AppendLine($"difficultyId  : {chart.song.difficultyId}");
                sb.AppendLine($"audioFile     : {chart.song.audioFile}");
                sb.AppendLine($"audioOffsetMs : {chart.song.audioOffsetMs}");
            }
            else
            {
                sb.AppendLine("song          : (null)");
            }

            // Tempo.
            int segCount = chart.tempo?.segments?.Count ?? 0;
            sb.AppendLine($"tempoSegments : {segCount}");

            // Object counts.
            int arenaCount = chart.arenas?.Count ?? 0;
            int laneCount  = chart.lanes?.Count  ?? 0;
            sb.AppendLine($"arenas        : {arenaCount}");
            sb.AppendLine($"lanes         : {laneCount}");

            // Note counts by type.
            int tapCount     = 0;
            int flickCount   = 0;
            int catchCount   = 0;
            int holdCount    = 0;
            int unknownCount = 0;

            if (chart.notes != null)
            {
                foreach (ChartNote note in chart.notes)
                {
                    if (note == null) { continue; }

                    switch (note.type)
                    {
                        case NoteType.Tap:   tapCount++;     break;
                        case NoteType.Flick: flickCount++;   break;
                        case NoteType.Catch: catchCount++;   break;
                        case NoteType.Hold:  holdCount++;    break;
                        default:             unknownCount++; break;
                    }
                }
            }

            int totalNotes = tapCount + flickCount + catchCount + holdCount + unknownCount;
            sb.AppendLine($"notes (total) : {totalNotes}");
            sb.AppendLine($"  tap         : {tapCount}");
            sb.AppendLine($"  flick       : {flickCount}");
            sb.AppendLine($"  catch       : {catchCount}");
            sb.AppendLine($"  hold        : {holdCount}");

            if (unknownCount > 0)
            {
                sb.AppendLine($"  UNKNOWN     : {unknownCount}  ← invalid 'type' field");
            }

            sb.Append("=========================");

            return sb.ToString();
        }
    }
}
