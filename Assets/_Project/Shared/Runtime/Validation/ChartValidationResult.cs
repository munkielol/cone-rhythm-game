// ChartValidationResult.cs
// Holds all errors and warnings produced by ChartValidator.Validate().
//
// Errors   → export-blocking; must be fixed before the .rpk can be written.
// Warnings → informational; do not block export but should be reviewed.

using System.Collections.Generic;
using System.Text;

namespace RhythmicFlow.Shared
{
    public class ChartValidationResult
    {
        // True when there are zero errors (warnings are allowed and do not affect validity).
        public bool IsValid => Errors.Count == 0;

        // Export-blocking problems. Fix all of these before exporting.
        public List<string> Errors { get; } = new List<string>();

        // Non-blocking advisories. Review these for quality/readability.
        public List<string> Warnings { get; } = new List<string>();

        // Adds one export-blocking error message.
        public void AddError(string message) => Errors.Add(message);

        // Adds one non-blocking warning message.
        public void AddWarning(string message) => Warnings.Add(message);

        // Returns a formatted summary suitable for logging or display.
        public override string ToString()
        {
            var sb = new StringBuilder();

            sb.AppendLine(IsValid
                ? $"Chart validation PASSED ({Warnings.Count} warning(s))"
                : $"Chart validation FAILED ({Errors.Count} error(s), {Warnings.Count} warning(s))");

            foreach (string e in Errors)
            {
                sb.AppendLine($"  [ERROR] {e}");
            }

            foreach (string w in Warnings)
            {
                sb.AppendLine($"  [WARN]  {w}");
            }

            return sb.ToString();
        }
    }
}
