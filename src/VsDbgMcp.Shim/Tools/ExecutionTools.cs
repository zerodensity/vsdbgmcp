using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    [McpServerToolType]
    public sealed class ExecutionTools : ToolBase
    {
        public ExecutionTools(SessionManager sessions) : base(sessions) { }

        [McpServerTool(Name = "wait", ReadOnly = true)]
        [Description("Block until the debuggee stops, then report why: which breakpoint, which exception, a completed step, or the process exiting. This is the correct way to find out that execution stopped - never call status in a loop. Returns the pinned watch values along with the stop.")]
        public async Task<string> Wait(
            [Description("How long to wait before giving up, in seconds. On timeout the program is still running and you can wait again.")] int timeoutSeconds = 30,
            [Description("Instance id. Omit for the session default, or pass 'any' to return as soon as any connected instance stops - useful when debugging two processes in two windows.")] string instance = null,
            CancellationToken ct = default)
        {
            var seconds = Math.Max(1, Math.Min(timeoutSeconds, 600));

            string target;
            if (string.Equals(instance, "any", StringComparison.OrdinalIgnoreCase))
            {
                // Make sure every instance is connected, or a stop over there is never seen here.
                await Sessions.RefreshAsync(true, ct).ConfigureAwait(false);
                target = null;
            }
            else
            {
                try
                {
                    var link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
                    target = link.Id;
                }
                catch (RoutingException ex)
                {
                    return ex.Message;
                }
            }

            var stop = await Sessions.Events.WaitAsync(target, TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
            return Render.Stop(stop);
        }

        [McpServerTool(Name = "go")]
        [Description("Resume execution after a break (F5). Returns as soon as the debuggee is running again; call wait to find out where it stops next.")]
        public Task<string> Go(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                Sessions.Events.MarkSeen();
                var result = await link.Debug.GoAsync(ct).ConfigureAwait(false);
                return Render.Op(result, "Running.");
            });

        [McpServerTool(Name = "pause")]
        [Description("Break into a running debuggee (Ctrl+Alt+Break). Use this when the program is running and you want to see where it is, for example when it seems hung. Blocks until it has actually stopped and reports where, so the frame is safe to inspect afterwards. If it has not stopped within 30 seconds the reply says so and the program is still running; nothing can be read from it until it stops.")]
        public Task<string> Pause(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                Sessions.Events.MarkSeen();
                var result = await link.Debug.PauseAsync(ct).ConfigureAwait(false);
                if (!result.Ok) return Render.Op(result, null);

                // Returning on the request alone is what let a caller read a running
                // process and believe the answer, so the stop has to be confirmed here.
                var stop = await Sessions.Events.WaitAsync(link.Id, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                return stop == null
                    ? "Break requested, but the debuggee has not stopped within 30 seconds. It is still " +
                      "running, so nothing can be read from it yet. Call wait to keep waiting."
                    : Render.Stop(stop);
            });

        [McpServerTool(Name = "step")]
        [Description("Step the debuggee: into a call (F11), over it (F10), or out of the current function (Shift+F11). Blocks until the step completes and reports the new location. Only works in break mode.")]
        public Task<string> Step(
            [Description("into, over, or out.")] string kind = "over",
            [Description("Repeat the step this many times.")] int count = 1,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var normalized = (kind ?? "over").Trim().ToLowerInvariant();
                if (normalized != StepKind.Into && normalized != StepKind.Over && normalized != StepKind.Out)
                    return "kind must be into, over, or out.";

                Sessions.Events.MarkSeen();
                var result = await link.Debug.StepAsync(normalized, Math.Max(1, count), ct).ConfigureAwait(false);
                if (!result.Ok) return Render.Op(result, null);

                var stop = await Sessions.Events.WaitAsync(link.Id, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                return stop == null ? "Step issued; the debuggee has not stopped yet." : Render.Stop(stop);
            }, kind);

        [McpServerTool(Name = "run_to")]
        [Description("Run until execution reaches a file and line, without leaving a breakpoint behind. Blocks until it arrives or the program stops somewhere else first.")]
        public Task<string> RunTo(
            [Description("Full path of the source file.")] string file,
            [Description("Line number, 1 based.")] int line,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                Sessions.Events.MarkSeen();
                var result = await link.Debug.RunToAsync(file, line, ct).ConfigureAwait(false);
                if (!result.Ok) return Render.Op(result, null);

                var stop = await Sessions.Events.WaitAsync(link.Id, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                return stop == null ? "Running; has not reached the location yet." : Render.Stop(stop);
            }, System.IO.Path.GetFileName(file) + ":" + line);

        [McpServerTool(Name = "set_next", Destructive = true)]
        [Description("Move the instruction pointer to another line in the current function without executing what lies between. Skips code or re-runs it. Destructive: it can leave the program in a state it could never reach on its own.")]
        public Task<string> SetNext(
            [Description("Full path of the source file. Must be the file of the current frame.")] string file,
            [Description("Line number, 1 based.")] int line,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.SetNextAsync(file, line, ct).ConfigureAwait(false);
                return Render.Op(result, "Instruction pointer moved.");
            }, System.IO.Path.GetFileName(file) + ":" + line);
    }
}
