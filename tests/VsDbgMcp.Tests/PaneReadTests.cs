using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// A long pane is read only at its end. A search that finds nothing there must say
    /// so, rather than read as the program never having printed the line.
    /// </summary>
    public class PaneReadTests
    {
        [Fact]
        public void A_search_of_the_end_of_a_long_pane_says_where_it_started()
        {
            var reply = Render.Pane(new OutputResult { Text = "", SearchedFromLine = 180001 }, "Debug");

            Assert.StartsWith("(read from line 180001 to the end of the pane; earlier lines were not read)", reply);
            Assert.Contains("has no matching output", reply);
        }

        [Fact]
        public void A_pane_read_whole_says_nothing_about_where_it_started()
        {
            var reply = Render.Pane(new OutputResult { Text = "one\ntwo", Lines = 2 }, "Debug");

            Assert.Equal("one\ntwo", reply);
        }

        [Fact]
        public void Lines_cut_to_the_tail_are_counted()
        {
            var reply = Render.Pane(new OutputResult { Text = "two", Lines = 1, Truncated = true }, "Debug");

            Assert.Equal("(showing the last 1 matching lines)\ntwo", reply);
        }
    }
}
