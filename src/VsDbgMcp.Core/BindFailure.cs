using System.IO;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    /// <summary>
    /// What to tell the caller when a line breakpoint did not bind.
    ///
    /// The causes come in the order they are certain of themselves. A module loaded
    /// without symbols explains the failure on its own. A source file written after
    /// that module was built is only offered once symbols are ruled out, because two
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

            if (sourceIsNewer == true)
            {
                return Path.GetFileName(sourceFile) + " has been modified since " + owner.Name +
                       " was built" + Times(sourceWritten, owner.Built) + ", so its line numbers no longer " +
                       "match the binary. Rebuild the module, or set the breakpoint by function name";
            }

            return NoCodeHere;
        }

        static string Times(string sourceWritten, string built) =>
            string.IsNullOrEmpty(sourceWritten) || string.IsNullOrEmpty(built)
                ? ""
                : " (file " + sourceWritten + ", binary " + built + ")";
    }
}
