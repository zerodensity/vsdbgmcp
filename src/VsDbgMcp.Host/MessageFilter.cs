using System;
using System.Runtime.InteropServices;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Makes COM calls into Visual Studio wait and retry instead of failing.
    ///
    /// While the debuggee is stopped, or while a build is running, the shell rejects
    /// incoming calls with RPC_E_CALL_REJECTED and RPC_E_SERVERCALL_RETRYLATER. Without
    /// a message filter those surface as random failures from any tool, which is the
    /// usual reason automation against Visual Studio feels unreliable.
    ///
    /// Install this on every thread that talks to the DTE or shell services.
    /// </summary>
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int callType, IntPtr threadIdCaller, int tickCount, IntPtr interfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr threadIdCallee, int tickCount, int rejectType);

        [PreserveSig]
        int MessagePending(IntPtr threadIdCallee, int tickCount, int pendingType);
    }

    public sealed class MessageFilter : IOleMessageFilter
    {
        const int Handled = 0;
        const int RetryAllowed = 2;
        const int CancelCall = -1;

        /// <summary>Retry immediately for this long, then give up rather than hang forever.</summary>
        const int RetryWindowMs = 30000;

        [DllImport("ole32.dll")]
        static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        [ThreadStatic] static bool _installed;

        /// <summary>Safe to call repeatedly; installs once per thread.</summary>
        public static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;

            try
            {
                CoRegisterMessageFilter(new MessageFilter(), out _);
            }
            catch
            {
                // Only works on an STA thread. Nothing to do if it does not take.
            }
        }

        int IOleMessageFilter.HandleInComingCall(int callType, IntPtr threadIdCaller, int tickCount, IntPtr interfaceInfo)
            => Handled;

        int IOleMessageFilter.RetryRejectedCall(IntPtr threadIdCallee, int tickCount, int rejectType)
        {
            // rejectType 2 is SERVERCALL_RETRYLATER. Returning a value below 100 means
            // "retry after that many milliseconds", which is what makes the caller wait
            // out a busy shell instead of failing.
            if (rejectType != 2) return CancelCall;
            return tickCount < RetryWindowMs ? RetryAllowed : CancelCall;
        }

        int IOleMessageFilter.MessagePending(IntPtr threadIdCallee, int tickCount, int pendingType)
            => 2; // PENDINGMSG_WAITDEFPROCESS: keep pumping so the UI stays alive.
    }
}
