using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Session
{
    /// <summary>What happened, in the terms the model is shown it.</summary>
    public enum EventKind
    {
        Stopped,
        Exited,
        DebuggingStarted,
        DebuggingEnded,
        OperationDone,
        InstanceGone,
        SolutionChanged
    }

    /// <summary>A change this session asked for, so the log does not report it back.</summary>
    public enum Expected
    {
        RunStart,
        End
    }

    public sealed class LogEntry
    {
        public long Seq { get; set; }

        /// <summary>When it reached the shim. Nothing pushed here carries a time of its own.</summary>
        public DateTime At { get; set; }

        public string InstanceId { get; set; }
        public EventKind Kind { get; set; }
        public StopEvent Stop { get; set; }
        public OperationInfo Operation { get; set; }

        /// <summary>The model already has this, from the reply that caused it.</summary>
        public bool Hidden { get; set; }

        /// <summary>The model has been shown this.</summary>
        public bool Seen { get; set; }

        /// <summary>
        /// Whether the state a reply was read from has moved. A build finishing does
        /// not move it; everything else here does.
        /// </summary>
        public bool Invalidates => Kind != EventKind.OperationDone;
    }

    /// <summary>
    /// The journal of what happened in Visual Studio while the model was doing
    /// something else.
    ///
    /// The bus answers a wait: it hands one stop to one waiter and is done with it.
    /// This keeps the account instead, because the model's next call is where it
    /// finds out, and a call made for an unrelated reason has to be able to carry a
    /// stop from ten minutes ago without consuming anything the bus owns.
    ///
    /// Shown once is the whole rule. Every reader marks what it showed, so two
    /// replies never carry the same line and nothing is lost between them.
    /// </summary>
    public sealed class EventLog
    {
        const int BufferSize = 256;

        /// <summary>
        /// How long the design mode that follows an exit is taken for that same
        /// ending. The notification arrives seconds after the change it announces.
        /// </summary>
        static readonly TimeSpan ExitTail = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long a change this session asked for is still expected. Long enough for
        /// Visual Studio's late notification, short enough that a run somebody else
        /// starts afterwards is not swallowed by it.
        /// </summary>
        static readonly TimeSpan ExpectationLife = TimeSpan.FromSeconds(15);

        /// <summary>How many reported operation ids to remember for pushes that have not landed yet.</summary>
        const int ReportedKept = 64;

        readonly object _gate = new object();
        readonly LinkedList<LogEntry> _entries = new LinkedList<LogEntry>();
        readonly List<Waiter> _waiters = new List<Waiter>();
        readonly List<Action<LogEntry>> _subscribers = new List<Action<LogEntry>>();
        readonly Dictionary<string, string> _modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly List<Expectation> _expectations = new List<Expectation>();
        readonly LinkedList<string> _reported = new LinkedList<string>();
        readonly Func<DateTime> _now;
        long _seq;

        public EventLog(Func<DateTime> now = null)
        {
            _now = now ?? (() => DateTime.UtcNow);
        }

        sealed class Waiter
        {
            public string InstanceId;
            public EventKind[] Kinds;
            public Func<LogEntry, bool> Accept;
            public TaskCompletionSource<LogEntry> Completion;
        }

        sealed class Expectation
        {
            public string InstanceId;
            public Expected Kind;
            public string OperationId;
            public DateTime Deadline;
        }

        // ---- what happened ----

        public void Stopped(StopEvent stop)
        {
            if (stop == null) return;

            var exited = stop.Reason == StopReason.Exited;

            // Terminating the debuggee is what stop does, and the reply already said
            // "Stopped." The exit it causes is that same answer arriving again.
            bool hidden;
            lock (_gate) hidden = exited && Ending(stop.InstanceId);

            Add(exited ? EventKind.Exited : EventKind.Stopped, stop.InstanceId, stop: stop, hidden: hidden);
        }

        public void SolutionChanged(string instanceId) => Add(EventKind.SolutionChanged, instanceId);

        public void InstanceGone(string instanceId) => Add(EventKind.InstanceGone, instanceId);

        /// <summary>
        /// Where an instance's debugger was when this first heard of it. Seeded on
        /// connect the way the bus is, so the first real change has something to be a
        /// change from.
        /// </summary>
        public void InitializeMode(string instanceId, string mode)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            lock (_gate) _modes[instanceId] = mode ?? DebugModes.Design;
        }

        /// <summary>
        /// Told what mode an instance's debugger is in, every time it changes.
        ///
        /// Only crossing design mode is worth a line. Break and run alternate on every
        /// go and every step, and a line for each would bury the ones that matter.
        /// </summary>
        public void ModeChanged(string instanceId, string mode)
        {
            if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(mode)) return;

            string previous;
            lock (_gate)
            {
                _modes.TryGetValue(instanceId, out previous);
                _modes[instanceId] = mode;
            }

            // Nothing seeded it, so there is no change to report yet.
            if (previous == null) return;

            var wasDesign = IsDesign(previous);
            var isDesign = IsDesign(mode);

            if (wasDesign && !isDesign)
            {
                bool asked;
                lock (_gate) asked = Consume(instanceId, Expected.RunStart);
                if (!asked) Add(EventKind.DebuggingStarted, instanceId);
                return;
            }

            if (!wasDesign && isDesign)
            {
                bool asked;
                lock (_gate) asked = Consume(instanceId, Expected.End);
                if (!asked) EndedDebugging(instanceId);
            }
        }

        void EndedDebugging(string instanceId)
        {
            lock (_gate)
            {
                // The exit already said it. This is that same ending arriving late.
                var newest = _entries.LastOrDefault(e => Matches(instanceId, e.InstanceId));
                if (newest != null && newest.Kind == EventKind.Exited && _now() - newest.At <= ExitTail) return;
            }

            Add(EventKind.DebuggingEnded, instanceId);
        }

        LogEntry Add(EventKind kind, string instanceId, StopEvent stop = null, OperationInfo operation = null, bool hidden = false)
        {
            var entry = new LogEntry { InstanceId = instanceId, Kind = kind, Stop = stop, Operation = operation, Hidden = hidden };

            List<Action<LogEntry>> toNotify = null;

            lock (_gate)
            {
                entry.Seq = ++_seq;
                entry.At = _now();
                _entries.AddLast(entry);
                while (_entries.Count > BufferSize) _entries.RemoveFirst();

                if (!entry.Hidden)
                {
                    for (var i = _waiters.Count - 1; i >= 0; i--)
                    {
                        var w = _waiters[i];
                        if (!Matches(w.InstanceId, entry.InstanceId)) continue;
                        if (w.Kinds != null && Array.IndexOf(w.Kinds, entry.Kind) < 0) continue;
                        if (w.Accept != null && !w.Accept(entry)) continue;

                        _waiters.RemoveAt(i);

                        // Handing it over and marking it shown are one step, still under
                        // the lock. A waiter that gave up at this instant never receives
                        // it, and an entry marked shown to nobody is gone from the digest
                        // too. Continuations here are queued, never run on this thread.
                        if (w.Completion.TrySetResult(entry)) entry.Seen = true;
                    }

                    if (_subscribers.Count > 0) toNotify = new List<Action<LogEntry>>(_subscribers);
                }
            }

            if (toNotify != null)
                foreach (var subscriber in toNotify)
                {
                    // A subscriber that throws must not take the event pump down with it.
                    try { subscriber(entry); } catch { }
                }

            return entry;
        }

        // ---- who has been shown what ----

        /// <summary>
        /// Everything not shown yet, marked as shown. One lock, because two tool calls
        /// running at the same time must not both carry the same event.
        /// </summary>
        public IReadOnlyList<LogEntry> TakeUnseen(string instanceId = null)
        {
            lock (_gate)
            {
                var taken = _entries.Where(e => !e.Hidden && !e.Seen && Matches(instanceId, e.InstanceId)).ToList();
                foreach (var entry in taken) entry.Seen = true;
                return taken;
            }
        }

        /// <summary>The last few, shown or not. status answers with these.</summary>
        public IReadOnlyList<LogEntry> Recent(string instanceId, int count)
        {
            lock (_gate)
            {
                var all = _entries.Where(e => !e.Hidden && Matches(instanceId, e.InstanceId)).ToList();
                return all.Skip(Math.Max(0, all.Count - count)).ToList();
            }
        }

        /// <summary>
        /// One instance's events have been accounted for by other means, or every
        /// instance's when no id is given.
        /// </summary>
        public void MarkSeen(string instanceId = null)
        {
            lock (_gate)
                foreach (var entry in _entries.Where(e => Matches(instanceId, e.InstanceId))) entry.Seen = true;
        }

        /// <summary>
        /// The first unshown entry of these kinds, or the next one to arrive. Null on
        /// timeout, and null kinds means any. What it returns is marked shown, because
        /// every caller reports it — so an entry the accept predicate turns down is
        /// left alone for the digest to carry. A caller that ends up not reporting what
        /// it was given hands it back with <see cref="PutBack"/>.
        ///
        /// The accept predicate runs under this log's lock. Keep it short, and never let
        /// it call back into anything that takes this lock.
        /// </summary>
        public async Task<LogEntry> WaitForAsync(string instanceId, EventKind[] kinds, TimeSpan timeout,
            CancellationToken ct, Func<LogEntry, bool> accept = null)
        {
            Waiter waiter;

            lock (_gate)
            {
                var already = _entries.FirstOrDefault(e => !e.Hidden && !e.Seen &&
                    Matches(instanceId, e.InstanceId) && (kinds == null || Array.IndexOf(kinds, e.Kind) >= 0) &&
                    (accept == null || accept(e)));
                if (already != null)
                {
                    already.Seen = true;
                    return already;
                }

                waiter = new Waiter
                {
                    InstanceId = instanceId,
                    Kinds = kinds,
                    Accept = accept,
                    Completion = new TaskCompletionSource<LogEntry>(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                _waiters.Add(waiter);
            }

            return await Waiting.ForAsync(
                waiter.Completion,
                () => { lock (_gate) _waiters.Remove(waiter); },
                timeout, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// A reader took this and did not report it after all, so it is news again.
        ///
        /// A wait that raced something else and lost is the caller: what it consumed
        /// would otherwise stand marked shown to nobody and never reach the digest.
        /// </summary>
        public void PutBack(LogEntry entry)
        {
            if (entry == null) return;
            lock (_gate) { if (!entry.Hidden) entry.Seen = false; }
        }

        /// <summary>
        /// Called for every entry as it is added, while it is being added. --follow
        /// prints from here, so nothing that blocks belongs in a handler.
        /// </summary>
        public void Subscribe(Action<LogEntry> handler)
        {
            if (handler == null) return;
            lock (_gate) _subscribers.Add(handler);
        }

        // ---- what this session asked for ----

        /// <summary>
        /// This session asked for a change, so the notification that confirms it is not
        /// news. The reply already said so, and repeating it teaches the model to stop
        /// reading the block.
        ///
        /// It expires, because the call may have started nothing. An expectation that
        /// outlived its change swallows somebody else's; one that expires too early
        /// echoes this session's own. Fifteen seconds is where those two cost least.
        /// </summary>
        public void Expect(string instanceId, Expected kind, string operationId = null)
        {
            if (string.IsNullOrEmpty(instanceId)) return;

            lock (_gate)
            {
                _expectations.RemoveAll(e => Same(e, instanceId, kind));
                _expectations.Add(new Expectation
                {
                    InstanceId = instanceId,
                    Kind = kind,
                    OperationId = operationId,
                    Deadline = _now() + ExpectationLife
                });
            }
        }

        /// <summary>The call failed, so nothing is coming to confirm it.</summary>
        public void Unexpect(string instanceId, Expected kind)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            lock (_gate) _expectations.RemoveAll(e => Same(e, instanceId, kind));
        }

        /// <summary>
        /// An operation has reached its end.
        ///
        /// A launch may build for minutes before the run starts, so an expectation tied
        /// to one is kept alive until the operation itself finishes, and dropped
        /// outright when it failed: no run is coming after a build that did not link.
        /// </summary>
        public void OperationDone(OperationInfo operation)
        {
            if (operation == null || !operation.Terminal || string.IsNullOrEmpty(operation.OperationId)) return;

            bool reported;
            lock (_gate)
            {
                reported = _reported.Remove(operation.OperationId);

                foreach (var expectation in _expectations.Where(e => e.OperationId == operation.OperationId).ToList())
                {
                    if (OperationOutcome.Failing(operation)) _expectations.Remove(expectation);
                    else expectation.Deadline = _now() + ExpectationLife;
                }
            }

            Add(EventKind.OperationDone, operation.InstanceId, operation: operation, hidden: reported);
        }

        /// <summary>
        /// A reply carried this stop, so the model has it. Matched by identity: the
        /// entry holds the same object the bus handed out.
        /// </summary>
        public void Delivered(StopEvent stop)
        {
            if (stop == null) return;

            lock (_gate)
            {
                var entry = _entries.LastOrDefault(e => ReferenceEquals(e.Stop, stop));
                if (entry == null) return;
                entry.Hidden = true;
                entry.Seen = true;
            }
        }

        /// <summary>
        /// A reply carried this operation's outcome. The push may already have landed,
        /// or may be milliseconds behind the reply, so both orders are covered.
        /// </summary>
        public void OperationReported(string operationId)
        {
            if (string.IsNullOrEmpty(operationId)) return;

            lock (_gate)
            {
                var entry = _entries.LastOrDefault(e =>
                    e.Kind == EventKind.OperationDone && e.Operation?.OperationId == operationId);
                if (entry != null)
                {
                    entry.Hidden = true;
                    entry.Seen = true;
                    return;
                }

                _reported.AddLast(operationId);
                while (_reported.Count > ReportedKept) _reported.RemoveFirst();
            }
        }

        static bool Same(Expectation expectation, string instanceId, Expected kind) =>
            expectation.Kind == kind &&
            string.Equals(expectation.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase);

        /// <summary>Called with the lock held.</summary>
        bool Consume(string instanceId, Expected kind)
        {
            _expectations.RemoveAll(e => e.Deadline <= _now());

            var index = _expectations.FindIndex(e => Same(e, instanceId, kind));
            if (index < 0) return false;

            _expectations.RemoveAt(index);
            return true;
        }

        /// <summary>Called with the lock held.</summary>
        bool Ending(string instanceId) =>
            _expectations.Any(e => Same(e, instanceId, Expected.End) && e.Deadline > _now());

        static bool IsDesign(string mode) =>
            string.Equals(mode, DebugModes.Design, StringComparison.OrdinalIgnoreCase);

        static bool Matches(string instanceId, string entryInstanceId) =>
            string.IsNullOrEmpty(instanceId) ||
            string.Equals(instanceId, entryInstanceId, StringComparison.OrdinalIgnoreCase);
    }
}
