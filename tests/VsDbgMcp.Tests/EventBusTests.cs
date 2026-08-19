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
    /// happened between two calls. Both of those are properties of this class.
    /// </summary>
    public class EventBusTests
    {
        static StopEvent Stop(string instance, string reason = StopReason.Breakpoint) =>
            new StopEvent { InstanceId = instance, Reason = reason };

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
    }
}
