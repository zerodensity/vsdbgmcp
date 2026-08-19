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
