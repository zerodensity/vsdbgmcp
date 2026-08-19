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
    sealed class ProjectSystem : IProjectSystem
    {
        const int MaxDiagnostics = 25;

        readonly VsDbgMcpPackage _package;
        readonly DTE2 _dte;
        readonly IVsSolution _solution;
        readonly JoinableTaskFactory _jtf;
        readonly Action<string> _log;

        // Rooted in a field on purpose: an unrooted event sink is collected and the
        // events silently stop arriving, long after the code that set it up ran.
        BuildEvents _buildEvents;
        TaskCompletionSource<bool> _buildDone;

        public ProjectSystem(VsDbgMcpPackage package, DTE2 dte, IVsSolution solution, JoinableTaskFactory jtf, Action<string> log)
        {
            _package = package;
            _dte = dte;
            _solution = solution;
            _jtf = jtf;
            _log = log ?? (_ => { });
        }

        async Task<T> UIAsync<T>(Func<T> body, [CallerMemberName] string caller = null)
        {
            if (Activity.Paused) throw new InvalidOperationException(Activity.PausedMessage);

            await _jtf.SwitchToMainThreadAsync();
            MessageFilter.EnsureInstalled();

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
                Activity.Record(DebugHost.Name(caller), null, (int)started.ElapsedMilliseconds, failed);
            }
        }

        void EnsureBuildEvents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_buildEvents != null) return;

            _buildEvents = _dte.Events.BuildEvents;
            _buildEvents.OnBuildDone += (scope, action) =>
            {
                var waiter = _buildDone;
                _buildDone = null;
                waiter?.TrySetResult(true);
            };
        }

        public async Task<BuildResult> BuildAsync(string mode, string project, string configuration, string platform,
            CancellationToken ct = default)
        {
            var stopwatch = Stopwatch.StartNew();

            var started = await UIAsync(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                EnsureBuildEvents();

                if (!string.IsNullOrEmpty(configuration) || !string.IsNullOrEmpty(platform))
                {
                    var applied = ApplyConfiguration(configuration, platform);
                    if (!applied.Ok) return applied;
                }

                _buildDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    var build = _dte.Solution.SolutionBuild;
                    var target = FindProjectUniqueName(project);
                    if (project != null && target == null)
                        return OpResult.Bad("No project named '" + project + "'.");

                    var configName = build.ActiveConfiguration?.Name ?? "Debug";

                    switch (mode)
                    {
                        case "clean":
                            build.Clean(false);
                            break;
                        case "rebuild":
                            if (target != null) build.BuildProject(configName, target, false);
                            else { build.Clean(true); build.Build(false); }
                            break;
                        default:
                            if (target != null) build.BuildProject(configName, target, false);
                            else build.Build(false);
                            break;
                    }

                    return OpResult.Good(null);
                }
                catch (Exception ex)
                {
                    _buildDone = null;
                    return OpResult.Bad(ex.Message);
                }
            }).ConfigureAwait(false);

            if (!started.Ok)
                return new BuildResult { Succeeded = false, Message = started.Message, Diagnostics = new List<BuildDiagnostic>() };

            var waiter = _buildDone;
            var finished = true;
            if (waiter != null)
            {
                // VSTHRD003: this task is completed by Visual Studio's OnBuildDone event
                // rather than by work started here. That is deliberate - it is what makes
                // build() block to completion instead of returning a job to poll - and it
                // cannot deadlock because this runs on a background thread.
#pragma warning disable VSTHRD003
                var completed = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromMinutes(30), ct))
                    .ConfigureAwait(false);
#pragma warning restore VSTHRD003
                finished = completed == waiter.Task;
            }

            stopwatch.Stop();

            return await UIAsync(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                var result = Collect();
                result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;

                if (!finished)
                {
                    result.Message = "The build did not finish within 30 minutes. It may still be running; use build_cancel.";
                    result.Succeeded = false;
                }

                return result;
            }).ConfigureAwait(false);
        }

        BuildResult Collect()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new BuildResult { Diagnostics = new List<BuildDiagnostic>() };

            try
            {
                var errorList = _dte.ToolWindows.ErrorList;
                var items = errorList.ErrorItems;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (var i = 1; i <= items.Count; i++)
                {
                    var item = items.Item(i);

                    var severity = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh ? "error"
                        : item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium ? "warning"
                        : "message";

                    if (severity == "error") result.TotalErrors++;
                    else if (severity == "warning") result.TotalWarnings++;
                    else continue;

                    var key = severity + "|" + item.FileName + "|" + item.Line + "|" + item.Description;
                    if (!seen.Add(key)) continue;

                    if (result.Diagnostics.Count >= MaxDiagnostics) continue;

                    result.Diagnostics.Add(new BuildDiagnostic
                    {
                        Severity = severity,
                        Text = item.Description,
                        File = item.FileName,
                        Line = item.Line,
                        Column = item.Column,
                        Project = item.Project
                    });
                }

                // Errors first: the first real error is almost always the useful one.
                result.Diagnostics = result.Diagnostics
                    .OrderBy(d => d.Severity == "error" ? 0 : 1)
                    .ThenBy(d => d.File, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.Line)
                    .ToList();

                result.Succeeded = result.TotalErrors == 0 &&
                                   _dte.Solution.SolutionBuild.LastBuildInfo == 0;
            }
            catch (Exception ex)
            {
                result.Message = "Could not read the Error List: " + ex.Message;
            }

            return result;
        }

        public Task<OpResult> BuildCancelAsync(CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                _dte.ExecuteCommand("Build.Cancel");

                var waiter = _buildDone;
                _buildDone = null;
                waiter?.TrySetResult(false);

                return OpResult.Good("Cancel requested.");
            }
            catch (Exception ex)
            {
                return OpResult.Bad(ex.Message);
            }
        });

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
