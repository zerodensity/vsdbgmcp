using System;
using System.Collections.Generic;

namespace VsDbgMcp
{
    /// <summary>
    /// Writing an expression so the debugger resolves its type names in a module of the
    /// caller's choosing.
    ///
    /// Visual Studio's module qualifier only reaches the token directly after it, so a
    /// type inside a cast never sees it: "((T*)0x1234)->Member" comes back saying T is
    /// undefined, while the same read written as "(*(T*)0x1234).Member" with the
    /// qualifier in front of the star works. Nobody calling a debugger should have to
    /// know that, so this turns the expression a caller would naturally write into the
    /// forms worth trying, best first.
    /// </summary>
    public static class ModuleQualifier
    {
        /// <summary>
        /// How to write <paramref name="expression"/> so its types come from
        /// <paramref name="module"/>, best first. Never empty: with no module, or a shape
        /// this does not know how to rewrite, the expression comes back qualified as it
        /// was written and the engine's own error reaches the caller.
        /// </summary>
        public static List<string> Forms(string expression, string module)
        {
            var forms = new List<string>();
            if (string.IsNullOrWhiteSpace(expression)) return forms;

            var text = expression.Trim();
            var name = ModuleName(module);

            // A caller who already wrote the qualifier themselves meant it where they put
            // it, and a second one would not parse.
            if (name == null || text.Contains("{,,"))
            {
                forms.Add(text);
                return forms;
            }

            var throughCast = ThroughCast(text, name);
            if (throughCast != null) forms.Add(throughCast);

            forms.Add("{,," + name + "}" + text);
            return forms;
        }

        /// <summary>
        /// Why a module cannot be looked up in, or null when it can be.
        ///
        /// A name that is not loaded is worth refusing over rather than passing to the
        /// engine. The qualifier then names nothing, the type resolves in whatever module
        /// the frame happens to be in, and a same-named symbol there answers with a value
        /// that reads as data: an actor pointer that prints somebody else's string.
        ///
        /// This catches a name that is wrong or misspelled and nothing else. A cast written
        /// with no module at all is the same wrong answer reached by not asking, and nothing
        /// here can see it: there is no second reading to disagree with the first.
        /// </summary>
        public static string NotLoaded(string module, IReadOnlyList<string> loaded)
        {
            var name = ModuleName(module);
            if (name == null) return null;

            // An empty list is what "nothing could be enumerated" looks like as well as
            // what "nothing is loaded" looks like, and refusing on the first would be a
            // claim about a list that was never read.
            if (loaded == null || loaded.Count == 0) return null;

            // A caller with the module's path in hand passes the path; the engine names
            // modules by their file name, so that is what is compared.
            var wanted = FileName(name);

            foreach (var candidate in loaded)
            {
                if (IsTheSameModule(wanted, candidate)) return null;
            }

            var near = NearMisses(wanted, loaded);
            return "No loaded module is named '" + name + "', so type names cannot be looked up in " +
                   "it. " + loaded.Count + " modules are loaded" +
                   (near.Count == 0
                       ? " and none of their names is close to it."
                       : " and the closest names are " + string.Join(", ", near.ToArray()) + ".") +
                   " Those are spelled as the debugger spells them; 'modules' lists what the " +
                   "session has loaded, and wait(for: \"module:NAME\") waits for one that has not " +
                   "loaded yet. Evaluating this without the module would " +
                   "resolve the type in the frame's own module instead, which is where a cast comes " +
                   "back as a confident wrong value rather than as an error.";
        }

