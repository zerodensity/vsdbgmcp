using System.Collections.Generic;
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
        [Description("Read a retained build, launch or breakpoint operation, optionally waiting for its terminal result. Works without the VS UI thread. A wait timeout never cancels the operation. Reuse requestId on retries to avoid duplicates.")]
        public async Task<OperationInfo> Status(string operationId, int waitSeconds = 0, string instance = null, CancellationToken ct = default)
        {
            var link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
            return await link.Operations.OperationStatusAsync(operationId, waitSeconds, ct).ConfigureAwait(false);
        }

        [McpServerTool(Name = "operations", ReadOnly = true, UseStructuredContent = true)]
        [Description("List operations retained by this VS host, including requests still pending inside VS. Does not need the VS UI thread and does not issue or retry any command.")]
        public async Task<List<OperationInfo>> List(string instance = null, CancellationToken ct = default)
        {
            var link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
            return await link.Operations.OperationsAsync(ct).ConfigureAwait(false);
        }

        [McpServerTool(Name = "debug_state", ReadOnly = true, UseStructuredContent = true)]
        [Description("Bounded, read-only debugger/process/profile snapshot without watches, stack inspection or expression evaluation. A live process can outlive debugger detachment. Unknown fields do not establish absence.")]
        public async Task<StateObservation> Observe(string instance = null, CancellationToken ct = default)
        {
            var link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
            return await Snapshot(link, ct).ConfigureAwait(false);
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
