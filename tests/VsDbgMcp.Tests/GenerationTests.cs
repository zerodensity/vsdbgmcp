using System;
using System.Collections.Generic;
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
    /// A pid, a thread id and an address belong to the run they were read in.
    ///
    /// Across one session the editor ran as five different pids and the launcher as
    /// four, and an address was once compared across a restart without anyone noticing.
    /// Nothing here detects a stale address - it cannot know when one was captured - so
    /// the run is numbered and the number is said everywhere identities are.
    /// </summary>
    public class GenerationTests
    {
        static StopEvent Stop(string instance) => new StopEvent
        {
            InstanceId = instance,
            Reason = StopReason.Breakpoint,
            ProcessName = "host.exe",
            Pid = 1724,
            Mode = DebugModes.Break
        };

        [Fact]
        public void Leaving_design_mode_counts_a_run()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.Equal(1, bus.Generation("Engine#1"));
        }

        [Fact]
        public void Each_restart_counts_another()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.Equal(2, bus.Generation("Engine#1"));
        }

        /// <summary>Breaking and resuming is the same run all the way through.</summary>
        [Fact]
        public void Stopping_and_resuming_is_not_a_new_run()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.ModeChanged("Engine#1", DebugModes.Break);
            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.Equal(1, bus.Generation("Engine#1"));
        }

        /// <summary>
        /// A session already under way when the shim connected was never seen to begin,
        /// so it gets no number rather than being called the first.
        /// </summary>
        [Fact]
        public void A_session_that_was_already_running_has_no_number()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Break);

            Assert.Equal(0, bus.Generation("Engine#1"));
        }

        /// <summary>
        /// A window that has sat in design mode since before the shim connected never
        /// announces it, so nothing would count the first run. The tool that starts it
        /// says so instead.
        /// </summary>
        [Fact]
        public void The_first_run_is_counted_even_though_design_mode_was_never_announced()
        {
            var bus = new EventBus();
            bus.StartingRun("Engine#1");
            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.Equal(1, bus.Generation("Engine#1"));
        }

        /// <summary>
        /// The tool and the notification both report the same restart, and the run is
        /// counted once. Counting twice would say two runs where there was one.
        /// </summary>
        [Fact]
        public void A_restart_counts_once_however_it_is_heard_about()
        {
            var bus = new EventBus();
            bus.StartingRun("Engine#1");
            bus.ModeChanged("Engine#1", DebugModes.Run);

            bus.StartingRun("Engine#1");
            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.Equal(2, bus.Generation("Engine#1"));
        }

        /// <summary>
        /// The mode notification arrives seconds after the change it announces, so the
        /// count cannot wait for it: the stops the new run produces in the meantime would
        /// wear the previous run's number, which is the comparison this exists to stop.
        /// </summary>
        [Fact]
        public void A_restart_moves_the_number_before_the_debugger_confirms_it()
        {
            var bus = new EventBus();
            bus.StartingRun("Engine#1");
            bus.ModeChanged("Engine#1", DebugModes.Run);

            var beforeRestart = Stop("Engine#1");
            bus.Publish(beforeRestart);

            bus.StartingRun("Engine#1");

            var afterRestart = Stop("Engine#1");
            bus.Publish(afterRestart);

            Assert.Equal(1, beforeRestart.Generation);
            Assert.Equal(2, afterRestart.Generation);
        }

        /// <summary>
        /// A call that failed starts nothing, so no mode change is coming to confirm it.
        /// Left expecting one, the bus would take the next run somebody starts from the
        /// IDE for that confirmation and two runs would share a number.
        /// </summary>
        [Fact]
        public void A_run_that_never_started_does_not_swallow_the_next_one()
        {
            var bus = new EventBus();
            bus.StartingRun("Engine#1");
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.ModeChanged("Engine#1", DebugModes.Design);

            // attach, refused because the pattern matched nothing.
            bus.StartingRun("Engine#1");
            bus.RunNotStarted("Engine#1");

            var before = bus.Generation("Engine#1");
            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.True(bus.Generation("Engine#1") > before,
                "a run started from the IDE went uncounted after a call that started nothing");
        }

        /// <summary>
        /// Resuming is not starting a run, and the mode notification that follows one
        /// must not be read as a restart.
        /// </summary>
        [Fact]
        public void Resuming_after_a_run_was_started_does_not_count_again()
        {
            var bus = new EventBus();
            bus.StartingRun("Engine#1");
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.ModeChanged("Engine#1", DebugModes.Break);
            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.Equal(1, bus.Generation("Engine#1"));
        }

        /// <summary>
        /// Starting a run puts the debuggee somewhere else, so where it was last stopped
        /// is no longer an answer to anything.
        /// </summary>
        [Fact]
        public async Task Starting_a_run_forgets_where_the_debuggee_was_stopped()
        {
            var bus = new EventBus();
            bus.Publish(Stop("Engine#1"));
            await bus.WaitAsync("Engine#1", TimeSpan.FromMilliseconds(200), CancellationToken.None);

            bus.StartingRun("Engine#1");

            Assert.Empty(bus.AlreadyStopped("Engine#1", new[] { "Engine#1" }));
        }

        [Fact]
        public void Each_window_counts_its_own_runs()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.ModeChanged("Editor#2", DebugModes.Design);
            bus.ModeChanged("Editor#2", DebugModes.Run);

            Assert.Equal(2, bus.Generation("Engine#1"));
            Assert.Equal(1, bus.Generation("Editor#2"));
        }

        [Fact]
        public void A_stop_carries_the_run_it_happened_in()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);

            var first = Stop("Engine#1");
            bus.Publish(first);

            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);

            var second = Stop("Engine#1");
            bus.Publish(second);

            Assert.Equal(1, first.Generation);
            Assert.Equal(2, second.Generation);
        }

        [Fact]
        public void The_stop_line_carries_the_number()
        {
            var text = Render.Stop(new StopEvent
            {
                InstanceId = "Engine#1",
                Reason = StopReason.Breakpoint,
                ProcessName = "host.exe",
                Pid = 1724,
                Generation = 3
            });

            Assert.Contains("gen 3", text);
        }

        /// <summary>An unnumbered run says so rather than looking like the first.</summary>
        [Fact]
        public void The_stop_line_says_when_there_is_no_number()
        {
            var text = Render.Stop(new StopEvent { InstanceId = "Engine#1", Reason = StopReason.Breakpoint });

            Assert.Contains("gen ?", text);
        }

        static HostStatus Status(string mode) => new HostStatus
        {
            InstanceId = "Engine#1",
            Mode = mode,
            Processes = new List<ProcessInfo> { new ProcessInfo { Pid = 1724, Name = "host.exe", IsDebugged = true } }
        };

        [Fact]
        public void Status_says_the_run_and_what_the_number_is_for()
        {
            var text = Render.Status(Status(DebugModes.Break), 2);

            Assert.Contains("gen 2", text);
            Assert.Contains("the same number means the same run", text);
        }

        /// <summary>
        /// It says what it does not know, not why. A window that sat in design mode
        /// before the shim connected never announced it, so a run started at the keyboard
        /// afterwards is also unnumbered - "it was already running" would be a guess.
        /// </summary>
        [Fact]
        public void Status_says_when_the_run_has_no_number()
        {
            var text = Render.Status(Status(DebugModes.Run), 0);

            Assert.Contains("gen ?", text);
            Assert.Contains("nothing here saw this run begin", text);
        }

        /// <summary>In design mode there is no run to number.</summary>
        [Fact]
        public void Status_numbers_nothing_when_nothing_is_being_debugged()
        {
            var text = Render.Status(Status(DebugModes.Design), 4);

            Assert.DoesNotContain("gen 4", text);
            Assert.DoesNotContain("gen ?", text);
        }
    }

    /// <summary>
    /// What the tools that begin a run say about everything read before them.
    /// </summary>
    public class GenerationToolTests : IDisposable
    {
        readonly string _dir;
        readonly FakeHost _host;
        readonly SessionManager _sessions;

        public GenerationToolTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-gen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var pipe = "vsdbgmcp-gen-" + Guid.NewGuid().ToString("N");
            _host = new FakeHost(pipe) { InstanceId = "App#" + Process.GetCurrentProcess().Id };
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

        [Fact]
        public async Task Restart_says_every_identity_from_the_previous_run_is_void()
        {
            var text = await new LifecycleTools(_sessions).Restart(null, CancellationToken.None);

            Assert.Contains("pid, thread id, address and container reference", text);
            Assert.Contains("names nothing now", text);
            Assert.Contains("generation number", text);
        }

        [Fact]
        public async Task Launch_says_it_too()
        {
            var text = await new LifecycleTools(_sessions)
                .Launch(null, null, false, false, null, CancellationToken.None);

            Assert.Contains("belongs to a different run", text);
        }

        /// <summary>
        /// Starting without the debugger ends no debug session, so there is no run whose
        /// identities have just stopped meaning anything.
        /// </summary>
        [Fact]
        public async Task Launching_without_the_debugger_says_nothing_about_runs()
        {
            var text = await new LifecycleTools(_sessions)
                .Launch(null, null, false, true, null, CancellationToken.None);

            Assert.DoesNotContain("generation", text);
        }

        /// <summary>Attaching may join a session instead of starting one, and says so.</summary>
        [Fact]
        public async Task Attach_says_it_may_have_started_a_run()
        {
            var text = await new LifecycleTools(_sessions).Attach(1724, null, null, CancellationToken.None);

            Assert.Contains("If this started a run", text);
            Assert.Contains("generation number", text);
        }

        [Fact]
        public async Task Opening_a_dump_says_it_too()
        {
            var file = Path.Combine(_dir, "crash.dmp");
            File.WriteAllText(file, "not really a dump");

            var text = await new LifecycleTools(_sessions).DumpOpen(file, null, CancellationToken.None);

            Assert.Contains("generation number", text);
        }

        [Fact]
        public async Task Status_carries_the_number_the_shim_has_counted()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            _host.RaiseModeChange(DebugModes.Design);
            _host.RaiseModeChange(DebugModes.Run);

            var text = await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            Assert.Contains("gen 1", text);
        }

        /// <summary>
        /// The window sat in design mode before the shim ever connected, so the launch
        /// itself is what says a run is starting.
        /// </summary>
        [Fact]
        public async Task A_launch_into_a_window_that_never_announced_design_still_counts()
        {
            var tools = new LifecycleTools(_sessions);
            await tools.Launch(null, null, false, false, null, CancellationToken.None);

            _host.RaiseModeChange(DebugModes.Run);

            Assert.Contains("gen 1", await tools.Status(null, CancellationToken.None));
        }

        /// <summary>
        /// The whole point of the number, through the real tools: two runs of the same
        /// debuggee, and the stops that name the same pid do not claim to be comparable.
        /// The mode notification for the restart has not arrived yet, which is when this
        /// used to get it wrong.
        /// </summary>
        [Fact]
        public async Task Two_runs_never_share_a_number_while_the_mode_is_still_catching_up()
        {
            var tools = new LifecycleTools(_sessions);
            await tools.Launch(null, null, false, false, null, CancellationToken.None);
            _host.RaiseModeChange(DebugModes.Run);

            var execution = new ExecutionTools(_sessions);
            _host.RaiseStop(new StopEvent { Reason = StopReason.Breakpoint, ProcessName = "host.exe", Pid = 1724, Mode = DebugModes.Break });
            var first = await execution.Wait(1, null, null, CancellationToken.None);

            await tools.Restart(null, CancellationToken.None);
            _host.RaiseStop(new StopEvent { Reason = StopReason.Breakpoint, ProcessName = "host.exe", Pid = 1724, Mode = DebugModes.Break });
            var second = await execution.Wait(1, null, null, CancellationToken.None);

            Assert.Contains("gen 1", first);
            Assert.Contains("gen 2", second);
        }

        [Fact]
        public async Task A_restart_moves_the_number_on()
        {
            var tools = new LifecycleTools(_sessions);
            await tools.Launch(null, null, false, false, null, CancellationToken.None);
            _host.RaiseModeChange(DebugModes.Run);

            await tools.Restart(null, CancellationToken.None);
            _host.RaiseModeChange(DebugModes.Design);
            _host.RaiseModeChange(DebugModes.Run);

            Assert.Contains("gen 2", await tools.Status(null, CancellationToken.None));
        }
    }
}
