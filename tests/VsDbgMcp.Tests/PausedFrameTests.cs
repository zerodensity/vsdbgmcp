using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// After a pause the innermost frame is Visual Studio's own row, which nothing can be
    /// read in. Asking the engine whether a frame has an expression context needs a live
    /// engine; these cover the two halves that do not - which frame gets picked given
    /// which ones can be read, and what the caller is told about it.
    /// </summary>
    public class PausedFrameTests
    {
        /// <summary>The stack out of the report: four rows of pseudo-frame and system code.</summary>
        static bool AfterAPause(int index) => index >= 4;

        [Fact]
        public void A_read_nobody_pinned_lands_on_the_first_frame_it_can_use()
        {
            Assert.Equal(4, FrameChoice.Nearest(0, 8, AfterAPause));
        }

        [Fact]
        public void The_frame_already_on_is_kept_when_it_works()
        {
            Assert.Equal(5, FrameChoice.Nearest(5, 8, AfterAPause));
        }

        [Fact]
        public void Equal_distances_go_to_the_inner_frame()
        {
            Assert.Equal(3, FrameChoice.Nearest(4, 8, i => i == 3 || i == 5));
        }

        [Fact]
        public void A_stack_with_nothing_readable_picks_nothing()
        {
            Assert.Null(FrameChoice.Nearest(0, 8, i => false));
        }

        [Fact]
        public void A_pinned_frame_that_cannot_be_read_names_one_that_can()
        {
            var text = FrameChoice.CannotEvaluate(4, "host.exe!main", 8);

            Assert.Contains("this frame has no expression context", text);
            Assert.Contains("Frame 4 (host.exe!main)", text);
            Assert.Contains("select(frame: 4)", text);
        }

        [Fact]
        public void A_stack_with_nothing_readable_says_how_far_it_looked()
        {
            var text = FrameChoice.CannotEvaluate(null, null, 16);

            Assert.Contains("neither does any of the first 16 frames", text);
            Assert.DoesNotContain("select(frame:", text);
        }

        [Fact]
        public void Moving_off_a_frame_says_which_one_was_read_instead()
        {
            var text = FrameChoice.Moved(0, 4, "host.exe!main");

            Assert.Contains("frame 0 has no expression context", text);
            Assert.Contains("read in frame 4 (host.exe!main)", text);
        }

        [Fact]
        public void A_frame_past_the_end_of_the_stack_says_how_many_there_are()
        {
            Assert.Contains("there is no frame 9 on this thread; it has 5 frames",
                FrameChoice.NoSuchFrame(9, 5));
        }

        // ------------------------------------------------------------------ rendering

        [Fact]
        public void An_eval_in_a_frame_it_moved_to_says_so_above_the_value()
        {
            var text = Render.Evals(new[]
            {
                new EvalResult
                {
                    Expression = "count",
                    Value = "3",
                    IsValid = true,
                    Frame = new Frame { Index = 4, Function = "host.exe!main", File = @"D:\repo\main.cpp", Line = 31 },
                    FrameNote = FrameChoice.Moved(0, 4, "host.exe!main")
                }
            });

            Assert.Contains("read in frame 4", text);
            Assert.Contains("#4   host.exe!main  main.cpp:31", text);
            Assert.Contains("count = 3", text);
        }

        [Fact]
        public void An_eval_in_the_frame_it_was_asked_for_adds_no_line()
        {
            var text = Render.Evals(new[]
            {
                new EvalResult { Expression = "count", Value = "3", IsValid = true }
            });

            Assert.Equal("count = 3", text);
        }

        [Fact]
        public void Vars_with_nothing_to_show_says_why_rather_than_looking_empty()
        {
            var text = Render.Vars(new VarsResult
            {
                Message = FrameChoice.CannotEvaluate(4, "host.exe!main", 8)
            });

            Assert.Contains("no expression context", text);
            Assert.DoesNotContain("(nothing in scope)", text);
        }

        [Fact]
        public void A_frame_that_genuinely_holds_nothing_still_says_nothing_is_in_scope()
        {
            Assert.Equal("  (nothing in scope)", Render.Vars(new VarsResult()));
        }

        [Fact]
        public void Vars_names_the_frame_it_moved_to()
        {
            var text = Render.Vars(new VarsResult
            {
                Nodes = new List<VarNode> { new VarNode { Name = "argc", Value = "1", Type = "int" } },
                Frame = new Frame { Index = 4, Function = "host.exe!main" },
                FrameNote = FrameChoice.Moved(0, 4, "host.exe!main")
            });

            Assert.Contains("read in frame 4", text);
            Assert.Contains("#4   host.exe!main", text);
            Assert.Contains("argc = 1", text);
        }

        [Fact]
        public void Status_carries_the_frame_reads_will_land_in()
        {
            var text = Render.Status(new HostStatus
            {
                InstanceId = "Engine#1",
                Mode = DebugModes.Break,
                CurrentThreadId = 4242,
                CurrentFrameIndex = 4,
                FrameNote = FrameChoice.Moved(0, 4, "host.exe!main"),
                TopFrames = new List<Frame>
                {
                    new Frame { Index = 0, Function = "[Application execution paused]" },
                    new Frame { Index = 4, Function = "host.exe!main" }
                }
            });

            Assert.Contains("> #4   host.exe!main", text);
            Assert.Contains("read in frame 4", text);
        }
    }
}
