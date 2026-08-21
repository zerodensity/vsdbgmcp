using System;
using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Listens to the debug engine directly.
    ///
    /// Mode changes tell you that execution stopped; they do not tell you why. The
    /// reason - which breakpoint, which exception, a completed step, the process
    /// exiting - only exists at this level, and it is what makes a single wait call
    /// worth more than a polling loop.
    /// </summary>
    sealed class DebugEventSink : IDebugEventCallback2
    {
        readonly Action<string> _log;
        bool _advised;
        IDebugProgram2 _destroyed;

        public DebugEventSink(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        readonly List<IDebugProgram2> _programs = new List<IDebugProgram2>();
        readonly object _programGate = new object();

        public event Action<StopEvent> StopOccurred;
        public event Action<OutputEvent> OutputOccurred;

        /// <summary>The thread that last stopped. Expression evaluation runs against it.</summary>
        public IDebugThread2 CurrentThread { get; private set; }

        /// <summary>The program the last event came from.</summary>
        public IDebugProgram2 CurrentProgram { get; private set; }

        /// <summary>
        /// Every program being debugged, not just the one that last stopped.
        ///
        /// A session can hold several: a launcher and the editor it starts, a host and
        /// its workers. Tracking only the latest makes every thread outside it invisible,
        /// which looks to a caller like the debugger denying threads it can see.
        /// </summary>
        public List<IDebugProgram2> Programs
        {
            get { lock (_programGate) return new List<IDebugProgram2>(_programs); }
        }

        /// <summary>
        /// Whether this program is still part of the session.
        ///
        /// A thread pointer outlives the process it came from, so one process ending
        /// while another stays stopped leaves a pointer behind that still answers
        /// questions. Asking here is how a reader tells that apart from a live frame.
        /// </summary>
        public bool Knows(IDebugProgram2 program)
        {
            if (program == null) return false;
            lock (_programGate)
            {
                foreach (var known in _programs)
                {
                    if (ReferenceEquals(known, program)) return true;
                    if (SameProgram(known, program)) return true;
                }
                return false;
            }
        }

        void Remember(IDebugProgram2 program)
        {
            if (program == null) return;
            lock (_programGate)
            {
                foreach (var known in _programs)
                {
                    if (ReferenceEquals(known, program)) return;
                    if (SameProgram(known, program)) return;
                }
                _programs.Add(program);
            }
        }

        void Forget(IDebugProgram2 program)
        {
            if (program == null) return;
            lock (_programGate)
            {
                for (var i = _programs.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(_programs[i], program) || SameProgram(_programs[i], program))
                        _programs.RemoveAt(i);
                }
            }
        }

        static bool SameProgram(IDebugProgram2 a, IDebugProgram2 b)
        {
            var first = Guid.Empty;
            var second = Guid.Empty;
            if (a.GetProgramId(out first) != VSConstants.S_OK) return false;
            if (b.GetProgramId(out second) != VSConstants.S_OK) return false;
            return first == second;
        }

        public ExceptionInfo LastException { get; private set; }

        public void Advise(IVsDebugger debugger)
        {
            if (debugger == null || _advised) return;
            debugger.AdviseDebugEventCallback(this);
            _advised = true;
        }

        public void Unadvise(IVsDebugger debugger)
        {
            if (debugger == null || !_advised) return;
            debugger.UnadviseDebugEventCallback(this);
            _advised = false;
        }

        public int Event(IDebugEngine2 engine, IDebugProcess2 process, IDebugProgram2 program,
            IDebugThread2 thread, IDebugEvent2 debugEvent, ref Guid riidEvent, uint attributes)
        {
            if (program != null)
            {
                CurrentProgram = program;
                Remember(program);
            }
            if (thread != null) CurrentThread = thread;
            _destroyed = program;

            // This is a callback from the debug engine. Letting an exception cross back
            // into it would take down the debug session, so this one boundary catches
            // everything and writes it where it can be read.
            try
            {
                Dispatch(thread, debugEvent, riidEvent, attributes);
            }
            catch (Exception ex)
            {
                _log("event handler: " + ex);
            }

            // Notification only. Visual Studio owns continuation; calling Continue here
            // would resume the debuggee behind the user's back.
            return VSConstants.S_OK;
        }

        /// <summary>
        /// The engine says whether an event stops execution. Asking it is the only
        /// reliable way to tell a crash that halts the program from one of the many
        /// first-chance exceptions a C++ program throws and handles on its own.
        /// </summary>
        static bool Stops(uint attributes) =>
            (attributes & (uint)enum_EVENTATTRIBUTES.EVENT_STOPPING) != 0;

        void Dispatch(IDebugThread2 thread, IDebugEvent2 debugEvent, Guid riid, uint attributes)
        {
            if (riid == typeof(IDebugOutputStringEvent2).GUID)
            {
                if (debugEvent is IDebugOutputStringEvent2 output &&
                    output.GetString(out var text) == VSConstants.S_OK && !string.IsNullOrEmpty(text))
                {
                    OutputOccurred?.Invoke(new OutputEvent { Pane = "Debug", Text = text });
                }
                return;
            }

            if (riid == typeof(IDebugModuleLoadEvent2).GUID)
            {
                if (debugEvent is IDebugModuleLoadEvent2 moduleLoad)
                {
                    IDebugModule2 module = null;
                    string message = null;
                    var loaded = 0;
                    moduleLoad.GetModule(out module, ref message, ref loaded);
                    if (!string.IsNullOrEmpty(message))
                        OutputOccurred?.Invoke(new OutputEvent { Pane = "Debug", Text = message });
                }
                return;
            }

            // The process going away is not a "stopping" event, but it is certainly
            // something a waiter needs to hear about, so it is handled before the gate.
            if (riid == typeof(IDebugProgramDestroyEvent2).GUID)
            {
                uint exitCode = 0;
                (debugEvent as IDebugProgramDestroyEvent2)?.GetExitCode(out exitCode);

                var identity = ProcessIdentity.Of(_destroyed);
                Forget(_destroyed);

                // Only clear the current pointers if it was this program that ended;
                // another process in the session may still be stopped and inspectable.
                lock (_programGate)
                {
                    if (_programs.Count == 0)
                    {
                        CurrentThread = null;
                        CurrentProgram = null;
                    }
                    else if (CurrentProgram == null || !_programs.Contains(CurrentProgram))
                    {
                        CurrentProgram = _programs[_programs.Count - 1];
                        CurrentThread = null;
                    }
                }

                StopOccurred?.Invoke(new StopEvent
                {
                    Reason = StopReason.Exited,
                    ExitCode = unchecked((int)exitCode),
                    ProcessName = identity.Name,
                    Pid = identity.Pid,
                    Mode = DebugModes.Design
                });
                return;
            }

            if (riid == typeof(IDebugExceptionEvent2).GUID)
            {
                var exception = ReadException(debugEvent as IDebugExceptionEvent2, Stops(attributes));
                if (exception != null) LastException = exception;
                if (Stops(attributes)) Raise(StopReason.Exception, thread, exception);
                return;
            }

            if (!Stops(attributes)) return;

            if (riid == typeof(IDebugBreakpointEvent2).GUID) Raise(StopReason.Breakpoint, thread);
            else if (riid == typeof(IDebugStepCompleteEvent2).GUID) Raise(StopReason.Step, thread);
            else if (riid == typeof(IDebugBreakEvent2).GUID) Raise(StopReason.Pause, thread);

            // The entry point is deliberately not reported. The engine raises it as a
            // stopping event and then continues on its own, so treating it as a stop
            // makes every launch look like it halted at the first line of main when it
            // did nothing of the sort. A caller who asked to stop at entry gets a step
            // completion instead, which is a real stop.
        }

        void Raise(string reason, IDebugThread2 thread, ExceptionInfo exception = null)
        {
            var identity = ProcessIdentity.Of(thread);
            StopOccurred?.Invoke(new StopEvent
            {
                Reason = reason,
                Exception = exception,
                ThreadId = ThreadId(thread),
                ProcessName = identity.Name,
                Pid = identity.Pid,
                Frame = FrameReader.TopFrame(thread),
                Mode = DebugModes.Break
            });
        }

        static int ThreadId(IDebugThread2 thread)
        {
            if (thread == null) return 0;
            return thread.GetThreadId(out var id) == VSConstants.S_OK ? unchecked((int)id) : 0;
        }

        static ExceptionInfo ReadException(IDebugExceptionEvent2 evt, bool stopping)
        {
            if (evt == null) return null;

            var info = new EXCEPTION_INFO[1];
            if (evt.GetException(info) != VSConstants.S_OK) return null;

            evt.GetExceptionDescription(out var description);

            var code = "0x" + info[0].dwCode.ToString("X8");
            var name = info[0].bstrExceptionName;

            // Native exceptions often report the code as their name; saying it twice
            // helps nobody.
            if (string.Equals(name, code, StringComparison.OrdinalIgnoreCase)) name = null;

            return new ExceptionInfo
            {
                Code = code,
                Name = name,
                Message = Tidy(description),

                // Whether the debugger stopped is the fact that matters. The state flags
                // describe how it was configured to react, not what happened.
                FirstChance = !stopping
            };
        }

        static string Tidy(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return text.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
