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
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        static IntPtr _previous;
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
            _armedAt = DateTime.UtcNow;
        }

        public static void Disarm()
        {
            _previous = IntPtr.Zero;
            _armedAt = DateTime.MinValue;
        }

        /// <summary>
        /// Called after Visual Studio has had its chance to come forward. Does nothing
        /// unless an agent command armed it and Visual Studio actually took the
        /// foreground.
        ///
        /// A launch brings the IDE forward twice: once when the session starts and again
        /// when it next stops. So entering run mode restores the foreground but stays
        /// armed for the stop that follows; reaching break or design mode is the end of
        /// what the agent asked for, and disarms.
        /// </summary>
        public static void Restore(string mode)
        {
            if (!Armed) { Disarm(); return; }

            var target = _previous;
            if (mode != DebugModes.Run) Disarm();

            if (!Activity.GuardFocus || !IsWindow(target)) return;

            // Visual Studio did not come forward, so there is nothing to undo.
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var pid);
            if (pid != CurrentProcessId) return;

            // Windows only grants the foreground to a thread that already has it, so
            // borrow the input queue of whoever holds it for the length of the call.
            var us = GetCurrentThreadId();
            var them = GetWindowThreadProcessId(target, out _);

            if (AttachThreadInput(us, them, true))
            {
                SetForegroundWindow(target);
                AttachThreadInput(us, them, false);
            }
            else
            {
                SetForegroundWindow(target);
            }
        }

        static uint CurrentProcessId => (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    }
}
