using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsDbgMcp.Shim.Profiling;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    [McpServerToolType]
    public sealed class ProfileTools : ToolBase
    {
        public ProfileTools(SessionManager sessions) : base(sessions) { }

        [McpServerTool(Name = "profile_start")]
        [Description("Begin sampling the debuggee's CPU use. Start this, let the work you care about happen - go, run_to, or simply wait - then call profile_stop. Sampling sees only threads that are on a processor: waiting on a lock, on a file or on another thread is invisible to it, so a profile of a program that was blocked will be nearly empty and will say so rather than reporting that nothing was slow. The debuggee keeps running while this collects and the debugger stays attached, so a breakpoint can still stop it - though stopping it means it is not running, and time spent stopped is time not sampled.")]
        public Task<string> ProfileStart(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.ProfileStartAsync(ct).ConfigureAwait(false);
                return Render.Op(result, "Collecting.");
            });

        [McpServerTool(Name = "profile_stop")]
        [Description("Stop sampling and report where the time went: the functions the samples landed in, the call path most of them went down, and what each thread was doing. Reading the trace takes a few seconds and the trace is then deleted, but the counts are kept for the rest of the session, so profile_report can ask other things of this same profile without collecting again. Function names come from the symbols the debugger has already loaded, so a module whose symbols are not loaded is reported as one row rather than as functions.")]
        public Task<string> ProfileStop(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var collection = await link.Debug.ProfileStopAsync(ct).ConfigureAwait(false);
                if (collection == null) return "The collector said nothing at all.";
                if (!string.IsNullOrEmpty(collection.Error)) return collection.Error;

                // The debugger has already found and loaded the symbols for this session,
                // and knows which modules are the user's own. Reading the trace without
                // that would mean going to a symbol server for every module in the
                // process and still not knowing which of them anyone cares about.
                var modules = await link.Debug.ModulesAsync(null, ct).ConfigureAwait(false);

                var capture = TraceReader.Read(collection, modules?.Modules ?? new System.Collections.Generic.List<VsDbgMcp.Contracts.ModuleInfo>(), out var failure);
                if (capture == null) return failure;

                Sessions.Captures.Keep(capture);
                return Render.Profile(capture, new ProfileQuery(), null, Sessions.Captures.All);
            });

        [McpServerTool(Name = "profile_report", ReadOnly = true)]
        [Description("Ask something else of a profile already collected. With no arguments it repeats the most recent one's summary. 'function' gives one function's callers and the functions it called, and its source lines where the debuggee's own symbols carry line numbers. 'module' keeps only frames in that binary, still as shares of the whole capture; 'thread' keeps only one thread, as shares of that thread. sort='inclusive' reads the same samples as a call tree, which says which subsystem costs rather than which function; sort='module' rolls them up per binary, which is the first question to ask of a host with plugins in it. 'against' compares one capture with another and reports what moved, in percentage points rather than as a ratio, because two runs rarely lasted the same time. None of this collects anything, so it is free and can be asked as often as needed.")]
        public Task<string> ProfileReport(
            [Description("Which capture, by the number the reports print. Omit for the most recent.")] int? capture = null,
            [Description("Compare with this capture, reporting what moved between them.")] int? against = null,
            [Description("One function, named as the report prints it, or by its own name without the module or the namespace. A name that could mean more than one function is refused and the candidates listed.")] string function = null,
            [Description("Keep only frames in modules whose name contains this.")] string module = null,
            [Description("Keep only samples taken in this thread.")] int? thread = null,
            [Description("self for where samples landed, inclusive for a call tree, module to roll up per binary.")] string sort = null,
            [Description("How many rows before the rest is folded into one line.")] int top = 12,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => Locally(instance, ct, () =>
            {
                var taken = Sessions.Captures;
                var one = taken.Find(capture);
                if (one == null)
                {
                    return taken.All.Count == 0
                        ? "No profile has been collected in this session. Call profile_start, let the program work, then profile_stop."
                        : "There is no capture #" + capture + ". " + Available(taken);
                }

                Capture other = null;
                if (against != null)
                {
                    other = taken.Find(against);
                    if (other == null) return "There is no capture #" + against + ". " + Available(taken);
                    if (other.Id == one.Id) return "That is the same capture on both sides, so nothing can have moved.";
                }

                var query = new ProfileQuery
                {
                    Function = string.IsNullOrWhiteSpace(function) ? null : function.Trim(),
                    Module = string.IsNullOrWhiteSpace(module) ? null : module.Trim(),
                    Thread = thread,
                    Sort = Sorting(sort),
                    Top = Math.Max(1, Math.Min(top, 200))
                };

                return Render.Profile(one, query, other, taken.All);
            }, function ?? module ?? sort);

        static string Available(Captures taken)
        {
            var names = new System.Text.StringBuilder("Collected so far:");
            foreach (var one in taken.All) names.Append(" #").Append(one.Id);
            return names.ToString();
        }

        static string Sorting(string sort)
        {
            if (string.IsNullOrWhiteSpace(sort)) return ProfileQuery.Self;

            var asked = sort.Trim().ToLowerInvariant();
            if (asked == ProfileQuery.Inclusive || asked == "tree") return ProfileQuery.Inclusive;
            if (asked == ProfileQuery.ByModule || asked == "modules") return ProfileQuery.ByModule;
            return ProfileQuery.Self;
        }
    }
}
