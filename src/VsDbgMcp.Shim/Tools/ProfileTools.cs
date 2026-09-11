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
            CancellationToken ct = default,
            [Description("Keep the raw trace after aggregation for export, subject to the documented retention policy.")] bool retainRaw = false)
            => On(instance, ct, async link =>
            {
                var result = await link.Profiles.ProfileBeginAsync(retainRaw, ct).ConfigureAwait(false);
                return Render.Op(result, "Collecting.");
            });

        [McpServerTool(Name = "profile_stop")]
        [Description("Stop sampling and report where the time went: the functions the samples landed in, the call path most of them went down, and what each thread was doing. Metadata and aggregate counts are persisted. Raw traces are deleted only after successful persistence unless retainRaw was requested; failed traces are retained, so profile_report can ask other things of this same profile without collecting again. Function names come from the symbols the debugger has already loaded, so a module whose symbols are not loaded is reported as one row rather than as functions.")]
        public Task<string> ProfileStop(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var collection = await link.Debug.ProfileStopAsync(ct).ConfigureAwait(false);
                if (collection == null) return "The collector said nothing at all.";
                if (!string.IsNullOrEmpty(collection.Error)) return collection.Error;

                return Aggregate(collection);

            });

        string Aggregate(VsDbgMcp.Contracts.ProfileCollection collection, bool recoverClosedTrace = false)
        {
            var existing = Sessions.Captures.FindStable(collection.CaptureId);
            if (existing != null) return Render.Profile(existing, new ProfileQuery(), null, Sessions.Captures.All);
            if (recoverClosedTrace && ProfileArtifacts.ReconcileClosedTrace(collection))
            {
                // Read validates the ETL before retaining an aggregate. Keep the raw
                // package for host-side reconciliation of collector ownership.
                collection.RetainRaw = true;
            }
            if (collection.Status != "collected" && collection.Status != "interrupted" && collection.Status != "aggregated")
                return "Capture " + collection.CaptureId + ": " + collection.Status + ". " + collection.Error;
            var capture = TraceReader.Read(collection, collection.Modules ?? new System.Collections.Generic.List<VsDbgMcp.Contracts.ModuleInfo>(), out var failure);
            if (capture == null) return failure + " Raw trace retained: " + collection.Path;
            capture.CaptureId = collection.CaptureId;
            capture.Metadata = collection;
            Sessions.Captures.Keep(capture); // May fail; preserve raw evidence if it does.
            if (!collection.RetainRaw && System.IO.File.Exists(collection.Path)) System.IO.File.Delete(collection.Path);
            return Render.Profile(capture, new ProfileQuery(), null, Sessions.Captures.All);
        }

        [McpServerTool(Name = "profile_recover")]
        [Description("Recover a completed or interrupted capture by its stable captureId. Uses saved module identities and symbols; attaching to the original process is unnecessary. Does not stop or restart a collector.")]
        public Task<string> Recover(string captureId, string instance = null, CancellationToken ct = default) => Locally(instance, ct, () =>
        {
            var collection = Captures.ReadCollection(captureId);
            return Aggregate(collection, recoverClosedTrace: true);
        });

        [McpServerTool(Name = "profile_recover_stop")]
        [Description("Explicitly stop this tool's retained collector session by captureId after an interruption, then recover its results. Refuses to stop a different active capture; does not require the original process to remain debugged.")]
        public Task<string> RecoverStop(string captureId, string instance = null, CancellationToken ct = default) => On(instance, ct, async link =>
        {
            var collection = await link.Profiles.ProfileStopCaptureAsync(captureId, ct).ConfigureAwait(false);
            return Aggregate(collection);
        });

        [McpServerTool(Name = "profile_status", ReadOnly = true, UseStructuredContent = true)]
        [Description("List persisted capture metadata, including owner PID, host epoch, debug generation, timestamps, status, intervention markers and raw trace path. A saved collecting state is not a live collector health check.")]
        public System.Collections.Generic.List<ProfileResponse> ProfileStatus() => Captures.Collections().ConvertAll(ProfileResponse.From);

        [McpServerTool(Name = "profile_export")]
        [Description("Export a capture as structured JSON, a readable report, and an optional retained raw trace. Existing destination files are not overwritten. Works without a live debugger.")]
        public Task<string> Export(string directory, int? capture = null, bool includeRaw = false, string captureId = null,
            string instance = null, CancellationToken ct = default) => Locally(instance, ct, () =>
        {
            var one = captureId == null ? Sessions.Captures.Find(capture) : Sessions.Captures.FindStable(captureId);
            if (one == null) return "Capture not found. Use profile_status or profile_recover.";
            return Sessions.Captures.Export(one, directory, includeRaw);
        });

        [McpServerTool(Name = "profile_report", ReadOnly = true)]
        [Description("Query a persisted CPU capture. Combine module and thread filters with sort=self, inclusive (call tree), or module. focus selects a subtree with sort=inclusive; rawTree preserves startup frames and disables percentage pruning. Function view shows callers/callees. Comparisons use against alone. details repeats full coverage and catalog metadata. Numeric capture IDs are local to this shim; captureId is stable across reconnects.")]
        public Task<string> ProfileReport(
            [Description("Which capture, by the number the reports print. Omit for the most recent.")] int? capture = null,
            [Description("Compare with this capture, reporting what moved between them.")] int? against = null,
            [Description("One function, named as the report prints it, or by its own name without the module or the namespace. A name that could mean more than one function is refused and the candidates listed.")] string function = null,
            [Description("Keep only frames in modules whose name contains this.")] string module = null,
            [Description("Keep only samples taken in this thread.")] int? thread = null,
            [Description("self for where samples landed, inclusive for a call tree, module to roll up per binary.")] string sort = null,
            [Description("How many rows before the rest is folded into one line.")] int top = 12,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default,
            bool details = false, string focus = null, bool rawTree = false, string captureId = null)
            => Locally(instance, ct, () =>
            {
                var taken = Sessions.Captures;
                var one = captureId == null ? taken.Find(capture) : taken.FindStable(captureId);
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

                if (sort != null && sort != "self" && sort != "inclusive" && sort != "module" && sort != "tree" && sort != "modules") return "sort must be self, inclusive, or module.";
                var query = new ProfileQuery
                {
                    Function = string.IsNullOrWhiteSpace(function) ? null : function.Trim(),
                    Module = string.IsNullOrWhiteSpace(module) ? null : module.Trim(),
                    Thread = thread,
                    Sort = Sorting(sort),
                    Top = Math.Max(1, Math.Min(top, TopLimit)), Details = details, Focus = focus, RawTree = rawTree
                };

                var clash = query.Conflict(other != null);
                if (clash != null) return clash;

                var answer = Render.Profile(one, query, other, taken.All);
                return top > TopLimit
                    ? answer + "\n\nnote: " + top + " rows were asked for and " + TopLimit + " is the most " +
                      "this will print, so the fold above happened " + TopLimit + " rows in."
                    : answer;
            }, function ?? module ?? sort);

        /// <summary>How many rows are worth printing before a reply stops being read.</summary>
        const int TopLimit = 200;

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
