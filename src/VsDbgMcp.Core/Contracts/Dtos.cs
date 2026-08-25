using System;
using System.Collections.Generic;

namespace VsDbgMcp.Contracts
{
    public sealed class OpResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }

        public static OpResult Good(string message = null) => new OpResult { Ok = true, Message = message };
        public static OpResult Bad(string message) => new OpResult { Ok = false, Message = message };
    }

    public sealed class Frame
    {
        public int Index { get; set; }
        public string Function { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public string Module { get; set; }
        public string Language { get; set; }
        public string Address { get; set; }
    }

    public sealed class ExceptionInfo
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Message { get; set; }
        public string Address { get; set; }
        public bool FirstChance { get; set; }
    }

    public static class StopReason
    {
        public const string Breakpoint = "breakpoint";
        public const string Exception = "exception";
        public const string Step = "step";
        public const string Entry = "entry";
        public const string Pause = "pause";
        public const string Exited = "exited";
        public const string Timeout = "timeout";
    }

    /// <summary>Why execution stopped. The whole point of wait().</summary>
    public sealed class StopEvent
    {
        /// <summary>Monotonic per instance, so a waiter can ask for anything newer than N.</summary>
        public long Seq { get; set; }
        public string InstanceId { get; set; }
        public string Reason { get; set; }

        /// <summary>
        /// Which process stopped. In a session holding a launcher and the editor it
        /// starts, "stopped: breakpoint" on its own is half an answer.
        /// </summary>
        public string ProcessName { get; set; }
        public int Pid { get; set; }
        public int? BreakpointId { get; set; }
        public ExceptionInfo Exception { get; set; }
        public int? ExitCode { get; set; }
        public int ThreadId { get; set; }
        public Frame Frame { get; set; }
        public Dictionary<string, string> Watches { get; set; }
        public string Mode { get; set; }
    }

    /// <summary>
    /// A module the debuggee has just loaded.
    ///
    /// Loading one does not stop execution, so this is not a stop and is never handed
    /// to a plain wait(). It exists because breakpoints in a plugin sit unbound until
    /// the host loads it, and the only other way to learn that is to poll.
    /// </summary>
    public sealed class ModuleLoadEvent
    {
        public string InstanceId { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }

        /// <summary>
        /// Whether symbols came with it. An unbound breakpoint in a module that just
        /// loaded is usually a missing symbol file rather than a wrong line.
        /// </summary>
        public bool SymbolsLoaded { get; set; }

        /// <summary>Why symbols are missing, when they are.</summary>
        public string SymbolStatus { get; set; }
    }

    /// <summary>
    /// One tool call, as it happened, for the panel inside Visual Studio.
    ///
    /// Reported by the shim rather than recorded in the extension because the text an
    /// agent was given only exists on that side. Showing anything else would mean
    /// rendering the same result twice and showing the person something the agent
    /// never saw.
    /// </summary>
    public sealed class CallReport
    {
        public string Tool { get; set; }

        /// <summary>The part of the request worth reading: an expression, a file and line.</summary>
        public string Arguments { get; set; }

        /// <summary>What the agent was given back, capped.</summary>
        public string Result { get; set; }

        public int Milliseconds { get; set; }
        public bool Failed { get; set; }
    }

    public sealed class OutputEvent
    {
        public string InstanceId { get; set; }
        public string Pane { get; set; }
        public string Text { get; set; }
    }

    public sealed class HostStatus
    {
        public string InstanceId { get; set; }
        public WorkspaceInfo Workspace { get; set; }
        public string Mode { get; set; }
        public int CurrentThreadId { get; set; }
        public string CurrentProcessName { get; set; }
        public int CurrentPid { get; set; }

        /// <summary>Set when a caller pinned a thread with select, rather than it being
        /// wherever the debugger last stopped.</summary>
        public bool ThreadWasSelected { get; set; }

        public int CurrentFrameIndex { get; set; }

        /// <summary>
        /// Set when that frame is not the one on top of the stack, saying why. After a
        /// pause the top frame is Visual Studio's own and nothing can be read in it.
        /// </summary>
        public string FrameNote { get; set; }

        public List<Frame> TopFrames { get; set; }
        public ExceptionInfo PendingException { get; set; }
        public List<ProcessInfo> Processes { get; set; }
        public Dictionary<string, string> Watches { get; set; }
        public int BreakpointCount { get; set; }
        public string ActiveConfiguration { get; set; }
        public string StartupProject { get; set; }
    }

    public sealed class ProcessInfo
    {
        public int Pid { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsDebugged { get; set; }
        public string Engine { get; set; }

        /// <summary>
        /// True only when the debug server said the process is somewhere else. A
        /// question the engine would not answer leaves this false, because a session
        /// wrongly called remote sends a reader looking for a machine that does not
        /// exist.
        /// </summary>
        public bool IsRemote { get; set; }

        /// <summary>The machine it is running on, when that is not this one.</summary>
        public string Machine { get; set; }

        /// <summary>How the debugger reaches it: TCP/IP, a named pipe. Null when the engine would not say.</summary>
        public string Transport { get; set; }
    }

    public sealed class ThreadSummary
    {
        public int Id { get; set; }
        public string Name { get; set; }

        /// <summary>The process this thread belongs to, so a thread id can be acted on.</summary>
        public string ProcessName { get; set; }
        public int Pid { get; set; }
        public bool IsCurrent { get; set; }
        public bool IsFrozen { get; set; }
        public int SuspendCount { get; set; }
        public List<Frame> TopFrames { get; set; }
    }

    public static class BreakpointKind
    {
        public const string Location = "location";
        public const string Function = "function";
        public const string Data = "data";
    }

    public sealed class BreakpointRequest
    {
        public string Kind { get; set; } = BreakpointKind.Location;
        public string File { get; set; }
        public int Line { get; set; }
        public string Function { get; set; }
        public string Module { get; set; }

        /// <summary>Address or expression for a data breakpoint.</summary>
        public string Expression { get; set; }
        public int Size { get; set; } = 4;

        public string Condition { get; set; }
        public int HitCountTarget { get; set; }

        /// <summary>Non-null turns the breakpoint into a tracepoint.</summary>
        public string LogMessage { get; set; }

        /// <summary>
        /// Keep this tracepoint's records for trace_read instead of leaving them in the
        /// Debug pane among everything else the program writes.
        /// </summary>
        public bool Collect { get; set; }

        /// <summary>
        /// Log only every Nth hit. The debug engine counts, so the message and its
        /// expressions are built one time in N, which is where a tracepoint's cost is.
        /// </summary>
        public int EveryNthHit { get; set; }

        /// <summary>
        /// Keep at most this many records a second, dropping the rest at the sink. Makes
        /// a flood readable; does nothing for what the tracepoint costs the program.
        /// </summary>
        public int MaxPerSecond { get; set; }
    }

    /// <summary>
    /// One {expr} out of a tracepoint message, evaluated once when the breakpoint was
    /// set. An expression that will not evaluate otherwise announces itself only after
    /// it has logged a thousand records saying so.
    /// </summary>
    public sealed class TraceExpression
    {
        public string Expression { get; set; }
        public string Value { get; set; }

        /// <summary>The evaluator's own words, so "identifier X is undefined" arrives as itself.</summary>
        public string Error { get; set; }
    }

    /// <summary>One record a collected tracepoint produced.</summary>
    public sealed class TraceRecord
    {
        /// <summary>Which record this is for this tracepoint, counting from when collection started.</summary>
        public long Hit { get; set; }

        /// <summary>UTC, stamped when the record reached the extension.</summary>
        public DateTime Time { get; set; }

        public string Text { get; set; }
    }

    public sealed class TraceResult
    {
        public int BreakpointId { get; set; }

        /// <summary>Oldest first.</summary>
        public List<TraceRecord> Records { get; set; }

        /// <summary>Records this tracepoint has produced since collection started.</summary>
        public long Collected { get; set; }

        /// <summary>Records the per-second cap threw away.</summary>
        public long Dropped { get; set; }

        /// <summary>UTC, when this tracepoint started collecting. The span since is what
        /// gives a rate when the records themselves are not timed.</summary>
        public DateTime StartedUtc { get; set; }

        /// <summary>
        /// Whether each record carries the moment it arrived. Visual Studio prints a
        /// tracepoint's record to the Debug pane itself rather than raising it as an
        /// event, so on a Visual Studio whose pane cannot be watched the records are
        /// recovered from the pane afterwards: correct, in order, and undated.
        /// </summary>
        public bool Timed { get; set; } = true;

        /// <summary>Set when there is nothing to return, saying why.</summary>
        public string Message { get; set; }
    }

    public sealed class BreakpointInfo
    {
        public int Id { get; set; }
        public string Kind { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public string Function { get; set; }
        public string Module { get; set; }
        public string Expression { get; set; }
        public int Size { get; set; }
        public string Condition { get; set; }
        public int HitCountTarget { get; set; }
        public int HitCount { get; set; }
        public bool Enabled { get; set; }
        public bool Bound { get; set; }

        /// <summary>
        /// Why an unbound breakpoint is unbound: module not loaded, no symbols,
        /// source mismatch. Reporting "set" for something that will never hit is
        /// worse than reporting nothing.
        /// </summary>
        public string BindState { get; set; }

        /// <summary>What a tracepoint logs, without the marker collection puts in front of it.</summary>
        public string LogMessage { get; set; }

        /// <summary>Records are being kept for this tracepoint, ready for trace_read.</summary>
        public bool Collecting { get; set; }

        /// <summary>Each {expr} in the message, with what it evaluated to when the breakpoint was set.</summary>
        public List<TraceExpression> LogExpressions { get; set; }

        /// <summary>
        /// Set when those expressions carry no result, saying why. Checking them needs
        /// the debuggee stopped where the tracepoint sits; evaluated anywhere else, a
        /// failure says more about where the debugger is than about the expression.
        /// </summary>
        public string LogCheckDeferred { get; set; }
    }

    public sealed class EvalOptions
    {
        public string Expression { get; set; }
        public int? ThreadId { get; set; }

        /// <summary>
        /// Which frame to evaluate in. Null is not frame 0: it means whichever frame the
        /// session is on, and lets a pseudo-frame nobody chose be skipped. A caller who
        /// names a frame gets that frame, failure and all.
        /// </summary>
        public int? FrameIndex { get; set; }

        /// <summary>Native format specifier without the comma: x, d, su, and so on.</summary>
        public string Format { get; set; }

        /// <summary>Bypass natvis visualizers, the ",!" specifier.</summary>
        public bool Raw { get; set; }

        /// <summary>
        /// Off by default. The native evaluator will call functions inside an
        /// expression, which mutates the program being debugged.
        /// </summary>
        public bool AllowSideEffects { get; set; }

        /// <summary>Evaluate on every thread and return one row each.</summary>
        public bool AllThreads { get; set; }

        /// <summary>
        /// Look type names up in this module instead of the one the frame is in. Needed
        /// whenever the expression casts to a type the current module does not define.
        /// </summary>
        public string TypeModule { get; set; }

        public int TimeoutMs { get; set; } = 5000;
    }

    public sealed class EvalResult
    {
        public string Expression { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
        public bool IsValid { get; set; }
        public string Error { get; set; }
        public bool HasChildren { get; set; }
        public string Ref { get; set; }
        public int? ThreadId { get; set; }

        /// <summary>
        /// The frame this was read in, when that is worth saying. A value carries no trace
        /// of where it came from, so one frame's value is otherwise read as another's.
        /// </summary>
        public Frame Frame { get; set; }

        /// <summary>Set when that is not the frame the call started from, saying why it moved.</summary>
        public string FrameNote { get; set; }
    }

    /// <summary>
    /// What vars or expand found, and where it looked.
    ///
    /// An empty list on its own reads as "nothing here", which is a different statement
    /// from "this frame could not be read at all" - and after a pause the second one is
    /// what was true.
    /// </summary>
    public sealed class VarsResult
    {
        public List<VarNode> Nodes { get; set; } = new List<VarNode>();

        /// <summary>
        /// The expression these nodes came from, in the form the engine takes back. Set
        /// when a call reached something the caller did not name, which is the whole point
        /// of picking a container element: the reference comes back instead of being
        /// copied out of an earlier reply by hand.
        /// </summary>
        public string Ref { get; set; }

        public Frame Frame { get; set; }
        public string FrameNote { get; set; }

        /// <summary>Set when there is nothing to return, or something to add, saying what.</summary>
        public string Message { get; set; }
    }

    public sealed class VarNode
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Type { get; set; }
        public bool HasChildren { get; set; }
        public string Ref { get; set; }
        public List<VarNode> Children { get; set; }

        /// <summary>
        /// False when the engine could not read the variable here, which in an optimized
        /// frame usually means the compiler kept nothing to read. Value then holds the
        /// engine's reason instead of a value.
        /// </summary>
        public bool Readable { get; set; } = true;

        /// <summary>
        /// Other variables in the same frame reading the same address. An optimized frame
        /// hands several names one slot, and without this the reply cannot be told apart
        /// from two variables that genuinely hold the same pointer.
        /// </summary>
        public List<string> SameAddressAs { get; set; }
    }

    public sealed class ModuleInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Version { get; set; }
        public string Address { get; set; }
        public bool SymbolsLoaded { get; set; }
        public string SymbolStatus { get; set; }
        public string SymbolPath { get; set; }
        public bool IsUserCode { get; set; }
        public int Order { get; set; }

        /// <summary>
        /// When the binary on disk was last written, ready to show, or empty when the
        /// path could not be read. Source files edited after this no longer line up
        /// with the code that is running.
        ///
        /// This is a file on the machine Visual Studio is running on. When the debuggee
        /// is on another machine there is no such file and this stays empty.
        /// </summary>
        public string Built { get; set; }

        /// <summary>
        /// The time stamped into the image the debuggee actually loaded, ready to show,
        /// or empty when the header does not hold one. This is the field that answers
        /// whether a binary somewhere else is the one that was just built; the file
        /// time above cannot, because that file may be a different copy or absent.
        /// </summary>
        public string ImageBuilt { get; set; }

        /// <summary>How much address space the image occupies, ready to show. Empty when unknown.</summary>
        public string Size { get; set; }

        /// <summary>
        /// A source file with a breakpoint in it that was written after this binary
        /// was built. Null when nothing is known to be newer.
        /// </summary>
        public string NewerSource { get; set; }
    }

    /// <summary>
    /// The modules a 'modules' call is reporting, and how many were loaded before the
    /// filter picked from them. A filtered list on its own reads as everything there
    /// is, which is wrong while a process is still loading its plugins.
    /// </summary>
    public sealed class ModulesResult
    {
        public List<ModuleInfo> Modules { get; set; } = new List<ModuleInfo>();

        /// <summary>How many modules the process had loaded, before the filter.</summary>
        public int LoadedCount { get; set; }

        public string Filter { get; set; }
    }

    /// <summary>
    /// One module's symbol story: what state its symbols are in now, and, when they
    /// are not loaded, every path the engine tried and what it made of each.
    /// </summary>
    public sealed class SymbolResult
    {
        /// <summary>What the caller asked for, so a refusal can quote it back.</summary>
        public string Query { get; set; }

        /// <summary>The module that matched, read after any load attempt. Null when nothing matched.</summary>
        public ModuleInfo Module { get; set; }

        /// <summary>Every module the query matched, when it matched more than one.</summary>
        public List<string> Candidates { get; set; }

        /// <summary>How many modules were loaded when the query ran, so a miss can be sized.</summary>
        public int LoadedCount { get; set; }

        /// <summary>True when a symbol load was asked for and the engine was asked to do it.</summary>
        public bool LoadTried { get; set; }

        /// <summary>
        /// True when the engine turned the load down rather than searching and coming
        /// back empty. The two look the same in the symbol state afterwards and are
        /// different problems.
        /// </summary>
        public bool LoadRefused { get; set; }

        /// <summary>
        /// The Symbol Load Information text: every path the engine tried and why each
        /// one did not answer. Only read while symbols are missing, which is the only
        /// time it has anything to say.
        /// </summary>
        public string SearchInfo { get; set; }

        /// <summary>Why there is nothing else here: no program, no match, or a module the engine will not discuss.</summary>
        public string Message { get; set; }
    }

    public sealed class MemoryResult
    {
        public string Address { get; set; }
        public int Length { get; set; }
        public string Hex { get; set; }
        public string Ascii { get; set; }
        public string Error { get; set; }
        public Frame Frame { get; set; }
        public string FrameNote { get; set; }
    }

    public sealed class RegisterInfo
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Group { get; set; }
    }

    public sealed class DisasmLine
    {
        public string Address { get; set; }
        public string Bytes { get; set; }
        public string Text { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
    }

    /// <summary>
    /// Registers and the frame they came out of.
    ///
    /// An empty list used to be the answer to both "this frame has no registers" and
    /// "nothing here can be read at all", and the second one has somewhere to send the
    /// caller. The frame is named whenever it is not the one the call started from,
    /// because registers read a frame further up are different numbers, not a different
    /// label on the same ones.
    /// </summary>
    public sealed class RegistersResult
    {
        public List<RegisterInfo> Registers { get; set; } = new List<RegisterInfo>();
        public Frame Frame { get; set; }
        public string FrameNote { get; set; }

        /// <summary>Set when there is nothing to return, saying why.</summary>
        public string Message { get; set; }
    }

    /// <summary>Disassembly and the frame whose instruction pointer it started from.</summary>
    public sealed class DisasmResult
    {
        public List<DisasmLine> Lines { get; set; } = new List<DisasmLine>();
        public Frame Frame { get; set; }
        public string FrameNote { get; set; }

        /// <summary>Set when there is nothing to return, saying why.</summary>
        public string Message { get; set; }
    }

    public sealed class ConsoleResult
    {
        public string Text { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int CursorRow { get; set; }
        public int CursorCol { get; set; }
        public string Error { get; set; }
    }

    public sealed class OutputResult
    {
        public string Pane { get; set; }
        public string Text { get; set; }
        public int Lines { get; set; }
        public bool Truncated { get; set; }
    }

    public sealed class CaptureResult
    {
        public string Format { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Base64 { get; set; }
        public string Error { get; set; }
    }

    public sealed class BuildDiagnostic
    {
        public string Severity { get; set; }
        public string Code { get; set; }
        public string Text { get; set; }
        public string File { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string Project { get; set; }
    }

    public sealed class BuildResult
    {
        public bool Succeeded { get; set; }
        public bool Cancelled { get; set; }
        public double ElapsedSeconds { get; set; }
        public int TotalErrors { get; set; }
        public int TotalWarnings { get; set; }
        public List<BuildDiagnostic> Diagnostics { get; set; }
        public string Message { get; set; }
    }

    public sealed class LaunchRequest
    {
        public string Project { get; set; }
        public string Args { get; set; }
        public Dictionary<string, string> Env { get; set; }
        public bool StopAtEntry { get; set; }
        public bool NoDebug { get; set; }
    }

    public sealed class AttachRequest
    {
        public int? Pid { get; set; }
        public string NameRegex { get; set; }
    }

    public static class StepKind
    {
        public const string Into = "into";
        public const string Over = "over";
        public const string Out = "out";
    }

    public sealed class ExceptionSetting
    {
        public string Category { get; set; }
        public string Code { get; set; }
        public string BreakOn { get; set; }
    }
}
