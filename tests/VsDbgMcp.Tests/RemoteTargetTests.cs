using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// A debuggee reached over msvsmon.
    ///
    /// The failure these cover: nothing in status told a local process from one on
    /// another machine, so a pid missing from the local process list and paths that do
    /// not exist on this disk read as a session that had gone stale. Both observations
    /// were right, and both are what remote debugging looks like.
    /// </summary>
    public class RemoteTargetTests
    {
        static HostStatus Status(params ProcessInfo[] processes) => new HostStatus
        {
            InstanceId = "Engine#42696",
            Mode = DebugModes.Break,
            Processes = new List<ProcessInfo>(processes),
            BreakpointCount = 3
        };

        [Fact]
        public void A_remote_target_is_named_with_its_machine_and_transport()
        {
            var text = Render.Status(Status(new ProcessInfo
            {
                Pid = 1724,
                Name = "host.exe",
                IsDebugged = true,
                IsRemote = true,
                Machine = "BUILDBOX",
                Transport = "TCP/IP"
            }));

            Assert.Contains("host.exe (1724) runs on BUILDBOX over TCP/IP", text);
            Assert.Contains("is on that machine", text);
            Assert.Contains("not one this machine's process list will show", text);
        }

        [Fact]
        public void A_remote_target_the_engine_would_not_name_still_says_it_is_remote()
        {
            var text = Render.Status(Status(new ProcessInfo
            {
                Pid = 1724,
                Name = "host.exe",
                IsDebugged = true,
                IsRemote = true
            }));

            Assert.Contains("runs on another machine", text);
            Assert.DoesNotContain(" over ", text);
        }

        [Fact]
        public void A_local_session_says_nothing_about_machines()
        {
            var text = Render.Status(Status(new ProcessInfo { Pid = 1724, Name = "host.exe", IsDebugged = true }));

            Assert.Contains("processes: host.exe (1724)", text);
            Assert.DoesNotContain("remote", text);
            Assert.DoesNotContain("machine", text);
        }
    }
}
