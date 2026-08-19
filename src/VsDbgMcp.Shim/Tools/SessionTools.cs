using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    [McpServerToolType]
    public sealed class SessionTools : ToolBase
    {
        public SessionTools(SessionManager sessions) : base(sessions) { }

        [McpServerTool(Name = "instances", ReadOnly = true)]
        [Description("List the running Visual Studio instances this server can drive, with the solution each has open, its debugger mode, and which one calls go to by default. Use the id shown here as the 'instance' argument on any other tool.")]
        public async Task<string> Instances(CancellationToken ct = default)
        {
            var links = await Sessions.RefreshAsync(true, ct).ConfigureAwait(false);
            return Render.Instances(links, Sessions.Cwd, Sessions.StickyInstanceId);
        }

        [McpServerTool(Name = "use")]
        [Description("Set the default Visual Studio instance for the rest of this session, so other tools do not need an 'instance' argument. Pass an id from 'instances', or leave empty to go back to picking by working directory.")]
        public async Task<string> Use(
            [Description("Instance id such as 'App#42696'. A unique prefix or a bare process id also works. Empty clears the default.")] string instance = null,
            CancellationToken ct = default)
        {
            try
            {
                return await Sessions.UseAsync(instance, ct).ConfigureAwait(false);
            }
            catch (RoutingException ex)
            {
                return ex.Message;
            }
        }
    }
}
