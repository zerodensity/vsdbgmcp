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
        readonly Func<string, bool> _idInUse;
        public string Epoch { get; } = ShortId.New();

        public OperationRegistry(Func<OperationInfo, OperationInfo> copy, Action<OperationInfo> save = null, string instanceId = null,
            Func<string, bool> idInUse = null)
        { _copy = copy; _save = save; _instanceId = instanceId; _idInUse = idInUse; }

        public OperationInfo Begin(string kind, string requestId, string request, bool exclusive, out bool created)
        {
            lock (_gate)
            {
                var existing = Existing(kind, requestId, request, exclusive);
                if (existing != null) { created = false; return _copy(existing); }
            }
            // Historical collision checks may touch disk. Never hold up status reads.
            var id = ShortId.New(candidate =>
            {
                lock (_gate) if (_items.ContainsKey(candidate)) return true;
                return _idInUse?.Invoke(candidate) ?? false;
            });
            OperationInfo result;
            lock (_gate)
            {
                // Recheck after allocation: another caller may have won this request.
                var previous = Existing(kind, requestId, request, exclusive);
                if (previous != null)
                {
                    created = false;
                    return _copy(previous);
                }
                result = new OperationInfo { OperationId = id,
                    RequestId = requestId, Request = request, Kind = kind, HostEpoch = Epoch, InstanceId = _instanceId,
                    StartedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, State = "requested",
                    ExecutionPhase = "queued", BlocksNewRequests = exclusive };
                _items.Add(result.OperationId, result);
                _done.Add(result.OperationId, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                _updateGates.Add(result.OperationId, new object());
                result = _copy(result);
                created = true;
            }
            Persist(result);
            return result;
        }

        // Called under _gate, both before and after allocating an ID.
        OperationInfo Existing(string kind, string requestId, string request, bool exclusive)
        {
            var previous = _items.Values.FirstOrDefault(x => x.Kind == kind && x.RequestId == requestId && requestId != null);
            if (previous != null)
            {
                if (previous.Request != request) throw new InvalidOperationException("requestId was already used with different arguments.");
                return previous;
            }
            previous = _items.Values.FirstOrDefault(x => x.Kind == kind && x.BlocksNewRequests);
            if (exclusive && previous != null)
                throw new InvalidOperationException(kind + " already pending: " + previous.OperationId + ". Query operation_status before retrying.");
            return null;
        }

        public OperationInfo Read(string id)
        {
            lock (_gate) return _items.TryGetValue(id, out var item) ? _copy(item) : null;
        }
        public List<OperationInfo> All()
        { lock (_gate) return _items.Values.OrderByDescending(x => x.StartedUtc).Select(_copy).ToList(); }

        public OperationInfo Update(string id, Action<OperationInfo> update, bool terminal = false) => Mutate(id, update, terminal, false);

        OperationInfo Mutate(string id, Action<OperationInfo> update, bool terminal, bool lifecycle)
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
                if (item.Terminal && !lifecycle) return _copy(item);
                update(item);
                item.UpdatedUtc = DateTime.UtcNow;
                if (terminal || item.Terminal)
                {
                    item.Terminal = true;
                    item.CompletedUtc = item.CompletedUtc ?? item.UpdatedUtc;
                    item.BlocksNewRequests = (item.CommandInFlight || item.State == "unknown") && item.BlocksNewRequests;
                }
                lock (_gate)
                {
                    _items[id] = _copy(item);
                    // A retained result with an unresolved dispatch must not turn
                    // the model's requested wait into an immediate polling loop.
                    if (item.Terminal && !item.BlocksNewRequests) done = _done[id];
                    result = _copy(item);
                }
            }
            Persist(result);
            done?.TrySetResult(true);
            return result;
        }

        public bool TryStartCommand(string id)
        {
            bool started = false;
            Mutate(id, o =>
            {
                if (o.Terminal || o.CommandStartedUtc != null) return;
                started = true;
                o.ExecutionPhase = "in-call";
                o.CommandInFlight = true;
                o.CommandStartedUtc = DateTime.UtcNow;
            }, false, false);
            return started;
        }

        public void CommandReturned(string id) => Mutate(id, o =>
        {
            o.CommandInFlight = false;
            o.CommandReturnedUtc = DateTime.UtcNow;
            o.ExecutionPhase = "returned";
            if (o.Terminal && o.State != "unknown") o.BlocksNewRequests = false;
        }, false, true);

        // Only uncertainty plus a fresh idle observation can retire unresolved work.
        // Normal pending work may still be queued inside VS after dispatch returns.
        public OperationInfo ObserveIdle(string id, StateObservation observation)
        {
            return Mutate(id, o =>
            {
                if (o.State != "unknown" || o.CommandInFlight || o.CommandReturnedUtc == null ||
                    observation == null || observation.TimestampUtc < o.CommandReturnedUtc) return;
                var idle = o.Kind == "build" ? observation.BuildBusy == false :
                    o.Kind == "launch" && observation.Mode == "design";
                if (!idle || !o.BlocksNewRequests) return;
                o.Observation = observation;
                o.BlocksNewRequests = false;
                o.Terminal = true;
                o.Message = (o.Message == null ? "" : o.Message + " ") +
                    "The command returned and VS was observed idle. The outcome is still unknown; inspect its effects before starting another request.";
            }, false, true);
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
