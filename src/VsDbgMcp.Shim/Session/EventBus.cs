using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Session
{
    /// <summary>
    /// Stop events from every connected instance, in one ordered stream.
    ///
    /// Events are pushed by Visual Studio and buffered here, so a stop that happens
    /// between two wait() calls is still delivered on the next one. Polling for state
    /// cannot give that guarantee, which is the whole reason for this class.
    /// </summary>
    public sealed class EventBus
    {
        const int BufferSize = 256;

        readonly object _gate = new object();
        readonly LinkedList<StopEvent> _buffer = new LinkedList<StopEvent>();
        readonly List<Waiter> _waiters = new List<Waiter>();
        long _seq;
        long _cursor;

        sealed class Waiter
        {
            public string InstanceId;
            public TaskCompletionSource<StopEvent> Completion;
        }

        public void Publish(StopEvent stop)
        {
            if (stop == null) return;

            List<Waiter> toSignal = null;

            lock (_gate)
            {
                stop.Seq = ++_seq;
                _buffer.AddLast(stop);
                while (_buffer.Count > BufferSize) _buffer.RemoveFirst();

                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    var w = _waiters[i];
                    if (!Matches(w.InstanceId, stop)) continue;

                    _waiters.RemoveAt(i);
                    (toSignal ??= new List<Waiter>()).Add(w);
                    _cursor = stop.Seq;
                }
            }

            if (toSignal == null) return;
            foreach (var w in toSignal)
                w.Completion.TrySetResult(stop);
        }

        /// <summary>
        /// The next stop we have not handed out yet, or the next one to arrive.
        /// Returns null on timeout, which the caller reports as reason "timeout".
        /// </summary>
        public async Task<StopEvent> WaitAsync(string instanceId, TimeSpan timeout, CancellationToken ct)
        {
            Waiter waiter;

            lock (_gate)
            {
                var buffered = _buffer.FirstOrDefault(e => e.Seq > _cursor && Matches(instanceId, e));
                if (buffered != null)
                {
                    _cursor = buffered.Seq;
                    return buffered;
                }

                waiter = new Waiter
                {
                    InstanceId = instanceId,
                    Completion = new TaskCompletionSource<StopEvent>(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                _waiters.Add(waiter);
            }

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (ct.Register(() => waiter.Completion.TrySetCanceled(ct)))
            {
                var delay = Task.Delay(timeout, cts.Token);
                var completed = await Task.WhenAny(waiter.Completion.Task, delay).ConfigureAwait(false);

                if (completed == waiter.Completion.Task)
                {
                    cts.Cancel();
                    lock (_gate) _waiters.Remove(waiter);

                    // Throws if the call was cancelled rather than satisfied.
                    return await waiter.Completion.Task.ConfigureAwait(false);
                }
            }

            lock (_gate) _waiters.Remove(waiter);
            ct.ThrowIfCancellationRequested();
            return null;
        }

        /// <summary>
        /// Moves the cursor past everything currently buffered. Called when a tool
        /// resumes execution, so the next wait() reports the coming stop rather than
        /// the one that is already history.
        /// </summary>
        public void MarkSeen()
        {
            lock (_gate) _cursor = _seq;
        }

        public StopEvent Latest(string instanceId)
        {
            lock (_gate) return _buffer.LastOrDefault(e => Matches(instanceId, e));
        }

        static bool Matches(string instanceId, StopEvent stop) =>
            string.IsNullOrEmpty(instanceId) ||
            string.Equals(instanceId, stop.InstanceId, StringComparison.OrdinalIgnoreCase);
    }
}
