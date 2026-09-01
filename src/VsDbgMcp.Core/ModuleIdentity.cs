using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VsDbgMcp.Contracts;

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
        /// The image's own time stamp, or null when the field does not hold a time.
        ///
        /// Two things put something other than a time there. A module with no stamp
        /// reports the start of the file-time epoch, and a build made reproducible
        /// stamps a hash of the contents in its place, which lands wherever the hash
        /// falls. Only the values that could not be a build time are caught, so this
        /// removes the obvious nonsense and cannot promise every stamp it returns is
        /// really a time. Everything that compares one build against another goes
        /// through here, so a stamp that is not a time is never read as a date.
        /// </summary>
        public static DateTime? BuildTime(DateTime? utc)
        {
            if (utc == null) return null;

            // A stamp read straight from the image is already UTC and carries no kind.
            // One that has been through the wire can come back as a local time, and
            // relabelling that as UTC would shift it by the offset.
            var value = utc.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)
                : utc.Value.ToUniversalTime();

            if (value < Earliest || value > DateTime.UtcNow.AddDays(1)) return null;

            return value;
        }

        /// <summary>
        /// When a module was built: the stamp in the image the debuggee loaded where the
        /// header carries a usable one, and the file at the module's path otherwise.
        ///
        /// The stamp is preferred because it travels with the binary. The file time
        /// belongs to whatever sits at that path on this machine, which for a module
        /// deployed elsewhere is a different copy or nothing at all.
        /// </summary>
        public static DateTime? BinaryBuilt(ModuleInfo module) =>
            module == null ? null : BuildTime(module.ImageStamp) ?? SourceFreshness.LastWritten(module.Path);

        /// <summary>
        /// The files sitting where a module's symbols came from that could be the build
        /// this module was made from, closest guess first: the image's own name, then
        /// the symbol file's name with the image's extension, because a project can name
        /// its PDB differently from the binary it produces.
        ///
        /// Empty when there is nothing there to compare against: no symbol file, no name
        /// to look for, or symbols sitting in the directory the image was loaded from.
        /// Both names are looked for in one directory, so where that is the image's own
        /// directory the second name would be some other project's output sitting beside
        /// it rather than another copy of this module.
        /// </summary>
        public static List<string> BesideSymbols(string imagePath, string symbolPath)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(symbolPath)) return found;

            // Both come from the debug engine, so either can be something this machine
            // will not parse as a path at all.
            try
            {
                var folder = Path.GetDirectoryName(symbolPath);
                if (string.IsNullOrEmpty(folder)) return found;
                if (PathUtil.SamePath(folder, Path.GetDirectoryName(imagePath))) return found;

                var image = Path.GetFileName(imagePath);
                var afterPdb = Path.GetFileNameWithoutExtension(symbolPath) + Path.GetExtension(imagePath);

                if (!string.IsNullOrEmpty(image)) found.Add(Path.Combine(folder, image));
                if (!string.IsNullOrEmpty(afterPdb) &&
                    !string.Equals(afterPdb, image, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(Path.Combine(folder, afterPdb));
                }
            }
            catch (ArgumentException) { return found; }
            catch (PathTooLongException) { return found; }

            return found;
        }

        /// <summary>
        /// How much later the file beside the symbols has to be when the loaded side has
        /// nothing but the stamp in its image. That stamp is written when the binary is
        /// linked and a file's time when it is written, so one build leaves two times
        /// that sit however long the link took apart. Only a gap no single build could
        /// account for is worth reporting, which means a rebuild from an hour ago that
        /// was never deployed passes unmentioned. Saying nothing there costs a reader
        /// less than a claim that turns out to be one slow link.
        /// </summary>
        static readonly TimeSpan LinkGap = TimeSpan.FromHours(1);

        /// <summary>
        /// What to say when the debuggee loaded an older copy of a module than the build
        /// sitting where its symbols came from, and null when nothing here can say so.
        ///
        /// This is the failure a fresh build does not fix: an engine that loads plugins
        /// from a deployment directory keeps running the copy that is there, while the
        /// PDB the debugger matches comes from the build tree. Nothing announces it, and
        /// breakpoints simply do not bind.
        /// </summary>
        /// <param name="module">The module as the engine described it.</param>
        /// <param name="loadedWritten">
        /// When the file the module was loaded from was last written, or null when this
        /// machine has no such file to read.
        /// </param>
        public static string StaleDeployment(ModuleInfo module, DateTime? loadedWritten)
        {
            if (module == null) return null;

            // The failure this catches is a matched PDB sitting beside a build the
            // debuggee never loaded. Without symbols there is no PDB to have matched,
            // and the directory beside it is not known to be this module's build tree.
            if (!module.SymbolsLoaded) return null;

            string beside = null;
            DateTime? besideWritten = null;
            foreach (var candidate in BesideSymbols(module.Path, module.SymbolPath))
            {
                besideWritten = SourceFreshness.LastWritten(candidate);
                if (besideWritten == null) continue;

                beside = candidate;
                break;
            }
            if (beside == null) return null;

            var name = string.IsNullOrEmpty(module.Name) ? Path.GetFileName(module.Path) : module.Name;

            // Where the file found is not the one the image is named after, say where
            // its name came from. Otherwise it reads as an unrelated binary.
            var whichFile = string.Equals(Path.GetFileName(beside), Path.GetFileName(module.Path),
                            StringComparison.OrdinalIgnoreCase)
                ? beside
                : "the build named after that PDB, " + beside + ",";

            // Two file times can be compared as they are. Where the loaded copy is on
            // another machine there is no file here to read it from, and the stamp in
            // the image is the only time the loaded side has.
            if (loadedWritten != null)
            {
                if (SourceFreshness.WrittenAfter(besideWritten, loadedWritten) != true) return null;

                return name + " may be running an older build than the one beside its PDB: the file at its " +
                       "load path (" + module.Path + ") was written " + SourceFreshness.Show(loadedWritten) +
                       ", and " + whichFile + " was written " + SourceFreshness.Show(besideWritten) +
                       ". Two write times are not proof the two files differ, but a deployed copy older " +
                       "than the build tree is the usual reason breakpoints in a plugin do not bind. " +
                       "Redeploy and relaunch before trusting them";
            }

            // Whatever filled the module in, a stamp that could not be a time must not
            // be compared against one.
            var stamp = BuildTime(module.ImageStamp);
            if (SourceFreshness.Later(besideWritten, stamp, LinkGap) != true) return null;

            return name + " may be running an older build than the one beside its PDB: the image it loaded " +
                   "is stamped " + SourceFreshness.Show(stamp) + ", and " + whichFile + " was written " +
                   SourceFreshness.Show(besideWritten) + ". There is no readable file at its load path on " +
                   "this machine to compare against, and a link stamp and a write time are not the same " +
                   "clock, so this is evidence rather than proof";
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
