using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    // Pure state machine. The owner supplies snapshot copying and optional persistence.
    // Update callbacks and copy functions must be pure, bounded in-memory work.
    // Persistence runs outside the registry lock; callers must snapshot VS state first.
    public sealed class OperationRegistry
    {
        readonly object _gate = new object();
        readonly Dictionary<string, OperationInfo> _items = new Dictionary<string, OperationInfo>();
        readonly Dictionary<string, TaskCompletionSource<bool>> _done = new Dictionary<string, TaskCompletionSource<bool>>();
        readonly Dictionary<string, object> _updateGates = new Dictionary<string, object>();
        readonly Func<OperationInfo, OperationInfo> _copy;
        readonly Action<OperationInfo> _save;
        readonly string _instanceId;
        public string Epoch { get; } = Guid.NewGuid().ToString("N");

        public OperationRegistry(Func<OperationInfo, OperationInfo> copy, Action<OperationInfo> save = null, string instanceId = null)
        { _copy = copy; _save = save; _instanceId = instanceId; }

        public OperationInfo Begin(string kind, string requestId, string request, bool exclusive, out bool created)
        {
            OperationInfo result;
            lock (_gate)
            {
                var previous = _items.Values.FirstOrDefault(x => x.Kind == kind && x.RequestId == requestId && requestId != null);
                if (previous != null)
                {
                    if (previous.Request != request) throw new InvalidOperationException("requestId was already used with different arguments.");
                    created = false;
                    return _copy(previous);
                }
                previous = _items.Values.FirstOrDefault(x => x.Kind == kind && !x.Terminal);
                if (exclusive && previous != null)
                    throw new InvalidOperationException(kind + " already pending: " + previous.OperationId + ". Query operation_status before retrying.");
                result = new OperationInfo { OperationId = kind + "-" + Guid.NewGuid().ToString("N"),
                    RequestId = requestId, Request = request, Kind = kind, HostEpoch = Epoch, InstanceId = _instanceId,
                    StartedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, State = "requested" };
                _items.Add(result.OperationId, result);
                _done.Add(result.OperationId, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                _updateGates.Add(result.OperationId, new object());
                result = _copy(result);
                created = true;
            }
            Persist(result);
            return result;
        }

        public OperationInfo Read(string id)
        {
            lock (_gate) return _items.TryGetValue(id, out var item) ? _copy(item) : null;
        }
        public List<OperationInfo> All()
        { lock (_gate) return _items.Values.OrderByDescending(x => x.StartedUtc).Select(_copy).ToList(); }

        public OperationInfo Update(string id, Action<OperationInfo> update, bool terminal = false)
        {
            OperationInfo result;
            TaskCompletionSource<bool> done = null;
            object updateGate;
            lock (_gate) updateGate = _updateGates[id];
            // Serialize mutations of this operation, without blocking observation or
            // unrelated operations if an owner's update callback unexpectedly stalls.
            lock (updateGate)
            {
                OperationInfo item;
                lock (_gate) item = _copy(_items[id]);
                if (item.Terminal) return _copy(item);
                update(item);
                item.UpdatedUtc = DateTime.UtcNow;
                if (terminal)
                {
                    item.Terminal = true;
                    item.CompletedUtc = item.UpdatedUtc;
                }
                lock (_gate)
                {
                    _items[id] = _copy(item);
                    if (terminal) done = _done[id];
                    result = _copy(item);
                }
            }
            Persist(result);
            done?.TrySetResult(true);
            return result;
        }

        void Persist(OperationInfo snapshot)
        {
            try { _save?.Invoke(snapshot); }
            catch (Exception ex)
            {
                snapshot.PersistenceError = ex.Message;
                lock (_gate) _items[snapshot.OperationId].PersistenceError = ex.Message;
            }
        }

        public async Task<OperationInfo> WaitAsync(string id, int seconds, CancellationToken ct)
        {
            Task done;
            lock (_gate)
            {
                if (!_done.TryGetValue(id, out var source)) return null;
                done = source.Task;
            }
            if (seconds > 0)
                await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(Math.Min(60, seconds)), ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); // Cancels the observation only, never the operation.
            return Read(id);
        }
    }
}
