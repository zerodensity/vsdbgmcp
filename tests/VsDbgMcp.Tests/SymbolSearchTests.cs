using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// The engine keeps every symbol search it has made for a module and hands back all
    /// of them at once. Driving a real debugger showed the same four lines coming back
    /// four times over after four attempts, which reads as four different searches.
    /// </summary>
    public class SymbolSearchTests
    {
        const string OneSearch =
            "D:\\build\\ntdll.pdb: Cannot find or open the PDB file.\n" +
            "C:\\Windows\\System32\\ntdll.pdb: Cannot find or open the PDB file.";

        [Fact]
        public void One_search_comes_back_as_it_arrived()
        {
            int searches;
            var text = SymbolSearch.Tidy(OneSearch, out searches);

            Assert.Equal(1, searches);
            Assert.Equal(OneSearch, text);
        }

        [Fact]
        public void The_same_search_repeated_is_reported_once_and_counted()
        {
            int searches;
            var text = SymbolSearch.Tidy(
                OneSearch + "\n\n" + OneSearch + "\n\n" + OneSearch, out searches);

            Assert.Equal(3, searches);
            Assert.StartsWith(OneSearch, text);
            Assert.Contains("searched 3 times", text);

            // Once, not three times: the paths are what a reader is here for.
            Assert.Equal(2, Occurrences(text, "Cannot find or open the PDB file."));
        }

        /// <summary>
        /// Two searches that looked in different places are two facts, and dropping
        /// either would hide where the engine went after the first one failed.
        /// </summary>
        [Fact]
        public void Searches_that_differ_are_all_kept()
        {
            const string second = "C:\\symbols\\ntdll.pdb: PDB does not match image.";

            int searches;
            var text = SymbolSearch.Tidy(OneSearch + "\n\n" + second, out searches);

            Assert.Equal(2, searches);
            Assert.Contains("Cannot find or open the PDB file.", text);
            Assert.Contains("PDB does not match image.", text);
            Assert.DoesNotContain("searched", text);
        }

        [Fact]
        public void Nothing_to_say_comes_back_as_nothing()
        {
            Assert.Null(SymbolSearch.Tidy(null));
            Assert.Null(SymbolSearch.Tidy("   \r\n  \r\n "));
        }

        static int Occurrences(string text, string part)
        {
            var count = 0;
            for (var at = text.IndexOf(part); at >= 0; at = text.IndexOf(part, at + 1)) count++;
            return count;
        }
    }
}
