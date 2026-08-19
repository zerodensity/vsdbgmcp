using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    [McpServerToolType]
    public sealed class BreakpointTools : ToolBase
    {
        public BreakpointTools(SessionManager sessions) : base(sessions) { }

        [McpServerTool(Name = "bp_set")]
        [Description("Set a breakpoint and report whether it actually bound. Three kinds: a source location (file and line), a function by name with an optional module, or a data breakpoint that fires when the memory at an address changes. Always check the reply: an unbound breakpoint never hits, and the reply says why - module not loaded, no symbols, or source that does not match the binary.")]
        public Task<string> BpSet(
            [Description("Full path of the source file, for a location breakpoint.")] string file = null,
            [Description("Line number, 1 based, for a location breakpoint.")] int line = 0,
            [Description("Function name, for a function breakpoint. C++ may need the qualified name, for example 'Mesh::Upload'.")] string function = null,
            [Description("Module that owns the function, for example 'engine.dll'. Helps when the same name exists in several modules.")] string module = null,
            [Description("Address or expression to watch, for a data breakpoint. C++ only. This is the tool for finding what corrupts a value.")] string dataExpression = null,
            [Description("Bytes to watch for a data breakpoint: 1, 2, 4, or 8.")] int dataSize = 4,
            [Description("Only break when this expression is true, for example 'i == 42'.")] string condition = null,
            [Description("Only break on this hit number. 0 means break every time.")] int hitCount = 0,
            [Description("Log this message and keep running instead of breaking. Makes it a tracepoint. Expressions in braces are evaluated, for example 'n={count}'.")] string logMessage = null,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var request = new BreakpointRequest
                {
                    File = file,
                    Line = line,
                    Function = function,
                    Module = module,
                    Expression = dataExpression,
                    Size = dataSize,
                    Condition = condition,
                    HitCountTarget = hitCount,
                    LogMessage = logMessage
                };

                if (!string.IsNullOrEmpty(dataExpression)) request.Kind = BreakpointKind.Data;
                else if (!string.IsNullOrEmpty(function)) request.Kind = BreakpointKind.Function;
                else request.Kind = BreakpointKind.Location;

                if (request.Kind == BreakpointKind.Location && (string.IsNullOrEmpty(file) || line <= 0))
                    return "Give a file and line, a function, or a data expression.";

                var info = await link.Debug.BreakpointSetAsync(request, ct).ConfigureAwait(false);
                return Render.Breakpoint(info);
            });

        [McpServerTool(Name = "bp_list", ReadOnly = true)]
        [Description("List every breakpoint with its bind state and hit count. Unbound ones are marked and carry the reason they did not bind.")]
        public Task<string> BpList(
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var list = await link.Debug.BreakpointListAsync(ct).ConfigureAwait(false);
                return Render.Breakpoints(list);
            });

        [McpServerTool(Name = "bp_remove", Destructive = true)]
        [Description("Remove a breakpoint by the id shown in bp_list or returned by bp_set.")]
        public Task<string> BpRemove(
            [Description("Breakpoint id.")] int id,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.BreakpointRemoveAsync(id, ct).ConfigureAwait(false);
                return Render.Op(result, "Removed.");
            });

        [McpServerTool(Name = "bp_enable")]
        [Description("Enable or disable a breakpoint without removing it, so its condition and hit count survive.")]
        public Task<string> BpEnable(
            [Description("Breakpoint id.")] int id,
            [Description("True to enable, false to disable.")] bool enabled = true,
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                var result = await link.Debug.BreakpointEnableAsync(id, enabled, ct).ConfigureAwait(false);
                return Render.Op(result, enabled ? "Enabled." : "Disabled.");
            });

        [McpServerTool(Name = "exceptions_set")]
        [Description("Choose when the debugger breaks on an exception. Break on 'thrown' to stop at the throw site even when something catches it, which is how you find the origin of a swallowed failure. Break on 'unhandled' for the default behaviour, or 'never' to ignore it. Call with no code to list the current settings.")]
        public Task<string> ExceptionsSet(
            [Description("Category, for example 'C++ Exceptions', 'Win32 Exceptions', or 'Common Language Runtime Exceptions'.")] string category = null,
            [Description("Exception type or code, for example 'std::bad_alloc' or '0xC0000005'. Omit to list current settings.")] string code = null,
            [Description("thrown, unhandled, or never.")] string breakOn = "thrown",
            [Description("Instance id. Omit to use the default for this session.")] string instance = null,
            CancellationToken ct = default)
            => On(instance, ct, async link =>
            {
                if (string.IsNullOrEmpty(code))
                {
                    var settings = await link.Debug.ExceptionListAsync(ct).ConfigureAwait(false);
                    if (settings == null || settings.Count == 0) return "No non-default exception settings.";

                    var sb = new StringBuilder();
                    foreach (var s in settings.OrderBy(s => s.Category).ThenBy(s => s.Code))
                        sb.Append("  ").Append(s.Category).Append("  ").Append(s.Code).Append("  ").AppendLine(s.BreakOn);
                    return sb.ToString().TrimEnd();
                }

                var result = await link.Debug.ExceptionSetAsync(new ExceptionSetting
                {
                    Category = category,
                    Code = code,
                    BreakOn = breakOn
                }, ct).ConfigureAwait(false);
                return Render.Op(result, "Exception setting updated.");
            });
    }
}
