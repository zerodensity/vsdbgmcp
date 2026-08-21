using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Shim.Discovery;
using VsDbgMcp.Shim.Session;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// What the panel inside Visual Studio is told about each call.
    ///
    /// The rendered reply only exists on the shim side, so the extension can only show
    /// what the agent actually saw if the shim sends it. These cover that it does, that
    /// it names the tool the way the agent typed it, and that a failure to report never
    /// reaches the caller.
    /// </summary>
    public class CallReportTests : IDisposable
    {
        readonly string _dir;
        readonly FakeHost _host;
        readonly SessionManager _sessions;

        public CallReportTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-report-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var pipe = "vsdbgmcp-report-" + Guid.NewGuid().ToString("N");
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

        /// <summary>The report is sent without being awaited, so give it a moment to land.</summary>
        static async Task Settle() => await Task.Delay(250);

        [Fact]
        public async Task A_call_is_reported_with_what_the_agent_was_given()
        {
            var text = await new LifecycleTools(_sessions).Status(null, CancellationToken.None);
            await Settle();

            var report = _host.ReportFor("status");
            Assert.NotNull(report);
            Assert.Equal(text, report.Result);
            Assert.False(report.Failed);
        }

        [Fact]
        public async Task The_tool_is_named_the_way_an_agent_types_it()
        {
            await new BreakpointTools(_sessions).BpList(null, CancellationToken.None);
            await Settle();

            // Not "BpList": the panel is read by a person deciding whether to allow this.
            Assert.NotNull(_host.ReportFor("bp_list"));
        }

        [Fact]
        public async Task The_argument_worth_reading_comes_with_it()
        {
            await new InspectionTools(_sessions)
                .Eval("mesh.refCount", null, false, false, false, null, 0, null, CancellationToken.None);
            await Settle();

            var report = _host.ReportFor("eval");
            Assert.NotNull(report);
            Assert.Equal("mesh.refCount", report.Arguments);
        }

        [Fact]
        public async Task A_failed_call_is_reported_as_failed()
        {
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);
            await Settle();

            _host.FailNextCall = true;
            await new LifecycleTools(_sessions).Status(null, CancellationToken.None);
            await Settle();

            var report = _host.ReportFor("status");
            Assert.NotNull(report);
            Assert.True(report.Failed);
            Assert.Contains("the debugger said no", report.Result);
        }

        [Fact]
        public async Task A_routing_failure_is_reported_rather_than_lost()
        {
            var text = await new LifecycleTools(_sessions).Status("Nope#1", CancellationToken.None);

            // Routing never resolved an instance, so there is nobody to report to; the
            // caller still gets the message that tells it how to recover.
            Assert.Contains("instance=", text);
        }

        [Fact]
        public async Task Reporting_does_not_change_what_the_caller_gets()
        {
            var first = await new BreakpointTools(_sessions).BpList(null, CancellationToken.None);
            await Settle();
            var second = await new BreakpointTools(_sessions).BpList(null, CancellationToken.None);

            Assert.Equal(first, second);
            Assert.Contains("UNBOUND", first);
        }
    }
}
