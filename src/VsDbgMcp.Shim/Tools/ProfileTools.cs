using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
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
            // A saved aggregate that cannot be read is rebuilt from the raw trace when it is
            // still there. The lookup goes through Select because this reply is about one named
            // capture: honoured exactly rather than answered with a neighbour, and a file that
            // cannot be read answers with the call that can rebuild it, which is the hint the
            // reply has already told the model to make.
            // With no captureId there is no saved capture to look for. Select takes a null id to
            // mean the most recent one, which would answer with an unrelated capture.
            var taken = Sessions.Captures;
            var pick = string.IsNullOrEmpty(collection.CaptureId) ? Selection.Nothing.Value : Select(taken, null, collection.CaptureId);
            if (pick is Selection.Found found) return Reported(found.Capture, taken);

            // A damaged file is a fact about the store, not the outcome of this call, so it
            // follows the reply as a note rather than opening it: a model reads the first line
            // as what happened, and what happened here is the capture being stopped.
            var damaged = pick as Selection.Damaged;
            var hint = damaged?.Error;

            if (recoverClosedTrace && ProfileArtifacts.ReconcileClosedTrace(collection))
            {
                // Read validates the ETL before retaining an aggregate. Keep the raw
                // package for host-side reconciliation of collector ownership.
                collection.RetainRaw = true;
            }
            if (collection.Status != "collected" && collection.Status != "interrupted" && collection.Status != "aggregated")
                return Notes("Capture " + collection.CaptureId + ": " + collection.Status + ". " + collection.Error, hint);
            var capture = TraceReader.Read(collection, collection.Modules ?? new System.Collections.Generic.List<VsDbgMcp.Contracts.ModuleInfo>(), out var failure);
            if (capture == null)
            {
                // profile_recover rebuilds from this same raw trace, and the line above is why that
                // just failed, so the note names the damaged file without sending the model
                // straight back to the same call.
                if (damaged != null)
                    hint = damaged.Problem + " profile_recover reads the same raw trace, so it can only help once the " +
                           "problem above is resolved; profile_start collects a new capture.";
                var retained = File.Exists(collection.Path) ? " Raw trace retained: " + collection.Path : "";
                return Notes(failure + retained, hint);
            }
            capture.CaptureId = collection.CaptureId;
            capture.Metadata = collection;
            Sessions.Captures.Keep(capture); // May fail; preserve raw evidence if it does.
            if (!collection.RetainRaw && System.IO.File.Exists(collection.Path)) System.IO.File.Delete(collection.Path);
            // Keeping replaced the damaged entry, so the store no longer has this one to warn about.
            return Reported(capture, taken);
        }

        /// <summary>A full report of one capture, with the note on the saved captures that need
        /// recovery said once: here rather than also in the report's footer.</summary>
        static string Reported(Capture capture, Captures taken)
        {
            var note = RecoveryNote(taken);
            return Notes(Render.Profile(capture, new ProfileQuery { RecoveryAlreadySaid = note != null }, null, taken.All), note);
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
            if (collection == null) return "The collector said nothing at all.";
            return Aggregate(collection);
        });

        [McpServerTool(Name = "profile_status", ReadOnly = true, UseStructuredContent = true)]
        [Description("List persisted capture metadata, including owner PID, host epoch, debug generation, timestamps, status, intervention markers and raw trace path. needsRecovery marks the captures whose saved aggregate cannot be read, so their captureId can be handed profile_recover. A saved collecting state is not a live collector health check.")]
        public System.Collections.Generic.List<ProfileResponse> ProfileStatus()
        {
            // The collections directory says what the collector recorded; only the capture
            // store knows which aggregates still read. A capture whose file is damaged
            // otherwise appears here as sound, beside the raw trace that could rebuild it.
            var needsRecovery = Sessions.Captures.NeedsRecovery;
            return Captures.Collections().ConvertAll(one => ProfileResponse.From(one, needsRecovery));
        }

        [McpServerTool(Name = "profile_export")]
        [Description("Export a capture as structured JSON, a readable report, and an optional retained raw trace. Existing destination files are not overwritten. Works without a live debugger. A capture named by number or captureId whose saved file cannot be read is refused before anything is written, with its captureId for profile_recover; with neither, the most recent readable capture is exported and the reply says which.")]
        public Task<string> Export(string directory, int? capture = null, bool includeRaw = false, string captureId = null,
            string instance = null, CancellationToken ct = default) => Locally(instance, ct, () =>
        {
            var taken = Sessions.Captures;
            var pick = Select(taken, capture, captureId);
            if (pick is Selection.Nothing) return "No capture has been collected in this session. Call profile_start, let the program work, then profile_stop.";
            if (pick is Selection.Refused refused) return Reply.Bad(Notes(refused.Error, RecoveryNote(taken, pick)));
            var found = (Selection.Found) pick;
            try
            {
                return Notes(taken.Export(found.Capture, directory, includeRaw), found.Notice, RecoveryNote(taken, pick));
            }
            // The store's message says what is on disk afterwards: an earlier export in the way,
            // or nothing from this call, or the files it wrote and could not remove.
            catch (IOException ex) { return Reply.Bad(ex.Message); }
        });

        // Read-only is kept on purpose. What this reads is persisted evidence that the call
        // cannot change, and the only thing it mutates is the shim's own cache of what it
        // last loaded, which no client can observe; so a caller that treats a report as safe
        // to retry, to run in parallel, or to run without approval is still right. What a
        // first report of an old capture costs is time - the whole capture comes off disk -
        // and that is said in the description rather than taken back here.
        [McpServerTool(Name = "profile_report", ReadOnly = true)]
        // What is left here is the one thing a model cannot learn by reading the reply: that the
        // capture it gets with no argument may not be the newest one, and says which it is. The
        // cost of a first report - the whole capture comes off disk - and the refusal of a
        // capture that cannot be read, with its captureId and the raw trace recovery depends on,
        // are both demonstrated by the replies themselves, so they are not paid for again here.
        [Description("Query a persisted CPU capture. Combine module and thread filters with sort=self, inclusive (call tree), or module. focus selects a subtree with sort=inclusive; rawTree preserves startup frames and disables percentage pruning. Function view shows callers/callees. Comparisons use against alone. details repeats full coverage and catalog metadata. Numeric capture IDs are local to this shim; captureId is stable across reconnects. Omitting the capture reports the most recent readable one, and the reply says which. An unreadable capture is refused with its captureId for profile_recover.")]
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
                var pick = Select(taken, capture, captureId);
                if (pick is Selection.Nothing)
                    return "No profile has been collected in this session. Call profile_start, let the program work, then profile_stop.";
                if (pick is Selection.Refused refused) return Reply.Bad(Notes(refused.Error, RecoveryNote(taken, pick)));
                var found = (Selection.Found) pick;
                var one = found.Capture;

                Capture other = null;
                if (against != null)
                {
                    // A number is honoured exactly or refused, so this is one or the other.
                    var rival = Select(taken, against, null);
                    if (rival is Selection.Refused unusable) return Reply.Bad(Notes(unusable.Error, RecoveryNote(taken, rival)));
                    other = ((Selection.Found) rival).Capture;
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

                // The footer would otherwise say "profile_recover" a second time in one reply,
                // after the line naming the capture the default skipped or the note below.
                var recovery = RecoveryNote(taken, pick);
                query.RecoveryAlreadySaid = found.Notice != null || recovery != null;
                var answer = Render.Profile(one, query, other, taken.All);
                return Notes(answer,
                    top > TopLimit
                        ? top + " rows were asked for and " + TopLimit + " is the most " +
                          "this will print, so the fold above happened " + TopLimit + " rows in."
                        : null,
                    found.Notice, recovery);
            }, function ?? module ?? sort);

        /// <summary>How many rows are worth printing before a reply stops being read.</summary>
        const int TopLimit = 200;

        /// <summary>
        /// What a capture handle resolved to: the capture to report on, what has to be said
        /// about how it was reached, why it could not be, or that there is nothing at all.
        /// </summary>
        public abstract class Selection
        {
            private protected Selection() { }

            /// <summary>Nothing has been collected in this session.</summary>
            public sealed class Nothing : Selection
            {
                Nothing() { }
                internal static readonly Nothing Value = new Nothing();
            }

            /// <summary>The capture to report on.</summary>
            public sealed class Found : Selection
            {
                internal Found(Capture capture, string notice = null)
                {
                    Capture = capture;
                    Notice = notice;
                }

                public Capture Capture { get; }

                /// <summary>The line the reply must carry when this is not the capture the default
                /// pointed at, or null when it is.</summary>
                public string Notice { get; }
            }

            /// <summary>The call cannot be answered, and why, already worded for the model.</summary>
            public class Refused : Selection
            {
                internal Refused(string error) => Error = error;
                public string Error { get; }
            }

            /// <summary>
            /// Refused because the saved file behind the handle is there but cannot be read, so
            /// the capture can still be rebuilt rather than being gone. Any other refusal is a
            /// capture that does not exist, and says nothing about the store.
            /// </summary>
            public sealed class Damaged : Refused
            {
                internal Damaged(string problem, string next) : base(problem + "\n" + next) => Problem = problem;

                /// <summary>The store's line naming the capture and its file, without the next call.</summary>
                public string Problem { get; }
            }
        }

        /// <summary>
        /// Which capture a call meant.
        ///
        /// A number or an id is honoured exactly or refused, never answered with a
        /// neighbouring capture: reporting a different run as the one asked for is the one
        /// thing a profile reply must not do quietly. Only the default - the most recent
        /// capture - may fall back to the next readable one, and then the reply carries the
        /// line saying so.
        /// </summary>
        public static Selection Select(Captures taken, int? id, string captureId)
        {
            if (captureId != null) return Exactly(taken, () => taken.FindStable(captureId), "capture with captureId \"" + captureId + "\"");
            if (id != null) return Exactly(taken, () => taken.Find(id), "capture #" + id);
            return MostRecent(taken);
        }

        static Selection Exactly(Captures taken, Func<Capture> find, string handle)
        {
            Capture one;
            try { one = find(); }
            catch (UnreadableCaptureException ex) { return new Selection.Damaged(ex.Message, Recovery(taken, ex)); }
            catch (CaptureUnavailableException ex) { return new Selection.Refused(ex.Message + "\n" + Retry); }
            // What a refusal offers instead is only ever a capture a call can be made with. The
            // ones needing recovery are named in the note, with the call that can do something
            // with them, rather than here as if asking for them again were the next step.
            return one == null
                ? new Selection.Refused("There is no " + handle + ". " + Available(taken, true))
                : new Selection.Found(one);
        }

        /// <summary>
        /// The newest capture, or the newest one that can still be read when the newest
        /// cannot. Before saved captures were listed as they are, the default question was
        /// always answerable; a single damaged file must not cost a session its reports.
        /// </summary>
        static Selection MostRecent(Captures taken)
        {
            Capture newest;
            CaptureReadException failure;

            try
            {
                newest = taken.Find(null);
                if (newest == null) return Selection.Nothing.Value;
                return new Selection.Found(newest);
            }
            catch (CaptureReadException ex) { failure = ex; }

            var next = NextReadable(taken, Failed(taken, failure));
            if (next == null) return new Selection.Refused(Unreadable(taken, failure) + " No other saved capture can be read.");

            try { return new Selection.Found(taken.Find(next.Id), Skipped(taken, failure, next)); }
            catch (CaptureReadException ex) { return new Selection.Refused(Unreadable(taken, ex)); }
        }

        /// <summary>
        /// The newest capture the listing expects to read, other than the one that just failed.
        /// A file can look sound from its header and fail on the way through the rest of it, and
        /// the capture that failed is not a candidate for answering in its place however it is
        /// listed.
        /// </summary>
        static Captures.Entry NextReadable(Captures taken, Captures.Entry failed) =>
            taken.All.LastOrDefault(e => !e.Unreadable && !SameEntry(e, failed));

        static bool SameEntry(Captures.Entry one, Captures.Entry other)
        {
            if (one == null || other == null) return false;
            if (one.Path != null && other.Path != null) return string.Equals(one.Path, other.Path, StringComparison.OrdinalIgnoreCase);
            return one.Id == other.Id;
        }

        /// <summary>
        /// The one line naming the capture the default skipped and the one reported in its
        /// place, so a reply that is not about the newest capture never reads as if it were.
        /// </summary>
        static string Skipped(Captures taken, CaptureReadException ex, Captures.Entry next)
        {
            var failed = Failed(taken, ex);
            return "the most recent capture " + (failed == null ? "" : "#" + failed.Id + " " + Handle(failed) + " ") +
                   "could not be read" + (string.IsNullOrEmpty(ex.Path) ? "" : " from " + Captures.Fold(ex.Path)) +
                   " and was skipped; this report is of #" + next.Id + " " + Handle(next) + ". " +
                   NextStep(taken, ex);
        }

        /// <summary>How a reply names a listed capture: by its captureId, or by its file's name
        /// when that is not a captureId, so no reply offers a call a handle it would refuse.</summary>
        static string Handle(Captures.Entry entry)
        {
            var id = IdOf(entry);
            return Captures.CanRecover(id) ? "captureId \"" + id + "\"" : "\"" + id + "\"";
        }

        /// <summary>
        /// A capture that cannot be read: the store's own line naming it and its file, kept
        /// because that is the evidence, then what to do about it.
        /// </summary>
        static string Unreadable(Captures taken, CaptureReadException ex) =>
            ex.Message + "\n" + NextStep(taken, ex);

        static string NextStep(Captures taken, CaptureReadException ex) =>
            ex is UnreadableCaptureException unreadable ? Recovery(taken, unreadable) : Retry;

        /// <summary>For a file that could not be opened just now: nothing says it is damaged.</summary>
        const string Retry = "Nothing says the file is damaged, so profile_recover is not the fix; the same call can be tried again.";

        /// <summary>
        /// The next call, named with the argument it needs. Recovery is offered as
        /// conditional on the raw trace rather than promised, because a trace deleted after
        /// aggregation is the usual reason an aggregate file is the only copy left, and a
        /// model that tries it and is refused has spent a call to learn what could have been
        /// said here.
        /// </summary>
        static string Recovery(Captures taken, UnreadableCaptureException ex)
        {
            var id = string.IsNullOrEmpty(ex.CaptureId) ? IdOf(Failed(taken, ex)) : ex.CaptureId;
            // A file dropped into the directory by hand names a capture this store can list but no
            // call can act on: profile_recover refuses a handle that is not a captureId, so
            // recommending it would cost a call to arrive back where this reply started.
            return string.IsNullOrEmpty(id) || !Captures.CanRecover(id)
                ? "It has no captureId, so profile_recover cannot name it; profile_start collects another capture."
                : "Call profile_recover(captureId: \"" + id +
                  "\"). It rebuilds the aggregate from the raw trace, so it works only while that trace is still retained; profile_status shows whether it is.";
        }

        /// <summary>The list entry a failed load was working from, found by file rather than out of the message.</summary>
        static Captures.Entry Failed(Captures taken, CaptureReadException ex)
        {
            if (taken == null) return null;
            var all = taken.All;
            if (ex.Path != null)
            {
                var byPath = all.FirstOrDefault(e => ex.Path == e.Path);
                if (byPath != null) return byPath;
            }
            return ex.CaptureId == null ? null : all.FirstOrDefault(e => ex.CaptureId == e.CaptureId);
        }

        /// <summary>The id to print for an entry: the saved one, else the file it came from.</summary>
        static string IdOf(Captures.Entry entry)
        {
            if (entry == null) return null;
            if (!string.IsNullOrEmpty(entry.CaptureId)) return entry.CaptureId;
            return entry.Path == null ? null : Path.GetFileNameWithoutExtension(entry.Path);
        }

        /// <summary>
        /// One line on the saved captures this reply did not touch and cannot read, so they
        /// are heard of here rather than from a later call failing. The count is what a model
        /// needs to decide to look; the ids are what profile_recover needs to be given.
        /// </summary>
        internal static string RecoveryNote(Captures taken) => RecoveryNote(taken == null ? null : taken.NeedsRecovery, null);

        static string RecoveryNote(System.Collections.Generic.IReadOnlyList<Captures.Entry> broken, string said)
        {
            if (broken == null || broken.Count == 0) return null;
            var rest = said == null
                ? broken
                : broken.Where(e =>
                {
                    var id = IdOf(e);
                    return id == null || said.IndexOf(id, StringComparison.Ordinal) < 0;
                }).ToList();
            if (rest.Count == 0) return null;

            // profile_recover refuses a name that is not a captureId, so those files are counted
            // apart instead of being offered to it.
            var recoverable = rest.Where(e => Captures.CanRecover(IdOf(e))).ToList();
            var stray = rest.Count - recoverable.Count;
            var lines = new System.Collections.Generic.List<string>();
            if (recoverable.Count > 0)
            {
                var ids = recoverable.Select(IdOf).Take(3).ToList();
                var listed = string.Join(", ", ids.ConvertAll(id => "\"" + id + "\""));
                if (recoverable.Count > ids.Count) listed += " and " + (recoverable.Count - ids.Count) + " more";
                lines.Add(recoverable.Count + " saved capture" + (recoverable.Count == 1 ? "" : "s") + " need" + (recoverable.Count == 1 ? "s" : "") +
                          " recovery: captureId " + listed +
                          ". profile_status marks which of them need it and whether each still has a raw trace; profile_recover rebuilds those.");
            }
            if (stray > 0)
                lines.Add(stray + " saved file" + (stray == 1 ? " cannot be read and has" : "s cannot be read and have") +
                          " no captureId, so profile_recover cannot rebuild " + (stray == 1 ? "it." : "them."));
            return string.Join(" ", lines);
        }

        /// <summary>
        /// The same line with the captures this reply has already named taken out of it. A
        /// refusal or a fallback line that quotes a captureId has said the thing about that
        /// capture, and two notes saying one thing is one note too many; what the reply did not
        /// name is still worth hearing, so the line is kept for the rest of them.
        /// </summary>
        static string RecoveryNote(Captures taken, Selection pick)
        {
            var said = pick switch
            {
                Selection.Found found => found.Notice,
                Selection.Refused refused => refused.Error,
                _ => null,
            };
            return said == null ? RecoveryNote(taken) : RecoveryNote(taken.NeedsRecovery, said);
        }

        /// <summary>
        /// The captures there are, as handles a call can actually use, newest first and no more
        /// than three of them: this prints inside an error reply, and a list that grows with the
        /// store costs that reply exactly what it needs to not cost, so what is left out is
        /// counted instead. A number is only good until this shim restarts, so the stable id is
        /// printed beside it - including for a capture whose file cannot be read, because that
        /// id is the only thing a call can be made with for it, and it is marked as needing
        /// profile_recover rather than offered as if it would answer.
        /// </summary>
        public static string Available(Captures taken, bool readableOnly = false)
        {
            var all = taken.All;
            var listed = new System.Collections.Generic.List<Captures.Entry>();
            for (var i = all.Count - 1; i >= 0; i--)
                if (!readableOnly || !all[i].Unreadable) listed.Add(all[i]);

            var names = new StringBuilder("Collected so far:");
            if (listed.Count == 0) return names.Append(readableOnly ? " none that can be read." : " none.").ToString();

            for (var i = 0; i < Math.Min(listed.Count, AvailableLimit); i++)
            {
                var one = listed[i];
                var id = IdOf(one);
                names.Append(" #").Append(one.Id);
                if (Captures.CanRecover(id))
                {
                    names.Append(" captureId \"").Append(id).Append('"');
                    if (one.Unreadable) names.Append(" (needs profile_recover)");
                }
                // A file whose name is not a captureId: no call can be made with it.
                else if (!string.IsNullOrEmpty(id)) names.Append(" \"").Append(id).Append("\" (unreadable, no captureId)");
            }
            if (listed.Count > AvailableLimit) names.Append(" and ").Append(listed.Count - AvailableLimit).Append(" more");
            return names.ToString();
        }

        /// <summary>How many capture handles are worth offering inside an error reply.</summary>
        const int AvailableLimit = 3;

        /// <summary>
        /// Adds what the reply has to say about the store rather than the capture, each line as
        /// a note, so a report of one capture can still warn about the others. A line may arrive
        /// already labelled with the word, because the helper that wrote it says the same thing;
        /// the label is taken off rather than doubled, the line is trimmed of the whitespace it
        /// may have picked up, and one blank line goes before it, so a reply never carries a
        /// blank paragraph or a note about a note.
        /// </summary>
        static string Notes(string answer, params string[] lines)
        {
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var text = line.Trim();
                if (text.StartsWith(Note, StringComparison.OrdinalIgnoreCase)) text = text.Substring(Note.Length).Trim();
                if (text.Length == 0) continue;
                answer = answer.TrimEnd('\r', '\n') + "\n\n" + Note + text;
            }
            return answer;
        }

        const string Note = "note: ";

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
