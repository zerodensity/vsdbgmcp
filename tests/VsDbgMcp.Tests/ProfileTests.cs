using System.Collections.Generic;
using System.Linq;
using VsDbgMcp.Shim.Profiling;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// A sampling profile is counted stacks, and every question anyone asks of one is a
    /// different sum over the same counts. Getting a sum wrong does not fail loudly: it
    /// reports the wrong function as the expensive one, which is worse than reporting
    /// nothing, so each of these sums is pinned to a profile whose answer is known.
    /// </summary>
    public class ProfileTests
    {
        /// <summary>
        /// Builds a capture from stacks written outermost first. A frame is "module!name",
        /// and a module called "app" is the user's own code.
        /// </summary>
        sealed class Builder
        {
            readonly Capture _capture = new Capture();
            readonly Dictionary<string, int> _known = new Dictionary<string, int>();

            public Builder Stack(string path, int samples, int thread = 7, string file = null, int line = 0)
            {
                var frames = path.Split(';').Select(f => Frame(f, file, line)).ToArray();
                _capture.Stacks.Add(new Capture.Stack { Frames = frames, ThreadId = thread, Samples = samples });
                _capture.Samples += samples;
                _capture.Stacked += samples;
                _capture.Threads[thread] = _capture.Threads.TryGetValue(thread, out var had) ? had + samples : samples;
                return this;
            }

            int Frame(string text, string file, int line)
            {
                if (_known.TryGetValue(text, out var index)) return index;

                var bang = text.IndexOf('!');
                var module = text.Substring(0, bang);
                var method = text.Substring(bang + 1);

                // "Work@hot" and "Work@cold" are two frames of the one function Work,
                // which is how one function comes to have more than one source line.
                var at = method.IndexOf('@');
                if (at >= 0) method = method.Substring(0, at);

                _capture.Frames.Add(new Capture.Frame
                {
                    Module = module,
                    Method = method == "?" ? null : method,
                    Address = "0x1000",
                    UserCode = module == "app",
                    File = file,
                    Line = line
                });

                index = _capture.Frames.Count - 1;
                _known[text] = index;
                return index;
            }

            public Capture Done() => _capture;
        }

        static Capture Simple() =>
            new Builder()
                .Stack("sys!start;app!main;app!Work;app!Inner", 60)
                .Stack("sys!start;app!main;app!Work;app!Other", 30)
                .Stack("sys!start;app!main;app!Idle", 10)
                .Done();

        [Fact]
        public void Self_counts_only_the_function_that_was_running()
        {
            var rows = Simple().Self();

            Assert.Equal("app!Inner", rows[0].Key);
            Assert.Equal(60, rows[0].Samples);
            Assert.Equal(30, rows.Single(r => r.Key == "app!Other").Samples);

            // Work was on the stack for 90 samples and running for none of them.
            Assert.DoesNotContain(rows, r => r.Key == "app!Work");
        }

        [Fact]
        public void Inclusive_counts_everything_the_call_was_underneath()
        {
            var rows = Simple().Inclusive();

            Assert.Equal(100, rows.Single(r => r.Key == "app!main").Samples);
            Assert.Equal(90, rows.Single(r => r.Key == "app!Work").Samples);
        }

        [Fact]
        public void A_function_that_called_itself_is_still_one_sample()
        {
            var capture = new Builder().Stack("app!main;app!Down;app!Down;app!Down;app!Leaf", 5).Done();

            Assert.Equal(5, capture.Inclusive().Single(r => r.Key == "app!Down").Samples);
        }

        [Fact]
        public void The_hot_path_follows_the_heaviest_branch_and_stops_where_it_thins_out()
        {
            var path = Simple().HotPath().Select(r => r.Key).ToList();

            Assert.Equal(new[] { "app!main", "app!Work", "app!Inner" }, path);
        }

        [Fact]
        public void The_shared_frames_above_the_program_are_counted_so_they_can_be_left_out()
        {
            // start is on every stack and is not the user's code; main is.
            Assert.Equal(1, Simple().EntryDepth());
        }

        [Fact]
        public void Trimming_stops_at_the_first_frame_of_the_users_own_code()
        {
            var capture = new Builder().Stack("app!main;sys!deep;sys!deeper", 5).Done();

            Assert.Equal(0, capture.EntryDepth());
        }

        [Fact]
        public void Trimming_stops_where_the_stacks_stop_agreeing()
        {
            var capture = new Builder()
                .Stack("sys!start;sys!one;app!main", 5)
                .Stack("sys!start;sys!two;app!main", 5)
                .Done();

            Assert.Equal(1, capture.EntryDepth());
        }

        [Fact]
        public void Callers_and_callees_are_counted_by_the_samples_through_that_edge()
        {
            var capture = Simple();

            Assert.Equal("app!main", capture.Callers("app!Work").Single().Key);

            var callees = capture.Callees("app!Work");
            Assert.Equal(60, callees.Single(r => r.Key == "app!Inner").Samples);
            Assert.Equal(30, callees.Single(r => r.Key == "app!Other").Samples);
        }

        [Fact]
        public void A_function_can_be_named_without_its_module()
        {
            Assert.Equal("app!Inner", Simple().Matches("Inner").Single());
            Assert.Equal("app!Inner", Simple().Matches("app!Inner").Single());
            Assert.Empty(Simple().Matches("NotThere"));
        }

        [Fact]
        public void A_function_can_be_named_without_the_namespace_it_sits_in()
        {
            var capture = new Builder().Stack("app!main;app!`anonymous namespace'::Worker", 5).Done();

            Assert.Equal("app!`anonymous namespace'::Worker", capture.Matches("Worker").Single());
        }

        [Fact]
        public void One_name_meaning_two_costs_is_not_answered_with_either()
        {
            var capture = new Builder()
                .Stack("app!main;app!left::Run", 5)
                .Stack("app!main;app!right::Run", 5)
                .Done();

            Assert.Equal(2, capture.Matches("Run").Count);
        }

        [Fact]
        public void Lines_gather_the_samples_that_landed_on_each_one()
        {
            var capture = new Builder()
                .Stack("app!main;app!Work", 12, file: @"c:\src\work.cpp", line: 42)
                .Done();

            var lines = capture.Lines("app!Work");
            Assert.Equal("work.cpp:42", lines.Single().Source);
            Assert.Equal(12, lines.Single().Samples);
        }

        [Fact]
        public void A_module_with_no_symbols_is_one_row_rather_than_one_per_address()
        {
            var capture = new Builder()
                .Stack("app!main;other!?", 4)
                .Stack("app!main;app!Work;other!?", 6)
                .Done();

            var rows = capture.Self();
            Assert.Equal("other!(no symbols)", rows[0].Key);
            Assert.Equal(10, rows[0].Samples);

            Assert.Equal(10, capture.Unresolved().Single().Samples);
        }

        [Fact]
        public void A_comparison_reports_where_the_share_moved_to()
        {
            var before = new Builder().Stack("app!main;app!Slow", 90).Stack("app!main;app!Fast", 10).Done();
            var after = new Builder().Stack("app!main;app!Slow", 10).Stack("app!main;app!Fast", 90).Done();

            var changes = Capture.Compare(before, after);

            var slow = changes.Single(c => c.Key == "app!Slow");
            Assert.Equal(-80, slow.Points, 1);
            Assert.False(slow.IsNew);

            Assert.Equal(80, changes.Single(c => c.Key == "app!Fast").Points, 1);
        }

        [Fact]
        public void A_cost_that_was_not_there_before_is_marked_as_new()
        {
            var before = new Builder().Stack("app!main;app!Work", 100).Done();
            var after = new Builder().Stack("app!main;app!Work", 50).Stack("app!main;app!Copy", 50).Done();

            var appeared = Capture.Compare(before, after).Single(c => c.Key == "app!Copy");

            Assert.True(appeared.IsNew);
            Assert.Equal(50, appeared.Points, 1);
        }

        [Fact]
        public void Comparing_runs_of_different_lengths_compares_shares_rather_than_counts()
        {
            // The same program, sampled for twice as long. Nothing about it changed.
            var shorter = new Builder().Stack("app!main;app!Work", 50).Done();
            var longer = new Builder().Stack("app!main;app!Work", 100).Done();

            Assert.All(Capture.Compare(shorter, longer), c => Assert.Equal(0, c.Points, 1));
        }

        [Fact]
        public void Processor_time_is_only_reported_when_the_sampling_rate_is_known()
        {
            var capture = Simple();
            capture.Seconds = 10;

            Assert.Null(capture.CpuSeconds());

            capture.SamplesPerSecond = 1000;
            Assert.Equal(0.1, capture.CpuSeconds().Value, 3);
        }

        [Fact]
        public void A_capture_that_cannot_tell_its_rate_reports_no_processor_time()
        {
            var capture = Simple();
            capture.Seconds = 10;
            capture.SamplesPerSecond = 0;

            Assert.Null(capture.CpuSeconds());
        }

        [Fact]
        public void Several_busy_threads_use_more_processor_time_than_the_clock_ran_for()
        {
            // Four threads on four cores for one second is four seconds of processor
            // time. A share of the wall clock would have to call that 400%.
            var capture = new Builder()
                .Stack("app!main;app!Work", 1000, thread: 1)
                .Stack("app!main;app!Work", 1000, thread: 2)
                .Stack("app!main;app!Work", 1000, thread: 3)
                .Stack("app!main;app!Work", 1000, thread: 4)
                .Done();
            capture.Seconds = 1;
            capture.SamplesPerSecond = 1000;

            Assert.Equal(4, capture.CpuSeconds().Value, 3);
        }

        [Fact]
        public void The_tree_keeps_the_shape_and_leaves_out_what_is_too_small_to_matter()
        {
            var nodes = Simple().Tree();

            Assert.Equal("app!main", nodes[0].Row.Key);
            Assert.Equal(0, nodes[0].Depth);
            Assert.Equal("app!Work", nodes[1].Row.Key);
            Assert.Equal(1, nodes[1].Depth);
            Assert.Contains(nodes, n => n.Row.Key == "app!Inner" && n.Depth == 2);
        }

        [Theory]
        [InlineData("sleep_for<__int64,std::ratio<1,1000> >", "sleep_for<>")]
        [InlineData("std::vector<int>::push_back", "std::vector<>::push_back")]
        [InlineData("Plain", "Plain")]
        [InlineData("", "")]
        public void A_templates_arguments_are_left_out_of_the_name(string method, string expected)
        {
            Assert.Equal(expected, Capture.Frame.Collapse(method));
        }

        [Fact]
        public void Every_instantiation_of_one_template_is_one_row()
        {
            var capture = new Builder()
                .Stack("app!main;app!push<int>", 4)
                .Stack("app!main;app!push<float>", 6)
                .Done();

            var rows = capture.Self();
            Assert.Single(rows);
            Assert.Equal("app!push<>", rows[0].Key);
            Assert.Equal(10, rows[0].Samples);
        }

        [Fact]
        public void A_function_asked_for_with_its_template_arguments_still_matches()
        {
            var capture = new Builder().Stack("app!main;app!push<int>", 4).Done();

            Assert.Equal("app!push<>", capture.Matches("push<int>").Single());
            Assert.Equal("app!push<>", capture.Matches("push<>").Single());
        }

        [Fact]
        public void The_hot_path_stops_before_it_stops_being_readable()
        {
            var deep = string.Join(";", Enumerable.Range(0, 40).Select(i => "app!f" + i));

            Assert.Equal(6, new Builder().Stack(deep, 50).Done().HotPath(0.2, 6).Count);
        }

        [Fact]
        public void A_thread_never_holds_more_than_all_of_the_samples()
        {
            var capture = new Builder()
                .Stack("app!main;app!Work", 100, thread: 1)
                .Done();

            // Samples that carried no stack are counted in the total and attributed to
            // nothing, so a thread measured against them would run past a hundred.
            capture.Samples += 40;

            var attributed = capture.Stacks.Sum(s => s.Samples);
            Assert.True(capture.ByThread()[0].Value <= attributed);
        }

        [Fact]
        public void The_line_beside_a_function_is_the_one_most_samples_landed_on()
        {
            // Two addresses inside one function, on two different lines. The row has
            // room for one, and the busier line is the one worth printing.
            var capture = new Builder()
                .Stack("app!main;app!Work@cold", 3, file: @"c:\src\work.cpp", line: 10)
                .Stack("app!main;app!Work@hot", 97, file: @"c:\src\work.cpp", line: 55)
                .Done();

            // Both frames are the same function, so they are one row.
            var row = capture.Self().Single(r => r.Key.StartsWith("app!Work"));
            Assert.Equal(100, row.Samples);
            Assert.Equal("work.cpp:55", row.Source);
        }

        // ---- a reply must not answer a question that was not the one asked ----

        [Fact]
        public void One_reading_at_a_time_is_answered_and_two_are_refused()
        {
            Assert.Null(new ProfileQuery().Conflict(false));
            Assert.Null(new ProfileQuery { Function = "Work" }.Conflict(false));
            Assert.Null(new ProfileQuery { Module = "app" }.Conflict(false));
            Assert.Null(new ProfileQuery { Sort = ProfileQuery.Inclusive }.Conflict(false));
        }

        [Fact]
        public void Module_and_tree_filters_compose()
        {
            var clash = new ProfileQuery { Module = "app", Sort = ProfileQuery.Inclusive }.Conflict(false);

            Assert.Null(clash);
        }

        [Theory]
        [InlineData("function", "sort", true)]
        [InlineData("function", "thread", false)]
        [InlineData("thread", "sort", false)]
        public void Filters_compose_but_conflicting_views_are_rejected(string first, string second, bool conflicts)
        {
            var query = new ProfileQuery();
            foreach (var one in new[] { first, second })
            {
                if (one == "function") query.Function = "Work";
                if (one == "thread") query.Thread = 7;
                if (one == "sort") query.Sort = ProfileQuery.Inclusive;
            }

            Assert.Equal(conflicts, query.Conflict(false) != null);
        }

        [Fact]
        public void Comparing_two_captures_is_a_reading_of_its_own()
        {
            Assert.Null(new ProfileQuery().Conflict(true));
            Assert.NotNull(new ProfileQuery { Function = "Work" }.Conflict(true));
        }

        [Fact]
        public void A_tree_that_hit_its_row_limit_says_so_rather_than_looking_complete()
        {
            var wide = new Builder();
            for (var i = 0; i < 60; i++) wide.Stack("app!main;app!branch" + i, 100);
            var capture = wide.Done();

            capture.Tree(0.001, 40);
            Assert.True(capture.TreeWasCut);

            capture.Tree(0.001, 500);
            Assert.False(capture.TreeWasCut);
        }

        [Fact]
        public void A_thread_is_summed_on_its_own()
        {
            var capture = new Builder()
                .Stack("app!main;app!Work", 80, thread: 1)
                .Stack("app!main;app!Wait", 5, thread: 2)
                .Done();

            var threads = capture.ByThread();
            Assert.Equal(1, threads[0].Key);
            Assert.Equal(80, threads[0].Value);
            Assert.Equal("app!Wait", capture.BusiestIn(2).Key);
        }
    }
}
