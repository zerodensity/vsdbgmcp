using System;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    public abstract class ToolBase
    {
        protected ToolBase(SessionManager sessions)
        {
            Sessions = sessions;
        }

        protected SessionManager Sessions { get; }

        /// <summary>
        /// Resolves the instance, runs the call, and turns every failure into text the
        /// agent can act on. Routing problems already carry their own fix, so they are
        /// passed through unchanged rather than wrapped in an error.
        /// </summary>
        protected async Task<string> On(string instance, CancellationToken ct, Func<HostLink, Task<string>> body)
        {
            try
            {
                var link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
                return await body(link).ConfigureAwait(false);
            }
            catch (RoutingException ex)
            {
                return ex.Message;
            }
            catch (OperationCanceledException)
            {
                return "Cancelled.";
            }
            catch (Exception ex)
            {
                return "ERROR: " + Flatten(ex);
            }
        }

        internal static string Flatten(Exception ex)
        {
            // StreamJsonRpc wraps the far side's failure; the inner message is the useful one.
            var remote = ex as StreamJsonRpc.RemoteInvocationException;
            if (remote != null && !string.IsNullOrEmpty(remote.Message)) return remote.Message;

            if (ex is AggregateException agg && agg.InnerException != null) return Flatten(agg.InnerException);
            if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                return Flatten(tie.InnerException);

            return ex.Message;
        }
    }
}
