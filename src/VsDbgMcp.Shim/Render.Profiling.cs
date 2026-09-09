using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VsDbgMcp.Shim.Profiling;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// Profiles as text.
    ///
    /// A person reading a profiler expands a tree until they find the answer. Nothing
    /// here can be expanded, so every reply has to be short enough to read at once and
    /// has to say, on the line where it stopped saying more, exactly which argument
    /// says more. A reply that folds something away without that is a dead end, and the
    /// only way out of it is collecting the whole profile again.
    /// </summary>
    public static partial class Render
    {
        /// <summary>Below this a ranking is noise wearing a percentage sign.</summary>
        const int TooFewSamples = 100;

        /// <summary>
        /// How far down the hot path is worth printing. A thread parked in the kernel
        /// has twenty frames of one path and reading them costs more than the answer,
        /// which is in the first few.
        /// </summary>
        const int HotPathDepth = 10;

        public static string Profile(Capture capture, ProfileQuery query, Capture against,
            IReadOnlyList<Capture> taken)
        {
            if (capture == null) return "No profile has been taken. Call profile_start, let the program work, then profile_stop.";

            var sb = new StringBuilder();
            var scope = Scope(capture, query, against);
            var total = Denominator(capture, query);

            sb.Append("capture #").Append(capture.Id).Append("  ")
              .Append(capture.Seconds.ToString("F1", CultureInfo.InvariantCulture)).Append("s  ")
              .Append(Samples(capture.Stacked)).Append("  ")
              .Append(capture.ProcessName).Append(" (").Append(capture.Pid).Append(")  ")
              .AppendLine(scope);

            if (capture.Metadata != null)
                sb.Append("owner: ").Append(capture.Metadata.InstanceId).Append(" / ").Append(capture.Metadata.HostEpoch)
                  .Append(" generation ").Append(capture.Metadata.SessionGeneration).Append("  captureId ").AppendLine(capture.CaptureId);
            if (capture.Metadata != null && query.Details)
            {
                sb.Append("started UTC: ").Append(capture.Metadata.StartedUtc.ToString("O"))
                  .Append("; local: ").AppendLine(capture.Metadata.StartedUtc.ToLocalTime().ToString("O"));
                if (capture.Metadata.Interventions != null)
                    foreach (var marker in capture.Metadata.Interventions)
                        sb.Append("intervention ").Append(marker.TimestampUtc.ToString("O")).Append(": ").Append(marker.Kind)
                          .Append(" ").Append(marker.Detail).AppendLine("; target pause duration unmeasured");
            }
            sb.Append("denominator: ").Append(total).Append(query.Thread != null ? " stacked samples in selected thread" : " stacked samples in whole capture").AppendLine();
            var selected = SelectProfile(capture, query, out var selectionError);
            if (selectionError != null) sb.AppendLine(selectionError);
            else if (against != null) Diff(sb, capture, against);
            else if (query.Function != null) Function(sb, selected, query, total);
            else if (query.Sort == ProfileQuery.Inclusive) Tree(sb, selected, total, query);
            else if (query.Sort == ProfileQuery.ByModule) Modules(sb, selected, total);
            else if (query.Thread != null && query.Module == null) OneThread(sb, selected, query, total);
            else Summary(sb, selected, query, total);

            if (query.Details) { Notes(sb, capture); Footer(sb, capture, taken); }
            else
            {
                if (capture.Stacked < capture.Samples) sb.AppendLine("Quality: some samples have no stacks; details=true shows coverage.");
                if (capture.Metadata?.Status == "interrupted") sb.AppendLine("Applicability: debugger session changed during this capture; inspect intervention markers.");
                sb.AppendLine("CPU samples only; blocked time is not measured. details=true shows coverage.");
            }
            return sb.ToString().TrimEnd();
        }

        static Capture SelectProfile(Capture capture, ProfileQuery query, out string error)
        {
            error = null;
            var selected = new Capture { Id = capture.Id, CaptureId = capture.CaptureId, Metadata = capture.Metadata,
                ProcessName = capture.ProcessName, Pid = capture.Pid, Seconds = capture.Seconds,
                Samples = capture.Samples, Stacked = capture.Stacked, SamplesPerSecond = capture.SamplesPerSecond };
            selected.Frames.AddRange(capture.Frames);
            foreach (var thread in capture.Threads) selected.Threads[thread.Key] = thread.Value;
            string focus = null;
            if (query.Focus != null)
            {
                var matches = capture.Matches(query.Focus);
                if (matches.Count != 1) { error = "focus must identify one captured function. Candidates: " + string.Join(", ", matches.Take(8)); return selected; }
                focus = matches[0];
            }
            foreach (var stack in capture.Stacks)
            {
                if (query.Thread != null && stack.ThreadId != query.Thread) continue;
                var frames = stack.Frames;
                if (query.Sort == ProfileQuery.Inclusive && (focus != null || query.Module != null))
                {
                    var from = Array.FindIndex(frames, n => (focus == null || capture.Frames[n].Key == focus) &&
                        (query.Module == null || Contains(capture.Frames[n].Module, query.Module)));
                    if (from < 0) continue;
                    frames = frames.Skip(from).ToArray();
                }
                else if (query.Module != null && query.Sort == ProfileQuery.ByModule &&
                    (frames.Length == 0 || !Contains(capture.Frames[frames[frames.Length - 1]].Module, query.Module))) continue;
                selected.Stacks.Add(new Capture.Stack { Frames = frames, ThreadId = stack.ThreadId, Samples = stack.Samples });
            }
            return selected;
        }

        static string Scope(Capture capture, ProfileQuery query, Capture against)
        {
            if (against != null) return "against #" + against.Id;
            if (query.Function != null) return "one function";
            if (query.Thread != null) return "thread " + query.Thread.Value + " only";
            if (query.Sort == ProfileQuery.Inclusive) return "inclusive, as a tree" + (query.Module == null ? "" : ", subtrees rooted in " + query.Module);
            if (query.Sort == ProfileQuery.ByModule) return "self time, by module";
            if (query.Module != null) return "self time in " + query.Module + ", of the whole capture";
            return "self time, all threads";
        }

        /// <summary>
        /// What the percentages are shares of. Always said in the header, because a
        /// filtered report whose percentages are still of the whole profile and a
        /// filtered report whose percentages are of the filter look identical and mean
        /// opposite things.
        /// </summary>
        static int Denominator(Capture capture, ProfileQuery query)
        {
            if (query.Thread != null)
                return Math.Max(1, capture.Stacks.Where(s => s.ThreadId == query.Thread.Value).Sum(s => s.Samples));

            return Math.Max(1, capture.Stacks.Sum(s => s.Samples));
        }

        static void Summary(StringBuilder sb, Capture capture, ProfileQuery query, int total)
        {
            var rows = capture.Self();
            if (query.Module != null)
            {
                rows = rows.Where(r => Contains(r.Module, query.Module)).ToList();
                if (rows.Count == 0)
                {
                    sb.Append("  No samples landed in a module whose name contains ").Append(query.Module)
                      .AppendLine(". These took some:");

                    var sampled = capture.ByModule();
                    foreach (var one in sampled.Take(12))
                        sb.Append("    ").Append(one.Module).Append("  ").AppendLine(Samples(one.Samples));

                    if (sampled.Count > 12)
                        sb.Append("    and ").Append(sampled.Count - 12).AppendLine(" more");
                    return;
                }
            }

            Flat(sb, rows, query, total);

            // The path and the threads are of the whole profile. Printing them under a
            // report filtered to one module would put unfiltered numbers below a header
            // that says the report was filtered.
            if (query.Module != null) return;

            var path = capture.HotPath(0.2, HotPathDepth);
            if (path.Count > 1)
            {
                var share = path[path.Count - 1].Samples * 100.0 / total;
                sb.AppendLine().Append("hot path, ").Append(share.ToString("F0", CultureInfo.InvariantCulture))
                  .AppendLine("% of samples reach the end of it");
                sb.Append("  ").AppendLine(string.Join(" -> ", path.Select(r => r.Key).ToArray()));

                var trimmed = capture.EntryDepth();
                if (trimmed > 0)
                    sb.Append("  above it, ").Append(trimmed)
                      .AppendLine(" frames of thread start-up that every sample shares");

                if (path.Count >= HotPathDepth)
                    sb.AppendLine("  the path goes on below this; it is cut here to stay readable");
            }

            Threads(sb, capture, total);
        }

        static void Flat(StringBuilder sb, List<Capture.Row> rows, ProfileQuery query, int total)
        {
            if (rows.Count == 0)
            {
                sb.AppendLine("  (nothing was sampled here)");
                return;
            }

            var shown = 0;
            var hidden = 0;
            var hiddenSamples = 0;

            foreach (var row in rows)
            {
                if (shown < query.Top)
                {
                    Line(sb, row.Samples, total, row.Key, row.Source);
                    shown++;
                    continue;
                }

                hidden++;
                hiddenSamples += row.Samples;
            }

            if (hidden > 0)
                Line(sb, hiddenSamples, total, hidden + " more, each smaller than the last shown", "top: " + (query.Top + hidden));
        }

        static void OneThread(StringBuilder sb, Capture capture, ProfileQuery query, int total)
        {
            var rows = InThread(capture, query.Thread.Value);
            if (rows.Count > 0)
            {
                Flat(sb, rows, query, total);
                return;
            }

            // A thread id that took no samples and a thread id that was never there read
            // the same, and only one of them means the caller mistyped it.
            sb.Append("  No samples were taken in thread ").Append(query.Thread.Value)
              .AppendLine(". These threads have some:");

            var all = capture.ByThread();
            foreach (var thread in all.Take(12))
                sb.Append("    ").Append(thread.Key).Append("  ").AppendLine(Samples(thread.Value));

            if (all.Count > 12) sb.Append("    and ").Append(all.Count - 12).AppendLine(" more");
        }

        static void Tree(StringBuilder sb, Capture capture, int total, ProfileQuery query)
        {
            var nodes = capture.Tree(query.RawTree ? 0 : 0.01, query.Top, query.RawTree || query.Focus != null || query.Module != null);
            if (nodes.Count == 0)
            {
                sb.AppendLine("  (nothing was sampled)");
                return;
            }

            foreach (var node in nodes)
                Line(sb, node.Row.Samples, total, new string(' ', node.Depth * 2) + node.Row.Key, null);

            sb.AppendLine("  inclusive percentages overlap; do not sum them.");
            if (!query.RawTree) sb.Append("  branches under 1% are left out").AppendLine(Trimmed(capture));
            if (capture.TreeWasCut)
                sb.Append("  the tree is cut off at ").Append(nodes.Count)
                  .AppendLine(" rows; increase top or use focus=FUNCTION with sort=inclusive to inspect omitted branches.");
        }

        /// <summary>
        /// Says when frames were trimmed off the top. They are the same on every sample
        /// and hold the whole profile, so leaving them in costs lines that all read 100%
        /// - but leaving them out silently would be a stack that is not the stack.
        /// </summary>
        static string Trimmed(Capture capture)
        {
            var depth = capture.EntryDepth();
            return depth == 0 ? "" : ", as are the " + depth + " frames of thread start-up above the root";
        }

        static void Modules(StringBuilder sb, Capture capture, int total)
        {
            foreach (var row in capture.ByModule())
                Line(sb, row.Samples, total, row.Module + (row.UserCode ? "  user code" : ""), null);
        }

        static void Function(StringBuilder sb, Capture capture, ProfileQuery query, int total)
        {
            var found = capture.Matches(query.Function);
            if (query.Module != null) found = found.Where(k => capture.Frames.Any(f => f.Key == k && Contains(f.Module, query.Module))).ToList();
            if (found.Count == 0)
            {
                sb.Append("  Nothing called ").Append(query.Function)
                  .AppendLine(" was sampled. A function that never had a sample land in it or under it ")
                  .AppendLine("  is not in this profile at all; the report without a function names what is.");
                var tokens = query.Function.Split(new[] { ':', '!', '<', '>' }, StringSplitOptions.RemoveEmptyEntries);
                var candidates = capture.Inclusive().Where(r => tokens.Any(t => t.Length > 2 && Contains(r.Key, t))).Take(5).ToList();
                foreach (var candidate in candidates) sb.Append("    candidate: ").Append(candidate.Key).Append("  inclusive ").AppendLine(Samples(candidate.Samples));
                return;
            }

            if (found.Count > 1)
            {
                sb.Append("  ").Append(query.Function).AppendLine(" could be any of these, which are different costs:");
                foreach (var candidate in found.Take(8)) sb.Append("    ").AppendLine(candidate);
                if (found.Count > 8) sb.Append("    and ").Append(found.Count - 8).AppendLine(" more");
                sb.AppendLine("  Name one of them.");
                return;
            }

            var key = found[0];

            var self = capture.Self().FirstOrDefault(r => r.Key == key);
            var inclusive = capture.Inclusive().FirstOrDefault(r => r.Key == key);

            sb.Append("  ").Append(key).Append("   self ").Append(Share(self == null ? 0 : self.Samples, total))
              .Append(", inclusive ").AppendLine(Share(inclusive == null ? 0 : inclusive.Samples, total));

            Neighbours(sb, "called from", capture.Callers(key), total);
            Neighbours(sb, "calls", capture.Callees(key), total);

            var lines = capture.Lines(key);
            sb.AppendLine().AppendLine("lines");

            if (lines.Count == 0)
                sb.AppendLine(capture.LinesRanOut
                    ? "  none: this profile had more distinct addresses than lines were looked up for, so " +
                      "some functions have none. That is a limit of the reading, not of the symbols."
                    : "  none: source lines are read from the symbols of the debuggee's own modules, and " +
                      "this function is not in one of those or its symbols carry no line numbers");

            foreach (var line in lines.Take(query.Top))
                Line(sb, line.Samples, total, line.Source, null);
        }

        static void Neighbours(StringBuilder sb, string title, List<Capture.Row> rows, int total)
        {
            sb.AppendLine().AppendLine(title);
            if (rows.Count == 0)
            {
                sb.AppendLine("  (nothing, in every sample that reached it)");
                return;
            }

            foreach (var row in rows.Take(8)) Line(sb, row.Samples, total, row.Key, row.Source);

            if (rows.Count > 8)
                sb.Append("  and ").Append(rows.Count - 8).AppendLine(" more, each smaller than those");
        }

        static void Threads(StringBuilder sb, Capture capture, int total)
        {
            var threads = capture.ByThread();
            if (threads.Count == 0) return;

            sb.AppendLine().AppendLine(threads.Count == 1 ? "thread" : "threads");
            var shown = 0;

            foreach (var thread in threads)
            {
                if (shown == 6)
                {
                    sb.Append("  ").Append(threads.Count - shown).AppendLine(" more, quieter still");
                    break;
                }

                var busiest = capture.BusiestIn(thread.Key);
                sb.Append("  ").Append(thread.Key.ToString().PadRight(8))
                  .Append(Share(thread.Value, total).PadLeft(7))
                  .Append("  ").AppendLine(busiest == null ? "(no stacks)" : "mostly " + busiest.Key);
                shown++;
            }
        }

        static void Diff(StringBuilder sb, Capture now, Capture before)
        {
            sb.Append("  ").Append(before.Seconds.ToString("F1", CultureInfo.InvariantCulture)).Append("s and ")
              .Append(Samples(before.Stacked)).Append(" then, ")
              .Append(now.Seconds.ToString("F1", CultureInfo.InvariantCulture)).Append("s and ")
              .Append(Samples(now.Stacked)).AppendLine(" now");
            sb.AppendLine("  shares, not counts: two runs of different lengths cannot be compared any other way");
            sb.AppendLine();

            if (before.Metadata != null && now.Metadata != null &&
                (before.Metadata.Executable != now.Metadata.Executable || before.Metadata.Configuration != now.Metadata.Configuration))
                sb.AppendLine("Applicability: executable or configuration differs between these captures.");
            if ((before.Stacked < before.Samples) != (now.Stacked < now.Samples))
                sb.AppendLine("Coverage: unattributed sample coverage differs between captures.");
            var changes = Capture.Compare(before, now);
            var moved = 0;
            var still = 0;

            foreach (var change in changes)
            {
                if (Math.Abs(change.Points) < 1.0) { still++; continue; }
                if (moved == 15) { still++; continue; }

                var moveText = (change.Points > 0 ? "+" : "") +
                               change.Points.ToString("F1", CultureInfo.InvariantCulture) + "pp";

                sb.Append("  ").Append(Percent(change.Before * 100).PadLeft(6)).Append(" -> ")
                  .Append(Percent(change.After * 100).PadLeft(6)).Append("   ")
                  .Append(moveText.PadRight(9)).Append(change.Key);

                if (change.IsNew) sb.Append("   new");
                else if (change.Gone) sb.Append("   gone");
                sb.AppendLine();
                moved++;
            }

            if (moved == 0) sb.AppendLine("  nothing moved by as much as a point");
            if (still > 0) sb.Append("  ").Append(still).AppendLine(" more within a point of where they were");
        }

        /// <summary>
        /// What the numbers above do not cover. Each of these is a way for a profile to
        /// be read as saying something it did not say.
        /// </summary>
        static void Notes(StringBuilder sb, Capture capture)
        {
            var notes = new List<string>();

            if (capture.Stacked < TooFewSamples)
                notes.Add("Only " + Samples(capture.Stacked) + ", which is too few to rank: one sample either " +
                          "way moves any of these lines. Profile for longer.");

            var cpu = capture.CpuSeconds();
            if (cpu == null)
            {
                // Saying nothing here would leave the one thing sampling cannot see
                // unmentioned, which is how a profile of a blocked program comes to read
                // as a program with nothing slow in it.
                notes.Add("The trace did not record how often it sampled, so how much processor time this " +
                          "actually used cannot be worked out from it. Sampling sees only threads on a " +
                          "processor, so time spent waiting is missing from everything above.");
            }
            else
            {
                var used = cpu.Value.ToString("F1", CultureInfo.InvariantCulture);
                var clock = capture.Seconds.ToString("F1", CultureInfo.InvariantCulture);

                notes.Add(cpu.Value < capture.Seconds * 0.5
                    ? "It used " + used + " seconds of processor time in " + clock + " seconds of wall clock, " +
                      "so it spent most of that waiting - on a lock, on a file, on another thread - and " +
                      "sampling sees none of that. What is ranked above is only the part that was running."
                    : "It used " + used + " seconds of processor time in " + clock + " seconds of wall clock, " +
                      "so what is ranked above accounts for most of what it was doing.");
            }

            if (capture.StacksCutShort > 0)
                notes.Add(Samples(capture.StacksCutShort) + " came from stacks deeper than 128 frames. The " +
                          "innermost 128 were kept, so the outermost callers of those are missing.");

            var stackless = capture.Samples - capture.Stacked;
            if (stackless > 0)
                notes.Add("A further " + Samples(stackless) + " arrived without a stack, left out of everything " +
                          "above because there is nothing to attribute them to. They are why the seconds of " +
                          "processor time account for more than the rows do.");

            var blind = capture.Unresolved();
            var total = Math.Max(1, capture.Stacks.Sum(s => s.Samples));
            var worst = blind.Where(r => r.Samples > total * 0.02).ToList();
            if (worst.Count > 3)
                notes.Add(worst.Count + " modules took time with no symbols; the three largest are named below.");

            foreach (var row in worst.Take(3))
                notes.Add(row.Module + " took " + Share(row.Samples, total) +
                          " and has no symbols, so its frames are one row rather than functions. " +
                          "symbols(\"" + row.Module + "\", load: true) names them.");

            if (notes.Count == 0) return;

            sb.AppendLine();
            foreach (var note in notes) sb.Append("note: ").AppendLine(note);
        }

        static void Footer(StringBuilder sb, Capture capture, IReadOnlyList<Capture> taken)
        {
            if (taken == null || taken.Count == 0) return;

            sb.AppendLine();
            sb.Append("captures:");
            foreach (var other in taken)
            {
                sb.Append("  #").Append(other.Id).Append(" ")
                  .Append(other.Seconds.ToString("F0", CultureInfo.InvariantCulture)).Append("s ")
                  .Append(Samples(other.Stacked));
                if (other.Id == capture.Id) sb.Append(" (this)");
            }
            sb.AppendLine();
        }

        static List<Capture.Row> InThread(Capture capture, int threadId)
        {
            var rows = new List<Capture.Row>();
            var one = capture.BusiestIn(threadId);
            if (one == null) return rows;

            // Everything sampled in that thread, ranked, by asking the capture for the
            // whole ranking and keeping what this thread contributed.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var stack in capture.Stacks)
            {
                if (stack.ThreadId != threadId || stack.Frames.Length == 0) continue;

                var key = capture.Frames[stack.Frames[stack.Frames.Length - 1]].Key;
                counts[key] = counts.TryGetValue(key, out var had) ? had + stack.Samples : stack.Samples;
            }

            foreach (var row in capture.Self())
            {
                if (!counts.TryGetValue(row.Key, out var mine)) continue;
                rows.Add(new Capture.Row { Key = row.Key, Module = row.Module, Method = row.Method, Source = row.Source, Samples = mine });
            }

            return rows.OrderByDescending(r => r.Samples).ToList();
        }

        static void Line(StringBuilder sb, int samples, int total, string what, string trailer)
        {
            sb.Append("  ").Append(Share(samples, total).PadLeft(6)).Append("  ")
              .Append(Count(samples).PadLeft(6)).Append("  ").Append(what);

            if (!string.IsNullOrEmpty(trailer)) sb.Append("   ").Append(trailer);
            sb.AppendLine();
        }

        static string Share(int samples, int total) => Percent(samples * 100.0 / Math.Max(1, total));

        static string Percent(double value) => value > 0 && value < 0.1 ? "<0.1%" : value.ToString("F1", CultureInfo.InvariantCulture) + "%";

        static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

        static string Samples(int value) => Count(value) + (value == 1 ? " sample" : " samples");

        static bool Contains(string text, string part) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
