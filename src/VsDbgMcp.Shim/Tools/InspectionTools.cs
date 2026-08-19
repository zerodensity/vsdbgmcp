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
    public sealed class InspectionTools : ToolBase
    {
        public InspectionTools(SessionManager sessions) : base(sessions) { }

        [McpServerTool(Name = "threads", ReadOnly = true)]
        [Description("Every thread in the debug session with the top of its stack, split by process and grouped so threads sitting in the same place collapse into one row. A deadlock or a stalled worker pool is visible in this one call. When a session holds several processes - a launcher and what it starts, a host and its workers - this is where you find the thread ids of the other ones. Only works in break mode.")]
        public Task<string> Threads(
            [Description("How many frames to show per thread. Three is usually enough to tell groups apart.")] int depth = 3,
            [Description("Only threads of this process, by pid or part of its name. Omit for every process in the session.")] string process = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var list = await link.Debug.ThreadsAsync(Math.Max(1, Math.Min(depth, 20)), process, ct)
                    .ConfigureAwait(false);
                return Render.Threads(list);
            });

        [McpServerTool(Name = "stack", ReadOnly = true)]
        [Description("The call stack of one thread, deepest call first. Omit the thread to use the current one. Only works in break mode.")]
        public Task<string> Stack(
            [Description("Thread id, from 'threads'. Any thread in the session, including one in another process. Omit for the current thread.")] int? thread = null,
            [Description("Maximum frames to return.")] int count = 40,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var frames = await link.Debug.StackAsync(thread, Math.Max(1, count), ct).ConfigureAwait(false);
                return Render.Frames(frames);
            });

        [McpServerTool(Name = "select")]
        [Description("Choose the thread, process and stack frame that eval, vars, registers, memory and stack operate on. In a session holding several processes this is how you look at one other than the one that stopped - pass a process by name or pid to switch to it, or a thread id from 'threads'. The choice lasts until the program next runs, because a frame does not survive its thread resuming.")]
        public Task<string> Select(
            [Description("Thread id to switch to. Thread ids are unique across processes, so this alone is enough.")] int? thread = null,
            [Description("Process to switch to, by pid or part of its name, for example 'nosLauncher'. Picks that process's current thread. Ignored when a thread is given.")] string process = null,
            [Description("Frame index within that thread, 0 being the innermost.")] int? frame = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.SelectAsync(thread, frame, process, ct).ConfigureAwait(false);
                return Render.Op(result, "Selected.");
            });

        [McpServerTool(Name = "freeze")]
        [Description("Freeze or thaw a thread. Freezing every thread but one and stepping is how you isolate a race: the suspect runs alone and the interleaving stops changing under you.")]
        public Task<string> Freeze(
            [Description("Thread id.")] int thread,
            [Description("True to freeze, false to thaw.")] bool frozen = true,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.FreezeAsync(thread, frozen, ct).ConfigureAwait(false);
                return Render.Op(result, frozen ? "Frozen." : "Thawed.");
            });

        [McpServerTool(Name = "eval", ReadOnly = true)]
        [Description("Evaluate an expression in the current frame, through the same visualizers the debugger uses, so a std::vector prints as its elements. Function calls are refused by default: the native evaluator would really run them and change the program. Set allowSideEffects only when you intend that.")]
        public Task<string> Eval(
            [Description("Expression in the language of the current frame.")] string expression,
            [Description("Format specifier without the comma: x for hex, d for decimal, su for a unicode string, or '[n]' to show n elements.")] string format = null,
            [Description("Bypass visualizers and show the raw layout, the ',!' specifier.")] bool raw = false,
            [Description("Allow the expression to call functions, which executes code in the debuggee.")] bool allowSideEffects = false,
            [Description("Evaluate on every thread and group the results. Use this to compare one value across a worker pool.")] bool allThreads = false,
            [Description("Frame index to evaluate in. Defaults to the selected frame.")] int frame = 0,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var results = await link.Debug.EvalAsync(new EvalOptions
                {
                    Expression = expression,
                    Format = format,
                    Raw = raw,
                    AllowSideEffects = allowSideEffects,
                    AllThreads = allThreads,
                    FrameIndex = frame
                }, ct).ConfigureAwait(false);
                return Render.Evals(results);
            });

        [McpServerTool(Name = "vars", ReadOnly = true)]
        [Description("Variables in the current frame. Returns one level by default; large containers report that they have children rather than printing thousands of elements. Use expand on the reference to go deeper.")]
        public Task<string> Vars(
            [Description("locals, args, autos, or watch.")] string scope = "locals",
            [Description("How many levels to expand. Keep this small; depth costs tokens fast.")] int depth = 1,
            [Description("Only return variables whose name contains this text.")] string filter = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var nodes = await link.Debug.VarsAsync(scope, Math.Max(1, Math.Min(depth, 5)), filter, ct)
                    .ConfigureAwait(false);
                return Render.Vars(nodes);
            });

        [McpServerTool(Name = "expand", ReadOnly = true)]
        [Description("Expand one variable or expression by the reference that vars or eval returned, so you pay for only the part of a large structure you actually need.")]
        public Task<string> Expand(
            [Description("Reference from a previous vars or eval reply.")] string reference,
            [Description("How many levels to expand.")] int depth = 1,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var nodes = await link.Debug.ExpandAsync(reference, Math.Max(1, Math.Min(depth, 5)), ct)
                    .ConfigureAwait(false);
                return Render.Vars(nodes);
            });

        [McpServerTool(Name = "watch_set")]
        [Description("Pin a set of expressions. Their values come back with every wait and every status, so a debugging loop does not need a handful of eval calls at each stop. Replaces the whole set; pass an empty list to clear it.")]
        public Task<string> WatchSet(
            [Description("Expressions to pin.")] string[] expressions = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var list = expressions ?? Array.Empty<string>();
                var result = await link.Debug.WatchSetAsync(list, ct).ConfigureAwait(false);
                return Render.Op(result, list.Length == 0 ? "Watches cleared." : "Watching " + list.Length + " expressions.");
            });

        [McpServerTool(Name = "memory", ReadOnly = true)]
        [Description("Read raw memory as hex and ASCII. Takes an address or any expression that evaluates to one, so 'buffer' or '&obj' work as well as '0x7ff6...'.")]
        public Task<string> Memory(
            [Description("Address or an expression that yields one.")] string address,
            [Description("How many bytes to read.")] int size = 128,
            [Description("Display width: bytes, words, dwords, or qwords.")] string format = "bytes",
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.MemoryAsync(address, Math.Max(1, Math.Min(size, 4096)), format, ct)
                    .ConfigureAwait(false);
                return Render.Memory(result);
            });

        [McpServerTool(Name = "registers", ReadOnly = true)]
        [Description("CPU registers for the current frame. Native debugging only. Useful when there are no symbols, or when reading the exception record after a crash.")]
        public Task<string> Registers(
            [Description("Register group: general, flags, floating, or sse. Omit for the general set.")] string group = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var list = await link.Debug.RegistersAsync(group, ct).ConfigureAwait(false);
                if (list == null || list.Count == 0) return "No registers available. Native debugging only, and only in break mode.";

                var sb = new StringBuilder();
                var i = 0;
                foreach (var r in list)
                {
                    sb.Append(r.Name.PadRight(8)).Append(r.Value.PadRight(20));
                    if (++i % 3 == 0) sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            });

        [McpServerTool(Name = "disasm", ReadOnly = true)]
        [Description("Disassemble around an address, with source lines interleaved where symbols allow. Omit the address to start at the current instruction pointer. Native debugging only.")]
        public Task<string> Disasm(
            [Description("Address or expression. Omit for the current instruction pointer.")] string address = null,
            [Description("How many instructions to return.")] int count = 24,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var lines = await link.Debug.DisasmAsync(address, Math.Max(1, Math.Min(count, 200)), ct)
                    .ConfigureAwait(false);
                if (lines == null || lines.Count == 0) return "No disassembly available.";

                var sb = new StringBuilder();
                foreach (var l in lines)
                {
                    if (!string.IsNullOrEmpty(l.File))
                        sb.Append("; ").Append(System.IO.Path.GetFileName(l.File)).Append(':').Append(l.Line).AppendLine();
                    sb.Append(l.Address).Append("  ").Append((l.Bytes ?? "").PadRight(18)).AppendLine(l.Text);
                }
                return sb.ToString().TrimEnd();
            });

        [McpServerTool(Name = "modules", ReadOnly = true)]
        [Description("Loaded modules and their symbol state. Check this first when a breakpoint will not bind or a stack is full of addresses instead of function names: the answer is almost always a module with no symbols loaded.")]
        public Task<string> Modules(
            [Description("Only modules whose name contains this text.")] string filter = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var list = await link.Debug.ModulesAsync(filter, ct).ConfigureAwait(false);
                return Render.Modules(list);
            });
    }
}
