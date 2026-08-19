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
    /// A debug session holding a launcher and the editor it started.
    ///
    /// The failure these cover: the surface used to see only the process that last
    /// stopped, so a thread id from the other one came back as "no such thread" even
    /// though the process was plainly listed. A caller had no way through.
    /// </summary>
    public class MultiProcessTests : IDisposable
    {
        readonly string _dir;
        readonly FakeHost _host;
        readonly SessionManager _sessions;

        public MultiProcessTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-mp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var pipe = "vsdbgmcp-mp-" + Guid.NewGuid().ToString("N");
            _host = new FakeHost(pipe);
            _host.Start();

            File.WriteAllText(
                Path.Combine(_dir, Names.InstanceFilePrefix + Process.GetCurrentProcess().Id + Names.InstanceFileSuffix),
                InstanceFile.Serialize(new InstanceRecord
                {
                    Pid = Process.GetCurrentProcess().Id,
                    Pipe = pipe,
                    Token = "t",
                    Contract = Names.ContractVersion,
                    DebugMode = DebugModes.Break,
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

        [Fact]
        public async Task Threads_shows_every_process_in_the_session()
        {
            var text = await new InspectionTools(_sessions).Threads(2, null, null, CancellationToken.None);

            Assert.Contains("across 2 processes", text);
            Assert.Contains("nosEditor.exe (4001)", text);
            Assert.Contains("nosLauncher.exe (4002)", text);

            // The launcher's thread id has to be readable, or there is no way to act on it.
            Assert.Contains("200", text);
            Assert.Contains("Launcher::Pump", text);
        }

        [Fact]
        public async Task Threads_can_be_narrowed_to_one_process()
        {
            var text = await new InspectionTools(_sessions).Threads(2, "nosLauncher", null, CancellationToken.None);

            Assert.Contains("nosLauncher.exe", text);
            Assert.DoesNotContain("Editor::Tick", text);
        }

        [Fact]
        public async Task A_process_can_be_selected_by_name()
        {
            var text = await new InspectionTools(_sessions)
                .Select(null, "nosLauncher", null, null, CancellationToken.None);

            Assert.Contains("nosLauncher.exe (4002)", text);
        }

        [Fact]
        public async Task A_process_can_be_selected_by_pid()
        {
            var text = await new InspectionTools(_sessions)
                .Select(null, "4002", null, null, CancellationToken.None);

            Assert.Contains("nosLauncher.exe", text);
        }

        [Fact]
        public async Task A_thread_in_another_process_can_be_selected_by_id()
        {
            var text = await new InspectionTools(_sessions)
                .Select(200, null, null, null, CancellationToken.None);

            Assert.Contains("200", text);
            Assert.DoesNotContain("No thread", text);
        }

        [Fact]
        public async Task An_unknown_thread_names_the_threads_that_do_exist()
        {
            var text = await new InspectionTools(_sessions)
                .Select(9999, null, null, null, CancellationToken.None);

            // The error is the fix: it says which ids exist and which process each is in.
            Assert.Contains("No thread 9999", text);
            Assert.Contains("nosEditor.exe (4001): 100, 101", text);
            Assert.Contains("nosLauncher.exe (4002): 200", text);
        }

        [Fact]
        public async Task An_unknown_process_names_the_ones_that_do_exist()
        {
            var text = await new InspectionTools(_sessions)
                .Select(null, "nosNothing", null, null, CancellationToken.None);

            Assert.Contains("No process matching 'nosNothing'", text);
            Assert.Contains("nosLauncher.exe", text);
        }

        [Fact]
        public async Task A_stop_says_which_process_stopped()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            var waiting = new ExecutionTools(_sessions).Wait(10, null, CancellationToken.None);

            await Task.Delay(100);
            _host.RaiseStop(new StopEvent
            {
                Reason = StopReason.Breakpoint,
                ThreadId = 200,
                ProcessName = "nosLauncher.exe",
                Pid = 4002,
                Frame = new Frame { Function = "Launcher::Pump", File = "launcher.cpp", Line = 88 }
            });

            var text = await waiting;

            // Two processes and "stopped: breakpoint" alone is half an answer.
            Assert.Contains("stopped: breakpoint in nosLauncher.exe (4002)", text);
        }
    }
}
