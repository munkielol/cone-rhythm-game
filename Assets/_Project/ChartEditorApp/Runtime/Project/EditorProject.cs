// EditorProject.cs
// The authoritative in-memory model for a chart editor project.
// Persisted as *.rproj.json on disk (spec §12A).
//
// The rproj.json holds ALL authoring data (spec §12A):
//   – paths to imported OGG + jacket sources
//   – song metadata inputs (title, artist, preview range)
//   – all difficulties' chart data
//   – chart editor-only metadata (lane column ordering, last selected objects, etc.)
//
// JSON serialization uses Unity JsonUtility (no external packages).
// The project file is the authoritative source of truth; the exported .rpk is derived from it.
//
// No UnityEditor APIs used here (spec: ChartEditorApp must not use UnityEditor namespace).

using System;
using System.Collections.Generic;
using System.IO;
using RhythmicFlow.Shared;
using UnityEngine;

namespace RhythmicFlow.ChartEditor
{
    // -----------------------------------------------------------------------
    // Serializable project file model (rproj.json)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Root of the .rproj.json file. Serialized/deserialized by JsonUtility.
    /// Spec §12A.
    /// </summary>
    [Serializable]
    public class RprojFile
    {
        // Schema version for future migration support.
        public int rprojVersion = 1;

        // Song metadata inputs (spec §12A / §13.3).
        public string title   = "";
        public string artist  = "";
        public string songId  = "";
        public int    lengthMs;

        // BPM display (auto-derived from tempo; may be user-overridden).
        public float bpmDisplayMin;
        public float bpmDisplayMax;

        // Absolute path to the imported OGG source on the authoring machine.
        // The file is copied into the .rpk as audio/song.ogg on export.
        // OGG-only in v0 (spec §13.6).
        public string audioSourcePath = "";

        // Absolute path to the imported jacket image source.
        public string jacketSourcePath = "";

        // Optional preview range (spec §13.3).
        public int previewStartMs;
        public int previewEndMs;

        // All difficulties in this project. Each holds a full ChartJsonV1 serialized inline.
        public List<DifficultyRecord> difficulties = new List<DifficultyRecord>();

        // Chart editor-only metadata (not exported to .rpk, spec §12A).
        public EditorMetadata editorMeta = new EditorMetadata();
    }

    /// <summary>
    /// One difficulty's chart data stored inside the project file.
    /// </summary>
    [Serializable]
    public class DifficultyRecord
    {
        /// <summary>Difficulty identifier: "easy", "normal", "hard", etc.</summary>
        public string difficultyId = "";

        /// <summary>
        /// The chart JSON for this difficulty, serialized inline as a string.
        /// Using a string lets us store it without a nested Serializable class forest.
        /// (JsonUtility does not support nested [Serializable] with generics cleanly for deep graphs.)
        /// </summary>
        public string chartJson = "";
    }

    /// <summary>
    /// Chart editor-only metadata that is NOT exported to .rpk.
    /// Spec §12A: "lane column ordering, last selected objects, etc."
    /// </summary>
    [Serializable]
    public class EditorMetadata
    {
        // Per-arena lane column ordering: each entry is a comma-separated list of laneIds.
        // Key = arenaId, Value = ordered list. JsonUtility needs a flat list approach.
        public List<ArenaColumnOrder> laneColumnOrders = new List<ArenaColumnOrder>();

        // Last selected note IDs (restored on project reload).
        public List<string> lastSelectedNoteIds = new List<string>();
    }

    [Serializable]
    public class ArenaColumnOrder
    {
        /// <summary>Arena identifier.</summary>
        public string arenaId = "";

        /// <summary>Lane IDs in display order for the note canvas (spec §3.2 note canvas).</summary>
        public List<string> orderedLaneIds = new List<string>();
    }

    // -----------------------------------------------------------------------
    // EditorProject — in-memory working model
    // -----------------------------------------------------------------------

    /// <summary>
    /// In-memory chart editor project. Wraps RprojFile with load/save and
    /// tracks dirty state.
    ///
    /// To edit chart data, use the UndoStack command pattern so all changes
    /// are undoable (spec §4.2 "Full undo/redo for all chart edits").
    /// </summary>
    public class EditorProject
    {
        // -------------------------------------------------------------------
        // State
        // -------------------------------------------------------------------

        /// <summary>The currently loaded project data.</summary>
        public RprojFile Data { get; private set; }

        /// <summary>
        /// Absolute path to the .rproj.json file on disk.
        /// Null when the project has not been saved yet (new unsaved project).
        /// </summary>
        public string FilePath { get; private set; }

