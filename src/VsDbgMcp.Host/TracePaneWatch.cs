using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Watches the Debug pane so a collected tracepoint's records can be timed.
    ///
    /// Visual Studio prints a tracepoint's message itself. It does not go out as
    /// debuggee output, so the debug event callback never sees one - that channel
    /// carries what the program writes, not what the debugger writes about it. The
    /// only place a record exists is the pane, so that is where it has to be read,
    /// and it has to be read as it arrives: the pane keeps the text and nothing else,
    /// so a record's arrival time exists only for as long as it takes to notice it.
    ///
    /// Attaching can fail on a Visual Studio whose Output window is not a text buffer.
    /// It says so rather than throwing, and collection then reports the records it can
    /// still recover without times.
    /// </summary>
    sealed class TracePaneWatch : IVsTextLinesEvents, IDisposable
    {
        readonly TraceLog _trace;
        readonly Action<string> _log;

        IVsTextLines _lines;
        IConnectionPoint _connection;
        int _cookie;
        int _seen;

        public TracePaneWatch(TraceLog trace, Action<string> log)
        {
            _trace = trace;
            _log = log ?? (_ => { });
        }

        /// <summary>Whether records are being timed as they arrive.</summary>
        public bool Attached => _connection != null;

        /// <summary>
        /// Attaches to the Debug pane if it is not already. Called when a tracepoint
        /// starts collecting, because the pane does not exist until something has
        /// debugged.
        /// </summary>
        public bool EnsureAttached(IVsOutputWindow window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Attached) return true;
            if (window == null) return false;

            var pane = DebugPane(window);
            if (pane == null) return false;

            var lines = BufferOf(pane);
            if (lines == null)
            {
                _log("trace: the Debug pane is not a text buffer on this Visual Studio, " +
                     "so tracepoint records cannot be timed as they arrive");
                return false;
            }

            var container = lines as IConnectionPointContainer;
            if (container == null) return false;

            try
            {
                var events = typeof(IVsTextLinesEvents).GUID;
                container.FindConnectionPoint(ref events, out var connection);
                if (connection == null) return false;

                connection.Advise(this, out _cookie);
                _connection = connection;
                _lines = lines;

                // Everything already in the pane belongs to before this tracepoint
                // existed, so start from the end rather than replaying history.
                _seen = LineCount(lines);
                _log("trace: watching the Debug pane from line " + _seen);
                return true;
            }
            catch (Exception ex)
            {
                _log("trace: could not watch the Debug pane: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Recovers records from the pane's text, for a Visual Studio that will not let
        /// them be watched as they arrive. Only lines not read before are taken, so a
        /// record is counted once however often this is called.
        ///
        /// These records carry no time. Stamping them now would date every one of them
        /// to the moment somebody asked, which reads like measurement and is not.
        /// </summary>
        public void PumpFromText(string paneText)
        {
            if (Attached || string.IsNullOrEmpty(paneText)) return;

            var lines = paneText.Replace("\r\n", "\n").Split('\n');

            // The final line has nothing after it yet, so it may still be half written.
            for (; _seen < lines.Length - 1; _seen++)
            {
                var body = TraceMessage.Unmark(lines[_seen], out var breakpointId);
                if (breakpointId > 0) _trace.Add(breakpointId, body, default(DateTime));
            }
        }

        /// <summary>
        /// Forgets how much of the pane has been read, so a new debug session starts
        /// from the beginning of a pane Visual Studio has cleared.
        /// </summary>
        public void Rewind()
        {
            if (!Attached) _seen = 0;
        }

        static IVsOutputWindowPane DebugPane(IVsOutputWindow window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var guid = VSConstants.GUID_OutWindowDebugPane;
            return window.GetPane(ref guid, out var pane) == VSConstants.S_OK ? pane : null;
        }

        /// <summary>
        /// The text behind a pane. Panes carry their buffer as user data rather than
        /// exposing it, and a Visual Studio that does not keep one simply has nothing
        /// to give back here.
        /// </summary>
        static IVsTextLines BufferOf(IVsOutputWindowPane pane)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var data = pane as IVsUserData;
            if (data == null) return null;

            try
            {
                var guid = typeof(IVsTextLines).GUID;
                return data.GetData(ref guid, out var value) == VSConstants.S_OK ? value as IVsTextLines : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static int LineCount(IVsTextLines lines)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return lines.GetLineCount(out var count) == VSConstants.S_OK ? count : 0;
        }

        /// <summary>
        /// Called as the pane grows. Only whole lines that are new since the last call
        /// are read, so a record is timed once and a line still being written is left
        /// until it is finished.
        /// </summary>
        public void OnChangeLineText(TextLineChange[] change, int last)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_lines == null) return;

            var arrived = DateTime.UtcNow;

            try
            {
                var count = LineCount(_lines);

                // The last line has no newline behind it yet, so it may still be half
                // written. It is read on the next change, once something follows it.
                for (; _seen < count - 1; _seen++)
                {
                    var text = LineAt(_lines, _seen);
                    if (string.IsNullOrEmpty(text)) continue;

                    var body = TraceMessage.Unmark(text, out var breakpointId);
                    if (breakpointId > 0) _trace.Add(breakpointId, body, arrived);
                }

                if (_seen > count) _seen = count;
            }
            catch (Exception ex)
            {
                _log("trace: reading the Debug pane failed: " + ex.Message);
            }
        }

        static string LineAt(IVsTextLines lines, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (lines.GetLengthOfLine(line, out var length) != VSConstants.S_OK) return null;
            if (length <= 0) return "";
            return lines.GetLineText(line, 0, line, length, out var text) == VSConstants.S_OK ? text : null;
        }

        public void OnChangeLineAttributes(int firstLine, int lastLine)
        {
        }

        public void Dispose()
        {
            var connection = _connection;
            _connection = null;
            _lines = null;

            if (connection == null) return;
            try { connection.Unadvise(_cookie); }
            catch (Exception) { }
        }
    }
}
