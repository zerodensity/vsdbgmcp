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
        public DateTime When { get; set; }
        public string Tool { get; set; }
        public string Detail { get; set; }
        public int Milliseconds { get; set; }
        public bool Failed { get; set; }
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

        static readonly LinkedList<ActivityEntry> Entries = new LinkedList<ActivityEntry>();
        static readonly object Gate = new object();

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

        public static void Record(string tool, string detail, int milliseconds, bool failed)
        {
            lock (Gate)
            {
                Entries.AddLast(new ActivityEntry
                {
                    When = DateTime.Now,
                    Tool = tool,
                    Detail = detail,
                    Milliseconds = milliseconds,
                    Failed = failed
                });

                while (Entries.Count > Capacity) Entries.RemoveFirst();
            }
            Raise();
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

        static void Raise() => Changed?.Invoke();
    }
}
