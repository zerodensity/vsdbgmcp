using System;
using System.Collections.Generic;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Gives breakpoints stable small numbers.
    ///
    /// The automation model has no breakpoint id, so ids are assigned here and kept for
    /// the life of the session, keyed on what the breakpoint points at. That way a
    /// number handed to an agent still means the same breakpoint several calls later.
    /// </summary>
    sealed class BreakpointTable
    {
        readonly Dictionary<string, int> _ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, Location> _locations = new Dictionary<int, Location>();
        int _next = 1;

        struct Location
        {
            public string File;
            public int Line;
        }

        public int IdFor(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var key = Signature(breakpoint);
            if (!_ids.TryGetValue(key, out var id))
            {
                id = _next++;
                _ids[key] = id;
            }

            _locations[id] = new Location { File = breakpoint.File, Line = breakpoint.FileLine };
            return id;
        }

        static string Signature(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return string.Join("|",
                breakpoint.File ?? "",
                breakpoint.FileLine.ToString(),
                breakpoint.FunctionName ?? "",
                breakpoint.Condition ?? "",
                breakpoint.Tag ?? "");
        }

        public Breakpoint Find(int id, Breakpoints all)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (all == null) return null;

            foreach (Breakpoint breakpoint in all)
            {
                if (IdFor(breakpoint) == id) return breakpoint;
            }
            return null;
        }

        /// <summary>
        /// Which breakpoint a stop happened at, matched on location. The engine reports
        /// that a breakpoint was hit but not which of ours it was.
        /// </summary>
        public int? MatchLocation(Frame frame)
        {
            if (frame == null || string.IsNullOrEmpty(frame.File)) return null;

            foreach (var pair in _locations)
            {
                if (pair.Value.Line != frame.Line) continue;
                if (!PathUtil.SamePath(pair.Value.File, frame.File)) continue;
                return pair.Key;
            }
            return null;
        }
    }
}
