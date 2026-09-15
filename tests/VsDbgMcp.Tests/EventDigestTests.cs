using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Discovery;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// The model learns what happened through the calls it already makes. This is the
    /// whole mechanism: a block at the top of the reply, and nothing at all when
    /// nothing happened.
    /// </summary>
    public class EventDigestTests : IDisposable
    {
        readonly string _dir;
        readonly FakeHost _host;

        public EventDigestTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-digest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var pipe = "vsdbgmcp-digest-" + Guid.NewGuid().ToString("N");
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
                    DebugMode = DebugModes.Break,
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
            _host.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public async Task Events_arrive_at_the_top_of_the_next_reply_and_only_once()
        {
            using var server = await Server.StartAsync(_dir);

            // Connects the shim to the fake window, so pushes have somewhere to land.
            await server.CallAsync("status", new { });

            _host.RaiseOperationChanged(new OperationInfo
            {
                OperationId = "ab12", Kind = "build", State = "succeeded", Terminal = true,
                Build = new BuildResult { TotalErrors = 2 }
            });
            await Task.Delay(300);

            var carried = await server.CallAsync("bp_list", new { });
            var first = carried.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.StartsWith("Since your last call:", first);
            Assert.Contains("build done: 2 errors (operation ab12)", first);

            // A blank line closes the block, so the tool's own first line cannot be read
            // as one more event however the client joins the content blocks.
            Assert.EndsWith("\n\n", first);

            var next = await server.CallAsync("bp_list", new { });
            Assert.DoesNotContain("Since your last call",
                next.GetProperty("content")[0].GetProperty("text").GetString());
        }

        [Fact]
        public async Task A_stop_changes_the_state_a_failed_call_was_read_from()
        {
            using var server = await Server.StartAsync(_dir);
            await server.CallAsync("status", new { });

            _host.RaiseStop(new StopEvent { Reason = StopReason.Exited, ExitCode = 3, ProcessName = "myapp.exe" });
            await Task.Delay(300);

            var failed = await server.CallAsync("bp_list", new { instance = "Missing#999999" });
            Assert.True(failed.GetProperty("isError").GetBoolean());

            var first = failed.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.StartsWith("Since your last call, state changed:", first);
            Assert.Contains("exited with code 3 (myapp.exe)", first);
        }

        [Fact]
        public async Task A_structured_reply_keeps_its_structure()
        {
            using var server = await Server.StartAsync(_dir);
            await server.CallAsync("status", new { });

            _host.RaiseOperationChanged(new OperationInfo
            {
                OperationId = "cd34", Kind = "build", State = "succeeded", Terminal = true,
                Build = new BuildResult { Succeeded = true }
            });
            await Task.Delay(300);

            var result = await server.CallAsync("operation_status", new { operationId = "cd34" });
            Assert.StartsWith("Since your last call:",
                result.GetProperty("content")[0].GetProperty("text").GetString());
            Assert.True(result.TryGetProperty("structuredContent", out _));
        }

        [Fact]
        public async Task The_server_says_how_events_reach_the_model()
        {
            using var server = await Server.StartAsync(_dir);

            var instructions = server.Initialized.GetProperty("instructions").GetString();

            Assert.Contains("Since your last call", instructions);
            Assert.Contains("for='any'", instructions);
            Assert.Contains("--follow", instructions);
            Assert.Contains(@"--cwd ""D:\repo\Engine""", instructions);
        }

        /// <summary>The shim as a client sees it: a process speaking MCP over stdio.</summary>
        sealed class Server : IDisposable
        {
            Process _process;
            int _id;

            public JsonElement Initialized { get; private set; }

            public static async Task<Server> StartAsync(string dataDir)
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
                };
                start.ArgumentList.Add(typeof(EvidenceTools).Assembly.Location);
                start.ArgumentList.Add("--cwd");
                start.ArgumentList.Add(@"D:\repo\Engine");
                start.Environment["VSDBGMCP_DATA_DIR"] = dataDir;

                var server = new Server { _process = Process.Start(start) };
                server.Initialized = await server.RequestAsync("initialize", new
                {
                    protocolVersion = "2025-11-25",
                    capabilities = new { },
                    clientInfo = new { name = "digest-test", version = "1" }
                });
                await server._process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                await server._process.StandardInput.FlushAsync();
                return server;
            }

            public Task<JsonElement> CallAsync(string tool, object arguments) =>
                RequestAsync("tools/call", new { name = tool, arguments });

            async Task<JsonElement> RequestAsync(string method, object arguments)
            {
                var requestId = ++_id;
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0", id = requestId, method, @params = arguments
                }));
                await _process.StandardInput.FlushAsync();

                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (true)
                {
                    var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(deadline - DateTime.UtcNow);
                    Assert.NotNull(line);
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != requestId) continue;
                    Assert.False(root.TryGetProperty("error", out _), line);
                    return root.GetProperty("result").Clone();
                }
            }

            public void Dispose()
            {
                try { _process.StandardInput.Close(); } catch { }
                try { if (!_process.WaitForExit(3000)) _process.Kill(true); } catch { }
                _process.Dispose();
            }
        }
    }
}
