using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    public class OperationResponseTests
    {
        [Theory]
        [InlineData("requested", false, "pending")]
        [InlineData("running", false, "pending")]
        [InlineData("running", true, "succeeded")]
        [InlineData("pending-symbols", true, "succeeded")]
        [InlineData("failed", true, "failed")]
        [InlineData("cancelled", true, "cancelled")]
        [InlineData("unknown", false, "unknown")]
        [InlineData("unknown", true, "unknown")]
        [InlineData("issued", true, "unknown")]
        [InlineData("unexpected-new-state", true, "unknown")]
        public void Model_outcomes_distinguish_pending_success_and_uncertainty(string state, bool terminal, string expected)
        {
            var response = OperationResponse.From(new OperationInfo { OperationId = "build-1", Kind = "build", State = state, Terminal = terminal }, "VS#123");
            Assert.Equal(expected, response.State);
        }

        [Fact]
        public void Pending_response_names_a_valid_next_call_and_hides_bookkeeping()
        {
            var item = new OperationInfo { OperationId = "build-1", Kind = "build", State = "accepted", CommandInFlight = true,
                BlocksNewRequests = true, Request = "internal fingerprint", HostEpoch = "epoch" };
            var response = OperationResponse.From(item, "VS#123");
            Assert.Equal("operation_status", response.NextAction.Tool);
            Assert.Equal("build-1", response.NextAction.Arguments["operationId"]);
            Assert.Equal("VS#123", response.NextAction.Arguments["instance"]);
            Assert.Equal(30, response.NextAction.Arguments["waitSeconds"]);
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            Assert.DoesNotContain("terminal", json);
            Assert.DoesNotContain("commandInFlight", json);
            Assert.DoesNotContain("internal fingerprint", json);
            Assert.True(json.Length < 450, json);
            Assert.True(OperationResponse.From(item, "VS#123", true).Details.CommandInFlight);
        }

        [Fact]
        public void Fresh_idle_unknown_result_requires_inspection_instead_of_a_retry()
        {
            var item = new OperationInfo { OperationId = "build-1", Kind = "build", State = "unknown", Terminal = true,
                Observation = new StateObservation { Mode = "design", BuildBusy = false }, BlocksNewRequests = false };
            var response = OperationResponse.From(item, "VS#123");
            Assert.Equal("unknown", response.State);
            Assert.Equal("build_log", response.NextAction.Tool);
            Assert.Contains("before deciding", response.Message);
            item.BlocksNewRequests = true;
            Assert.Equal("operation_status", OperationResponse.From(item, "VS#123").NextAction.Tool);
            item.Historical = true;
            response = OperationResponse.From(item, "VS#123");
            Assert.Equal("build_log", response.NextAction.Tool);
            Assert.Contains("does not deduplicate", response.Message);
        }

        [Fact]
        public void Unknown_collector_guidance_does_not_supply_an_invalid_instance_argument()
        {
            var response = OperationResponse.From(new OperationInfo { Kind = "profile-stop", State = "stop-unknown", Terminal = true }, "VS#123");
            Assert.Equal("unknown", response.State);
            Assert.Equal("profile_status", response.NextAction.Tool);
            Assert.Empty(response.NextAction.Arguments);
        }

        [Fact]
        public void Observation_and_persistence_failures_remain_visible_in_compact_output()
        {
            var response = OperationResponse.From(new OperationInfo { OperationId = "launch-1", State = "accepted", Kind = "launch",
                Observation = new StateObservation { Error = "VS unavailable" }, PersistenceError = "disk full" }, "VS#123");
            Assert.Equal("pending", response.State);
            Assert.Contains("VS unavailable", response.Message);
            Assert.Contains("disk full", response.Message);
        }
    }
}
