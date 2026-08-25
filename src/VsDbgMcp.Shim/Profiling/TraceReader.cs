using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Etlx = Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Profiling
{
    /// <summary>
    /// Turns what the collector wrote into counted stacks, and throws the trace away.
    ///
    /// Reading happens here rather than in the extension because a few seconds of one
    /// small program is tens of megabytes on disk and several hundred in memory while
    /// it is being read, and the extension lives inside the editor.
    /// </summary>
    static class TraceReader
    {
        /// <summary>
        /// How many distinct addresses are worth asking the symbol files for a source
        /// line. Every ask is a lookup in a PDB, and a profile with thousands of cold
        /// addresses would spend longer resolving lines nobody reads than it did
        /// collecting.
        /// </summary>
        const int LineBudget = 4000;

        public static Capture Read(ProfileCollection collection, IReadOnlyList<ModuleInfo> modules, out string error)
        {
            error = null;
            var workspace = Path.Combine(Path.GetTempPath(), "vsdbgmcp-profile-" + collection.Pid);

            try
            {
                var etl = Extract(collection.Path, workspace, out error);
                if (etl == null) return null;

                return Digest(etl, collection, modules, out error);
            }
            catch (Exception ex)
            {
                error = "Could not read the profile: " + ex.Message;
                return null;
            }
            finally
            {
                Discard(workspace);
                Discard(collection.Path);
            }
        }

        /// <summary>
        /// Pulls the trace out of the collector's package, which is an ordinary zip with
        /// one entry that matters.
        /// </summary>
        static string Extract(string package, string workspace, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(package) || !File.Exists(package))
            {
                error = "The collector's output is not at " + package + ".";
                return null;
            }

            Directory.CreateDirectory(workspace);

            using (var zip = ZipFile.OpenRead(package))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".etl", StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    error = "The collector's output holds no trace, only: " +
                            string.Join(", ", zip.Entries.Select(e => e.Name).ToArray());
                    return null;
                }

                var path = Path.Combine(workspace, entry.Name);
                entry.ExtractToFile(path, true);
                return path;
            }
        }

        static Capture Digest(string etl, ProfileCollection collection, IReadOnlyList<ModuleInfo> modules, out string error)
        {
            error = null;

            var etlx = Etlx.TraceLog.CreateFromEventTraceLogFile(etl);
            using (var log = Etlx.TraceLog.OpenOrConvert(etlx))
            using (var symbols = SymbolsFor(modules))
            {
                var capture = new Capture
                {
                    ProcessName = collection.ProcessName,
                    Pid = collection.Pid,
                    Seconds = collection.Seconds
                };

                Resolve(log, symbols, collection.Pid, modules);

                var byAddress = new Dictionary<Etlx.CodeAddressIndex, int>();
                var byStack = new Dictionary<string, Capture.Stack>(StringComparer.Ordinal);
                var lineBudget = LineBudget;
                var path = new List<int>();

                foreach (var data in log.Events)
                {
                    if (data is SampledProfileIntervalTraceData interval && capture.SamplesPerSecond == null)
                    {
                        // The interval is in units of 100 nanoseconds. Taking the rate from
                        // the trace rather than assuming the usual one is what lets the
                        // report say what share of the wall clock this was, honestly.
                        if (interval.NewInterval > 0) capture.SamplesPerSecond = 10000000.0 / interval.NewInterval;
                        continue;
                    }

                    if (!(data is SampledProfileTraceData sample) || sample.ProcessID != collection.Pid) continue;

                    capture.Samples++;
                    capture.Threads[sample.ThreadID] =
                        capture.Threads.TryGetValue(sample.ThreadID, out var had) ? had + 1 : 1;

                    var stack = sample.CallStack();
                    if (stack == null) continue;
                    capture.Stacked++;

                    path.Clear();
                    for (var frame = stack; frame != null && path.Count < 128; frame = frame.Caller)
                        path.Add(Intern(capture, byAddress, frame.CodeAddress, symbols, modules, ref lineBudget));
                    path.Reverse();

                    Add(byStack, capture, path, sample.ThreadID);
                }

                if (capture.Samples == 0)
                {
                    error = "The profile holds no samples for " + collection.ProcessName + " (" + collection.Pid +
                            "). A process that was not running while it was collected has nothing to sample.";
                    return null;
                }

                return capture;
            }
        }

        /// <summary>
        /// Asks the symbol files about the modules the debugger already has symbols for,
        /// and no others.
        ///
        /// The debugger has done this work once already, and its answer is on hand: the
        /// exact file it loaded for each module. Letting the trace reader go looking for
        /// itself would send it to symbol servers for every system module in the process,
        /// which is minutes of network for names nobody asked about.
        /// </summary>
        static void Resolve(Etlx.TraceLog log, SymbolReader symbols, int pid, IReadOnlyList<ModuleInfo> modules)
        {
            var wanted = new HashSet<string>(
                modules.Where(m => m.SymbolsLoaded && !string.IsNullOrEmpty(m.Name)).Select(m => m.Name),
                StringComparer.OrdinalIgnoreCase);

            if (wanted.Count == 0) return;

            foreach (var process in log.Processes)
            {
                if (process.ProcessID != pid) continue;

                foreach (var module in process.LoadedModules)
                {
                    if (module.ModuleFile == null) continue;

                    var name = Path.GetFileName(module.ModuleFile.FilePath);
                    if (!wanted.Contains(name)) continue;

                    try
                    {
                        log.CodeAddresses.LookupSymbolsForModule(symbols, module.ModuleFile);
                    }
                    catch (Exception)
                    {
                        // A symbol file that will not open leaves its frames as addresses,
                        // which the report already knows how to say.
                    }
                }
            }
        }

        static SymbolReader SymbolsFor(IReadOnlyList<ModuleInfo> modules)
        {
            var directories = modules
                .Select(m => m.SymbolPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => { try { return Path.GetDirectoryName(p); } catch (ArgumentException) { return null; } })
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // No symbol server: everything this looks for has already been found once by
            // the debugger, and a server would only be consulted for what has not.
            return new SymbolReader(TextWriter.Null, string.Join(";", directories));
        }

        static int Intern(Capture capture, Dictionary<Etlx.CodeAddressIndex, int> byAddress, Etlx.TraceCodeAddress code,
            SymbolReader symbols, IReadOnlyList<ModuleInfo> modules, ref int lineBudget)
        {
            if (code == null) return Unknown(capture, byAddress);
            if (byAddress.TryGetValue(code.CodeAddressIndex, out var known)) return known;

            var module = code.ModuleName;
            var method = code.FullMethodName;
            var frame = new Capture.Frame
            {
                Module = string.IsNullOrEmpty(module) ? null : module + Extension(module, modules),
                Method = string.IsNullOrEmpty(method) ? null : method,
                Address = "0x" + code.Address.ToString("x"),
                UserCode = IsUserCode(module, modules)
            };

            if (frame.Named && frame.UserCode && lineBudget > 0)
            {
                lineBudget--;
                try
                {
                    var line = code.GetSourceLine(symbols);
                    if (line != null)
                    {
                        frame.File = line.SourceFile == null ? null : line.SourceFile.BuildTimeFilePath;
                        frame.Line = line.LineNumber;
                    }
                }
                catch (Exception)
                {
                    // Line information is a bonus; a function name without one still says
                    // where to look.
                }
            }

            capture.Frames.Add(frame);
            var index = capture.Frames.Count - 1;
            byAddress[code.CodeAddressIndex] = index;
            return index;
        }

        static int Unknown(Capture capture, Dictionary<Etlx.CodeAddressIndex, int> byAddress)
        {
            if (byAddress.TryGetValue(Etlx.CodeAddressIndex.Invalid, out var known)) return known;

            capture.Frames.Add(new Capture.Frame { Module = null, Method = null, Address = "0x0" });
            var index = capture.Frames.Count - 1;
            byAddress[Etlx.CodeAddressIndex.Invalid] = index;
            return index;
        }

        /// <summary>
        /// The trace names a module without its extension. The debugger's own list has
        /// the full name, and matching the two lets a report say DebugTarget.exe where
        /// the trace would only say DebugTarget.
        /// </summary>
        static string Extension(string module, IReadOnlyList<ModuleInfo> modules)
        {
            foreach (var known in modules)
            {
                if (string.IsNullOrEmpty(known.Name)) continue;

                var stem = Path.GetFileNameWithoutExtension(known.Name);
                if (string.Equals(stem, module, StringComparison.OrdinalIgnoreCase))
                    return Path.GetExtension(known.Name);
            }
            return "";
        }

        static bool IsUserCode(string module, IReadOnlyList<ModuleInfo> modules)
        {
            if (string.IsNullOrEmpty(module)) return false;

            foreach (var known in modules)
            {
                if (string.IsNullOrEmpty(known.Name)) continue;

                var stem = Path.GetFileNameWithoutExtension(known.Name);
                if (string.Equals(stem, module, StringComparison.OrdinalIgnoreCase)) return known.IsUserCode;
            }
            return false;
        }

        static void Add(Dictionary<string, Capture.Stack> byStack, Capture capture, List<int> path, int threadId)
        {
            var key = threadId + ":" + string.Join(",", path.Select(i => i.ToString()).ToArray());
            if (byStack.TryGetValue(key, out var stack))
            {
                stack.Samples++;
                return;
            }

            stack = new Capture.Stack { Frames = path.ToArray(), ThreadId = threadId, Samples = 1 };
            byStack[key] = stack;
            capture.Stacks.Add(stack);
        }

        static void Discard(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
