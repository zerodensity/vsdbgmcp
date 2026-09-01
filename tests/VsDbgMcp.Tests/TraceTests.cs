using System;
using System.Collections.Generic;
using System.Linq;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// The tracepoint sink without Visual Studio: the marker that separates a
    /// tracepoint's records from the program's own output, the buffer that keeps them,
    /// and what a caller is shown. What this cannot cover is the debug engine actually
    /// writing a record, which needs the extension loaded.
    /// </summary>
    public class TraceTests
    {
        static readonly DateTime Noon = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void A_marked_record_comes_back_as_the_message_that_was_written()
        {
            var marked = TraceMessage.Mark(7, "mic tick publish, n=42");

            var body = TraceMessage.Unmark(marked, out var id, out var cutShort);

            Assert.Equal(7, id);
            Assert.Equal("mic tick publish, n=42", body);
            Assert.False(cutShort);
        }

        [Fact]
        public void The_program_s_own_output_is_left_exactly_as_it_was()
        {
            var text = "[submix] callback 47/s";

            var body = TraceMessage.Unmark(text, out var id, out _);

            Assert.Equal(0, id);
            Assert.Same(text, body);
        }

        [Fact]
        public void A_record_whose_breakpoint_stopped_collecting_keeps_its_text()
        {
            var log = new TraceLog();

            var body = TraceMessage.Unmark(TraceMessage.Mark(9, "still logging"), out var id, out _);

            Assert.False(log.Add(id, body, Noon, false));
            Assert.Equal("still logging", body);
        }

        [Fact]
        public void Records_carry_the_time_they_arrived_and_which_hit_they_were()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);

            Assert.True(log.Add(7, "first", Noon, false));
            Assert.True(log.Add(7, "second", Noon.AddMilliseconds(20), false));

            var result = log.Read(7, 0);

            Assert.Equal(2L, result.Collected);
            Assert.Equal(new long[] { 1, 2 }, result.Records.ConvertAll(r => r.Hit).ToArray());
            Assert.Equal(Noon.AddMilliseconds(20), result.Records[1].Time);
        }

        [Fact]
        public void A_callback_that_never_stops_cannot_grow_the_buffer_forever()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);

            for (var i = 1; i <= TraceLog.Capacity + 500; i++)
                log.Add(7, "tick " + i, Noon.AddMilliseconds(i * 20), false);

            var result = log.Read(7, 0);

            Assert.Equal(TraceLog.Capacity, result.Records.Count);
            Assert.Equal(TraceLog.Capacity + 500L, result.Collected);

            // The hit numbers say which ones the buffer no longer holds.
            Assert.Equal(501L, result.Records[0].Hit);
            Assert.Equal("tick 501", result.Records[0].Text);
        }

        [Fact]
        public void A_tail_returns_the_newest_records()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);
            for (var i = 1; i <= 10; i++) log.Add(7, "tick " + i, Noon.AddMilliseconds(i * 20), false);

            var result = log.Read(7, 3);

            Assert.Equal(3, result.Records.Count);
            Assert.Equal("tick 8", result.Records[0].Text);
            Assert.Equal("tick 10", result.Records[2].Text);
        }

        [Fact]
        public void The_per_second_cap_drops_records_and_counts_what_it_dropped()
        {
            var log = new TraceLog();
            log.Start(7, 2, DateTime.UtcNow);

            for (var i = 1; i <= 5; i++) log.Add(7, "tick " + i, Noon.AddMilliseconds(i * 10), false);
            log.Add(7, "next second", Noon.AddSeconds(2), false);

            var result = log.Read(7, 0);

            Assert.Equal(3, result.Records.Count);
            Assert.Equal(6L, result.Collected);
            Assert.Equal(3L, result.Dropped);

            // A gap in the hit numbers is what makes the loss visible.
            Assert.Equal(6L, result.Records[2].Hit);
        }

        [Fact]
        public void Setting_a_tracepoint_again_measures_from_now()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);
            log.Add(7, "before", Noon, false);

            log.Start(7, 0, DateTime.UtcNow);

            var result = log.Read(7, 0);
            Assert.Empty(result.Records);
            Assert.Equal(0L, result.Collected);
        }

        [Fact]
        public void Reading_an_id_that_is_not_collecting_names_the_ones_that_are()
        {
            var log = new TraceLog();
            log.Start(9, 0, DateTime.UtcNow);
            log.Add(9, "tick", Noon, false);

            var result = log.Read(3, 0);

            Assert.Empty(result.Records);
            Assert.Contains("#3 is not collecting", result.Message);
            Assert.Contains("#9 (1 record)", result.Message);
        }

        [Fact]
        public void With_nothing_collecting_the_reply_says_how_to_start()
        {
            var result = new TraceLog().Read(3, 0);

            Assert.Contains("collect: true", result.Message);
        }

        [Fact]
        public void A_removed_breakpoint_stops_collecting()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);
            log.Forget(7);

            Assert.False(log.IsCollecting(7));
            Assert.False(log.Add(7, "tick", Noon, false));
        }

        [Fact]
        public void Every_expression_in_a_message_is_found_once_in_the_order_it_appears()
        {
            var found = TraceMessage.Expressions("publishing samples={n} ch={ch} rate={rate} again={n}");

            Assert.Equal(new[] { "n", "ch", "rate" }, found);
        }

        [Fact]
        public void An_expression_carrying_braces_of_its_own_is_read_whole()
        {
            var found = TraceMessage.Expressions("dst={fn({a})} src={p->q}");

            Assert.Equal(new[] { "fn({a})", "p->q" }, found);
        }

        [Fact]
        public void A_brace_the_message_shows_is_not_an_expression()
        {
            var found = TraceMessage.Expressions(@"state \{idle\} count={n}");

            Assert.Equal(new[] { "n" }, found);
        }

        [Fact]
        public void An_unclosed_brace_yields_nothing_rather_than_the_rest_of_the_message()
        {
            Assert.Empty(TraceMessage.Expressions("count={n"));
            Assert.Empty(TraceMessage.Expressions("nothing here"));
            Assert.Empty(TraceMessage.Expressions(null));
        }

        [Fact]
        public void The_rendered_stream_carries_timing_and_a_rate()
        {
            var result = new TraceResult
            {
                BreakpointId = 7,
                Collected = 1200,
                Records = new List<TraceRecord>
                {
                    new TraceRecord { Hit = 1198, Time = Noon, Text = "publish samples=1024" },
                    new TraceRecord { Hit = 1199, Time = Noon.AddMilliseconds(20), Text = "publish samples=1024" },
                    new TraceRecord { Hit = 1200, Time = Noon.AddMilliseconds(40), Text = "publish samples=1024" }
                }
            };

            var text = Render.Trace(result);

            Assert.Contains("#7  3 of 1200 records", text);
            Assert.Contains("50.0/s over 0.04s", text);
            Assert.Contains("#1200", text);
            Assert.Contains("publish samples=1024", text);
        }

        [Fact]
        public void Dropped_records_are_reported_rather_than_hidden()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 7,
                Collected = 400,
                Dropped = 350,
                Records = new List<TraceRecord>
                {
                    new TraceRecord { Hit = 400, Time = Noon, Text = "tick" }
                }
            });

            Assert.Contains("350 dropped by the per-second cap", text);
        }

        [Fact]
        public void A_tracepoint_expression_that_will_not_evaluate_is_named_at_bind_time()
        {
            var text = Render.Breakpoint(new BreakpointInfo
            {
                Id = 7,
                Kind = BreakpointKind.Location,
                File = @"D:\repo\Engine\audio.cpp",
                Line = 214,
                Bound = true,
                Collecting = true,
                LogMessage = "publish samples={n} dst={ResInfo}",
                LogExpressions = new List<TraceExpression>
                {
                    new TraceExpression { Expression = "n", Value = "1024" },
                    new TraceExpression { Expression = "ResInfo", Error = "identifier \"ResInfo\" is undefined" }
                }
            });

            Assert.Contains("{n} = 1024", text);
            Assert.Contains("{ResInfo}  -- identifier \"ResInfo\" is undefined", text);
            Assert.Contains("trace_read", text);
        }

        [Fact]
        public void A_check_that_did_not_happen_is_reported_as_that_and_not_as_success()
        {
            var text = Render.Breakpoint(new BreakpointInfo
            {
                Id = 7,
                Kind = BreakpointKind.Location,
                File = @"D:\repo\Engine\audio.cpp",
                Line = 214,
                Bound = true,
                LogMessage = "publish samples={n}",
                LogExpressions = new List<TraceExpression> { new TraceExpression { Expression = "n" } },
                LogCheckDeferred = "the debuggee is not stopped, so nothing was evaluated"
            });

            Assert.Contains("{n}", text);
            Assert.DoesNotContain("{n} =", text);
            Assert.Contains("not checked: the debuggee is not stopped", text);
        }

        // Visual Studio prints a tracepoint's record to the Debug pane itself instead of
        // raising it as debuggee output, and on Visual Studio 2026 that pane is not a
        // text buffer that can be watched. Records are then recovered from the pane after
        // the fact, which keeps their order and loses their times. These cover what that
        // costs, because the difference is easy to paper over by accident.

        [Fact]
        public void A_record_with_no_time_makes_the_whole_stream_untimed()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);

            log.Add(7, "first", default(DateTime), false);
            log.Add(7, "second", default(DateTime), false);

            var result = log.Read(7, 0);

            Assert.False(result.Timed);
            Assert.Equal(2, result.Collected);
            Assert.Equal(new long[] { 1, 2 }, result.Records.Select(r => r.Hit).ToArray());
        }

        [Fact]
        public void A_stream_that_was_timed_stays_timed()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);

            log.Add(7, "first", new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc), false);

            Assert.True(log.Read(7, 0).Timed);
        }

        [Fact]
        public void Untimed_records_are_rendered_without_a_time_and_say_why()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 7,
                Collected = 2,
                Timed = false,
                StartedUtc = DateTime.UtcNow.AddSeconds(-4),
                Records = new List<TraceRecord>
                {
                    new TraceRecord { Hit = 1, Text = "worker 1" },
                    new TraceRecord { Hit = 2, Text = "worker 2" }
                }
            });

            // 00:00:00.000 is what a default DateTime renders as, and printing it would
            // read as a measurement rather than the absence of one.
            Assert.DoesNotContain("00:00:00", text);
            Assert.Contains("read back out of the Debug pane", text);
            Assert.Contains("#1", text);
            Assert.Contains("worker 2", text);
        }

        [Fact]
        public void A_timed_stream_still_prints_each_record_s_time()
        {
            var start = new DateTime(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 7,
                Collected = 2,
                Timed = true,
                StartedUtc = start,
                Records = new List<TraceRecord>
                {
                    new TraceRecord { Hit = 1, Text = "worker 1", Time = start },
                    new TraceRecord { Hit = 2, Text = "worker 2", Time = start.AddSeconds(1) }
                }
            });

            Assert.DoesNotContain("read back out of the Debug pane", text);
            Assert.Contains("/s over", text);
        }

        // An empty stream used to say the tracepoint had not been hit. Three collecting
        // tracepoints in three modules reported bound and collected nothing, one of them
        // on a line a thread had just been caught sitting on, and that sentence is what
        // turned it into "this function is never executing". The buffer knows no such
        // thing: a record that was never written and a record that never arrived look
        // exactly alike from here.

        [Fact]
        public void An_empty_stream_does_not_claim_the_tracepoint_was_never_hit()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);

            var message = log.Read(7, 0).Message;

            Assert.DoesNotContain("has not been hit", message);
            Assert.Contains("never hit", message);
            Assert.Contains("never reached this buffer", message);
            Assert.Contains("tells those apart", message);
        }

        /// <summary>
        /// The experiment proves the difference is this tracepoint. That is not the same
        /// as the line not running: a condition, a hit filter, a disabled or unbound
        /// tracepoint all keep a line that really was reached silent, so the advice stops
        /// where the evidence does.
        /// </summary>
        [Fact]
        public void What_would_settle_it_claims_only_what_the_experiment_shows()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);

            var settles = log.Read(7, 0).Settles;

            Assert.Contains("a line you have already watched the debugger stop on", settles);
            Assert.Contains("the difference is this tracepoint", settles);
            Assert.Contains("condition or hit filter", settles);
            Assert.DoesNotContain("this line is not being reached", settles);
        }

        /// <summary>
        /// Whether records are picked up as they land or recovered from the pane's text
        /// afterwards changes what an empty stream is worth. It says which of the two it
        /// is doing and nothing about why, because not watching can mean this Visual
        /// Studio will not allow it or only that the pane did not exist yet.
        /// </summary>
        [Fact]
        public void An_empty_stream_says_whether_the_pane_is_being_watched()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);

            var unwatched = log.Read(7, 0).Message;
            Assert.Contains("not being watched here", unwatched);
            Assert.DoesNotContain("cannot be watched on this Visual Studio", unwatched);

            log.PaneWatched = true;
            Assert.Contains("being watched as it fills", log.Read(7, 0).Message);
        }

        /// <summary>
        /// Another tracepoint collecting normally is the strongest evidence available
        /// here, because every record comes through the same pane. It rules the path in
        /// and leaves the question about this line alone.
        /// </summary>
        [Fact]
        public void An_empty_stream_reports_that_records_are_arriving_elsewhere()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);
            log.Start(9, 0, DateTime.UtcNow);
            log.Add(9, "tick", Noon, false);

            var message = log.Read(7, 0).Message;

            Assert.Contains("#9 (1 record)", message);
            Assert.Contains("so that path does work", message);
            Assert.Contains("this tracepoint not logging", message);
        }

        [Fact]
        public void With_no_record_anywhere_the_reply_rules_nothing_in_or_out()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);
            log.Start(9, 0, DateTime.UtcNow);

            var message = log.Read(7, 0).Message;

            Assert.Contains("No tracepoint here has collected a record at all", message);
            Assert.Contains("nothing rules that path in or out", message);
            Assert.DoesNotContain("#9", message);
        }

        /// <summary>
        /// Records that arrived and were thrown away are not records that never came,
        /// so the explanation for an empty stream belongs only to a stream nothing has
        /// ever reached.
        /// </summary>
        [Fact]
        public void A_stream_the_cap_thinned_carries_no_explanation_for_being_empty()
        {
            var log = new TraceLog();
            log.Start(7, 1, DateTime.UtcNow);
            log.Add(7, "first", Noon, false);
            log.Add(7, "second", Noon.AddMilliseconds(10), false);

            var result = log.Read(7, 0);

            Assert.Equal(2L, result.Collected);
            Assert.Equal(1L, result.Dropped);
            Assert.Null(result.Message);
        }

        // The message is written between markers of this server's own. Both are literal
        // text, so the debugger copies them whatever the {expr} parts do: a record that
        // arrives proves the line was reached, and one that arrives without its end was
        // cut short before the message was finished.

        [Fact]
        public void A_whole_record_carries_both_markers_and_neither_reaches_the_reader()
        {
            var body = TraceMessage.Unmark(TraceMessage.Mark(7, "publish n=1024"), out var id, out var cutShort);

            Assert.Equal(7, id);
            Assert.Equal("publish n=1024", body);
            Assert.False(cutShort);
            Assert.DoesNotContain("vsdbg", body);
        }

        [Fact]
        public void A_record_that_stopped_before_the_end_marker_is_marked_as_cut_short()
        {
            var body = TraceMessage.Unmark("[vsdbg:7] publish n=", out var id, out var cutShort);

            Assert.Equal(7, id);
            Assert.Equal("publish n=", body);
            Assert.True(cutShort);
        }

        /// <summary>
        /// The pane's last line has nothing after it yet, so the reader cannot tell a
        /// finished record from one still being written. The end marker is what settles
        /// that, and a line without it waits rather than arriving half read.
        /// </summary>
        [Fact]
        public void Only_a_record_carrying_its_end_counts_as_finished()
        {
            Assert.True(TraceMessage.Finished(TraceMessage.Mark(7, "publish n=1024")));
            Assert.False(TraceMessage.Finished("[vsdbg:7] publish n="));
            Assert.False(TraceMessage.Finished("[submix] callback 47/s"));
            Assert.False(TraceMessage.Finished(""));
            Assert.False(TraceMessage.Finished(null));
        }

        /// <summary>
        /// A message a user wrote with the end marker in it survives, because the last
        /// one is the one taken off. Anything else would eat the reader's own text.
        /// </summary>
        [Fact]
        public void A_message_that_ends_the_way_the_marker_does_keeps_its_own_text()
        {
            var body = TraceMessage.Unmark(TraceMessage.Mark(7, "done [/vsdbg]"), out var id, out var cutShort);

            Assert.Equal(7, id);
            Assert.Equal("done [/vsdbg]", body);
            Assert.False(cutShort);
        }

        /// <summary>
        /// The pane hands lines over with their line endings still on, which is what the
        /// end marker has to be recognised through.
        /// </summary>
        [Fact]
        public void A_record_still_carrying_its_line_ending_is_read_as_whole()
        {
            var body = TraceMessage.Unmark(TraceMessage.Mark(7, "publish n=1024") + "\r\n",
                                           out var id, out var cutShort);

            Assert.Equal(7, id);
            Assert.Equal("publish n=1024", body);
            Assert.False(cutShort);
        }

        [Fact]
        public void An_empty_message_leaves_an_empty_record_and_not_a_marker()
        {
            var body = TraceMessage.Unmark(TraceMessage.Mark(7, ""), out var id, out var cutShort);

            Assert.Equal(7, id);
            Assert.Equal("", body);
            Assert.False(cutShort);
        }

        [Fact]
        public void Cut_short_records_are_counted_separately_and_still_kept()
        {
            var log = new TraceLog();
            log.Start(7, 0, DateTime.UtcNow);

            log.Add(7, "publish n=1024", Noon, false);
            log.Add(7, "publish n=", Noon.AddMilliseconds(20), true);

            var result = log.Read(7, 0);

            Assert.Equal(2L, result.Collected);
            Assert.Equal(1L, result.CutShort);
            Assert.Equal(new[] { "publish n=1024", "publish n=" }, result.Records.Select(r => r.Text).ToArray());
        }

        /// <summary>
        /// What stopped the message is not known from here, so it is not named. What is
        /// known is that a record arrived, and a record arriving means the line ran.
        /// </summary>
        [Fact]
        public void A_cut_short_record_says_the_line_was_reached_without_naming_a_cause()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 7,
                Collected = 2,
                CutShort = 2,
                Records = new List<TraceRecord>
                {
                    new TraceRecord { Hit = 1, Time = Noon, Text = "publish n=" },
                    new TraceRecord { Hit = 2, Time = Noon.AddMilliseconds(20), Text = "publish n=" }
                }
            });

            Assert.Contains("2 of the records collected arrived without the end marker", text);
            Assert.Contains("The line itself was reached", text);
            Assert.DoesNotContain("would not evaluate in that frame", text);
        }

        [Fact]
        public void Records_dropped_by_the_cap_and_records_cut_short_are_both_reported()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 7,
                Collected = 400,
                Dropped = 350,
                CutShort = 12,
                Records = new List<TraceRecord> { new TraceRecord { Hit = 400, Time = Noon, Text = "tick" } }
            });

            Assert.Contains("350 dropped by the per-second cap", text);
            Assert.Contains("12 of the records collected arrived without the end marker", text);
        }

        [Fact]
        public void A_whole_stream_says_nothing_about_records_being_cut_short()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 7,
                Collected = 1,
                Records = new List<TraceRecord> { new TraceRecord { Hit = 1, Time = Noon, Text = "publish n=1024" } }
            });

            Assert.DoesNotContain("end marker", text);
        }

        /// <summary>
        /// Tools > Options > Debugging > "Redirect all Output Window text to the
        /// Immediate Window" sends a tracepoint's records somewhere this cannot read,
        /// which is exactly a stream that stays empty however often the line runs.
        /// </summary>
        [Fact]
        public void An_empty_stream_carries_what_Visual_Studio_s_own_settings_say()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 7,
                Records = new List<TraceRecord>(),
                Message = "#7 has collected nothing.",
                OutputRedirect = "Visual Studio is redirecting Output window text to the Immediate window."
            });

            Assert.Contains("#7 has collected nothing.", text);
            Assert.Contains("redirecting Output window text to the Immediate window", text);
        }

        /// <summary>
        /// A cause already found makes the experiment pointless, so the experiment is
        /// read after it rather than before.
        /// </summary>
        [Fact]
        public void What_to_do_about_an_empty_stream_is_read_after_why_it_is_empty()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 7,
                Records = new List<TraceRecord>(),
                Message = "#7 has collected nothing.",
                TracepointState = "The breakpoint says it is disabled.",
                OutputRedirect = "Visual Studio is redirecting Output window text to the Immediate window.",
                Settles = "What settles it: set a collecting tracepoint on a line already seen to stop."
            });

            Assert.True(text.IndexOf("disabled", StringComparison.Ordinal) <
                        text.IndexOf("Immediate window", StringComparison.Ordinal));
            Assert.True(text.IndexOf("Immediate window", StringComparison.Ordinal) <
                        text.IndexOf("What settles it", StringComparison.Ordinal));
        }

        /// <summary>
        /// Nothing after a moment and nothing after ten minutes of running are different
        /// evidence, and a reply that leaves the span out reads the same either way.
        /// </summary>
        [Fact]
        public void An_empty_stream_says_how_long_it_has_been_collecting()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 7,
                Records = new List<TraceRecord>(),
                Message = "#7 has collected nothing.",
                StartedUtc = DateTime.UtcNow.AddSeconds(-42)
            });

            Assert.Contains("It has been collecting for 42", text);
            Assert.Contains("time the debuggee spent stopped", text);
        }

        [Fact]
        public void A_stream_that_never_started_reports_no_span_at_all()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 3,
                Records = new List<TraceRecord>(),
                Message = "Tracepoint #3 is not collecting."
            });

            Assert.DoesNotContain("It has been collecting for", text);
        }

        /// <summary>
        /// The cap counts records into one second and then the next. An undated record
        /// belongs to neither, and the window it never rolls kept the first few and threw
        /// away everything after them for as long as the program ran - under a heading
        /// calling itself a rate. Keeping everything is the only honest reading.
        /// </summary>
        [Fact]
        public void A_cap_is_not_applied_to_records_that_carry_no_time()
        {
            var log = new TraceLog();
            log.Start(4, 2, Noon);

            for (var i = 0; i < 10; i++) log.Add(4, "tick " + i, default(DateTime), false);

            var result = log.Read(4, 0);

            Assert.Equal(10, result.Collected);
            Assert.Equal(0, result.Dropped);
            Assert.Equal(10, result.Records.Count);
            Assert.True(result.CapUnused);
        }

        [Fact]
        public void A_cap_still_applies_where_the_records_are_timed()
        {
            var log = new TraceLog();
            log.Start(5, 2, Noon);

            for (var i = 0; i < 10; i++) log.Add(5, "tick " + i, Noon.AddMilliseconds(i * 10), false);

            var result = log.Read(5, 0);

            Assert.Equal(10, result.Collected);
            Assert.Equal(8, result.Dropped);
            Assert.False(result.CapUnused);
        }

        [Fact]
        public void A_stream_with_no_cap_asked_for_says_nothing_about_one()
        {
            var log = new TraceLog();
            log.Start(6, 0, Noon);

            log.Add(6, "tick", default(DateTime), false);

            Assert.False(log.Read(6, 0).CapUnused);
        }

        [Fact]
        public void A_cap_that_could_not_be_applied_says_so_instead_of_reporting_a_rate()
        {
            var text = Render.Trace(new TraceResult
            {
                BreakpointId = 4,
                Collected = 10,
                Timed = false,
                CapUnused = true,
                StartedUtc = DateTime.UtcNow.AddSeconds(-5),
                Records = new List<TraceRecord> { new TraceRecord { Hit = 1, Text = "tick" } }
            });

            Assert.Contains("per-second cap was not applied", text);
            Assert.Contains("everyNthHit", text);
        }
    }
}
