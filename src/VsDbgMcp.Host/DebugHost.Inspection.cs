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
                            HitCount: HitCount(request),
                            HitCountType: HitCountType(request));
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
                            HitCount: HitCount(request),
                            HitCountType: HitCountType(request));
                        break;
                }

                var created = added != null && added.Count > 0 ? added.Item(1) : NewestMatching(request);
                if (created == null) return Failed(request, "Visual Studio did not create the breakpoint.");

                if (!string.IsNullOrEmpty(request.LogMessage))
                {
                    var tracepoint = created as EnvDTE80.Breakpoint2;
                    if (tracepoint == null) return Failed(request, "This breakpoint cannot be made a tracepoint.");

                    // The id has to exist before the message does. Every record this
                    // tracepoint writes carries it, and that marker is the only thing
                    // that tells its records from the program's own output.
                    var id = _breakpoints.IdFor(created);
                    tracepoint.Message = request.Collect
                        ? TraceMessage.Mark(id, request.LogMessage)
                        : request.LogMessage;
                    tracepoint.BreakWhenHit = false;

                    if (request.Collect)
                    {
                        _sink.Trace.Start(id, request.MaxPerSecond, DateTime.UtcNow);

                        // Visual Studio writes a tracepoint's record to the Debug pane
                        // itself, so the pane is where it has to be picked up and the
                        // moment it lands there is the only time it can be stamped.
                        _package.EnsureTraceWatch();
                    }
                }

                var info = Describe(created, LoadedModules());
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

                if (!string.IsNullOrEmpty(request.LogMessage)) CheckLogExpressions(request, info);

                // Binding is not settled the instant a breakpoint is created, so do not
                // claim it failed to bind when it simply has not bound yet. A cause that
                // is already known - no symbols, a source newer than the binary - is
                // worth saying now rather than one call later.
                if (!info.Bound && CurrentMode != DebugModes.Design && info.BindState == BindFailure.NoCodeHere)
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

        // "Every Nth hit" is the debug engine's own filter, so a tracepoint told to log
        // one hit in fifty builds its message fifty times less often. That is where a
        // tracepoint on a hot path costs the program.

        static int HitCount(BreakpointRequest request) =>
            request.EveryNthHit > 1 ? request.EveryNthHit : request.HitCountTarget;

        static dbgHitCountType HitCountType(BreakpointRequest request) =>
            request.EveryNthHit > 1 ? dbgHitCountType.dbgHitCountTypeMultiple
                : request.HitCountTarget > 0 ? dbgHitCountType.dbgHitCountTypeEqual
                : dbgHitCountType.dbgHitCountTypeNone;

        /// <summary>
        /// Evaluates each {expr} in a tracepoint message once, so a message that would
        /// log "identifier X is undefined" a thousand times says so before it logs any
        /// of them.
        ///
        /// Only where the tracepoint sits does the answer mean anything: the same
        /// expression fails in every other frame for reasons that have nothing to do
        /// with it. Stopped anywhere else, this says it did not check rather than
        /// reporting something it did not establish.
        /// </summary>
        void CheckLogExpressions(BreakpointRequest request, BreakpointInfo info)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var expressions = TraceMessage.Expressions(request.LogMessage);
            if (expressions.Count == 0) return;

            info.LogExpressions = expressions.Select(e => new TraceExpression { Expression = e }).ToList();

            if (request.Kind == BreakpointKind.Data)
            {
                info.LogCheckDeferred =
                    "a data breakpoint fires wherever the write happens, which is not known yet";
                return;
            }

            if (CurrentMode != DebugModes.Break)
            {
                info.LogCheckDeferred = "the debuggee is not stopped, so nothing was evaluated";
                return;
            }

            var chosen = CurrentFrame();
            if (chosen.Refusal != null)
            {
                info.LogCheckDeferred = chosen.Refusal;
                return;
            }

            var here = FrameReader.Describe(chosen.Frame, chosen.Index);
            if (here == null)
            {
                info.LogCheckDeferred = "there is no current frame to evaluate them against";
                return;
            }

            if (!SameLocation(here, request))
            {
                info.LogCheckDeferred = "the debuggee is stopped in " + StoppedAt(here) +
                                        ", not where this tracepoint sits, so its locals were not checked";
                return;
            }

            foreach (var expression in info.LogExpressions)
            {
                var result = ExpressionEval.Evaluate(chosen.Frame,
                    new EvalOptions { Expression = expression.Expression, TimeoutMs = 2000 });

                if (result.IsValid) expression.Value = result.Value;
                else expression.Error = result.Error ?? "could not be evaluated";
            }
        }

        static bool SameLocation(Frame here, BreakpointRequest request)
        {
            if (request.Kind == BreakpointKind.Function)
            {
                return !string.IsNullOrEmpty(here.Function) && !string.IsNullOrEmpty(request.Function) &&
                       here.Function.IndexOf(request.Function, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return here.Line == request.Line && PathUtil.SamePath(here.File, request.File);
        }

        static string StoppedAt(Frame frame)
        {
            var name = string.IsNullOrEmpty(frame.Function) ? "an unnamed function" : frame.Function;
            if (string.IsNullOrEmpty(frame.File)) return name;
            return name + " at " + System.IO.Path.GetFileName(frame.File) + ":" + frame.Line;
        }

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

        BreakpointInfo Describe(Breakpoint breakpoint, Lazy<List<ModuleInfo>> modules)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var info = new BreakpointInfo
            {
                Id = _breakpoints.IdFor(breakpoint),
                File = breakpoint.File,
                Line = breakpoint.FileLine,
                Function = breakpoint.FunctionName,
                Condition = breakpoint.Condition,
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

            var message = Read(() => (breakpoint as EnvDTE80.Breakpoint2)?.Message, null, "the tracepoint message");
            if (!string.IsNullOrEmpty(message)) info.LogMessage = TraceMessage.Unmark(message, out _);
            info.Collecting = _sink.Trace.IsCollecting(info.Id);

            // The breakpoint the automation model hands out is the pending one; binding
            // produces its children. They answer both whether it will be hit and how
            // often it has been.
            var bound = Read(() => breakpoint.Children, null, "the bound breakpoints");
            info.HitCount = Read(() => Hits(bound), 0, "the hit count");
            ReadBindState(bound, info, modules);
            return info;
        }

        /// <summary>
        /// How many times the breakpoint was reached, totalled over every place it bound.
        ///
        /// The pending breakpoint counts nothing itself, so reading its own CurrentHits
        /// reported zero for a line that had run thousands of times. A line in a header
        /// inlined into two modules binds in both, and the question a count answers -
        /// does this fail on the first frame or after running for a while - is about the
        /// line, not about one of the copies.
        /// </summary>
        static int Hits(Breakpoints bound)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (bound == null) return 0;

            var total = 0;
            foreach (Breakpoint instance in bound) total += instance.CurrentHits;
            return total;
        }

        /// <summary>
        /// Whether the breakpoint will actually be hit, and if not, why.
        ///
        /// A breakpoint that reports success but never binds is the most expensive
        /// failure in native debugging, because everything downstream looks like the
        /// code was not reached.
        /// </summary>
        void ReadBindState(Breakpoints bound, BreakpointInfo info, Lazy<List<ModuleInfo>> modules)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (CurrentMode == DebugModes.Design)
            {
                info.Bound = false;
                info.BindState = "pending: not debugging yet, binding happens at launch";
                return;
            }

            if (bound != null && bound.Count > 0)
            {
                info.Bound = true;
                return;
            }

            info.Bound = false;
            info.BindState = WhyNotBound(info, modules);
        }

        /// <summary>
        /// The reason to give for a breakpoint that did not bind. Everything specific
        /// needs the module the file was built into, so a file that belongs to no loaded
        /// module gets the general answer.
        /// </summary>
        string WhyNotBound(BreakpointInfo info, Lazy<List<ModuleInfo>> modules)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (info.Kind != BreakpointKind.Location || string.IsNullOrEmpty(info.File))
                return BindFailure.NoCodeHere;

            var owner = OwningModule(info.File, modules.Value);
            var written = SourceFreshness.LastWritten(info.File);
            var built = owner == null ? null : SourceFreshness.LastWritten(owner.Path);

            return BindFailure.Explain(info.File, owner,
                SourceFreshness.SourceIsNewer(written, built), SourceFreshness.Show(written));
        }

        /// <summary>
        /// The loaded modules, read at most once however many breakpoints ask for them.
        /// Walking the engine's module list and reading every binary's timestamp is not
        /// free, and a list in which everything bound needs none of it.
        /// </summary>
        Lazy<List<ModuleInfo>> LoadedModules() =>
            new Lazy<List<ModuleInfo>>(() => NativeReader.ReadModules(_sink.CurrentProgram));

        public Task<List<BreakpointInfo>> BreakpointListAsync(CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var list = new List<BreakpointInfo>();
            var modules = LoadedModules();
            foreach (Breakpoint breakpoint in _dte.Debugger.Breakpoints) list.Add(Describe(breakpoint, modules));
            return list;
        });

        public Task<OpResult> BreakpointRemoveAsync(int id, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var breakpoint = _breakpoints.Find(id, _dte.Debugger.Breakpoints);
            if (breakpoint == null) return OpResult.Bad("No breakpoint #" + id + ".");

            _sink.Trace.Forget(id);
            return Try(() => breakpoint.Delete(), "Removed #" + id + ".");
        });

        /// <summary>
        /// The records are already in this process, collected as they arrived. Nothing
        /// here asks Visual Studio anything, so nothing here can be stale.
        /// </summary>
        public Task<TraceResult> TraceReadAsync(int id, int tail, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Where the pane cannot be watched as it fills, the records are still in it.
            // Reading them now is what makes this work at all on such a Visual Studio;
            // where the watch did attach this finds nothing new and costs a string.
            _package.PumpTrace(DebugPaneText());
            return _sink.Trace.Read(id, tail);
        });

        /// <summary>
        /// The whole Debug pane as text, or null if the shell will not hand it over.
        /// The shell refuses while it is busy writing, and a tracepoint that is being
        /// read is one that is being written to.
        /// </summary>
        string DebugPaneText()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Read<string>(() =>
            {
                foreach (OutputWindowPane candidate in _dte.ToolWindows.OutputWindow.OutputWindowPanes)
                {
                    if (candidate.Name.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    var selection = candidate.TextDocument.Selection;
                    selection.StartOfDocument(false);
                    selection.EndOfDocument(true);
                    var text = selection.Text;
                    selection.StartOfDocument(false);
                    return text;
                }
                return null;
            }, null, "the Debug pane");
        }

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

            // A running thread has no frames to enumerate, and an empty list reads as a
            // thread that is somehow sitting on nothing rather than a program that is
            // still going.
            RequireStopped();

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

            // A frame named here is the caller's choice, and no read is allowed to move
            // off it afterwards even when it turns out to be one nothing can be read in.
            if (frameIndex.HasValue)
            {
                _selectedFrame = Math.Max(0, frameIndex.Value);
                _framePinned = true;
            }

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

            if (options.AllThreads)
            {
                // The per-thread frames below are enumerated fresh, so the state check
                // that CurrentFrame does has to be asked for here.
                RequireStopped();

                foreach (var thread in AllThreads())
                {
                    var result = Evaluate(ChooseFrame(thread, options.FrameIndex), options);
                    result.ThreadId = ThreadIdOf(thread);
                    results.Add(result);
                }
                return results;
            }

            results.Add(Evaluate(CurrentFrame(options.FrameIndex), options));
            return results;
        });

        /// <summary>
        /// Evaluates in a frame that has already been settled on, so a frame nothing can
        /// be read in names one that can instead of failing with the same three words
        /// however many times it is asked.
        /// </summary>
        static EvalResult Evaluate(ChosenFrame chosen, EvalOptions options)
        {
            if (chosen.Refusal != null)
                return new EvalResult { Expression = options.Expression, Error = chosen.Refusal };

            var result = ExpressionEval.Evaluate(chosen.Frame, options);
            result.Frame = Reported(chosen);
            result.FrameNote = chosen.Note;
            return result;
        }

        public Task<VarsResult> VarsAsync(string scope, int depth, string filter, bool sharedAddresses, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var chosen = CurrentFrame();
            if (chosen.Refusal != null) return new VarsResult { Message = chosen.Refusal };

            return ReadIn(chosen, ExpressionEval.Scope(chosen.Frame, scope, depth, filter, sharedAddresses));
        });

        public Task<VarsResult> ExpandAsync(string reference, int depth, string typeModule, int? index, string key, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var chosen = CurrentFrame();
            if (chosen.Refusal != null) return new VarsResult { Message = chosen.Refusal };

            return ReadIn(chosen, ExpressionEval.Expand(chosen.Frame, reference, depth, typeModule, index, key));
        });

        static VarsResult ReadIn(ChosenFrame chosen, VarsResult result)
        {
            result.Frame = Reported(chosen);
            result.FrameNote = chosen.Note;
            return result;
        }

        // ---------------------------------------------------------------- native

        public Task<MemoryResult> MemoryAsync(string addressOrExpression, int size, string format, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var chosen = CurrentFrame();
            if (chosen.Refusal != null)
                return new MemoryResult { Address = addressOrExpression, Length = size, Error = chosen.Refusal };

            var memory = NativeReader.ReadMemory(chosen.Frame, addressOrExpression, size);
            memory.Frame = Reported(chosen);
            memory.FrameNote = chosen.Note;
            return memory;
        });

        public Task<RegistersResult> RegistersAsync(string group, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var chosen = CurrentFrame();
            if (chosen.Refusal != null) return new RegistersResult { Message = chosen.Refusal };

            return new RegistersResult
            {
                Registers = NativeReader.ReadRegisters(chosen.Frame, group),
                Frame = Reported(chosen),
                FrameNote = chosen.Note
            };
        });

        public Task<DisasmResult> DisasmAsync(string address, int count, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var chosen = CurrentFrame();
            if (chosen.Refusal != null) return new DisasmResult { Message = chosen.Refusal };

            return new DisasmResult
            {
                Lines = NativeReader.Disassemble(_sink.CurrentProgram, chosen.Frame, count),
                Frame = Reported(chosen),
                FrameNote = chosen.Note
            };
        });

        public Task<ModulesResult> ModulesAsync(string filter, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var loaded = NativeReader.ReadModules(_sink.CurrentProgram);
            MarkSourcesNewerThanBinaries(loaded);

            return new ModulesResult
            {
                Modules = string.IsNullOrEmpty(filter)
                    ? loaded
                    : loaded.Where(m => (m.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
                LoadedCount = loaded.Count,
                Filter = filter
            };
        });

        public Task<SymbolResult> SymbolsAsync(string module, bool load, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return SymbolLoad.For(_sink.CurrentProgram, module, load);
        });

        // ---------------------------------------------------------------- profiling

        public Task<OpResult> ProfileStartAsync(CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (CurrentMode == DebugModes.Design)
                return OpResult.Bad("Nothing is being debugged, so there is no process to profile. " +
                                    "Use launch or attach first.");

            if (_profiler == null)
            {
                _profiler = Profiler.Find(DevenvPath(), out var missing);
                if (_profiler == null) return OpResult.Bad(missing);
            }

            if (_profiler.Running)
                return OpResult.Bad("A profile is already being collected. Call profile_stop to end it.");

            var target = ProfileTarget(out var refusal);
            if (refusal != null) return OpResult.Bad(refusal);

            var failure = _profiler.Start(target.Pid, CapturePath(target.Pid));
            if (failure != null) return OpResult.Bad(failure);

            _profiled = target;
            return OpResult.Good("Collecting a profile of " + target.Describe() +
                                 ". Let it do the work you want measured, then call profile_stop. " +
                                 "Only threads on the CPU are sampled, so time spent blocked will not appear.");
        });

        public Task<ProfileCollection> ProfileStopAsync(CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_profiler == null || !_profiler.Running)
                return new ProfileCollection { Error = "Nothing is being profiled. Call profile_start first." };

            var failure = _profiler.Stop(out var seconds, out var path);
            if (failure != null) return new ProfileCollection { Error = failure };

            return new ProfileCollection
            {
                Path = path,
                ProcessName = _profiled.Name,
                Pid = _profiled.Pid,
                Seconds = seconds
            };
        });

        /// <summary>
        /// Which process to profile. With one debuggee it is that one; with several the
        /// caller has to say, because a profile of the launcher when they meant the
        /// program it started looks like a program that does nothing.
        /// </summary>
        ProcessIdentity ProfileTarget(out string refusal)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            refusal = null;

            var current = ProcessIdentity.Of(CurrentThreadObject());
            if (current.Pid != 0) return current;

            var debugged = DebuggedProcesses();
            if (debugged.Count == 1)
                return new ProcessIdentity { Pid = debugged[0].Pid, Name = debugged[0].Name };

            refusal = debugged.Count == 0
                ? "No process is being debugged in this session."
                : "This session is debugging " + debugged.Count + " processes and none is current, so " +
                  "which one to profile is not decided: " +
                  string.Join(", ", debugged.Select(p => p.Name + " (" + p.Pid + ")").ToArray()) +
                  ". Use select to pick one.";
            return default(ProcessIdentity);
        }

        /// <summary>Where this collection is written. The shim reads it and deletes it.</summary>
        static string CapturePath(int pid) =>
            System.IO.Path.Combine(Names.InstanceDir, "captures",
                pid + "-" + DateTime.UtcNow.ToString("HHmmss") + ".diagsession");

        /// <summary>
        /// The running Visual Studio's own executable, which is what locates everything
        /// that ships beside it.
        /// </summary>
        string DevenvPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Read(() => _dte.FullName, null, "the Visual Studio install path");
        }

        // ---------------------------------------------------------------- scratch

        public Task<ScratchResult> ScratchAsync(int bytes, string type, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var chosen = CurrentFrame();
            if (chosen.Refusal != null) return Held(chosen.Refusal);

            if (bytes <= 0 && string.IsNullOrWhiteSpace(type))
                return Held("Give a size in bytes, or a type to take the size from.");

            if (bytes <= 0)
            {
                bytes = Scratch.SizeOf(chosen.Frame, type, out var sizeError);
                if (bytes <= 0) return Held(sizeError);
            }

            var block = Scratch.Allocate(chosen.Frame, bytes, type, out var error);
            if (block == null) return Held(error);

            _scratch.Add(block);
            return new ScratchResult { Block = block, Outstanding = _scratch.ToList() };
        });

        public Task<ScratchResult> ScratchFreeAsync(string address, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_scratch.Count == 0) return Held("Nothing is allocated.");

            var chosen = CurrentFrame();
            if (chosen.Refusal != null) return Held(chosen.Refusal);

            var everything = string.Equals(address, "all", StringComparison.OrdinalIgnoreCase);
            var wanted = _scratch.Where(b => everything || SameAddress(b.Address, address)).ToList();
            if (wanted.Count == 0)
                return Held("No block at " + address + " came from scratch. Free one it handed out, or all.");

            var stuck = new List<string>();
            foreach (var block in wanted)
            {
                if (Scratch.Free(chosen.Frame, block, out var error)) _scratch.Remove(block);
                else stuck.Add(block.Address + ": " + error);
            }

            return new ScratchResult
            {
                Freed = wanted.Count - stuck.Count,
                Error = stuck.Count == 0 ? null : "Could not free " + string.Join("; ", stuck),
                Outstanding = _scratch.ToList()
            };
        });

        /// <summary>A refusal that still says what is outstanding, so a failed call does not read as an empty session.</summary>
        ScratchResult Held(string error) => new ScratchResult { Error = error, Outstanding = _scratch.ToList() };

        /// <summary>
        /// The same address written two ways. A caller reads one out of a reply and may
        /// well type it back without the leading zeros.
        /// </summary>
        static bool SameAddress(string block, string asked)
        {
            if (string.IsNullOrWhiteSpace(asked)) return false;

            var text = asked.Trim();
            if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = "0x" + text;

            return ScratchAddress.TryParse(block, out var left) &&
                   ScratchAddress.TryParse(text, out var right) &&
                   left == right;
        }

        /// <summary>
        /// Marks the modules whose binary is older than a source file someone has a
        /// breakpoint in. Reading that here costs a line; finding it out from a
        /// breakpoint that never binds costs a session.
        /// </summary>
        void MarkSourcesNewerThanBinaries(List<ModuleInfo> modules)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (modules.Count == 0) return;

            foreach (var file in BreakpointFiles())
            {
                var owner = OwningModule(file, modules);
                if (owner == null || !string.IsNullOrEmpty(owner.NewerSource)) continue;

                var newer = SourceFreshness.SourceIsNewer(
                    SourceFreshness.LastWritten(file), SourceFreshness.LastWritten(owner.Path));
                if (newer == true) owner.NewerSource = System.IO.Path.GetFileName(file);
            }
        }

        /// <summary>Each file a breakpoint sits in, once.</summary>
        List<string> BreakpointFiles() => Read(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Breakpoint breakpoint in _dte.Debugger.Breakpoints)
            {
                var file = breakpoint.File;
                if (!string.IsNullOrEmpty(file) && seen.Add(file)) files.Add(file);
            }
            return files;
        }, new List<string>(), "the files breakpoints are in");

        /// <summary>
        /// Which loaded module a source file was built into.
        ///
        /// The debug engine will not say which files a module was built from without
        /// reading its PDB directly, so this asks what Visual Studio already knows: the
        /// project holding the file, matched by name against the loaded modules. It
        /// answers only when exactly one module matches, because naming the wrong module
        /// in a breakpoint's failure message is worse than naming none.
        /// </summary>
        ModuleInfo OwningModule(string file, List<ModuleInfo> modules)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(file) || modules.Count == 0) return null;

            var project = Read(() => _dte.Solution.FindProjectItem(file)?.ContainingProject?.Name,
                null, "the project holding " + file);
            if (string.IsNullOrEmpty(project)) return null;

            var named = modules.Where(m => string.Equals(Stem(m), project, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0)
                named = modules.Where(m => Stem(m).IndexOf(project, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            return named.Count == 1 ? named[0] : null;
        }

        /// <summary>
        /// A module's name without its extension. What the engine reports is not always
        /// a path this machine can parse.
        /// </summary>
        static string Stem(ModuleInfo module)
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(module.Name ?? module.Path ?? ""); }
            catch (ArgumentException) { return ""; }
        }

        // ---------------------------------------------------------------- triage

        public Task<string> TriageAsync(CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (CurrentMode != DebugModes.Break)
                return "Nothing to triage: the debugger is not stopped. Current mode: " + CurrentMode + ".";

            var sb = new StringBuilder();
            var thread = CurrentThreadObject();
            var frame = CurrentFrame().Frame;

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

            var modules = NativeReader.ReadModules(_sink.CurrentProgram);
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
