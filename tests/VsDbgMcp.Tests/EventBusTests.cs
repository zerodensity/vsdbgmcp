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
            bus.MarkSeen();

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
            bus.MarkSeen();

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
    }
}
