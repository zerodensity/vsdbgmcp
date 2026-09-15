using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// One line per thing that happened, in the words the model acts on.
    ///
    /// The digest at the top of a reply, the Recent section of status, the --follow
    /// stream and anything pushed to a client later all read from here. One
    /// vocabulary, so a line recognised in one place is the same line everywhere, and
    /// a model watching a monitor can tell that a digest line is the event it already
    /// saw rather than a second one.
    /// </summary>
    public static class EventLines
    {
        /// <summary>Enough to say what happened without becoming the reply.</summary>
        const int DigestLines = 6;

        public static string Line(LogEntry entry, bool withInstance, DateTime now)
        {
            if (entry == null) return "";

            var sb = new StringBuilder();

            var age = Age(now - entry.At);
            if (age != null) sb.Append(age).Append(": ");

            // These two name the window inside the line, so prefixing would say it twice.
            if (withInstance && entry.Kind != EventKind.InstanceGone && entry.Kind != EventKind.SolutionChanged)
                sb.Append('[').Append(entry.InstanceId).Append("] ");

            sb.Append(Body(entry));
            return sb.ToString();
        }

        /// <summary>
        /// What goes at the top of a reply. The header says whether what follows was
        /// read from a state that has since moved, because that changes how the rest of
        /// the reply should be read.
        /// </summary>
        public static string Digest(IReadOnlyList<LogEntry> entries, bool withInstance, DateTime now)
        {
            if (entries == null || entries.Count == 0) return null;

            var sb = new StringBuilder();
            sb.Append(entries.Any(e => e.Invalidates)
                ? "Since your last call, state changed:"
                : "Since your last call:").Append('\n');

            foreach (var entry in entries.Take(DigestLines))
                sb.Append("- ").Append(Line(entry, withInstance, now)).Append('\n');

            if (entries.Count > DigestLines)
                sb.Append("- and ").Append(entries.Count - DigestLines)
                  .Append(" more; call status for the current state").Append('\n');

            // A blank line to close the block. Content blocks arrive joined, and how a
            // client joins them is not this server's to rely on; without the break the
            // first line of the tool's own answer reads as one more event.
            return sb.Append('\n').ToString();
        }

        /// <summary>The same lines under status, where they are history rather than news.</summary>
        public static string Recent(IReadOnlyList<LogEntry> entries, bool withInstance, DateTime now)
        {
            if (entries == null || entries.Count == 0) return null;

            var sb = new StringBuilder("Recent:");
            foreach (var entry in entries)
                sb.Append("\n- ").Append(Line(entry, withInstance, now));
            return sb.ToString();
        }

        static string Body(LogEntry entry)
        {
            switch (entry.Kind)
            {
                case EventKind.Stopped: return Stopped(entry.Stop);
                case EventKind.Exited: return Exited(entry.Stop);
                case EventKind.DebuggingStarted: return "debugging started";
                case EventKind.DebuggingEnded: return "debugging ended";
                case EventKind.OperationDone: return Operation(entry.Operation);
                case EventKind.InstanceGone: return "Visual Studio " + entry.InstanceId + " closed";
                case EventKind.SolutionChanged: return "solution changed in Visual Studio " + entry.InstanceId;
                default: return entry.Kind.ToString();
            }
        }

        static string Stopped(StopEvent stop)
        {
            if (stop == null) return "stopped";

            var sb = new StringBuilder();
            switch (stop.Reason)
            {
                case StopReason.Breakpoint:
                    sb.Append("stopped at breakpoint");
                    if (stop.BreakpointId.HasValue) sb.Append(' ').Append(stop.BreakpointId.Value);
                    sb.Append(',');
                    break;
                case StopReason.Exception:
                    sb.Append("stopped on ").Append(Exception(stop.Exception));
                    break;
                case StopReason.Step: sb.Append("stopped after step"); break;
                case StopReason.Pause: sb.Append("paused"); break;
                case StopReason.Entry: sb.Append("stopped at entry point"); break;
                default: sb.Append("stopped: ").Append(stop.Reason); break;
            }

            var where = Where(stop.Frame);
            if (where != null) sb.Append(stop.Reason == StopReason.Breakpoint ? " " : " at ").Append(where);
            else if (stop.Reason == StopReason.Breakpoint) sb.Length -= 1;

            if (stop.ThreadId != 0) sb.Append(" (thread ").Append(stop.ThreadId).Append(')');
            return sb.ToString();
        }

        static string Where(Frame frame)
        {
            if (frame == null) return null;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(frame.File))
                parts.Add(Path.GetFileName(frame.File) + ":" + frame.Line.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(frame.Function)) parts.Add("in " + frame.Function);

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        static string Exception(ExceptionInfo e)
        {
            if (e == null) return "an exception";
            var name = e.Name ?? e.Code ?? "an exception";
            return e.Name != null && e.Code != null ? name + " (" + e.Code + ")" : name;
        }

        static string Exited(StopEvent stop)
        {
            var sb = new StringBuilder("exited");
            if (stop?.ExitCode != null) sb.Append(" with code ").Append(stop.ExitCode.Value.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(stop?.ProcessName)) sb.Append(" (").Append(stop.ProcessName).Append(')');
            return sb.ToString();
        }

        /// <summary>
        /// What an operation came to, in the same five words operation_status uses -
        /// they are read side by side, so one classifier decides both. Only a real
        /// success is worded as one: an outcome nobody established says so instead of
        /// reading as a launch that worked.
        /// </summary>
        static string Operation(OperationInfo operation)
        {
            if (operation == null) return "an operation finished";

            var tail = " (operation " + operation.OperationId + ")";
            var kind = operation.Kind ?? "operation";
            var outcome = OperationOutcome.Of(operation);

            // These three read the same whatever was asked for, and none of them may be
            // worded as the work having happened.
            if (outcome == OperationOutcome.Cancelled) return kind + " cancelled" + tail;
            if (outcome == OperationOutcome.Unknown) return kind + " outcome unknown" + Because(operation) + tail;
            if (outcome == OperationOutcome.Pending) return kind + " still running" + tail;

            var failed = outcome == OperationOutcome.Failed;

            switch (operation.Kind)
            {
                case "build":
                    var build = operation.Build;
                    if (build == null) return (failed ? "build failed" : "build done") + Because(operation) + tail;
                    if (build.TotalErrors == 0 && build.TotalWarnings == 0) return "build succeeded" + tail;
                    return "build done: " + Count(build.TotalErrors, "error") +
                        (build.TotalWarnings > 0 ? ", " + Count(build.TotalWarnings, "warning") : "") + tail;

                case "launch":
                    if (failed) return "launch failed" + Because(operation) + tail;
                    return "launch succeeded" +
                        (string.IsNullOrEmpty(operation.Executable) ? "" : ": " + operation.Executable) + tail;

                case "breakpoint":
                    var bp = operation.Breakpoint;
                    if (bp == null) return "breakpoint " + (failed ? "failed" : "set") + tail;
                    if (bp.Bound)
                        return "breakpoint " + bp.Id + " bound" +
                            (string.IsNullOrEmpty(bp.File) ? "" : " at " + Path.GetFileName(bp.File) + ":" + bp.Line) + tail;
                    return "breakpoint " + bp.Id + " could not bind" +
                        (string.IsNullOrEmpty(bp.BindState) ? "" : ": " + bp.BindState) + tail;

                default:
                    // State cannot be null here: a null one classifies as unknown and was
                    // answered three lines up.
                    return kind + " done: " + operation.State + tail;
            }
        }

        /// <summary>How long a reason gets before it stops being one line.</summary>
        const int ReasonLength = 100;

        /// <summary>
        /// Why, in the room one line has. A host message runs to a paragraph (the one
        /// that retires unresolved work is about 110 characters on its own) and this
        /// goes at the top of a reply beside everything else that happened. The whole
        /// text is in operation_status.
        /// </summary>
        static string Because(OperationInfo operation)
        {
            var message = operation.Message;
            if (string.IsNullOrEmpty(message)) return "";

            message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (message.Length == 0) return "";
            if (message.Length > ReasonLength) message = message.Substring(0, ReasonLength - 3).TrimEnd() + "...";
            return ": " + message;
        }

        static string Count(int n, string noun) =>
            n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

        /// <summary>
        /// How long ago, when that is worth saying. Under a minute it is "just now",
        /// because the reply is being read as it is written, and a time on every line
        /// would be noise on all of them.
        /// </summary>
        static string Age(TimeSpan since)
        {
            if (since < TimeSpan.FromMinutes(1)) return null;
            if (since < TimeSpan.FromHours(1)) return (int)since.TotalMinutes + " min ago";
            return (int)since.TotalHours + " h ago";
        }
    }
}
