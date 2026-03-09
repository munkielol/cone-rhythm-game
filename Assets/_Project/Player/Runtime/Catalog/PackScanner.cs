// PackScanner.cs
// Scans Application.persistentDataPath/Packs/ for .rpk files, loads songinfo.json
// from each, validates the pack, and populates a PackCatalog.
//
// Discovery rules (spec §2.6):
//   – Scan directory: Application.persistentDataPath + "/Packs/"
//   – Any *.rpk file in that directory is a candidate.
//   – Invalid packs are EXCLUDED from the catalog and the reason is logged.
//   – Log includes: pack filename, failing path, validation error summary.
//
// Required pack contents (spec §2.2):
//   songinfo.json
//   audio/song.ogg   (existence only; audio loading happens at gameplay time)
//   charts/<difficultyId>.json  (each chart validated via ChartValidator)
//
// Audio format rule (spec §2.7): only audio/song.ogg is supported in v0.
//
// This class is not a MonoBehaviour. Call Scan() from whatever controls game flow.

using System;
using System.Collections.Generic;
using System.IO;
using RhythmicFlow.Shared;
using UnityEngine;

namespace RhythmicFlow.Player
{
    public static class PackScanner
    {
        // Required audio path inside every .rpk (spec §2.7).
        private const string RequiredAudioPath = "audio/song.ogg";

        // Required songinfo entry (spec §2.2).
        private const string SongInfoPath = "songinfo.json";

        // -------------------------------------------------------------------
        // Public entry point
        // -------------------------------------------------------------------

        /// <summary>
        /// Scans Application.persistentDataPath/Packs/ for .rpk files.
        /// Clears and repopulates <paramref name="catalog"/> with all valid packs.
        /// Invalid packs are logged and excluded (spec §2.6).
        /// To override the directory (e.g. for an Editor DevPacks folder) use
        /// <see cref="Scan(PackCatalog,string)"/>.
        /// </summary>
        public static void Scan(PackCatalog catalog)
        {
            Scan(catalog, Path.Combine(Application.persistentDataPath, "Packs"));
        }

        /// <summary>
        /// Scans <paramref name="packsDirectory"/> for .rpk files.
        /// Clears and repopulates <paramref name="catalog"/> with all valid packs.
        /// The directory is created if it does not exist (no files are written).
        /// Invalid packs are logged and excluded (spec §2.6).
        /// </summary>
        public static void Scan(PackCatalog catalog, string packsDirectory)
        {
            catalog.Clear();

            string packsDir = packsDirectory;

            if (!Directory.Exists(packsDir))
            {
                Directory.CreateDirectory(packsDir);
                Debug.Log($"[PackScanner] Created packs directory: {packsDir}");
                return;
            }

            string[] rpkPaths = Directory.GetFiles(packsDir, "*.rpk", SearchOption.TopDirectoryOnly);
            Debug.Log($"[PackScanner] Found {rpkPaths.Length} .rpk candidate(s) in {packsDir}");

            foreach (string rpkPath in rpkPaths)
            {
                string packFile = Path.GetFileName(rpkPath);
                PackEntry entry;

                if (TryLoadPack(rpkPath, packFile, out entry, out string loadError))
                {
                    catalog.Add(entry);
                    Debug.Log($"[PackScanner] Loaded: {packFile} ({entry.Difficulties.Count} difficulty/-ies)");
                }
                else
                {
                    // Spec §2.6: log filename + failing path + error summary; exclude from list.
                    Debug.LogWarning($"[PackScanner] EXCLUDED {packFile}: {loadError}");
                }
            }

            Debug.Log($"[PackScanner] Catalog contains {catalog.Count} valid pack(s).");
        }

        // -------------------------------------------------------------------
        // Private: load one pack
        // -------------------------------------------------------------------

