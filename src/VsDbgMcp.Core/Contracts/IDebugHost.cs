using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VsDbgMcp.Contracts
{
    /// <summary>
    /// Everything about debugging, implemented inside Visual Studio and called over
    /// the pipe. Project-system agnostic: the debugger does not care how the process
    /// being debugged was launched.
    /// </summary>
    public interface IDebugHost
    {
        Task<string> HandshakeAsync(int shimContractVersion, string token);

        /// <summary>
        /// Tells the extension what an agent just did, so the panel can show it. One way:
        /// the caller does not wait for this and a failure here must never affect a tool.
        /// </summary>
        Task ReportCallAsync(CallReport report);

        Task<HostStatus> GetStatusAsync(CancellationToken ct = default);

        // Session
        Task<OpResult> LaunchAsync(LaunchRequest request, CancellationToken ct = default);
        Task<OpResult> AttachAsync(AttachRequest request, CancellationToken ct = default);
        Task<OpResult> DetachAsync(int? pid, CancellationToken ct = default);
        Task<OpResult> StopAsync(int? pid, CancellationToken ct = default);
        Task<OpResult> RestartAsync(CancellationToken ct = default);
        Task<List<ProcessInfo>> ProcessesAsync(bool includeLocal, CancellationToken ct = default);
        Task<OpResult> OpenDumpAsync(string path, CancellationToken ct = default);

        // Execution
        Task<OpResult> GoAsync(CancellationToken ct = default);
        Task<OpResult> PauseAsync(CancellationToken ct = default);
        Task<OpResult> StepAsync(string kind, int count, CancellationToken ct = default);
        Task<OpResult> RunToAsync(string file, int line, CancellationToken ct = default);
        Task<OpResult> SetNextAsync(string file, int line, CancellationToken ct = default);

        // Breakpoints
        Task<BreakpointInfo> BreakpointSetAsync(BreakpointRequest request, CancellationToken ct = default);
        Task<List<BreakpointInfo>> BreakpointListAsync(CancellationToken ct = default);
        Task<OpResult> BreakpointRemoveAsync(int id, CancellationToken ct = default);
        Task<OpResult> BreakpointEnableAsync(int id, bool enabled, CancellationToken ct = default);
        Task<TraceResult> TraceReadAsync(int id, int tail, CancellationToken ct = default);
        Task<OpResult> ExceptionSetAsync(ExceptionSetting setting, CancellationToken ct = default);
        Task<List<ExceptionSetting>> ExceptionListAsync(CancellationToken ct = default);

        // Inspection
        Task<List<ThreadSummary>> ThreadsAsync(int frameDepth, string process, CancellationToken ct = default);
        Task<List<Frame>> StackAsync(int? threadId, int count, CancellationToken ct = default);
        Task<OpResult> SelectAsync(int? threadId, int? frameIndex, string process, CancellationToken ct = default);
        Task<OpResult> FreezeAsync(int threadId, bool frozen, CancellationToken ct = default);
        Task<List<EvalResult>> EvalAsync(EvalOptions options, CancellationToken ct = default);
        Task<VarsResult> VarsAsync(string scope, int depth, string filter, bool sharedAddresses, CancellationToken ct = default);

        Task<FrameReport> FrameAsync(int? thread, int? frame, int maxVariables, CancellationToken ct = default);
        Task<VarsResult> ExpandAsync(string reference, int depth, string typeModule, int? index, string key, CancellationToken ct = default);
        Task<OpResult> WatchSetAsync(string[] expressions, CancellationToken ct = default);
        Task<MemoryResult> MemoryAsync(string addressOrExpression, int size, string format, CancellationToken ct = default);
        Task<RegistersResult> RegistersAsync(string group, CancellationToken ct = default);
        Task<DisasmResult> DisasmAsync(string address, int count, CancellationToken ct = default);
        Task<ModulesResult> ModulesAsync(string filter, CancellationToken ct = default);
        Task<SymbolResult> SymbolsAsync(string module, bool load, CancellationToken ct = default);
        Task<ScratchResult> ScratchAsync(int bytes, string type, CancellationToken ct = default);
        Task<ScratchResult> ScratchFreeAsync(string address, CancellationToken ct = default);

        // Profiling: the extension collects, the shim reads what it collected.
        Task<OpResult> ProfileStartAsync(CancellationToken ct = default);
        Task<ProfileCollection> ProfileStopAsync(CancellationToken ct = default);

        // Evidence
        Task<string> TriageAsync(CancellationToken ct = default);
        Task<CaptureResult> CaptureAsync(int[] region, CancellationToken ct = default);

        // Debuggee input and output
        Task<ConsoleResult> ConsoleReadAsync(int tailLines, CancellationToken ct = default);
        Task<OpResult> ConsoleSendAsync(string text, string keys, CancellationToken ct = default);
        Task<OutputResult> OutputReadAsync(string pane, string pattern, int tailLines, CancellationToken ct = default);
    }

    /// <summary>
    /// Build and project selection. The only interface a non-solution workspace
    /// would need a second implementation of.
    /// </summary>
    public interface IProjectSystem
    {
        Task<OperationInfo> BuildBeginAsync(BuildRequest request, CancellationToken ct = default);
        Task<OpResult> BuildCancelOperationAsync(string id, CancellationToken ct = default);
        Task<BuildLog> BuildLogAsync(string id, long offset, int maxChars, CancellationToken ct = default);
        Task<BuildResult> BuildAsync(string mode, string project, string configuration, string platform, CancellationToken ct = default);
        Task<OpResult> BuildCancelAsync(CancellationToken ct = default);
        Task<OutputResult> BuildOutputAsync(string pattern, int tailLines, CancellationToken ct = default);
        Task<string> ConfigurationAsync(string set, CancellationToken ct = default);
        Task<string> StartupProjectAsync(string set, CancellationToken ct = default);
        Task<List<string>> ProjectsAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Implemented by the shim, called by Visual Studio. Events are pushed rather than
    /// polled so a waiter cannot miss one that happened between two calls.
    /// </summary>
    public interface IShimEvents
    {
        Task OnStopAsync(StopEvent stop);
        Task OnModuleLoadAsync(ModuleLoadEvent module);
        Task OnOutputAsync(OutputEvent output);
        Task OnModeChangedAsync(string instanceId, string mode);
        Task OnWorkspaceChangedAsync(string instanceId);
    }
}
