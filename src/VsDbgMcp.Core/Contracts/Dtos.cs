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
        public int FrameIndex { get; set; }

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
    }

    public sealed class MemoryResult
    {
        public string Address { get; set; }
        public int Length { get; set; }
        public string Hex { get; set; }
        public string Ascii { get; set; }
        public string Error { get; set; }
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
