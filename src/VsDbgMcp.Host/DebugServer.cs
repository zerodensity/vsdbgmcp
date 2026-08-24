using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Which machine a debugged process is actually running on.
    ///
    /// A process reached over msvsmon looks like a dead one in every reply: its pid is
    /// in no local process list and the paths it reports do not exist on this disk.
    /// Both observations are what remote debugging looks like, and with nothing saying
    /// so they read as a session that has gone stale.
    /// </summary>
    struct DebugServer
    {
        public bool IsRemote;
        public string Machine;
        public string Transport;

        public static DebugServer Of(IDebugProgram2 program)
        {
            var server = default(DebugServer);
            if (program == null) return server;

            if (program.GetProcess(out var process) != VSConstants.S_OK || process == null) return server;
            if (process.GetServer(out var core) != VSConstants.S_OK || core == null) return server;

            var core3 = core as IDebugCoreServer3;
            if (core3 == null) return server;

            // QueryIsLocal answers through the HRESULT itself: S_OK for this machine,
            // S_FALSE for another one. Anything else is the question going unanswered,
            // and an unanswered question must not come out as "remote".
            if (core3.QueryIsLocal() != VSConstants.S_FALSE) return server;

            server.IsRemote = true;

            if (core3.GetServerFriendlyName(out var friendly) == VSConstants.S_OK && !string.IsNullOrWhiteSpace(friendly))
                server.Machine = friendly.Trim();
            else if (core3.GetMachineName(out var machine) == VSConstants.S_OK && !string.IsNullOrWhiteSpace(machine))
                server.Machine = machine.Trim();

            var protocol = new CONNECTION_PROTOCOL[1];
            if (core3.GetConnectionProtocol(protocol) == VSConstants.S_OK)
                server.Transport = Named(protocol[0]);

            return server;
        }

        static string Named(CONNECTION_PROTOCOL protocol)
        {
            switch (protocol)
            {
                case CONNECTION_PROTOCOL.CONNECTION_TCPIP: return "TCP/IP";
                case CONNECTION_PROTOCOL.CONNECTION_PIPE: return "a named pipe";
                case CONNECTION_PROTOCOL.CONNECTION_HTTP: return "HTTP";
                default: return null;
            }
        }
    }
}
