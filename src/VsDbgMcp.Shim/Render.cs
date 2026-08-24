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

                var remote = Remote(s.Processes);
                if (remote != null) sb.AppendLine(remote);
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
                if (!string.IsNullOrEmpty(s.FrameNote)) sb.AppendLine(s.FrameNote);
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

        /// <summary>
        /// One line saying the debuggee is on another machine, or null when nothing
        /// established that it is.
        ///
        /// Without it a remote session reads as a dead one: the pid is in no local
        /// process list and the paths in every other reply do not exist on this disk.
        /// Both are correct, and both are what remote debugging looks like.
        /// </summary>
        static string Remote(IReadOnlyList<ProcessInfo> processes)
        {
            var remote = processes.Where(p => p.IsRemote).ToList();
            if (remote.Count == 0) return null;

            var machines = remote.Select(p => p.Machine)
                .Where(m => !string.IsNullOrEmpty(m))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var transport = remote.Select(p => p.Transport).FirstOrDefault(t => !string.IsNullOrEmpty(t));

            var sb = new StringBuilder("remote: ");
            sb.Append(string.Join(", ", remote.Select(p => p.Name + " (" + p.Pid + ")")));
            sb.Append(machines.Count > 0 ? " runs on " + string.Join(", ", machines) : " runs on another machine");
            if (transport != null) sb.Append(" over ").Append(transport);
            sb.AppendLine(".");
            sb.Append("Every path in this session - module paths, source files - is on that machine, ");
            sb.Append("and the pid is not one this machine's process list will show.");
            return sb.ToString();
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

                // Zero is an answer - it was never reached - and leaving it out instead
                // reads as nothing having been counted.
                if (b.Bound) sb.Append("  hits ").Append(b.HitCount);
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
            var detailed = modules.Count <= DetailedModules;

            if (string.IsNullOrEmpty(result.Filter))
            {
                sb.Append(modules.Count).Append(" modules, ")
                  .Append(modules.Count(m => !m.SymbolsLoaded)).Append(" without symbols");
            }
            else
            {
                sb.Append(modules.Count).Append(" of ").Append(loaded).Append(" loaded modules match '")
                  .Append(result.Filter).Append("'; more can load while the program runs");
            }

            if (!detailed) sb.Append(". Filter to see each one's path, size and load address");
            sb.AppendLine();

            foreach (var m in modules)
            {
                Module(sb, m, detailed);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// How many modules still get their full identity printed. Past this the list is
        /// something being scanned for a name rather than read, and a process with
        /// hundreds of modules loaded would otherwise answer in pages.
        /// </summary>
        const int DetailedModules = 40;

        /// <summary>
        /// One module, as a line naming it and an indented line saying which binary that
        /// actually is.
        ///
        /// The two times are deliberately labelled apart. 'image' is stamped into the
        /// binary the debuggee loaded and travels with it; the file time belongs to
        /// whatever sits at that path on this machine, which for a module deployed
        /// somewhere else is a different copy or nothing at all.
        /// </summary>
        static void Module(StringBuilder sb, ModuleInfo m, bool detailed)
        {
            sb.Append("  ").Append((m.Name ?? "?").PadRight(34));
            sb.Append((m.SymbolsLoaded ? "symbols" : "NO SYMBOLS").PadRight(12));

            if (!string.IsNullOrEmpty(m.ImageBuilt)) sb.Append("image ").Append(m.ImageBuilt);
            else if (!string.IsNullOrEmpty(m.Built)) sb.Append("file ").Append(m.Built);

            if (m.IsUserCode) sb.Append("  user code");
            if (!m.SymbolsLoaded && !string.IsNullOrEmpty(m.SymbolStatus))
                sb.Append("  -- ").Append(m.SymbolStatus);
            if (!string.IsNullOrEmpty(m.NewerSource))
                sb.Append("  -- ").Append(m.NewerSource).Append(" was edited after this binary was built");
            sb.AppendLine();

            if (!detailed) return;

            if (!string.IsNullOrEmpty(m.Path))
            {
                sb.Append("    ").Append(m.Path);
                if (!string.IsNullOrEmpty(m.Size)) sb.Append("  ").Append(m.Size);
                if (!string.IsNullOrEmpty(m.Address)) sb.Append(" at ").Append(m.Address);

                // Only worth saying once. Where there is no image time the line above
                // already showed this one, and repeating it reads as two facts.
                if (!string.IsNullOrEmpty(m.Built) && !string.IsNullOrEmpty(m.ImageBuilt))
                    sb.Append("  -- the file at that path on this machine was written ").Append(m.Built);

                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(m.SymbolPath))
                sb.Append("    symbols ").AppendLine(m.SymbolPath);
        }

        /// <summary>
        /// What state one module's symbols are in, and where the engine looked when they
        /// are missing. The search text is the only place a PDB that is present and does
        /// not match the binary ever says so.
        /// </summary>
        public static string Symbols(SymbolResult r)
        {
            if (r == null) return "No answer.";

            var sb = new StringBuilder();

            if (r.Module == null)
            {
                sb.Append(r.Message ?? "Nothing to report.");
                if (r.Candidates != null && r.Candidates.Count > 0)
                {
                    sb.AppendLine();
                    foreach (var name in r.Candidates) sb.Append("  ").AppendLine(name);
                }
                return sb.ToString().TrimEnd();
            }

            Module(sb, r.Module, true);

            if (!string.IsNullOrEmpty(r.Message)) sb.AppendLine(r.Message);

            if (r.LoadTried)
            {
                if (r.Module.SymbolsLoaded)
                {
                    sb.AppendLine("Load Symbols worked. It does not survive the module being unloaded and " +
                                  "loaded again: the Include/Exclude list under Tools > Options > Debugging > " +
                                  "Symbols is applied afresh on every load, so a plugin that reloads comes back " +
                                  "without symbols unless that list is changed.");
                }
                else if (r.LoadRefused)
                {
                    sb.AppendLine("The engine turned the load down rather than searching. The module still has " +
                                  "no symbols.");
                }
                else
                {
                    sb.AppendLine("Load Symbols ran and the module still has no symbols.");
                }
            }

            if (!string.IsNullOrEmpty(r.SearchInfo))
            {
                sb.AppendLine("Where it looked:");
                foreach (var line in r.SearchInfo.Split('\n'))
                    sb.Append("  ").AppendLine(line.TrimEnd());
            }
            else if (!r.Module.SymbolsLoaded)
            {
                sb.AppendLine("The engine did not say where it looked.");
            }

            // The whole point of the tool is that the next move is available from here
            // rather than from a dialog someone else has to open.
            if (!r.Module.SymbolsLoaded && !r.LoadTried && string.IsNullOrEmpty(r.Message))
                sb.AppendLine("Call this again with load to make the engine search now.");

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// What vars and expand answer with. The frame and the reference are printed only
        /// when the host filled them in, which it does when there is something to say -
        /// a frame that was moved past, or an element reference the caller would otherwise
        /// have to copy out of this reply by hand.
        /// </summary>
        public static string Vars(VarsResult result)
        {
            if (result == null) return "No result.";

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(result.FrameNote)) sb.AppendLine(result.FrameNote);
            if (result.Frame != null) sb.AppendLine(Frames(new[] { result.Frame }, result.Frame.Index));
            if (!string.IsNullOrEmpty(result.Ref)) sb.Append("ref: ").AppendLine(result.Ref);

            var nodes = result.Nodes;
            if (nodes != null && nodes.Count > 0) sb.AppendLine(Vars(nodes));
            else if (string.IsNullOrEmpty(result.Message)) sb.AppendLine("  (nothing in scope)");

            if (!string.IsNullOrEmpty(result.Message)) sb.AppendLine(result.Message);
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

                var head = new StringBuilder();
                if (!string.IsNullOrEmpty(r.FrameNote)) head.AppendLine(r.FrameNote);
                if (r.Frame != null) head.AppendLine(Frames(new[] { r.Frame }, r.Frame.Index));

                if (!r.IsValid)
                    return head + r.Expression + " -- " + (r.Error ?? "could not be evaluated");

                var text = r.Expression + " = " + r.Value;
                if (!string.IsNullOrEmpty(r.Type)) text += "  (" + r.Type + ")";
                if (r.HasChildren) text += "  ... expand " + r.Ref;
                var fills = FillPatterns.Notes(r.Value);
                if (fills.Count > 0) text += "  -- " + string.Join("; ", fills);
                return head + text;
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
