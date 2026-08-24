using System;
using System.Collections.Generic;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    /// <summary>
    /// Picking one element out of a container the way the debugger already shows it.
    ///
    /// A natvis visualizer lays a container's elements out as rows named [0], [1] and so
    /// on, and every row carries the expression that reaches that element - the two
    /// hundred character cast through $LinkedListItem that nobody would write by hand and
    /// nobody can proofread. So choosing an element is choosing a row, and everything here
    /// works on rows rather than on C++.
    ///
    /// A key is a walk down those rows comparing what each renders as. It asks the
    /// container nothing, it is not a hash lookup, and it can only see the rows an
    /// expansion actually read.
    /// </summary>
    public static class ContainerElement
    {
        /// <summary>The rows that are elements, leaving behind whatever else a visualizer adds.</summary>
        public static List<VarNode> In(IReadOnlyList<VarNode> rows)
        {
            var elements = new List<VarNode>();
            if (rows == null) return elements;

            foreach (var row in rows)
            {
                if (IsElement(row.Name)) elements.Add(row);
            }
            return elements;
        }

        /// <summary>"[0]", "[17]" - and not "[raw view]", "[bucket_count]" or a member name.</summary>
        public static bool IsElement(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 3) return false;
            if (name[0] != '[' || name[name.Length - 1] != ']') return false;

            for (var i = 1; i < name.Length - 1; i++)
            {
                if (!char.IsDigit(name[i])) return false;
            }
            return true;
        }

        /// <summary>The element the visualizer numbered this way, or null.</summary>
        public static VarNode At(IReadOnlyList<VarNode> elements, int index)
        {
            var wanted = "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
            foreach (var element in elements)
            {
                if (string.Equals(element.Name, wanted, StringComparison.Ordinal)) return element;
            }
            return null;
        }

        /// <summary>The first element whose key renders as this text, or null.</summary>
        public static VarNode WithKey(IReadOnlyList<VarNode> elements, string key)
        {
            foreach (var element in elements)
            {
                if (Same(KeyOf(element), key)) return element;
            }
            return null;
        }

        /// <summary>
        /// What this element is stored under: a map row's "first", or the element itself
        /// where the container keeps no separate key.
        /// </summary>
        public static string KeyOf(VarNode element)
        {
            if (element == null) return null;

            if (element.Children != null)
            {
                foreach (var child in element.Children)
                {
                    if (string.Equals(child.Name, "first", StringComparison.Ordinal)) return child.Value;
                }
            }
            return element.Value;
        }

        /// <summary>Nothing the visualizer showed was an element, so there is nothing to index.</summary>
        public static string NotAContainer(string reference, IReadOnlyList<VarNode> rows)
        {
            var count = rows == null ? 0 : rows.Count;
            if (count == 0)
                return "'" + reference + "' expands to nothing here, so it has no elements to pick from.";

            return "'" + reference + "' does not expand as elements. The visualizer showed " + count +
                   " rows named " + Names(rows) + ", not [0], [1] and so on. Expand it without index " +
                   "or key and pick a row by name.";
        }

        /// <summary>
        /// Nothing matched, said in terms of what was actually looked at. A container
        /// larger than one expansion reads is the case where "not found" would otherwise
        /// be a claim about elements nobody looked at.
        /// </summary>
        public static string NotFound(string reference, IReadOnlyList<VarNode> elements,
            int? index, string key, int limit)
        {
            var count = elements == null ? 0 : elements.Count;
            var capped = count >= limit
                ? " That is as many as one expansion reads, so there may be more beyond it."
                : "";

            if (index.HasValue)
            {
                return "'" + reference + "' has no element [" + index.Value + "]. The visualizer produced " +
                       count + " elements, [0] to [" + (count - 1) + "]." + capped;
            }

            return "No element of '" + reference + "' has the key " + key + ". This walked " + count +
                   " elements comparing what each one's key renders as" + Keys(elements) + "." + capped;
        }

        /// <summary>Same rendered text, allowing for the quotes a string value comes wrapped in.</summary>
        static bool Same(string rendered, string key)
        {
            if (rendered == null || key == null) return false;

            var left = rendered.Trim();
            var right = key.Trim();
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Unquoted(left), right, StringComparison.OrdinalIgnoreCase);
        }

        static string Unquoted(string text)
        {
            if (text.Length < 2) return text;
            var first = text[0];
            if ((first == '"' || first == '\'') && text[text.Length - 1] == first)
                return text.Substring(1, text.Length - 2);
            return text;
        }

        static string Names(IReadOnlyList<VarNode> rows) => Join(rows, row => row.Name);

        static string Keys(IReadOnlyList<VarNode> elements)
        {
            var keys = Join(elements, KeyOf);
            return keys.Length == 0 ? "" : ": " + keys;
        }

        /// <summary>A few of them, because a refusal that lists two hundred is not read.</summary>
        static string Join(IReadOnlyList<VarNode> nodes, Func<VarNode, string> of)
        {
            if (nodes == null || nodes.Count == 0) return "";

            var shown = new List<string>();
            for (var i = 0; i < nodes.Count && shown.Count < 6; i++)
            {
                var text = of(nodes[i]);
                if (!string.IsNullOrEmpty(text)) shown.Add(text);
            }

            var joined = string.Join(", ", shown);
            return nodes.Count > shown.Count ? joined + ", ..." : joined;
        }
    }
}
