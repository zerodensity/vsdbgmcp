using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using VsDbgMcp;
using VsDbgMcp.Contracts;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// An operation reaching its end is the one thing the host knew and the shim did
    /// not, which is why a build finishing while the model worked could only be found
    /// by asking. This callback is what carries it out of here.
    /// </summary>
    public class OperationRegistryTests
    {
        static OperationInfo Copy(OperationInfo value) =>
            JsonConvert.DeserializeObject<OperationInfo>(JsonConvert.SerializeObject(value));

        [Fact]
        public void An_operation_reaching_its_end_is_announced_once()
        {
            var registry = new OperationRegistry(Copy);
            var operation = registry.Begin("build", null, "solution", true, out _);

            var announced = new List<OperationInfo>();
            registry.Completed += o => announced.Add(o);

            registry.Update(operation.OperationId, o => o.State = "succeeded", terminal: true);
            registry.CommandReturned(operation.OperationId);

            var only = Assert.Single(announced);
            Assert.Equal("succeeded", only.State);
            Assert.True(only.Terminal);
        }

        [Fact]
        public void Work_still_running_is_not_announced()
        {
            var registry = new OperationRegistry(Copy);
            var operation = registry.Begin("build", null, "solution", true, out _);

            var announced = new List<OperationInfo>();
            registry.Completed += o => announced.Add(o);

            registry.Update(operation.OperationId, o => o.LastProgress = "compiling");

            Assert.Empty(announced);
        }

        [Fact]
        public void An_idle_observation_that_retires_an_unknown_outcome_announces_it_too()
        {
            var registry = new OperationRegistry(Copy);
            var operation = registry.Begin("build", null, "solution", true, out _);
            registry.TryStartCommand(operation.OperationId);
            registry.Update(operation.OperationId, o => o.State = "unknown");
            registry.CommandReturned(operation.OperationId);

            var announced = new List<OperationInfo>();
            registry.Completed += o => announced.Add(o);

            registry.ObserveIdle(operation.OperationId, new StateObservation
            {
                BuildBusy = false,
                TimestampUtc = DateTime.UtcNow.AddSeconds(5)
            });

            var only = Assert.Single(announced);
            Assert.True(only.Terminal);
        }
    }
}
