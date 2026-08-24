using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Loading a module's symbols on demand, and the account of where the engine
    /// looked when they will not load.
    ///
    /// Both live behind the Modules window, which only the person at the keyboard can
    /// reach: Load Symbols, and Symbol Load Information. Without them a session with
    /// no symbols has to be fixed by someone else, in a dialog, while the agent waits.
    /// </summary>
    static class SymbolLoad
    {
        public static SymbolResult For(IDebugProgram2 program, string query, bool load)
        {
            var result = new SymbolResult { Query = query };

            if (string.IsNullOrWhiteSpace(query))
            {
                result.Message = "Name a module, or part of one. 'modules' lists them.";
                return result;
            }

            if (program == null)
            {
                result.Message = "No program is being debugged, so there are no modules to load symbols for.";
                return result;
            }

            query = query.Trim();
            IDebugModule2 match = null;
            var names = new List<string>();

            foreach (var module in NativeReader.Enumerate(program))
            {
                result.LoadedCount++;

                var name = NativeReader.NameOf(module);
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                // An exact name beats every partial one, so 'nos.dll' picks that and not
                // the plugin whose name happens to contain it.
                if (match == null || string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
                    match = module;

                names.Add(name);
            }

            if (names.Count == 0)
            {
                result.Message = "No loaded module matches '" + query + "'. " + result.LoadedCount +
                                 " modules are loaded and more can load while the program runs.";
                return result;
            }

            if (names.Count > 1 && !names.Contains(query, StringComparer.OrdinalIgnoreCase))
            {
                result.Candidates = names;
                result.Message = "'" + query + "' matches " + names.Count + " modules. Name one of them.";
                return result;
            }

            var module3 = match as IDebugModule3;
            if (module3 == null)
            {
                // Symbol loading and the search account both live on IDebugModule3. An
                // engine whose modules stop at IDebugModule2 can still be asked what
                // state they are in, and that is all this may claim.
                result.Module = NativeReader.Describe(match);
                result.Message = "This debug engine does not offer symbol loading for " + names[0] +
                                 ", so nothing here can load them or say where it looked.";
                return result;
            }

            if (load)
            {
                result.LoadTried = true;
                result.LoadRefused = module3.LoadSymbols() != VSConstants.S_OK;
            }

            // Read after the attempt, never before: whether it worked is a property of
            // the module now, not of what the load call returned.
            result.Module = NativeReader.Describe(match);

            if (result.Module == null)
            {
                result.Message = "The engine would not say what state " + names[0] + " is in" +
                                 (load ? ", so whether the load worked is unknown." : ".");
                return result;
            }

            if (!result.Module.SymbolsLoaded) result.SearchInfo = VerboseSearch(module3);

            return result;
        }

        /// <summary>
        /// The Symbol Load Information text: every path tried, and what each one turned
        /// out to be. This is where a PDB that is present but does not match the binary
        /// finally says so.
        /// </summary>
        static string VerboseSearch(IDebugModule3 module)
        {
            var search = new MODULE_SYMBOL_SEARCH_INFO[1];
            if (module.GetSymbolInfo(enum_SYMBOL_SEARCH_INFO_FIELDS.SSIF_VERBOSE_SEARCH_INFO, search)
                    != VSConstants.S_OK)
            {
                return null;
            }

            var text = search[0].bstrVerboseSearchInfo;
            return string.IsNullOrWhiteSpace(text) ? null : text.Replace("\r\n", "\n").Trim();
        }
    }
}
