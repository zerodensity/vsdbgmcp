using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using VsDbgMcp.Shim.Discovery;
using VsDbgMcp.Shim.Session;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Waiting for a stop that cannot come.
    ///
    /// Twice in one session a caller removed a breakpoint, set another, and waited
    /// without resuming. wait answered "execution did not stop within the timeout. Still
    /// running." and the silence was reported as a function having stopped being called.
    /// Both halves were wrong: nothing was running, and nothing had been checked.
    /// </summary>
    public class WaitingWhileStoppedTests
    {
        static StopEvent AtBreakpoint(string instance) => new StopEvent
        {
            InstanceId = instance,
            Reason = StopReason.Breakpoint,
            BreakpointId = 3,
            ProcessName = "host.exe",
            Pid = 1724,
            ThreadId = 6120,
            Frame = new Frame { Index = 0, Function = "ProcessCopies", File = @"D:\repo\copy.cpp", Line = 88 },
            Mode = DebugModes.Break
        };

        static string[] Only(string instance) => new[] { instance };

        static async Task Take(EventBus bus, string instance) =>
            await bus.WaitAsync(instance, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        [Fact]
        public async Task An_instance_that_stopped_and_was_never_resumed_is_still_stopped()
        {
            var bus = new EventBus();
            bus.Publish(AtBreakpoint("Engine#1"));
            await Take(bus, "Engine#1");

            var sitting = bus.AlreadyStopped("Engine#1", Only("Engine#1"));

            Assert.Single(sitting);
            Assert.Equal("ProcessCopies", sitting[0].Frame.Function);
        }

        /// <summary>
        /// The stop the caller has not been given yet is the answer to the wait. Saying
        /// "you are already stopped" instead would swallow it.
        /// </summary>
        [Fact]
        public void A_stop_still_waiting_to_be_handed_out_comes_first()
        {
            var bus = new EventBus();
            bus.Publish(AtBreakpoint("Engine#1"));

            Assert.Empty(bus.AlreadyStopped("Engine#1", Only("Engine#1")));
        }

        [Fact]
        public async Task Resuming_makes_the_next_wait_wait_again()
        {
            var bus = new EventBus();
            bus.Publish(AtBreakpoint("Engine#1"));
            await Take(bus, "Engine#1");

            bus.MarkSeen("Engine#1");

            Assert.Empty(bus.AlreadyStopped("Engine#1", Only("Engine#1")));
        }

        /// <summary>
        /// The mode notification arrives seconds after the change it announces, so a
        /// break it reports can belong to a stop the caller has already resumed from.
        /// Only a stop event, which is pushed as it happens, establishes this.
        /// </summary>
        [Fact]
        public void Being_told_the_mode_is_break_is_not_enough()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Break);

            Assert.Empty(bus.AlreadyStopped("Engine#1", Only("Engine#1")));
        }

        [Fact]
        public async Task Being_told_the_debuggee_is_running_takes_it_away()
        {
            var bus = new EventBus();
            bus.Publish(AtBreakpoint("Engine#1"));
            await Take(bus, "Engine#1");

            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.Empty(bus.AlreadyStopped("Engine#1", Only("Engine#1")));
        }

        /// <summary>A process that has exited is not one sitting in break.</summary>
        [Fact]
        public async Task An_exit_leaves_nothing_sitting_in_break()
        {
            var bus = new EventBus();
            bus.Publish(new StopEvent
            {
                InstanceId = "Engine#1",
                Reason = StopReason.Exited,
                ProcessName = "host.exe",
                Pid = 1724,
                ExitCode = 0,
                Mode = DebugModes.Design
            });
            await Take(bus, "Engine#1");

            Assert.Empty(bus.AlreadyStopped("Engine#1", Only("Engine#1")));
        }

        [Fact]
        public async Task One_window_running_keeps_a_wait_on_any_instance_waiting()
        {
            var bus = new EventBus();
            bus.Publish(AtBreakpoint("Engine#1"));
            bus.Publish(AtBreakpoint("Editor#2"));
            await Take(bus, "Engine#1");
            await Take(bus, "Editor#2");

            // One of the two was resumed, so a stop can still come from it.
            bus.ModeChanged("Editor#2", DebugModes.Run);

            Assert.Empty(bus.AlreadyStopped(null, new[] { "Engine#1", "Editor#2" }));
        }

        [Fact]
        public async Task Every_window_stopped_answers_a_wait_on_any_instance_at_once()
        {
            var bus = new EventBus();
            bus.Publish(AtBreakpoint("Engine#1"));
            bus.Publish(AtBreakpoint("Editor#2"));
            await Take(bus, "Engine#1");
            await Take(bus, "Editor#2");

            Assert.Equal(2, bus.AlreadyStopped(null, new[] { "Engine#1", "Editor#2" }).Count);
        }

        /// <summary>
        /// A window nothing has ever been heard from may be running, so it is never
        /// counted as sitting still.
        /// </summary>
        [Fact]
        public async Task A_window_nothing_is_known_about_keeps_the_wait_waiting()
        {
            var bus = new EventBus();
            bus.Publish(AtBreakpoint("Engine#1"));
            await Take(bus, null);

            Assert.Empty(bus.AlreadyStopped(null, new[] { "Engine#1", "Editor#2" }));
        }

        [Fact]
        public void A_timeout_claims_nothing_about_the_debuggee()
        {
            var text = Render.Stop(null);

            Assert.Contains("timeout", text);
            Assert.DoesNotContain("Still running", text);
            Assert.Contains("unknown", text);
            Assert.Contains("not queried", text);
        }

        [Fact]
        public void The_reply_says_break_belongs_to_the_window_and_not_to_one_process()
        {
            var text = Render.AlreadyStopped(new[] { AtBreakpoint("Engine#1") });

            Assert.Contains("go", text);
            Assert.Contains("ProcessCopies", text);
            Assert.Contains("whole Visual Studio window", text);
        }
    }

    /// <summary>
    /// The same fault through the real tools over a real pipe, which is where the
    /// retrospective met it.
    /// </summary>
    public class WaitingWhileStoppedOverThePipeTests : IDisposable
    {
        readonly string _dir;
        readonly FakeHost _host;
        readonly SessionManager _sessions;

        public WaitingWhileStoppedOverThePipeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-sitting-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var pipe = "vsdbgmcp-sitting-" + Guid.NewGuid().ToString("N");
            _host = new FakeHost(pipe) { InstanceId = Process.GetCurrentProcess().Id.ToString() };
            _host.Start();

            File.WriteAllText(
                Path.Combine(_dir, Names.InstanceFilePrefix + Process.GetCurrentProcess().Id + Names.InstanceFileSuffix),
                InstanceFile.Serialize(new InstanceRecord
                {
                    Pid = Process.GetCurrentProcess().Id,
                    Pipe = pipe,
                    Token = "t",
                    Contract = Names.ContractVersion,
                    DebugMode = DebugModes.Break,
                    Workspace = new WorkspaceInfo
                    {
                        Kind = WorkspaceKind.Sln,
                        Root = @"D:\repo",
                        File = @"D:\repo\App.sln",
                        Name = "App"
                    }
                }));

            _sessions = new SessionManager(@"D:\repo", new InstanceStore(_dir));
        }

        public void Dispose()
        {
            _sessions.Dispose();
            _host.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        Task Connect() => new LifecycleTools(_sessions).Status(null, CancellationToken.None);

        [Fact]
        public async Task A_launch_retry_preserves_the_stop_and_the_next_real_run_advances_generation()
        {
            await Connect();
            _host.RaiseModeChange(DebugModes.Design);
            _host.RaiseModeChange(DebugModes.Run);
            StopAtBreakpoint();
            var wait = new ExecutionTools(_sessions);
            await wait.Wait(1, structured: true);
            var generation = _sessions.Events.Generation(_host.InstanceId);
            await new LifecycleTools(_sessions).Launch(requestId: "same-successful-operation");
            await new LifecycleTools(_sessions).Launch(requestId: "same-successful-operation");
            Assert.Equal(generation, _sessions.Events.Generation(_host.InstanceId));
            var stillStopped = Newtonsoft.Json.Linq.JObject.Parse(await wait.Wait(1, structured: true));
            Assert.Equal("already-stopped", (string)stillStopped["outcome"]);
            _host.RaiseModeChange(DebugModes.Design);
            _host.RaiseModeChange(DebugModes.Run);
            Assert.Equal(generation + 1, _sessions.Events.Generation(_host.InstanceId));
        }

        [Fact]
        public async Task Structured_stop_repeats_and_buffered_modules_are_always_json()
        {
            await Connect();
            StopAtBreakpoint();
            var wait = new ExecutionTools(_sessions);
            var first = Newtonsoft.Json.Linq.JObject.Parse(await wait.Wait(1, structured: true));
            var repeat = Newtonsoft.Json.Linq.JObject.Parse(await wait.Wait(1, structured: true));
            Assert.Equal("event", (string)first["outcome"]);
            Assert.True((bool)first["eventReceived"]);
            Assert.Equal("already-stopped", (string)repeat["outcome"]);
            Assert.False((bool)repeat["eventReceived"]);
            _host.RaiseModuleLoad(new ModuleLoadEvent { Name = "plugin.dll", SymbolsLoaded = true });
            var module = Newtonsoft.Json.Linq.JObject.Parse(await wait.Wait(1, "module:plugin", structured: true));
            Assert.Equal("already-loaded", (string)module["outcome"]);
            Assert.False((bool)module["eventReceived"]);
            Assert.Equal("plugin.dll", (string)module["module"]["Name"]);
        }

        [Fact]
        public async Task A_first_breakpoint_before_the_mode_event_counts_the_new_run_once()
        {
            await Connect();
            _host.RaiseModeChange(DebugModes.Design);
            var generation = _sessions.Events.Generation(_host.InstanceId);
            StopAtBreakpoint();
            Assert.Equal(generation + 1, _sessions.Events.Generation(_host.InstanceId));
            _host.RaiseModeChange(DebugModes.Break);
            Assert.Equal(generation + 1, _sessions.Events.Generation(_host.InstanceId));
        }

        void StopAtBreakpoint() => _host.RaiseStop(new StopEvent
        {
            Reason = StopReason.Breakpoint,
            BreakpointId = 3,
            ProcessName = "host.exe",
            Pid = 1724,
            ThreadId = 6120,
            Frame = new Frame { Index = 0, Function = "ProcessCopies", File = @"D:\repo\copy.cpp", Line = 88 },
            Mode = DebugModes.Break
        });

        [Fact]
        public async Task Waiting_without_resuming_says_so_instead_of_timing_out()
        {
            await Connect();
            StopAtBreakpoint();

            var tools = new ExecutionTools(_sessions);
            await tools.Wait(1, null, null, CancellationToken.None);

            var again = await tools.Wait(30, null, null, CancellationToken.None);

            Assert.DoesNotContain("no stop arrived", again);
            Assert.Contains("ProcessCopies", again);
            Assert.Contains("go", again);
        }

        [Fact]
        public async Task It_returns_without_using_the_timeout()
        {
            await Connect();
            StopAtBreakpoint();

            var tools = new ExecutionTools(_sessions);
            await tools.Wait(1, null, null, CancellationToken.None);

            var clock = Stopwatch.StartNew();
            await tools.Wait(30, null, null, CancellationToken.None);
            clock.Stop();

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "wait sat out its timeout: " + clock.Elapsed);
        }

        /// <summary>
        /// Ctrl+F5 starts nothing under the debugger, so it does not move a debuggee that
        /// is sitting in break and must not make wait forget where it is.
        /// </summary>
        [Fact]
        public async Task Launching_without_the_debugger_leaves_the_stop_where_it_was()
        {
            await Connect();
            StopAtBreakpoint();

            var tools = new ExecutionTools(_sessions);
            await tools.Wait(1, null, null, CancellationToken.None);
            await new LifecycleTools(_sessions).Launch(null, null, false, true, null, CancellationToken.None);

            var again = await tools.Wait(30, null, null, CancellationToken.None);

            Assert.Contains("ProcessCopies", again);
        }

        /// <summary>
        /// A detached process runs free. Answering with the frame it stopped in, and
        /// telling the caller to resume it, would be two claims about a process the
        /// debugger no longer holds.
        /// </summary>
        [Fact]
        public async Task Detaching_stops_wait_answering_with_the_old_frame()
        {
            await Connect();
            StopAtBreakpoint();

            var tools = new ExecutionTools(_sessions);
            await tools.Wait(1, null, null, CancellationToken.None);
            await new LifecycleTools(_sessions).Detach(null, null, CancellationToken.None);

            var text = await tools.Wait(1, null, null, CancellationToken.None);

            Assert.Contains("timeout", text);
            Assert.DoesNotContain("ProcessCopies", text);
        }

        [Fact]
        public async Task After_go_it_waits_the_way_it_always_did()
        {
            await Connect();
            StopAtBreakpoint();

            var tools = new ExecutionTools(_sessions);
            await tools.Wait(1, null, null, CancellationToken.None);
            await tools.Go(null, CancellationToken.None);

            var text = await tools.Wait(1, null, null, CancellationToken.None);

            Assert.Contains("timeout", text);
        }
    }
}
