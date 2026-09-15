using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Discovery;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// The stream a background monitor watches. It has to be a real process: what is
    /// being tested is that a line reaches stdout as it happens and that the process
    /// ends when the client does.
    /// </summary>
    public class FollowTests : IDisposable
    {
        readonly string _dir;
        readonly FakeHost _host;
        Process _follow;

        public FollowTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-follow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var pipe = "vsdbgmcp-follow-" + Guid.NewGuid().ToString("N");
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
                        Root = @"D:\repo\Engine",
                        File = @"D:\repo\Engine\App.sln",
                        Name = "App"
                    }
                }));
        }

        public void Dispose()
        {
            try { if (_follow != null && !_follow.HasExited) _follow.Kill(true); } catch { }
            _follow?.Dispose();
            _host.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        async Task<Process> StartAsync(params string[] extra)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            start.ArgumentList.Add(typeof(EvidenceTools).Assembly.Location);
            start.ArgumentList.Add("--follow");
            start.ArgumentList.Add("--cwd");
            start.ArgumentList.Add(@"D:\repo\Engine");
            start.ArgumentList.Add("--instance");
            start.ArgumentList.Add("any");
            foreach (var argument in extra) start.ArgumentList.Add(argument);
            start.Environment["VSDBGMCP_DATA_DIR"] = _dir;

            _follow = Process.Start(start);

            // Connecting is reported on stderr, and nothing can be seen until it has.
            var connected = await _follow.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.StartsWith("connected", connected);
            return _follow;
        }

        [Fact]
        public async Task A_stop_becomes_one_line_on_stdout()
        {
            var follow = await StartAsync();

            _host.RaiseStop(new StopEvent
            {
                Reason = StopReason.Breakpoint, BreakpointId = 3, ThreadId = 15224,
                Frame = new Frame { Function = "Mesh::Upload", File = @"D:\repo\Engine\main.cpp", Line = 42 }
            });

            var line = await follow.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("stopped at breakpoint 3, main.cpp:42 in Mesh::Upload", line);
        }

        [Fact]
        public async Task A_matched_Debug_pane_line_is_printed_too()
        {
            var follow = await StartAsync("--match", "ERROR");

            _host.RaiseOutput(new OutputEvent { Pane = "Debug", Text = "loading shaders\r\nERROR: device lost\r\n" });

            var line = await follow.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("output:", line);
            Assert.Contains("ERROR: device lost", line);
        }

        [Fact]
        public async Task It_ends_when_the_client_closes_its_end()
        {
            var follow = await StartAsync();

            follow.StandardInput.Close();

            Assert.True(follow.WaitForExit(15000));
        }
    }
}
