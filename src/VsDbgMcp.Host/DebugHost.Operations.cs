using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;
using Task = System.Threading.Tasks.Task;

namespace VsDbgMcp.Host
{
    partial class DebugHost : IOperationHost
    {
        readonly object _historyGate = new object();
        readonly List<Intervention> _interventions = new List<Intervention>();
        int _sessionGeneration;
        ProfileCollection _activeProfile;
        ProfileCollection _lastProfile;
        Intervention _lastTransition;

        void Mark(string kind, string detail = null)
        {
            var marker = new Intervention { Kind = kind, Detail = detail, TimestampUtc = DateTime.UtcNow,
                SessionGeneration = _sessionGeneration, Pid = _activeProfile?.Pid };
            lock (_historyGate)
            {
                _interventions.Add(marker);
                if (_interventions.Count > 10000) _interventions.RemoveAt(0);
            }
        }

        public Task<List<OperationInfo>> OperationsAsync(CancellationToken ct = default) => Task.FromResult(HostOperations.Store.All());

        public async Task<OperationInfo> OperationStatusAsync(string id, int waitSeconds, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Give an operationId from build, launch or bp_set.");
            var item = await HostOperations.Store.WaitAsync(id, waitSeconds, ct).ConfigureAwait(false);
            if (item == null) return HostOperations.Historical(id) ?? throw new ArgumentException("Unknown operationId: " + id);
            if (item.Terminal && item.State != "unknown" && !item.BlocksNewRequests) return item;
            // Retained status remains available even when VS cannot service its UI queue.
            var query = ObserveAsync();
            StateObservation observation = null;
            if (await Task.WhenAny(query, Task.Delay(1000, ct)).ConfigureAwait(false) == query)
            {
                try { observation = await query.ConfigureAwait(false); }
                catch (Exception ex) { observation = new StateObservation { Mode = "unknown", TimestampUtc = DateTime.UtcNow, Error = ex.Message }; }
            }
            ct.ThrowIfCancellationRequested();
            if (observation != null && item.State == "unknown") HostOperations.Store.ObserveIdle(id, observation);
            item = HostOperations.Store.Read(id);
            item.Observation = observation ?? new StateObservation { Mode = "unknown", TimestampUtc = DateTime.UtcNow, Error = "VS state query unavailable; retained operation status is shown." };
            return item;
        }

