using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
        readonly Dictionary<string, string> _modes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, long> _sessionStart = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> _generations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _startingRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, StopEvent> _stoppedAt = new Dictionary<string, StopEvent>(StringComparer.OrdinalIgnoreCase);
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

            lock (_gate)
            {
                stop.Seq = ++_seq;
                stop.Generation = GenerationFor(stop);
                _buffer.AddLast(stop);
                while (_buffer.Count > BufferSize) _buffer.RemoveFirst();

                // A stop is the only thing that says the debuggee is sitting still, and
                // it is pushed as it happens. The mode notification is not: it arrives
                // seconds after the change it announces, so a break it reports can
                // belong to a stop the caller has already resumed from.
                if (IsBreak(stop.Mode)) _stoppedAt[stop.InstanceId ?? ""] = stop;
                else _stoppedAt.Remove(stop.InstanceId ?? "");

                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    var w = _waiters[i];
                    if (!Matches(w.InstanceId, stop.InstanceId)) continue;

                    _waiters.RemoveAt(i);

                    // Handing the stop over and moving the cursor past it are one step,
                    // still under the lock. A waiter that gave up at this instant never
                    // receives it, and a cursor moved for a waiter that got nothing hides
                    // the stop from every later wait. Continuations here are queued,
                    // never run on this thread.
                    if (w.Completion.TrySetResult(stop)) _cursor = stop.Seq;
                }
            }
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
                var buffered = _buffer.FirstOrDefault(e =>
                    e.Seq > _cursor && InCurrentSession(e) && Matches(instanceId, e.InstanceId));
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

            return await Waiting.ForAsync(
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

                    // Handing it over and marking it reported are one step, the same rule
                    // the stop stream follows: a load marked reported for a waiter that
                    // gave up is one nobody can wait for any more.
                    if (w.Completion.TrySetResult(module)) entry.Reported = true;
                }
            }
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
                    var load = already.Load;
                    return new ModuleLoadEvent { InstanceId = load.InstanceId, Name = load.Name, Path = load.Path,
                        SymbolsLoaded = load.SymbolsLoaded, SymbolStatus = load.SymbolStatus, AlreadyLoaded = true };
                }

                waiter = new ModuleWaiter
                {
                    InstanceId = instanceId,
                    Pattern = pattern,
                    Completion = new TaskCompletionSource<ModuleLoadEvent>(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                _moduleWaiters.Add(waiter);
            }

            return await Waiting.ForAsync(
                waiter.Completion,
                () => { lock (_gate) _moduleWaiters.Remove(waiter); },
                timeout, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Debug pane lines kept for a wait to match against. Deeper than the stop
        /// buffer because a program prints far more than it stops.
        /// </summary>
        const int OutputBufferSize = 2000;

        readonly LinkedList<OutputLine> _output = new LinkedList<OutputLine>();
        readonly List<OutputWaiter> _outputWaiters = new List<OutputWaiter>();
        readonly List<Action<string, string>> _outputSubscribers = new List<Action<string, string>>();

        sealed class OutputLine
        {
            public string InstanceId;
            public string Text;

            /// <summary>A wait has been answered with this line. One line, one answer.</summary>
            public bool Reported;

            /// <summary>
            /// No longer in the buffer, because the run it belonged to is over.
            ///
            /// Matching happens outside the lock, so a reader is holding this object for
            /// as long as a match takes and ClearOutput can drop it from the buffer
            /// meanwhile. The reader has only what is on the object to go by, so the
            /// removal is recorded here rather than left implicit in the list.
            /// </summary>
            public bool Cleared;
        }

        /// <summary>
        /// Whether this line is still there to be answered with. Both flags matter and
        /// neither is enough alone: between a reader's snapshot and its claim the line
        /// may have answered somebody else, or the run it belonged to may have ended and
        /// taken it out of the buffer. Read with the lock held.
        /// </summary>
        static bool Available(OutputLine line) => !line.Reported && !line.Cleared;

        sealed class OutputWaiter
        {
            public string InstanceId;
            public Regex Pattern;
            public TaskCompletionSource<string> Completion;
        }

        /// <summary>
        /// Lines the debuggee and the engine printed to the Debug pane, split as they
        /// arrive. Nobody waiting for a stop hears about them: a program that prints is
        /// not a program that stopped, and in C++ it prints a line per module load.
        ///
        /// Matching runs outside the lock. The pattern is the caller's and each match is
        /// allowed a second, while the lock is held by every stop, every other line and -
        /// through the predicate the log's own wait passes in - the digest on every tool
        /// call. One pattern that backtracks would otherwise stall the whole shim.
        /// </summary>
        public void PublishOutput(OutputEvent output)
        {
            if (output == null || string.IsNullOrEmpty(output.Text)) return;

            var added = new List<OutputLine>();
            List<OutputWaiter> waiting;
            List<Action<string, string>> toNotify = null;

            lock (_gate)
            {
                foreach (var raw in output.Text.Split('\n'))
                {
                    var text = raw.TrimEnd('\r');
                    if (text.Length == 0) continue;

                    var line = new OutputLine { InstanceId = output.InstanceId, Text = text };
                    _output.AddLast(line);
                    while (_output.Count > OutputBufferSize) _output.RemoveFirst();
                    added.Add(line);
                }

                waiting = new List<OutputWaiter>(_outputWaiters);
                if (_outputSubscribers.Count > 0) toNotify = new List<Action<string, string>>(_outputSubscribers);
            }

            foreach (var waiter in waiting)
            {
                foreach (var line in added)
                {
                    if (!Matches(waiter.InstanceId, line.InstanceId)) continue;
                    if (!IsMatch(waiter.Pattern, line.Text)) continue;

                    lock (_gate)
                    {
                        // Everything is read again here, because all of it can have moved
                        // while the match ran: the line may have answered somebody else or
                        // gone with the run that ended, and this waiter may have been
                        // answered or have given up.
                        if (!Available(line)) continue;
                        if (!_outputWaiters.Remove(waiter)) break;

                        // Handing the line over and marking it answered are one step. A
                        // waiter cancelled at this instant never receives it, and a line
                        // marked answered for nobody is lost to every other waiter.
                        if (waiter.Completion.TrySetResult(line.Text)) line.Reported = true;
                    }
                    break;
                }
            }

            if (toNotify != null)
                foreach (var subscriber in toNotify)
                    foreach (var line in added)
                    {
                        try { subscriber(output.InstanceId, line.Text); } catch { }
                    }
        }

        /// <summary>
        /// The first line matching that nobody has been given yet, out of the buffer or
        /// the next one to arrive. Null on timeout. A line is answered with once,
        /// whatever pattern matched it, the same rule the module stream uses.
        ///
        /// The waiter is registered before the buffer is searched, because the search
        /// runs outside the lock - up to a second of matching, which nothing else here
        /// can afford to queue behind. Registering first means a line arriving during the
        /// search is caught by the publisher instead of falling between the two.
        /// </summary>
        public async Task<string> WaitForOutputAsync(string instanceId, Regex pattern, TimeSpan timeout, CancellationToken ct)
        {
            OutputWaiter waiter;
            List<OutputLine> buffered;

            lock (_gate)
            {
                waiter = new OutputWaiter
                {
                    InstanceId = instanceId,
                    Pattern = pattern,
                    Completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                _outputWaiters.Add(waiter);
                buffered = _output.Where(l => Available(l) && Matches(instanceId, l.InstanceId)).ToList();
            }

            foreach (var line in buffered)
            {
                if (!IsMatch(pattern, line.Text)) continue;

                lock (_gate)
                {
                    // While this was matching, the line may have answered somebody else or
                    // gone with the run that ended, and a publisher may already have
                    // answered this waiter. Only the reader that takes the waiter out of
                    // the list owns the line, and only a line still in the buffer counts.
                    if (!Available(line)) continue;
                    if (!_outputWaiters.Remove(waiter)) break;
                    line.Reported = true;
                }
                return line.Text;
            }

            return await Waiting.ForAsync(
                waiter.Completion,
                () => { lock (_gate) _outputWaiters.Remove(waiter); },
                timeout, ct).ConfigureAwait(false);
        }

        /// <summary>Every line as it arrives. --follow prints from here.</summary>
        public void SubscribeOutput(Action<string, string> handler)
        {
            if (handler == null) return;
            lock (_gate) _outputSubscribers.Add(handler);
        }

        /// <summary>Where this instance's debugger was last seen. Empty when it is unknown.</summary>
        public string ModeOf(string instanceId)
        {
            lock (_gate) return _modes.TryGetValue(instanceId ?? "", out var mode) ? mode : null;
        }

        /// <summary>
        /// A pattern the caller wrote, on a line the debuggee wrote. The timeout is on
        /// the expression itself; a pattern that cannot finish counts as no match.
        /// </summary>
        static bool IsMatch(Regex pattern, string text)
        {
            if (pattern == null) return true;
            try { return pattern.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        /// <summary>
        /// Lines belong to a debug session. Cleared where a run begins, so "Server
        /// listening" from the run before this one can never answer a wait on this one.
        /// Called with the lock held.
        /// </summary>
        void ClearOutput(string instanceId)
        {
            for (var node = _output.First; node != null;)
            {
                var next = node.Next;
                if (Matches(instanceId, node.Value.InstanceId))
                {
                    // Stamped as it goes. A reader matching outside the lock is holding
                    // this line and would otherwise still see something unreported and
                    // answer this run's wait with the last run's output.
                    node.Value.Cleared = true;
                    _output.Remove(node);
                }
                node = next;
            }
        }

        /// <summary>
        /// Moves the cursor past everything currently buffered. Called when a tool
        /// resumes execution, so the next wait() reports the coming stop rather than
        /// the one that is already history.
        ///
        /// It also forgets where the named instance was sitting, because the caller has
        /// just asked it to run and where it used to be is no longer where it is. The
        /// cursor is the whole bus's; the instance only says whose stop to forget.
        /// </summary>
        public void MarkSeen(string instanceId)
        {
            lock (_gate)
            {
                _cursor = _seq;
                if (string.IsNullOrEmpty(instanceId)) _stoppedAt.Clear();
                else _stoppedAt.Remove(instanceId);
            }
        }

        /// <summary>
        /// Told what mode an instance's debugger is in, every time it changes.
        ///
        /// Leaving design mode begins a debug session, and whatever is still buffered
        /// belongs to one that is over. After the debuggee was restarted and the
        /// debugger re-attached, the first wait() answered with the previous process
        /// exiting, which reads as the current target having died.
        ///
        /// The rule is here rather than in attach, launch and dump_open because each of
        /// them would have to remember it and the next way into a session would not.
        /// Only a debugger seen sitting in design mode counts as one having ended, so a
        /// stop buffered before this ever heard from the instance is never discarded.
        ///
        /// Module loads are left alone: they arrive while a program is starting, and
        /// dropping one that came in ahead of the mode change would leave a wait for a
        /// module that is already there sitting out its timeout.
        ///
        /// Sitting in design mode is also what arms the generation counter, so leaving it
        /// counts a run.
        /// </summary>
        public void InitializeMode(string instanceId, string mode)
        {
            lock (_gate)
            {
                if (_modes.ContainsKey(instanceId))
                {
                    // Events may have been lost while disconnected. Conservatively
                    // invalidate identities and stale stops before accepting callbacks.
                    _generations[instanceId] = GenerationOf(instanceId) + 1;
                    _sessionStart[instanceId] = _seq;
                    _startingRun.Remove(instanceId);
                    _stoppedAt.Remove(instanceId);
                    for (var node = _modules.First; node != null;)
                    {
                        var next = node.Next;
                        if (Matches(instanceId, node.Value.Load.InstanceId)) _modules.Remove(node);
                        node = next;
                    }
                }
                _modes[instanceId] = mode ?? DebugModes.Design;
            }
        }

        public void ObserveStopMode(string instanceId, string mode)
        {
            lock (_gate)
            {
                // A first breakpoint can arrive before the delayed mode notification.
                // Do not consume restart's marker on its old process's exit.
                if (!string.IsNullOrEmpty(mode) && !IsDesign(mode) &&
                    _modes.TryGetValue(instanceId, out var previous) && IsDesign(previous))
                    ModeChanged(instanceId, mode);
            }
        }

        public void ModeChanged(string instanceId, string mode)
        {
            if (string.IsNullOrEmpty(instanceId) || string.IsNullOrEmpty(mode)) return;

            lock (_gate)
            {
                var wasIdle = _modes.TryGetValue(instanceId, out var previous) && IsDesign(previous);
                _modes[instanceId] = mode;

                if (wasIdle && !IsDesign(mode))
                {
                    _sessionStart[instanceId] = _seq;

                    // A run this session started has already cleared these. One started
                    // from the IDE is announced late, so its first lines can be lost;
                    // that is cheaper than answering with the previous run's.
                    ClearOutput(instanceId);
                }

                // Leaving design mode is the debugger's own account of a run beginning.
                // When a tool already said one was starting, this is that same run
                // arriving late and it must not be counted twice.
                if (!IsDesign(mode) && !_startingRun.Remove(instanceId) && wasIdle)
                    _generations[instanceId] = GenerationOf(instanceId) + 1;

                // Running or no session at all takes the belief away; break never
                // establishes it, because this notification is late enough that the
                // break it reports can be one the caller has already resumed from.
                if (!IsBreak(mode)) _stoppedAt.Remove(instanceId);
            }
        }

        /// <summary>
        /// A tool is starting a run in this instance. Counted here and now, before the
        /// call goes out, so the first stop of the new run already carries the new
        /// number.
        ///
        /// Waiting for the mode notification instead would have got that wrong in the
        /// dangerous direction: it arrives seconds after the change it announces, so a
        /// restart's first stops would have worn the previous run's number while a reply
        /// told the caller to compare numbers before comparing addresses. The
        /// notification for the same run arrives afterwards and is not counted again.
        ///
        /// It also cannot be left to the notification alone in the other direction: a
        /// window that has sat in design mode since before the shim connected never
        /// announces it, so its first run would go unnumbered.
        ///
        /// Attaching may join a session rather than start one, and nothing here knows
        /// which it did. It costs a number that labels no restart, and its reply says so.
        /// </summary>
        public void StartingRun(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;

            lock (_gate)
            {
                _generations[instanceId] = GenerationOf(instanceId) + 1;
                _startingRun.Add(instanceId);
                _stoppedAt.Remove(instanceId);
                ClearOutput(instanceId);
            }
        }

        /// <summary>
        /// The call that was starting a run failed, so no mode change is coming to
        /// confirm it. Without this the next run somebody starts from the IDE would be
        /// taken for the confirmation and go uncounted.
        ///
        /// The number stays where it went. It only ever moves forward, and one that
        /// labels no run costs a re-read, while moving it back would make two different
        /// runs share a number.
        /// </summary>
        public void RunNotStarted(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            lock (_gate) _startingRun.Remove(instanceId);
        }

        /// <summary>
        /// Which run of this instance's debuggee is current. Zero when nothing here saw
        /// one begin.
        ///
        /// The same number means the same run. A different one does not always mean a
        /// different run - joining a session, or a mode change arriving out of order, can
        /// move it without a restart - so it is read as "everything has to be read
        /// again", never as proof that something restarted. A pid, a thread id, an
        /// address and a container reference mean something only under the number they
        /// were read under. Nothing here checks that; the number is published so a caller
        /// can.
        /// </summary>
        public int Generation(string instanceId)
        {
            lock (_gate) return GenerationOf(instanceId);
        }

        int GenerationOf(string instanceId) =>
            _generations.TryGetValue(instanceId ?? "", out var generation) ? generation : 0;

        /// <summary>
        /// The run a stop belongs to, which is not always the run that is current.
        ///
        /// Restarting ends one run and begins another, and the old process's exit
        /// arrives after the new number has been taken. Stamped with the current number
        /// it said the process that just died and the process now running were the same
        /// run - the one thing this number exists to rule out, wrong in the direction
        /// that misleads.
        ///
        /// So an exit that lands while a run is still starting is counted against the
        /// run that ended. A new run that dies immediately is labelled one too early by
        /// this, which costs a reader a re-read; the other way costs them a comparison
        /// they should never have made.
        ///
        /// Called with the lock already held.
        /// </summary>
        int GenerationFor(StopEvent stop)
        {
            var current = GenerationOf(stop.InstanceId);

            if (stop.Reason != StopReason.Exited) return current;
            if (!_startingRun.Contains(stop.InstanceId ?? "")) return current;

            // Zero is what a run nobody saw begin already reports, so an exit from
            // before this ever heard of the instance stays unnumbered rather than
            // borrowing the number in front of it.
            return current > 0 ? current - 1 : 0;
        }

        /// <summary>
        /// Where each of these instances is sitting, when every one of them stopped and
        /// nothing has asked it to run since, and there is nothing buffered to hand out.
        /// A wait on them could then only sit out its timeout. Empty when any of them
        /// may still be running.
        ///
        /// Only a stop event establishes this. The mode notification arrives seconds
        /// after the change it announces, so a break it reports can belong to a stop the
        /// caller has already resumed from; run and design are believed, because
        /// believing them costs a wait that blocks the way it always did.
        ///
        /// Break mode is a property of the whole Visual Studio window rather than of one
        /// process. With a launcher and the editor it starts in one session, either one
        /// stopping puts the window in break while the other keeps running. This is the
        /// floor, not a proof, and the reply says so.
        /// </summary>
        public IReadOnlyList<StopEvent> AlreadyStopped(string instanceId, IReadOnlyList<string> instances)
        {
            if (instances == null || instances.Count == 0) return Array.Empty<StopEvent>();

            lock (_gate)
            {
                // A stop nobody has been given yet is the answer to the wait, not this.
                if (_buffer.Any(e => e.Seq > _cursor && InCurrentSession(e) && Matches(instanceId, e.InstanceId)))
                    return Array.Empty<StopEvent>();

                var sitting = new List<StopEvent>();
                foreach (var id in instances)
                {
                    if (!_stoppedAt.TryGetValue(id ?? "", out var stop)) return Array.Empty<StopEvent>();
                    sitting.Add(stop);
                }
                return sitting;
            }
        }

        public StopEvent Latest(string instanceId)
        {
            lock (_gate) return _buffer.LastOrDefault(e => Matches(instanceId, e.InstanceId));
        }

        /// <summary>
        /// Whether the stop belongs to the debug session that is running now. An older
        /// one names a process that is already gone, and an exit is the dangerous case:
        /// read plainly it says the thing being debugged has stopped existing.
        /// </summary>
        bool InCurrentSession(StopEvent stop) =>
            !_sessionStart.TryGetValue(stop.InstanceId ?? "", out var start) || stop.Seq > start;

        static bool IsDesign(string mode) =>
            string.Equals(mode, DebugModes.Design, StringComparison.OrdinalIgnoreCase);

        static bool IsBreak(string mode) =>
            string.Equals(mode, DebugModes.Break, StringComparison.OrdinalIgnoreCase);

        static bool Matches(string instanceId, string eventInstanceId) =>
            string.IsNullOrEmpty(instanceId) ||
            string.Equals(instanceId, eventInstanceId, StringComparison.OrdinalIgnoreCase);

        /// <summary>Any part of the module's name, ignoring case, the way modules() filters.</summary>
        static bool NameContains(string name, string pattern) =>
            string.IsNullOrEmpty(pattern) ||
            (name != null && name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
