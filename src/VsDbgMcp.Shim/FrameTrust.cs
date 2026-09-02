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
                // Which of them the slot currently belongs to is not knowable from here.
                // An optimized build reusing one slot for several names is the usual
                // reason, but so is reading a local before the line that declares it,
                // and naming a cause would be picking one.
                marks.Add("reads the same address as " + string.Join(", ", node.SameAddressAs.ToArray()) +
                          " - one slot with more than one name on it, so this value may belong to " +
                          "any of them rather than to this one");
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
        /// A summary whose whole content is the claim that there is nothing inside.
        ///
        /// The word "whole" is doing the work. Where expand asks this it has the
        /// visualizer's rows in hand and can see that it produced none; here there are
        /// no rows to count, so the only safe reading is a summary that says nothing but
        /// "empty" - "Empty", "{}", "{ size=0 }".
        ///
        /// A struct summary mentioning a member that happens to be zero is not that.
        /// {name="terrain" vertices={ size=0 } refCount=1 } is a mesh with a name and a
        /// reference count, and marking it would put a value that is plainly fine in the
        /// one list that has to be believed.
        /// </summary>
        static bool LooksLikeAnEmptyContainer(VarNode node)
        {
            if (!node.HasChildren) return false;
            if (!ContainerElement.LooksEmpty(node.Value)) return false;

            return OneFieldOnly(node.Value);
        }

        /// <summary>
        /// True when the summary carries at most one named field, which is what tells a
        /// container's own count from a member of something larger.
        ///
        /// The object's own fields are the ones inside its outermost braces. A pointer
        /// renders as an address and then the braces, so taking the text as it comes
        /// would find every field nested one level down and count none of them.
        /// </summary>
        static bool OneFieldOnly(string value)
        {
            var text = Inside(value == null ? "" : value.Trim());

            var fields = 0;
            var depth = 0;
            foreach (var c in text)
            {
                if (c == '{' || c == '[' || c == '(') depth++;
                else if (c == '}' || c == ']' || c == ')') depth--;
                else if (c == '=' && depth == 0) fields++;
            }
            return fields <= 1;
        }

        /// <summary>
        /// What is between the outermost braces, or the whole text when there are none.
        /// "Empty" has no braces and is the claim itself.
        /// </summary>
        static string Inside(string text)
        {
            var open = text.IndexOf('{');
            if (open < 0) return text;

            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(open + 1, i - open - 1);
            }
            return text.Substring(open + 1);
        }
    }
}
