using System.Collections.Generic;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// Which of a frame's values are evidence and which are not.
    ///
    /// Every fact here is already against its own value in the list above, in a few
    /// words. Nobody scanning forty rows sees them there, so they are gathered once at
    /// the end with what to do about each. The gathering is the point; the row is where
    /// the fact happened.
    ///
    /// Nothing here looks at the program. Every reason comes from something the debugger
    /// already said about the value, or from the shape of the text it returned.
    /// </summary>
    public static class FrameTrust
    {
        /// <summary>
        /// Why this value is not evidence, or null when nothing is against it.
        ///
        /// One reason, not a list. Where more than one thing is wrong the first is the
        /// one that decides what to do next, and the row above carries the rest.
        /// </summary>
        public static string Reason(VarNode node)
        {
            if (node == null) return null;

            if (!node.Readable)
                return "not readable; the text beside it is the engine's reason, not a value";

            if (node.SameAddressAs != null && node.SameAddressAs.Count > 0)
            {
                return "shares a slot with " + string.Join(", ", node.SameAddressAs.ToArray()) +
                       "; the value may be any of theirs";
            }

            if (node.HasChildren && ContainerElement.SaysOnlyEmpty(node.Value))
                return "says empty; expand reads the raw layout, where a full one shows";

            var fills = FillPatterns.Notes(node.Value);
            if (fills.Count > 0) return string.Join("; ", fills.ToArray()) + "; not live data";

            return null;
        }

        /// <summary>
        /// The closing list: every value with something against it, and what. Null when
        /// there were no values to judge, because a frame with none is not a frame whose
        /// values read cleanly.
        /// </summary>
        public static string NotEvidence(IReadOnlyList<VarNode> nodes)
        {
            if (nodes == null || nodes.Count == 0) return null;

            var lines = new List<string>();
            var width = 0;
            foreach (var node in nodes)
            {
                if (Reason(node) == null) continue;
                if (node.Name != null && node.Name.Length > width) width = node.Name.Length;
            }

            foreach (var node in nodes)
            {
                var reason = Reason(node);
                if (reason == null) continue;
                lines.Add("  " + (node.Name ?? "").PadRight(width) + "  " + reason);
            }

            // One line, because it is the answer on most frames. Said at all, because a
            // missing section reads as a check that never ran.
            if (lines.Count == 0) return "  none - every value above read cleanly.";

            return string.Join("\n", lines.ToArray());
        }
    }
}
