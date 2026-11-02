using System;
using System.Runtime.InteropServices;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Keeps Visual Studio from stealing the foreground when an agent drives it.
    ///
    /// Launching and breaking both bring the IDE forward, which is right when a person
    /// pressed F5 and wrong when something else did. The difference matters: a guard
    /// that cannot tell the two apart takes focus away from the person every time they
    /// step, which is worse than not having one.
    ///
    /// So it arms only on an agent command that resumes execution, fires at most once,
    /// and forgets itself shortly afterwards. A stop nobody asked for through this
    /// server - someone pressing F10 in the IDE - finds it disarmed and is left alone.
    ///
    /// The foreground is handed back from a thread of its own, never the UI thread.
    /// Activating a window waits on the thread that owns it, and in break mode the
    /// debugger has suspended every thread of the debugged processes: handing focus to
    /// a debuggee's window from the UI thread froze Visual Studio for good.
    /// </summary>
    static class FocusGuard
    {
        /// <summary>
        /// How long an arming stays good for. Long enough for a launch to reach its
        /// first breakpoint, short enough that it cannot ambush a person later.
        /// </summary>
        static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

        static IntPtr _previous;
        static int _previousPid;
        static DateTime _armedAt = DateTime.MinValue;

        static bool Armed => _previous != IntPtr.Zero && DateTime.UtcNow - _armedAt < Window;

        /// <summary>
        /// Called by the agent commands that make Visual Studio come forward - launching,
        /// resuming, stepping. Anything belonging to another process is worth putting
        /// back; if Visual Studio already had the foreground, the agent is not taking it
        /// from anyone.
        /// </summary>
        public static void Arm()
        {
            Disarm();
            if (!Activity.GuardFocus) return;

            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return;

            GetWindowThreadProcessId(foreground, out var pid);
            if (pid == CurrentProcessId) return;

            _previous = foreground;
            _previousPid = unchecked((int)pid);
            _armedAt = DateTime.UtcNow;
        }

        public static void Disarm()
        {
            _previous = IntPtr.Zero;
            _previousPid = 0;
            _armedAt = DateTime.MinValue;
        }

        /// <summary>
        /// Called on the UI thread when the debugger's mode changes. Does nothing unless
        /// an agent command armed it, and does no window work on the calling thread.
        ///
        /// A launch brings the IDE forward twice: once when the session starts and again
        /// when it next stops. So entering run mode hands the foreground back but stays
        /// armed for the stop that follows; reaching break or design mode is the end of
        /// what the agent asked for, and disarms.
        /// </summary>
        public static void Restore(string mode, Func<int, bool> isDebugged)
        {
            if (!Armed) { Disarm(); return; }

            var target = _previous;
            var owner = _previousPid;
            if (mode != DebugModes.Run) Disarm();

            if (!Activity.GuardFocus) return;
            if (!ShouldHandBack(mode, isDebugged(owner))) return;

            var thread = new System.Threading.Thread(() => HandBack(target))
            {
                IsBackground = true,
                Name = "vsdbgmcp focus"
            };
            thread.Start();
        }

        /// <summary>
        /// Whether the window that had the foreground can take it back. A debugged
        /// process's window cannot while the program is stopped: its threads are
        /// suspended, so it could neither take the focus nor draw itself with it.
        /// </summary>
        internal static bool ShouldHandBack(string mode, bool ownerIsDebugged) =>
            mode == DebugModes.Run || !ownerIsDebugged;

        static void HandBack(IntPtr target)
        {
            if (!IsWindow(target)) return;

            // Visual Studio did not come forward, so there is nothing to undo. While it
            // has the foreground, any of its threads may give it away.
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid != CurrentProcessId) return;

            SetForegroundWindow(target);
        }

        static readonly uint CurrentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    }
}
