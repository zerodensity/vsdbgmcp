using System;
using System.Collections.Generic;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// Which of a frame's values are evidence and which are not.
    ///
    /// A frame report prints every variable in one go, and nobody scanning forty rows
    /// notices that one of them came from a slot the compiler handed to two locals, or
    /// that a map rendering Empty has entries. So each fact is said twice: against the
    /// value, and again at the end as the list of everything not to be believed. The
    /// list is the part that gets read.
    ///
    /// Nothing here looks at the program. Every mark comes from something the debugger
    /// already said about the value, or from the shape of the text it returned.
    /// </summary>
    public static class FrameTrust
    {
        /// <summary>
        /// What is wrong with this value, as far as anything here can tell. Empty for a
        /// value with nothing against it, which is most of them.
        /// </summary>
        public static List<string> Marks(VarNode node)
        {
            var marks = new List<string>();
            if (node == null) return marks;

            if (!node.Readable)
            {
                marks.Add("not readable here - in an optimized build the compiler usually kept " +
                          "nothing for it, and the text beside it is the engine's reason rather " +
                          "than a value");
            }

            if (node.SameAddressAs != null && node.SameAddressAs.Count > 0)
            {
                marks.Add("reads the same address as " + string.Join(", ", node.SameAddressAs.ToArray()) +
                          " - one slot the optimizer gave to more than one name, so this value may " +
                          "belong to any of them");
            }

            foreach (var fill in FillPatterns.Notes(node.Value))
            {
                marks.Add(fill);
            }

            if (LooksLikeAnEmptyContainer(node))
            {
                marks.Add("the visualizer says it is empty - read it again with expand, which " +
                          "checks the raw layout, because a container rendering empty while it " +
                          "holds entries reads as an answer rather than as an error");
            }

            return marks;
        }

        /// <summary>
        /// The closing list: every value carrying a mark, and nothing else. Null when
        /// there were no variables to judge, because a frame with none is not a frame
        /// whose values read cleanly.
        /// </summary>
        public static string NotEvidence(IReadOnlyList<VarNode> nodes)
        {
            if (nodes == null || nodes.Count == 0) return null;

            var marked = new List<string>();
            foreach (var node in nodes)
            {
                if (Marks(node).Count > 0) marked.Add(node.Name);
            }

            if (marked.Count == 0)
            {
                return "Every value above read cleanly: none is a compiler-discarded local, a shared " +
                       "slot, allocator fill, or a container claiming to be empty. That is what was " +
                       "checked, and not a promise that the values are correct.";
            }

            return "Not evidence without a second reading: " + string.Join(", ", marked.ToArray()) +
                   ". Each is marked above with what is wrong with it.";
        }

        /// <summary>
        /// A summary that claims there is nothing inside, on something that has an inside.
        ///
        /// The children flag is what keeps an int of zero out of this. A scalar has no
        /// contents to disagree with its summary, and marking every zero in the frame
        /// would bury the marks that matter.
        /// </summary>
        static bool LooksLikeAnEmptyContainer(VarNode node) =>
            node.HasChildren && ContainerElement.LooksEmpty(node.Value);
    }
}
