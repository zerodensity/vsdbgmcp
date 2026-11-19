using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// The modal dialog Visual Studio is showing, if any.
    ///
    /// A modal dialog disables the window that owns it and runs its own message loop on
    /// the UI thread, so the command that opened it does not return until a person
    /// closes it. Read with Win32, which answers from any thread while that loop runs.
    /// </summary>
    sealed class ShellDialog
    {
        delegate bool WindowVisitor(IntPtr window, IntPtr parameter);

        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, WindowVisitor visit, IntPtr parameter);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint relation);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int InternalGetWindowText(IntPtr window, StringBuilder text, int capacity);

        const uint Owner = 4;

        readonly uint _uiThread;

        ShellDialog(uint uiThread)
        {
            _uiThread = uiThread;
        }

        /// <summary>Watches the windows of the calling thread, which must be Visual Studio's UI thread.</summary>
        public static ShellDialog OfCurrentThread() => new ShellDialog(GetCurrentThreadId());

        /// <summary>
        /// The open dialog's title, or null when there is none: a visible window on the UI
        /// thread whose owner is disabled. The main window has no single handle to watch;
        /// Visual Studio keeps more than one top-level window by that name.
        /// </summary>
        public string OpenTitle()
        {
            string title = null;
            EnumThreadWindows(_uiThread, (window, _) =>
            {
                var owner = GetWindow(window, Owner);
                if (owner == IntPtr.Zero || !IsWindowVisible(window) || IsWindowEnabled(owner)) return true;

                // The most recent of several nested dialogs, which is the one waiting for an answer.
                var popup = GetLastActivePopup(owner);
                title = Text(popup == owner ? window : popup);
                return false;
            }, IntPtr.Zero);
            return title;
        }

        /// <summary>
        /// The title as the window last set it. GetWindowText would send WM_GETTEXT to
        /// the UI thread and wait on it, which is the thread a dialog may be holding.
        /// </summary>
        static string Text(IntPtr window)
        {
            var text = new StringBuilder(256);
            InternalGetWindowText(window, text, text.Capacity);
            return text.Length == 0 ? "untitled" : text.ToString();
        }
    }
}
