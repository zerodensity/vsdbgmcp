using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using StreamJsonRpc;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Stands in for the Visual Studio extension: same pipe, same contracts, same
    /// formatter. Lets the whole shim path be tested - discovery, routing, connecting,
    /// calling, and events coming back - without Visual Studio in the picture.
    /// </summary>
    sealed class FakeHost : IDebugHost, IProjectSystem, IDisposable
    {
        readonly string _pipeName;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly List<IShimEvents> _clients = new List<IShimEvents>();
        readonly object _gate = new object();
        Task _listener;

        public FakeHost(string pipeName)
        {
            _pipeName = pipeName;
        }

        public string InstanceId { get; set; } = "Fake#1";
        public string Mode { get; set; } = DebugModes.Break;
        public string LastToken { get; private set; }
        public int HandshakeCount { get; private set; }
        public List<string> Calls { get; } = new List<string>();
        public bool FailNextCall { get; set; }

        public void Start()
        {
            _listener = Task.Run(() => ListenAsync(_cts.Token));
        }

        async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var stream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await stream.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    stream.Dispose();
                    return;
                }

                var formatter = new JsonMessageFormatter();
                formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;

                var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
                rpc.AddLocalRpcTarget<IDebugHost>(this, null);
                rpc.AddLocalRpcTarget<IProjectSystem>(this, null);

                var client = rpc.Attach<IShimEvents>();
                lock (_gate) _clients.Add(client);

                rpc.StartListening();
            }
        }

        /// <summary>Pushes a stop the way the debug engine would.</summary>
        public void RaiseStop(StopEvent stop)
        {
            List<IShimEvents> targets;
            lock (_gate) targets = new List<IShimEvents>(_clients);
            foreach (var client in targets)
            {
                try { client.OnStopAsync(stop).GetAwaiter().GetResult(); } catch { }
            }
        }

        void Record(string name)
        {
            lock (_gate) Calls.Add(name);
            if (!FailNextCall) return;
            FailNextCall = false;
            throw new InvalidOperationException("the debugger said no");
        }

        public Task<string> HandshakeAsync(int shimContractVersion, string token)
        {
            LastToken = token;
            HandshakeCount++;
            return Task.FromResult(Names.ContractVersion.ToString());
        }

        /// <summary>What the panel would have been told.</summary>
        public List<CallReport> Reports { get; } = new List<CallReport>();

        public Task ReportCallAsync(CallReport report)
        {
            lock (_gate) Reports.Add(report);
            return Task.CompletedTask;
        }

        public CallReport ReportFor(string tool)
        {
            lock (_gate) return Reports.LastOrDefault(r => r.Tool == tool);
        }

        public Task<HostStatus> GetStatusAsync(CancellationToken ct = default)
        {
            Record(nameof(GetStatusAsync));
            return Task.FromResult(new HostStatus
            {
                InstanceId = InstanceId,
                Mode = Mode,
                CurrentThreadId = 4242,
                BreakpointCount = 2,
                ActiveConfiguration = "Debug|x64",
                StartupProject = "Engine",
                Workspace = new WorkspaceInfo { Root = @"D:\repo\Engine", File = @"D:\repo\Engine\App.slnx", Name = "App" },
                TopFrames = new List<Frame>
                {
                    new Frame { Index = 0, Function = "Mesh::Upload", File = @"D:\repo\Engine\mesh.cpp", Line = 218, Module = "engine.dll" }
                },
                Watches = new Dictionary<string, string> { ["m_state"] = "Uploading" }
            });
        }

        public Task<List<BreakpointInfo>> BreakpointListAsync(CancellationToken ct = default)
        {
            Record(nameof(BreakpointListAsync));
            return Task.FromResult(new List<BreakpointInfo>
            {
                new BreakpointInfo { Id = 1, Kind = BreakpointKind.Location, File = @"D:\repo\Engine\mesh.cpp", Line = 218, Enabled = true, Bound = true },
                new BreakpointInfo { Id = 2, Kind = BreakpointKind.Function, Function = "Mesh::Free", Module = "engine.dll", Enabled = true, Bound = false, BindState = "module not loaded" }
            });
        }

        public Task<BreakpointInfo> BreakpointSetAsync(BreakpointRequest request, CancellationToken ct = default)
        {
            Record(nameof(BreakpointSetAsync));
            return Task.FromResult(new BreakpointInfo
            {
                Id = 7,
                Kind = request.Kind,
                File = request.File,
                Line = request.Line,
                Function = request.Function,
                Expression = request.Expression,
                Size = request.Size,
                Bound = false,
                BindState = "pending: not debugging yet, binding happens at launch"
            });
        }

        public Task<List<EvalResult>> EvalAsync(EvalOptions options, CancellationToken ct = default)
        {
            Record(nameof(EvalAsync));
            var results = new List<EvalResult>();

            if (options.AllThreads)
            {
                results.Add(new EvalResult { Expression = options.Expression, Value = "1", IsValid = true, ThreadId = 10 });
                results.Add(new EvalResult { Expression = options.Expression, Value = "1", IsValid = true, ThreadId = 11 });
                results.Add(new EvalResult { Expression = options.Expression, Value = "9", IsValid = true, ThreadId = 12 });
                return Task.FromResult(results);
            }

            results.Add(new EvalResult
            {
                Expression = options.Expression,
                Value = options.AllowSideEffects ? "42" : "<function evaluation refused>",
                Type = "int",
                IsValid = options.AllowSideEffects,
                Error = options.AllowSideEffects ? null : "needs a function call; set allowSideEffects"
            });
            return Task.FromResult(results);
        }

        public Task<BuildResult> BuildAsync(string mode, string project, string configuration, string platform, CancellationToken ct = default)
        {
            Record(nameof(BuildAsync));
            return Task.FromResult(new BuildResult
            {
                Succeeded = false,
                ElapsedSeconds = 12.5,
                TotalErrors = 3,
                TotalWarnings = 1,
                Diagnostics = new List<BuildDiagnostic>
                {
                    new BuildDiagnostic { Severity = "error", Code = "C2065", Text = "'foo': undeclared identifier", File = @"D:\repo\Engine\mesh.cpp", Line = 12 }
                }
            });
        }

        // Everything below is unused by the tests but has to exist for the proxy to attach.

        public Task<OpResult> LaunchAsync(LaunchRequest request, CancellationToken ct = default) { Record(nameof(LaunchAsync)); return Ok(); }
        public Task<OpResult> AttachAsync(AttachRequest request, CancellationToken ct = default) { Record(nameof(AttachAsync)); return Ok(); }
        public Task<OpResult> DetachAsync(int? pid, CancellationToken ct = default) => Ok();
        public Task<OpResult> StopAsync(int? pid, CancellationToken ct = default) => Ok();
        public Task<OpResult> RestartAsync(CancellationToken ct = default) => Ok();
        public Task<List<ProcessInfo>> ProcessesAsync(bool includeLocal, CancellationToken ct = default) =>
            Task.FromResult(new List<ProcessInfo> { new ProcessInfo { Pid = 900, Name = "engine.exe", IsDebugged = true } });
        public Task<OpResult> OpenDumpAsync(string path, CancellationToken ct = default) => Ok();
        public Task<OpResult> GoAsync(CancellationToken ct = default) { Record(nameof(GoAsync)); return Ok(); }
        /// <summary>What pause answers. The real host refuses when nothing is running.</summary>
        public OpResult PauseResult { get; set; } = OpResult.Good(null);
        public Task<OpResult> PauseAsync(CancellationToken ct = default) => Task.FromResult(PauseResult);
        public Task<OpResult> StepAsync(string kind, int count, CancellationToken ct = default) { Record(nameof(StepAsync)); return Ok(); }
        public Task<OpResult> RunToAsync(string file, int line, CancellationToken ct = default) => Ok();
        public Task<OpResult> SetNextAsync(string file, int line, CancellationToken ct = default) => Ok();
        public Task<OpResult> BreakpointRemoveAsync(int id, CancellationToken ct = default) => Ok();
        public Task<OpResult> BreakpointEnableAsync(int id, bool enabled, CancellationToken ct = default) => Ok();
        public Task<OpResult> ExceptionSetAsync(ExceptionSetting setting, CancellationToken ct = default) => Ok();
        public Task<List<ExceptionSetting>> ExceptionListAsync(CancellationToken ct = default) => Task.FromResult(new List<ExceptionSetting>());
        /// <summary>A launcher and the editor it started, which is the shape that broke.</summary>
        public Task<List<ThreadSummary>> ThreadsAsync(int frameDepth, string process, CancellationToken ct = default)
        {
            Record(nameof(ThreadsAsync));

            var all = new List<ThreadSummary>
            {
                new ThreadSummary { Id = 100, ProcessName = "nosEditor.exe", Pid = 4001, IsCurrent = true,
                    TopFrames = new List<Frame> { new Frame { Function = "Editor::Tick" } } },
                new ThreadSummary { Id = 101, ProcessName = "nosEditor.exe", Pid = 4001,
                    TopFrames = new List<Frame> { new Frame { Function = "ntdll!Wait" } } },
                new ThreadSummary { Id = 200, ProcessName = "nosLauncher.exe", Pid = 4002,
                    TopFrames = new List<Frame> { new Frame { Function = "Launcher::Pump" } } }
            };

            if (string.IsNullOrEmpty(process)) return Task.FromResult(all);

            return Task.FromResult(all
                .Where(t => t.Pid.ToString() == process ||
                            t.ProcessName.IndexOf(process, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList());
        }
        public Task<List<Frame>> StackAsync(int? threadId, int count, CancellationToken ct = default) => Task.FromResult(new List<Frame>());
        public Task<OpResult> SelectAsync(int? threadId, int? frameIndex, string process, CancellationToken ct = default)
        {
            Record(nameof(SelectAsync));

            if (!string.IsNullOrEmpty(process))
            {
                if (process.IndexOf("nosLauncher", StringComparison.OrdinalIgnoreCase) < 0 && process != "4002")
                {
                    return Task.FromResult(OpResult.Bad(
                        "No process matching '" + process + "' is being debugged.\n" + KnownThreads));
                }
                return Task.FromResult(OpResult.Good("thread 200 in nosLauncher.exe (4002), frame 0"));
            }

            if (threadId.HasValue && threadId.Value != 100 && threadId.Value != 101 && threadId.Value != 200)
            {
                return Task.FromResult(OpResult.Bad(
                    "No thread " + threadId.Value + " in this session.\n" + KnownThreads));
            }

            return Task.FromResult(OpResult.Good("thread " + threadId + " in nosLauncher.exe (4002), frame 0"));
        }

        /// <summary>What the real host appends to a thread or process that did not resolve.</summary>
        const string KnownThreads =
            "Threads in this session:\n" +
            "  nosEditor.exe (4001): 100, 101\n" +
            "  nosLauncher.exe (4002): 200";
        public Task<OpResult> FreezeAsync(int threadId, bool frozen, CancellationToken ct = default) => Ok();
        public Task<List<VarNode>> VarsAsync(string scope, int depth, string filter, CancellationToken ct = default) => Task.FromResult(new List<VarNode>());
        public Task<List<VarNode>> ExpandAsync(string reference, int depth, CancellationToken ct = default) => Task.FromResult(new List<VarNode>());
        public Task<OpResult> WatchSetAsync(string[] expressions, CancellationToken ct = default) => Ok();
        public Task<MemoryResult> MemoryAsync(string addressOrExpression, int size, string format, CancellationToken ct = default) => Task.FromResult(new MemoryResult());
        public Task<List<RegisterInfo>> RegistersAsync(string group, CancellationToken ct = default) => Task.FromResult(new List<RegisterInfo>());
        public Task<List<DisasmLine>> DisasmAsync(string address, int count, CancellationToken ct = default) => Task.FromResult(new List<DisasmLine>());
        public Task<List<ModuleInfo>> ModulesAsync(string filter, CancellationToken ct = default) => Task.FromResult(new List<ModuleInfo>());
        public Task<string> TriageAsync(CancellationToken ct = default) => Task.FromResult("nothing to triage");
        public Task<CaptureResult> CaptureAsync(int[] region, CancellationToken ct = default) => Task.FromResult(new CaptureResult());
        public Task<ConsoleResult> ConsoleReadAsync(int tailLines, CancellationToken ct = default) => Task.FromResult(new ConsoleResult { Text = "hello from the debuggee" });
        public Task<OpResult> ConsoleSendAsync(string text, string keys, CancellationToken ct = default) => Ok();
        public Task<OutputResult> OutputReadAsync(string pane, string pattern, int tailLines, CancellationToken ct = default) => Task.FromResult(new OutputResult());
        public Task<OpResult> BuildCancelAsync(CancellationToken ct = default) => Ok();
        public Task<OutputResult> BuildOutputAsync(string pattern, int tailLines, CancellationToken ct = default) => Task.FromResult(new OutputResult());
        public Task<string> ConfigurationAsync(string set, CancellationToken ct = default) => Task.FromResult("Debug|x64");
        public Task<string> StartupProjectAsync(string set, CancellationToken ct = default) => Task.FromResult("Engine");
        public Task<List<string>> ProjectsAsync(CancellationToken ct = default) => Task.FromResult(new List<string> { "Engine", "Editor" });

        static Task<OpResult> Ok() => Task.FromResult(OpResult.Good(null));

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener?.Wait(500); } catch { }
            _cts.Dispose();
        }
    }
}
