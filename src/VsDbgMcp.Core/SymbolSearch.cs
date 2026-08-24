using System;
using System.Collections.Generic;

namespace VsDbgMcp
{
    /// <summary>
    /// The engine's account of where it looked for a module's symbols, made readable.
    ///
    /// The engine keeps every search it has made for a module and hands back all of
    /// them at once, separated by blank lines. Asking twice returns the same paths
    /// twice, and a module whose symbols have been retried a few times answers with the
    /// same four lines over and over. Repeating a list adds nothing to it, so identical
    /// searches collapse into one and the count is said instead.
    /// </summary>
    public static class SymbolSearch
    {
        public static string Tidy(string text)
        {
            int searches;
            return Tidy(text, out searches);
        }

        /// <summary>
        /// The distinct searches, oldest first, and how many the engine actually
        /// reported. Null when there is nothing to say.
        /// </summary>
        public static string Tidy(string text, out int searches)
        {
            searches = 0;
            if (string.IsNullOrWhiteSpace(text)) return null;

            var kept = new List<string>();
            foreach (var block in Blocks(text.Replace("\r\n", "\n")))
            {
                searches++;
                if (!kept.Contains(block)) kept.Add(block);
            }

            if (kept.Count == 0) return null;

            var joined = string.Join("\n\n", kept.ToArray());
            if (searches <= kept.Count) return joined;

            // Said rather than hidden: the engine looked more than once, and a reader
            // who saw one list would otherwise take it for one attempt.
            return joined + "\n(the engine searched " + searches + " times and looked in the same " +
                   (kept.Count == 1 ? "places" : "sets of places") + " each time)";
        }

        /// <summary>One search: the run of non-blank lines between blank ones.</summary>
        static IEnumerable<string> Blocks(string text)
        {
            var current = new List<string>();

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0)
                {
                    if (current.Count > 0)
                    {
                        yield return string.Join("\n", current.ToArray());
                        current.Clear();
                    }
                    continue;
                }
                current.Add(line);
            }

            if (current.Count > 0) yield return string.Join("\n", current.ToArray());
        }
    }
}
