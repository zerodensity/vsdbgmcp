using System;
using System.Collections.Generic;
using System.IO;

namespace VsDbgMcp
{
    /// <summary>
    /// Path comparison for routing. Windows paths, case-insensitive, boundary aware.
    /// </summary>
    public static class PathUtil
    {
        /// <summary>
        /// Full path with a trailing separator removed. Returns the input unchanged
        /// if it cannot be resolved, so a bad record never throws during discovery.
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            // A discovery record can hold anything, including a path with characters
            // this machine will not resolve. An unusable path is still comparable as
            // text, and routing simply will not match it.
            string full;
            try { full = Path.GetFullPath(path); }
            catch (ArgumentException) { full = path; }
            catch (NotSupportedException) { full = path; }
            catch (PathTooLongException) { full = path; }
            return TrimSeparator(full);
        }

        static string TrimSeparator(string p)
        {
            if (p.Length > 3 && (p[p.Length - 1] == Path.DirectorySeparatorChar ||
                                 p[p.Length - 1] == Path.AltDirectorySeparatorChar))
            {
                return p.Substring(0, p.Length - 1);
            }
            return p;
        }

        public static bool SamePath(string a, string b)
        {
            a = Normalize(a);
            b = Normalize(b);
            if (a == null || b == null) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when <paramref name="candidate"/> is <paramref name="ancestor"/> or sits under it.
        /// Compares whole segments, so D:\repo\Engine does not contain D:\repo\EngineTools.
        /// </summary>
        public static bool Contains(string ancestor, string candidate)
        {
            ancestor = Normalize(ancestor);
            candidate = Normalize(candidate);
            if (ancestor == null || candidate == null) return false;

            if (string.Equals(ancestor, candidate, StringComparison.OrdinalIgnoreCase))
                return true;

            if (candidate.Length <= ancestor.Length) return false;
            if (!candidate.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)) return false;

            var next = candidate[ancestor.Length];
            return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar;
        }

        /// <summary>
        /// How specific a containing directory is. Longer means more specific, so the
        /// nearest enclosing workspace wins. Zero when it does not contain the path.
        /// </summary>
        public static int Specificity(string ancestor, string candidate)
        {
            if (!Contains(ancestor, candidate)) return 0;
            return Normalize(ancestor).Length;
        }

        /// <summary>The directory itself and every parent, nearest first.</summary>
        public static IEnumerable<string> SelfAndAncestors(string dir)
        {
            var current = Normalize(dir);
            while (!string.IsNullOrEmpty(current))
            {
                yield return current;
                // Walking up can cross a directory this user cannot read.
                DirectoryInfo parent;
                try { parent = Directory.GetParent(current); }
                catch (IOException) { yield break; }
                catch (UnauthorizedAccessException) { yield break; }
                if (parent == null) yield break;
                current = TrimSeparator(parent.FullName);
            }
        }

        /// <summary>
        /// Solution files in a directory.
        ///
        /// The three patterns are listed separately on purpose. .NET matches a
        /// three-character extension pattern against longer extensions when the volume
        /// has 8.3 short names enabled, so "*.sln" sometimes also returns .slnx and
        /// sometimes does not, depending on the machine. Never rely on that.
        /// </summary>
        public static List<string> SolutionFilesIn(string dir)
        {
            var found = new List<string>();
            foreach (var pattern in new[] { "*.sln", "*.slnx", "*.slnf" })
            {
                // A directory on the way up may be unreadable; that is not a reason to
                // stop looking in the ones that are.
                string[] files;
                try { files = Directory.GetFiles(dir, pattern); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var f in files)
                {
                    if (!HasExactExtension(f, pattern.Substring(1))) continue;
                    found.Add(Path.GetFullPath(f));
                }
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        static bool HasExactExtension(string file, string extension) =>
            string.Equals(Path.GetExtension(file), extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// App.sln and App.slnx in one directory are one solution mid-migration, not two.
        /// Collapses them to a single entry, preferring the newer .slnx form.
        /// </summary>
        public static List<string> CollapseSolutionVariants(IEnumerable<string> files)
        {
            var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            foreach (var f in files)
            {
                var dir = Path.GetDirectoryName(f) ?? string.Empty;
                var ext = Path.GetExtension(f);

                // A filter is its own thing to open, never merged with the solution it
                // filters, so it gets a key of its own.
                var stem = dir + "|" + Path.GetFileNameWithoutExtension(f) + (IsFilter(ext) ? "|slnf" : "");

                if (!byStem.ContainsKey(stem))
                {
                    byStem[stem] = f;
                    order.Add(stem);
                    continue;
                }

                // App.sln and App.slnx are the same solution mid-migration. Prefer the
                // newer form; the instance is the authority on which one is really open.
                if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                    byStem[stem] = f;
            }

            var result = new List<string>();
            foreach (var stem in order) result.Add(byStem[stem]);
            return result;
        }

        static bool IsFilter(string ext) =>
            string.Equals(ext, ".slnf", StringComparison.OrdinalIgnoreCase);

        public static string KindOf(string solutionFile)
        {
            if (string.IsNullOrEmpty(solutionFile)) return WorkspaceKind.None;
            switch ((Path.GetExtension(solutionFile) ?? string.Empty).ToLowerInvariant())
            {
                case ".sln": return WorkspaceKind.Sln;
                case ".slnx": return WorkspaceKind.Slnx;
                case ".slnf": return WorkspaceKind.Slnf;
                default: return WorkspaceKind.Folder;
            }
        }
    }
}
