using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Session;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// What happened while the model was not looking. The rule the whole design rests
    /// on is that an event is shown exactly once, to exactly one reply.
    /// </summary>
    public class EventLogTests
    {
        DateTime _now = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

        EventLog NewLog() => new EventLog(() => _now);

        static StopEvent Stop(string instance, string reason = StopReason.Breakpoint) =>
            new StopEvent { InstanceId = instance, Reason = reason };

        [Fact]
        public void An_event_is_taken_once()
        {
            var log = NewLog();
            log.Stopped(Stop("App#1"));

            Assert.Single(log.TakeUnseen());
            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void An_exit_is_its_own_kind()
        {
            var log = NewLog();
            log.Stopped(Stop("App#1", StopReason.Exited));

            Assert.Equal(EventKind.Exited, Assert.Single(log.TakeUnseen()).Kind);
        }

        [Fact]
        public void Recent_shows_one_instance_and_consumes_nothing()
        {
            var log = NewLog();
            log.Stopped(Stop("App#1"));
            log.Stopped(Stop("App#2"));

            var recent = log.Recent("App#1", 5);
            Assert.Equal("App#1", Assert.Single(recent).InstanceId);
            Assert.Equal(2, log.TakeUnseen().Count);
        }

        [Fact]
        public void Marking_one_instance_seen_leaves_the_other_to_be_reported()
        {
            var log = NewLog();
            log.Stopped(Stop("App#1"));
            log.Stopped(Stop("App#2"));

            log.MarkSeen("App#1");

            Assert.Equal("App#2", Assert.Single(log.TakeUnseen()).InstanceId);
        }

        [Fact]
        public void Entries_come_back_oldest_first()
        {
            var log = NewLog();
            log.Stopped(Stop("App#1"));
            log.SolutionChanged("App#1");

            var taken = log.TakeUnseen();
            Assert.Equal(EventKind.Stopped, taken[0].Kind);
            Assert.Equal(EventKind.SolutionChanged, taken[1].Kind);
        }

        [Fact]
        public void The_oldest_entries_fall_off()
        {
            var log = NewLog();
            for (var i = 0; i < 300; i++) log.Stopped(Stop("App#1"));

            Assert.Equal(256, log.TakeUnseen().Count);
        }

        [Fact]
        public async Task A_waiter_is_woken_by_the_kind_it_asked_for()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Run);
            var waiting = log.WaitForAsync("App#1", new[] { EventKind.DebuggingEnded },
                TimeSpan.FromSeconds(5), CancellationToken.None);

            log.ModeChanged("App#1", DebugModes.Design);

            Assert.Equal(EventKind.DebuggingEnded, (await waiting).Kind);
        }

        [Fact]
        public async Task A_wait_that_nothing_answers_ends_as_null()
        {
            var log = NewLog();
            Assert.Null(await log.WaitForAsync(null, new[] { EventKind.InstanceGone },
                TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }

        [Fact]
        public async Task An_event_handed_to_a_waiter_is_not_shown_again()
        {
            var log = NewLog();
            log.InstanceGone("App#1");

            Assert.NotNull(await log.WaitForAsync("App#1", new[] { EventKind.InstanceGone },
                TimeSpan.FromMilliseconds(50), CancellationToken.None));
            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void Leaving_design_mode_is_debugging_starting()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Design);
            log.ModeChanged("App#1", DebugModes.Run);

            Assert.Equal(EventKind.DebuggingStarted, Assert.Single(log.TakeUnseen()).Kind);
        }

        [Fact]
        public void Running_and_breaking_are_not_events()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Run);
            log.ModeChanged("App#1", DebugModes.Break);
            log.ModeChanged("App#1", DebugModes.Run);

            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void A_mode_change_before_the_first_mode_is_known_says_nothing()
        {
            var log = NewLog();
            log.ModeChanged("App#1", DebugModes.Run);

            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void The_design_mode_that_follows_an_exit_is_not_a_second_line()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Run);
            log.Stopped(Stop("App#1", StopReason.Exited));
            _now = _now.AddSeconds(1);
            log.ModeChanged("App#1", DebugModes.Design);

            Assert.Equal(EventKind.Exited, Assert.Single(log.TakeUnseen()).Kind);
        }

        [Fact]
        public void A_session_ending_long_after_an_exit_is_its_own_line()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Run);
            log.Stopped(Stop("App#1", StopReason.Exited));
            log.TakeUnseen();
            _now = _now.AddSeconds(30);
            log.ModeChanged("App#1", DebugModes.Design);

            Assert.Equal(EventKind.DebuggingEnded, Assert.Single(log.TakeUnseen()).Kind);
        }

        [Fact]
        public void Everything_but_an_operation_finishing_moves_the_state_a_reply_was_read_from()
        {
            var log = NewLog();
            log.Stopped(Stop("App#1"));
            log.SolutionChanged("App#1");
            log.InstanceGone("App#1");

            Assert.All(log.TakeUnseen(), e => Assert.True(e.Invalidates));
        }

        [Fact]
        public void A_subscriber_sees_every_entry_as_it_arrives()
        {
            var log = NewLog();
            var seen = new List<EventKind>();
            log.Subscribe(e => seen.Add(e.Kind));

            log.Stopped(Stop("App#1"));
            log.InstanceGone("App#1");

            Assert.Equal(new[] { EventKind.Stopped, EventKind.InstanceGone }, seen);
        }

        static OperationInfo Operation(string id, string kind = "build", string state = "succeeded") =>
            new OperationInfo { OperationId = id, InstanceId = "App#1", Kind = kind, State = state, Terminal = true };

        [Fact]
        public void A_run_this_session_started_is_not_reported_back_to_it()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Design);

            log.Expect("App#1", Expected.RunStart);
            log.ModeChanged("App#1", DebugModes.Run);

            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void A_second_run_is_reported_because_only_the_first_was_asked_for()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Design);

            log.Expect("App#1", Expected.RunStart);
            log.ModeChanged("App#1", DebugModes.Run);
            log.ModeChanged("App#1", DebugModes.Design);
            log.TakeUnseen();
            log.ModeChanged("App#1", DebugModes.Run);

            Assert.Equal(EventKind.DebuggingStarted, Assert.Single(log.TakeUnseen()).Kind);
        }

        [Fact]
        public void An_expectation_nothing_answered_stops_hiding_after_fifteen_seconds()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Design);

            log.Expect("App#1", Expected.RunStart);
            _now = _now.AddSeconds(16);
            log.ModeChanged("App#1", DebugModes.Run);

            Assert.Equal(EventKind.DebuggingStarted, Assert.Single(log.TakeUnseen()).Kind);
        }

        [Fact]
        public void A_launch_that_builds_first_still_owns_the_run_it_starts()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Design);

            log.Expect("App#1", Expected.RunStart, "ab12");
            _now = _now.AddMinutes(3);
            log.OperationDone(Operation("ab12", "launch"));
            log.ModeChanged("App#1", DebugModes.Run);

            Assert.Equal(EventKind.OperationDone, Assert.Single(log.TakeUnseen()).Kind);
        }

        [Fact]
        public void A_launch_that_failed_takes_its_expectation_with_it()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Design);

            log.Expect("App#1", Expected.RunStart, "ab12");
            log.OperationDone(Operation("ab12", "launch", "failed"));
            log.TakeUnseen();
            log.ModeChanged("App#1", DebugModes.Run);

            Assert.Equal(EventKind.DebuggingStarted, Assert.Single(log.TakeUnseen()).Kind);
        }

        [Fact]
        public void A_call_that_failed_withdraws_what_it_expected()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Design);

            log.Expect("App#1", Expected.RunStart);
            log.Unexpect("App#1", Expected.RunStart);
            log.ModeChanged("App#1", DebugModes.Run);

            Assert.Equal(EventKind.DebuggingStarted, Assert.Single(log.TakeUnseen()).Kind);
        }

        [Fact]
        public void Stopping_hides_both_the_exit_and_the_session_ending()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Run);

            log.Expect("App#1", Expected.End);
            log.Stopped(Stop("App#1", StopReason.Exited));
            log.ModeChanged("App#1", DebugModes.Design);

            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void A_stop_the_reply_carried_is_not_repeated()
        {
            var log = NewLog();
            var stop = Stop("App#1");
            log.Stopped(stop);

            log.Delivered(stop);

            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void An_operation_the_reply_carried_is_not_repeated()
        {
            var log = NewLog();
            log.OperationDone(Operation("ab12"));

            log.OperationReported("ab12");

            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void An_operation_reported_before_its_push_arrives_is_still_not_repeated()
        {
            var log = NewLog();
            log.OperationReported("ab12");

            log.OperationDone(Operation("ab12"));

            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void An_operation_nobody_reported_is_shown()
        {
            var log = NewLog();
            log.OperationDone(Operation("ab12"));

            var entry = Assert.Single(log.TakeUnseen());
            Assert.Equal(EventKind.OperationDone, entry.Kind);
            Assert.Equal("ab12", entry.Operation.OperationId);
        }

        [Fact]
        public void Work_that_has_not_finished_is_not_logged()
        {
            var log = NewLog();
            log.OperationDone(new OperationInfo { OperationId = "ab12", InstanceId = "App#1", Kind = "build", Terminal = false });

            Assert.Empty(log.TakeUnseen());
        }

        [Fact]
        public void A_subscriber_is_not_told_what_the_model_already_has()
        {
            var log = NewLog();
            log.InitializeMode("App#1", DebugModes.Run);
            var seen = new List<EventKind>();
            log.Subscribe(e => seen.Add(e.Kind));

            log.Expect("App#1", Expected.End);
            log.Stopped(Stop("App#1", StopReason.Exited));

            Assert.Empty(seen);
        }
    }
}
