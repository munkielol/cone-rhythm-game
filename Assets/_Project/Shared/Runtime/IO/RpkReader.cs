// RpkReader.cs
// Read-only accessor for .rpk song-pack files.
//
// .rpk is a standard ZIP container (compression only, no encryption — spec §2.1).
// This class uses System.IO.Compression (included in Unity's .NET Standard 2.1 profile)
// so no external packages are required.
//
// Typical .rpk structure (spec §2.2):
//   songinfo.json
//   audio/song.ogg
//   jacket/jacket_<size>.png
//   charts/<difficultyId>.json
//
// All methods are static and read-only; they never modify the archive.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RhythmicFlow.Shared
{
    /// <summary>
    /// Provides read-only access to the contents of a .rpk song-pack file.
    /// </summary>
    public static class RpkReader
    {
        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Reads the text content of a named entry inside an .rpk archive.
        /// Returns true and sets <paramref name="text"/> on success.
        /// Returns false and sets <paramref name="error"/> with a human-readable
        /// message if the pack cannot be opened or the entry is not found.
        /// </summary>
        /// <param name="rpkPath">Absolute path to the .rpk file.</param>
        /// <param name="entryName">
        /// The full in-archive path of the entry (forward-slash separated,
        /// e.g. "songinfo.json" or "charts/normal.json").
        /// </param>
        /// <param name="text">The UTF-8 text content if found.</param>
        /// <param name="error">Human-readable error message if failed.</param>
        public static bool TryReadTextEntry(
            string rpkPath,
            string entryName,
            out string text,
            out string error)
        {
            text  = null;
            error = null;

            if (!ValidatePath(rpkPath, out error)) { return false; }

            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(rpkPath))
                {
                    ZipArchiveEntry entry = FindEntry(archive, entryName);

                    if (entry == null)
                    {
                        error = $"Entry '{entryName}' not found in pack '{rpkPath}'. " +
                                $"Available entries: {ListEntryNames(archive)}";
                        return false;
                    }

                    text = ReadEntryAsText(entry);
                    return true;
                }
            }
            catch (InvalidDataException ex)
            {
                error = $"Pack '{rpkPath}' is not a valid ZIP/RPK file: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Failed to read entry '{entryName}' from '{rpkPath}': {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Reads the raw bytes of a named entry inside an .rpk archive.
        /// Returns true and sets <paramref name="bytes"/> on success.
        /// Returns false and sets <paramref name="error"/> on failure.
        /// </summary>
        public static bool TryReadBinaryEntry(
            string rpkPath,
            string entryName,
            out byte[] bytes,
            out string error)
        {
            bytes = null;
            error = null;

            if (!ValidatePath(rpkPath, out error)) { return false; }

            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(rpkPath))
                {
                    ZipArchiveEntry entry = FindEntry(archive, entryName);

                    if (entry == null)
                    {
                        error = $"Entry '{entryName}' not found in pack '{rpkPath}'. " +
                                $"Available entries: {ListEntryNames(archive)}";
                        return false;
                    }

                    bytes = ReadEntryAsBytes(entry);
                    return true;
                }
            }
            catch (InvalidDataException ex)
            {
                error = $"Pack '{rpkPath}' is not a valid ZIP/RPK file: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Failed to read binary entry '{entryName}' from '{rpkPath}': {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Returns a list of all entry names (full in-archive paths) in the .rpk.
        /// Returns true on success; false with an actionable <paramref name="error"/> on failure.
        /// </summary>
        public static bool TryEnumerateEntries(
            string rpkPath,
            out List<string> entryNames,
            out string error)
        {
            entryNames = null;
            error      = null;

            if (!ValidatePath(rpkPath, out error)) { return false; }

            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(rpkPath))
                {
                    entryNames = new List<string>(archive.Entries.Count);

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        // Skip directory entries (entries whose names end with '/').
                        if (!entry.FullName.EndsWith("/", StringComparison.Ordinal))
                        {
                            entryNames.Add(entry.FullName);
                        }
                    }

                    return true;
                }
            }
            catch (InvalidDataException ex)
            {
                error = $"Pack '{rpkPath}' is not a valid ZIP/RPK file: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Failed to enumerate entries in '{rpkPath}': {ex.Message}";
                return false;
            }
        }

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        // Validates that rpkPath is non-empty and the file exists on disk.
        private static bool ValidatePath(string rpkPath, out string error)
        {
            if (string.IsNullOrEmpty(rpkPath))
            {
                error = "rpkPath is null or empty.";
                return false;
            }

            if (!File.Exists(rpkPath))
            {
                error = $"Pack file not found: {rpkPath}";
                return false;
            }

            error = null;
            return true;
        }

        // Finds an entry by full name, using case-insensitive comparison to be
        // robust across platforms (ZIP entry names are not guaranteed to match case).
        private static ZipArchiveEntry FindEntry(ZipArchive archive, string entryName)
        {
            // Try exact match first (fastest and most correct).
            ZipArchiveEntry exact = archive.GetEntry(entryName);
            if (exact != null) { return exact; }

            // Fall back to case-insensitive scan for cross-platform robustness.
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.Equals(entry.FullName, entryName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        // Reads the full text content of an entry as UTF-8.
        private static string ReadEntryAsText(ZipArchiveEntry entry)
        {
            using (Stream stream = entry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        // Reads the full binary content of an entry into a byte array.
        private static byte[] ReadEntryAsBytes(ZipArchiveEntry entry)
        {
            using (Stream stream = entry.Open())
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // Builds a compact comma-separated list of entry names for error messages.
        private static string ListEntryNames(ZipArchive archive)
        {
            var sb = new StringBuilder();
            int count = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (count > 0) { sb.Append(", "); }
                sb.Append(entry.FullName);
                count++;

                // Truncate very long lists to keep error messages readable.
                if (count >= 20)
                {
                    sb.Append($", ... ({archive.Entries.Count - count} more)");
                    break;
                }
            }

            return sb.Length > 0 ? sb.ToString() : "(empty archive)";
        }
    }
}
