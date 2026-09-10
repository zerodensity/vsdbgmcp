using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;
using Xunit;

namespace VsDbgMcp.Tests
{
    public class OperationReconciliationTests
    {
        static OperationRegistry Registry() => new OperationRegistry(o => JsonConvert.DeserializeObject<OperationInfo>(JsonConvert.SerializeObject(o)));
        static StateObservation Idle() => new StateObservation { Mode = "design", BuildBusy = false, TimestampUtc = DateTime.UtcNow };

        [Fact]
        public void Idle_does_not_retire_a_queued_or_normally_pending_command()
        {
            var registry = Registry();
            var item = registry.Begin("build", "request", "args", true, out _);
            Assert.False(registry.ObserveIdle(item.OperationId, Idle()).Terminal);
            Assert.True(registry.TryStartCommand(item.OperationId));
            registry.CommandReturned(item.OperationId);
            registry.Update(item.OperationId, o => o.State = "accepted");
            Assert.False(registry.ObserveIdle(item.OperationId, Idle()).Terminal);
            Assert.True(registry.Read(item.OperationId).BlocksNewRequests);
        }

        [Fact]
        public void Unknown_inflight_command_retains_protection_until_fresh_idle_evidence()
        {
            var registry = Registry();
            var item = registry.Begin("launch", "original", "args", true, out _);
            registry.TryStartCommand(item.OperationId);
            registry.Update(item.OperationId, o => o.State = "unknown");
            var oldIdle = Idle();
            Assert.True(registry.ObserveIdle(item.OperationId, oldIdle).BlocksNewRequests);
            registry.CommandReturned(item.OperationId);
            Assert.True(registry.ObserveIdle(item.OperationId, oldIdle).BlocksNewRequests);
            Assert.True(registry.ObserveIdle(item.OperationId, null).BlocksNewRequests);
            var observed = registry.ObserveIdle(item.OperationId, Idle());
            Assert.False(observed.BlocksNewRequests);
            Assert.True(observed.Terminal);
            Assert.Equal("unknown", observed.State);
            Assert.Equal(item.OperationId, registry.Begin("launch", "original", "args", true, out var created).OperationId);
            Assert.False(created);
            Assert.NotEqual(item.OperationId, registry.Begin("launch", "new", "args", true, out _).OperationId);
        }

        [Fact]
        public void Completion_during_dispatch_keeps_protection_until_return_and_preserves_success()
        {
            var registry = Registry();
            var item = registry.Begin("build", null, "args", true, out _);
            registry.TryStartCommand(item.OperationId);
            registry.Update(item.OperationId, o => o.State = "succeeded", true);
            Assert.True(registry.Read(item.OperationId).BlocksNewRequests);
            registry.CommandReturned(item.OperationId);
            Assert.False(registry.Read(item.OperationId).BlocksNewRequests);
            Assert.Equal("succeeded", registry.ObserveIdle(item.OperationId, Idle()).State);
        }

        [Fact]
        public async Task Idle_reconciliation_racing_completion_never_fabricates_success()
        {
            for (var i = 0; i < 100; i++)
            {
                var registry = Registry();
                var item = registry.Begin("build", null, "args", true, out _);
                registry.TryStartCommand(item.OperationId);
                registry.CommandReturned(item.OperationId);
                registry.Update(item.OperationId, o => o.State = "unknown");
                await Task.WhenAll(Task.Run(() => registry.ObserveIdle(item.OperationId, Idle())),
                    Task.Run(() => registry.Update(item.OperationId, o => o.State = "succeeded", true)));
                var result = registry.Read(item.OperationId);
                Assert.True(result.Terminal);
                Assert.False(result.BlocksNewRequests);
                Assert.Contains(result.State, new[] { "unknown", "succeeded" });
            }
        }

        [Fact]
        public async Task Unknown_terminal_result_still_honors_wait_until_protection_is_released()
        {
            var registry = Registry();
            var item = registry.Begin("build", null, "args", true, out _);
            registry.TryStartCommand(item.OperationId);
            registry.CommandReturned(item.OperationId);
            registry.Update(item.OperationId, o => o.State = "unknown", true);
            var waiting = registry.WaitAsync(item.OperationId, 30, default);
            Assert.False(waiting.IsCompleted);
            registry.ObserveIdle(item.OperationId, Idle());
            var result = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("unknown", result.State);
            Assert.False(result.BlocksNewRequests);
        }

        [Fact]
        public void Unknown_terminal_build_requires_build_idle_and_does_not_reconcile_collectors()
        {
            var registry = Registry();
            var item = registry.Begin("build", null, "args", true, out _);
            registry.TryStartCommand(item.OperationId);
            registry.Update(item.OperationId, o => o.State = "unknown", true);
            registry.CommandReturned(item.OperationId);
            Assert.True(registry.ObserveIdle(item.OperationId, new StateObservation { Mode = "design", BuildBusy = true, TimestampUtc = DateTime.UtcNow }).BlocksNewRequests);
            Assert.False(registry.ObserveIdle(item.OperationId, Idle()).BlocksNewRequests);
            var collector = registry.Begin("profile-stop", null, "capture", true, out _);
            registry.TryStartCommand(collector.OperationId);
            registry.CommandReturned(collector.OperationId);
            registry.Update(collector.OperationId, o => o.State = "unknown");
            Assert.True(registry.ObserveIdle(collector.OperationId, Idle()).BlocksNewRequests);
        }
    }
}
