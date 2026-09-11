using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
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
        [Description("Screenshot the window of the program being debugged. Works while it is stopped at a breakpoint and while the window is behind others, so it answers what was on screen when this went wrong. Returns native MCP PNG image content and a short dimensions summary. Requires a visible top-level window.")]
        public async Task<CallToolResult> Capture(
            [Description("Region as x,y,width,height in pixels relative to the window's top-left corner. Omit to capture the whole window.")] string region = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
        {
            ImageContentBlock image = null;
            string summary;
            try
            {
                summary = await On(instance, ct, async link =>
                {
                    int[] box = null;
                    if (!string.IsNullOrWhiteSpace(region))
                    {
                        var parts = region.Split(',');
                        if (parts.Length != 4) return Reply.Bad("region must be x,y,width,height.");
                        box = new int[4];
                        for (var i = 0; i < 4; i++)
                        {
                            if (!int.TryParse(parts[i].Trim(), out box[i])) return Reply.Bad("region must be four numbers.");
                        }
                    }

                    var result = await link.Debug.CaptureAsync(box, ct).ConfigureAwait(false);
                    if (result == null) return Reply.Bad("No capture.");
                    if (!string.IsNullOrEmpty(result.Error)) return Reply.Bad("Could not capture: " + result.Error);
                    if (string.IsNullOrWhiteSpace(result.Base64) || result.Width <= 0 || result.Height <= 0)
                        return Reply.Bad("Could not capture: the host returned no usable image.");

                    image = new ImageContentBlock { MimeType = "image/png", Data = Encoding.UTF8.GetBytes(result.Base64) };
                    return "Captured " + result.Width + "x" + result.Height + " PNG; " +
                        (box == null ? "whole window." : "requested region: " + string.Join(",", box) + " (window-relative pixels).");
                }, detail: region).ConfigureAwait(false);
            }
            catch (ToolFailure failure)
            {
                return new CallToolResult { IsError = true, Content = { new TextContentBlock { Text = failure.Message } } };
            }

            var response = new CallToolResult
            {
                IsError = image == null,
                Content = { new TextContentBlock { Text = summary } }
            };
            if (image != null) response.Content.Add(image);
            return response;
        }
    }
}
