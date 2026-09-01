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

            var text = await Failure.Text(new ExecutionTools(_sessions).Pause(null, CancellationToken.None));

            Assert.Contains("Nothing is running", text);
        }

        [Fact]
        public async Task Waiting_with_nothing_happening_reports_a_timeout_not_a_failure()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            var text = await new ExecutionTools(_sessions).Wait(1, instance: null, ct: CancellationToken.None);

            Assert.Contains("timeout", text);

            // Nothing was asked about the debuggee, so nothing may be said about it.
            Assert.DoesNotContain("Still running", text);
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
        public async Task A_tracepoint_reports_which_of_its_expressions_will_work()
        {
            var text = await new BreakpointTools(_sessions).BpSet(
                file: @"D:\repo\Engine\audio.cpp", line: 214,
                logMessage: "publish samples={n} dst={ResInfo}", collect: true,
                ct: CancellationToken.None);

            Assert.Contains("{n} = 1024", text);
            Assert.Contains("identifier \"ResInfo\" is undefined", text);
            Assert.Contains("trace_read", text);
        }

        [Fact]
        public async Task A_collected_tracepoint_reads_back_as_its_own_stream()
        {
            var tools = new BreakpointTools(_sessions);
            await tools.BpSet(file: @"D:\repo\Engine\audio.cpp", line: 214,
                logMessage: "publish samples={n}", collect: true, ct: CancellationToken.None);

            var start = DateTime.UtcNow;
            for (var i = 0; i < 3; i++) _host.RaiseTrace(7, "publish samples=1024", start.AddMilliseconds(i * 20));

            var text = await tools.TraceRead(7, 50, null, CancellationToken.None);

            Assert.Contains("#7  3 of 3 records", text);
            Assert.Contains("50.0/s", text);
            Assert.Contains("publish samples=1024", text);
        }

        /// <summary>
        /// The whole path, because this is the reply that read as an answer: a
        /// tracepoint that has collected nothing used to come back saying it had not
        /// been hit, and a function that was executing was reported as dead.
        /// </summary>
        [Fact]
        public async Task A_collecting_tracepoint_with_nothing_in_it_does_not_report_no_hits()
        {
            var tools = new BreakpointTools(_sessions);
            await tools.BpSet(file: @"D:\repo\Engine\audio.cpp", line: 214,
                logMessage: "publish samples={n}", collect: true, ct: CancellationToken.None);

            var text = await tools.TraceRead(7, 50, null, CancellationToken.None);

            Assert.DoesNotContain("has not been hit", text);
            Assert.Contains("never reached this buffer", text);
            Assert.Contains("What settles it", text);
        }

        [Fact]
        public async Task Reading_a_tracepoint_that_is_not_collecting_says_which_ones_are()
        {
            var tools = new BreakpointTools(_sessions);
            await tools.BpSet(file: @"D:\repo\Engine\audio.cpp", line: 214,
                logMessage: "publish samples={n}", collect: true, ct: CancellationToken.None);

            var text = await tools.TraceRead(3, 50, null, CancellationToken.None);

            Assert.Contains("#3 is not collecting", text);
            Assert.Contains("#7", text);
        }

        [Fact]
        public async Task Collecting_without_a_message_is_refused_rather_than_ignored()
        {
            var text = await new BreakpointTools(_sessions).BpSet(
                file: @"D:\repo\Engine\audio.cpp", line: 214, collect: true, ct: CancellationToken.None);

            Assert.Contains("Give a logMessage", text);
        }

        [Fact]
        public async Task Evaluation_refuses_to_call_functions_unless_asked()
        {
            var tools = new InspectionTools(_sessions);

            var refused = await Failure.Text(
                tools.Eval("v.size()", frame: 0, ct: CancellationToken.None));
            Assert.Contains("allowSideEffects", refused);

            var allowed = await tools.Eval("v.size()", allowSideEffects: true, frame: 0, ct: CancellationToken.None);
            Assert.Contains("42", allowed);
        }

        [Fact]
        public async Task Evaluating_across_threads_groups_equal_values()
        {
            var text = await new InspectionTools(_sessions)
                .Eval("m_state", allThreads: true, frame: 0, ct: CancellationToken.None);

            // Two threads share a value and one differs; the odd one out should stand out.
            Assert.Contains("10, 11", text);
            Assert.Contains("12", text);
        }

        [Fact]
        public async Task Reading_a_raw_array_by_index_returns_one_row_each()
        {
            var text = await new InspectionTools(_sessions)
                .Eval("RawParams->Pins", count: 28, member: "->Name", ct: CancellationToken.None);

            Assert.Contains("(RawParams->Pins)[0]->Name = \"Pin0\"", text);
            Assert.Contains("(RawParams->Pins)[27]->Name = \"Pin3\"", text);

            // The repeated block is the finding, and counting it is what makes it visible.
            Assert.Contains("28 values read, 12 of them distinct.", text);
        }

        [Fact]
        public async Task Two_readings_asked_for_at_once_are_refused()
        {
            var text = await Failure.Text(new InspectionTools(_sessions)
                .Eval("Pins", allThreads: true, count: 4, ct: CancellationToken.None));

            Assert.Contains("Ask for one of them", text);
        }

        [Fact]
        public async Task A_member_with_nothing_to_read_it_on_is_refused()
        {
            var text = await Failure.Text(new InspectionTools(_sessions)
                .Eval("Pins", member: "->Name", ct: CancellationToken.None));

            Assert.Contains("no count was given", text);
        }

        [Fact]
        public async Task Asking_for_more_indexes_than_the_cap_says_so()
        {
            var text = await new InspectionTools(_sessions)
                .Eval("Pins", count: 4000, ct: CancellationToken.None);

            Assert.Contains("4000 indexes were asked for and 256 read", text);
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
            var text = await Failure.Text(new LifecycleTools(_sessions).Status("Nope#1", CancellationToken.None));

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
        public async Task A_failure_on_the_far_side_comes_back_as_a_readable_failure()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);
            _host.FailNextCall = true;

            var text = await Failure.Text(new LifecycleTools(_sessions).Status(null, CancellationToken.None));

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

        [Fact]
        public async Task A_module_list_carries_build_times_and_what_a_filter_left_out()
        {
            var tools = new InspectionTools(_sessions);

            var all = await tools.Modules(null, null, CancellationToken.None);
            Assert.Contains("image 2026-08-21 12:11", all);
            Assert.Contains("mesh.cpp was edited after this binary was built", all);

            var filtered = await tools.Modules("vulkan", null, CancellationToken.None);
            Assert.Contains("1 of 3 loaded modules match 'vulkan'", filtered);
        }

        [Fact]
        public async Task A_module_list_says_which_binary_each_module_actually_is()
        {
            var text = await new InspectionTools(_sessions).Modules("engine", null, CancellationToken.None);

            Assert.Contains(@"D:\repo\Engine\out\engine.dll", text);
            Assert.Contains("1.3 MB (0x145000)", text);
            Assert.Contains("0x7ff6b2340000", text);
            Assert.Contains(@"symbols D:\repo\Engine\out\engine.pdb", text);

            // The image is stamped an hour before the file at that path here. Both times
            // are shown, and which is which has to be readable from the text alone.
            Assert.Contains("image 2026-08-21 12:11", text);
            Assert.Contains("on this machine was written 2026-08-21 13:05", text);
        }

        [Fact]
        public async Task Symbols_report_where_the_engine_looked_and_what_it_found_there()
        {
            var text = await new InspectionTools(_sessions).Symbols("ucrtbase", false, null, CancellationToken.None);

            Assert.Contains("NO SYMBOLS", text);
            Assert.Contains("Where it looked:", text);
            Assert.Contains("PDB does not match image", text);
        }

        [Fact]
        public async Task Symbols_for_a_name_that_is_not_loaded_says_how_many_are()
        {
            var text = await new InspectionTools(_sessions).Symbols("nosuch", true, null, CancellationToken.None);

            Assert.Contains("No loaded module matches 'nosuch'", text);
            Assert.Contains("3 modules are loaded", text);
        }
    }
}
