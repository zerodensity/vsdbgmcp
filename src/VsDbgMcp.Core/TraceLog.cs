using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    /// <summary>
    /// Where a collected tracepoint's records are kept, one buffer per breakpoint.
    ///
    /// Visual Studio writes tracepoint records to the Debug pane, mixed in with
    /// everything else the program logs. On a hot path that makes both unreadable, and
    /// reading cadence out of how two streams interleave is guesswork. So a collected
    /// tracepoint wraps its message in a marker carrying the breakpoint's id, the event
    /// sink pulls the marked records back out of the stream, and they land here with the
    /// time they arrived and which hit they were.
    ///
    /// Each buffer keeps the newest records and no more, because the callback filling
    /// it can run tens of times a second for as long as the program does.
    /// </summary>
    public sealed class TraceLog
    {
        /// <summary>Records kept per breakpoint. Forty seconds of a 50 Hz callback.</summary>
        public const int Capacity = 2000;

        /// <summary>Longest record kept. A visualizer summary of a large object has no natural limit.</summary>
        public const int MaxRecordLength = 1000;

        sealed class Stream
        {
            public readonly Queue<TraceRecord> Records = new Queue<TraceRecord>();
            public long Arrived;
            public long Dropped;
            public long CutShort;
            public int MaxPerSecond;
            public DateTime SecondStarted;
            public int InThisSecond;
            public DateTime StartedUtc;

            /// <summary>
            /// False once a record has arrived without a time. Records read back out of
            /// the Debug pane are in the order they happened and nothing more, so a
            /// stream carrying one cannot claim to know when any of them arrived.
            /// </summary>
            public bool Timed = true;
        }

        readonly Dictionary<int, Stream> _streams = new Dictionary<int, Stream>();
        readonly object _gate = new object();

        /// <summary>
        /// Whether records are picked up from the Debug pane as they land, rather than
        /// recovered from its text when somebody reads. An empty stream means different
        /// things in the two cases, so the reply says which one it is.
        /// </summary>
        public bool PaneWatched { get; set; }

        /// <summary>
        /// Begins collecting for a breakpoint, throwing away anything kept for it
        /// before. Setting a tracepoint again means measuring from now.
        /// </summary>
        public void Start(int breakpointId, int maxPerSecond, DateTime startedUtc)
        {
            if (breakpointId <= 0) return;
            lock (_gate)
            {
                _streams[breakpointId] = new Stream
                {
                    MaxPerSecond = maxPerSecond > 0 ? maxPerSecond : 0,
                    StartedUtc = startedUtc
                };
            }
        }

        public void Forget(int breakpointId)
        {
            lock (_gate) _streams.Remove(breakpointId);
        }

        public bool IsCollecting(int breakpointId)
        {
            if (breakpointId <= 0) return false;
            lock (_gate) return _streams.ContainsKey(breakpointId);
        }

        /// <summary>
        /// Takes one record, and says whether it belonged here. A record for a
        /// breakpoint that is not collecting is not this buffer's, and the caller
        /// should let it through as ordinary output rather than lose it.
        ///
        /// <paramref name="cutShort"/> is for a record that arrived without the end
        /// marker, which means the debugger stopped building the message partway.
        /// </summary>
        public bool Add(int breakpointId, string text, DateTime whenUtc, bool cutShort)
        {
            if (breakpointId <= 0) return false;

            lock (_gate)
            {
                Stream stream;
                if (!_streams.TryGetValue(breakpointId, out stream)) return false;

                // Counted before the cap, so a record's hit number stays the number of
                // the hit that produced it and a gap in the numbers shows what was lost.
                stream.Arrived++;
                if (cutShort) stream.CutShort++;

                if (whenUtc == default(DateTime)) stream.Timed = false;

                // The cap makes the stream readable. It cannot make the tracepoint
                // cheaper: the program has already paid for this record by the time it
                // reaches here, so what the cap throws away is evidence, not overhead.
                if (stream.MaxPerSecond > 0)
                {
                    if (whenUtc - stream.SecondStarted >= TimeSpan.FromSeconds(1))
                    {
                        stream.SecondStarted = whenUtc;
                        stream.InThisSecond = 0;
                    }
                    if (stream.InThisSecond >= stream.MaxPerSecond)
                    {
                        stream.Dropped++;
                        return true;
                    }
                    stream.InThisSecond++;
                }

                stream.Records.Enqueue(new TraceRecord
                {
                    Hit = stream.Arrived,
                    Time = whenUtc,
                    Text = Shorten(text)
                });

                while (stream.Records.Count > Capacity) stream.Records.Dequeue();
                return true;
            }
        }

        public TraceResult Read(int breakpointId, int tail)
        {
            var result = new TraceResult { BreakpointId = breakpointId, Records = new List<TraceRecord>() };

            lock (_gate)
            {
                Stream stream;
                if (!_streams.TryGetValue(breakpointId, out stream))
                {
                    result.Message = "Tracepoint #" + breakpointId + " is not collecting. " + Elsewhere();
                    return result;
                }

                result.Collected = stream.Arrived;
                result.Dropped = stream.Dropped;
                result.CutShort = stream.CutShort;
                result.StartedUtc = stream.StartedUtc;
                result.Timed = stream.Timed;

                var skip = tail > 0 && stream.Records.Count > tail ? stream.Records.Count - tail : 0;
                foreach (var record in stream.Records)
                {
                    if (skip-- > 0) continue;
                    result.Records.Add(record);
                }

                if (result.Records.Count == 0)
                {
                    result.Message = Empty(breakpointId, stream);
                    if (stream.Arrived == 0) result.Settles = Settles;
                }
            }

            return result;
        }

        /// <summary>
        /// The one experiment that separates the two cases an empty stream cannot. It
        /// says what it proves and no more: a tracepoint that logs nothing where another
        /// one does is a tracepoint that is not logging, which is not the same as a line
        /// that is not running.
        /// </summary>
        const string Settles =
            "What settles it: set a collecting tracepoint on a line you have already watched the debugger " +
            "stop on. If that one collects and this one does not, the difference is this tracepoint rather " +
            "than the pane or the pipeline - check that it is enabled, bound, and carries no condition or " +
            "hit filter, before concluding the line is not running.";

        /// <summary>
        /// What an empty stream establishes, which is less than it looks like. It is
        /// what a tracepoint nobody reached looks like and what a tracepoint whose
        /// records went somewhere else looks like, and this buffer cannot tell those
        /// apart. So it says both, reports the evidence it does have, and names what
        /// would settle it. Called with the lock already held.
        /// </summary>
        string Empty(int breakpointId, Stream stream)
        {
            // Records that arrived and were then thrown away are not records that never
            // came. The cap keeps the first record of every second, so an empty buffer
            // that collected something cannot happen today; if it ever can, everything
            // below it would be a wrong answer rather than a missing one.
            if (stream.Arrived > 0)
            {
                return "Tracepoint #" + breakpointId + " has collected " + stream.Arrived + " records, " +
                       stream.Dropped + " of them dropped by the per-second cap, and none of them is in " +
                       "the buffer. Setting maxPerSecond to 0 keeps every record.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("Tracepoint #" + breakpointId + " has collected nothing. That is what a tracepoint " +
                          "that was never hit looks like, and also what a tracepoint whose records never " +
                          "reached this buffer looks like. Nothing here tells those apart.");

            sb.Append(PaneWatched
                ? "The Debug pane is being watched as it fills, so a record written to it arrives here at once."
                : "The Debug pane is not being watched here, so records are recovered from its text when " +
                  "you read.");
            sb.AppendLine(" A record that never reached that pane is invisible from here either way.");

            var elsewhere = _streams
                .Where(s => s.Key != breakpointId && s.Value.Arrived > 0)
                .OrderBy(s => s.Key)
                .Select(s => "#" + s.Key + " (" + Records(s.Value.Arrived) + ")")
                .ToArray();

            sb.Append(elsewhere.Length > 0
                ? "Records have reached this buffer from " + string.Join(", ", elsewhere) + ", so that path " +
                  "does work. What is left is this line not being reached, or this tracepoint not logging."
                : "No tracepoint here has collected a record at all, so nothing rules that path in or out.");

            return sb.ToString();
        }

        /// <summary>
        /// Names the tracepoints that are collecting, so an id that resolved to nothing
        /// is one call away from the right one. Called with the lock already held.
        /// </summary>
        string Elsewhere()
        {
            if (_streams.Count == 0)
                return "Nothing is. Set one with bp_set(logMessage: ..., collect: true).";

            return "Collecting now: " + string.Join(", ", _streams
                .OrderBy(s => s.Key)
                .Select(s => "#" + s.Key + " (" + Records(s.Value.Arrived) + ")")
                .ToArray()) + ".";
        }

        static string Records(long count) => count + (count == 1 ? " record" : " records");

        static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var trimmed = text.TrimEnd('\r', '\n');
            return trimmed.Length <= MaxRecordLength
                ? trimmed
                : trimmed.Substring(0, MaxRecordLength) + " ...";
        }
    }

    /// <summary>
    /// The marker that tells a collected tracepoint's records from the program's own
    /// output, and the expressions inside a tracepoint message.
    /// </summary>
    public static class TraceMessage
    {
        // Short, because it goes in front of every record and Visual Studio still shows
        // it in the Debug pane. Nothing a program prints starts with this.
        const string Open = "[vsdbg:";

        // And after it. Both ends are literal text, so the debugger copies them whatever
        // the {expr} parts of the message do. A record that arrives at all therefore
        // proves the path from the tracepoint to this buffer, and one that arrives
        // without its end was cut short before the message was finished.
        const string Close = "[/vsdbg]";

        public static string Mark(int breakpointId, string message) =>
            Open + breakpointId + "] " + (string.IsNullOrEmpty(message) ? "" : message + " ") + Close;

        /// <summary>
        /// Takes the marker back off, and says whether the record carried its end. Text
        /// that never carried a marker comes back unchanged with an id of zero, so no
        /// record is lost to a parse that did not match.
        /// </summary>
        public static string Unmark(string text, out int breakpointId, out bool cutShort)
        {
            breakpointId = 0;
            cutShort = false;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Open, StringComparison.Ordinal)) return text;

            var end = Open.Length;
            while (end < text.Length && text[end] >= '0' && text[end] <= '9') end++;
            if (end == Open.Length || end >= text.Length || text[end] != ']') return text;

            int id;
            if (!int.TryParse(text.Substring(Open.Length, end - Open.Length), out id)) return text;

            breakpointId = id;
            var body = text.Substring(end + 1);
            if (body.Length > 0 && body[0] == ' ') body = body.Substring(1);

            var trimmed = body.TrimEnd();
            if (!trimmed.EndsWith(Close, StringComparison.Ordinal))
            {
                cutShort = true;
                return trimmed;
            }

            return trimmed.Substring(0, trimmed.Length - Close.Length).TrimEnd();
        }

        /// <summary>
        /// Whether a line of the pane holds a record that is finished. The reader cannot
        /// know whether the pane's last line is complete or still being written, and a
        /// record's end marker is what answers that.
        /// </summary>
        public static bool Finished(string line)
        {
            Unmark(line, out var breakpointId, out var cutShort);
            return breakpointId > 0 && !cutShort;
        }

        /// <summary>
        /// Every {expr} in a tracepoint message, in the order it appears and once each.
        /// A backslash in front of a brace makes it a literal, which is how a message
        /// shows one.
        /// </summary>
        public static List<string> Expressions(string message)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(message)) return found;

            for (var i = 0; i < message.Length; i++)
            {
                if (message[i] == '\\' && i + 1 < message.Length &&
                    (message[i + 1] == '{' || message[i + 1] == '}'))
                {
                    i++;
                    continue;
                }

                if (message[i] != '{') continue;

                var depth = 1;
                var start = i + 1;
                var end = start;
                while (end < message.Length && depth > 0)
                {
                    if (message[end] == '{') depth++;
                    else if (message[end] == '}') depth--;
                    end++;
                }

                // An unclosed brace is the rest of the message, not an expression.
                if (depth != 0) break;

                var expression = message.Substring(start, end - start - 1).Trim();
                if (expression.Length > 0 && !found.Contains(expression)) found.Add(expression);
                i = end - 1;
            }

            return found;
        }
    }
}
