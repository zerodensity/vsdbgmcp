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
                return "the engine kept nothing to read here; the text beside it is its reason, not a value";

            if (node.SameAddressAs != null && node.SameAddressAs.Count > 0)
            {
                return "shares one slot with " + string.Join(", ", node.SameAddressAs.ToArray()) +
                       ", so the value may belong to any of them";
            }

            if (node.HasChildren && ContainerElement.SaysOnlyEmpty(node.Value))
                return "says it is empty; expand reads the raw layout, which is where a full one shows";

            var fills = FillPatterns.Notes(node.Value);
            if (fills.Count > 0) return string.Join("; ", fills.ToArray()) + ", so this is not live data";

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

            // Said in one line, because it is the answer on most frames and a paragraph
            // saying nothing is wrong is a paragraph nobody finishes. Said at all,
            // because a missing section reads as a check that never ran.
            if (lines.Count == 0)
                return "  nothing: every value above read cleanly.";

            return string.Join("\n", lines.ToArray());
        }
    }
}
