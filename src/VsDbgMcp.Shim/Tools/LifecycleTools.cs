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

        [McpServerTool(Name = "status", ReadOnly = true)]
        [Description("Where the debugger is right now: solution, debugger mode (design, run, break), current thread and frame, the top of the call stack, any pending exception, debugged processes, and the pinned watch values. Call this first when you do not know the state; it is cheap and always works.")]
        public Task<string> Status(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var status = await link.Debug.GetStatusAsync(ct).ConfigureAwait(false);
                return Render.Status(status);
            });

        [McpServerTool(Name = "launch")]
        [Description("Start debugging (F5). Blocks until the process is running or has already stopped, then reports which. Does not return before the debuggee exists, so there is no need to poll afterwards.")]
        public Task<string> Launch(
            [Description("Project to launch. Omit to use the solution's startup project.")] string project = null,
            [Description("Command line arguments for the debuggee.")] string args = null,
            [Description("Break at the entry point instead of running to the first breakpoint.")] bool stopAtEntry = false,
            [Description("Run without the debugger attached (Ctrl+F5). Breakpoints will not hit.")] bool noDebug = false,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                Sessions.Events.MarkSeen();
                var result = await link.Debug.LaunchAsync(new LaunchRequest
                {
                    Project = project,
                    Args = args,
                    StopAtEntry = stopAtEntry,
                    NoDebug = noDebug
                }, ct).ConfigureAwait(false);
                return Render.Op(result, "Launched.");
            }, args ?? project);

        [McpServerTool(Name = "attach")]
        [Description("Attach the debugger to a running process, by process id or by a regular expression matched against process names. If the expression matches more than one process the call fails and lists the matches, so a second call can name the right pid.")]
        public Task<string> Attach(
            [Description("Process id to attach to.")] int? pid = null,
            [Description("Regular expression matched against process names, for example 'engine.*'.")] string nameRegex = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.AttachAsync(new AttachRequest { Pid = pid, NameRegex = nameRegex }, ct)
                    .ConfigureAwait(false);
                return Render.Op(result, "Attached.");
            }, nameRegex ?? pid?.ToString());

        [McpServerTool(Name = "detach")]
        [Description("Detach the debugger and leave the process running. Pass a pid to detach from one process of a multi-process session.")]
        public Task<string> Detach(
            [Description("Process to detach from. Omit to detach from everything.")] int? pid = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.DetachAsync(pid, ct).ConfigureAwait(false);
                return Render.Op(result, "Detached.");
            });

        [McpServerTool(Name = "stop", Destructive = true)]
        [Description("Stop debugging and terminate the debuggee (Shift+F5). Pass a pid to terminate one process of a multi-process session.")]
        public Task<string> Stop(
            [Description("Process to terminate. Omit to stop the whole session.")] int? pid = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                Sessions.Events.MarkSeen();
                var result = await link.Debug.StopAsync(pid, ct).ConfigureAwait(false);
                return Render.Op(result, "Stopped.");
            });

        [McpServerTool(Name = "restart", Destructive = true)]
        [Description("Restart the debugging session: terminate the debuggee and launch it again with the same settings.")]
        public Task<string> Restart(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                Sessions.Events.MarkSeen();
                var result = await link.Debug.RestartAsync(ct).ConfigureAwait(false);
                return Render.Op(result, "Restarted.");
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
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.OpenDumpAsync(path, ct).ConfigureAwait(false);
                return Render.Op(result, "Dump opened.");
            }, path);
    }
}
