using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    [McpServerToolType]
    public sealed class IoTools : ToolBase
    {
        public IoTools(SessionManager sessions) : base(sessions) { }

        [McpServerTool(Name = "console_read", ReadOnly = true)]
        [Description("Read what the program being debugged has printed to its console. This is the debuggee's own stdout, not the Visual Studio output window, and it is the only way to see what a console program is doing. Works while it is stopped at a breakpoint.")]
        public Task<string> ConsoleRead(
            [Description("Return only the last N lines. 0 returns the whole buffer.")] int tail = 60,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.ConsoleReadAsync(tail, ct).ConfigureAwait(false);
                if (result == null) return "No console.";
                if (!string.IsNullOrEmpty(result.Error)) return result.Error;
                if (string.IsNullOrWhiteSpace(result.Text)) return "Console is empty.";
                return result.Text;
            });

        [McpServerTool(Name = "console_send")]
        [Description("Send input to the console of the program being debugged, either as text or as key names. Use this to drive a program that is waiting on input instead of leaving it blocked.")]
        public Task<string> ConsoleSend(
            [Description("Text to type. A newline is appended unless you end it with one.")] string text = null,
            [Description("Key names instead of text, for example 'enter', 'ctrl+c', 'escape'.")] string keys = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(keys)) return "Give text or keys.";
                var result = await link.Debug.ConsoleSendAsync(text, keys, ct).ConfigureAwait(false);
                return Render.Op(result, "Sent.");
            });

        [McpServerTool(Name = "output", ReadOnly = true)]
        [Description("Read a Visual Studio output pane. The Debug pane is where native diagnostics land: module loads, 'cannot find or open the PDB file', first-chance exception notices, and everything the program writes with OutputDebugString. Check it whenever symbols or breakpoints behave oddly. Filter with a regular expression to keep the reply small.")]
        public Task<string> Output(
            [Description("Pane name: Debug, Build, or any other pane title.")] string pane = "Debug",
            [Description("Regular expression; only matching lines are returned.")] string pattern = null,
            [Description("Return only the last N matching lines.")] int tail = 100,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.OutputReadAsync(pane, pattern, tail, ct).ConfigureAwait(false);
                if (result == null) return "No output.";
                if (string.IsNullOrWhiteSpace(result.Text)) return "Pane '" + pane + "' has no matching output.";

                var header = result.Truncated ? "(showing last " + result.Lines + " lines)\n" : "";
                return header + result.Text;
            });
    }
}
