using System;
using System.Collections.Generic;
using System.Linq;

namespace VsDbgMcp.Shim.Profiling
{
    /// <summary>
    /// One profiling run, reduced to the only thing a sampling profile really is: a bag
    /// of stacks with a count against each.
    ///
    /// The trace it came from is tens of megabytes for a few seconds and is thrown away
    /// once this exists, because everything anyone asks afterwards - what is hot, who
    /// calls it, which thread, which module, what changed since last time - is a
    /// different reading of these same counts. Keeping the trace instead would mean
    /// parsing it again for every question and holding a gigabyte to answer each one.
    /// </summary>
    public sealed class Capture
    {
        public int Id { get; set; }
        public string ProcessName { get; set; }
        public int Pid { get; set; }
        public double Seconds { get; set; }

        /// <summary>Samples that landed in this process, whether or not a stack came with them.</summary>
        public int Samples { get; set; }

        /// <summary>Of those, the ones that carried a stack. Only these can be attributed.</summary>
        public int Stacked { get; set; }

        /// <summary>
        /// How often the kernel sampled, taken from the trace rather than assumed. Null
        /// when the trace did not say, and then nothing here reports a share of the wall
        /// clock, because without the rate that number would be invented.
        /// </summary>
        public double? SamplesPerSecond { get; set; }

        /// <summary>
        /// Set when the reader stopped asking the symbol files for source lines. Some
        /// functions then have none, and a report that blamed their symbols for it would
        /// be naming the wrong reason.
        /// </summary>
        public bool LinesRanOut { get; set; }

        /// <summary>Stacks that were deeper than the reader would follow, so are not whole.</summary>
        public int StacksCutShort { get; set; }

        public List<Frame> Frames { get; } = new List<Frame>();
        public List<Stack> Stacks { get; } = new List<Stack>();

        /// <summary>Thread id to the samples taken in it.</summary>
        public Dictionary<int, int> Threads { get; } = new Dictionary<int, int>();

        /// <summary>A function, and what is known about where it lives.</summary>
        public sealed class Frame
        {
            public string Module { get; set; }

            /// <summary>Null when the module had no symbols, and then only the address is known.</summary>
            public string Method { get; set; }

            public string Address { get; set; }
            public string File { get; set; }
            public int Line { get; set; }

            /// <summary>Whether the debugger calls this module the user's own code.</summary>
            public bool UserCode { get; set; }

            /// <summary>
            /// What rows aggregate on. Two frames in one function are one row, and every
            /// frame in a module nothing could name is one row for the whole module: a
            /// module without symbols has an address per sample, and keeping those apart
            /// would bury the functions that do have names under hundreds of rows worth
            /// one sample each.
            /// </summary>
            public string Key => (Module ?? "?") + "!" + (Named ? Collapse(Method) : "(no symbols)");

            public bool Named => !string.IsNullOrEmpty(Method);

            /// <summary>
            /// A template's arguments, taken out and left as an empty pair of brackets.
            ///
            /// One frame in a C++ program can otherwise be two hundred characters of
            /// nested template arguments, which is a whole line of a report spent on a
            /// name that says no more than sleep_for does. Every instantiation of one
            /// template becomes a single row, which is nearly always the wanted answer:
            /// a profile that ranks vector&lt;int&gt; apart from vector&lt;float&gt; has
            /// split one cost in two. The empty brackets are left in so that a name
            /// which was shortened does not read like one that was not.
            /// </summary>
            public static string Collapse(string method)
            {
                if (string.IsNullOrEmpty(method) || method.IndexOf('<') < 0) return method;

                var sb = new System.Text.StringBuilder(method.Length);
                var depth = 0;

                foreach (var c in method)
                {
                    if (c == '<')
                    {
                        depth++;
                        if (depth == 1) sb.Append("<>");
                        continue;
                    }

                    if (c == '>')
                    {
                        if (depth > 0) depth--;
                        continue;
                    }

                    if (depth == 0) sb.Append(c);
                }

                return sb.ToString();
            }
        }

        /// <summary>One call stack, outermost first, and how often it was sampled.</summary>
        public sealed class Stack
        {
            public int[] Frames { get; set; }
            public int ThreadId { get; set; }
            public int Samples { get; set; }
        }

        /// <summary>A line of any of the reports: a name and a count.</summary>
        public sealed class Row
        {
            public string Key { get; set; }
            public string Module { get; set; }
            public string Method { get; set; }
            public string Source { get; set; }
            public bool UserCode { get; set; }
            public bool Named { get; set; }
            public int Samples { get; set; }
        }

        Frame At(int index) => Frames[index];

