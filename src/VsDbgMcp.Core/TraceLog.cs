using System;
using System.Collections.Generic;
using System.Linq;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    /// <summary>
    /// Where a collected tracepoint's records are kept, one buffer per breakpoint.
    ///
    /// Visual Studio writes tracepoint records to the Debug pane, mixed in with
    /// everything else the program logs. On a hot path that makes both unreadable, and
    /// reading cadence out of how two streams interleave is guesswork. So a collected
    /// tracepoint marks its message with the breakpoint's id, the event sink pulls the
    /// marked records back out of the stream, and they land here with the time they
    /// arrived and which hit they were.
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
        /// </summary>
        public bool Add(int breakpointId, string text, DateTime whenUtc)
        {
            if (breakpointId <= 0) return false;

            lock (_gate)
            {
                Stream stream;
                if (!_streams.TryGetValue(breakpointId, out stream)) return false;

                // Counted before the cap, so a record's hit number stays the number of
                // the hit that produced it and a gap in the numbers shows what was lost.
                stream.Arrived++;

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
                result.StartedUtc = stream.StartedUtc;
                result.Timed = stream.Timed;

                var skip = tail > 0 && stream.Records.Count > tail ? stream.Records.Count - tail : 0;
                foreach (var record in stream.Records)
                {
                    if (skip-- > 0) continue;
                    result.Records.Add(record);
                }

                if (result.Records.Count == 0)
                    result.Message = "Nothing collected yet: the tracepoint has not been hit since it was set.";
            }

            return result;
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
                .Select(s => "#" + s.Key + " (" + s.Value.Arrived + " records)")
                .ToArray()) + ".";
        }

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

        public static string Mark(int breakpointId, string message) =>
            Open + breakpointId + "] " + (message ?? "");

        /// <summary>
        /// Takes the marker back off. Text that never carried one comes back unchanged
        /// with an id of zero, so no record is lost to a parse that did not match.
        /// </summary>
        public static string Unmark(string text, out int breakpointId)
        {
            breakpointId = 0;
            if (string.IsNullOrEmpty(text) || !text.StartsWith(Open, StringComparison.Ordinal)) return text;

            var end = Open.Length;
            while (end < text.Length && text[end] >= '0' && text[end] <= '9') end++;
            if (end == Open.Length || end >= text.Length || text[end] != ']') return text;

            int id;
            if (!int.TryParse(text.Substring(Open.Length, end - Open.Length), out id)) return text;

            breakpointId = id;
            var body = text.Substring(end + 1);
            return body.Length > 0 && body[0] == ' ' ? body.Substring(1) : body;
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
