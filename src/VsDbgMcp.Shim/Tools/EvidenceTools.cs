using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    [McpServerToolType]
    public sealed class EvidenceTools : ToolBase
    {
        public EvidenceTools(SessionManager sessions) : base(sessions) { }

        [McpServerTool(Name = "triage", ReadOnly = true)]
        [Description("Everything worth knowing about a crash, in one call: the exception record, the faulting thread's stack, the registers that matter, the memory around the fault address, and which modules had symbols. Call this first when the debuggee stops on an access violation or an assertion, instead of assembling the same picture from six separate calls.")]
        public Task<string> Triage(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link => await link.Debug.TriageAsync(ct).ConfigureAwait(false));

        [McpServerTool(Name = "capture", ReadOnly = true)]
        [Description("Screenshot the window of the program being debugged. Works while it is stopped at a breakpoint and while the window is behind others, so it answers what was on screen when this went wrong. Returns a PNG.")]
        public Task<string> Capture(
            [Description("Region as x,y,width,height. Omit to capture the whole window.")] string region = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                int[] box = null;
                if (!string.IsNullOrWhiteSpace(region))
                {
                    var parts = region.Split(',');
                    if (parts.Length != 4) return "region must be x,y,width,height.";
                    box = new int[4];
                    for (var i = 0; i < 4; i++)
                    {
                        if (!int.TryParse(parts[i].Trim(), out box[i])) return "region must be four numbers.";
                    }
                }

                var result = await link.Debug.CaptureAsync(box, ct).ConfigureAwait(false);
                if (result == null) return "No capture.";
                if (!string.IsNullOrEmpty(result.Error)) return "Could not capture: " + result.Error;

                return "Captured " + result.Width + "x" + result.Height + " PNG (base64, " +
                       (result.Base64?.Length ?? 0) + " chars):\n" + result.Base64;
            });
    }
}
