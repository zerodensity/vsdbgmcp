using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Discovery;
using VsDbgMcp.Shim.Session;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// What Visual Studio pushes has to reach the journal over the real pipe, because
    /// the wiring is what is being tested rather than any one rule inside the log.
    /// </summary>
    public class EventPushTests : IDisposable
    {
        readonly string _dir;
        readonly FakeHost _host;
        readonly SessionManager _sessions;

        public EventPushTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vsdbgmcp-push-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            var pipe = "vsdbgmcp-push-" + Guid.NewGuid().ToString("N");
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

            _sessions = new SessionManager(@"D:\repo\Engine", new InstanceStore(_dir));
        }

        public void Dispose()
        {
            _sessions.Dispose();
            _host.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        async Task<HostLink> Connected() => await _sessions.ResolveAsync(null, CancellationToken.None);

        static async Task<T> Eventually<T>(Func<T> read, Func<T, bool> until)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                var value = read();
                if (until(value) || DateTime.UtcNow > deadline) return value;
                await Task.Delay(25);
            }
        }

        [Fact]
        public async Task A_build_that_finishes_later_reaches_the_journal()
        {
            var link = await Connected();
            _host.RaiseOperationChanged(new OperationInfo
            {
                OperationId = "ab12", Kind = "build", State = "succeeded", Terminal = true
            });

            var entries = await Eventually(() => _sessions.Log.Recent(link.Id, 5), e => e.Count > 0);
            var entry = Assert.Single(entries);
            Assert.Equal(EventKind.OperationDone, entry.Kind);
            Assert.Equal("ab12", entry.Operation.OperationId);
            Assert.Equal(link.Id, entry.InstanceId);
        }

        [Fact]
        public async Task Opening_another_solution_reaches_the_journal()
        {
            var link = await Connected();
            _host.RaiseWorkspaceChanged();

            var entries = await Eventually(() => _sessions.Log.Recent(link.Id, 5), e => e.Count > 0);
            Assert.Equal(EventKind.SolutionChanged, Assert.Single(entries).Kind);
        }

        [Fact]
        public async Task A_stop_nobody_was_waiting_for_reaches_the_journal()
        {
            var link = await Connected();
            _host.RaiseStop(new StopEvent { Reason = StopReason.Breakpoint, BreakpointId = 3 });

            var entries = await Eventually(() => _sessions.Log.Recent(link.Id, 5), e => e.Count > 0);
            var entry = Assert.Single(entries);
            Assert.Equal(EventKind.Stopped, entry.Kind);
            Assert.Equal(3, entry.Stop.BreakpointId);
        }

        [Fact]
        public async Task A_window_that_closes_is_reported_once()
        {
            var link = await Connected();
            _host.Drop();

            var entries = await Eventually(() => _sessions.Log.Recent(link.Id, 5),
                e => e.Count > 0 && e[e.Count - 1].Kind == EventKind.InstanceGone);
            Assert.Equal(EventKind.InstanceGone, entries[entries.Count - 1].Kind);

            File.Delete(Path.Combine(_dir, Names.InstanceFilePrefix + Process.GetCurrentProcess().Id + Names.InstanceFileSuffix));
            try { await _sessions.RefreshAsync(true, CancellationToken.None); } catch { }

            var after = _sessions.Log.Recent(link.Id, 10);
            Assert.Single(after, e => e.Kind == EventKind.InstanceGone);
        }

        [Fact]
        public async Task Stopping_the_debuggee_is_not_reported_back_to_whoever_stopped_it()
        {
            var link = await Connected();
            _host.RaiseModeChange(DebugModes.Run);
            await Task.Delay(100);
            _sessions.Log.MarkSeen();

            await new LifecycleTools(_sessions).Stop(null, null, CancellationToken.None);

            _host.RaiseStop(new StopEvent { Reason = StopReason.Exited, ExitCode = 0, ProcessName = "engine.exe" });
            _host.RaiseModeChange(DebugModes.Design);
            await Task.Delay(300);

            Assert.Empty(_sessions.Log.TakeUnseen());
        }

        [Fact]
        public async Task Someone_else_ending_the_session_is_reported()
        {
            var link = await Connected();
            _host.RaiseModeChange(DebugModes.Run);
            await Task.Delay(100);
            _sessions.Log.MarkSeen();

            _host.RaiseModeChange(DebugModes.Design);
            await Task.Delay(300);

            Assert.Equal(EventKind.DebuggingEnded, Assert.Single(_sessions.Log.TakeUnseen()).Kind);
        }

        [Fact]
        public async Task A_build_whose_reply_carried_its_errors_is_not_reported_again()
        {
            await Connected();
            await new BuildTools(_sessions).Build("build", null, null, null, null, CancellationToken.None);

            _host.RaiseOperationChanged(new OperationInfo
            {
                OperationId = "build-1", Kind = "build", State = "failed", Terminal = true,
                Build = new BuildResult { TotalErrors = 3 }
            });
            await Task.Delay(300);

            Assert.Empty(_sessions.Log.TakeUnseen());
        }

        [Fact]
        public async Task A_stop_a_wait_returned_is_not_reported_again()
        {
            await Connected();
            var waiting = new ExecutionTools(_sessions).Wait(5, null, null, CancellationToken.None);
            await Task.Delay(100);

            _host.RaiseStop(new StopEvent { Reason = StopReason.Breakpoint, BreakpointId = 3 });
            await waiting;
            await Task.Delay(200);

            Assert.Empty(_sessions.Log.TakeUnseen());
        }

        [Fact]
        public async Task Waiting_for_a_line_answers_from_what_already_arrived()
        {
            await Connected();
            _host.RaiseOutput(new OutputEvent { Pane = "Debug", Text = "Server listening on 8080\r\n" });
            await Task.Delay(200);

            var reply = await new ExecutionTools(_sessions).Wait(2, "output:listening", null, CancellationToken.None);

            Assert.Contains("Server listening on 8080", reply);
        }

        [Fact]
        public async Task Waiting_for_a_line_that_never_comes_says_only_that()
        {
            await Connected();

            var reply = await new ExecutionTools(_sessions).Wait(1, "output:never-printed", null, CancellationToken.None);

            Assert.StartsWith("timeout:", reply);
            Assert.Contains("never-printed", reply);
        }

        [Fact]
        public async Task A_pattern_that_is_not_a_pattern_says_so()
        {
            await Connected();

            var reply = await new ExecutionTools(_sessions).Wait(1, "output:[unclosed", null, CancellationToken.None);

            Assert.Contains("not a regular expression", reply);
        }

        [Fact]
        public async Task Waiting_for_anything_returns_what_happened_and_marks_it_shown()
        {
            var link = await Connected();
            _host.RaiseOperationChanged(new OperationInfo
            {
                OperationId = "ab12", Kind = "build", State = "succeeded", Terminal = true,
                Build = new BuildResult { Succeeded = true }
            });
            await Task.Delay(200);

            var reply = await new ExecutionTools(_sessions).Wait(2, "any", null, CancellationToken.None);

            Assert.Contains("build succeeded (operation ab12)", reply);
            Assert.Empty(_sessions.Log.TakeUnseen());
        }

        [Fact]
        public async Task A_stop_that_can_no_longer_come_ends_the_wait()
        {
            await Connected();
            _host.RaiseModeChange(DebugModes.Run);
            await Task.Delay(100);
            _sessions.Log.MarkSeen();

            var waiting = new ExecutionTools(_sessions).Wait(20, null, null, CancellationToken.None);
            await Task.Delay(100);
            _host.RaiseModeChange(DebugModes.Design);

            var reply = await waiting;
            Assert.Contains("Debugging ended", reply);
            Assert.DoesNotContain("timeout", reply);
        }

        [Fact]
        public async Task A_window_that_closes_ends_the_wait()
        {
            await Connected();
            _host.RaiseModeChange(DebugModes.Run);
            await Task.Delay(100);

            var waiting = new ExecutionTools(_sessions).Wait(20, null, null, CancellationToken.None);
            await Task.Delay(100);
            _host.Drop();

            Assert.Contains("closed", await waiting);
        }

        [Fact]
        public async Task Status_lists_what_happened_and_does_not_leave_it_to_be_reported_twice()
        {
            var link = await Connected();
            _host.RaiseOperationChanged(new OperationInfo
            {
                OperationId = "ab12", Kind = "build", State = "succeeded", Terminal = true,
                Build = new BuildResult { Succeeded = true }
            });
            await Task.Delay(200);

            var status = await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            Assert.Contains("Recent:", status);
            Assert.Contains("build succeeded (operation ab12)", status);
            Assert.Empty(_sessions.Log.TakeUnseen());
        }

        [Fact]
        public async Task Status_with_nothing_behind_it_adds_nothing()
        {
            await Connected();

            var status = await new LifecycleTools(_sessions).Status(null, CancellationToken.None);

            Assert.DoesNotContain("Recent:", status);
        }
    }
}
