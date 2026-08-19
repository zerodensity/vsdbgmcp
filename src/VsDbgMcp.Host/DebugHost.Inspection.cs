using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using VsDbgMcp.Contracts;
using Task = System.Threading.Tasks.Task;

namespace VsDbgMcp.Host
{
    partial class DebugHost
    {
        // ---------------------------------------------------------------- breakpoints

        public Task<BreakpointInfo> BreakpointSetAsync(BreakpointRequest request, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                Breakpoints added;
                switch (request.Kind)
                {
                    case BreakpointKind.Function:
                        added = _dte.Debugger.Breakpoints.Add(
                            Function: QualifiedFunction(request),
                            Condition: request.Condition ?? "",
                            ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                            HitCount: request.HitCountTarget,
                            HitCountType: request.HitCountTarget > 0
                                ? dbgHitCountType.dbgHitCountTypeEqual
                                : dbgHitCountType.dbgHitCountTypeNone);
                        break;

                    case BreakpointKind.Data:
                        added = _dte.Debugger.Breakpoints.Add(
                            Data: request.Expression,
                            DataCount: Math.Max(1, request.Size),
                            Condition: request.Condition ?? "",
                            ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);
                        break;

                    default:
                        added = _dte.Debugger.Breakpoints.Add(
                            File: request.File,
                            Line: request.Line,
                            Condition: request.Condition ?? "",
                            ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue,
                            HitCount: request.HitCountTarget,
                            HitCountType: request.HitCountTarget > 0
                                ? dbgHitCountType.dbgHitCountTypeEqual
                                : dbgHitCountType.dbgHitCountTypeNone);
                        break;
                }

                var created = added != null && added.Count > 0 ? added.Item(1) : NewestMatching(request);
                if (created == null) return Failed(request, "Visual Studio did not create the breakpoint.");

                if (!string.IsNullOrEmpty(request.LogMessage))
                {
                    var tracepoint = created as EnvDTE80.Breakpoint2;
                    if (tracepoint == null) return Failed(request, "This breakpoint cannot be made a tracepoint.");
                    tracepoint.Message = request.LogMessage;
                    tracepoint.BreakWhenHit = false;
                }

                var info = Describe(created);
                info.Kind = request.Kind;
                if (request.Kind == BreakpointKind.Data)
                {
                    info.Expression = request.Expression;
                    info.Size = request.Size;
                }
                if (request.Kind == BreakpointKind.Function && string.IsNullOrEmpty(info.Function))
                {
                    info.Function = request.Function;
                    info.Module = request.Module;
                }

                // Binding is not settled the instant a breakpoint is created, so do not
                // claim it failed to bind when it simply has not bound yet.
                if (!info.Bound && CurrentMode != DebugModes.Design)
                    info.BindState = "just created; bp_list confirms whether it bound";

                return info;
            }
            catch (Exception ex)
            {
                return Failed(request, ex.Message);
            }
        });

        /// <summary>
        /// Breakpoints.Add sometimes returns an empty collection having created the
        /// breakpoint anyway, so find it rather than reporting a failure that did not
        /// happen. Data breakpoints carry no location to match on, so the most recently
        /// added one is the one just asked for.
        /// </summary>
        Breakpoint NewestMatching(BreakpointRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Breakpoint last = null;
            foreach (Breakpoint candidate in _dte.Debugger.Breakpoints)
            {
                last = candidate;

                if (request.Kind == BreakpointKind.Location &&
                    candidate.FileLine == request.Line &&
                    PathUtil.SamePath(candidate.File, request.File))
                {
                    return candidate;
                }

                if (request.Kind == BreakpointKind.Function &&
                    !string.IsNullOrEmpty(candidate.FunctionName) &&
                    candidate.FunctionName.IndexOf(request.Function ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }

            return request.Kind == BreakpointKind.Data ? last : null;
        }

        static string QualifiedFunction(BreakpointRequest request) =>
            string.IsNullOrEmpty(request.Module) ? request.Function : request.Module + "!" + request.Function;

        static BreakpointInfo Failed(BreakpointRequest request, string reason) => new BreakpointInfo
        {
            Kind = request.Kind,
            File = request.File,
            Line = request.Line,
            Function = request.Function,
            Module = request.Module,
            Expression = request.Expression,
            Bound = false,
            BindState = reason
        };

        BreakpointInfo Describe(Breakpoint breakpoint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var info = new BreakpointInfo
            {
                Id = _breakpoints.IdFor(breakpoint),
                File = breakpoint.File,
                Line = breakpoint.FileLine,
                Function = breakpoint.FunctionName,
                Condition = breakpoint.Condition,
                HitCount = breakpoint.CurrentHits,
                HitCountTarget = breakpoint.HitCountTarget,
                Enabled = breakpoint.Enabled
            };

            // The automation model does not report which kind a breakpoint is, so it is
            // inferred from what it does carry. A breakpoint with neither a location nor
            // a function is a data breakpoint; the address it watches is not readable
            // back, so listing one shows less than setting one did.
            info.Kind = !string.IsNullOrEmpty(info.Function) && info.Line == 0 ? BreakpointKind.Function
                : string.IsNullOrEmpty(info.File) && info.Line == 0 ? BreakpointKind.Data
                : BreakpointKind.Location;

            ReadBindState(breakpoint, info);
            return info;
        }

        /// <summary>
        /// Whether the breakpoint will actually be hit, and if not, why.
        ///
        /// A breakpoint that reports success but never binds is the most expensive
        /// failure in native debugging, because everything downstream looks like the
        /// code was not reached.
        /// </summary>
        void ReadBindState(Breakpoint breakpoint, BreakpointInfo info)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (CurrentMode == DebugModes.Design)
            {
                info.Bound = false;
                info.BindState = "pending: not debugging yet, binding happens at launch";
                return;
            }

            // A pending breakpoint's bound instances appear as its children.
            var children = Read(() => breakpoint.Children, null, "the bound breakpoints");
            if (children != null && children.Count > 0)
            {
                info.Bound = true;
                return;
            }

            info.Bound = false;
            info.BindState =
                "no code loaded at this location. Check 'modules' for the owning module and " +
                "whether its symbols loaded, and the Debug pane via 'output' for PDB messages";
        }

        public Task<List<BreakpointInfo>> BreakpointListAsync(CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var list = new List<BreakpointInfo>();
            foreach (Breakpoint breakpoint in _dte.Debugger.Breakpoints) list.Add(Describe(breakpoint));
            return list;
        });

        public Task<OpResult> BreakpointRemoveAsync(int id, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var breakpoint = _breakpoints.Find(id, _dte.Debugger.Breakpoints);
            if (breakpoint == null) return OpResult.Bad("No breakpoint #" + id + ".");
            return Try(() => breakpoint.Delete(), "Removed #" + id + ".");
        });

        public Task<OpResult> BreakpointEnableAsync(int id, bool enabled, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var breakpoint = _breakpoints.Find(id, _dte.Debugger.Breakpoints);
            if (breakpoint == null) return OpResult.Bad("No breakpoint #" + id + ".");
            return Try(() => breakpoint.Enabled = enabled, null);
        });

        // The exception-settings objects are reached through late binding. Their
        // interop types are not in the reference assemblies the SDK package ships, and
        // this corner is small enough that binding by name costs nothing.

        public Task<OpResult> ExceptionSetAsync(ExceptionSetting setting, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (setting == null || string.IsNullOrEmpty(setting.Code))
                return OpResult.Bad("Give an exception code or name.");

            dynamic group = FindExceptionGroup(setting.Category);
            if (group == null)
            {
                var available = ExceptionGroupNames();
                return OpResult.Bad("No exception category '" + setting.Category + "'." +
                                    (available.Count == 0
                                        ? " This instance exposes no exception categories."
                                        : " Available: " + string.Join(", ", available)));
            }

            var never = string.Equals(setting.BreakOn, "never", StringComparison.OrdinalIgnoreCase);
            var thrown = string.Equals(setting.BreakOn, "thrown", StringComparison.OrdinalIgnoreCase);

            return Try(() =>
            {
                dynamic entry = group.Item(setting.Code);
                group.SetBreakWhenThrown(thrown && !never, entry);
            }, "Break on " + setting.Code + " when " + setting.BreakOn + ".");
        });

        /// <summary>
        /// The categories this instance actually offers. Naming them beats guessing at
        /// them, which is what an error that repeats the caller's own word amounts to.
        /// </summary>
        List<string> ExceptionGroupNames()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Read(() =>
            {
                var names = new List<string>();
                dynamic debugger = _dte.Debugger;
                foreach (dynamic group in debugger.ExceptionGroups)
                {
                    var name = Convert.ToString(group.Name);
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
                return names;
            }, new List<string>(), "the exception categories");
        }

        object FindExceptionGroup(string category)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Late bound, so a missing member surfaces here rather than at compile time.
            return Read<object>(() =>
            {
                dynamic debugger = _dte.Debugger;
                dynamic groups = debugger.ExceptionGroups;
                if (string.IsNullOrEmpty(category)) return groups.Item(1);

                foreach (dynamic group in groups)
                {
                    string name = Convert.ToString(group.Name);
                    if (string.Equals(name, category, StringComparison.OrdinalIgnoreCase)) return group;
                }
                return groups.Item(category);
            }, null, "the exception groups");
        }

        public Task<List<ExceptionSetting>> ExceptionListAsync(CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Read(() =>
            {
                var list = new List<ExceptionSetting>();
                dynamic debugger = _dte.Debugger;

                foreach (dynamic group in debugger.ExceptionGroups)
                {
                    string category = Convert.ToString(group.Parent);
                    foreach (dynamic setting in group)
                    {
                        if (!(bool)setting.BreakWhenThrown) continue;
                        list.Add(new ExceptionSetting
                        {
                            Category = category,
                            Code = Convert.ToString(setting.Name),
                            BreakOn = "thrown"
                        });
                    }
                }
                return list;
            }, new List<ExceptionSetting>(), "the exception settings");
        });

        // ---------------------------------------------------------------- threads

        public Task<List<ThreadSummary>> ThreadsAsync(int frameDepth, string process, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var summaries = new List<ThreadSummary>();
            var currentId = ThreadIdOf(CurrentThreadObject());

            foreach (var thread in AllThreads())
            {
                var identity = ProcessIdentity.Of(thread);
                if (!string.IsNullOrWhiteSpace(process) && !identity.Matches(process)) continue;

                var id = ThreadIdOf(thread);
                thread.GetName(out var name);

                summaries.Add(new ThreadSummary
                {
                    Id = id,
                    Name = name,
                    ProcessName = identity.Name,
                    Pid = identity.Pid,
                    IsCurrent = id == currentId,
                    IsFrozen = _suspended.TryGetValue(id, out var count) && count > 0,
                    SuspendCount = _suspended.TryGetValue(id, out var c) ? (int)c : 0,
                    TopFrames = FrameReader.Frames(thread, frameDepth)
                });
            }

            return summaries;
        });

        public Task<List<Frame>> StackAsync(int? threadId, int count, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var thread = threadId.HasValue
                ? AllThreads().FirstOrDefault(t => ThreadIdOf(t) == threadId.Value)
                : CurrentThreadObject();

            if (thread == null && threadId.HasValue) throw new InvalidOperationException(
                "No thread " + threadId.Value + " in this session.\nThreads in this session:\n" + KnownThreads());

            return FrameReader.Frames(thread, count);
        });

        public Task<OpResult> SelectAsync(int? threadId, int? frameIndex, string process, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Naming a process without a thread means "whichever of its threads is
            // current", which is what a caller who has only seen a process list can ask.
            if (!string.IsNullOrWhiteSpace(process) && !threadId.HasValue)
            {
                var match = AllThreads().FirstOrDefault(t => ProcessIdentity.Of(t).Matches(process));
                if (match == null)
                {
                    return OpResult.Bad("No process matching '" + process + "' is being debugged.\n" +
                                        "Threads in this session:\n" + KnownThreads());
                }
                threadId = ThreadIdOf(match);
            }

            if (threadId.HasValue)
            {
                if (AllThreads().All(t => ThreadIdOf(t) != threadId.Value))
                {
                    return OpResult.Bad("No thread " + threadId.Value + " in this session.\n" +
                                        "Threads in this session:\n" + KnownThreads());
                }

                _selectedThreadId = threadId.Value;

                // Keep the Visual Studio UI on the same thread and process, so a person
                // looking at the window sees what the agent is looking at. Cosmetic, so a
                // refusal here must not fail the call.
                Read<object>(() =>
                {
                    foreach (EnvDTE.Process p in _dte.Debugger.DebuggedProcesses)
                    {
                        foreach (EnvDTE.Program program in p.Programs)
                        {
                            foreach (EnvDTE.Thread t in program.Threads)
                            {
                                if (t.ID != threadId.Value) continue;
                                _dte.Debugger.CurrentProcess = p;
                                _dte.Debugger.CurrentProgram = program;
                                _dte.Debugger.CurrentThread = t;
                                return null;
                            }
                        }
                    }
                    return null;
                }, null, "the debugged processes");
            }

            if (frameIndex.HasValue) _selectedFrame = Math.Max(0, frameIndex.Value);

            var selected = AllThreads().FirstOrDefault(t => ThreadIdOf(t) == _selectedThreadId);
            var where = selected == null ? "(current)" : ProcessIdentity.Of(selected).Describe();

            return OpResult.Good("thread " + (_selectedThreadId == 0 ? "(current)" : _selectedThreadId.ToString()) +
                                 " in " + where + ", frame " + _selectedFrame);
        });

        public Task<OpResult> FreezeAsync(int threadId, bool frozen, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var thread = AllThreads().FirstOrDefault(t => ThreadIdOf(t) == threadId);
            if (thread == null)
            {
                return OpResult.Bad("No thread " + threadId + " in this session.\n" +
                                    "Threads in this session:\n" + KnownThreads());
            }

            try
            {
                uint count;
                if (frozen) thread.Suspend(out count);
                else thread.Resume(out count);

                _suspended[threadId] = count;
                return OpResult.Good((frozen ? "Froze" : "Thawed") + " thread " + threadId + " (suspend count " + count + ").");
            }
            catch (Exception ex)
            {
                return OpResult.Bad(ex.Message);
            }
        });

        // ---------------------------------------------------------------- evaluation

        public Task<List<EvalResult>> EvalAsync(EvalOptions options, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var results = new List<EvalResult>();

            if (CurrentMode != DebugModes.Break)
            {
                results.Add(new EvalResult
                {
                    Expression = options.Expression,
                    Error = "the debugger is not stopped; expressions can only be evaluated in break mode"
                });
                return results;
            }

            if (options.AllThreads)
            {
                foreach (var thread in AllThreads())
                {
                    var frame = FrameReader.FrameAt(thread, 0);
                    var result = ExpressionEval.Evaluate(frame, options);
                    result.ThreadId = ThreadIdOf(thread);
                    results.Add(result);
                }
                return results;
            }

            results.Add(ExpressionEval.Evaluate(CurrentFrame(options.FrameIndex), options));
            return results;
        });

        public Task<List<VarNode>> VarsAsync(string scope, int depth, string filter, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ExpressionEval.Scope(CurrentFrame(0), scope, depth, filter);
        });

        public Task<List<VarNode>> ExpandAsync(string reference, int depth, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ExpressionEval.Expand(CurrentFrame(0), reference, depth);
        });

        // ---------------------------------------------------------------- native

        public Task<MemoryResult> MemoryAsync(string addressOrExpression, int size, string format, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return NativeReader.ReadMemory(CurrentFrame(0), addressOrExpression, size);
        });

        public Task<List<RegisterInfo>> RegistersAsync(string group, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return NativeReader.ReadRegisters(CurrentFrame(0), group);
        });

        public Task<List<DisasmLine>> DisasmAsync(string address, int count, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return NativeReader.Disassemble(_sink.CurrentProgram, CurrentFrame(0), count);
        });

        public Task<List<ModuleInfo>> ModulesAsync(string filter, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return NativeReader.ReadModules(_sink.CurrentProgram, filter);
        });

        // ---------------------------------------------------------------- triage

        public Task<string> TriageAsync(CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (CurrentMode != DebugModes.Break)
                return "Nothing to triage: the debugger is not stopped. Current mode: " + CurrentMode + ".";

            var sb = new StringBuilder();
            var thread = CurrentThreadObject();
            var frame = CurrentFrame(0);

            sb.AppendLine("== stop ==");
            var exception = _sink.LastException;
            sb.AppendLine(exception != null
                ? Describe(exception)
                : "no exception recorded; stopped for another reason");

            sb.AppendLine();
            sb.AppendLine("== faulting thread " + ThreadIdOf(thread) + " ==");
            foreach (var f in FrameReader.Frames(thread, 20))
            {
                sb.Append("  #").Append(f.Index).Append(' ').Append(f.Function ?? "(unknown)");
                if (!string.IsNullOrEmpty(f.File)) sb.Append("  ").Append(f.File).Append(':').Append(f.Line);
                if (!string.IsNullOrEmpty(f.Module)) sb.Append("  [").Append(f.Module).Append(']');
                sb.AppendLine();
            }

            var registers = NativeReader.ReadRegisters(frame, null);
            if (registers.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("== registers ==");
                var interesting = registers.Where(r => IsInteresting(r.Name)).ToList();
                foreach (var r in (interesting.Count > 0 ? interesting : registers.Take(12)))
                    sb.Append("  ").Append(r.Name.PadRight(8)).AppendLine(r.Value);
            }

            if (exception != null && !string.IsNullOrEmpty(exception.Address))
            {
                var memory = NativeReader.ReadMemory(frame, exception.Address, 64);
                if (string.IsNullOrEmpty(memory.Error))
                {
                    sb.AppendLine();
                    sb.AppendLine("== memory at the fault address ==");
                    sb.AppendLine("  " + memory.Hex);
                }
            }

            var modules = NativeReader.ReadModules(_sink.CurrentProgram, null);
            var stripped = modules.Where(m => !m.SymbolsLoaded).ToList();
            sb.AppendLine();
            sb.AppendLine("== symbols ==");
            sb.AppendLine("  " + modules.Count + " modules, " + stripped.Count + " without symbols");
            foreach (var m in stripped.Take(10)) sb.AppendLine("  no symbols: " + m.Name);

            return sb.ToString().TrimEnd();
        });

        static string Describe(ExceptionInfo e)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(e.Code)) parts.Add(e.Code);
            if (!string.IsNullOrEmpty(e.Name)) parts.Add(e.Name);
            if (!string.IsNullOrEmpty(e.Message)) parts.Add(e.Message);
            if (!string.IsNullOrEmpty(e.Address)) parts.Add("at " + e.Address);
            parts.Add(e.FirstChance ? "first-chance" : "unhandled");
            return string.Join("  ", parts);
        }

        static bool IsInteresting(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            switch (name.ToLowerInvariant())
            {
                case "rip": case "rsp": case "rbp":
                case "rax": case "rcx": case "rdx":
                case "eip": case "esp": case "ebp": case "eax":
                    return true;
                default:
                    return false;
            }
        }
    }
}