        Row RowFor(int index, int samples)
        {
            var frame = At(index);
            return new Row
            {
                Key = frame.Key,
                Module = frame.Module,
                Method = frame.Method,
                Source = string.IsNullOrEmpty(frame.File) ? null : Shorten(frame.File) + ":" + frame.Line,
                UserCode = frame.UserCode,
                Named = frame.Named,
                Samples = samples
            };
        }

        static string Shorten(string file)
        {
            var slash = file.LastIndexOfAny(new[] { '\\', '/' });
            return slash < 0 ? file : file.Substring(slash + 1);
        }

        /// <summary>
        /// Where the samples actually landed: the function on top of the stack. This is
        /// the list worth acting on, because it is the code that was running rather than
        /// the code waiting for it.
        /// </summary>
        public List<Row> Self()
        {
            var byFrame = new Dictionary<int, int>();

            foreach (var stack in Stacks)
            {
                if (stack.Frames.Length == 0) continue;
                Count(byFrame, stack.Frames[stack.Frames.Length - 1], stack.Samples);
            }

            return Ranked(byFrame);
        }

        /// <summary>
        /// Every function on the stack, counted once per sample however many times it
        /// appears in one - a recursive function that called itself forty deep was still
        /// only one sample, and counting each level would make it the whole profile.
        /// </summary>
        public List<Row> Inclusive()
        {
            var byFrame = new Dictionary<int, int>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var stack in Stacks)
            {
                seen.Clear();
                foreach (var index in stack.Frames)
                {
                    if (seen.Add(At(index).Key)) Count(byFrame, index, stack.Samples);
                }
            }

            return Ranked(byFrame);
        }

