using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    [McpServerToolType]
    public sealed class LifecycleTools : ToolBase
    {
        public LifecycleTools(SessionManager sessions) : base(sessions) { }

        /// <summary>
        /// Said by everything that starts a run.
        ///
        /// One session ran the editor as five pids and the launcher as four, and an
        /// address was compared across a restart without anyone noticing. Nothing here
        /// can tell when an address was captured, so a check that fired sometimes would
        /// be worse than the rule stated plainly every time.
        /// </summary>
        const string NewRun =
            "Every pid, thread id, address and container reference read before this call belongs to a " +
            "different run of the debuggee and names nothing now. status and wait carry a generation " +
            "number; compare it before comparing any of them.";

        /// <summary>
        /// Attaching may join a session that is already being debugged instead of
        /// starting one, and nothing here knows which it did.
        /// </summary>
        const string MaybeNewRun =
            "If this started a run rather than joining one already being debugged, every pid, thread id, " +
            "address and container reference read before it names nothing now. status and wait carry a " +
            "generation number; compare it before comparing any of them.";

        /// <summary>
        /// Runs a call that begins a run, and keeps the run count honest around it.
        ///
        /// The count moves before the call goes out, so the first stop of the new run
        /// already carries the new number rather than the old one. If the call then did
        /// not start anything the bus is told, because it would otherwise take the next
        /// run somebody starts from the IDE for the confirmation of this one and leave
        /// that run uncounted. One place, so no tool can do half of it.
        /// </summary>
        async Task<Reply> StartRun(HostLink link, Func<Task<OpResult>> call, string success, string note)
        {
            Sessions.Events.StartingRun(link.Id);
            Sessions.Log.Expect(link.Id, Expected.RunStart);

            OpResult result;
            try
            {
                result = await call().ConfigureAwait(false);
            }
            catch
            {
                Sessions.Events.RunNotStarted(link.Id);
                Sessions.Log.Unexpect(link.Id, Expected.RunStart);
                throw;
            }

            var reply = Render.Op(result, success);
            if (!reply.Failed)
            {
                if (result.Pending) Sessions.Log.Expect(link.Id, Expected.RunStart, result.OperationId);
                else Sessions.Log.OperationReported(result.OperationId);
                return result.Pending ? reply.Text : reply.Text + "\n" + note;
            }

            Sessions.Events.RunNotStarted(link.Id);
            Sessions.Log.Unexpect(link.Id, Expected.RunStart);
            return reply;
        }

        [McpServerTool(Name = "status", ReadOnly = true)]
        [Description("Where the debugger is right now: solution, debugger mode (design, run, break), current thread and frame, the top of the call stack, any pending exception, debugged processes, and the pinned watch values. Call this first when you do not know the state; it is cheap and always works. It also lists what has happened recently in this window - stops, exits, builds finishing, debugging starting or ending.")]
        public Task<string> Status(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var status = await link.Debug.GetStatusAsync(ct).ConfigureAwait(false);
                status.InstanceId = link.Id;
                var text = Render.Status(status, Sessions.Events.Generation(link.Id));
                if (status.Observation != null)
                {
                    var observed = status.Observation;
                    text += "\nHost session generation: " + observed.SessionGeneration + ", observed " + observed.TimestampUtc.ToString("O") + ".";
                    text += "\nActive CPU capture: " + (observed.ActiveProfile == null ? "none" : observed.ActiveProfile.CaptureId + " PID " + observed.ActiveProfile.Pid + " " + observed.ActiveProfile.Status);
                    if (observed.LastProfile != null) text += "\nLatest retained capture: " + observed.LastProfile.CaptureId + " PID " + observed.LastProfile.Pid + " generation " + observed.LastProfile.SessionGeneration;
                }

                // status answers for one window, so it accounts for that window only.
                // A stop in another one still reaches the digest, where the instance
                // prefix says which window it was.
                var recent = EventLines.Recent(Sessions.Log.Recent(link.Id, 5), Sessions.ConnectedCount > 1, DateTime.UtcNow);
                if (recent != null) text += "\n" + recent;
                Sessions.Log.MarkSeen(link.Id);

                return text;
            });

        [McpServerTool(Name = "launch")]
        [Description("Start debugging (F5). Blocks until the process is running or has already stopped, then reports which. Returns a confirmed state or a pending operationId after 30 seconds. Query operation_status before retrying.")]
        public Task<string> Launch(
            [Description("Project to launch. Omit to use the solution's startup project.")] string project = null,
            [Description("Command line arguments for the debuggee.")] string args = null,
            [Description("Break at the entry point instead of running to the first breakpoint.")] bool stopAtEntry = false,
            [Description("Run without the debugger attached (Ctrl+F5). Breakpoints will not hit.")] bool noDebug = false,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default,
            [Description("Stable request ID; reuse identical arguments after a timeout.")] string requestId = null)
            => On(instance, ct, async link =>
            {
                var request = new LaunchRequest
                {
                    RequestId = requestId,
                    Project = project,
                    Args = args,
                    StopAtEntry = stopAtEntry,
                    NoDebug = noDebug
                };

                // Ctrl+F5 starts nothing under the debugger, so it begins no run and
                // moves no debuggee that is sitting in break. Saying either would be this
                // tool inventing a state change it did not cause.
                if (noDebug)
                    return Render.Op(await link.Debug.LaunchAsync(request, ct).ConfigureAwait(false), "Launched.");

                Sessions.Log.Expect(link.Id, Expected.RunStart);

                // A retry can return a previous operation, even after reconnecting.
                // Only actual debugger events establish a new launch session.
                var result = await link.Debug.LaunchAsync(request, ct).ConfigureAwait(false);
                var reply = Render.Op(result, "Launched.");
                if (reply.Failed)
                {
                    Sessions.Log.Unexpect(link.Id, Expected.RunStart);
                    return reply;
                }

                // The build in front of it can run for minutes, so the expectation lives
                // as long as the operation does rather than fifteen seconds.
                if (result.Pending) Sessions.Log.Expect(link.Id, Expected.RunStart, result.OperationId);
                else Sessions.Log.OperationReported(result.OperationId);

                return !result.Pending ? reply.Text + "\nIdentities from before this operation's run belong to a different run. " +
                    "A requestId retry returns that same operation; compare the generation number before reusing identities." : reply;
            }, args ?? project);

        [McpServerTool(Name = "attach")]
        [Description("Attach the debugger to a running process, by process id or by a regular expression matched against process names. If the expression matches more than one process the call fails and lists the matches, so a second call can name the right pid.")]
        public Task<string> Attach(
            [Description("Process id to attach to.")] int? pid = null,
            [Description("Regular expression matched against process names, for example 'engine.*'.")] string nameRegex = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, link =>
                StartRun(link,
                    () => link.Debug.AttachAsync(new AttachRequest { Pid = pid, NameRegex = nameRegex }, ct),
                    "Attached.", MaybeNewRun),
                nameRegex ?? pid?.ToString());

        [McpServerTool(Name = "detach")]
        [Description("Detach the debugger and leave the process running. Pass a pid to detach from one process of a multi-process session.")]
        public Task<string> Detach(
            [Description("Process to detach from. Omit to detach from everything.")] int? pid = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                // A detached process runs free, so where it last stopped is not where it
                // is. Without this, wait would answer with that frame and tell the caller
                // to resume something the debugger no longer holds.
                Sessions.Events.MarkSeen(link.Id);
                Sessions.Log.Expect(link.Id, Expected.End);
                var result = await link.Debug.DetachAsync(pid, ct).ConfigureAwait(false);
                var reply = Render.Op(result, "Detached.");
                if (reply.Failed) Sessions.Log.Unexpect(link.Id, Expected.End);
                return reply;
            });

        [McpServerTool(Name = "stop", Destructive = true)]
        [Description("Stop debugging and terminate the debuggee (Shift+F5). Pass a pid to terminate one process of a multi-process session.")]
        public Task<string> Stop(
            [Description("Process to terminate. Omit to stop the whole session.")] int? pid = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                Sessions.Events.MarkSeen(link.Id);
                Sessions.Log.Expect(link.Id, Expected.End);
                var result = await link.Debug.StopAsync(pid, ct).ConfigureAwait(false);
                var reply = Render.Op(result, "Stopped.");
                if (reply.Failed) Sessions.Log.Unexpect(link.Id, Expected.End);
                return reply;
            });

        [McpServerTool(Name = "restart", Destructive = true)]
        [Description("Restart the debugging session: terminate the debuggee and launch it again with the same settings.")]
        public Task<string> Restart(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, link =>
            {
                Sessions.Events.MarkSeen(link.Id);
                Sessions.Log.Expect(link.Id, Expected.End);
                return StartRun(link, () => link.Debug.RestartAsync(ct), "Restarted.", NewRun);
            });

        [McpServerTool(Name = "processes", ReadOnly = true)]
        [Description("List processes. By default only the ones being debugged, which is what you want for a host that spawns workers. Set includeLocal to also list processes available to attach to.")]
        public Task<string> Processes(
            [Description("Also list local processes that could be attached to.")] bool includeLocal = false,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var list = await link.Debug.ProcessesAsync(includeLocal, ct).ConfigureAwait(false);
                if (list == null || list.Count == 0) return "No processes.";

                var sb = new StringBuilder();
                foreach (var p in list.OrderByDescending(p => p.IsDebugged).ThenBy(p => p.Name))
                {
                    sb.Append(p.IsDebugged ? "* " : "  ");
                    sb.Append(p.Pid.ToString().PadLeft(7)).Append("  ").Append(p.Name);
                    if (!string.IsNullOrEmpty(p.Engine)) sb.Append("  [").Append(p.Engine).Append(']');

                    // A remote pid is in no local process list, so without this the row
                    // reads as a process this machine simply cannot find.
                    if (p.IsRemote) sb.Append("  on ").Append(p.Machine ?? "another machine");
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            });

        [McpServerTool(Name = "dump_open")]
        [Description("Open a crash dump (.dmp) for post-mortem inspection. Afterwards every read-only inspection tool works exactly as it does on a live process: stack, threads, eval, memory, registers, modules, triage.")]
        public Task<string> DumpOpen(
            [Description("Full path to the .dmp file.")] string path,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct,
                link => StartRun(link, () => link.Debug.OpenDumpAsync(path, ct), "Dump opened.", NewRun),
                path);
    }
}
