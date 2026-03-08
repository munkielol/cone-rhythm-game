// RpkExporter.cs
// Exports the current EditorProject to a .rpk (ZIP) file.
//
// Export is BLOCKED if any difficulty has validation errors (spec §12.1 / §13).
// Export produces (spec §13.2):
//   songinfo.json           — auto-generated from project metadata (spec §13.3)
//   audio/song.ogg          — copied from audioSourcePath (OGG-only, spec §13.6)
//   jacket/jacket_<N>.png   — any subset of 256/512/1024 copied from jacketSourcePath
//   charts/<diffId>.json    — one chart JSON per difficulty
//
// No UnityEditor APIs (spec: ChartEditorApp must not use UnityEditor namespace).
// Uses System.IO.Compression (no external packages).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using RhythmicFlow.Shared;
using UnityEngine;

namespace RhythmicFlow.ChartEditor
{
    // -----------------------------------------------------------------------
    // Export result
    // -----------------------------------------------------------------------

    public class ExportResult
    {
        /// <summary>True when the export completed without errors.</summary>
        public bool Success { get; set; }

        /// <summary>Absolute path to the output .rpk file (set on success).</summary>
        public string OutputPath { get; set; }

        /// <summary>Validation errors that blocked the export (one entry per difficulty).</summary>
        public List<string> ValidationErrors { get; } = new List<string>();

        /// <summary>Non-blocking warnings (filled even on success).</summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Human-readable summary of the export result.</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();

            if (Success)
            {
                sb.AppendLine($"Export SUCCESS → {OutputPath}");
            }
            else
            {
                sb.AppendLine($"Export FAILED ({ValidationErrors.Count} error(s))");
            }

            foreach (string e in ValidationErrors) { sb.AppendLine($"  [ERROR] {e}"); }
            foreach (string w in Warnings)         { sb.AppendLine($"  [WARN]  {w}"); }

