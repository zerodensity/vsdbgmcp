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