        /// <summary>
        /// The same module, allowing for the extension being left off - which is passed on
        /// without knowing whether the engine takes that spelling, because a name that is
        /// nearly right is the engine's to reject and refusing it here would be guessing.
        ///
        /// Nothing looser than that. Stripping the extension from both sides as well would
        /// make Foo.exe match a loaded Foo.dll, and that is a wrong name worth being told
        /// about rather than passed through.
        /// </summary>
        static bool IsTheSameModule(string name, string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return false;

            var loaded = FileName(candidate);
            return string.Equals(name, loaded, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, WithoutExtension(loaded), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Loaded names close enough to be what was meant: the same letters written with
        /// different separators first, then any loaded name that contains what was asked
        /// for. That covers the two ways this is got wrong - nosSysVulkan.dll written for
        /// nos.sys.vulkan.dll, and half of a name written for the whole of it.
        ///
        /// Only that direction. A short loaded name buried inside a long one that was asked
        /// for has nothing to do with it: user32.dll is not what somebody meant by
        /// MyUser32Wrapper.dll, and offering it would send a reader off after it.
        /// </summary>
        static List<string> NearMisses(string name, IReadOnlyList<string> loaded)
        {
            var wanted = Squashed(name);
            var near = new List<string>();
            var loose = new List<string>();

            foreach (var candidate in loaded)
            {
                if (string.IsNullOrEmpty(candidate)) continue;

                var other = Squashed(FileName(candidate));
                if (other.Length == 0) continue;

                if (string.Equals(wanted, other, StringComparison.Ordinal))
                {
                    if (!near.Contains(candidate)) near.Add(candidate);
                }
                else if (wanted.Length > 3 && other.IndexOf(wanted, StringComparison.Ordinal) >= 0)
                {
                    if (!loose.Contains(candidate)) loose.Add(candidate);
                }
            }

            foreach (var candidate in loose)
            {
                if (near.Count >= 5) break;
                near.Add(candidate);
            }

            return near.Count > 5 ? near.GetRange(0, 5) : near;
        }

        /// <summary>The letters of a name with the separators nobody agrees on taken out.</summary>
        static string Squashed(string name)
        {
            var text = WithoutExtension(name).ToLowerInvariant();
            var kept = new System.Text.StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c)) kept.Append(c);
            }
            return kept.ToString();
        }

        static string FileName(string path)
        {
            var text = (path ?? "").Trim();
            var cut = text.LastIndexOfAny(new[] { '\\', '/' });
            return cut < 0 ? text : text.Substring(cut + 1);
        }

        static string WithoutExtension(string name)
        {
            var dot = name.LastIndexOf('.');
            return dot <= 0 ? name : name.Substring(0, dot);
        }

        /// <summary>
        /// The module as a caller is likely to give it, including one that already carries
        /// the debugger's own braces.
        /// </summary>
        static string ModuleName(string module)
        {
            if (string.IsNullOrWhiteSpace(module)) return null;
            var name = module.Trim().Trim('{', '}').TrimStart(',').Trim();
            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// "((T*)a)->m" and "(*(T*)a).m" are the same read, and both can be written with
        /// the module in front of the dereference, which is where the qualifier reaches
        /// the cast. Returns null for anything else, including a cast of something the
        /// dereference would bind to the wrong half of, such as "((T*)a + 1)->m".
        /// </summary>
        static string ThroughCast(string text, string module)
        {
            if (text.Length == 0 || text[0] != '(') return null;

            var close = MatchingParen(text, 0);
            if (close < 0) return null;

            var head = text.Substring(1, close - 1).Trim();
            var rest = text.Substring(close + 1).TrimStart();

            string dereference;
            if (rest.StartsWith("->", StringComparison.Ordinal) && IsSimpleCast(head))
            {
                dereference = "*" + head;
                rest = rest.Substring(2);
            }
            else if (rest.StartsWith(".", StringComparison.Ordinal) &&
                     head.StartsWith("*", StringComparison.Ordinal) &&
                     IsSimpleCast(head.Substring(1).TrimStart()))
            {
                dereference = head;
                rest = rest.Substring(1);
            }
            else
            {
                return null;
            }

            rest = rest.Trim();
            if (rest.Length == 0) return null;

            return "({,," + module + "}" + dereference + ")." + rest;
        }

        /// <summary>
        /// A cast of one plain operand: "(T*)0x1234", "(T*)ptr", "(T*)&amp;obj". An operand
        /// built out of anything else is left alone rather than guessed at.
        /// </summary>
        static bool IsSimpleCast(string text)
        {
            if (text.Length == 0 || text[0] != '(') return false;

            var close = MatchingParen(text, 0);
            if (close < 0) return false;

            var operand = text.Substring(close + 1).Trim();
            if (operand.Length > 0 && operand[0] == '&') operand = operand.Substring(1).TrimStart();
            if (operand.Length == 0) return false;

            foreach (var c in operand)
            {
                if (!char.IsLetterOrDigit(c) && c != '_' && c != ':') return false;
            }
            return true;
        }

        static int MatchingParen(string text, int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }
    }
}
