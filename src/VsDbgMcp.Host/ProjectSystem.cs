using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using VsDbgMcp.Contracts;
using Task = System.Threading.Tasks.Task;

namespace VsDbgMcp.Host
{
    /// <summary>Projects in the open solution, flattened through solution folders.</summary>
    static class SolutionProjects
    {
        public static List<Project> All(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var found = new List<Project>();
            foreach (Project project in dte.Solution.Projects) Flatten(project, found);
            return found;
        }

        static void Flatten(Project project, List<Project> into)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null) return;

            if (project.Kind == ProjectKinds.vsProjectKindSolutionFolder)
            {
                foreach (ProjectItem item in project.ProjectItems)
                {
                    if (item.SubProject != null) Flatten(item.SubProject, into);
                }
                return;
            }

            into.Add(project);
        }
    }

    /// <summary>
    /// Build and project selection. Build exists here to serve the debug loop, so it
    /// blocks to completion and answers with the errors rather than with a log.
    /// </summary>
    sealed class ProjectSystem : IProjectSystem, IVsUpdateSolutionEvents
    {
        const int MaxDiagnostics = 25;

        readonly VsDbgMcpPackage _package;
        readonly DTE2 _dte;
        readonly IVsSolution _solution;
        readonly JoinableTaskFactory _jtf;
        readonly Action<string> _log;

        readonly IVsSolutionBuildManager2 _buildManager;
        uint _buildCookie;
        string _activeBuild;
        string _outputSeen = "";
        string _buildLog = "";
        bool _observedBusy;
        bool _cancelObserved;
        BuildEvents _buildEvents;

        public ProjectSystem(VsDbgMcpPackage package, DTE2 dte, IVsSolution solution, JoinableTaskFactory jtf, Action<string> log, IVsSolutionBuildManager2 buildManager)
        {
            _package = package;
            _buildManager = buildManager;
            _dte = dte;
            _solution = solution;
            _jtf = jtf;
            _log = log ?? (_ => { });
        }

        async Task<T> UIAsync<T>(Func<T> body)
        {
            if (Activity.Paused) throw new InvalidOperationException(Activity.PausedMessage);

            await _jtf.SwitchToMainThreadAsync();
            MessageFilter.EnsureInstalled();

            // Not recorded here, for the same reason as the debug host: the shim reports
            // the call once it has the reply. Recording here too listed every build tool
            // twice, once with its output and once without.
            return body();
        }

        public async Task<OperationInfo> BuildBeginAsync(BuildRequest request, CancellationToken ct = default)
        {
            request = request ?? new BuildRequest();
            if (request.Mode != "build" && request.Mode != "clean" && request.Mode != "rebuild")
                throw new ArgumentException("mode must be build, rebuild, or clean.");
            var fingerprint = Newtonsoft.Json.JsonConvert.SerializeObject(new { request.Mode, request.Project, request.Configuration, request.Platform });
            var operation = HostOperations.Store.Begin("build", request.RequestId, fingerprint, true, out var created);
            if (created) HostOperations.Run(operation.OperationId, () => RunBuildAsync(operation.OperationId, request));
            return await HostOperations.Store.WaitAsync(operation.OperationId, request.WaitSeconds, ct).ConfigureAwait(false);
        }

        public async Task<BuildResult> BuildAsync(string mode, string project, string configuration, string platform, CancellationToken ct = default)
        {
            var operation = await BuildBeginAsync(new BuildRequest { Mode = mode, Project = project,
                Configuration = configuration, Platform = platform }, ct).ConfigureAwait(false);
            return operation.Build ?? new BuildResult { OperationId = operation.OperationId, State = operation.State,
                Message = operation.Message ?? "Build is pending. Query operation_status with its operationId." };
        }

        async Task RunBuildAsync(string id, BuildRequest request)
        {
            await UIAsync(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (HostOperations.Store.Read(id).CancelRequested)
                {
                    HostOperations.Store.Update(id, o => { o.State = "cancelled"; o.Message = "Cancelled before dispatch; no VS build started."; }, true);
                    return false;
                }
                if (_buildManager == null) throw new InvalidOperationException("Visual Studio build manager unavailable.");
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(_buildManager.QueryBuildManagerBusy(out var busy));
                if (busy != 0) throw new InvalidOperationException("Visual Studio is already building; no second build was started.");
                if (!_dte.Solution.IsOpen) throw new InvalidOperationException("No solution is open.");
                var target = FindProjectUniqueName(request.Project);
                if (request.Project != null && target == null) throw new InvalidOperationException("No project named '" + request.Project + "'.");
                IVsHierarchy hierarchy = null;
                if (target != null) Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(_solution.GetProjectOfUniqueName(target, out hierarchy));
                var applied = string.IsNullOrEmpty(request.Configuration) && string.IsNullOrEmpty(request.Platform)
                    ? OpResult.Good() : ApplyConfiguration(request.Configuration, request.Platform);
                if (!applied.Ok) throw new InvalidOperationException(applied.Message);
                if (_buildCookie == 0)
                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(_buildManager.AdviseUpdateSolutionEvents(this, out _buildCookie));
                if (_buildEvents == null)
                {
                    _buildEvents = _dte.Events.BuildEvents;
                    _buildEvents.OnBuildProjConfigDone += (project, config, platform, solutionConfig, success) =>
                    {
                        if (_activeBuild == null) return;
                        HostOperations.Store.Update(_activeBuild, o => { o.LastProgress = project; o.LastProgressUtc = DateTime.UtcNow; });
                    };
                }
                _outputSeen = ReadBuildPane();
                _buildLog = "";
                _observedBusy = false;
                _cancelObserved = false;
                var configName = _dte.Solution.SolutionBuild.ActiveConfiguration?.Name;
                var solutionName = _dte.Solution.FullName;
                var configuration = configName + "|" + (_dte.Solution.SolutionBuild.ActiveConfiguration as SolutionConfiguration2)?.PlatformName;
                _activeBuild = id;
                HostOperations.Store.Update(id, o => { o.State = "accepted"; o.Solution = solutionName;
                    o.Configuration = configuration;
                    o.LogPath = System.IO.Path.Combine(HostOperations.DirectoryPath, id + ".log"); });
                var flags = request.Mode == "clean" ? VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_CLEAN : VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_BUILD;
                if (request.Mode == "rebuild") flags |= VSSOLNBUILDUPDATEFLAGS.SBF_OPERATION_FORCE_UPDATE;
                int hr;
                if (target == null) hr = _buildManager.StartSimpleUpdateSolutionConfiguration((uint)flags, 0, 0);
                else
                {
                    hr = _buildManager.StartUpdateProjectConfigurations(1, new[] { hierarchy }, (uint)flags, 0);
                }
                if (Microsoft.VisualStudio.ErrorHandler.Failed(hr))
                {
                    _activeBuild = null;
                    HostOperations.Store.Update(id, o => { o.State = "failed"; o.Message = "Visual Studio rejected build dispatch (HRESULT 0x" + hr.ToString("X8") + ").";
                        o.EvidenceSource = "build manager dispatch HRESULT"; }, true);
                    return false;
                }
                return true;
            }).ConfigureAwait(false);

            // Reconcile missed events internally. The caller can reconnect or stop waiting.
            while (!HostOperations.Store.Read(id).Terminal)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                await UIAsync(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (_activeBuild != id) return false;
                    var record = HostOperations.Store.Read(id);
                    if (!_dte.Solution.IsOpen || _dte.Solution.FullName != record.Solution)
                    {
                        FinishBuild(false, false, "solution changed; outcome unknown", "unknown");
                        return false;
                    }
                    CaptureBuildOutput();
                    Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(_buildManager.QueryBuildManagerBusy(out var busy));
                    if (busy != 0) _observedBusy = true;
                    else if (_observedBusy)
                        ReconcileIdleBuild("build manager idle; completion event unavailable");
                    return true;
                }).ConfigureAwait(false);
            }
        }

        string ReadBuildPane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (OutputWindowPane pane in _dte.ToolWindows.OutputWindow.OutputWindowPanes)
            {
                if (pane.Name.IndexOf("Build", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var start = pane.TextDocument.StartPoint.CreateEditPoint();
                return start.GetText(pane.TextDocument.EndPoint) ?? "";
            }
            return "";
        }

        void ReconcileIdleBuild(string evidence)
        {
            // Zero failed projects does not prove completion: a cancelled build can
            // also report zero. Only an observed cancellation narrows this outcome.
            FinishBuild(false, _cancelObserved, evidence, _cancelObserved ? "cancelled" : "unknown");
        }

        void CaptureBuildOutput()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_activeBuild == null) return;
            try
            {
                var current = ReadBuildPane();
                var added = current.StartsWith(_outputSeen, StringComparison.Ordinal) ? current.Substring(_outputSeen.Length) : current;
                _outputSeen = current;
                if (added.Length == 0) return;
                _buildLog += added;
                var operation = HostOperations.Store.Update(_activeBuild, o => o.LastProgressUtc = DateTime.UtcNow);
                System.IO.Directory.CreateDirectory(HostOperations.DirectoryPath);
                System.IO.File.AppendAllText(operation.LogPath, added);
            }
            catch (Exception ex) { HostOperations.Store.Update(_activeBuild, o => o.Message = "Build output incomplete: " + ex.Message); }
        }

        void FinishBuild(bool succeeded, bool cancelled, string source, string state = null)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var id = _activeBuild;
            if (id == null) return;
            CaptureBuildOutput();
            var operation = HostOperations.Store.Read(id);
            var diagnostics = BuildDiagnostics.Parse(_buildLog, id, operation.Configuration, DateTime.UtcNow);
            var result = new BuildResult { OperationId = id, State = state ?? (cancelled ? "cancelled" : succeeded ? "succeeded" : "failed"),
                Succeeded = succeeded && !cancelled && state == null, Cancelled = cancelled,
                ElapsedSeconds = (DateTime.UtcNow - operation.StartedUtc).TotalSeconds,
                TotalErrors = diagnostics.Count(d => d.Severity == "error"), TotalWarnings = diagnostics.Count(d => d.Severity == "warning"),
                UniqueWarnings = BuildDiagnostics.Unique(diagnostics, "warning"),
                Diagnostics = diagnostics.GroupBy(BuildDiagnostics.Key).Select(g => g.First()).Take(100).ToList(),
                EvidenceSource = source, LogPath = operation.LogPath,
                Message = operation.Message };
            HostOperations.Store.Update(id, o => { o.State = result.State; o.Build = result; o.EvidenceSource = source; }, true);
            _activeBuild = null;
        }

        public int UpdateSolution_Begin(ref int cancel)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_activeBuild != null) { _observedBusy = true; HostOperations.Store.Update(_activeBuild, o => o.State = "running"); }
            return 0;
        }
        public int UpdateSolution_StartUpdate(ref int cancel) => 0;
        public int UpdateSolution_Cancel() { _cancelObserved = true; return 0; }
        public int OnActiveProjectCfgChange(IVsHierarchy hierarchy) => 0;
        public int UpdateSolution_Done(int succeeded, int modified, int cancelled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            FinishBuild(succeeded != 0, cancelled != 0, "Visual Studio UpdateSolution_Done");
            return 0;
        }

        public Task<OpResult> BuildCancelAsync(CancellationToken ct = default) => BuildCancelOperationAsync(null, ct);
        public async Task<OpResult> BuildCancelOperationAsync(string id, CancellationToken ct = default)
        {
            var operation = id == null ? HostOperations.Store.All().FirstOrDefault(o => o.Kind == "build") : HostOperations.Store.Read(id);
            if (operation == null || operation.Kind != "build") return OpResult.Bad("No matching build operation.");
            if (operation.Terminal) return OpResult.Good("Build " + operation.OperationId + " already " + operation.State + "; no cancellation performed.");
            HostOperations.Store.Update(operation.OperationId, o => { o.CancelRequested = true; o.Message = "Cancellation queued; acceptance not yet observed."; });
            var cancel = UIAsync(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var latest = HostOperations.Store.Read(operation.OperationId);
                if (latest.Terminal) return OpResult.Good("Already " + latest.State + "; no cancellation performed.");
                if (_activeBuild != operation.OperationId) return OpResult.Bad("Build has not started; cancellation was not issued. Query operation_status.");
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(_buildManager.QueryBuildManagerBusy(out var busy));
                if (busy == 0)
                {
                    if (_observedBusy) ReconcileIdleBuild("build manager idle before cancel; completion event unavailable");
                    return OpResult.Good("No active VS build to cancel; query operation_status for the retained result.");
                }
                HostOperations.Store.Update(operation.OperationId, o => { o.CancelRequested = true; o.State = "cancelling"; });
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(_buildManager.CancelUpdateSolutionConfiguration());
                return OpResult.Good("Cancellation requested for " + operation.OperationId + "; completion is not yet confirmed.");
            });
            if (await Task.WhenAny(cancel, Task.Delay(3000, ct)).ConfigureAwait(false) == cancel) return await cancel.ConfigureAwait(false);
            return OpResult.Good("Cancellation request queued inside VS for " + operation.OperationId + "; query operation_status.");
        }

        public Task<BuildLog> BuildLogAsync(string id, long offset, int maxChars, CancellationToken ct = default)
        {
            var operation = id == null ? HostOperations.Store.All().FirstOrDefault(o => o.Kind == "build") : HostOperations.Store.Read(id) ?? HostOperations.Historical(id);
            if (operation == null || operation.Kind != "build") throw new ArgumentException("No matching build operation.");
            var text = operation.LogPath != null && System.IO.File.Exists(operation.LogPath) ? System.IO.File.ReadAllText(operation.LogPath) : "";
            if (offset < 0 || offset > text.Length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset is outside this build log.");
            var count = Math.Min(text.Length - (int)offset, Math.Max(1, Math.Min(maxChars, 1000000)));
            return Task.FromResult(new BuildLog { OperationId = operation.OperationId, Offset = offset,
                NextOffset = offset + count, HasMore = offset + count < text.Length, Text = text.Substring((int)offset, count), Path = operation.LogPath });
        }

        public Task<OutputResult> BuildOutputAsync(string pattern, int tailLines, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new OutputResult { Pane = "Build" };
            try
            {
                OutputWindowPane pane = null;
                foreach (OutputWindowPane candidate in _dte.ToolWindows.OutputWindow.OutputWindowPanes)
                {
                    if (candidate.Name.IndexOf("Build", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    pane = candidate;
                    break;
                }

                if (pane == null)
                {
                    result.Text = "No build output yet.";
                    return result;
                }

                var selection = pane.TextDocument.Selection;
                selection.StartOfDocument(false);
                selection.EndOfDocument(true);
                var lines = (selection.Text ?? "").Replace("\r\n", "\n").Split('\n').ToList();
                selection.StartOfDocument(false);

                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    Regex regex;
                    try { regex = new Regex(pattern, RegexOptions.IgnoreCase); }
                    catch (Exception ex) { result.Text = "Bad pattern: " + ex.Message; return result; }
                    lines = lines.Where(l => regex.IsMatch(l)).ToList();
                }

                if (tailLines > 0 && lines.Count > tailLines)
                {
                    lines = lines.Skip(lines.Count - tailLines).ToList();
                    result.Truncated = true;
                }

                result.Lines = lines.Count;
                result.Text = string.Join("\n", lines).Trim();
            }
            catch (Exception ex)
            {
                result.Text = "Could not read the build output: " + ex.Message;
            }

            return result;
        });

        public Task<string> ConfigurationAsync(string set, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!string.IsNullOrWhiteSpace(set))
            {
                var parts = set.Split('|');
                var applied = ApplyConfiguration(parts[0], parts.Length > 1 ? parts[1] : null);
                if (!applied.Ok) return "Failed: " + applied.Message;
            }

            try
            {
                var active = _dte.Solution.SolutionBuild.ActiveConfiguration;
                if (active == null) return "(no active configuration)";

                var platform = active.SolutionContexts.Count > 0
                    ? active.SolutionContexts.Item(1).PlatformName
                    : null;

                var available = _dte.Solution.SolutionBuild.SolutionConfigurations
                    .Cast<SolutionConfiguration>()
                    .Select(c => c.Name)
                    .Distinct()
                    .ToList();

                return (string.IsNullOrEmpty(platform) ? active.Name : active.Name + "|" + platform) +
                       "\navailable: " + string.Join(", ", available);
            }
            catch (Exception ex)
            {
                return "Could not read the configuration: " + ex.Message;
            }
        });

        OpResult ApplyConfiguration(string configuration, string platform)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                foreach (SolutionConfiguration2 candidate in _dte.Solution.SolutionBuild.SolutionConfigurations)
                {
                    if (!string.IsNullOrEmpty(configuration) &&
                        !string.Equals(candidate.Name, configuration, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(platform) &&
                        !string.Equals(candidate.PlatformName, platform, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    candidate.Activate();
                    return OpResult.Good(null);
                }

                return OpResult.Bad("No configuration matching '" + configuration + "|" + platform + "'.");
            }
            catch (Exception ex)
            {
                return OpResult.Bad(ex.Message);
            }
        }

        public Task<string> StartupProjectAsync(string set, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!string.IsNullOrWhiteSpace(set))
            {
                var wanted = FindProjectUniqueName(set);
                if (wanted == null) return "No project named '" + set + "'.";

                try
                {
                    _dte.Solution.SolutionBuild.StartupProjects = wanted;
                    return "Startup project is now " + set + ".";
                }
                catch (Exception ex)
                {
                    return "Could not set the startup project: " + ex.Message;
                }
            }

            if (!(_dte.Solution.SolutionBuild.StartupProjects is Array projects) || projects.Length == 0)
                return null;

            var unique = projects.GetValue(0)?.ToString();
            var match = SolutionProjects.All(_dte)
                .FirstOrDefault(p => string.Equals(p.UniqueName, unique, StringComparison.OrdinalIgnoreCase));

            return match?.Name ?? unique;
        });

        public Task<List<string>> ProjectsAsync(CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return SolutionProjects.All(_dte).Select(p => p.Name).ToList();
        });

        string FindProjectUniqueName(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(name)) return null;

            return SolutionProjects.All(_dte)
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.UniqueName;
        }
    }
}
