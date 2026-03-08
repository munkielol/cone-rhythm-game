// ChartJsonReader.cs
// Loads a ChartJsonV1 from a file path or a raw JSON string.
// Uses Unity's built-in JsonUtility — no external packages.
//
// JsonUtility limitations relevant to ChartJsonV1 (documented for future contributors):
//   1. No Dictionary<K,V> support.
//      → All variable-keyed data uses named list/struct fields instead.
//   2. No polymorphic type deserialization (base class → subclass).
//      → All note types are flattened into one ChartNote class with a "type" field.
//   3. Nullable value types not supported.
//      → C# field initializers supply safe defaults (e.g. judging = true, lists = new List).
//   4. 'judging = true' default: JsonUtility preserves C# field initializers for fields
//      absent from JSON, so "judging": false only appears in the output when explicitly false.

using System;
using System.IO;
using UnityEngine;

namespace RhythmicFlow.Shared
{
    public static class ChartJsonReader
    {
        // Reads a ChartJsonV1 from the file at the given absolute path.
        // Returns true and sets 'chart' on success.
        // Returns false and sets 'error' with a human-readable message on failure.
        public static bool TryReadFromFile(
            string path,
            out ChartJsonV1 chart,
            out string error)
        {
            chart = null;
            error = null;

            if (string.IsNullOrEmpty(path))
            {
                error = "Path is null or empty.";
                return false;
            }

            if (!File.Exists(path))
            {
                error = $"Chart file not found: {path}";
                return false;
            }

            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = $"Failed to read chart file '{path}': {ex.Message}";
                return false;
            }

            return TryReadFromText(json, out chart, out error);
        }

        // Parses a ChartJsonV1 from a raw JSON string.
        // Returns true and sets 'chart' on success.
        // Returns false and sets 'error' with a human-readable message on failure.
        public static bool TryReadFromText(
            string json,
            out ChartJsonV1 chart,
            out string error)
        {
            chart = null;
            error = null;

            if (string.IsNullOrEmpty(json))
            {
                error = "JSON string is null or empty.";
                return false;
            }

            try
            {
                chart = JsonUtility.FromJson<ChartJsonV1>(json);
            }
            catch (Exception ex)
            {
                error = $"JSON parse error: {ex.Message}";
                return false;
            }

            if (chart == null)
            {
                error = "JSON deserialized to null. " +
                        "The root object may be missing or the JSON may be malformed.";
                return false;
            }

            return true;
        }
    }
}
