using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim.Tools
{
    public abstract class ToolBase
    {
        /// <summary>
        /// What the panel shows folded. Longer than a glance but far short of a
        /// disassembly listing; the extension caps it again on arrival.
        /// </summary>
        const int MaxReportedResult = 4000;

        protected ToolBase(SessionManager sessions)
        {
            Sessions = sessions;
        }

        protected SessionManager Sessions { get; }

        /// <summary>
        /// Resolves the instance, runs the call, and turns every failure into text the
        /// agent can act on. Routing problems already carry their own fix, so they are
        /// passed through as they are rather than dressed up as an error.
        ///
        /// A failure leaves as an exception, which is how this SDK marks a result as an
        /// error without every tool having to return a protocol object. The text is the
        /// same either way; what changes is that a caller no longer has to read the words
        /// to find out whether the call did anything.
        ///
        /// Also the one place every tool passes through, so it is where the call is
        /// reported to the panel inside Visual Studio.
        /// </summary>
        protected async Task<string> On(string instance, CancellationToken ct, Func<HostLink, Task<Reply>> body,
            string detail = null, [CallerMemberName] string caller = null)
        {
            var started = Stopwatch.StartNew();
            HostLink link = null;
            Reply reply;

            try
            {
                link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false);
                reply = await body(link).ConfigureAwait(false);
            }
            catch (RoutingException ex)
            {
                reply = Reply.Bad(ex.Message);
            }
            catch (OperationCanceledException)
            {
                return "Cancelled.";
            }
            catch (Exception ex)
            {
                reply = Reply.Bad("ERROR: " + Flatten(ex));
            }

            started.Stop();
            return Answer(link, caller, detail, reply, started.ElapsedMilliseconds);
        }

        /// <summary>
        /// A tool that answers out of what the shim already holds.
        ///
        /// It is still shown in the panel when a window is connected, and it still works
        /// when none is: refusing to re-read a profile that was collected an hour ago
        /// because Visual Studio has since closed would be a failure with no cause.
        /// </summary>
        protected async Task<string> Locally(string instance, CancellationToken ct, Func<Reply> body,
            string detail = null, [CallerMemberName] string caller = null)
        {
            var started = Stopwatch.StartNew();
            HostLink link = null;

            try { link = await Sessions.ResolveAsync(instance, ct).ConfigureAwait(false); }
            catch (Exception) { /* no window is a reason to skip the panel, not to refuse. */ }

            Reply reply;

            try
            {
                reply = body();
            }
            catch (OperationCanceledException)
            {
                return "Cancelled.";
            }
            catch (Exception ex)
            {
                reply = Reply.Bad("ERROR: " + Flatten(ex));
            }

            started.Stop();
            return Answer(link, caller, detail, reply, started.ElapsedMilliseconds);
        }

        /// <summary>
        /// Tells the panel how the call went and hands the text back, as a failure when
        /// that is what it is.
        /// </summary>
        static string Answer(HostLink link, string caller, string detail, Reply reply, long milliseconds)
        {
            Report(link, ToolName(caller), detail, reply.Text, (int)milliseconds, reply.Failed);
            if (reply.Failed) throw new ToolFailure(reply.Text);
            return reply.Text;
        }

        /// <summary>
        /// One way and never awaited. The panel is worth having, but not at the cost of
        /// slowing a tool down or failing one because the report did not land - including
        /// against an older extension that has no method to receive it.
        /// </summary>
        static void Report(HostLink link, string tool, string detail, string result, int milliseconds, bool failed)
        {
            var debug = link?.Debug;
            if (debug == null) return;

            var report = new CallReport
            {
                Tool = tool,
                Arguments = detail,
                Result = Trim(result),
                Milliseconds = milliseconds,
                Failed = failed
            };

            try
            {
                _ = debug.ReportCallAsync(report).ContinueWith(
                    t => { var ignored = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            catch
            {
                // The connection went away between the call and the report.
            }
        }

        static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxReportedResult) return text;
            return text.Substring(0, MaxReportedResult);
        }

        /// <summary>The tool name as the agent typed it, from the method serving it.</summary>
        internal static string ToolName(string caller)
        {
            if (string.IsNullOrEmpty(caller)) return "(unknown)";

            var sb = new StringBuilder(caller.Length + 4);
            for (var i = 0; i < caller.Length; i++)
            {
                if (i > 0 && char.IsUpper(caller[i])) sb.Append('_');
                sb.Append(char.ToLowerInvariant(caller[i]));
            }
            return sb.ToString();
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
