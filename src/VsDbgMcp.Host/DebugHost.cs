using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VsDbgMcp.Contracts;
using Task = System.Threading.Tasks.Task;
using Thread = EnvDTE.Thread;

namespace VsDbgMcp.Host
{
    sealed partial class DebugHost : IDebugHost
    {
        readonly VsDbgMcpPackage _package;
        readonly DTE2 _dte;
        readonly IVsSolution _solution;
        readonly IVsDebugger _vsDebugger;
        readonly DebugEventSink _sink;
        readonly JoinableTaskFactory _jtf;
        readonly Action<string> _log;

        readonly BreakpointTable _breakpoints = new BreakpointTable();
        readonly Dictionary<int, uint> _suspended = new Dictionary<int, uint>();
        readonly object _modeGate = new object();

        PipeServer _server;
        string[] _watches = new string[0];
        int _selectedThreadId;
        int _selectedFrame;
        TaskCompletionSource<string> _modeWaiter;

        public DebugHost(VsDbgMcpPackage package, DTE2 dte, IVsSolution solution, IVsDebugger vsDebugger,
            DebugEventSink sink, JoinableTaskFactory jtf, Action<string> log)
        {
            _package = package;
            _dte = dte;
            _solution = solution;
            _vsDebugger = vsDebugger;
            _sink = sink;
            _jtf = jtf;
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// The tools whose effect is that the debuggee starts running again, which is
        /// what brings Visual Studio to the front when it next stops.
        /// </summary>
        static readonly HashSet<string> ResumesExecution = new HashSet<string>(StringComparer.Ordinal)
        {
            "launch", "go", "step", "run_to", "restart", "pause"
        };

        public string CurrentMode { get; private set; } = DebugModes.Design;

        public void AttachServer(PipeServer server) => _server = server;

        public void SetMode(string mode)
        {
            // A frame does not survive its thread resuming, so neither should a pinned
            // selection. Leaving it in place would quietly evaluate later expressions in
            // a process the caller stopped meaning to look at.
            if (mode != DebugModes.Break) _selectedThreadId = 0;

            CurrentMode = mode;
            TaskCompletionSource<string> waiter;
            lock (_modeGate)
            {
                waiter = _modeWaiter;
                _modeWaiter = null;
            }
            waiter?.TrySetResult(mode);
        }

        Task<string> NextModeAsync(TimeSpan timeout)
        {
            TaskCompletionSource<string> waiter;
            lock (_modeGate)
            {
                _modeWaiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiter = _modeWaiter;
            }

            return Task.WhenAny(waiter.Task, Task.Delay(timeout))
                .ContinueWith(t => waiter.Task.IsCompleted ? waiter.Task.Result : null,
                    TaskScheduler.Default);
        }

        /// <summary>
        /// Runs on the UI thread with the message filter installed, which is what keeps
        /// calls made during break mode from failing with an RPC rejection.
        ///
        /// Every tool passes through here, which makes it the one place that can record
        /// what the agent did and the one place that can refuse to let it.
        /// </summary>
        async Task<T> UIAsync<T>(Func<T> body, [CallerMemberName] string caller = null)
        {
            if (Activity.Paused) throw new InvalidOperationException(Activity.PausedMessage);

            await _jtf.SwitchToMainThreadAsync();
            MessageFilter.EnsureInstalled();

            // Only the commands that resume execution make Visual Studio come forward,
            // and only those should hand the foreground back afterwards. Arming on every
            // call would leave the guard primed to fire on a stop the person at the
            // keyboard caused, taking focus away from them mid-step.
            if (ResumesExecution.Contains(Name(caller))) FocusGuard.Arm();

            var started = Stopwatch.StartNew();
            var failed = false;
            try
            {
                return body();
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                started.Stop();
                Activity.Record(Name(caller), null, (int)started.ElapsedMilliseconds, failed);
            }
        }

        Task<OpResult> UIOpAsync(Func<OpResult> body, [CallerMemberName] string caller = null) =>
            UIAsync(body, caller);

        /// <summary>
        /// The tool name the agent used, worked out from the method serving it. The panel
        /// is read by a person deciding whether to let this carry on, so it should say
        /// what was asked for rather than which method happened to answer.
        /// </summary>
        internal static string Name(string caller)
        {
            if (string.IsNullOrEmpty(caller)) return "(unknown)";

            var name = caller.EndsWith("Async", StringComparison.Ordinal)
                ? caller.Substring(0, caller.Length - "Async".Length)
                : caller;

            switch (name)
            {
                case "GetStatus": return "status";
                case "LaunchCore": return "launch";
                case "OpenDump": return "dump_open";
                case "OutputRead": return "output";
                case "Configuration": return "config";
                case "ExceptionSet":
                case "ExceptionList": return "exceptions_set";
                case "Breakpoint": return "bp";
            }

            if (name.StartsWith("Breakpoint", StringComparison.Ordinal))
                return "bp_" + Snake(name.Substring("Breakpoint".Length));

            return Snake(name);
        }

        static string Snake(string pascal)
        {
            var sb = new System.Text.StringBuilder(pascal.Length + 4);
            for (var i = 0; i < pascal.Length; i++)
            {
                if (i > 0 && char.IsUpper(pascal[i])) sb.Append('_');
                sb.Append(char.ToLowerInvariant(pascal[i]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Runs an automation command and turns a refusal into a result the caller can
        /// read. These are cross-process calls into the shell, which rejects them
        /// outright when it is busy, so this is the one place that translation happens.
        /// </summary>
        static OpResult Try(Action action, string success)
        {
            try
            {
                action();
                return OpResult.Good(success);
            }
            catch (Exception ex)
            {
                return OpResult.Bad(ex.Message);
            }
        }

        /// <summary>
        /// Reads one automation property, falling back when the shell refuses. Only for
        /// values status is better off reporting without than failing over: a build in
        /// progress must not stop the agent from seeing where the debugger is.
        /// </summary>
        T Read<T>(Func<T> read, T fallback, string what)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                _log("could not read " + what + ": " + ex.Message);
                return fallback;
            }
        }

        // ---------------------------------------------------------------- handshake

        public Task<string> HandshakeAsync(int shimContractVersion, string token)
        {
            var version = _server?.Handshake(shimContractVersion, token) ?? Names.ContractVersion.ToString();
            return Task.FromResult(version);
        }

        // ---------------------------------------------------------------- status

        public Task<HostStatus> GetStatusAsync(CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var status = new HostStatus
            {
                InstanceId = InstanceId(),
                Workspace = WorkspaceProbe.Read(_solution),
                Mode = CurrentMode,
                Watches = EvaluateWatches(),
                Processes = DebuggedProcesses(),
                BreakpointCount = BreakpointCount(),
                ActiveConfiguration = ActiveConfiguration(),
                StartupProject = StartupProjectName()
            };

            var thread = CurrentThreadObject();
            if (thread != null)
            {
                var identity = ProcessIdentity.Of(thread);
                status.CurrentThreadId = ThreadIdOf(thread);
                status.CurrentProcessName = identity.Name;
                status.CurrentPid = identity.Pid;
                status.ThreadWasSelected = _selectedThreadId != 0;
                status.CurrentFrameIndex = _selectedFrame;
                status.TopFrames = FrameReader.Frames(thread, 5);
            }

            if (CurrentMode == DebugModes.Break) status.PendingException = _sink.LastException;
            return status;
        });

        string InstanceId()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var workspace = WorkspaceProbe.Read(_solution);
            var name = string.IsNullOrEmpty(workspace?.Name) ? "vs" : workspace.Name;
            return name + "#" + System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        int BreakpointCount()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Read(() => _dte.Debugger.Breakpoints.Count, 0, "the breakpoint count");
        }

        string ActiveConfiguration()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Read(() =>
            {
                var config = _dte.Solution.SolutionBuild.ActiveConfiguration;
                if (config == null) return null;

                var platform = config.SolutionContexts.Count > 0
                    ? config.SolutionContexts.Item(1).PlatformName
                    : null;

                return string.IsNullOrEmpty(platform) ? config.Name : config.Name + "|" + platform;
            }, null, "the active configuration");
        }

