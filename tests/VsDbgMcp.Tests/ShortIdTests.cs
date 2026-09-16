using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    public class ShortIdTests
    {
        [Fact]
        public async Task Operations_keep_short_ids_through_retries_waits_and_persistence()
        {
            var saved = new System.Collections.Generic.List<OperationInfo>();
            var rejected = new System.Collections.Generic.List<string>();
            var registry = new OperationRegistry(
                x => JsonConvert.DeserializeObject<OperationInfo>(JsonConvert.SerializeObject(x)),
                x => saved.Add(x), "42696", id =>
                {
                    if (rejected.Count == 2) return false;
                    rejected.Add(id);
                    return true;
                });
            Assert.True(ShortId.IsValid(registry.Epoch));
            var first = registry.Begin("build", "build1", "Debug|x64", true, out var created);
            Assert.True(created);
            Assert.Matches("^[0-9a-hj-kmnp-tv-z]{6}$", first.OperationId);
            Assert.DoesNotContain(first.OperationId, rejected);
            var retry = registry.Begin("build", "build1", "Debug|x64", true, out created);
            Assert.False(created);
            Assert.Equal(first.OperationId, retry.OperationId);
            Assert.Throws<InvalidOperationException>(() => registry.Begin("build", "build1", "Release|x64", true, out _));
            registry.Update(first.OperationId, o => o.State = "succeeded", true);
            Assert.Equal("succeeded", (await registry.WaitAsync(first.OperationId, 0, default)).State);
            Assert.All(saved, o => Assert.Equal(first.OperationId, o.OperationId));
            var second = registry.Begin("build", "build2", "Debug|x64", true, out _);
            Assert.NotEqual(first.OperationId, second.OperationId);
            Assert.Equal(first.OperationId, registry.Read(first.OperationId).OperationId);
        }

        [Fact]
        public void Epochs_skip_ids_the_owner_reports_in_use()
        {
            var rejected = new System.Collections.Generic.List<string>();
            var registry = new OperationRegistry(x => x, epochInUse: id =>
            {
                if (rejected.Count == 2) return false;
                rejected.Add(id);
                return true;
            });
            Assert.Equal(2, rejected.Count);
            Assert.True(ShortId.IsValid(registry.Epoch));
            Assert.DoesNotContain(registry.Epoch, rejected);
        }

        [Fact]
        public void Exhausted_collision_checks_fail_instead_of_reusing_an_id()
        {
            Assert.Throws<InvalidOperationException>(() => ShortId.New(_ => true));
        }

        [Fact]
        public async Task Slow_historical_id_checks_do_not_block_reads_or_existing_retries()
        {
            using var entered = new System.Threading.ManualResetEventSlim();
            using var release = new System.Threading.ManualResetEventSlim();
            bool block = false;
            var registry = new OperationRegistry(x => JsonConvert.DeserializeObject<OperationInfo>(JsonConvert.SerializeObject(x)),
                idInUse: _ =>
                {
                    if (block) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
                    return false;
                });
            var first = registry.Begin("build", "r1", "args", false, out _);
            block = true;
            var allocating = Task.Run(() => registry.Begin("launch", "r2", "args", false, out _));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                await Task.Run(() =>
                {
                    Assert.Equal(first.OperationId, registry.Read(first.OperationId).OperationId);
                    Assert.Equal(first.OperationId, registry.Begin("build", "r1", "args", false, out _).OperationId);
                }).WaitAsync(TimeSpan.FromSeconds(1));
            }
            finally { release.Set(); await allocating; }
        }

        [Theory]
        [InlineData("7b4n9x", true)]
        [InlineData("7b4n9x2m6k8q", true)]
        [InlineData("0123456789abcdef0123456789abcdef", true)]
        [InlineData("0123456789ABCDEF0123456789ABCDEF", true)]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("../7b4n9x2m6k8q", false)]
        [InlineData("..\\7b4n9x2m6k8q", false)]
        [InlineData("7b4n9x2m6k8q.json", false)]
        [InlineData("7b4n9x2m6k8q:ads", false)]
        [InlineData("7B4N9X2M6K8Q", false)]
        [InlineData("7b4n9", false)]
        [InlineData("7b4n9x2", false)]
        [InlineData("7b4n9x2m6k8", false)]
        [InlineData("7b4n9x2m6k8q0", false)]
        [InlineData("7b4n9i", false)]
        [InlineData("7B4N9X", false)]
        public void Capture_ids_accept_legacy_records_and_reject_paths(string id, bool valid)
        {
            Assert.Equal(valid, ShortId.IsCaptureId(id));
        }

        [Fact]
        public void Model_profile_metadata_hides_the_collector_guid_without_changing_persistence()
        {
            var profile = new ProfileCollection { CaptureId = ShortId.New(), HostEpoch = ShortId.New(),
                InstanceId = "App#42696", CollectorSessionId = Guid.NewGuid().ToString("N"), Status = "collecting" };
            var response = DebugStateResponse.From(new StateObservation { ActiveProfile = profile, LastProfile = profile });
            var json = System.Text.Json.JsonSerializer.Serialize(response);
            Assert.DoesNotContain("CollectorSessionId", json);
            Assert.DoesNotContain(profile.CollectorSessionId, json);
            Assert.Equal("42696", response.ActiveProfile.InstanceId);
            Assert.Contains(profile.CaptureId, json);
            Assert.Contains(profile.CollectorSessionId, JsonConvert.SerializeObject(profile));
        }
    }
}
