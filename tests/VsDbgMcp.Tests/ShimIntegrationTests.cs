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
    /// The shim from end to end against a stand-in for the extension: discovery file on
    /// disk, real named pipe, real JSON-RPC, real tool rendering. What this cannot cover
    /// is Visual Studio's own behaviour, which needs the extension loaded.
    /// </summary>
    public class ShimIntegrationTests : IDisposable
    {
        readonly string _dir;
        readonly string _pipe;
        readonly FakeHost _host;
        readonly SessionManager _sessions;

        public ShimIntegrationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            _pipe = "vsdbgmcp-test-" + Guid.NewGuid().ToString("N");
            _host = new FakeHost(_pipe) { InstanceId = "App#" + Process.GetCurrentProcess().Id };
            _host.Start();

            InstanceFileWrite(new InstanceRecord
            {
                Pid = Process.GetCurrentProcess().Id,
                Pipe = _pipe,
                Token = "secret-token",
                VsVersion = "17.14.0",
                Contract = Names.ContractVersion,
                DebugMode = DebugModes.Break,
                Capabilities = new[] { Capabilities.Native },
                Workspace = new WorkspaceInfo
                {
                    Kind = WorkspaceKind.Slnx,
                    Root = @"D:\repo\Engine",
                    File = @"D:\repo\Engine\App.slnx",
                    Name = "App"
                }
            });

            _sessions = new SessionManager(@"D:\repo\Engine\src", new InstanceStore(_dir));
        }

        void InstanceFileWrite(InstanceRecord record)
        {
            File.WriteAllText(
                Path.Combine(_dir, Names.InstanceFilePrefix + record.Pid + Names.InstanceFileSuffix),
                InstanceFile.Serialize(record));
        }

        public void Dispose()
        {
            _sessions.Dispose();
            _host.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public async Task The_working_directory_alone_reaches_the_right_instance()
        {
            var status = await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            Assert.Contains("App#", status);
            Assert.Contains(DebugModes.Break, status);
            Assert.Contains("Mesh::Upload", status);
            Assert.Contains("Debug|x64", status);
        }

        [Fact]
        public async Task The_token_from_the_discovery_file_is_presented_at_handshake()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            Assert.Equal("secret-token", _host.LastToken);
            Assert.True(_host.HandshakeCount >= 1);
        }

        [Fact]
        public async Task Pinned_watches_come_back_with_the_status()
        {
            var status = await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            Assert.Contains("m_state = Uploading", status);
        }

        [Fact]
        public async Task A_stop_pushed_by_the_host_satisfies_wait()
        {
            var tools = new ExecutionTools(_sessions);

            // Connect first, so the event has somewhere to arrive.
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            var waiting = tools.Wait(10, instance: null, ct: CancellationToken.None);

            await Task.Delay(100);
            _host.RaiseStop(new StopEvent
            {
                Reason = StopReason.Exception,
                ThreadId = 15224,
                Exception = new ExceptionInfo
                {
                    Code = "0xC0000005",
                    Name = "Access violation",
                    Address = "0x7ff600001234",
                    FirstChance = false
                },
                Frame = new Frame { Function = "Mesh::Upload", File = "mesh.cpp", Line = 218, Module = "engine.dll" }
            });

            var text = await waiting;

            Assert.Contains("stopped: exception", text);
            Assert.Contains("0xC0000005", text);
            Assert.Contains("Access violation", text);
            Assert.Contains("Mesh::Upload", text);
            Assert.Contains("unhandled", text);
        }

        [Fact]
        public async Task Pause_returns_the_stop_rather_than_the_request()
        {
            var tools = new ExecutionTools(_sessions);
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            var pausing = tools.Pause(null, CancellationToken.None);

            await Task.Delay(100);
            _host.RaiseStop(new StopEvent
            {
                Reason = StopReason.Pause,
                ThreadId = 15224,
                Frame = new Frame { Function = "Engine::Tick", File = "engine.cpp", Line = 90 }
            });

            var text = await pausing;

            Assert.Contains("stopped: pause", text);
            Assert.Contains("Engine::Tick", text);
            Assert.DoesNotContain("Break requested", text);
        }

        [Fact]
        public async Task A_pause_the_debugger_refuses_comes_back_without_waiting()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);
            _host.PauseResult = OpResult.Bad("Nothing is running. Current mode: design.");

            var text = await new ExecutionTools(_sessions).Pause(null, CancellationToken.None);

            Assert.Contains("Nothing is running", text);
        }

        [Fact]
        public async Task Waiting_with_nothing_happening_reports_a_timeout_not_a_failure()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            var text = await new ExecutionTools(_sessions).Wait(1, instance: null, ct: CancellationToken.None);

            Assert.Contains("timeout", text);
            Assert.Contains("Still running", text);
        }

        [Fact]
        public async Task A_module_load_pushed_by_the_host_satisfies_a_module_wait()
        {
            var tools = new ExecutionTools(_sessions);

            // Connect first, so the event has somewhere to arrive.
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            var waiting = tools.Wait(10, "module:SceneTree", instance: null, ct: CancellationToken.None);

            await Task.Delay(100);
            _host.RaiseModuleLoad(new ModuleLoadEvent
            {
                Name = "UnrealEditor-NOSSceneTreeManager.dll",
                Path = @"D:\repo\Engine\Binaries\Win64\UnrealEditor-NOSSceneTreeManager.dll",
                SymbolsLoaded = false,
                SymbolStatus = "Cannot find or open the PDB file."
            });

            var text = await waiting;

            Assert.Contains("module loaded: UnrealEditor-NOSSceneTreeManager.dll", text);
            Assert.Contains("NO SYMBOLS", text);
            Assert.Contains("Cannot find or open the PDB file.", text);
        }

        [Fact]
        public async Task A_module_load_leaves_a_wait_for_a_stop_waiting()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            var waiting = new ExecutionTools(_sessions).Wait(1, instance: null, ct: CancellationToken.None);
            _host.RaiseModuleLoad(new ModuleLoadEvent { Name = "UnrealEditor-NOSSceneTreeManager.dll" });

            Assert.Contains("timeout", await waiting);
        }

        [Fact]
        public async Task An_unbound_breakpoint_says_so_and_says_why()
        {
            var text = await new BreakpointTools(_sessions).BpList(null, CancellationToken.None);

            Assert.Contains("UNBOUND", text);
            Assert.Contains("module not loaded", text);
            Assert.Contains("bound", text);
        }

        [Fact]
        public async Task A_data_breakpoint_is_routed_as_one()
        {
            var text = await new BreakpointTools(_sessions).BpSet(
                dataExpression: "&mesh->vertices[0]", dataSize: 8, instance: null, ct: CancellationToken.None);

            Assert.Contains("#7", text);
            Assert.Contains("data &mesh->vertices[0]", text);
            Assert.Contains("8 bytes", text);
        }

        [Fact]
        public async Task Evaluation_refuses_to_call_functions_unless_asked()
        {
            var tools = new InspectionTools(_sessions);

            var refused = await tools.Eval("v.size()", null, false, false, false, 0, null, CancellationToken.None);
            Assert.Contains("allowSideEffects", refused);

            var allowed = await tools.Eval("v.size()", null, false, true, false, 0, null, CancellationToken.None);
            Assert.Contains("42", allowed);
        }

        [Fact]
        public async Task Evaluating_across_threads_groups_equal_values()
        {
            var text = await new InspectionTools(_sessions)
                .Eval("m_state", null, false, false, true, 0, null, CancellationToken.None);

            // Two threads share a value and one differs; the odd one out should stand out.
            Assert.Contains("10, 11", text);
            Assert.Contains("12", text);
        }

        [Fact]
        public async Task Build_answers_with_errors_rather_than_a_log()
        {
            var text = await new BuildTools(_sessions).Build("build", null, null, null, null, CancellationToken.None);

            Assert.Contains("Build FAILED", text);
            Assert.Contains("C2065", text);
            Assert.Contains("mesh.cpp(12)", text);
            Assert.Contains("and 3 more", text);
        }

        [Fact]
        public async Task Asking_for_an_instance_that_is_not_there_lists_the_ones_that_are()
        {
            var text = await new LifecycleTools(_sessions).Status("Nope#1", CancellationToken.None);

            Assert.Contains("App#", text);
            Assert.Contains("instance=", text);
        }

        [Fact]
        public async Task Instances_reports_what_was_discovered()
        {
            var text = await new SessionTools(_sessions).Instances(CancellationToken.None);

            Assert.Contains("App#", text);
            Assert.Contains("App.slnx", text);
            Assert.Contains(@"D:\repo\Engine\src", text);
        }

        [Fact]
        public async Task A_failure_on_the_far_side_comes_back_as_readable_text()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);
            _host.FailNextCall = true;

            var text = await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            Assert.Contains("the debugger said no", text);
        }

        [Fact]
        public async Task Resuming_clears_stops_that_have_already_been_reported()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            _host.RaiseStop(new StopEvent { Reason = StopReason.Breakpoint, ThreadId = 1 });
            await Task.Delay(50);

            await new ExecutionTools(_sessions).Go(null, CancellationToken.None);

            var text = await new ExecutionTools(_sessions).Wait(1, instance: null, ct: CancellationToken.None);
            Assert.Contains("timeout", text);
        }
    }
}