            return sb.ToString();
        }
    }

    // -----------------------------------------------------------------------
    // RpkExporter
    // -----------------------------------------------------------------------

    public static class RpkExporter
    {
        // Required audio filename inside .rpk (spec §2.2 / §2.7).
        private const string AudioEntryPath = "audio/song.ogg";

        // -------------------------------------------------------------------
        // Public entry point
        // -------------------------------------------------------------------

        /// <summary>
        /// Exports the project to a .rpk file at <paramref name="outputPath"/>.
        ///
        /// Export is blocked if any difficulty fails ChartValidator (spec §12.1).
        /// Audio must be OGG (spec §13.6).
        ///
        /// Returns an ExportResult with Success=true on clean export.
        /// Returns Success=false with ValidationErrors if blocked.
        /// </summary>
        public static ExportResult Export(EditorProject project, string outputPath)
        {
            var result = new ExportResult();

            if (project == null)
            {
                result.ValidationErrors.Add("project is null.");
                return result;
            }

            if (string.IsNullOrEmpty(outputPath))
            {
                result.ValidationErrors.Add("outputPath is null or empty.");
                return result;
            }

            // ------------------------------------------------------------------
            // Step 1: Validate all difficulties. Block export on any error.
            // ------------------------------------------------------------------

            var validationResults = project.ValidateAll();
            bool hasAnyError = false;

            foreach (var (diffId, vr) in validationResults)
            {
                foreach (string w in vr.Warnings)
                {
                    result.Warnings.Add($"[{diffId}] {w}");
                }

                if (!vr.IsValid)
                {
                    hasAnyError = true;

                    foreach (string e in vr.Errors)
                    {
                        result.ValidationErrors.Add($"[{diffId}] {e}");
                    }
                }
            }

            if (hasAnyError)
            {
                result.Success = false;
                return result;
            }

            // ------------------------------------------------------------------
            // Step 2: Validate audio source (OGG-only, spec §13.6).
            // ------------------------------------------------------------------

            string audioPath = project.Data.audioSourcePath;

            if (string.IsNullOrEmpty(audioPath))
            {
                result.ValidationErrors.Add(
                    "audioSourcePath is empty. An OGG audio file must be imported before export.");
                result.Success = false;
                return result;
            }

            if (!audioPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                result.ValidationErrors.Add(
                    $"Audio file '{audioPath}' is not an OGG file. " +
                    $"Only .ogg audio is supported in v0 (spec §13.6).");
                result.Success = false;
                return result;
            }

            if (!File.Exists(audioPath))
            {
                result.ValidationErrors.Add($"Audio file not found: {audioPath}");
                result.Success = false;
                return result;
            }

            // ------------------------------------------------------------------
            // Step 3: Generate songinfo.json content (spec §13.3).
            // ------------------------------------------------------------------

            string songInfoJson = BuildSongInfoJson(project);

            // ------------------------------------------------------------------
            // Step 4: Write the .rpk zip file.
            // ------------------------------------------------------------------

            try
            {
                string outputDir = Path.GetDirectoryName(outputPath);

                if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                // Delete existing file if present (overwrite).
                if (File.Exists(outputPath)) { File.Delete(outputPath); }

                using (ZipArchive archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
                {
                    // songinfo.json
                    WriteTextEntry(archive, "songinfo.json", songInfoJson);

                    // audio/song.ogg
                    WriteBinaryEntry(archive, AudioEntryPath, File.ReadAllBytes(audioPath));

                    // jacket images (copy source as 256/512/1024 depending on user settings).
                    // TODO: Jacket auto-resize (spec §13.4) is not yet implemented.
                    //       For v0, copy source as jacket_256.png if jacket source exists.
                    string jacketSource = project.Data.jacketSourcePath;

                    if (!string.IsNullOrEmpty(jacketSource) && File.Exists(jacketSource))
                    {
                        string ext = Path.GetExtension(jacketSource).ToLowerInvariant();

                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                        {
                            WriteBinaryEntry(archive, "jacket/jacket_256.png",
                                File.ReadAllBytes(jacketSource));
                        }
                        else
                        {
                            result.Warnings.Add(
                                $"Jacket source '{jacketSource}' has unexpected extension '{ext}'. " +
                                $"Expected .png or .jpg. Jacket skipped in export.");
                        }
                    }
                    else
                    {
                        result.Warnings.Add("No jacket source set. Jacket omitted from .rpk.");
                    }

                    // charts/<difficultyId>.json
                    foreach (DifficultyRecord diff in project.Data.difficulties)
                    {
                        string chartEntry = $"charts/{diff.difficultyId}.json";
                        WriteTextEntry(archive, chartEntry, diff.chartJson);
                    }
                }
            }
            catch (Exception ex)
            {
                result.ValidationErrors.Add($"Failed to write .rpk file: {ex.Message}");
                result.Success = false;
                return result;
            }

            result.Success    = true;
            result.OutputPath = outputPath;

            Debug.Log($"[RpkExporter] Export complete: {outputPath}");

            return result;
        }

        // -------------------------------------------------------------------
        // songinfo.json builder (spec §13.3)
        // -------------------------------------------------------------------

        private static string BuildSongInfoJson(EditorProject project)
        {
            RprojFile data = project.Data;

            // Build the charts[] list for songinfo.
            var charts = new List<SongInfoChartEntry>(data.difficulties.Count);

            foreach (DifficultyRecord diff in data.difficulties)
            {
                charts.Add(new SongInfoChartEntry
                {
                    difficultyId = diff.difficultyId,
                    path         = $"charts/{diff.difficultyId}.json"
                });
            }

            var songInfo = new SongInfoOut
            {
                packageVersion = 1,
                songId         = data.songId,
                title          = data.title,
                artist         = data.artist,
                lengthMs       = data.lengthMs,
                bpmDisplay     = new BpmDisplayOut { min = data.bpmDisplayMin, max = data.bpmDisplayMax },
                audio          = new AudioOut { path = AudioEntryPath },
                jacket         = new JacketOut
                {
                    images = new[] { new JacketImageOut { size = 256, path = "jacket/jacket_256.png" } }
                },
                charts   = charts.ToArray(),
                preview  = new PreviewOut
                {
                    startTimeMs = data.previewStartMs,
                    endTimeMs   = data.previewEndMs
                }
            };

            return JsonUtility.ToJson(songInfo, prettyPrint: true);
        }

        // -------------------------------------------------------------------
        // ZIP helpers
        // -------------------------------------------------------------------

        private static void WriteTextEntry(ZipArchive archive, string entryName, string text)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);

            using (Stream stream = entry.Open())
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                writer.Write(text);
            }
        }

        private static void WriteBinaryEntry(ZipArchive archive, string entryName, byte[] bytes)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Optimal);

            using (Stream stream = entry.Open())
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        // -------------------------------------------------------------------
        // songinfo.json output model (JsonUtility-compatible, spec §13.3)
        // -------------------------------------------------------------------

        [Serializable]
        private class SongInfoOut
        {
            public int              packageVersion;
            public string           songId;
            public string           title;
            public string           artist;
            public int              lengthMs;
            public BpmDisplayOut    bpmDisplay;
            public AudioOut         audio;
            public JacketOut        jacket;
            public SongInfoChartEntry[] charts;
            public PreviewOut       preview;
        }

        [Serializable]
        private class BpmDisplayOut  { public float min; public float max; }

        [Serializable]
        private class AudioOut       { public string path; }

        [Serializable]
        private class JacketOut      { public JacketImageOut[] images; }

        [Serializable]
        private class JacketImageOut { public int size; public string path; }

        [Serializable]
        private class SongInfoChartEntry { public string difficultyId; public string path; }

        [Serializable]
        private class PreviewOut     { public int startTimeMs; public int endTimeMs; }
    }
}
