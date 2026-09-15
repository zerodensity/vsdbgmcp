using System;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Session;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// wait() has to be event driven, and it has to be impossible to miss a stop that
    /// happened between two calls. Both of those are properties of this class, and so
    /// is the rule that only a caller who asked about modules hears about them.
    /// </summary>
    public class EventBusTests
    {
        static StopEvent Stop(string instance, string reason = StopReason.Breakpoint) =>
            new StopEvent { InstanceId = instance, Reason = reason };

        static StopEvent Exit(string instance, int pid) => new StopEvent
        {
            InstanceId = instance,
            Reason = StopReason.Exited,
            ProcessName = "host.exe",
            Pid = pid,
            ExitCode = 0,
            Mode = DebugModes.Design
        };

        static ModuleLoadEvent Module(string instance, string name) =>
            new ModuleLoadEvent { InstanceId = instance, Name = name, SymbolsLoaded = true };

        [Fact]
        public async Task A_stop_that_already_happened_is_still_delivered()
        {
            var bus = new EventBus();
            bus.Publish(Stop("Engine#1"));

            var stop = await bus.WaitAsync(null, TimeSpan.FromMilliseconds(50), CancellationToken.None);

            Assert.NotNull(stop);
            Assert.Equal("Engine#1", stop.InstanceId);
        }

        [Fact]
        public async Task A_waiter_is_woken_by_a_stop_that_arrives_later()
        {
            var bus = new EventBus();
            var waiting = bus.WaitAsync(null, TimeSpan.FromSeconds(5), CancellationToken.None);

            bus.Publish(Stop("Engine#1", StopReason.Exception));

            var stop = await waiting;
            Assert.Equal(StopReason.Exception, stop.Reason);
        }

        [Fact]
        public async Task The_same_stop_is_not_handed_out_twice()
        {
            var bus = new EventBus();
            bus.Publish(Stop("Engine#1"));

            Assert.NotNull(await bus.WaitAsync(null, TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.Null(await bus.WaitAsync(null, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        [Fact]
        public async Task Timing_out_returns_nothing_rather_than_failing()
        {
            var bus = new EventBus();

            var stop = await bus.WaitAsync(null, TimeSpan.FromMilliseconds(30), CancellationToken.None);

            Assert.Null(stop);
        }

        [Fact]
        public async Task Waiting_on_one_instance_ignores_the_others()
        {
            var bus = new EventBus();
            bus.Publish(Stop("Editor#2"));

            Assert.Null(await bus.WaitAsync("Engine#1", TimeSpan.FromMilliseconds(30), CancellationToken.None));

            bus.Publish(Stop("Engine#1"));
            var mine = await bus.WaitAsync("Engine#1", TimeSpan.FromMilliseconds(200), CancellationToken.None);

            Assert.Equal("Engine#1", mine.InstanceId);
        }

        [Fact]
        public async Task Waiting_on_any_instance_returns_whichever_stops_first()
        {
            var bus = new EventBus();
            var waiting = bus.WaitAsync(null, TimeSpan.FromSeconds(5), CancellationToken.None);

            bus.Publish(Stop("Editor#2", StopReason.Step));

            var stop = await waiting;
            Assert.Equal("Editor#2", stop.InstanceId);
        }

        [Fact]
        public async Task Resuming_execution_discards_stops_that_are_already_history()
        {
            var bus = new EventBus();
            bus.Publish(Stop("Engine#1"));

            // What go() does before it resumes, so the next wait reports the coming
            // stop rather than the one the caller has already seen.
            bus.MarkSeen("Engine#1");

            Assert.Null(await bus.WaitAsync(null, TimeSpan.FromMilliseconds(30), CancellationToken.None));
        }

        [Fact]
        public async Task Cancelling_the_call_does_not_hang_the_waiter()
        {
            var bus = new EventBus();
            using (var cts = new CancellationTokenSource())
            {
                var waiting = bus.WaitAsync(null, TimeSpan.FromSeconds(30), cts.Token);
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            }
        }

        [Fact]
        public void Sequence_numbers_increase_so_order_is_never_ambiguous()
        {
            var bus = new EventBus();
            var first = Stop("Engine#1");
            var second = Stop("Engine#1");

            bus.Publish(first);
            bus.Publish(second);

            Assert.True(second.Seq > first.Seq);
        }

        /// <summary>
        /// The process that exited belongs to the run that is over. Handing it to the
        /// first wait() after re-attaching says the current target has died, which is a
        /// misleading place to start and cost a reader the first minutes of a session.
        /// </summary>
        [Fact]
        public async Task An_exit_from_the_previous_session_is_not_reported_once_a_new_one_starts()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.Publish(Exit("Engine#1", 1724));
            bus.ModeChanged("Engine#1", DebugModes.Design);

            // Attaching to the process that replaced it: Visual Studio leaves design
            // mode as it attaches, whichever tool asked for it.
            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.Null(await bus.WaitAsync(null, TimeSpan.FromMilliseconds(30), CancellationToken.None));
        }

        [Fact]
        public async Task An_exit_that_just_happened_is_still_reported()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.Publish(Exit("Engine#1", 1724));
            bus.ModeChanged("Engine#1", DebugModes.Design);

            var stop = await bus.WaitAsync(null, TimeSpan.FromMilliseconds(50), CancellationToken.None);

            Assert.Equal(StopReason.Exited, stop.Reason);
            Assert.Equal(1724, stop.Pid);
        }

        [Fact]
        public async Task A_session_starting_in_one_window_leaves_another_window_s_stop_alone()
        {
            var bus = new EventBus();
            bus.ModeChanged("Editor#2", DebugModes.Design);
            bus.ModeChanged("Editor#2", DebugModes.Run);
            bus.Publish(Stop("Editor#2"));

            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);

            var stop = await bus.WaitAsync(null, TimeSpan.FromMilliseconds(50), CancellationToken.None);

            Assert.Equal("Editor#2", stop.InstanceId);
        }

        [Fact]
        public async Task Stops_within_a_session_survive_its_own_mode_changes()
        {
            var bus = new EventBus();
            bus.ModeChanged("Engine#1", DebugModes.Design);
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.Publish(Stop("Engine#1"));
            bus.ModeChanged("Engine#1", DebugModes.Break);

            Assert.NotNull(await bus.WaitAsync(null, TimeSpan.FromMilliseconds(50), CancellationToken.None));

            // What go() does. Running again is the same session carrying on.
            bus.ModeChanged("Engine#1", DebugModes.Run);
            bus.Publish(Stop("Engine#1", StopReason.Exception));
            bus.ModeChanged("Engine#1", DebugModes.Break);

            var second = await bus.WaitAsync(null, TimeSpan.FromMilliseconds(50), CancellationToken.None);
            Assert.Equal(StopReason.Exception, second.Reason);
        }

        /// <summary>
        /// Connecting to a window that is already debugging: the first mode this hears
        /// is not a session beginning, so nothing buffered may be thrown away on it.
        /// </summary>
        [Fact]
        public async Task A_stop_buffered_before_any_mode_was_known_is_still_reported()
        {
            var bus = new EventBus();
            bus.Publish(Stop("Engine#1"));

            bus.ModeChanged("Engine#1", DebugModes.Break);

            Assert.NotNull(await bus.WaitAsync(null, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        /// <summary>
        /// The one thing a module load must never do. Every loop in this tool reads a
        /// returning wait() as "the debuggee stopped", and loading a module stops
        /// nothing.
        /// </summary>
        [Fact]
        public async Task A_plain_wait_is_never_woken_by_a_module_load()
        {
            var bus = new EventBus();
            var waiting = bus.WaitAsync(null, TimeSpan.FromMilliseconds(200), CancellationToken.None);

            bus.PublishModuleLoad(Module("Engine#1", "plugin.dll"));

            Assert.Null(await waiting);
            Assert.Null(await bus.WaitAsync(null, TimeSpan.FromMilliseconds(30), CancellationToken.None));
        }

        [Fact]
        public async Task Waiting_for_a_module_is_not_satisfied_by_a_stop()
        {
            var bus = new EventBus();
            var waiting = bus.WaitForModuleAsync(null, "plugin", TimeSpan.FromMilliseconds(200), CancellationToken.None);

            bus.Publish(Stop("Engine#1"));

            Assert.Null(await waiting);
        }

        [Fact]
        public async Task A_waiter_is_woken_by_the_module_it_asked_for()
        {
            var bus = new EventBus();
            var waiting = bus.WaitForModuleAsync(null, "plugin", TimeSpan.FromSeconds(5), CancellationToken.None);

            bus.PublishModuleLoad(Module("Engine#1", "unrelated.dll"));
            bus.PublishModuleLoad(Module("Engine#1", "MyPlugin.dll"));

            var module = await waiting;
            Assert.Equal("MyPlugin.dll", module.Name);
            Assert.Equal("Engine#1", module.InstanceId);
        }

        [Fact]
        public async Task A_module_that_loaded_before_the_wait_is_reported_at_once()
        {
            var bus = new EventBus();
            bus.PublishModuleLoad(Module("Engine#1", "MyPlugin.dll"));

            var module = await bus.WaitForModuleAsync(null, "myplugin", TimeSpan.FromMilliseconds(50), CancellationToken.None);

            Assert.NotNull(module);
            Assert.Equal("MyPlugin.dll", module.Name);
        }

        [Fact]
        public async Task The_same_module_load_is_not_reported_twice()
        {
            var bus = new EventBus();
            bus.PublishModuleLoad(Module("Engine#1", "MyPlugin.dll"));

            Assert.NotNull(await bus.WaitForModuleAsync(null, "MyPlugin", TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.Null(await bus.WaitForModuleAsync(null, "MyPlugin", TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        /// <summary>
        /// Arming breakpoints across two plugins and waiting for each in turn is the
        /// case this exists for, and the second one usually loaded first.
        /// </summary>
        [Fact]
        public async Task Waiting_for_one_module_leaves_the_others_to_be_reported()
        {
            var bus = new EventBus();
            bus.PublishModuleLoad(Module("Engine#1", "First.dll"));
            bus.PublishModuleLoad(Module("Engine#1", "Second.dll"));

            var second = await bus.WaitForModuleAsync(null, "Second", TimeSpan.FromMilliseconds(50), CancellationToken.None);
            var first = await bus.WaitForModuleAsync(null, "First", TimeSpan.FromMilliseconds(50), CancellationToken.None);

            Assert.Equal("Second.dll", second.Name);
            Assert.Equal("First.dll", first.Name);
        }

        [Fact]
        public async Task Waiting_for_a_module_in_one_instance_ignores_the_others()
        {
            var bus = new EventBus();
            bus.PublishModuleLoad(Module("Editor#2", "MyPlugin.dll"));

            Assert.Null(await bus.WaitForModuleAsync("Engine#1", "MyPlugin", TimeSpan.FromMilliseconds(30), CancellationToken.None));

            bus.PublishModuleLoad(Module("Engine#1", "MyPlugin.dll"));
            var mine = await bus.WaitForModuleAsync("Engine#1", "MyPlugin", TimeSpan.FromMilliseconds(200), CancellationToken.None);

            Assert.Equal("Engine#1", mine.InstanceId);
        }

        [Fact]
        public async Task Resuming_execution_keeps_the_modules_already_loaded()
        {
            var bus = new EventBus();
            bus.PublishModuleLoad(Module("Engine#1", "MyPlugin.dll"));

            // go() discards stops the caller has already seen. A module that is loaded
            // stays loaded, so it is still an answer.
            bus.MarkSeen("Engine#1");

            Assert.NotNull(await bus.WaitForModuleAsync(null, "MyPlugin", TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        [Fact]
        public async Task Waiting_for_a_module_that_never_loads_returns_nothing()
        {
            var bus = new EventBus();

            var module = await bus.WaitForModuleAsync(null, "MyPlugin", TimeSpan.FromMilliseconds(30), CancellationToken.None);

            Assert.Null(module);
        }

        [Fact]
        public async Task Cancelling_a_module_wait_does_not_hang_the_waiter()
        {
            var bus = new EventBus();
            using (var cts = new CancellationTokenSource())
            {
                var waiting = bus.WaitForModuleAsync(null, "MyPlugin", TimeSpan.FromSeconds(30), cts.Token);
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
            }
        }

        static System.Text.RegularExpressions.Regex Pattern(string text) =>
            new System.Text.RegularExpressions.Regex(text,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));

        static OutputEvent Output(string instance, string text) =>
            new OutputEvent { InstanceId = instance, Pane = "Debug", Text = text };

        [Fact]
        public async Task A_line_that_already_arrived_still_answers()
        {
            var bus = new EventBus();
            bus.PublishOutput(Output("Engine#1", "Server listening on 8080\r\n"));

            var line = await bus.WaitForOutputAsync("Engine#1", Pattern("listening"), TimeSpan.FromMilliseconds(50), CancellationToken.None);

            Assert.Equal("Server listening on 8080", line);
        }

        [Fact]
        public async Task A_waiter_is_woken_by_a_line_that_arrives_later()
        {
            var bus = new EventBus();
            var waiting = bus.WaitForOutputAsync(null, Pattern("ERROR"), TimeSpan.FromSeconds(5), CancellationToken.None);

            bus.PublishOutput(Output("Engine#1", "ERROR: device lost"));

            Assert.Equal("ERROR: device lost", await waiting);
        }

        [Fact]
        public async Task One_push_of_many_lines_becomes_many_lines()
        {
            var bus = new EventBus();
            bus.PublishOutput(Output("Engine#1", "first\r\n\r\nsecond\r\n"));

            Assert.Equal("first", await bus.WaitForOutputAsync(null, Pattern("first"), TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.Equal("second", await bus.WaitForOutputAsync(null, Pattern("second"), TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        [Fact]
        public async Task A_line_is_answered_with_once()
        {
            var bus = new EventBus();
            bus.PublishOutput(Output("Engine#1", "ready"));

            Assert.NotNull(await bus.WaitForOutputAsync(null, Pattern("ready"), TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.Null(await bus.WaitForOutputAsync(null, Pattern("ready"), TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        [Fact]
        public async Task A_line_from_the_previous_run_cannot_answer_a_wait_on_this_one()
        {
            var bus = new EventBus();
            bus.PublishOutput(Output("Engine#1", "Server listening on 8080"));

            bus.StartingRun("Engine#1");

            Assert.Null(await bus.WaitForOutputAsync("Engine#1", Pattern("listening"), TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        [Fact]
        public async Task A_run_somebody_started_from_the_IDE_also_clears_what_came_before()
        {
            var bus = new EventBus();
            bus.InitializeMode("Engine#1", DebugModes.Design);
            bus.PublishOutput(Output("Engine#1", "Server listening on 8080"));

            bus.ModeChanged("Engine#1", DebugModes.Run);

            Assert.Null(await bus.WaitForOutputAsync("Engine#1", Pattern("listening"), TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        /// <summary>
        /// A line the pattern below cannot finish on. Matching it runs until the match
        /// timeout gives up, which is what holds a reader inside its match phase for a
        /// known length of time rather than a hoped-for one.
        /// </summary>
        static readonly string Unfinishable = new string('a', 40) + "!";

        /// <summary>Matches the line that matters at once, and never finishes the other.</summary>
        static System.Text.RegularExpressions.Regex SlowPattern() =>
            new System.Text.RegularExpressions.Regex(@"^(a+)+$|Server listening",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));

        /// <summary>
        /// Matching runs outside the lock, so a run can end while a reader is still
        /// working through its snapshot of the buffer. What it holds then are lines from
        /// a session that is over, and "not answered with yet" no longer tells them from
        /// live ones: "Server listening" from the last run must not answer this one.
        /// </summary>
        [Fact]
        public async Task A_line_cleared_while_a_wait_was_matching_cannot_answer_it()
        {
            var bus = new EventBus();
            bus.PublishOutput(Output("Engine#1", Unfinishable + "\nServer listening on 8080"));

            var waiting = Task.Run(() => bus.WaitForOutputAsync("Engine#1", SlowPattern(),
                TimeSpan.FromMilliseconds(100), CancellationToken.None));

            // Inside the first line's match, which runs for the whole 250 ms.
            await Task.Delay(50);
            bus.StartingRun("Engine#1");

            Assert.Null(await waiting);
        }

        /// <summary>The publisher matches outside the lock too, and holds the same stale list.</summary>
        [Fact]
        public async Task A_line_cleared_while_a_publish_was_matching_cannot_answer_a_waiter()
        {
            var bus = new EventBus();
            var waiting = bus.WaitForOutputAsync("Engine#1", SlowPattern(),
                TimeSpan.FromMilliseconds(500), CancellationToken.None);

            var publishing = Task.Run(() =>
                bus.PublishOutput(Output("Engine#1", Unfinishable + "\nServer listening on 8080")));

            await Task.Delay(50);
            bus.StartingRun("Engine#1");

            Assert.Null(await waiting);
            await publishing;
        }

        [Fact]
        public async Task Waiting_for_a_stop_is_never_woken_by_output()
        {
            var bus = new EventBus();
            var waiting = bus.WaitAsync(null, TimeSpan.FromMilliseconds(200), CancellationToken.None);

            bus.PublishOutput(Output("Engine#1", "ERROR: device lost"));

            Assert.Null(await waiting);
        }
    }
}
