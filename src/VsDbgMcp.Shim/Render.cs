using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// Turns results into compact text. Every tool answers a question; none of them
    /// hand back a serialized object for the caller to interpret.
    /// </summary>
    public static class Render
    {
        public static string Instances(IReadOnlyList<HostLink> links, string cwd, string sticky)
        {
            if (links == null || links.Count == 0)
                return "No Visual Studio instance is running with the vsdbgmcp extension loaded.";

            var sb = new StringBuilder();
            sb.Append("cwd: ").AppendLine(cwd);
            foreach (var link in links.OrderBy(l => l.Id, StringComparer.OrdinalIgnoreCase))
            {
                var r = link.Record;
                var marks = new List<string>();
                if (string.Equals(r.Id, sticky, StringComparison.OrdinalIgnoreCase)) marks.Add("default");
                if (!link.IsConnected) marks.Add("disconnected");
                if (r.Workspace?.Filter != null) marks.Add("filter " + System.IO.Path.GetFileName(r.Workspace.Filter));

                sb.Append("  ").Append(r.Id.PadRight(20));
                sb.Append("  ").Append((r.DebugMode ?? DebugModes.Design).PadRight(7));
                sb.Append("  ").Append(r.Workspace?.File ?? r.Workspace?.Root ?? "(nothing open)");
                sb.Append("  vs").Append(Major(r.VsVersion));
                if (marks.Count > 0) sb.Append("  [").Append(string.Join(", ", marks)).Append(']');
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        static string Major(string version)
        {
            if (string.IsNullOrEmpty(version)) return "?";
            var dot = version.IndexOf('.');
            return dot > 0 ? version.Substring(0, dot) : version;
        }

        static string Process(string name, int pid)
        {
            if (string.IsNullOrEmpty(name)) return pid == 0 ? null : pid.ToString();
            return pid == 0 ? name : name + " (" + pid + ")";
        }

        public static string Status(HostStatus s)
        {
            if (s == null) return "No status returned.";

            var sb = new StringBuilder();
            sb.Append(s.InstanceId).Append("  ").Append(s.Mode ?? DebugModes.Design);
            if (!string.IsNullOrEmpty(s.ActiveConfiguration)) sb.Append("  ").Append(s.ActiveConfiguration);
            if (!string.IsNullOrEmpty(s.StartupProject)) sb.Append("  startup: ").Append(s.StartupProject);
            sb.AppendLine();

            if (s.Workspace != null)
                sb.Append("workspace: ").AppendLine(s.Workspace.File ?? s.Workspace.Root);

            if (s.PendingException != null)
                sb.Append("exception: ").AppendLine(Exception(s.PendingException));

            if (s.Processes != null && s.Processes.Count > 0)
            {
                sb.Append("processes: ");
                sb.AppendLine(string.Join(", ", s.Processes.Select(p => p.Name + " (" + p.Pid + ")")));
            }

            if (s.TopFrames != null && s.TopFrames.Count > 0)
            {
                sb.Append("thread ").Append(s.CurrentThreadId);

                var process = Process(s.CurrentProcessName, s.CurrentPid);
                if (!string.IsNullOrEmpty(process)) sb.Append(" in ").Append(process);
                if (s.ThreadWasSelected) sb.Append(" (selected)");

                // Several processes and no explicit pick is exactly when a caller ends up
                // reading one process while meaning another.
                if (!s.ThreadWasSelected && s.Processes != null && s.Processes.Count(p => p.IsDebugged) > 1)
                    sb.Append("  -- more than one process; threads lists them all, select picks one");

                sb.AppendLine(":");
                sb.AppendLine(Frames(s.TopFrames, s.CurrentFrameIndex));
            }

            if (s.Watches != null && s.Watches.Count > 0)
            {
                sb.AppendLine("watches:");
                foreach (var kv in s.Watches)
                    sb.Append("  ").Append(kv.Key).Append(" = ").AppendLine(kv.Value);
            }

            sb.Append("breakpoints: ").Append(s.BreakpointCount);
            return sb.ToString().TrimEnd();
        }

        public static string Exception(ExceptionInfo e)
        {
            if (e == null) return "(none)";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(e.Code)) parts.Add(e.Code);
            if (!string.IsNullOrEmpty(e.Name)) parts.Add(e.Name);
            if (!string.IsNullOrEmpty(e.Message)) parts.Add(e.Message);
            if (!string.IsNullOrEmpty(e.Address)) parts.Add("at " + e.Address);
            parts.Add(e.FirstChance ? "first-chance" : "unhandled");
            return string.Join("  ", parts);
        }

        public static string Frames(IReadOnlyList<Frame> frames, int currentIndex = -1)
        {
            if (frames == null || frames.Count == 0) return "  (no frames)";
            var sb = new StringBuilder();
            foreach (var f in frames)
            {
                sb.Append(f.Index == currentIndex ? "> " : "  ");
                sb.Append('#').Append(f.Index.ToString(CultureInfo.InvariantCulture).PadRight(3));
                sb.Append(' ').Append(f.Function ?? "(unknown)");
                if (!string.IsNullOrEmpty(f.File))
                    sb.Append("  ").Append(System.IO.Path.GetFileName(f.File)).Append(':').Append(f.Line);
                if (!string.IsNullOrEmpty(f.Module))
                    sb.Append("  [").Append(f.Module).Append(']');
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        public static string Stop(StopEvent e)
        {
            if (e == null) return "timeout: execution did not stop within the timeout. Still running.";

            var sb = new StringBuilder();
            sb.Append(e.InstanceId).Append("  stopped: ").Append(e.Reason);

            var process = Process(e.ProcessName, e.Pid);
            if (!string.IsNullOrEmpty(process)) sb.Append(" in ").Append(process);

            switch (e.Reason)
            {
                case StopReason.Breakpoint when e.BreakpointId.HasValue:
                    sb.Append(" #").Append(e.BreakpointId.Value);
                    break;
                case StopReason.Exception:
                    sb.Append("  ").Append(Exception(e.Exception));
                    break;
                case StopReason.Exited:
                    sb.Append("  exit code ").Append(e.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?");
                    break;
            }
            sb.AppendLine();

            if (e.Frame != null)
            {
                sb.Append("thread ").Append(e.ThreadId).Append("  ").Append(e.Frame.Function ?? "(unknown)");
                if (!string.IsNullOrEmpty(e.Frame.File))
                    sb.Append("  ").Append(e.Frame.File).Append(':').Append(e.Frame.Line);
                if (!string.IsNullOrEmpty(e.Frame.Module))
                    sb.Append("  [").Append(e.Frame.Module).Append(']');
                sb.AppendLine();
            }

            if (e.Watches != null && e.Watches.Count > 0)
            {
                foreach (var kv in e.Watches)
                    sb.Append("  ").Append(kv.Key).Append(" = ").AppendLine(kv.Value);
            }

            return sb.ToString().TrimEnd();
        }

        public static string ModuleLoad(ModuleLoadEvent e, string pattern)
        {
            if (e == null)
                return "timeout: no module matching \"" + pattern + "\" loaded within the timeout. Still running.";

            var sb = new StringBuilder();
            sb.Append(e.InstanceId).Append("  module loaded: ").Append(e.Name ?? "?");
            sb.Append(e.SymbolsLoaded ? "  symbols" : "  NO SYMBOLS");
            if (!e.SymbolsLoaded && !string.IsNullOrEmpty(e.SymbolStatus))
                sb.Append("  -- ").Append(e.SymbolStatus);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(e.Path)) sb.Append("  ").AppendLine(e.Path);
            sb.AppendLine("Breakpoints in it bind as it loads; bp_list says whether they did.");

            return sb.ToString().TrimEnd();
        }

        public static string Threads(IReadOnlyList<ThreadSummary> threads)
        {
            if (threads == null || threads.Count == 0) return "No threads. The debugger is not in break mode.";

            var sb = new StringBuilder();

            // Split by process first. A thread id is only actionable once you know which
            // process it belongs to, and a session often holds more than one.
            var processes = threads
                .GroupBy(t => Process(t.ProcessName, t.Pid) ?? "(unknown process)")
                .OrderByDescending(p => p.Any(t => t.IsCurrent))
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            sb.Append(threads.Count).Append(" threads");
            if (processes.Count > 1) sb.Append(" across ").Append(processes.Count).Append(" processes");
            sb.AppendLine();

            foreach (var process in processes)
            {
                // Named even when there is only one, because a reply that does not say
                // which process these threads belong to cannot be acted on with
                // confidence - least of all when a filter chose the process.
                sb.AppendLine();
                sb.Append(process.Key).Append("  ").Append(process.Count()).AppendLine(" threads");

                // Then by top frames: a deadlock shows up as one large group.
                var groups = process
                    .GroupBy(t => t.TopFrames == null ? "" : string.Join(" <- ", t.TopFrames.Select(f => f.Function)))
                    .OrderByDescending(g => g.Count());

                foreach (var group in groups)
                {
                    var ids = group.Select(t =>
                        (t.IsCurrent ? "*" : "") + t.Id + (t.IsFrozen ? "(frozen)" : "")).ToList();

                    sb.Append("  ").Append(group.Count()).Append(" x  ");
                    sb.AppendLine(string.Join(", ", ids.Take(12)) + (ids.Count > 12 ? ", ..." : ""));

                    var sample = group.First();
                    if (sample.TopFrames != null)
                    {
                        foreach (var f in sample.TopFrames)
                        {
                            sb.Append("      ").Append(f.Function ?? "(unknown)");
                            if (!string.IsNullOrEmpty(f.File))
                                sb.Append("  ").Append(System.IO.Path.GetFileName(f.File)).Append(':').Append(f.Line);
                            sb.AppendLine();
                        }
                    }
                }
            }

            return sb.ToString().TrimEnd();
        }

        public static string Breakpoints(IReadOnlyList<BreakpointInfo> bps)
        {
            if (bps == null || bps.Count == 0) return "No breakpoints.";

            var sb = new StringBuilder();
            foreach (var b in bps)
            {
                sb.Append('#').Append(b.Id.ToString(CultureInfo.InvariantCulture).PadRight(3));
                sb.Append(b.Enabled ? "on  " : "off ");
                sb.Append(b.Bound ? "bound   " : "UNBOUND ");
                sb.Append(Where(b));
                if (!string.IsNullOrEmpty(b.Condition)) sb.Append("  when ").Append(b.Condition);
                if (b.HitCount > 0) sb.Append("  hits ").Append(b.HitCount);
                if (!string.IsNullOrEmpty(b.LogMessage)) sb.Append(b.Collecting ? "  trace, collecting" : "  trace");
                if (!b.Bound && !string.IsNullOrEmpty(b.BindState)) sb.Append("  -- ").Append(b.BindState);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        public static string Breakpoint(BreakpointInfo b)
        {
            if (b == null) return "Breakpoint not set.";
            var sb = new StringBuilder();
            sb.Append('#').Append(b.Id).Append("  ").Append(Where(b));
            sb.Append(b.Bound ? "  bound" : "  UNBOUND");
            if (!b.Bound && !string.IsNullOrEmpty(b.BindState))
                sb.Append(" -- ").Append(b.BindState);
            sb.AppendLine();
            Tracepoint(sb, b);
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// What the tracepoint will log, and which of its expressions actually
        /// evaluated. An expression that will not is worth more here than in the
        /// thousand records that would otherwise carry the evaluator's complaint.
        /// </summary>
        static void Tracepoint(StringBuilder sb, BreakpointInfo b)
        {
            if (string.IsNullOrEmpty(b.LogMessage)) return;

            sb.Append("logs: ").Append(b.LogMessage);
            if (b.Collecting) sb.Append("   [collecting; read it with trace_read]");
            sb.AppendLine();

            if (b.LogExpressions != null)
            {
                foreach (var e in b.LogExpressions)
                {
                    sb.Append("  {").Append(e.Expression).Append('}');
                    if (!string.IsNullOrEmpty(e.Error)) sb.Append("  -- ").Append(e.Error);
                    else if (e.Value != null) sb.Append(" = ").Append(e.Value);
                    sb.AppendLine();
                }
            }

            if (!string.IsNullOrEmpty(b.LogCheckDeferred))
                sb.Append("not checked: ").AppendLine(b.LogCheckDeferred);
        }

        public static string Trace(TraceResult t)
        {
            if (t == null) return "No records.";
            if (t.Records == null || t.Records.Count == 0)
                return t.Message ?? "Tracepoint #" + t.BreakpointId + " has collected nothing.";

            var sb = new StringBuilder();
            sb.Append('#').Append(t.BreakpointId).Append("  ").Append(t.Records.Count);
            sb.Append(" of ").Append(t.Collected).Append(" records");
            if (t.Dropped > 0) sb.Append(", ").Append(t.Dropped).Append(" dropped by the per-second cap");

            // Timed records give a rate across the ones in hand. Untimed ones still give
            // one, from everything collected since the tracepoint was set, which is the
            // question a rate was wanted for.
            var span = t.Timed
                ? (t.Records[t.Records.Count - 1].Time - t.Records[0].Time).TotalSeconds
                : (DateTime.UtcNow - t.StartedUtc).TotalSeconds;
            var over = t.Timed ? t.Records.Count - 1 : t.Collected;

            if (over > 0 && span > 0)
            {
                sb.Append("  ").Append((over / span).ToString("0.0", CultureInfo.InvariantCulture));
                sb.Append("/s over ").Append(span.ToString("0.###", CultureInfo.InvariantCulture)).Append('s');
            }
            sb.AppendLine();

            foreach (var r in t.Records)
            {
                sb.Append("  ");
                if (t.Timed)
                    sb.Append(r.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("  ");
                sb.Append('#').Append(r.Hit.ToString(CultureInfo.InvariantCulture).PadRight(7));
                sb.AppendLine(r.Text);
            }

            if (!t.Timed)
            {
                sb.AppendLine("These records were read back out of the Debug pane, which keeps their order " +
                              "and not their times, so the rate above is over the whole collection rather " +
                              "than across the records shown.");
            }

            if (!string.IsNullOrEmpty(t.Message)) sb.AppendLine(t.Message);
            return sb.ToString().TrimEnd();
        }

        static string Where(BreakpointInfo b)
        {
            switch (b.Kind)
            {
                case BreakpointKind.Function:
                    return (string.IsNullOrEmpty(b.Module) ? "" : b.Module + "!") + b.Function;
                case BreakpointKind.Data:
                    return "data " + b.Expression + " (" + b.Size + " bytes)";
                default:
                    return (b.File ?? "?") + ":" + b.Line;
            }
        }

        public static string Modules(ModulesResult result)
        {
            var loaded = result?.LoadedCount ?? 0;
            if (loaded == 0) return "No modules loaded.";

            // A filtered list reads as the whole truth, and while a process is still
            // loading its plugins it is not. Say what it was picked from either way.
            var modules = result.Modules;
            if (modules == null || modules.Count == 0)
            {
                return "No module matches '" + result.Filter + "'. " + loaded +
                       " modules are loaded and more can load while the program runs.";
            }

            var sb = new StringBuilder();
            if (string.IsNullOrEmpty(result.Filter))
            {
                sb.Append(modules.Count).Append(" modules, ")
                  .Append(modules.Count(m => !m.SymbolsLoaded)).AppendLine(" without symbols");
            }
            else
            {
                sb.Append(modules.Count).Append(" of ").Append(loaded).Append(" loaded modules match '")
                  .Append(result.Filter).AppendLine("'; more can load while the program runs");
            }

            foreach (var m in modules)
            {
                sb.Append("  ").Append((m.Name ?? "?").PadRight(34));
                sb.Append((m.SymbolsLoaded ? "symbols" : "NO SYMBOLS").PadRight(11));
                if (!string.IsNullOrEmpty(m.Built)) sb.Append("built ").Append(m.Built);
                if (!m.SymbolsLoaded && !string.IsNullOrEmpty(m.SymbolStatus))
                    sb.Append("  -- ").Append(m.SymbolStatus);
                if (!string.IsNullOrEmpty(m.NewerSource))
                    sb.Append("  -- ").Append(m.NewerSource).Append(" was edited after this binary was built");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        public static string Vars(IReadOnlyList<VarNode> nodes, int indent = 0)
        {
            if (nodes == null || nodes.Count == 0) return "  (nothing in scope)";
            var sb = new StringBuilder();
            Walk(sb, nodes, indent);

            if (nodes.Any(n => n.SameAddressAs != null && n.SameAddressAs.Count > 0))
                sb.AppendLine().Append("Two names on one address usually mean the optimizer reused a slot, not two live variables.");

            return sb.ToString().TrimEnd();
        }

        static void Walk(StringBuilder sb, IReadOnlyList<VarNode> nodes, int indent, List<string> noted = null)
        {
            foreach (var n in nodes)
            {
                sb.Append(new string(' ', 2 + indent * 2));
                sb.Append(n.Name).Append(" = ").Append(n.Value);
                if (!string.IsNullOrEmpty(n.Type)) sb.Append("  (").Append(n.Type).Append(')');
                if (!n.Readable) sb.Append("  -- not readable here");
                if (n.SameAddressAs != null && n.SameAddressAs.Count > 0)
                    sb.Append("  -- same address as ").Append(string.Join(", ", n.SameAddressAs));
                if (n.HasChildren && (n.Children == null || n.Children.Count == 0))
                    sb.Append("  ... expand ").Append(n.Ref);

                // A fill the line above already reported is not reported again on the way
                // down, because a parent's value holds the very pointers its children are.
                var fills = FillPatterns.Notes(n.Value);
                if (noted != null) fills.RemoveAll(noted.Contains);
                if (fills.Count > 0)
                {
                    sb.Append("  -- ").Append(string.Join("; ", fills));
                    if (noted != null) fills.AddRange(noted);
                }
                sb.AppendLine();

                if (n.Children != null && n.Children.Count > 0)
                    Walk(sb, n.Children, indent + 1, fills.Count > 0 ? fills : noted);
            }
        }

        public static string Evals(IReadOnlyList<EvalResult> results)
        {
            if (results == null || results.Count == 0) return "No result.";

            if (results.Count == 1)
            {
                var r = results[0];
                if (!r.IsValid) return r.Expression + " -- " + (r.Error ?? "could not be evaluated");
                var text = r.Expression + " = " + r.Value;
                if (!string.IsNullOrEmpty(r.Type)) text += "  (" + r.Type + ")";
                if (r.HasChildren) text += "  ... expand " + r.Ref;
                var fills = FillPatterns.Notes(r.Value);
                if (fills.Count > 0) text += "  -- " + string.Join("; ", fills);
                return text;
            }

            var sb = new StringBuilder();
            foreach (var group in results.GroupBy(r => r.IsValid ? r.Value : "!" + r.Error).OrderByDescending(g => g.Count()))
            {
                var ids = group.Select(r => r.ThreadId?.ToString(CultureInfo.InvariantCulture) ?? "?").ToList();
                sb.Append("  ").Append(group.Key ?? "(null)");
                var fills = group.First().IsValid ? FillPatterns.Notes(group.Key) : new List<string>();
                if (fills.Count > 0) sb.Append("  -- ").Append(string.Join("; ", fills));
                sb.Append("   threads: ");
                sb.AppendLine(string.Join(", ", ids.Take(16)) + (ids.Count > 16 ? ", ..." : ""));
            }
            return sb.ToString().TrimEnd();
        }

        public static string Build(BuildResult b)
        {
            if (b == null) return "No build result.";
            if (b.Cancelled) return "Build cancelled after " + b.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s.";

            var sb = new StringBuilder();
            sb.Append(b.Succeeded ? "Build succeeded" : "Build FAILED");
            sb.Append(" in ").Append(b.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append('s');
            sb.Append("  ").Append(b.TotalErrors).Append(" errors, ").Append(b.TotalWarnings).AppendLine(" warnings");

            if (b.Diagnostics != null && b.Diagnostics.Count > 0)
            {
                foreach (var d in b.Diagnostics)
                {
                    sb.Append("  ").Append(d.Severity == "error" ? "E" : "W").Append(' ');
                    if (!string.IsNullOrEmpty(d.File))
                        sb.Append(d.File).Append('(').Append(d.Line).Append(") ");
                    if (!string.IsNullOrEmpty(d.Code)) sb.Append(d.Code).Append(": ");
                    sb.AppendLine(d.Text);
                }

                var shown = b.Diagnostics.Count;
                var total = b.TotalErrors + b.TotalWarnings;
                if (total > shown) sb.Append("  ... and ").Append(total - shown).AppendLine(" more");
            }

            if (!string.IsNullOrEmpty(b.Message)) sb.AppendLine(b.Message);
            return sb.ToString().TrimEnd();
        }

        public static string Memory(MemoryResult m)
        {
            if (m == null) return "No memory read.";
            if (!string.IsNullOrEmpty(m.Error)) return "Could not read memory: " + m.Error;

            var sb = new StringBuilder();
            sb.Append(m.Address).Append("  ").Append(m.Length).AppendLine(" bytes");
            sb.AppendLine(m.Hex);
            if (!string.IsNullOrEmpty(m.Ascii)) sb.AppendLine(m.Ascii);
            foreach (var run in FillPatterns.Runs(m.Hex)) sb.AppendLine(run);
            return sb.ToString().TrimEnd();
        }

        public static string Op(OpResult r, string success)
        {
            if (r == null) return "No result.";
            if (r.Ok) return string.IsNullOrEmpty(r.Message) ? success : r.Message;
            return "Failed: " + (r.Message ?? "no reason given");
        }
    }
}
