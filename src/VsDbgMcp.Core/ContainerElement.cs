using System;
using System.Collections.Generic;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    /// <summary>
    /// Getting at a container's elements, by whichever view of it there is.
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
    ///
    /// Where the visualizer produces no element rows at all there is the raw layout to
    /// fall back on, and where there is no visualizer at all there is the index. Both are
    /// here because both answer the question the rows would have.
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

        // ------------------------------------------------------- the raw layout instead

        /// <summary>
        /// True when a summary is the visualizer's own claim that there is nothing inside:
        /// "Empty", "{}", "{ size=0 }". Only the words are read; nothing here looks at the
        /// object, which is the whole reason the claim needs checking.
        /// </summary>
        public static bool LooksEmpty(string value)
        {
            if (value == null) return false;

            var text = value.Trim().Trim('{', '}').Trim();
            if (text.Length == 0) return true;
            if (string.Equals(text, "empty", StringComparison.OrdinalIgnoreCase)) return true;

            return SaysZero(text);
        }

        /// <summary>
        /// True when the whole summary is the claim that there is nothing inside, rather
        /// than a summary that happens to mention something which is zero.
        ///
        /// LooksEmpty answers the second question, which is right where the rows of an
        /// expansion are in hand to check it against. Where there are no rows - a
        /// variable printed in a list - it is too loose on its own:
        /// {name="terrain" vertices={ size=0 } refCount=1 } is a mesh with a name and a
        /// reference count, and calling it empty puts a plainly good value in front of a
        /// reader as a suspect one.
        ///
        /// So the object's own fields are counted, which are the ones between its
        /// outermost braces. A pointer renders as an address and then the braces, so
        /// reading the text as it comes would find every field nested one level down and
        /// count none of them.
        /// </summary>
        public static bool SaysOnlyEmpty(string value)
        {
            if (!LooksEmpty(value)) return false;

            var text = Inside((value ?? "").Trim());
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
        /// "Empty" carries no braces and is the claim itself.
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

        /// <summary>
        /// What to say when a container rendered as empty and was read again with the ",!"
        /// specifier, which turns the visualizer off.
        ///
        /// The fields are reported and no verdict is drawn from them, because a capacity and
        /// a size are spelled the same way: an empty deque keeps _Mysize at zero beside a
        /// _Mapsize of eight, and calling that a disagreement would be this inventing one.
        /// The one thing worth saying outright is that every field reading like a count is
        /// zero, and even that is only said when the whole layout was read.
        /// </summary>
        public static string RawView(string reference, string shown, int shownRows,
            IReadOnlyList<VarNode> rawRows)
        {
            var head = Head(reference, shown, shownRows);

            if (rawRows == null || rawRows.Count == 0)
            {
                return head + " Reading it raw with ',!' produced no fields either, so nothing " +
                       "here tells an empty container from a visualizer that is wrong about one.";
            }

            head += " The rows under [raw layout] below are the same value read with ',!'. ";

            var unread = Unread(rawRows);
            var cut = unread == 0
                ? ""
                : " " + unread + " of those rows have contents that were not read, so a count " +
                  "kept deeper than that would not have been seen here.";

            var counts = CountFields(rawRows);
            if (counts.Count == 0)
            {
                return head + "No field's name reads like a count, so nothing here confirms or " +
                       "contradicts the visualizer." + cut;
            }

            var notZero = new List<string>();
            var zero = new List<string>();
            foreach (var count in counts)
            {
                var text = count.Key + " = " + count.Value;
                if (IsZero(count.Value)) zero.Add(text);
                else notZero.Add(text);
            }

            if (notZero.Count == 0)
            {
                return head + "Every field that reads like a count is zero: " + Listed(zero) + "." +
                       (unread == 0 ? " Both views agree it is empty." : cut);
            }

            var said = head + "Fields that read like counts and are not zero: " + Listed(notZero) + ".";
            if (zero.Count > 0) said += " Ones that are zero: " + Listed(zero) + ".";

            return said + " A capacity and a bucket count are spelled the same way as a size, so " +
                   "these do not settle it on their own - read them and judge." + cut;
        }

        /// <summary>Said when the raw read itself would not run, which leaves one view and no check.</summary>
        public static string RawUnreadable(string reference, string shown, int shownRows, string why) =>
            Head(reference, shown, shownRows) + " Reading it raw with ',!' to check that failed as well" +
            (string.IsNullOrEmpty(why) ? "" : ": " + why.TrimEnd('.')) +
            ", so nothing here says whether it is really empty.";

        /// <summary>
        /// What the visualizer did show, which is not always nothing: a container it renders
        /// as empty still lays out rows of its own, and calling those nothing would be the
        /// same kind of wrong answer this is here to stop.
        /// </summary>
        static string Head(string reference, string shown, int shownRows)
        {
            var head = "'" + reference + "' renders as " + Rendered(shown) + " and expanding it ";
            return shownRows <= 0
                ? head + "produced no rows at all."
                : head + "produced " + shownRows + " rows, none of them an element - nothing " +
                  "named [0], [1] and so on.";
        }

        /// <summary>A few of them, and how many were left off.</summary>
        static string Listed(List<string> fields)
        {
            if (fields.Count <= 6) return string.Join(", ", fields.ToArray());

            return string.Join(", ", fields.GetRange(0, 6).ToArray()) +
                   ", and " + (fields.Count - 6) + " more";
        }

        /// <summary>
        /// Rows the engine says hold something that was not read, because the raw view is
        /// read only so deep. Silence about those is the same failure one level down.
        /// </summary>
        static int Unread(IReadOnlyList<VarNode> rows)
        {
            var count = 0;
            if (rows == null) return count;

            foreach (var row in rows)
            {
                if (row.HasChildren && row.Children == null) count++;
                else count += Unread(row.Children);
            }
            return count;
        }

        // -------------------------------------------------------- no visualizer at all

        /// <summary>
        /// How many indexes one call reads. A raw array carries no length, so the count is
        /// the caller's to give and this is the only thing stopping a typo asking for
        /// millions of evaluations.
        /// </summary>
        public const int IndexLimit = 256;

        /// <summary>
        /// Why a run of indexes cannot be read as it was asked for, or null when it can.
        /// Both of these are a call asking for two things at once; answering one of them
        /// quietly would leave the caller believing they had the other.
        /// </summary>
        public static string CannotEnumerate(int count, string member, bool allThreads)
        {
            if (count > 0 && allThreads)
            {
                return "count walks the indexes of one expression in one frame and allThreads reads " +
                       "one expression on every thread. Ask for one of them.";
            }

            if (count <= 0 && !string.IsNullOrWhiteSpace(member))
            {
                return "member reads something on each element of a run of indexes, and no count was " +
                       "given, so there is no run to read it on. Pass count as well, or write the " +
                       "member into the expression.";
            }

            return null;
        }

        /// <summary>
        /// One index of a raw array, written the way C++ writes it. The base expression is
        /// parenthesised because "p + 1" indexed without that is a different read.
        ///
        /// A member is used exactly as it follows the element - "->Name", ".Name", "[0]" -
        /// and a bare name is read as ".Name", because guessing between the two would put
        /// the wrong one in the reply as often as the right one.
        /// </summary>
        public static string Indexed(string expression, int index, string member)
        {
            var text = "(" + (expression ?? "").Trim() + ")[" +
                       index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

            var reach = (member ?? "").Trim();
            if (reach.Length == 0) return text;
            if (reach[0] == '.' || reach[0] == '[' || reach.StartsWith("->", StringComparison.Ordinal))
                return text + reach;
            return text + "." + reach;
        }

        /// <summary>
        /// What a run of indexed reads is worth saying beyond the rows themselves: why it is
        /// shorter than what was asked for, how many of the values were distinct, and why
        /// every one of them might have failed the same way.
        ///
        /// The distinct count is the measurement this exists for. A block of repeated values
        /// in a list of 28 is what a container's own view had already hidden, and counting
        /// them is the difference between seeing it and scrolling past it.
        /// </summary>
        public static string Enumeration(int asked, string member, IReadOnlyList<EvalResult> rows)
        {
            if (rows == null || rows.Count == 0 || rows[0].Index == null) return "";

            var lines = new List<string>();
            var wanted = asked < IndexLimit ? asked : IndexLimit;

            // Two reasons for a short run, and reading one off the row count alone would
            // report whichever happened as the other.
            if (rows.Count < wanted)
            {
                lines.Add("Stopped after " + rows.Count + " of the " + asked + " asked for: every one " +
                          "of those failed the same way, so the rest were not read.");
            }
            else if (asked > IndexLimit)
            {
                lines.Add(asked + " indexes were asked for and " + IndexLimit + " read, which is the " +
                          "cap. The rest were not looked at; where the expression is a pointer or an " +
                          "array, move it along - (expr + " + IndexLimit + ") - to read them.");
            }

            var read = 0;
            var failed = 0;
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            string firstError = null;

            foreach (var row in rows)
            {
                if (row.IsValid)
                {
                    read++;
                    distinct.Add(row.Value ?? "");
                }
                else
                {
                    failed++;
                    if (firstError == null) firstError = row.Error;
                }
            }

            if (distinct.Count > 0 && distinct.Count < read)
                lines.Add(read + " values read, " + distinct.Count + " of them distinct.");

            if (failed > 0 && read > 0)
                lines.Add(failed + " of the " + rows.Count + " could not be read: " + firstError);

            if (read == 0 && IsBare(member))
            {
                lines.Add("Every one failed, and the member was written as a bare name, so it was " +
                          "read as '." + member.Trim() + "'. Elements that are pointers need '->" +
                          member.Trim() + "'.");
            }

            // Said once for the whole run rather than beside each row, the same way a
            // reading taken across every thread says it once.
            if (read == 0)
            {
                var advice = EngineRefusal.Advice(firstError);
                if (advice != null) lines.Add(advice);
            }

            return string.Join("\n", lines);
        }

        /// <summary>A member with no connector in front of it, which had to be given one.</summary>
        static bool IsBare(string member)
        {
            var reach = (member ?? "").Trim();
            return reach.Length > 0 && reach[0] != '.' && reach[0] != '[' &&
                   !reach.StartsWith("->", StringComparison.Ordinal);
        }

        static string Rendered(string shown) =>
            string.IsNullOrWhiteSpace(shown) ? "nothing at all" : "'" + shown.Trim() + "'";

        /// <summary>
        /// Every field in the raw layout whose name reads like a count and whose value is a
        /// plain number, each with the path to reach it. The path is what makes the answer
        /// usable: the size of a std container can sit four levels down.
        /// </summary>
        static List<KeyValuePair<string, string>> CountFields(IReadOnlyList<VarNode> rows)
        {
            var found = new List<KeyValuePair<string, string>>();
            Collect(rows, "", found);
            return found;
        }

        /// <summary>
        /// A field that is not zero is the one worth reading, so it goes to the front of a
        /// list a reply only shows the first few of.
        /// </summary>
        static void Collect(IReadOnlyList<VarNode> rows, string path, List<KeyValuePair<string, string>> found)
        {
            if (rows == null) return;

            foreach (var row in rows)
            {
                var here = path.Length == 0 ? row.Name : path + "." + row.Name;

                if (IsCountName(row.Name) && IsNumber(row.Value))
                {
                    var field = new KeyValuePair<string, string>(here, row.Value.Trim());
                    if (IsZero(field.Value)) found.Add(field);
                    else found.Insert(0, field);
                }

                Collect(row.Children, here, found);
            }
        }

        /// <summary>
        /// The words a size field ends in, whichever library wrote it: _Mysize, ArrayNum,
        /// NumElements, Length. The end of the name is the only place worth looking, since
        /// _Mysize has no word boundary to find - which makes this weak evidence, and every
        /// reply built out of it says so.
        /// </summary>
        static readonly string[] CountWords = { "size", "count", "num", "len", "length", "used" };

        /// <summary>
        /// Words that end in one of those and mean nothing of the kind. No rule separates
        /// them from the real ones, so they are listed.
        /// </summary>
        static readonly string[] NotCountWords = { "enum", "unused" };

        static bool IsCountName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            return !EndsWithAny(name, NotCountWords) && EndsWithAny(name, CountWords);
        }

        static bool EndsWithAny(string name, string[] words)
        {
            foreach (var word in words)
            {
                if (name.Length >= word.Length &&
                    string.Compare(name, name.Length - word.Length, word, 0, word.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A "size=0" written into a visualizer's own summary.</summary>
        static bool SaysZero(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '=') continue;
                if (IsCountName(WordBefore(text, i)) && IsZero(NumberAfter(text, i))) return true;
            }
            return false;
        }

        static string WordBefore(string text, int at)
        {
            var end = at;
            while (end > 0 && text[end - 1] == ' ') end--;

            var start = end;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
            return text.Substring(start, end - start);
        }

        static string NumberAfter(string text, int at)
        {
            var start = at + 1;
            while (start < text.Length && text[start] == ' ') start++;

            var end = start;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == 'x')) end++;
            return text.Substring(start, end - start);
        }

        /// <summary>
        /// A decimal or hex literal and nothing else, so "{...}" is not a count. A negative
        /// one counts: a length that has gone below zero is the most telling value a field
        /// like this can hold, and dropping it would be silence about the interesting case.
        /// </summary>
        static bool IsNumber(string value)
        {
            var text = Unsigned((value ?? "").Trim());
            if (text.Length == 0) return false;

            var start = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            if (start == text.Length) return false;

            for (var i = start; i < text.Length; i++)
            {
                var c = text[i];
                var hex = start == 2 && ((c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
                if (!char.IsDigit(c) && !hex) return false;
            }
            return true;
        }

        static bool IsZero(string value)
        {
            if (!IsNumber(value)) return false;

            var text = Unsigned(value.Trim());
            var start = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] != '0') return false;
            }
            return true;
        }

        static string Unsigned(string text) =>
            text.Length > 1 && text[0] == '-' ? text.Substring(1) : text;

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