        // Tries to load one .rpk into a PackEntry.
        // Returns false and sets error if anything is missing or invalid.
        private static bool TryLoadPack(
            string rpkPath,
            string packFile,
            out PackEntry entry,
            out string error)
        {
            entry = null;
            error = null;

            // 1) Read songinfo.json.
            if (!RpkReader.TryReadTextEntry(rpkPath, SongInfoPath, out string songInfoJson, out string readError))
            {
                error = $"Missing {SongInfoPath}: {readError}";
                return false;
            }

            // 2) Parse songinfo.json.
            SongInfo songInfo;
            if (!TryParseSongInfo(songInfoJson, packFile, out songInfo, out error))
            {
                return false;
            }

            // 3) Verify audio/song.ogg exists (spec §2.7: ogg-only).
            if (!RpkReader.TryEnumerateEntries(rpkPath, out List<string> entryNames, out string enumError))
            {
                error = $"Could not enumerate pack entries: {enumError}";
                return false;
            }

            if (!EntryExists(entryNames, RequiredAudioPath))
            {
                error = $"Required audio entry '{RequiredAudioPath}' not found " +
                        $"(spec §2.7: ogg-only audio in v0).";
                return false;
            }

            // 4) Validate each chart listed in songinfo.
            var difficulties = new List<DifficultyEntry>();

            if (songInfo.charts == null || songInfo.charts.Length == 0)
            {
                error = "songinfo.json contains no charts[] entries.";
                return false;
            }

            foreach (SongInfoChart chart in songInfo.charts)
            {
                if (chart == null || string.IsNullOrEmpty(chart.difficultyId))
                {
                    error = "songinfo.json contains a charts[] entry with a null/empty difficultyId.";
                    return false;
                }

                string chartPath = chart.path;

                if (!RpkReader.TryReadTextEntry(rpkPath, chartPath, out string chartJson, out string chartReadError))
                {
                    error = $"Cannot read chart '{chartPath}': {chartReadError}";
                    return false;
                }

                if (!ChartJsonReader.TryReadFromText(chartJson, out ChartJsonV1 chartData, out string parseError))
                {
                    error = $"Chart '{chartPath}' JSON parse failed: {parseError}";
                    return false;
                }

                ChartValidationResult validationResult = ChartValidator.Validate(chartData);

                if (!validationResult.IsValid)
                {
                    error = $"Chart '{chartPath}' failed validation:\n{validationResult}";
                    return false;
                }

                difficulties.Add(new DifficultyEntry
                {
                    DifficultyId = chart.difficultyId,
                    ChartPath    = chartPath
                });
            }

            // 5) Load best-fit jacket image bytes (optional; failure is non-fatal).
            byte[] jacketBytes = LoadBestJacket(rpkPath, songInfo);

            // 6) Build the catalog entry.
            entry = new PackEntry
            {
                RpkPath       = rpkPath,
                SongId        = songInfo.songId  ?? "",
                Title         = songInfo.title   ?? "",
                Artist        = songInfo.artist  ?? "",
                LengthMs      = songInfo.lengthMs,
                BpmMin        = songInfo.bpmDisplay?.min ?? 0f,
                BpmMax        = songInfo.bpmDisplay?.max ?? 0f,
                JacketBytes   = jacketBytes,
                Difficulties  = difficulties,
                PreviewStartMs = songInfo.preview?.startTimeMs ?? 0,
                PreviewEndMs   = songInfo.preview?.endTimeMs   ?? 0
            };

            return true;
        }

        // Attempts to load the best-fit jacket image (256 < 512 < 1024 preference: largest available).
        // Returns null silently if no jacket is found — the UI should handle missing jackets.
        private static byte[] LoadBestJacket(string rpkPath, SongInfo songInfo)
        {
            if (songInfo.jacket?.images == null) { return null; }

            // Prefer the largest available size for highest quality.
            int[] preferredSizes = { 1024, 512, 256 };

            foreach (int size in preferredSizes)
            {
                foreach (SongInfoJacketImage img in songInfo.jacket.images)
                {
                    if (img == null) { continue; }

                    if (img.size == size && !string.IsNullOrEmpty(img.path))
                    {
                        if (RpkReader.TryReadBinaryEntry(rpkPath, img.path, out byte[] bytes, out _))
                        {
                            return bytes;
                        }
                    }
                }
            }

            return null;
        }

        // Case-insensitive existence check against a list of entry names.
        private static bool EntryExists(List<string> entryNames, string target)
        {
            foreach (string name in entryNames)
            {
                if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // -------------------------------------------------------------------
        // SongInfo JSON model (internal, mirrors songinfo.json spec §2.3)
        // -------------------------------------------------------------------

        // Parses the songinfo.json string into a SongInfo object.
        // Returns false with error if required fields are missing.
        private static bool TryParseSongInfo(
            string json,
            string packFile,
            out SongInfo songInfo,
            out string error)
        {
            songInfo = null;
            error    = null;

            try
            {
                songInfo = JsonUtility.FromJson<SongInfo>(json);
            }
            catch (Exception ex)
            {
                error = $"Failed to parse songinfo.json: {ex.Message}";
                return false;
            }

            if (songInfo == null)
            {
                error = "songinfo.json parsed to null (malformed JSON).";
                return false;
            }

            if (string.IsNullOrEmpty(songInfo.songId))
            {
                error = "songinfo.json: songId is missing or empty.";
                return false;
            }

            if (string.IsNullOrEmpty(songInfo.title))
            {
                error = "songinfo.json: title is missing or empty.";
                return false;
            }

            return true;
        }

        // -------------------------------------------------------------------
        // songinfo.json data model (JsonUtility-compatible, no Dictionary)
        // Spec §2.3
        // -------------------------------------------------------------------

        [Serializable]
        private class SongInfo
        {
            public int             packageVersion;
            public string          songId;
            public string          title;
            public string          artist;
            public int             lengthMs;
            public BpmDisplay      bpmDisplay;
            public SongInfoAudio   audio;
            public SongInfoJacket  jacket;
            public SongInfoChart[] charts;
            public SongInfoPreview preview;
        }

        [Serializable]
        private class BpmDisplay
        {
            public float min;
            public float max;
        }

        [Serializable]
        private class SongInfoAudio
        {
            public string path;
        }

        [Serializable]
        private class SongInfoJacket
        {
            public SongInfoJacketImage[] images;
        }

        [Serializable]
        private class SongInfoJacketImage
        {
            public int    size;  // 256, 512, or 1024
            public string path;
        }

        [Serializable]
        private class SongInfoChart
        {
            public string difficultyId;
            public string path;
        }

        [Serializable]
        private class SongInfoPreview
        {
            public int startTimeMs;
            public int endTimeMs;
        }
    }
}
