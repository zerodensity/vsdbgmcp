using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using VsDbgMcp.Shim.Discovery;
using VsDbgMcp.Shim.Session;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// A debuggee restarted outside the debugger, then attached to again.
    ///
    /// The first wait() answered "exited in host.exe (1724)" for the process of the
    /// previous run while the current one was still going. Read plainly that says the
    /// thing being debugged has died, and an agent has no way to tell it from the truth.
    /// These drive the real tools over a real pipe, because the fix is in what the shim
    /// does with what the extension pushes at it rather than in any one tool.
    /// </summary>
    public class StaleEventTests : IDisposable
    {
        readonly string _dir;
        readonly FakeHost _host;
        readonly SessionManager _sessions;

        public StaleEventTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-stale-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var pipe = "vsdbgmcp-stale-" + Guid.NewGuid().ToString("N");
            _host = new FakeHost(pipe) { InstanceId = "App#" + Process.GetCurrentProcess().Id };
            _host.Start();

            File.WriteAllText(
                Path.Combine(_dir, Names.InstanceFilePrefix + Process.GetCurrentProcess().Id + Names.InstanceFileSuffix),
                InstanceFile.Serialize(new InstanceRecord
                {
                    Pid = Process.GetCurrentProcess().Id,
                    Pipe = pipe,
                    Token = "t",
                    Contract = Names.ContractVersion,
                    DebugMode = DebugModes.Run,
                    Workspace = new WorkspaceInfo
                    {
                        Kind = WorkspaceKind.Sln,
                        Root = @"D:\repo",
                        File = @"D:\repo\App.sln",
                        Name = "App"
                    }
                }));

            _sessions = new SessionManager(@"D:\repo", new InstanceStore(_dir));
        }

        public void Dispose()
        {
            _sessions.Dispose();
            _host.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>Connects, so that what the host pushes next has somewhere to land.</summary>
        Task Connect() => new LifecycleTools(_sessions).Status(null, CancellationToken.None);

        void RunAndExit()
        {
            _host.RaiseModeChange(DebugModes.Run);
            _host.RaiseStop(new StopEvent
            {
                Reason = StopReason.Exited,
                ProcessName = "host.exe",
                Pid = 1724,
                ExitCode = 0,
                Mode = DebugModes.Design
            });
            _host.RaiseModeChange(DebugModes.Design);
        }

        [Fact]
        public async Task Wait_after_re_attaching_does_not_answer_with_the_previous_process_exiting()
        {
            await Connect();
            RunAndExit();

            await new LifecycleTools(_sessions).Attach(19808, null, null, CancellationToken.None);

            // Visual Studio leaves design mode as it attaches. Nothing in attach() asks
            // for the old session to be forgotten; hearing that is what does it.
            _host.RaiseModeChange(DebugModes.Run);

            var text = await new ExecutionTools(_sessions).Wait(1, null, null, CancellationToken.None);

            Assert.Contains("timeout", text);
            Assert.DoesNotContain("exited", text);
            Assert.DoesNotContain("1724", text);
        }

        /// <summary>
        /// The other half. A process that really did exit a moment ago is still the
        /// answer, however long the caller took to ask.
        /// </summary>
        [Fact]
        public async Task Wait_still_reports_a_process_that_has_just_exited()
        {
            await Connect();
            RunAndExit();

            var text = await new ExecutionTools(_sessions).Wait(1, null, null, CancellationToken.None);

            Assert.Contains("exited", text);
            Assert.Contains("host.exe (1724)", text);
            Assert.Contains("exit code 0", text);
        }
    }
}
