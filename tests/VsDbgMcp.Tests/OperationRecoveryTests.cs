using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using VsDbgMcp.Shim.Profiling;
using Xunit;

namespace VsDbgMcp.Tests
{
    public class OperationRecoveryTests
    {
        static OperationRegistry Registry() => new OperationRegistry(x => JsonConvert.DeserializeObject<OperationInfo>(JsonConvert.SerializeObject(x)));

        [Fact]
        public void Reconnect_invalidates_lost_mode_state_before_the_next_launch()
        {
            var bus = new VsDbgMcp.Shim.Session.EventBus();
            bus.InitializeMode("host", DebugModes.Design);
            bus.ModeChanged("host", DebugModes.Run);
            var previous = bus.Generation("host");
            bus.InitializeMode("host", DebugModes.Design); // Exit occurred while disconnected.
            Assert.True(bus.Generation("host") > previous);
            var reconnected = bus.Generation("host");
            bus.ObserveStopMode("host", DebugModes.Break); // Before delayed mode callback.
            Assert.True(bus.Generation("host") > reconnected);
            var launched = bus.Generation("host");
            bus.ModeChanged("host", DebugModes.Break);
            Assert.Equal(launched, bus.Generation("host"));
        }

        [Fact]
        public async Task A_stalled_update_does_not_block_status_or_other_operations()
        {
            var registry = Registry();
            var first = registry.Begin("launch", null, "a", true, out _);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var mutation = Task.Run(() => registry.Update(first.OperationId, o =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                o.State = "running";
            }, true));
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                await Task.Run(async () =>
                {
                    Assert.Single(registry.All());
                    Assert.Equal("requested", (await registry.WaitAsync(first.OperationId, 0, CancellationToken.None)).State);
                    registry.Begin("build", null, "b", true, out _);
                }).WaitAsync(TimeSpan.FromSeconds(2));
            }
            finally { release.Set(); await mutation; }
            Assert.True(registry.Read(first.OperationId).Terminal);
        }

        [Fact]
        public async Task Completion_before_wait_is_retained_and_query_does_not_restart()
        {
            var registry = Registry();
            var first = registry.Begin("build", "r1", "Debug|x64", true, out var created);
            Assert.True(created);
            registry.Update(first.OperationId, o => o.State = "succeeded", true);
            var result = await registry.WaitAsync(first.OperationId, 1, CancellationToken.None);
            Assert.Equal("succeeded", result.State);
            var again = registry.Begin("build", "r1", "Debug|x64", true, out created);
            Assert.False(created);
            Assert.Equal(first.OperationId, again.OperationId);
            Assert.Single(registry.All());
        }

        [Fact]
        public async Task Cancel_and_completion_race_cannot_change_a_terminal_result()
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var registry = Registry();
                var first = registry.Begin("build", "r1", "build", true, out _);
                await Task.WhenAll(Task.Run(() => registry.Update(first.OperationId, o => o.State = "succeeded", true)),
                    Task.Run(() => registry.Update(first.OperationId, o => { o.CancelRequested = true; o.State = "cancelling"; })));
                Assert.Equal("succeeded", registry.Read(first.OperationId).State);
                Assert.True(registry.Read(first.OperationId).Terminal);
            }
        }

        [Fact]
        public async Task Caller_cancellation_leaves_the_host_operation_pending()
        {
            var registry = Registry();
            var first = registry.Begin("build", null, "build", true, out _);
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.WaitAsync(first.OperationId, 1, cancel.Token));
            }
            Assert.False(registry.Read(first.OperationId).Terminal);
            Assert.Throws<InvalidOperationException>(() => registry.Begin("build", null, "other", true, out _));
            registry.Update(first.OperationId, o => o.State = "succeeded", true);
            Assert.Equal("succeeded", (await registry.WaitAsync(first.OperationId, 0, CancellationToken.None)).State);
        }

        [Fact]
        public void Duplicate_requests_must_have_identical_arguments_and_snapshots_are_isolated()
        {
            var registry = Registry();
            var first = registry.Begin("breakpoint", "r1", "function=Work", false, out _);
            Assert.Throws<InvalidOperationException>(() => registry.Begin("breakpoint", "r1", "function=Other", false, out _));
            first.State = "succeeded";
            Assert.Equal("requested", registry.Read(first.OperationId).State);
        }

        [Fact]
        public async Task Journal_failure_is_visible_without_losing_the_terminal_result()
        {
            var registry = new OperationRegistry(x => JsonConvert.DeserializeObject<OperationInfo>(JsonConvert.SerializeObject(x)),
                _ => throw new IOException("disk full"));
            var first = registry.Begin("build", null, "build", true, out _);
            registry.Update(first.OperationId, o => o.State = "succeeded", true);
            var result = await registry.WaitAsync(first.OperationId, 1, CancellationToken.None);
            Assert.Equal("succeeded", result.State);
            Assert.Contains("disk full", result.PersistenceError);
        }

        [Fact]
        public void Unknown_build_outcome_is_not_rendered_as_failure()
        {
            var report = Render.Build(new BuildResult { OperationId = "build-1", State = "unknown", EvidenceSource = "solution changed" });
            Assert.Contains("unknown", report);
            Assert.DoesNotContain("FAILED", report);
        }

        [Fact]
        public void Completed_and_pending_operations_both_expose_their_recovery_id()
        {
            Assert.Contains("launch-1", Render.Op(new OpResult { Ok = true, OperationId = "launch-1", State = "running" }, "Running").Text);
            Assert.Contains("pending", Render.Op(new OpResult { Ok = true, Pending = true, OperationId = "profile-1", State = "starting" }, "Reserved").Text);
            Assert.Contains("bp-1", Render.Breakpoint(new BreakpointInfo { Id = 1, Bound = true, OperationId = "bp-1" }));
        }

        [Fact]
        public void Diagnostics_have_invocation_ownership_and_occurrences_are_distinct_from_unique_counts()
        {
            var log = "1>C:\\src\\foo.cpp(42,7): warning C4244: conversion\r\n" +
                      "1>C:\\src\\foo.cpp(42,7): warning C4244: conversion\r\n" +
                      "2>C:\\src\\bar.cpp(3): warning C4267: narrowing\r\n" +
                      "CUSTOMBUILD : error MSB8066: command exited with code 1";
            var diagnostics = BuildDiagnostics.Parse(log, "build-1", "Debug|x64", DateTime.UtcNow);
            Assert.Equal(4, diagnostics.Count);
            Assert.Equal(2, BuildDiagnostics.Unique(diagnostics, "warning"));
            Assert.All(diagnostics, d => Assert.Equal("build-1", d.OperationId));
            Assert.Contains(diagnostics, d => d.Code == "MSB8066" && d.Severity == "error");
            Assert.Empty(BuildDiagnostics.Parse("Build succeeded", "build-2", "Release|x64", DateTime.UtcNow));
        }

        [Fact]
        public void Unknown_diagnostic_coverage_is_never_rendered_as_a_definitive_zero()
        {
            var report = Render.Build(new BuildResult { Succeeded = true, OperationId = "build-1", DiagnosticsComplete = false });
            Assert.Contains("counts: unknown", report);
            Assert.Contains("parsed", report);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Persisted_aggregates_survive_a_new_store_and_export_without_a_debugger(bool legacy)
        {
            var root = Path.Combine(Path.GetTempPath(), "vsdbgmcp-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new Captures(Path.Combine(root, "store"));
                var capture = Example();
                if (legacy) capture.CaptureId = Guid.NewGuid().ToString("N");
                store.Keep(capture);
                if (!legacy) Assert.True(ShortId.IsValid(capture.CaptureId));
                Assert.Same(capture, store.Keep(capture));
                var reconnected = new Captures(Path.Combine(root, "store"));
                var restored = reconnected.FindStable(capture.CaptureId);
                Assert.NotNull(restored);
                Assert.Equal(2, restored.Stacks.Count);
                Assert.Equal(2, restored.Metadata.SessionGeneration);
                var export = reconnected.Export(restored, Path.Combine(root, "export"), false);
                Assert.Contains(".json", export);
                Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "export")).Length);
                Assert.Throws<IOException>(() => reconnected.Export(restored, Path.Combine(root, "export"), false));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Fact]
        public void Module_thread_and_tree_filters_preserve_the_thread_denominator()
        {
            var capture = Example();
            var report = Render.Profile(capture, new ProfileQuery { Module = "plugin", Thread = 7, Sort = "inclusive", Details = false, RawTree = true }, null, new[] { capture });
            Assert.Contains("10 stacked samples in selected thread", report);
            Assert.Contains("plugin!Work", report);
            Assert.DoesNotContain("OtherThread", report);
            Assert.DoesNotContain("app!Root", report);
        }

        [Fact]
        public void Compact_reports_keep_ownership_and_tiny_costs_are_visible()
        {
            var capture = Example();
            capture.Stacks[1].Samples = 100000;
            var report = Render.Profile(capture, new ProfileQuery { Details = false }, null, new[] { capture });
            Assert.Contains("generation 2", report);
            Assert.Contains("<0.1%", report);
            Assert.DoesNotContain("captures:", report);
        }

        [Fact]
        public void Retention_preserves_active_traces_and_removes_oldest_completed_files_first()
        {
            var now = DateTime.UtcNow;
            var records = new[] {
                new RetainedArtifact { Path = "active", Bytes = 10, TimestampUtc = now.AddDays(-30), Protected = true },
                new RetainedArtifact { Path = "old", Bytes = 10, TimestampUtc = now.AddDays(-10) },
                new RetainedArtifact { Path = "recent", Bytes = 10, TimestampUtc = now } };
            var removed = RetentionPolicy.Expired(records, now, 7, 20);
            Assert.Single(removed);
            Assert.Equal("old", removed[0].Path);
        }

        static Capture Example()
        {
            var capture = new Capture { Pid = 123, ProcessName = "test", Seconds = 1, Samples = 20, Stacked = 20,
                Metadata = new ProfileCollection { SessionGeneration = 2, InstanceId = "vs1", HostEpoch = "epoch" } };
            capture.Frames.Add(new Capture.Frame { Module = "app", Method = "Root" });
            capture.Frames.Add(new Capture.Frame { Module = "plugin", Method = "Work", UserCode = true });
            capture.Frames.Add(new Capture.Frame { Module = "plugin", Method = "OtherThread", UserCode = true });
            capture.Stacks.Add(new Capture.Stack { Frames = new[] { 0, 1 }, Samples = 10, ThreadId = 7 });
            capture.Stacks.Add(new Capture.Stack { Frames = new[] { 0, 2 }, Samples = 10, ThreadId = 8 });
            return capture;
        }
    }
}