        // No watches, expression evaluation, stack walking, or target pauses.
        readonly object _observationGate = new object();
        Task<StateObservation> _pendingObservation;
        public Task<StateObservation> ObserveAsync(CancellationToken ct = default)
        {
            lock (_observationGate)
            {
                if (_pendingObservation == null || _pendingObservation.IsCompleted)
                {
                    _pendingObservation = Task.Run(() => UIAsync(LiveObservation));
                    // A timed-out observer must not leave an unobserved fault, or enqueue
                    // another UI walk while the original request is still blocked.
                    _ = _pendingObservation.ContinueWith(t => { var ignored = t.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                }
                return _pendingObservation;
            }
        }
        StateObservation LiveObservation()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var observation = new StateObservation { TimestampUtc = DateTime.UtcNow, SessionGeneration = _sessionGeneration,
                LastTransition = _lastTransition, ActiveProfile = _activeProfile == null ? null : HostOperations.Copy(_activeProfile),
                LastProfile = _lastProfile == null ? null : HostOperations.Copy(_lastProfile) };
            try
            {
                var mode = _dte.Debugger.CurrentMode;
                observation.Mode = mode == dbgDebugMode.dbgRunMode ? "run" : mode == dbgDebugMode.dbgBreakMode ? "break" : "design";
                observation.Processes = DebuggedProcesses();
                observation.EvidenceSource = "live DTE debugger mode and registered processes";
            }
            catch (Exception ex) { observation.Mode = "unknown"; observation.Error = ex.Message; }
            try { observation.BuildBusy = _package.ObserveBuildBusy(); }
            catch (Exception ex) { observation.Error = (observation.Error == null ? "" : observation.Error + "; ") + "Build state unavailable: " + ex.Message; }
            return observation;
        }

        public async Task<OpResult> LaunchAsync(LaunchRequest request, CancellationToken ct = default)
        {
            request = request ?? new LaunchRequest();
            var fingerprint = JsonConvert.SerializeObject(new { request.Project, request.Args, request.Env, request.NoDebug, request.StopAtEntry });
            var item = HostOperations.Store.Begin("launch", request.RequestId, fingerprint, true, out var created);
            if (created) HostOperations.Run(item.OperationId, async () =>
            {
                var result = await LaunchCoreAsync(request, CancellationToken.None, item.OperationId).ConfigureAwait(false);
                if (!result.Ok)
                {
                    HostOperations.Store.Update(item.OperationId, o => { o.State = "failed"; o.Result = result; }, true);
                    return;
                }
                if (request.NoDebug)
                {
                    HostOperations.Store.Update(item.OperationId, o => { o.State = "issued";
                        o.Result = OpResult.Good("Start without debugging command returned. Process creation is not observed by this interface.");
                        o.Message = o.Result.Message; }, true);
                    return;
                }
                while (!HostOperations.Store.Read(item.OperationId).Terminal)
                {
                    var observation = await ObserveAsync().ConfigureAwait(false);
                    var running = observation.Mode == "run" || observation.Mode == "break";
                    var hasProcess = observation.Processes?.Count > 0;
                    HostOperations.Store.Update(item.OperationId, o =>
                    {
                        o.Observation = observation;
                        if (running && hasProcess)
                        {
                            o.State = observation.Mode == "run" ? "running" : "stopped";
                            o.EvidenceSource = observation.EvidenceSource;
                            o.Result = OpResult.Good("Launch observed " + o.State + ".");
                        }
                    }, running && hasProcess);
                    if (running && hasProcess) break;
                    await Task.Delay(500).ConfigureAwait(false);
                }
            });
            var finished = await HostOperations.Store.WaitAsync(item.OperationId, 30, ct).ConfigureAwait(false);
            return new OpResult { OperationId = item.OperationId, State = finished.State, Pending = !finished.Terminal,
                Ok = !finished.Terminal || finished.Result?.Ok == true,
                Message = finished.Terminal ? finished.Result?.Message ?? finished.Message :
                    "Launch " + item.OperationId + " is still pending. Query operation_status; do not retry blindly. " +
                    (finished.Observation == null ? "State observation unavailable." : "Last observed mode: " + finished.Observation.Mode + ".") };
        }

        void DescribeLaunch(string id, LaunchRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var solution = _dte.Solution.FullName;
            var startup = StartupProjectName();
            var configuration = ActiveConfiguration();
            HostOperations.Store.Update(id, o => { o.Solution = solution; o.StartupProject = startup;
                o.Configuration = configuration; o.Arguments = request.Args; o.State = "accepted"; });
            // Macro expansion belongs to the active native configuration. Unknown stays null.
            try
            {
                var project = Projects().FirstOrDefault(p => p.Name == StartupProjectName());
                if (project == null) return;
                dynamic native = project.Object;
                foreach (dynamic config in native.Configurations)
                {
                    if ((string)config.ConfigurationName != project.ConfigurationManager.ActiveConfiguration.ConfigurationName ||
                        (string)config.Platform.Name != project.ConfigurationManager.ActiveConfiguration.PlatformName) continue;
                    string command = config.Evaluate((string)config.DebugSettings.Command);
                    string directory = config.Evaluate((string)config.DebugSettings.WorkingDirectory);
                    string args = config.Evaluate((string)config.DebugSettings.CommandArguments);
                    HostOperations.Store.Update(id, o => { o.Executable = command; o.WorkingDirectory = directory; o.Arguments = args; });
                    break;
                }
            }
            catch (Exception) { /* Startup information depends on project type; do not guess. */ }
        }

        public async Task<BreakpointInfo> BreakpointSetAsync(BreakpointRequest request, CancellationToken ct = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var normalized = HostOperations.Copy(request);
            normalized.RequestId = null;
            var item = HostOperations.Store.Begin("breakpoint", request.RequestId, JsonConvert.SerializeObject(normalized), false, out var created);
            if (created) HostOperations.Run(item.OperationId, async () =>
            {
                HostOperations.Store.Update(item.OperationId, o => o.State = "installing");
                var info = await BreakpointCoreAsync(request, item.OperationId).ConfigureAwait(false);
                var early = HostOperations.Store.Read(item.OperationId).Breakpoint;
                if (info.Id == 0 && early?.Id > 0) info.Id = early.Id;
                info.OperationId = item.OperationId;
                HostOperations.Store.Update(item.OperationId, o => { o.Breakpoint = info;
                    o.State = info.Id == 0 ? "rejected" : info.Bound ? "bound" : "pending-symbols";
                    o.EvidenceSource = "Visual Studio breakpoint automation"; }, true);
            });
            var done = await HostOperations.Store.WaitAsync(item.OperationId, 15, ct).ConfigureAwait(false);
            if (done.Breakpoint != null && done.Terminal) return done.Breakpoint;
            return new BreakpointInfo { OperationId = item.OperationId, Id = done.Breakpoint?.Id ?? 0,
                Pending = !done.Terminal, Function = request.Function, Module = request.Module, File = request.File, Line = request.Line,
                BindState = done.Message ?? "Installation still pending inside VS. Query operation_status before retrying." };
        }
    }
}
