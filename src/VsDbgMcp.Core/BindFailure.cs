using System.Collections.Generic;
using System.IO;
using System.Linq;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    /// <summary>
    /// What to tell the caller when a line breakpoint did not bind.
    ///
    /// The causes come in the order they are certain of themselves. A module loaded
    /// without symbols explains the failure on its own. A module the debuggee loaded
    /// an older copy of than the build beside its PDB comes next, because it is why
    /// the binary is old rather than a consequence of it. A source file written after
    /// that module was built is only offered once both are ruled out, because two
    /// file times are strong evidence rather than proof. When the module the file
    /// belongs to is not known at all, the general answer is the honest one.
    /// </summary>
    public static class BindFailure
    {
        /// <summary>What is left to say when nothing more specific is known.</summary>
        public const string NoCodeHere =
            "no code loaded at this location. Check 'modules' for the owning module and " +
            "whether its symbols loaded, and the Debug pane via 'output' for PDB messages";

        /// <summary>Why this breakpoint did not bind, as far as anything here can tell.</summary>
        /// <param name="sourceFile">The file the breakpoint is in.</param>
        /// <param name="owner">The loaded module the file belongs to, or null when it is not known.</param>
        /// <param name="sourceIsNewer">Whether the file has been written since that module was built.</param>
        /// <param name="sourceWritten">When the file was last written, ready to show.</param>
        public static string Explain(string sourceFile, ModuleInfo owner, bool? sourceIsNewer, string sourceWritten)
        {
            if (owner == null) return NoCodeHere;

            if (!owner.SymbolsLoaded)
            {
                return "the module this file belongs to, " + owner.Name + ", is loaded without symbols" +
                       (string.IsNullOrEmpty(owner.SymbolStatus) ? "" : " (" + owner.SymbolStatus + ")") +
                       ", so there are no line numbers to bind to. 'modules' shows where the debugger " +
                       "looked for its PDB";
            }

            // Said before the source times, because rebuilding is the advice those give
            // and rebuilding is exactly what does not fix this.
            if (!string.IsNullOrEmpty(owner.StaleDeployment)) return owner.StaleDeployment;

            if (sourceIsNewer == true)
            {
                return Path.GetFileName(sourceFile) + " has been modified since " + owner.Name +
                       " was built" + Times(sourceWritten, Built(owner)) + ", so its line numbers no longer " +
                       "match the binary. Rebuild the module, or set the breakpoint by function name";
            }

            return NoCodeHere;
        }

        /// <summary>
        /// Loaded modules running an older copy than the build beside their symbols.
        ///
        /// This is said where a breakpoint has not bound and nothing here could name the
        /// module its file belongs to - a plugin loaded by a host whose solution does
        /// not hold the plugin's project, which is the shape this failure comes in. It
        /// does not claim one of these owns the file, and it is also said the moment a
        /// breakpoint is created, before binding has settled, so it names something to
        /// rule out rather than a reason.
        /// </summary>
        public static string StaleModules(IEnumerable<ModuleInfo> loaded)
        {
            if (loaded == null) return null;

            var stale = loaded.Where(m => m != null && !string.IsNullOrEmpty(m.StaleDeployment)).ToList();
            if (stale.Count == 0) return null;

            var named = string.Join(", ", stale.Take(3).Select(m => m.Name ?? "?").ToArray());
            if (stale.Count > 3) named += " and " + (stale.Count - 3) + " more";

            return (stale.Count == 1
                       ? "One loaded module is running an older copy than the build beside its PDB: "
                       : stale.Count + " loaded modules are running an older copy than the build beside " +
                         "their PDB: ") + named +
                   ". Breakpoints in a module in that state do not bind, or bind to a line the " +
                   "source has since moved. Which module this breakpoint belongs to is not known " +
                   "here, so this is something to rule out rather than the reason. 'modules' shows " +
                   "each one's two paths and times.";
        }

        /// <summary>
        /// When the module was built, preferring the stamp the image carries because it
        /// is the one that survives the binary being deployed somewhere else.
        /// </summary>
        static string Built(ModuleInfo owner) =>
            string.IsNullOrEmpty(owner.ImageBuilt) ? owner.Built : owner.ImageBuilt;

        static string Times(string sourceWritten, string built) =>
            string.IsNullOrEmpty(sourceWritten) || string.IsNullOrEmpty(built)
                ? ""
                : " (file " + sourceWritten + ", binary " + built + ")";
    }
}
