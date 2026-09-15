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
    ///
    /// What every publisher owes this: hand the event to the waiter and mark it spent
    /// in the same step, under the lock that stopWaiting takes. Marking it spent first
    /// and handing it over afterwards loses it whenever the waiter gives up in between.
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

            // The publisher hands the event over and marks it shown in one step, both
            // under its own lock, and stopWaiting above takes that same lock. So an
            // answer sitting here belongs to this wait and to nobody else: the clock
            // ran out first, but the event has already been spent on it. Returning
            // null here instead would lose that event for good.
            if (completion.Task.IsCompletedSuccessfully) return completion.Task.Result;

            ct.ThrowIfCancellationRequested();
            return null;
        }
    }
}
