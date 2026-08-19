using System;
using System.Collections.Generic;
using System.Linq;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// One thing an agent did, as it appears in the panel.
    /// </summary>
    sealed class ActivityEntry
    {
        /// <summary>Increasing, so the panel can add only what it has not drawn yet.</summary>
        public long Id { get; set; }

        public DateTime When { get; set; }
        public string Tool { get; set; }

        /// <summary>The part of the request worth reading, shown next to the name.</summary>
        public string Detail { get; set; }

        /// <summary>What the agent was given back. Null for entries that are not tool calls.</summary>
        public string Result { get; set; }

        public int Milliseconds { get; set; }
        public bool Failed { get; set; }

        public bool HasResult => !string.IsNullOrEmpty(Result);
    }

    /// <summary>
    /// What the agent has been doing, and whether it is allowed to keep doing it.
    ///
    /// A server that drives the debugger from outside needs to be visible from inside:
    /// something running in your IDE that you cannot see or stop is not something to
    /// leave switched on.
    /// </summary>
    static class Activity
    {
        const int Capacity = 200;

        /// <summary>
        /// Long enough to read a whole reply, short enough that two hundred of them do
        /// not sit in memory. A capture returns base64 by the megabyte.
        /// </summary>
        const int MaxResultLength = 4000;

        static readonly LinkedList<ActivityEntry> Entries = new LinkedList<ActivityEntry>();
        static readonly object Gate = new object();
        static long _nextId;

        public static event Action Changed;

        /// <summary>
        /// When set, every tool refuses with an explanation instead of touching the
        /// debugger. The kill switch.
        /// </summary>
        public static bool Paused { get; private set; }

        /// <summary>
        /// When set, Visual Studio is put back behind whatever had the foreground after
        /// an agent-initiated launch or break.
        /// </summary>
        public static bool GuardFocus { get; private set; } = true;

        public static string PipeName { get; set; }
        public static string InstanceId { get; set; }
        public static string Mode { get; set; } = DebugModes.Design;
        public static int Clients { get; set; }

        public const string PausedMessage =
            "Paused in Visual Studio. Someone pressed Pause in the vsdbgmcp panel; " +
            "resume it there to let this session continue.";

        public static void SetPaused(bool paused)
        {
            Paused = paused;
            Record(paused ? "paused" : "resumed", null, 0, false);
        }

        public static void SetGuardFocus(bool guard)
        {
            GuardFocus = guard;
            Raise();
        }

        public static void Record(string tool, string detail, int milliseconds, bool failed, string result = null)
        {
            lock (Gate)
            {
                Entries.AddLast(new ActivityEntry
                {
                    Id = ++_nextId,
                    When = DateTime.Now,
                    Tool = tool,
                    Detail = detail,
                    Result = Cap(result),
                    Milliseconds = milliseconds,
                    Failed = failed
                });

                while (Entries.Count > Capacity) Entries.RemoveFirst();
            }
            Raise();
        }

        static string Cap(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxResultLength) return text;
            return text.Substring(0, MaxResultLength) +
                   Environment.NewLine + Environment.NewLine +
                   "... " + (text.Length - MaxResultLength) + " more characters";
        }

        public static void Clear()
        {
            lock (Gate) Entries.Clear();
            Raise();
        }

        /// <summary>Newest first, which is the order the panel reads in.</summary>
        public static List<ActivityEntry> Recent(int count)
        {
            lock (Gate) return Entries.Reverse().Take(count).ToList();
        }

        /// <summary>
        /// Anything added since the panel last drew, oldest first so it can be inserted
        /// at the top one at a time. Redrawing everything would throw away which rows
        /// the reader had unfolded.
        /// </summary>
        public static List<ActivityEntry> Since(long id)
        {
            lock (Gate) return Entries.Where(e => e.Id > id).ToList();
        }

        static void Raise() => Changed?.Invoke();
    }
}
