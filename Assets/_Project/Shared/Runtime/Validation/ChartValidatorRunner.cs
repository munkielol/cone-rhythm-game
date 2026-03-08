// ChartValidatorRunner.cs
// Reads a ChartJsonV1 fixture from disk, runs ChartValidator, and returns a report.
// Not a MonoBehaviour — designed to be called from Editor tooling or future test runners.
//
// Usage:
//   string report = ChartValidatorRunner.RunFixtureValidation("/path/to/fixture.json");
//   Debug.Log(report);

using System;
using System.IO;
using System.Text;

namespace RhythmicFlow.Shared
{
    public static class ChartValidatorRunner
    {
        // Reads the JSON file at fixturePath, parses it as ChartJsonV1,
        // runs ChartValidator.Validate(), and returns a formatted result string.
        // Always returns a non-null string; never throws.
        public static string RunFixtureValidation(string fixturePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== ChartValidatorRunner ===");
            sb.AppendLine($"Fixture : {fixturePath}");

            // Step 1: Read raw JSON from disk.
            string json;
            try
            {
                json = File.ReadAllText(fixturePath);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"[FAIL] Could not read file: {ex.Message}");
                return sb.ToString();
            }

            // Step 2: Parse JSON into ChartJsonV1 (uses Unity JsonUtility internally).
            if (!ChartJsonReader.TryReadFromText(json, out ChartJsonV1 chart, out string parseError))
            {
                sb.AppendLine($"[FAIL] JSON parse error: {parseError}");
                return sb.ToString();
            }

            // Step 3: Run the validator.
            ChartValidationResult result = ChartValidator.Validate(chart);

            // Step 4: Append the validation report.
            sb.AppendLine(result.ToString());

            return sb.ToString();
        }
    }
}
