using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// The thread every read of the debuggee runs on.
    ///
    /// A single-threaded apartment of its own, so it can carry a message filter: the
    /// engine's objects live on Visual Studio's UI thread, every call made here is
    /// carried out there, and a call the busy shell turns away is retried instead of
    /// coming back as an error code that reads like an empty answer. Calls arrive here
    /// one at a time already, so one thread costs nothing in throughput.
    /// </summary>
    sealed class DebuggeeThread
    {
        readonly BlockingCollection<Action> _work = new BlockingCollection<Action>();

        public DebuggeeThread()
        {
            var thread = new Thread(Serve) { IsBackground = true, Name = "vsdbgmcp debuggee reads" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        void Serve()
        {
            MessageFilter.EnsureInstalled();
            foreach (var work in _work.GetConsumingEnumerable()) work();
        }

        /// <summary>
        /// Runs <paramref name="body"/> here, with the caller's execution context. Fails
        /// rather than returning when the shell turned a call away for the whole retry
        /// window, because the answer would be missing whatever that call would have read.
        /// </summary>
        public Task<T> RunAsync<T>(Func<T> body)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var context = ExecutionContext.Capture();

            _work.Add(() =>
            {
                MessageFilter.Refused = 0;
                try
                {
                    var result = default(T);
                    if (context == null) result = body();
                    else ExecutionContext.Run(context, _ => result = body(), null);

                    if (MessageFilter.Refused > 0)
                        done.TrySetException(new InvalidOperationException(
                            "Visual Studio turned away calls from the debugger for 30 seconds, so this read is " +
                            "incomplete and nothing from it is returned. A dialog open in Visual Studio does " +
                            "this; close it and call again."));
                    else
                        done.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            });

            return done.Task;
        }
    }
}
