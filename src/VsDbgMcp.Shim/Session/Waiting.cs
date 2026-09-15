using System;
using System.Threading;
using System.Threading.Tasks;

namespace VsDbgMcp.Shim.Session
{
    /// <summary>
    /// Waiting for something that is pushed, with a deadline. Shared by the stop
    /// stream, the module stream, the output stream and the event log, because all
    /// four have to give up the same way: null when the time runs out, and a throw
    /// when the call itself was cancelled.
    /// </summary>
    static class Waiting
    {
        public static async Task<T> ForAsync<T>(TaskCompletionSource<T> completion, Action stopWaiting,
            TimeSpan timeout, CancellationToken ct) where T : class
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (ct.Register(() => completion.TrySetCanceled(ct)))
            {
                var delay = Task.Delay(timeout, cts.Token);
                var completed = await Task.WhenAny(completion.Task, delay).ConfigureAwait(false);

                if (completed == completion.Task)
                {
                    cts.Cancel();
                    stopWaiting();

                    // Throws if the call was cancelled rather than satisfied.
                    return await completion.Task.ConfigureAwait(false);
                }
            }

            stopWaiting();
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }
}
