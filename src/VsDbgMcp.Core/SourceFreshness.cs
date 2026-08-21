using System;
using System.Globalization;
using System.IO;

namespace VsDbgMcp
{
    /// <summary>
    /// Whether a source file has been written since the binary built from it.
    ///
    /// The exact answer is in the PDB, which records a checksum of every file it was
    /// built from, and reading it means driving the symbol reader's COM API. The two
    /// file times answer the question this is really asked - was the file edited after
    /// the last build - and nothing here claims more than they say.
    /// </summary>
    public static class SourceFreshness
    {
        /// <summary>
        /// How much newer a source has to be before it counts. Build outputs and
        /// sources can live on file systems whose clocks and timestamp resolution do
        /// not agree to the second, and a build is not wrong by two seconds.
        /// </summary>
        static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

        /// <summary>
        /// When a file was last written, in UTC, or null when there is no readable
        /// file at that path. Null has to stay unknown: a missing binary is not
        /// evidence that a source is out of date.
        /// </summary>
        public static DateTime? LastWritten(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            // The path comes from the debug engine or from a breakpoint, so it can be
            // anything, including something this machine will not resolve at all.
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? file.LastWriteTimeUtc : (DateTime?)null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        /// <summary>
        /// True when the source was written after the binary was built, false when it
        /// was not, null when either time is unknown.
        /// </summary>
        public static bool? SourceIsNewer(DateTime? sourceWritten, DateTime? binaryBuilt)
        {
            if (sourceWritten == null || binaryBuilt == null) return null;
            return sourceWritten.Value - binaryBuilt.Value > Slack;
        }

        /// <summary>
        /// A time to show a reader, in the time zone they are sitting in. Empty when
        /// it is unknown, so a caller can print it without testing it first.
        /// </summary>
        public static string Show(DateTime? utc) => utc == null
            ? ""
            : utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
