using System;
using System.Runtime.InteropServices;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Makes calls from the reading thread into Visual Studio wait and retry instead of
    /// failing.
    ///
    /// Reads of the debuggee run on a thread of their own, and the engine's objects
    /// live on Visual Studio's UI thread, so every call crosses to it. While the shell
    /// is busy it rejects such calls with RPC_E_SERVERCALL_RETRYLATER. The debug engine
    /// interfaces return that as an error code, and the readers would take it for an
    /// empty stack or a frame with nothing in it.
    ///
    /// Installed only on the reading thread. Registering a filter on Visual Studio's
    /// own UI thread replaces the shell's filter for the whole process.
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

        /// <summary>Calls on this thread the shell kept rejecting until the retry window ran out.</summary>
        [ThreadStatic] public static int Refused;

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
            if (rejectType == 2 && tickCount < RetryWindowMs) return RetryAllowed;
            Refused++;
            return CancelCall;
        }

        int IOleMessageFilter.MessagePending(IntPtr threadIdCallee, int tickCount, int pendingType)
            => 2; // PENDINGMSG_WAITDEFPROCESS: keep handling this thread's own messages.
    }
}
