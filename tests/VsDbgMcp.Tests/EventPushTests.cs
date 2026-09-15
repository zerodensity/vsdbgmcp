using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// Both waits inside the stop wait consume what they find, so whichever loses the
        /// race still has to be collected. The stop is the answer; the ending that landed
        /// beside it belongs to the next reply rather than to nobody.
        /// </summary>
        [Fact]
        public async Task A_stop_and_a_session_ending_together_answer_with_the_stop()
        {
            var link = await Connected();
            _host.RaiseModeChange(DebugModes.Run);
            await Task.Delay(100);
            _sessions.Log.MarkSeen();

            // Both have landed before the wait starts, so both of its races are answered
            // out of what is already buffered and neither can be the one that got there
            // first by chance.
            _host.RaiseStop(new StopEvent { Reason = StopReason.Breakpoint, BreakpointId = 3 });
            _host.RaiseModeChange(DebugModes.Design);
            await Eventually(() => _sessions.Log.Recent(link.Id, 5),
                e => e.Any(x => x.Kind == EventKind.Stopped) && e.Any(x => x.Kind == EventKind.DebuggingEnded));

            var reply = await new ExecutionTools(_sessions).Wait(5, null, null, CancellationToken.None);

            Assert.Contains("stopped: breakpoint", reply);
            Assert.DoesNotContain("Debugging ended", reply);

            // The ending was consumed by the race it lost. This reply did not mention it,
            // so it is still there for the next one to carry.
            Assert.Contains(_sessions.Log.Recent(link.Id, 5),
                e => e.Kind == EventKind.DebuggingEnded && !e.Seen);
        }

        /// <summary>
        /// Giving up must not eat what the race already handed over. The log marks an
        /// entry shown as it hands it to a waiter, so a wait cancelled after that point
        /// holds the only copy: reported by nobody, it would never reach a digest,
        /// for='any' or status again.
        ///
        /// The cancel is fired from a log subscriber, which the log calls as it adds the
        /// entry, immediately after handing it over — so it lands in or around the window
        /// rather than anywhere. Which side of the window it lands on is not something
        /// this can pin down, so the check is the invariant that holds on every side: a
        /// reply carried the ending, or the ending is still there to be carried. Consumed
        /// and reported by nobody is the failure, and it is what this used to do.
        /// </summary>
        [Fact]
        public async Task A_wait_the_caller_cancelled_hands_back_the_ending_it_was_given()
        {
            var link = await Connected();
            CancellationTokenSource cancelling = null;
            _sessions.Log.Subscribe(e => { if (e.Kind == EventKind.DebuggingEnded) cancelling?.Cancel(); });

            for (var attempt = 0; attempt < 12; attempt++)
            {
                _host.RaiseModeChange(DebugModes.Run);
                await Task.Delay(40);
                _sessions.Log.MarkSeen();

                cancelling = new CancellationTokenSource();
                var waiting = new ExecutionTools(_sessions).Wait(20, null, null, cancelling.Token);
                await Task.Delay(40);
                _host.RaiseModeChange(DebugModes.Design);

                string reply = null;
                try { reply = await waiting; } catch (OperationCanceledException) { }

                var entries = await Eventually(() => _sessions.Log.Recent(link.Id, 5),
                    e => e.Any(x => x.Kind == EventKind.DebuggingEnded));
                var ending = entries.Last(e => e.Kind == EventKind.DebuggingEnded);

                Assert.True(reply != null || !ending.Seen,
                    "attempt " + attempt + ": the ending was taken by a call that reported nothing");

                _sessions.Log.MarkSeen();
            }
        }

        [Fact]
        public async Task A_wait_the_caller_gave_up_on_is_a_cancellation_and_not_a_fault()
        {
            await Connected();
            using var cancelling = new CancellationTokenSource();

            var waiting = new ExecutionTools(_sessions).Wait(20, null, null, cancelling.Token);
            await Task.Delay(100);
            cancelling.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        [Fact]
        public async Task A_window_that_refuses_the_handshake_is_not_a_window_that_closed()
        {
            _host.FailHandshake = true;

            await Assert.ThrowsAsync<RoutingException>(() => _sessions.ResolveAsync(null, CancellationToken.None));
            await Task.Delay(200);

            Assert.DoesNotContain(_sessions.Log.Recent(null, 20), e => e.Kind == EventKind.InstanceGone);
        }

        [Fact]
        public async Task A_stop_that_threw_does_not_swallow_the_session_ending()
        {
            var link = await Connected();
            _host.RaiseModeChange(DebugModes.Run);
            await Task.Delay(100);
            _sessions.Log.MarkSeen();

            _host.FailNextCall = true;
            await Failure.Text(new LifecycleTools(_sessions).Stop(null, null, CancellationToken.None));

            _host.RaiseModeChange(DebugModes.Design);

            var entries = await Eventually(() => _sessions.Log.Recent(link.Id, 5),
                e => e.Any(x => x.Kind == EventKind.DebuggingEnded));
            Assert.Contains(entries, e => e.Kind == EventKind.DebuggingEnded);
        }

        /// <summary>
        /// Terminating one process of a multi-process session leaves the session running,
        /// so it must not claim the session's ending - which would also hide every other
        /// process exiting for the next fifteen seconds, from status as well as the digest.
        /// </summary>
        [Fact]
        public async Task Stopping_one_process_leaves_another_one_exiting_reported()
        {
            var link = await Connected();
            _host.RaiseModeChange(DebugModes.Run);
            await Task.Delay(100);
            _sessions.Log.MarkSeen();

            await new LifecycleTools(_sessions).Stop(1234, null, CancellationToken.None);

            _host.RaiseStop(new StopEvent { Reason = StopReason.Exited, ExitCode = 0, ProcessName = "worker.exe" });

            var entries = await Eventually(() => _sessions.Log.Recent(link.Id, 5),
                e => e.Any(x => x.Kind == EventKind.Exited));
            Assert.Contains(entries, e => e.Kind == EventKind.Exited);
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
