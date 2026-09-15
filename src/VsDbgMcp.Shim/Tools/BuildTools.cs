using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    [McpServerToolType]
    public sealed class BuildTools : ToolBase
    {
        public BuildTools(SessionManager sessions) : base(sessions) { }

        [McpServerTool(Name = "build_diagnostics", ReadOnly = true, UseStructuredContent = true)]
        [Description("Use an existing MSBuild .binlog when build output is incomplete or structured compiler diagnostics are needed. Reads offline without running a build. Returns outcome, warning/error events, ownership and coverage. Filters limit rows; counts always cover the whole file. A binlog is not recorded automatically by build.")]
        public Task<VsDbgMcp.Shim.Builds.BinaryBuildReport> Diagnostics(
            [Description("Absolute path to an existing MSBuild .binlog file.")] string binlog,
            [Description("Case-insensitive substring of project path; filters returned rows only.")] string project = null,
            [Description("Case-insensitive substring of configuration, such as Debug|x64; filters returned rows only.")] string configuration = null,
            [Description("Maximum diagnostic rows, 1 to 1000. Defaults to 25; truncated indicates more matching rows.")] int maxDiagnostics = 25,
            CancellationToken ct = default) =>
            Task.Run(() => VsDbgMcp.Shim.Builds.BinaryBuildDiagnostics.Read(binlog, project, configuration, maxDiagnostics, ct), ct);

        [McpServerTool(Name = "build")]
        [Description("Build, rebuild, or clean with a recoverable operation ID. Returns the errors themselves - deduplicated, with file and line, worst first - not the raw build log. Waits up to waitSeconds, then returns an operationId for operation_status. A wait timeout does not cancel the build.")]
        public Task<string> Build(
            [Description("build, rebuild, or clean.")] string mode = "build",
            [Description("Project to build. Omit to build the whole solution.")] string project = null,
            [Description("Configuration such as Debug or Release. Omit to keep the current one.")] string configuration = null,
            [Description("Platform such as x64. Omit to keep the current one.")] string platform = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default,
            [Description("Stable caller request ID. Reuse with identical arguments after a transport timeout.")] string requestId = null,
            [Description("Maximum seconds to wait, 0 to 60. Zero starts and returns immediately.")] int waitSeconds = 30)
            => On(instance, ct, async link =>
            {
                var normalized = (mode ?? "build").Trim().ToLowerInvariant();
                if (normalized != "build" && normalized != "rebuild" && normalized != "clean")
                    return "mode must be build, rebuild, or clean.";

                var operation = await link.Project.BuildBeginAsync(new VsDbgMcp.Contracts.BuildRequest {
                    RequestId = requestId, Mode = normalized, Project = project, Configuration = configuration,
                    Platform = platform, WaitSeconds = waitSeconds }, ct).ConfigureAwait(false);
                if (operation.Terminal) Sessions.Log.OperationReported(operation.OperationId);
                if (operation.Build != null) return Render.Build(operation.Build);
                return "Build " + operation.OperationId + ": " + operation.State + ". " + operation.Message +
                    (operation.Terminal ? "" : " Query operation_status to wait or recover its result.");
            }, project == null ? mode : mode + " " + project);

        [McpServerTool(Name = "build_cancel")]
        [Description("Cancel a build that is in progress. Use this when a build is taking far longer than it should rather than waiting out the timeout.")]
        public Task<string> BuildCancel(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default,
            [Description("Build operation ID. Omit for the latest build owned by this host.")] string operationId = null)
            => On(instance, ct, async link =>
            {
                var result = await link.Project.BuildCancelOperationAsync(operationId, ct).ConfigureAwait(false);
                return Render.Op(result, "Cancel request returned.");
            });

        [McpServerTool(Name = "build_output", ReadOnly = true)]
        [Description("The raw build log, for the times the structured errors are not enough - custom build steps, linker command lines, toolchain diagnostics. Filter with a regular expression.")]
        public Task<string> BuildOutput(
            [Description("Regular expression; only matching lines are returned.")] string pattern = null,
            [Description("Return only the last N matching lines.")] int tail = 200,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Project.BuildOutputAsync(pattern, Math.Max(1, tail), ct).ConfigureAwait(false);
                if (result == null || string.IsNullOrWhiteSpace(result.Text)) return "No build output.";
                return result.Text;
            });

        [McpServerTool(Name = "config")]
        [Description("Get or set the active solution configuration and platform, for example 'Debug|x64'. This changes which binaries launch builds and debugs, so check it when the debugger seems to be running something other than what you built.")]
        public Task<string> Config(
            [Description("New value such as 'Debug|x64'. Omit to read the current one.")] string set = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link => await link.Project.ConfigurationAsync(set, ct).ConfigureAwait(false), set);

        [McpServerTool(Name = "startup_project")]
        [Description("Get or set the project that launch starts. Call with no argument to see the current one along with the projects available.")]
        public Task<string> StartupProject(
            [Description("Project name to make the startup project. Omit to read the current one.")] string set = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var current = await link.Project.StartupProjectAsync(set, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(set)) return current;

                var projects = await link.Project.ProjectsAsync(ct).ConfigureAwait(false);
                var list = projects == null || projects.Count == 0
                    ? ""
                    : "\navailable: " + string.Join(", ", projects);
                return "startup: " + (current ?? "(none)") + list;
            });
    }
}
