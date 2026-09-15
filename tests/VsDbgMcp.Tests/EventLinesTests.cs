using System;
using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using VsDbgMcp.Shim.Session;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// One line per thing that happened, in the words the model acts on. The digest,
    /// status and --follow all read these, so a change here changes all three.
    /// </summary>
    public class EventLinesTests
    {
        static readonly DateTime Now = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

        static LogEntry Entry(EventKind kind, StopEvent stop = null, OperationInfo operation = null, int minutesAgo = 0) =>
            new LogEntry
            {
                Kind = kind,
                InstanceId = "App#42696",
                At = Now.AddMinutes(-minutesAgo),
                Stop = stop,
                Operation = operation
            };

        static string Line(LogEntry entry, bool withInstance = false) => EventLines.Line(entry, withInstance, Now);

        [Fact]
        public void A_breakpoint_says_which_one_and_where()
        {
            var line = Line(Entry(EventKind.Stopped, new StopEvent
            {
                Reason = StopReason.Breakpoint,
                BreakpointId = 3,
                ThreadId = 15224,
                Frame = new Frame { Function = "Mesh::Upload", File = @"D:\repo\Engine\main.cpp", Line = 42 }
            }));

            Assert.Equal("stopped at breakpoint 3, main.cpp:42 in Mesh::Upload (thread 15224)", line);
        }

        [Fact]
        public void An_exception_says_what_it_was()
        {
            var line = Line(Entry(EventKind.Stopped, new StopEvent
            {
                Reason = StopReason.Exception,
                ThreadId = 15224,
                Exception = new ExceptionInfo { Name = "Access violation", Code = "0xC0000005" },
                Frame = new Frame { Function = "Mesh::Upload", File = @"D:\repo\Engine\mesh.cpp", Line = 218 }
            }));

            Assert.Equal("stopped on Access violation (0xC0000005) at mesh.cpp:218 in Mesh::Upload (thread 15224)", line);
        }

        [Fact]
        public void A_stop_with_no_frame_still_says_what_happened()
        {
            Assert.Equal("stopped after step", Line(Entry(EventKind.Stopped, new StopEvent { Reason = StopReason.Step })));
        }

        [Fact]
        public void A_pause_reads_as_a_pause()
        {
            Assert.Equal("paused", Line(Entry(EventKind.Stopped, new StopEvent { Reason = StopReason.Pause })));
        }

        [Fact]
        public void An_entry_point_reads_as_one()
        {
            Assert.Equal("stopped at entry point", Line(Entry(EventKind.Stopped, new StopEvent { Reason = StopReason.Entry })));
        }

        [Fact]
        public void An_exit_carries_its_code_and_its_process()
        {
            var line = Line(Entry(EventKind.Exited, new StopEvent
            {
                Reason = StopReason.Exited, ExitCode = 3, ProcessName = "myapp.exe"
            }));

            Assert.Equal("exited with code 3 (myapp.exe)", line);
        }

        [Fact]
        public void Sessions_beginning_and_ending_are_worded_neutrally()
        {
            Assert.Equal("debugging started", Line(Entry(EventKind.DebuggingStarted)));
            Assert.Equal("debugging ended", Line(Entry(EventKind.DebuggingEnded)));
        }

        [Fact]
        public void A_build_reports_its_errors()
        {
            var line = Line(Entry(EventKind.OperationDone, operation: new OperationInfo
            {
                OperationId = "ab12", Kind = "build", State = "succeeded", Terminal = true,
                Build = new BuildResult { TotalErrors = 2, TotalWarnings = 1 }
            }));

            Assert.Equal("build done: 2 errors, 1 warning (operation ab12)", line);
        }

        [Fact]
        public void A_clean_build_says_so()
        {
            var line = Line(Entry(EventKind.OperationDone, operation: new OperationInfo
            {
                OperationId = "ab12", Kind = "build", State = "succeeded", Terminal = true,
                Build = new BuildResult { Succeeded = true }
            }));

            Assert.Equal("build succeeded (operation ab12)", line);
        }

        [Fact]
        public void A_cancelled_build_is_not_a_failed_one()
        {
            var line = Line(Entry(EventKind.OperationDone, operation: new OperationInfo
            {
                OperationId = "ab12", Kind = "build", State = "cancelled", Terminal = true
            }));

            Assert.Equal("build cancelled (operation ab12)", line);
        }

        [Fact]
        public void A_launch_names_what_it_started()
        {
            var line = Line(Entry(EventKind.OperationDone, operation: new OperationInfo
            {
                OperationId = "ab12", Kind = "launch", State = "running", Terminal = true, Executable = "myapp.exe"
            }));

            Assert.Equal("launch succeeded: myapp.exe (operation ab12)", line);
        }

        [Fact]
        public void A_launch_that_failed_says_why()
        {
            var line = Line(Entry(EventKind.OperationDone, operation: new OperationInfo
            {
                OperationId = "ab12", Kind = "launch", State = "failed", Terminal = true, Message = "project not built"
            }));

            Assert.Equal("launch failed: project not built (operation ab12)", line);
        }

        [Fact]
        public void A_breakpoint_that_bound_says_where()
        {
            var line = Line(Entry(EventKind.OperationDone, operation: new OperationInfo
            {
                OperationId = "ab12", Kind = "breakpoint", State = "bound", Terminal = true,
                Breakpoint = new BreakpointInfo { Id = 3, Bound = true, File = @"D:\repo\Engine\main.cpp", Line = 42 }
            }));

            Assert.Equal("breakpoint 3 bound at main.cpp:42 (operation ab12)", line);
        }

        [Fact]
        public void A_breakpoint_that_did_not_bind_says_why()
        {
            var line = Line(Entry(EventKind.OperationDone, operation: new OperationInfo
            {
                OperationId = "ab12", Kind = "breakpoint", State = "pending-symbols", Terminal = true,
                Breakpoint = new BreakpointInfo { Id = 3, Bound = false, BindState = "module not loaded" }
            }));

            Assert.Equal("breakpoint 3 could not bind: module not loaded (operation ab12)", line);
        }

        [Fact]
        public void An_operation_of_no_known_kind_still_renders()
        {
            var line = Line(Entry(EventKind.OperationDone, operation: new OperationInfo
            {
                OperationId = "ab12", Kind = "profile-stop", State = "collected", Terminal = true
            }));

            Assert.Equal("profile-stop done: collected (operation ab12)", line);
        }

        [Fact]
        public void A_window_closing_and_a_solution_changing_name_the_window_themselves()
        {
            Assert.Equal("Visual Studio App#42696 closed", Line(Entry(EventKind.InstanceGone), withInstance: true));
            Assert.Equal("solution changed in Visual Studio App#42696", Line(Entry(EventKind.SolutionChanged), withInstance: true));
        }

        [Fact]
        public void With_several_windows_a_line_says_which_one()
        {
            var line = Line(Entry(EventKind.DebuggingEnded), withInstance: true);
            Assert.Equal("[App#42696] debugging ended", line);
        }

        [Fact]
        public void Only_something_older_than_a_minute_carries_its_age()
        {
            Assert.Equal("debugging ended", Line(Entry(EventKind.DebuggingEnded)));
            Assert.Equal("4 min ago: debugging ended", Line(Entry(EventKind.DebuggingEnded, minutesAgo: 4)));
            Assert.Equal("2 h ago: debugging ended", Line(Entry(EventKind.DebuggingEnded, minutesAgo: 125)));
        }

        [Fact]
        public void A_digest_that_changes_the_state_says_so_and_ends_with_a_blank_line()
        {
            var text = EventLines.Digest(new List<LogEntry>
            {
                Entry(EventKind.Stopped, new StopEvent { Reason = StopReason.Step })
            }, false, Now);

            Assert.Equal("Since your last call, state changed:\n- stopped after step\n", text);
        }

        [Fact]
        public void A_digest_of_things_that_change_nothing_is_worded_plainly()
        {
            var text = EventLines.Digest(new List<LogEntry>
            {
                Entry(EventKind.OperationDone, operation: new OperationInfo
                {
                    OperationId = "ab12", Kind = "build", State = "succeeded", Terminal = true,
                    Build = new BuildResult { Succeeded = true }
                })
            }, false, Now);

            Assert.StartsWith("Since your last call:\n- build succeeded", text);
        }

        [Fact]
        public void A_long_digest_stops_at_six_lines_and_says_where_to_look()
        {
            var entries = new List<LogEntry>();
            for (var i = 0; i < 9; i++) entries.Add(Entry(EventKind.DebuggingEnded));

            var text = EventLines.Digest(entries, false, Now);

            var lines = text.TrimEnd('\n').Split('\n');
            Assert.Equal("Since your last call, state changed:", lines[0]);
            Assert.Equal(8, lines.Length);
            Assert.Equal("- and 3 more; call status for the current state", lines[7]);
        }
    }
}
