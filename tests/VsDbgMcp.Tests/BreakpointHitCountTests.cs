using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// bp_list promised a hit count and did not report one, so answering "has this line
    /// run once or ten thousand times" took a collecting tracepoint and a second
    /// reproduction. The count is the whole difference between a failure on the first
    /// frame and one that arrives after running for a while.
    /// </summary>
    public class BreakpointHitCountTests
    {
        static BreakpointInfo At(int line, bool bound, int hits) => new BreakpointInfo
        {
            Id = 16,
            Kind = BreakpointKind.Location,
            File = @"D:\repo\Engine\DMAWriteNode.cpp",
            Line = line,
            Enabled = true,
            Bound = bound,
            HitCount = hits
        };

        [Fact]
        public void A_bound_breakpoint_reports_how_often_it_was_hit()
        {
            var text = Render.Breakpoints(new[] { At(216, true, 4823) });

            Assert.Contains("hits 4823", text);
        }

        /// <summary>
        /// Never reached is an answer, and the one that says the code path is not the
        /// one running. Leaving it out reads as nothing having been counted.
        /// </summary>
        [Fact]
        public void A_bound_breakpoint_that_was_never_hit_says_zero_rather_than_nothing()
        {
            var text = Render.Breakpoints(new[] { At(216, true, 0) });

            Assert.Contains("hits 0", text);
        }

        [Fact]
        public void An_unbound_breakpoint_reports_no_count_at_all()
        {
            var text = Render.Breakpoints(new[]
            {
                new BreakpointInfo
                {
                    Id = 17,
                    Kind = BreakpointKind.Location,
                    File = @"D:\repo\Engine\DMAWriteNode.cpp",
                    Line = 216,
                    Enabled = true,
                    Bound = false,
                    BindState = "module not loaded"
                }
            });

            Assert.Contains("UNBOUND", text);
            Assert.DoesNotContain("hits", text);
        }
    }
}