        /// <summary>Self samples gathered by the binary they landed in.</summary>
        public List<Row> ByModule()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var user = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var stack in Stacks)
            {
                if (stack.Frames.Length == 0) continue;

                var frame = At(stack.Frames[stack.Frames.Length - 1]);
                var name = frame.Module ?? "?";
                counts[name] = counts.TryGetValue(name, out var had) ? had + stack.Samples : stack.Samples;
                user[name] = frame.UserCode;
            }

            return counts
                .Select(c => new Row { Key = c.Key, Module = c.Key, Samples = c.Value, UserCode = user[c.Key], Named = true })
                .OrderByDescending(r => r.Samples)
                .ToList();
        }

        /// <summary>
        /// The functions that called this one, and the ones it called, each counted by
        /// the samples that went through that edge. This is what says whether a function
        /// is worth fixing where it is or where it is called from.
        /// </summary>
        public List<Row> Callers(string key) => Neighbours(key, -1);

        public List<Row> Callees(string key) => Neighbours(key, 1);

        List<Row> Neighbours(string key, int direction)
        {
            var byFrame = new Dictionary<int, int>();

            foreach (var stack in Stacks)
            {
                for (var i = 0; i < stack.Frames.Length; i++)
                {
                    if (!Same(At(stack.Frames[i]).Key, key)) continue;

                    var side = i + direction;
                    if (side < 0 || side >= stack.Frames.Length) continue;

                    Count(byFrame, stack.Frames[side], stack.Samples);
                }
            }

            return Ranked(byFrame);
        }

        /// <summary>
        /// Which source lines of one function the samples landed on, when symbols carry
        /// lines. This is the difference between knowing a function is slow and knowing
        /// what to change in it.
        /// </summary>
        public List<Row> Lines(string key)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var stack in Stacks)
            {
                if (stack.Frames.Length == 0) continue;

                var leaf = At(stack.Frames[stack.Frames.Length - 1]);
                if (!Same(leaf.Key, key) || string.IsNullOrEmpty(leaf.File)) continue;

                var at = Shorten(leaf.File) + ":" + leaf.Line;
                counts[at] = counts.TryGetValue(at, out var had) ? had + stack.Samples : stack.Samples;
                order[at] = leaf.Line;
            }

            return counts
                .Select(c => new Row { Key = c.Key, Source = c.Key, Samples = c.Value, Named = true })
                .OrderByDescending(r => r.Samples)
                .ToList();
        }

        /// <summary>Samples per thread, busiest first.</summary>
        public List<KeyValuePair<int, int>> ByThread() =>
            Threads.OrderByDescending(t => t.Value).ToList();

        /// <summary>What that thread was mostly doing, by self samples.</summary>
        public Row BusiestIn(int threadId)
        {
            var byFrame = new Dictionary<int, int>();

            foreach (var stack in Stacks)
            {
                if (stack.ThreadId != threadId || stack.Frames.Length == 0) continue;
                Count(byFrame, stack.Frames[stack.Frames.Length - 1], stack.Samples);
            }

            return Ranked(byFrame).FirstOrDefault();
        }

        /// <summary>
        /// The one path the program spent most of its time down, found by walking from
        /// the outermost frame into the heaviest child until no child carries enough of
        /// what is left to be worth naming.
        ///
        /// A single most-sampled stack would be a worse answer: work spread over a dozen
        /// leaf functions under one hot caller has no single stack that stands out, and
        /// the caller is the thing worth knowing about.
        /// </summary>
        public List<Row> HotPath(double keepAbove = 0.2, int depthLimit = 10)
        {
            var path = new List<Row>();
            var live = Stacks.Where(s => s.Frames.Length > 0).ToList();
            var total = live.Sum(s => s.Samples);
            if (total == 0) return path;

            var from = EntryDepth();
            for (var depth = from; depth < 64 && path.Count < depthLimit; depth++)
            {
                var byFrame = new Dictionary<int, int>();

                foreach (var stack in live)
                {
                    if (depth < stack.Frames.Length) Count(byFrame, stack.Frames[depth], stack.Samples);
                }

                if (byFrame.Count == 0) break;

                var best = Ranked(byFrame)[0];
                if (best.Samples < total * keepAbove) break;

                path.Add(best);
                live = live.Where(s => depth < s.Frames.Length && At(s.Frames[depth]).Key == best.Key).ToList();
            }

            return path;
        }

        /// <summary>One line of the call tree: a row, and how deep it sits.</summary>
        public sealed class Node
        {
            public Row Row { get; set; }
            public int Depth { get; set; }
        }

        /// <summary>
        /// The call tree by inclusive samples, pruned to what carries enough to matter.
        ///
        /// Inclusive counts have to be read as a tree rather than a ranked list: the
        /// outermost function is always the whole profile, and a flat list sorted that
        /// way puts it on top and says nothing. What is worth reading is where the time
        /// splits, and that is a shape, not an order.
        /// </summary>
        /// <summary>
        /// Whether the last Tree call stopped at its row limit rather than because
        /// everything left was too small to matter. The two look identical on the page.
        /// </summary>
        public bool TreeWasCut { get; private set; }

        public List<Node> Tree(double prune = 0.01, int limit = 40)
        {
            var nodes = new List<Node>();
            TreeWasCut = false;
            var live = Stacks.Where(s => s.Frames.Length > 0).ToList();
            var total = live.Sum(s => s.Samples);
            if (total == 0) return nodes;

            var from = EntryDepth();
            Walk(live, from, from, total, prune, limit, nodes);
            TreeWasCut = nodes.Count >= limit;
            return nodes;
        }

        /// <summary>
        /// How many frames every stack shares before the program's own code begins.
        ///
        /// A stack starts in the loader and the thread entry point, and those frames are
        /// on every sample and hold the whole profile. Reporting them costs four lines
        /// that all read 100% and say nothing about where the time went. Trimming stops
        /// at the first frame that is the user's own code or the first place the stacks
        /// disagree, so nothing that carries information is ever cut.
        /// </summary>
        public int EntryDepth()
        {
            var live = Stacks.Where(s => s.Frames.Length > 0).ToList();
            if (live.Count == 0) return 0;

            var depth = 0;
            while (depth < 8)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                var reached = false;
                var user = false;

                foreach (var stack in live)
                {
                    if (depth >= stack.Frames.Length) continue;

                    reached = true;
                    var frame = At(stack.Frames[depth]);
                    keys.Add(frame.Key);
                    if (frame.UserCode) user = true;
                }

                if (!reached || user || keys.Count != 1) break;
                depth++;
            }

            return depth;
        }

        void Walk(List<Stack> live, int depth, int from, int total, double prune, int limit, List<Node> nodes)
        {
            if (depth > 48 || nodes.Count >= limit) return;

            var byFrame = new Dictionary<int, int>();

            foreach (var stack in live)
            {
                if (depth < stack.Frames.Length) Count(byFrame, stack.Frames[depth], stack.Samples);
            }

            foreach (var row in Ranked(byFrame))
            {
                if (row.Samples < total * prune) break;
                if (nodes.Count >= limit) return;

                nodes.Add(new Node { Row = row, Depth = depth - from });
                Walk(live.Where(s => depth < s.Frames.Length && At(s.Frames[depth]).Key == row.Key).ToList(),
                     depth + 1, from, total, prune, limit, nodes);
            }
        }

        /// <summary>What one function's share did between two profiles.</summary>
        public sealed class Change
        {
            public string Key { get; set; }
            public double Before { get; set; }
            public double After { get; set; }
            public double Points => (After - Before) * 100.0;
            public bool IsNew { get; set; }
            public bool Gone { get; set; }
        }

        /// <summary>
        /// How the shares moved between two profiles, biggest movement first.
        ///
        /// Shares rather than sample counts, because two collections almost never ran
        /// for the same length of time and comparing counts across them would report a
        /// shorter run as an improvement.
        /// </summary>
        public static List<Change> Compare(Capture before, Capture after)
        {
            var was = Shares(before);
            var now = Shares(after);
            var changes = new List<Change>();

            foreach (var key in was.Keys.Union(now.Keys, StringComparer.Ordinal))
            {
                var b = was.TryGetValue(key, out var hadBefore) ? hadBefore : 0.0;
                var a = now.TryGetValue(key, out var hasNow) ? hasNow : 0.0;

                changes.Add(new Change { Key = key, Before = b, After = a, IsNew = b == 0, Gone = a == 0 });
            }

            return changes.OrderByDescending(c => Math.Abs(c.Points)).ToList();
        }

        static Dictionary<string, double> Shares(Capture capture)
        {
            var total = Math.Max(1, capture.Stacks.Sum(s => s.Samples));
            return capture.Self().ToDictionary(r => r.Key, r => r.Samples / (double)total, StringComparer.Ordinal);
        }

        /// <summary>Modules that took time while nothing could name what in them, worst first.</summary>
        public List<Row> Unresolved()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var stack in Stacks)
            {
                if (stack.Frames.Length == 0) continue;

                var frame = At(stack.Frames[stack.Frames.Length - 1]);
                if (frame.Named) continue;

                var name = frame.Module ?? "?";
                counts[name] = counts.TryGetValue(name, out var had) ? had + stack.Samples : stack.Samples;
            }

            return counts
                .Select(c => new Row { Key = c.Key, Module = c.Key, Samples = c.Value })
                .OrderByDescending(r => r.Samples)
                .ToList();
        }

        /// <summary>
        /// How many seconds of processor time this process used, or null when the trace
        /// did not say how often it sampled.
        ///
        /// Processor time rather than a share of the wall clock, because a process on
        /// four busy threads uses four seconds of processor in one second of wall clock
        /// and a share would read as 400%. Set against the wall clock it answers the
        /// question that matters either way: a program that used a tenth of a second in
        /// nine was waiting, and waiting is what sampling cannot see.
        /// </summary>
        public double? CpuSeconds()
        {
            if (SamplesPerSecond == null || SamplesPerSecond.Value <= 0) return null;
            return Samples / SamplesPerSecond.Value;
        }

        /// <summary>
        /// Rows from samples counted against individual frames.
        ///
        /// One function is many frames, one per address a sample landed on, and a row
        /// has room for a single source line. It shows the line most of the samples
        /// landed on rather than whichever frame happened to be seen first: a line
        /// printed beside a hot function reads as the hot line, so an arbitrary one is
        /// a wrong answer rather than a missing one.
        /// </summary>
        List<Row> Ranked(Dictionary<int, int> byFrame)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var heaviest = new Dictionary<string, int>(StringComparer.Ordinal);
            var weight = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var entry in byFrame)
            {
                var key = At(entry.Key).Key;
                counts[key] = counts.TryGetValue(key, out var had) ? had + entry.Value : entry.Value;

                if (weight.TryGetValue(key, out var most) && most >= entry.Value) continue;

                weight[key] = entry.Value;
                heaviest[key] = entry.Key;
            }

            return counts
                .Select(c => RowFor(heaviest[c.Key], c.Value))
                .OrderByDescending(r => r.Samples)
                .ThenBy(r => r.Key, StringComparer.Ordinal)
                .ToList();
        }

        static void Count(Dictionary<int, int> byFrame, int index, int samples) =>
            byFrame[index] = byFrame.TryGetValue(index, out var had) ? had + samples : samples;

        /// <summary>
        /// Matches a row by what a report printed, by the function without its module, or
        /// by the bare name without whatever namespace it sits in.
        ///
        /// A caller reads Worker out of a hot path that printed it as
        /// module!`anonymous namespace'::Worker and asks about Worker, which is the name
        /// in their own source. Insisting on the printed form would refuse the only name
        /// they have.
        /// </summary>
        public static bool Same(string key, string asked)
        {
            if (string.IsNullOrEmpty(asked)) return false;

            var folded = Frame.Collapse(asked.Trim());
            if (Is(key, folded)) return true;

            var bang = key.IndexOf('!');
            var method = bang < 0 ? key : key.Substring(bang + 1);
            if (Is(method, folded)) return true;

            var scope = method.LastIndexOf("::", StringComparison.Ordinal);
            return scope >= 0 && Is(method.Substring(scope + 2), folded);
        }

        static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Every row an asked-for name could mean. More than one is not an answer: two
        /// functions of the same name in different namespaces are different costs, and
        /// picking whichever came first would report one as the other.
        /// </summary>
        public List<string> Matches(string asked)
        {
            var found = new List<string>();

            foreach (var frame in Frames)
            {
                if (Same(frame.Key, asked) && !found.Contains(frame.Key, StringComparer.Ordinal))
                    found.Add(frame.Key);
            }

            return found;
        }
    }
}
