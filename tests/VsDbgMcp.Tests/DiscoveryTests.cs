using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using VsDbgMcp.Shim.Discovery;
using Xunit;

namespace VsDbgMcp.Tests
{
    public class DiscoveryTests : IDisposable
    {
        readonly string _dir;

        public DiscoveryTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-disc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        static InstanceRecord Sample(int pid) => new InstanceRecord
        {
            Pid = pid,
            Pipe = Names.PipeName(pid),
            Token = "abc123",
            VsVersion = "17.14.0",
            Contract = Names.ContractVersion,
            ProjectDirs = new[] { @"D:\repo\Engine\src" },
            Capabilities = new[] { Capabilities.Native, Capabilities.DataBreakpoints },
            DebugMode = DebugModes.Break,
            StartedAt = "2026-08-18T10:12:03Z",
            Workspace = new WorkspaceInfo
            {
                Kind = WorkspaceKind.Slnx,
                Root = @"D:\repo\Engine",
                File = @"D:\repo\Engine\App.slnx",
                Name = "App"
            }
        };

        void WriteRecord(InstanceRecord record)
        {
            File.WriteAllText(
                Path.Combine(_dir, Names.InstanceFilePrefix + record.Pid + Names.InstanceFileSuffix),
                InstanceFile.Serialize(record));
        }

        [Fact]
        public void The_hand_written_record_round_trips_through_a_real_json_reader()
        {
            // The extension writes this without a JSON library, so the two sides have to
            // be checked against each other rather than assumed compatible.
            var json = InstanceFile.Serialize(Sample(4242));

            var parsed = JsonSerializer.Deserialize<InstanceRecord>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            Assert.Equal(4242, parsed.Pid);
            Assert.Equal("vsdbgmcp-4242", parsed.Pipe);
            Assert.Equal("abc123", parsed.Token);
            Assert.Equal(WorkspaceKind.Slnx, parsed.Workspace.Kind);
            Assert.Equal(@"D:\repo\Engine", parsed.Workspace.Root);
            Assert.Equal("App", parsed.Workspace.Name);
            Assert.Equal("App#4242", parsed.Id);
            Assert.Contains(Capabilities.DataBreakpoints, parsed.Capabilities);
        }

        [Fact]
        public void Backslashes_in_paths_survive_serialization()
        {
            var json = InstanceFile.Serialize(Sample(1));
            Assert.Contains(@"D:\\repo\\Engine", json);
        }

        [Fact]
        public void A_record_for_a_live_process_is_discovered()
        {
            var record = Sample(Process.GetCurrentProcess().Id);
            WriteRecord(record);

            var found = new InstanceStore(_dir).Discover();

            Assert.Single(found);
            Assert.Equal(record.Pid, found[0].Pid);
        }

        [Fact]
        public void A_record_for_a_dead_process_is_pruned()
        {
            // A pid this large is not in use; the file stands in for one left behind by
            // a Visual Studio that crashed.
            WriteRecord(Sample(0x7FFFFFF0));

            var found = new InstanceStore(_dir).Discover();

            Assert.Empty(found);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public void A_corrupt_record_is_ignored_rather_than_thrown_on()
        {
            File.WriteAllText(Path.Combine(_dir, "inst-999999.json"), "{ this is not json");
            WriteRecord(Sample(Process.GetCurrentProcess().Id));

            var found = new InstanceStore(_dir).Discover();

            Assert.Single(found);
        }

        [Fact]
        public void An_empty_directory_yields_nothing_and_does_not_throw()
        {
            Assert.Empty(new InstanceStore(_dir).Discover());
            Assert.Empty(new InstanceStore(Path.Combine(_dir, "does-not-exist")).Discover());
        }
    }
}
