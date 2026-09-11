using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;
using VsDbgMcp.Host;
using Xunit;

// The production lifecycle and collector sources are compiled into this test
// assembly. Only the VS/UI boundary is replaced; no Visual Studio is launched.
namespace Microsoft.VisualStudio.Shell
{
    static class ThreadHelper { public static void ThrowIfNotOnUIThread() { } }
}
namespace VsDbgMcp.Host
{
    static class Names { public static string InstanceDir; }
    static class HostOperations
    {
        public static OperationRegistry Store;
        public static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
        public static void Run(string id, Func<Task> body) => _ = Task.Run(async () =>
        {
            try { await body(); }
            catch (Exception ex) { Store.Update(id, o => { o.State = "failed"; o.Message = ex.Message; }, true); }
        });
    }
    partial class DebugHost
    {
        readonly object _historyGate = new object();
        readonly List<Intervention> _interventions = new List<Intervention>();
        int _sessionGeneration = 1;
        ProfileCollection _activeProfile;
        ProfileCollection _lastProfile;
        Profiler _profiler;
        ProcessInfo _profiled;
        public List<ModuleInfo> TestModules = new List<ModuleInfo>();
        public Func<Task> TestUiGate;
        public Func<List<ModuleInfo>> TestReadModules;
        public int TestUiCalls;
        public string CurrentMode = DebugModes.Run;
        public DebugHost(ProfileCollection collection, Profiler profiler) { _activeProfile = collection; _profiler = profiler; }
        public ProfileCollection Active => _activeProfile;
        public void SetCapture(ProfileCollection collection, Profiler profiler) { _activeProfile = collection; _profiler = profiler; }
        async Task<T> UIAsync<T>(Func<T> body)
        {
            System.Threading.Interlocked.Increment(ref TestUiCalls);
            if (TestUiGate != null) await TestUiGate();
            return body();
        }
        void Mark(string kind) { lock (_historyGate) _interventions.Add(new Intervention { Kind = kind, TimestampUtc = DateTime.UtcNow }); }
        List<ModuleInfo> ProfileModules(int pid) => TestReadModules == null ? TestModules : TestReadModules();
        string DevenvPath() => "not-used";
        string InstanceId() => "test-instance";
        string ActiveConfiguration() => "Debug|x64";
        ProcessInfo ProfileTarget(out string refusal) { refusal = null; return new ProcessInfo { Pid = 42, Name = "test.exe" }; }
    }
}
namespace VsDbgMcp.Tests
{
    public class HostProfileLifecycleTests : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "vsdbg-host-profile-" + Guid.NewGuid().ToString("N"));
        public HostProfileLifecycleTests()
        {
            VsDbgMcp.Host.Names.InstanceDir = _dir;
            HostOperations.Store = new OperationRegistry(HostOperations.Copy);
            Directory.CreateDirectory(Path.Combine(_dir, "profiles", "collections"));
        }
        public void Dispose() { Directory.Delete(_dir, true); }
        ProfileCollection Collection(string status = "collecting", bool legacy = false)
        {
            var id = legacy ? Guid.NewGuid().ToString("N") : ShortId.New();
            var root = Path.Combine(_dir, "profiles", "collections", id);
            var result = new ProfileCollection { CaptureId = id, CollectorSessionId = legacy ? id : Guid.NewGuid().ToString("N"), Path = root + ".diagsession",
                MetadataPath = root + ".json", StartedUtc = DateTime.UtcNow.AddSeconds(-5), SessionGeneration = 1, HostEpoch = HostOperations.Store.Epoch,
                Status = status, Pid = 42, Modules = new List<ModuleInfo>() };
            File.WriteAllText(result.MetadataPath, JsonConvert.SerializeObject(result));
            return result;
        }
        static void Package(string path)
        {
            using (var file = File.Create(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            using (var data = zip.CreateEntry("trace.etl").Open()) data.Write(new byte[] { 1, 2, 3 }, 0, 3);
        }
        static Profiler Collector(ProfileCollection collection, Func<string, Task<string>> command)
        {
            var result = new Profiler("unused", "unused", command);
            result.RecoverSession(collection.CollectorSessionId, collection.Path, collection.StartedUtc);
            return result;
        }

        [Fact]
        public async Task Start_issues_short_handles_and_keeps_the_collector_guid_private()
        {
            var commands = new List<string>();
            DebugHost host = null;
            var profiler = new Profiler("unused", "unused", command =>
            {
                commands.Add(command);
                if (command.StartsWith("stop ")) Package(host.Active.Path);
                return Task.FromResult<string>(null);
            });
            host = new DebugHost(null, profiler);
            var started = await host.ProfileBeginAsync(true);
            Assert.True(started.Ok);
            Assert.True(ShortId.IsValid(started.OperationId));
            var active = host.Active;
            Assert.True(ShortId.IsValid(active.CaptureId));
            Assert.True(Guid.TryParseExact(active.CollectorSessionId, "N", out _));
            Assert.NotEqual(active.CaptureId, active.CollectorSessionId);
            Assert.StartsWith("start " + active.CollectorSessionId + " ", commands.Single());
            var recovered = await host.ProfileRecoverAsync(active.CaptureId);
            Assert.Equal(active.CollectorSessionId, recovered.CollectorSessionId);
            var stopped = await host.ProfileStopCaptureAsync(active.CaptureId);
            Assert.Null(stopped.Error);
            Assert.StartsWith("stop " + active.CollectorSessionId + " ", commands.Last());
            Assert.Equal(active.CaptureId, stopped.CaptureId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Retained_short_and_legacy_captures_recover_and_stop_the_owned_collector(bool legacy)
        {
            var collection = Collection(legacy: legacy);
            var host = new DebugHost(collection, Collector(collection, command =>
            {
                Assert.StartsWith("stop " + collection.CollectorSessionId + " ", command);
                Package(collection.Path);
                return Task.FromResult<string>(null);
            }));
            Assert.Equal(collection.CollectorSessionId, (await host.ProfileRecoverAsync(collection.CaptureId)).CollectorSessionId);
            var stopped = await host.ProfileStopCaptureAsync(collection.CaptureId);
            Assert.Null(stopped.Error);
            Assert.Equal(collection.CaptureId, stopped.CaptureId);
        }

        [Fact]
        public async Task Concurrent_stops_share_one_collector_call_and_refuse_a_different_capture()
        {
            var a = Collection();
            var b = Collection();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var collector = Collector(a, async _ => { calls++; entered.SetResult(true); await release.Task; Package(a.Path); return null; });
            var host = new DebugHost(a, collector);
            var first = host.ProfileStopAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = host.ProfileStopCaptureAsync(a.CaptureId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ProfileStopCaptureAsync(b.CaptureId));
            release.SetResult(true);
            var results = await Task.WhenAll(first, second);
            Assert.Equal(1, calls);
            Assert.All(results, r => { Assert.Equal(a.CaptureId, r.CaptureId); Assert.Null(r.Error); });
            Assert.Null(host.Active);
        }

        [Fact]
        public async Task Each_stop_retains_its_own_result_after_a_later_capture()
        {
            var a = Collection();
            var host = new DebugHost(a, Collector(a, _ => { Package(a.Path); return Task.FromResult<string>(null); }));
            await host.ProfileStopAsync();
            var operation = HostOperations.Store.All().Single();
            var b = Collection();
            host.SetCapture(b, Collector(b, _ => { Package(b.Path); return Task.FromResult<string>(null); }));
            await host.ProfileStopAsync();
            Assert.Equal(a.CaptureId, HostOperations.Store.Read(operation.OperationId).Profile.CaptureId);
            Assert.Equal(a.CaptureId, (await host.ProfileStopCaptureAsync(a.CaptureId)).CaptureId);
        }

        [Fact]
        public async Task Failed_final_metadata_write_preserves_result_and_releases_active_capture()
        {
            var collection = Collection();
            var collector = Collector(collection, _ =>
            {
                Package(collection.Path);
                collection.MetadataPath = Path.Combine(_dir, "missing-directory", "manifest.json");
                return Task.FromResult<string>(null);
            });
            var host = new DebugHost(collection, collector);
            var result = await host.ProfileStopAsync();
            Assert.Null(result.Error);
            Assert.Equal("collected", result.Status);
            Assert.Null(host.Active);
            Assert.NotNull(HostOperations.Store.All().Single().PersistenceError);
            Assert.Equal(collection.CaptureId, HostOperations.Store.All().Single().Profile.CaptureId);
        }

        [Fact]
        public async Task Closed_package_with_stopping_manifest_recovers_without_a_collector()
        {
            var collection = Collection("stopping");
            Package(collection.Path);
            var host = new DebugHost(null, null);
            var result = await host.ProfileStopCaptureAsync(collection.CaptureId);
            Assert.Equal("interrupted", result.Status);
            Assert.Null(result.Error);
            Assert.Null(host.Active);
            Assert.Contains(result.Interventions, m => m.Kind == "recovered-closed-package");
            Assert.NotNull(result.EndedUtc);
            Assert.True(result.Seconds > 0);
        }

        [Fact]
        public async Task Collector_does_not_stop_an_already_closed_session_again()
        {
            var collection = Collection("stopping");
            Package(collection.Path);
            var collector = Collector(collection, _ => throw new Exception("A second stop must not run"));
            Assert.Null((await collector.StopAsync()).Error);
            Assert.False(collector.Running);
        }

        [Fact]
        public async Task Stop_refreshes_late_modules_and_preserves_previously_loaded_symbols()
        {
            var collection = Collection();
            collection.Modules.Add(new ModuleInfo { Name = "old.dll", Path = "old.dll", SymbolsLoaded = true, SymbolPath = "old.pdb" });
            var host = new DebugHost(collection, Collector(collection, _ => { Package(collection.Path); return Task.FromResult<string>(null); }));
            host.TestModules.Add(new ModuleInfo { Name = "plugin.dll", Path = "plugin.dll", SymbolsLoaded = true, SymbolPath = "plugin.pdb" });
            host.TestModules.Add(new ModuleInfo { Name = "old.dll", Path = "old.dll" });
            var result = await host.ProfileStopAsync();
            Assert.Equal(2, result.Modules.Count);
            Assert.Equal("plugin.pdb", result.Modules.Single(m => m.Name == "plugin.dll").SymbolPath);
            Assert.Equal("old.pdb", result.Modules.Single(m => m.Name == "old.dll").SymbolPath);
            var saved = JsonConvert.DeserializeObject<ProfileCollection>(File.ReadAllText(collection.MetadataPath));
            Assert.Equal(2, saved.Modules.Count);
        }

        [Fact]
        public void Open_or_incomplete_packages_are_not_reconciled()
        {
            var collection = Collection("stopping");
            File.WriteAllText(collection.Path, "incomplete");
            Assert.False(ProfileArtifacts.HasClosedTrace(collection.Path));
            Package(collection.Path);
            using (var locked = new FileStream(collection.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(ProfileArtifacts.HasClosedTrace(collection.Path));
            Assert.True(ProfileArtifacts.HasClosedTrace(collection.Path));
        }

        [Fact]
        public async Task Module_bursts_share_the_actual_ui_query_even_after_observation_times_out()
        {
            var collection = Collection();
            var host = new DebugHost(collection, Collector(collection, _ => { Package(collection.Path); return Task.FromResult<string>(null); }));
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.TestUiGate = async () => { entered.TrySetResult(true); await release.Task; };
            for (var i = 0; i < 100; i++) host.ProfileModulesChanged();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1200);
            for (var i = 0; i < 100; i++) host.ProfileModulesChanged();
            Assert.Equal(1, host.TestUiCalls);
            Assert.True(collection.RetainRaw);
            release.SetResult(true);
            var stopped = await host.ProfileStopAsync();
            Assert.Null(stopped.Error);
        }

        [Fact]
        public async Task A_load_after_the_snapshot_forces_a_fresh_catalog_before_stop_completes()
        {
            var collection = Collection();
            var host = new DebugHost(collection, Collector(collection, _ => { Package(collection.Path); return Task.FromResult<string>(null); }));
            var captured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new System.Threading.ManualResetEventSlim();
            int reads = 0;
            host.TestReadModules = () =>
            {
                if (System.Threading.Interlocked.Increment(ref reads) != 1)
                    return new List<ModuleInfo> { new ModuleInfo { Name = "late.dll", SymbolPath = "late.pdb", SymbolsLoaded = true } };
                var oldSnapshot = new List<ModuleInfo>();
                captured.SetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                return oldSnapshot;
            };
            host.ProfileModulesChanged();
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            host.ProfileModulesChanged();
            var stopping = host.ProfileStopAsync();
            release.Set();
            var result = await stopping;
            Assert.Null(result.Error);
            Assert.Contains(result.Modules, m => m.Name == "late.dll");
            Assert.True(reads >= 2);
        }
    }
}