        string StartupProjectName()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Read(() => _dte.Solution.SolutionBuild.StartupProjects is Array projects && projects.Length > 0
                ? projects.GetValue(0)?.ToString()
                : null, null, "the startup project");
        }

        List<ProcessInfo> DebuggedProcesses()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Read(() =>
            {
                var list = new List<ProcessInfo>();
                foreach (EnvDTE.Process process in _dte.Debugger.DebuggedProcesses)
                {
                    list.Add(new ProcessInfo
                    {
                        Pid = process.ProcessID,
                        Name = System.IO.Path.GetFileName(process.Name),
                        Path = process.Name,
                        IsDebugged = true
                    });
                }
                return list;
            }, new List<ProcessInfo>(), "the debugged processes");
        }

        // ---------------------------------------------------------------- session

        public Task<OpResult> LaunchAsync(LaunchRequest request, CancellationToken ct = default) =>
            LaunchCoreAsync(request ?? new LaunchRequest(), ct);

        async Task<OpResult> LaunchCoreAsync(LaunchRequest request, CancellationToken ct)
        {
            var prepared = await UIOpAsync(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (!string.IsNullOrEmpty(request.Project))
                {
                    var set = SetStartupProject(request.Project);
                    if (!set.Ok) return set;
                }

                if (!string.IsNullOrEmpty(request.Args))
                {
                    // Launching with arguments silently dropped is worse than not
                    // launching: the debuggee would take a path the caller did not ask for.
                    var applied = SetStartArguments(request.Args);
                    if (!applied.Ok)
                        return OpResult.Bad("Could not set the debugger arguments: " + applied.Message);
                }

                return Try(() =>
                {
                    if (request.NoDebug) _dte.ExecuteCommand("Debug.StartWithoutDebugging");
                    else if (request.StopAtEntry) _dte.ExecuteCommand("Debug.StepInto");
                    else _dte.ExecuteCommand("Debug.Start");
                }, null);
            }).ConfigureAwait(false);

            if (!prepared.Ok) return prepared;
            if (request.NoDebug) return OpResult.Good("Started without the debugger.");

            var mode = await NextModeAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (mode == null) return OpResult.Good("Launch issued; the debugger has not reported a state yet.");
            if (mode == DebugModes.Break) return OpResult.Good("Launched and already stopped. Call wait or status.");
            if (mode == DebugModes.Design) return OpResult.Bad("The debuggee exited immediately. Check the Debug output pane.");
            return OpResult.Good("Running.");
        }

        public Task<OpResult> AttachAsync(AttachRequest request, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (request == null) return OpResult.Bad("Give a pid or a name pattern.");

            var candidates = new List<EnvDTE.Process>();
            try
            {
                foreach (EnvDTE.Process process in _dte.Debugger.LocalProcesses)
                {
                    if (request.Pid.HasValue)
                    {
                        if (process.ProcessID == request.Pid.Value) candidates.Add(process);
                        continue;
                    }

                    if (string.IsNullOrEmpty(request.NameRegex)) continue;

                    var name = System.IO.Path.GetFileName(process.Name) ?? "";
                    if (System.Text.RegularExpressions.Regex.IsMatch(name, request.NameRegex,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        candidates.Add(process);
                    }
                }

                if (candidates.Count == 0)
                    return OpResult.Bad("No process matched. Call processes with includeLocal to see what is running.");

                if (candidates.Count > 1)
                {
                    var names = candidates.Take(12)
                        .Select(p => System.IO.Path.GetFileName(p.Name) + " (" + p.ProcessID + ")");
                    return OpResult.Bad("Matched " + candidates.Count + " processes; attach by pid instead:\n  " +
                                        string.Join("\n  ", names));
                }

                candidates[0].Attach();
                return OpResult.Good("Attached to " + System.IO.Path.GetFileName(candidates[0].Name) +
                                     " (" + candidates[0].ProcessID + ").");
            }
            catch (Exception ex)
            {
                return OpResult.Bad(ex.Message);
            }
        });

        public Task<OpResult> DetachAsync(int? pid, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Try(() =>
            {
                if (!pid.HasValue) { _dte.Debugger.DetachAll(); return; }

                foreach (EnvDTE.Process process in _dte.Debugger.DebuggedProcesses)
                {
                    if (process.ProcessID != pid.Value) continue;
                    process.Detach(false);
                    return;
                }
                throw new InvalidOperationException("Process " + pid.Value + " is not being debugged.");
            }, "Detached.");
        });

        public Task<OpResult> StopAsync(int? pid, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Try(() =>
            {
                if (!pid.HasValue) { _dte.Debugger.Stop(false); return; }

                foreach (EnvDTE.Process process in _dte.Debugger.DebuggedProcesses)
                {
                    if (process.ProcessID != pid.Value) continue;
                    process.Terminate(false);
                    return;
                }
                throw new InvalidOperationException("Process " + pid.Value + " is not being debugged.");
            }, "Stopped.");
        });

        public Task<OpResult> RestartAsync(CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Try(() => _dte.ExecuteCommand("Debug.Restart"), "Restarted.");
        });

        public Task<List<ProcessInfo>> ProcessesAsync(bool includeLocal, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var list = DebuggedProcesses();
            if (!includeLocal) return list;

            return Read(() =>
            {
                var known = new HashSet<int>(list.Select(p => p.Pid));
                foreach (EnvDTE.Process process in _dte.Debugger.LocalProcesses)
                {
                    if (known.Contains(process.ProcessID)) continue;
                    list.Add(new ProcessInfo
                    {
                        Pid = process.ProcessID,
                        Name = System.IO.Path.GetFileName(process.Name),
                        Path = process.Name,
                        IsDebugged = false
                    });
                }
                return list;
            }, list, "the local processes");
        });

        public Task<OpResult> OpenDumpAsync(string path, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                return OpResult.Bad("No file at " + path + ".");

            return Try(() =>
            {
                _dte.ItemOperations.OpenFile(path);
                _dte.ExecuteCommand("Debug.Start");
            }, "Dump opened and loaded. Inspection tools now work against it.");
        });

        // ---------------------------------------------------------------- execution

        public Task<OpResult> GoAsync(CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (CurrentMode == DebugModes.Design) return OpResult.Bad("Not debugging. Use launch or attach first.");
            return Try(() => _dte.Debugger.Go(false), "Running.");
        });

        public Task<OpResult> PauseAsync(CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (CurrentMode != DebugModes.Run) return OpResult.Bad("Nothing is running. Current mode: " + CurrentMode + ".");
            return Try(() => _dte.Debugger.Break(false), "Break requested.");
        });

        public Task<OpResult> StepAsync(string kind, int count, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (CurrentMode != DebugModes.Break) return OpResult.Bad("Stepping needs break mode. Current mode: " + CurrentMode + ".");

            return Try(() =>
            {
                for (var i = 0; i < Math.Max(1, count); i++)
                {
                    switch (kind)
                    {
                        case StepKind.Into: _dte.Debugger.StepInto(false); break;
                        case StepKind.Out: _dte.Debugger.StepOut(false); break;
                        default: _dte.Debugger.StepOver(false); break;
                    }
                }
            }, "Stepping.");
        });

        public Task<OpResult> RunToAsync(string file, int line, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var moved = MoveCaret(file, line);
            if (!moved.Ok) return moved;
            return Try(() => _dte.Debugger.RunToCursor(false), "Running to " + line + ".");
        });

        public Task<OpResult> SetNextAsync(string file, int line, CancellationToken ct = default) => UIOpAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (CurrentMode != DebugModes.Break) return OpResult.Bad("Setting the next statement needs break mode.");

            var moved = MoveCaret(file, line);
            if (!moved.Ok) return moved;
            return Try(() => _dte.ExecuteCommand("Debug.SetNextStatement"),
                "Instruction pointer moved to line " + line + ".");
        });

        OpResult MoveCaret(string file, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Try(() =>
            {
                var window = _dte.ItemOperations.OpenFile(file);
                window?.Activate();

                if (!(_dte.ActiveDocument?.Selection is TextSelection selection))
                    throw new InvalidOperationException("Could not place the caret in " + file + ".");

                selection.GotoLine(line, false);
            }, null);
        }

        OpResult SetStartupProject(string project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Try(() =>
            {
                foreach (EnvDTE.Project candidate in Projects())
                {
                    if (!string.Equals(candidate.Name, project, StringComparison.OrdinalIgnoreCase)) continue;
                    _dte.Solution.SolutionBuild.StartupProjects = candidate.UniqueName;
                    return;
                }
                throw new InvalidOperationException("No project named '" + project + "'.");
            }, null);
        }

        OpResult SetStartArguments(string args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!(_dte.Solution.SolutionBuild.StartupProjects is Array startups) || startups.Length == 0)
                    return OpResult.Bad("No startup project.");

                var unique = startups.GetValue(0)?.ToString();
                foreach (EnvDTE.Project project in Projects())
                {
                    if (!string.Equals(project.UniqueName, unique, StringComparison.OrdinalIgnoreCase)) continue;

                    var configuration = project.ConfigurationManager?.ActiveConfiguration;
                    if (configuration == null) return OpResult.Bad("No active configuration.");

                    // C++ keeps debugger arguments on the VC configuration's DebugSettings.
                    // Those configurations hang off the VCProject, not off the automation
                    // Configuration, and the generic Properties collection does not expose
                    // them at all. Reached by name because the VC project interop types are
                    // not in the SDK package.
                    if (SetNativeArguments(project, configuration, args)) return OpResult.Good(null);

                    // Managed projects do expose it there, under a different name.
                    var managed = Read(() =>
                    {
                        configuration.Properties.Item("StartArguments").Value = args;
                        return true;
                    }, false, "the managed debugger arguments");

                    if (managed) return OpResult.Good(null);
                    return OpResult.Bad("This project type does not expose debugger arguments.");
                }
                return OpResult.Bad("Startup project not found.");
            }
            catch (Exception ex)
            {
                return OpResult.Bad(ex.Message);
            }
        }

        /// <summary>
        /// Late bound: the VC project interop types are not in the SDK package, so a
        /// project that is not a C++ one fails here by name at runtime rather than
        /// being ruled out at compile time. The caller falls back when this returns false.
        /// </summary>
        bool SetNativeArguments(EnvDTE.Project project, EnvDTE.Configuration configuration, string args)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Read(() =>
            {
                dynamic vcProject = project.Object;
                if (vcProject == null) return false;

                foreach (dynamic vcConfiguration in vcProject.Configurations)
                {
                    string name = Convert.ToString(vcConfiguration.ConfigurationName);
                    string platform = Convert.ToString(vcConfiguration.Platform.Name);

                    if (!string.Equals(name, configuration.ConfigurationName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(platform, configuration.PlatformName, StringComparison.OrdinalIgnoreCase)) continue;

                    vcConfiguration.DebugSettings.CommandArguments = args;
                    return true;
                }
                return false;
            }, false, "the native debugger arguments");
        }

        IEnumerable<EnvDTE.Project> Projects()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return SolutionProjects.All(_dte);
        }

        // ---------------------------------------------------------------- watches

        public Task<OpResult> WatchSetAsync(string[] expressions, CancellationToken ct = default)
        {
            _watches = expressions ?? new string[0];
            return Task.FromResult(OpResult.Good(null));
        }

        Dictionary<string, string> EvaluateWatches()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_watches.Length == 0 || CurrentMode != DebugModes.Break) return null;

            var frame = CurrentFrame(0);
            if (frame == null) return null;

            var values = new Dictionary<string, string>();
            foreach (var expression in _watches)
            {
                var result = ExpressionEval.Evaluate(frame, new EvalOptions { Expression = expression, TimeoutMs = 1000 });
                values[expression] = result.IsValid ? result.Value : "<" + (result.Error ?? "error") + ">";
            }
            return values;
        }

        /// <summary>Called before a stop is broadcast, so watches ride along with it.</summary>
        public void FillWatches(StopEvent stop)
        {
            if (stop == null) return;

            _jtf.Run(async () =>
            {
                await _jtf.SwitchToMainThreadAsync();
                MessageFilter.EnsureInstalled();
                stop.BreakpointId = _breakpoints.MatchLocation(stop.Frame);
                if (_watches.Length > 0) stop.Watches = EvaluateWatches();
            });
        }

        IDebugStackFrame2 CurrentFrame(int index)
        {
            var thread = CurrentThreadObject();
            return thread == null ? null : FrameReader.FrameAt(thread, index > 0 ? index : _selectedFrame);
        }

        IDebugThread2 CurrentThreadObject()
        {
            if (_selectedThreadId != 0)
            {
                var match = AllThreads().FirstOrDefault(t => ThreadIdOf(t) == _selectedThreadId);
                if (match != null) return match;
            }
            return _sink.CurrentThread;
        }

        static int ThreadIdOf(IDebugThread2 thread)
        {
            if (thread == null) return 0;
            return thread.GetThreadId(out var id) == VSConstants.S_OK ? unchecked((int)id) : 0;
        }

        /// <summary>
        /// Every thread in the session, across every process it holds.
        ///
        /// Enumerating only the program that last stopped is what made a launcher's
        /// threads look nonexistent to a caller who could plainly see the process.
        /// </summary>
        List<IDebugThread2> AllThreads()
        {
            var threads = new List<IDebugThread2>();

            foreach (var program in _sink.Programs)
            {
                if (program.EnumThreads(out var enumerator) != VSConstants.S_OK || enumerator == null) continue;

                var buffer = new IDebugThread2[1];
                uint fetched = 0;
                while (enumerator.Next(1, buffer, ref fetched) == VSConstants.S_OK && fetched == 1)
                    threads.Add(buffer[0]);
            }

            return threads;
        }

        /// <summary>
        /// Names the threads a caller could have meant. An id that does not resolve is
        /// nearly always an id from another process, so saying which ids exist and where
        /// turns a dead end into the next call.
        /// </summary>
        string KnownThreads()
        {
            var lines = new List<string>();
            foreach (var group in AllThreads()
                         .Select(t => new { Id = ThreadIdOf(t), Process = ProcessIdentity.Of(t) })
                         .GroupBy(t => t.Process.Describe()))
            {
                var ids = group.Select(t => t.Id.ToString()).Take(24).ToList();
                lines.Add("  " + group.Key + ": " + string.Join(", ", ids) +
                          (group.Count() > ids.Count ? ", ..." : ""));
            }

            return lines.Count == 0
                ? "  (no threads; the debugger is not stopped)"
                : string.Join("\n", lines);
        }
    }
}
