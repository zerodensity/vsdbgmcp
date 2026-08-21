using System;
using System.Collections.Generic;
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

            var body = TraceMessage.Unmark(marked, out var id);

            Assert.Equal(7, id);
            Assert.Equal("mic tick publish, n=42", body);
        }

        [Fact]
        public void The_program_s_own_output_is_left_exactly_as_it_was()
        {
            var text = "[submix] callback 47/s";

            var body = TraceMessage.Unmark(text, out var id);

            Assert.Equal(0, id);
            Assert.Same(text, body);
        }

        [Fact]
        public void A_record_whose_breakpoint_stopped_collecting_keeps_its_text()
        {
            var log = new TraceLog();

            var body = TraceMessage.Unmark(TraceMessage.Mark(9, "still logging"), out var id);

            Assert.False(log.Add(id, body, Noon));
            Assert.Equal("still logging", body);
        }

        [Fact]
        public void Records_carry_the_time_they_arrived_and_which_hit_they_were()
        {
            var log = new TraceLog();
            log.Start(7, 0);

            Assert.True(log.Add(7, "first", Noon));
            Assert.True(log.Add(7, "second", Noon.AddMilliseconds(20)));

            var result = log.Read(7, 0);

            Assert.Equal(2L, result.Collected);
            Assert.Equal(new long[] { 1, 2 }, result.Records.ConvertAll(r => r.Hit).ToArray());
            Assert.Equal(Noon.AddMilliseconds(20), result.Records[1].Time);
        }

        [Fact]
        public void A_callback_that_never_stops_cannot_grow_the_buffer_forever()
        {
            var log = new TraceLog();
            log.Start(7, 0);

            for (var i = 1; i <= TraceLog.Capacity + 500; i++)
                log.Add(7, "tick " + i, Noon.AddMilliseconds(i * 20));

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
            log.Start(7, 0);
            for (var i = 1; i <= 10; i++) log.Add(7, "tick " + i, Noon.AddMilliseconds(i * 20));

            var result = log.Read(7, 3);

            Assert.Equal(3, result.Records.Count);
            Assert.Equal("tick 8", result.Records[0].Text);
            Assert.Equal("tick 10", result.Records[2].Text);
        }

        [Fact]
        public void The_per_second_cap_drops_records_and_counts_what_it_dropped()
        {
            var log = new TraceLog();
            log.Start(7, 2);

            for (var i = 1; i <= 5; i++) log.Add(7, "tick " + i, Noon.AddMilliseconds(i * 10));
            log.Add(7, "next second", Noon.AddSeconds(2));

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
            log.Start(7, 0);
            log.Add(7, "before", Noon);

            log.Start(7, 0);

            var result = log.Read(7, 0);
            Assert.Empty(result.Records);
            Assert.Equal(0L, result.Collected);
        }

        [Fact]
        public void Reading_an_id_that_is_not_collecting_names_the_ones_that_are()
        {
            var log = new TraceLog();
            log.Start(9, 0);
            log.Add(9, "tick", Noon);

            var result = log.Read(3, 0);

            Assert.Empty(result.Records);
            Assert.Contains("#3 is not collecting", result.Message);
            Assert.Contains("#9 (1 records)", result.Message);
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
            log.Start(7, 0);
            log.Forget(7);

            Assert.False(log.IsCollecting(7));
            Assert.False(log.Add(7, "tick", Noon));
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
    }
}
