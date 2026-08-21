using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Session
{
    /// <summary>
    /// Stop events from every connected instance, in one ordered stream, plus a second
    /// stream for the modules the debuggee loads.
    ///
    /// Events are pushed by Visual Studio and buffered here, so a stop that happens
    /// between two wait() calls is still delivered on the next one. Polling for state
    /// cannot give that guarantee, which is the whole reason for this class.
    ///
    /// The two streams are kept apart on purpose. Loading a module does not stop
    /// anything, and a caller waiting for the debuggee to stop must never be woken by
    /// one; only a caller who asked for modules is told about them.
    /// </summary>
    public sealed class EventBus
    {
        const int BufferSize = 256;

        readonly object _gate = new object();
        readonly LinkedList<StopEvent> _buffer = new LinkedList<StopEvent>();
        readonly List<Waiter> _waiters = new List<Waiter>();
        readonly LinkedList<LoadedModule> _modules = new LinkedList<LoadedModule>();
        readonly List<ModuleWaiter> _moduleWaiters = new List<ModuleWaiter>();
        long _seq;
        long _cursor;

        sealed class Waiter
        {
            public string InstanceId;
            public TaskCompletionSource<StopEvent> Completion;
        }

        sealed class ModuleWaiter
        {
            public string InstanceId;
            public string Pattern;
            public TaskCompletionSource<ModuleLoadEvent> Completion;
        }

        /// <summary>
        /// A buffered module load, and whether it has been reported yet.
        ///
        /// Modules are marked one by one rather than by a moving cursor: waiting for
        /// one plugin must not hide another that loaded a moment earlier, because
        /// arming breakpoints across several plugins and waiting for each in turn is
        /// exactly what this is for.
        /// </summary>
        sealed class LoadedModule
        {
            public ModuleLoadEvent Load;
            public bool Reported;
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
                    if (!Matches(w.InstanceId, stop.InstanceId)) continue;

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
                var buffered = _buffer.FirstOrDefault(e => e.Seq > _cursor && Matches(instanceId, e.InstanceId));
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

            return await AwaitAsync(
                waiter.Completion,
                () => { lock (_gate) _waiters.Remove(waiter); },
                timeout, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Publishes a module the debuggee has loaded. Nobody waiting for a stop hears
        /// about it.
        /// </summary>
        public void PublishModuleLoad(ModuleLoadEvent module)
        {
            if (module == null || string.IsNullOrEmpty(module.Name)) return;

            List<ModuleWaiter> toSignal = null;

            lock (_gate)
            {
                var entry = new LoadedModule { Load = module };
                _modules.AddLast(entry);
                while (_modules.Count > BufferSize) _modules.RemoveFirst();

                for (var i = _moduleWaiters.Count - 1; i >= 0; i--)
                {
                    var w = _moduleWaiters[i];
                    if (!Matches(w.InstanceId, module.InstanceId)) continue;
                    if (!NameContains(module.Name, w.Pattern)) continue;

                    _moduleWaiters.RemoveAt(i);
                    (toSignal ??= new List<ModuleWaiter>()).Add(w);
                    entry.Reported = true;
                }
            }

            if (toSignal == null) return;
            foreach (var w in toSignal)
                w.Completion.TrySetResult(module);
        }

        /// <summary>
        /// A module whose name contains the pattern: one that has already loaded and
        /// not been reported yet, or the next one to arrive. Returns null on timeout.
        ///
        /// Answering from the buffer matters as much as blocking does. A caller asking
        /// about a plugin that loaded while it was doing something else wants to hear
        /// that it is loaded, not wait out the timeout on something already done.
        /// </summary>
        public async Task<ModuleLoadEvent> WaitForModuleAsync(string instanceId, string pattern, TimeSpan timeout, CancellationToken ct)
        {
            ModuleWaiter waiter;

            lock (_gate)
            {
                var already = _modules.FirstOrDefault(m =>
                    !m.Reported && Matches(instanceId, m.Load.InstanceId) && NameContains(m.Load.Name, pattern));
                if (already != null)
                {
                    already.Reported = true;
                    return already.Load;
                }

                waiter = new ModuleWaiter
                {
                    InstanceId = instanceId,
                    Pattern = pattern,
                    Completion = new TaskCompletionSource<ModuleLoadEvent>(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                _moduleWaiters.Add(waiter);
            }

            return await AwaitAsync(
                waiter.Completion,
                () => { lock (_gate) _moduleWaiters.Remove(waiter); },
                timeout, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Waits for whatever the publisher sets, giving up after the timeout. Returns
        /// null when the time runs out, and throws when the call itself was cancelled.
        /// </summary>
        static async Task<T> AwaitAsync<T>(TaskCompletionSource<T> completion, Action stopWaiting, TimeSpan timeout, CancellationToken ct)
            where T : class
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
            lock (_gate) return _buffer.LastOrDefault(e => Matches(instanceId, e.InstanceId));
        }

        static bool Matches(string instanceId, string eventInstanceId) =>
            string.IsNullOrEmpty(instanceId) ||
            string.Equals(instanceId, eventInstanceId, StringComparison.OrdinalIgnoreCase);

        /// <summary>Any part of the module's name, ignoring case, the way modules() filters.</summary>
        static bool NameContains(string name, string pattern) =>
            string.IsNullOrEmpty(pattern) ||
            (name != null && name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
