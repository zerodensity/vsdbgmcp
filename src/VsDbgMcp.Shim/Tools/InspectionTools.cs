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
            }, process);

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
            }, thread?.ToString());

        [McpServerTool(Name = "select")]
        [Description("Choose the thread, process and stack frame that eval, vars, registers, memory and stack operate on. In a session holding several processes this is how you look at one other than the one that stopped - pass a process by name or pid to switch to it, or a thread id from 'threads'. The choice lasts until the program next runs, because a frame does not survive its thread resuming.")]
        public Task<string> Select(
            [Description("Thread id to switch to. Thread ids are unique across processes, so this alone is enough.")] int? thread = null,
            [Description("Process to switch to, by pid or part of its name, for example 'nosLauncher'. Picks that process's current thread. Ignored when a thread is given.")] string process = null,
            [Description("Frame index within that thread, 0 being the innermost. Naming one pins it: later reads use it and report a failure there rather than moving to a frame you did not ask for.")] int? frame = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.SelectAsync(thread, frame, process, ct).ConfigureAwait(false);
                return Render.Op(result, "Selected.");
            }, process ?? thread?.ToString());

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
            }, thread.ToString());

        [McpServerTool(Name = "eval", ReadOnly = true)]
        [Description("Evaluate an expression in the current frame, through the same visualizers the debugger uses, so a std::vector prints as its elements. Function calls are refused by default: the native evaluator would really run them and change the program. Set allowSideEffects only when you intend that. The evaluator will not run one call inside another and has nothing to bind a reference out-parameter to; where it refuses for either reason the reply says whose limit it is and what to do instead. Pass count to read a raw array one index at a time, which is how you check a container's own view against the memory behind it.")]
        public Task<string> Eval(
            [Description("Expression in the language of the current frame.")] string expression,
            [Description("Format specifier without the comma: x for hex, d for decimal, su for a unicode string, or '[n]' to show n elements.")] string format = null,
            [Description("Bypass visualizers and show the raw layout, the ',!' specifier.")] bool raw = false,
            [Description("Allow the expression to call functions, which executes code in the debuggee.")] bool allowSideEffects = false,
            [Description("Evaluate on every thread and group the results. Use this to compare one value across a worker pool.")] bool allThreads = false,
            [Description("Look type names up in this module, by file name, for example 'MyPlugin.dll'. Pass it when a cast fails with 'identifier X is undefined' because the type belongs to a module other than the one the frame is in. Write the expression the way you would normally; nothing else about it changes. A module that is not loaded is refused rather than passed on, because the qualifier would then name nothing and the type would resolve in the frame's own module. Nothing can check a cast you named no module for: a same-named type in the frame's module answers with a value that looks like data.")] string typeModule = null,
            [Description("Frame index to evaluate in. Omit to use the selected frame, which steps past the pseudo-frame a pause leaves on top of the stack; naming a frame pins this call to it and reports a failure there rather than reading somewhere else.")] int? frame = null,
            [Description("Read this many indexes of the expression instead of the expression itself: (expr)[0], (expr)[1], one row per index. Twenty-eight rows in one call rather than twenty-eight calls, and the reply counts how many of the values were distinct - a repeated block is what a container's view hides. Capped at 256. The run stops early when the first few indexes all fail the same way, and either way the reply says why it is shorter than what was asked for.")] int count = 0,
            [Description("What to read on each element, written the way it follows it: '->Name' for an array of pointers, '.Name' for an array of objects. A bare name is read as '.Name'. Needs count; on its own it is refused rather than dropped.")] string member = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var asked = Math.Max(0, count);
                var conflict = ContainerElement.CannotEnumerate(asked, member, allThreads);
                if (conflict != null) return Reply.Bad(conflict);

                var results = await link.Debug.EvalAsync(new EvalOptions
                {
                    Expression = expression,
                    Format = format,
                    Raw = raw,
                    AllowSideEffects = allowSideEffects,
                    AllThreads = allThreads,
                    TypeModule = typeModule,
                    FrameIndex = frame,
                    Count = Math.Min(asked, ContainerElement.IndexLimit),
                    Member = member
                }, ct).ConfigureAwait(false);

                return Render.Evals(results,
                    asked > 0 ? ContainerElement.Enumeration(asked, member, results) : null);
            }, expression);

        [McpServerTool(Name = "vars", ReadOnly = true)]
        [Description("Variables in the current frame. Returns one level by default; large containers report that they have children rather than printing thousands of elements. Use expand on the reference to go deeper. In an optimized build a variable the compiler kept nothing for is marked as not readable, and variables reading the same address are marked with each other's names, so a slot the compiler handed to two locals does not read as an ordinary value of both. An empty answer says why it is empty rather than leaving it to read as 'no locals here'.")]
        public Task<string> Vars(
            [Description("locals, args, autos, or watch.")] string scope = "locals",
            [Description("How many levels to expand. Keep this small; depth costs tokens fast.")] int depth = 1,
            [Description("Only return variables whose name contains this text.")] string filter = null,
            [Description("Mark variables that read the same address. Costs an extra engine call per variable, so turn it off in a frame with hundreds of locals or when the frame is not optimized.")] bool sharedAddresses = true,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.VarsAsync(scope, Math.Max(1, Math.Min(depth, 5)), filter, sharedAddresses, ct)
                    .ConfigureAwait(false);
                return Render.Vars(result);
            }, scope);

        [McpServerTool(Name = "frame", ReadOnly = true)]
        [Description("Everything one stop can be told about the frame it is in, in a single call: where it is, the source around the line it stopped on, which binary that code came from and whether the file on disk still matches it, the arguments and locals with their types, 'this' expanded one level, and last a list of the values that are not evidence - a local the optimizer kept nothing for, two names sharing one slot, allocator fill, a container claiming to be empty. On landing somewhere unfamiliar this replaces vars, eval(\"this\") and modules in one call; it shows one frame and no callers, so it pairs with stack rather than replacing it. It reads more than vars does and is meant to be called once at a stop rather than in a loop. Only works in break mode.")]
        public Task<string> Frame(
            [Description("Thread to report on, from 'threads'. Any thread in the session, including one in another process. Naming one selects it, the way select would, so later reads stay there. Omit for the current thread.")] int? thread = null,
            [Description("Frame index to report on, 0 being the innermost. Omit to use the selected frame, which steps past the pseudo-frame a pause leaves on top of the stack.")] int? frame = null,
            [Description("How many variables per scope to show. Past this the reply says how many there were and which call reads the rest.")] int maxVariables = 40,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var report = await link.Debug
                    .FrameAsync(thread, frame, Math.Max(1, Math.Min(maxVariables, 200)), ct).ConfigureAwait(false);
                return Render.Frame(report);
            }, thread?.ToString() ?? frame?.ToString());

        [McpServerTool(Name = "expand", ReadOnly = true)]
        [Description("Expand one variable or expression by the reference that vars or eval returned, so you pay for only the part of a large structure you actually need. Pass index or key to pick a single element out of a container: the reply carries that element's own reference, so a deeper call never has to repeat the visualizer's expression for it. A container whose visualizer shows no elements is read a second time with the visualizer off, and the reply reports what the raw layout holds - a map that renders as empty while its own count field is not zero is otherwise indistinguishable from an empty one.")]
        public Task<string> Expand(
            [Description("Reference from a previous vars or eval reply.")] string reference,
            [Description("How many levels to expand.")] int depth = 1,
            [Description("Look type names up in this module, by file name, for example 'MyPlugin.dll'. Pass it when the reference casts to a type that belongs to a module other than the one the frame is in. A module that is not loaded is refused rather than passed on, because the qualifier would then name nothing and the type would resolve in the frame's own module instead.")] string typeModule = null,
            [Description("Element at this position, as the visualizer numbers them: [0], [1], and so on. Exact, and no more work than expanding the container.")] int? index = null,
            [Description("Element whose key renders as this text. A walk down the container's elements comparing each one's rendered key - a map element's 'first', the element itself for a set or vector - not a hash lookup, and it sees only the first 200 elements an expansion reads.")] string key = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.ExpandAsync(reference, Math.Max(1, Math.Min(depth, 5)), typeModule, index, key, ct)
                    .ConfigureAwait(false);
                return Render.Vars(result);
            }, reference);

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
            }, expressions == null ? null : string.Join(", ", expressions));

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
            }, address);

        [McpServerTool(Name = "registers", ReadOnly = true)]
        [Description("CPU registers for the current frame. Native debugging only. Useful when there are no symbols, or when reading the exception record after a crash.")]
        public Task<string> Registers(
            [Description("Register group: general, flags, floating, or sse. Omit for the general set.")] string group = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.RegistersAsync(group, ct).ConfigureAwait(false);
                return Render.Registers(result);
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
                var result = await link.Debug.DisasmAsync(address, Math.Max(1, Math.Min(count, 200)), ct)
                    .ConfigureAwait(false);
                return Render.Disasm(result);
            });

        [McpServerTool(Name = "scratch", Destructive = true)]
        [Description("Allocate a block of the debuggee's own heap and return its address, so a call has somewhere to write. This is what makes an out-parameter possible: the evaluator will not create a temporary, so eval(\"Fill(result)\") has nowhere to put result, and eval(\"Fill(*(Result*)ADDRESS)\") does. Give a type to size the block from sizeof and get the expression to paste back into eval, or give bytes for a raw buffer. Calling the allocator runs code in the program being debugged, and the block is the program's to leak: free it with scratch_free. Every reply lists what is still outstanding.")]
        public Task<string> Scratch(
            [Description("Type to size the block for, and to cast it back to. It has to be one the frame's own module names: the module qualifier is not allowed in a type position, so for a type from elsewhere pass bytes and read the block back with expand(typeModule).")] string type = null,
            [Description("Size in bytes. Omit it when a type is given; give it for a raw buffer, or when the type cannot be sized here.")] int bytes = 0,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                const int cap = 1 << 20;
                if (bytes > cap)
                    return "That is more than a megabyte. Scratch is for arguments, not for buffers of " +
                           "that size; allocate one in the program itself if you really need it.";

                var result = await link.Debug.ScratchAsync(bytes, type, ct).ConfigureAwait(false);
                return Render.Scratch(result);
            }, type ?? (bytes + " bytes"));

        [McpServerTool(Name = "scratch_free", Destructive = true)]
        [Description("Give a scratch block back to the debuggee's allocator. Pass 'all' to release every outstanding block. Blocks are forgotten when the debug session ends, because the heap goes with the process, so this matters while a session is still running.")]
        public Task<string> ScratchFree(
            [Description("Address of the block, as scratch reported it, or 'all'.")] string address = "all",
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.ScratchFreeAsync(address, ct).ConfigureAwait(false);
                return Render.Scratch(result);
            }, address);

        [McpServerTool(Name = "modules", ReadOnly = true)]
        [Description("Loaded modules: symbol state, the time stamped into each loaded image, its load path, size and load address, any source file with a breakpoint in it that is newer than the module it belongs to, and any module the debuggee loaded an older build of than the one beside its matched PDB. Check this first when a breakpoint will not bind or a stack is full of addresses instead of function names: the answer is almost always a module with no symbols loaded, a source edited since the module was built, or a deployed copy the rebuild never replaced. Filter to a few modules to see each one's full identity, which is what answers whether a binary deployed elsewhere is the one that was just built. A filtered answer says how many modules it picked from, because more load while the program runs. Modules belong to a process, so this follows select the way eval does and names the process the list is from.")]
        public Task<string> Modules(
            [Description("Only modules whose name contains this text.")] string filter = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.ModulesAsync(filter, ct).ConfigureAwait(false);
                return Render.Modules(result);
            }, filter);

        [McpServerTool(Name = "symbols")]
        [Description("Why one module's symbols are not loaded, and, with load set, an attempt to load them. The report is the Modules window's Symbol Load Information: every path the engine tried and what each one turned out to be, which is the only place a PDB that is present but does not match the binary says so. Loading is the Modules window's Load Symbols, and it does not survive the module unloading and loading again, because the Include/Exclude symbol setting is applied afresh on every load. A module belongs to a process, so this follows select the way modules and eval do.")]
        public Task<string> Symbols(
            [Description("Module name or part of one, for example 'engine.dll'. An exact name wins over a partial match.")] string module,
            [Description("Try to load its symbols first. Leave this off to only read what the debugger already tried.")] bool load = false,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.SymbolsAsync(module, load, ct).ConfigureAwait(false);
                return Render.Symbols(result);
            }, module);
    }
}
