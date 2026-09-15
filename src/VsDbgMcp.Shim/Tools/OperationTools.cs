using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    [McpServerToolType]
    public sealed class OperationTools : ToolBase
    {
        public OperationTools(SessionManager sessions) : base(sessions) { }

        [McpServerTool(Name = "operation_status", ReadOnly = true, UseStructuredContent = true)]
        [Description("After build, launch or bp_set returns an operationId, call this to get its outcome and suggested next tool call. Set waitSeconds to wait for progress. Status remains available when VS is busy; a live check adds at most one second. Never retries or cancels the command. Unknown outcomes require inspecting effects before a new attempt.")]
        public async Task<OperationResponse> Status(
            [Description("ID returned by the original tool call.")] string operationId,
            [Description("Wait up to 60 seconds for the retained result; zero reads immediately, with at most one second for live evidence.")] int waitSeconds = 0,
            string instance = null, CancellationToken ct = default,
            [Description("Include internal dispatch state, timestamps and evidence. Usually unnecessary; the default gives the outcome and next action.")] bool details = false)
        {
            var link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
            var operation = await link.Operations.OperationStatusAsync(operationId, waitSeconds, ct).ConfigureAwait(false);
            if (operation != null && operation.Terminal) Sessions.Log.OperationReported(operation.OperationId);
            return OperationResponse.From(operation, link.Id, details);
        }

        [McpServerTool(Name = "operations", ReadOnly = true, UseStructuredContent = true)]
        [Description("Find an operationId after losing the original reply. Lists compact retained outcomes and next actions. Use operation_status for fresh evidence or to wait. Never starts or retries a command.")]
        public async Task<List<OperationResponse>> List(string instance = null, CancellationToken ct = default)
        {
            var link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
            var operations = await link.Operations.OperationsAsync(ct).ConfigureAwait(false);
            foreach (var operation in operations.Where(o => o.Terminal))
                Sessions.Log.OperationReported(operation.OperationId);
            return operations.Select(o => { var response = OperationResponse.From(o, link.Id); response.Result = null; return response; }).ToList();
        }

        [McpServerTool(Name = "debug_state", ReadOnly = true, UseStructuredContent = true)]
        [Description("Bounded, read-only debugger/process/profile snapshot without watches, stack inspection or expression evaluation. A live process can outlive debugger detachment. Unknown fields do not establish absence.")]
        public async Task<DebugStateResponse> Observe(string instance = null, CancellationToken ct = default)
        {
            var link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
            return DebugStateResponse.From(await Snapshot(link, ct).ConfigureAwait(false));
        }

        internal static async Task<StateObservation> Snapshot(HostLink link, CancellationToken ct)
        {
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                bounded.CancelAfter(1000);
                try
                {
                    return await link.Operations.ObserveAsync(bounded.Token).WaitAsync(bounded.Token).ConfigureAwait(false);
                }
                catch (System.Exception ex) when (!ct.IsCancellationRequested)
                {
                    return new StateObservation { Mode = "unknown", TimestampUtc = System.DateTime.UtcNow,
                        Error = link.IsConnected ? "State query unavailable: " + ex.Message : "Disconnected", EvidenceSource = "query unavailable" };
                }
            }
        }

        [McpServerTool(Name = "build_log", ReadOnly = true, UseStructuredContent = true)]
        [Description("Read an invocation's retained raw log incrementally. Offset/nextOffset count UTF-16 characters. Use the returned nextOffset for the next read; the full log path is included for export. Older build logs survive later builds.")]
        public async Task<BuildLog> Log(string operationId, long offset = 0, int maxChars = 20000, string instance = null, CancellationToken ct = default)
        {
            var link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
            return await link.Project.BuildLogAsync(operationId, offset, maxChars, ct).ConfigureAwait(false);
        }
    }
}
