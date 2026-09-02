using System.Collections.Generic;
using VsDbgMcp;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// The lines around where the program stopped.
    ///
    /// Showing source is showing something the debugger did not say, so the one thing
    /// that matters here is not being wrong about which lines those are. A file edited
    /// since the binary was built still opens and still has a line 38, and printing it
    /// beside a frame is how an agent comes to reason about code the process is not
    /// running.
    /// </summary>
    public class SourceWindowTests
    {
        static readonly List<string> Lines = new List<string>
        {
            "int Upload(Mesh& mesh, int scale) {",   // 1
            "    int total = 0;",                     // 2
            "    for (size_t i = 0; i < n; ++i) {",   // 3
            "        total += mesh.v[i] * scale;",    // 4
            "    }",                                  // 5
            "    mesh.refCount += 1;",                // 6
            "    return total;",                      // 7
            "}"                                       // 8
        };

        [Fact]
        public void The_window_is_centred_on_the_line_that_is_running()
        {
            var window = SourceWindow.Around(Lines, 4, 2);

            Assert.Equal(new[] { 2, 3, 4, 5, 6 }, Numbers(window));
        }

        [Fact]
        public void The_line_that_is_running_is_the_only_one_marked()
        {
            var window = SourceWindow.Around(Lines, 4, 2);

            Assert.Single(window, l => l.IsCurrent);
            Assert.Equal(4, First(window, l => l.IsCurrent).Number);
        }

        /// <summary>
        /// A frame at the top of a file has no lines above it, and padding the window
        /// with blanks would invent them.
        /// </summary>
        [Fact]
        public void A_window_at_the_start_of_a_file_stops_at_the_first_line()
        {
            var window = SourceWindow.Around(Lines, 1, 3);

            Assert.Equal(new[] { 1, 2, 3, 4 }, Numbers(window));
        }

        [Fact]
        public void A_window_at_the_end_of_a_file_stops_at_the_last_line()
        {
            var window = SourceWindow.Around(Lines, 8, 3);

            Assert.Equal(new[] { 5, 6, 7, 8 }, Numbers(window));
        }

        /// <summary>
        /// A line number the file does not have is a frame whose source has moved, which
        /// is the case worth refusing rather than showing whatever sits at that offset.
        /// </summary>
        [Fact]
        public void A_line_past_the_end_of_the_file_produces_no_window()
        {
            Assert.Empty(SourceWindow.Around(Lines, 40, 2));
        }

        [Fact]
        public void Line_zero_produces_no_window()
        {
            Assert.Empty(SourceWindow.Around(Lines, 0, 2));
        }

        [Fact]
        public void No_lines_at_all_produces_no_window()
        {
            Assert.Empty(SourceWindow.Around(new List<string>(), 1, 2));
            Assert.Empty(SourceWindow.Around(null, 1, 2));
        }

        /// <summary>
        /// The whole reason this is allowed to print source at all. Two file times are
        /// what iteration 1 settled on, and the warning has to name what it compared so
        /// nobody reads it as a checksum.
        /// </summary>
        [Fact]
        public void Source_newer_than_the_binary_says_the_lines_may_not_be_what_is_running()
        {
            var note = SourceWindow.Warning(true, "2026-09-02 10:14", "2026-09-01 18:00");

            Assert.Contains("2026-09-02 10:14", note);
            Assert.Contains("2026-09-01 18:00", note);
            Assert.Contains("not the lines", note);
        }

        [Fact]
        public void Source_older_than_the_binary_says_nothing()
        {
            Assert.Null(SourceWindow.Warning(false, "2026-09-01 10:14", "2026-09-01 18:00"));
        }

        /// <summary>
        /// Unknown is not the same as fine. Where either time could not be read, the
        /// window is still worth showing and the reader is still owed the gap.
        /// </summary>
        [Fact]
        public void An_unknown_comparison_says_it_could_not_be_checked()
        {
            var note = SourceWindow.Warning(null, "", "");

            Assert.NotNull(note);
            Assert.Contains("could not", note);
        }

        static int[] Numbers(IReadOnlyList<SourceLine> window)
        {
            var numbers = new int[window.Count];
            for (var i = 0; i < window.Count; i++) numbers[i] = window[i].Number;
            return numbers;
        }

        static SourceLine First(IReadOnlyList<SourceLine> window, System.Func<SourceLine, bool> match)
        {
            foreach (var line in window)
            {
                if (match(line)) return line;
            }
            return null;
        }
    }
}
