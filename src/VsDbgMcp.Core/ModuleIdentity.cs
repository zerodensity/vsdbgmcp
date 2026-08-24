using System;
using System.Globalization;

namespace VsDbgMcp
{
    /// <summary>
    /// Which build of a binary the debuggee is running, told from the image itself
    /// rather than from a file on this machine.
    ///
    /// A module deployed to another machine has no file here to look at, so the only
    /// thing that can answer "is that the one I just built" is what the loaded image
    /// carries in its own header.
    /// </summary>
    public static class ModuleIdentity
    {
        /// <summary>
        /// The oldest time the image header can plausibly carry. Anything earlier is
        /// the field being empty rather than a binary from before Windows.
        /// </summary>
        static readonly DateTime Earliest = new DateTime(1995, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The image's own time stamp, ready to show, or empty when the field does not
        /// hold a time.
        ///
        /// Two things put something other than a time there. A module with no stamp
        /// reports the start of the file-time epoch, and a build made reproducible
        /// stamps a hash of the contents in its place, which lands wherever the hash
        /// falls. Only the values that could not be a build time are caught, so this
        /// removes the obvious nonsense and cannot promise every stamp it shows is
        /// really a time.
        /// </summary>
        public static string ImageTime(DateTime? utc)
        {
            if (utc == null) return "";

            var value = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
            if (value < Earliest || value > DateTime.UtcNow.AddDays(1)) return "";

            return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A module's size in both the forms it gets read in: rounded for a person
        /// skimming a list, and exact in hex for comparing one build against another.
        /// </summary>
        public static string Size(long bytes)
        {
            if (bytes <= 0) return "";

            var rounded = bytes >= 1024 * 1024
                ? (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
                : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";

            return rounded + " (0x" + bytes.ToString("x", CultureInfo.InvariantCulture) + ")";
        }
    }
}