        /// <summary>True when there are unsaved changes.</summary>
        public bool IsDirty { get; private set; }

        // -------------------------------------------------------------------
        // Factory: new project
        // -------------------------------------------------------------------

        /// <summary>
        /// Creates a new empty project (not yet saved to disk).
        /// </summary>
        public static EditorProject CreateNew()
        {
            return new EditorProject
            {
                Data     = new RprojFile(),
                FilePath = null,
                IsDirty  = true
            };
        }

        // -------------------------------------------------------------------
        // Load from file
        // -------------------------------------------------------------------

        /// <summary>
        /// Loads a .rproj.json file from disk.
        /// Returns true and sets <paramref name="project"/> on success.
        /// Returns false and sets <paramref name="error"/> on failure.
        /// </summary>
        public static bool TryLoad(
            string           path,
            out EditorProject project,
            out string        error)
        {
            project = null;
            error   = null;

            if (string.IsNullOrEmpty(path))
            {
                error = "Project path is null or empty.";
                return false;
            }

            if (!File.Exists(path))
            {
                error = $"Project file not found: {path}";
                return false;
            }

            string json;

            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                error = $"Failed to read project file '{path}': {ex.Message}";
                return false;
            }

            RprojFile data;

            try
            {
                data = JsonUtility.FromJson<RprojFile>(json);
            }
            catch (Exception ex)
            {
                error = $"Failed to parse project JSON in '{path}': {ex.Message}";
                return false;
            }

            if (data == null)
            {
                error = $"Project JSON deserialized to null in '{path}'.";
                return false;
            }

            project = new EditorProject
            {
                Data     = data,
                FilePath = path,
                IsDirty  = false
            };

            return true;
        }

        // -------------------------------------------------------------------
        // Save to file
        // -------------------------------------------------------------------

        /// <summary>
        /// Saves the project to <paramref name="path"/> (or to FilePath if path is null).
        /// Updates FilePath on success.
        /// Returns false with <paramref name="error"/> on failure.
        /// </summary>
        public bool TrySave(string path, out string error)
        {
            error = null;

            string savePath = string.IsNullOrEmpty(path) ? FilePath : path;

            if (string.IsNullOrEmpty(savePath))
            {
                error = "No save path provided and project has no existing file path.";
                return false;
            }

            string json;

            try
            {
                json = JsonUtility.ToJson(Data, prettyPrint: true);
            }
            catch (Exception ex)
            {
                error = $"Failed to serialize project: {ex.Message}";
                return false;
            }

            try
            {
                // Ensure directory exists.
                string dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(savePath, json);
            }
            catch (Exception ex)
            {
                error = $"Failed to write project file '{savePath}': {ex.Message}";
                return false;
            }

            FilePath = savePath;
            IsDirty  = false;
            return true;
        }

        // -------------------------------------------------------------------
        // Validation integration
        // -------------------------------------------------------------------

        /// <summary>
        /// Validates all difficulties in the project using ChartValidator.
        /// Returns a list of (difficultyId, ChartValidationResult) pairs.
        /// Call this before exporting to .rpk.
        /// </summary>
        public List<(string difficultyId, ChartValidationResult result)> ValidateAll()
        {
            var results = new List<(string, ChartValidationResult)>(Data.difficulties.Count);

            foreach (DifficultyRecord diff in Data.difficulties)
            {
                ChartValidationResult result;

                if (string.IsNullOrEmpty(diff.chartJson))
                {
                    var empty = new ChartValidationResult();
                    empty.AddError($"Difficulty '{diff.difficultyId}' has no chart data.");
                    results.Add((diff.difficultyId, empty));
                    continue;
                }

                if (!ChartJsonReader.TryReadFromText(diff.chartJson, out ChartJsonV1 chart,
                    out string parseError))
                {
                    var parseResult = new ChartValidationResult();
                    parseResult.AddError($"JSON parse error: {parseError}");
                    results.Add((diff.difficultyId, parseResult));
                    continue;
                }

                result = ChartValidator.Validate(chart);
                results.Add((diff.difficultyId, result));
            }

            return results;
        }

        // -------------------------------------------------------------------
        // Dirty flag management
        // -------------------------------------------------------------------

        /// <summary>
        /// Marks the project as modified. Call this from undo commands after
        /// mutating Data (so saves prompt correctly).
        /// </summary>
        public void MarkDirty() => IsDirty = true;

        // -------------------------------------------------------------------
        // Private constructor (use factory methods)
        // -------------------------------------------------------------------

        private EditorProject() { }
    }
}
