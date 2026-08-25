using System.Collections.Generic;
using System.Linq;

namespace VsDbgMcp.Shim.Profiling
{
    /// <summary>
    /// The profiles taken during this session, numbered the way breakpoints are.
    ///
    /// They are kept for as long as the session lasts because each one is counted
    /// stacks rather than a trace - small enough that holding several costs nothing,
    /// and holding them is what makes comparing a change against what came before it
    /// possible at all.
    /// </summary>
    public sealed class Captures
    {
        readonly List<Capture> _taken = new List<Capture>();
        int _next = 1;

        public IReadOnlyList<Capture> All => _taken;

        public Capture Keep(Capture capture)
        {
            capture.Id = _next++;
            _taken.Add(capture);
            return capture;
        }

        /// <summary>The one asked for, or the most recent when none was.</summary>
        public Capture Find(int? id) =>
            id == null ? _taken.LastOrDefault() : _taken.FirstOrDefault(c => c.Id == id.Value);
    }
}
