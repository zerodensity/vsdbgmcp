using System.IO;
using System.Linq;
using Xunit;

namespace VsDbgMcp.Tests
{
    public class PathUtilTests
    {
        [Theory]
        [InlineData(@"D:\repo\Engine", @"D:\repo\Engine", true)]
        [InlineData(@"D:\repo\Engine", @"D:\repo\Engine\src\mesh.cpp", true)]
        [InlineData(@"D:\repo\Engine", @"D:\repo\Engine\", true)]
        [InlineData(@"d:\REPO\engine", @"D:\repo\Engine\src", true)]
        [InlineData(@"D:\repo\Engine", @"D:\repo\EngineTools\x.cpp", false)]
        [InlineData(@"D:\repo\Engine", @"D:\repo", false)]
        [InlineData(@"D:\repo\Engine", @"D:\other\Engine", false)]
        public void Contains_compares_whole_segments(string ancestor, string candidate, bool expected)
        {
            Assert.Equal(expected, PathUtil.Contains(ancestor, candidate));
        }

        [Fact]
        public void Specificity_prefers_the_nearest_enclosing_directory()
        {
            const string file = @"D:\repo\Engine\src\mesh.cpp";

            var outer = PathUtil.Specificity(@"D:\repo", file);
            var inner = PathUtil.Specificity(@"D:\repo\Engine", file);

            Assert.True(inner > outer);
            Assert.Equal(0, PathUtil.Specificity(@"D:\elsewhere", file));
        }

        [Fact]
        public void SolutionFilesIn_finds_every_solution_form()
        {
            using (var dir = new TempDir())
            {
                dir.Touch("App.sln");
                dir.Touch("App.slnx");
                dir.Touch("App.slnf");
                dir.Touch("readme.md");
                dir.Touch("App.slnLk");

                var found = PathUtil.SolutionFilesIn(dir.Path).Select(Path.GetFileName).ToList();

                Assert.Contains("App.sln", found);
                Assert.Contains("App.slnx", found);
                Assert.Contains("App.slnf", found);
                Assert.DoesNotContain("readme.md", found);

                // The three-character extension quirk would drag this in on a volume with
                // 8.3 names enabled. Matching the extension exactly keeps it out everywhere.
                Assert.DoesNotContain("App.slnLk", found);
            }
        }

        [Fact]
        public void Sln_and_slnx_side_by_side_are_one_solution()
        {
            using (var dir = new TempDir())
            {
                var sln = dir.Touch("App.sln");
                var slnx = dir.Touch("App.slnx");

                var collapsed = PathUtil.CollapseSolutionVariants(new[] { sln, slnx });

                Assert.Single(collapsed);
                Assert.Equal(".slnx", Path.GetExtension(collapsed[0]));
            }
        }

        [Fact]
        public void A_filter_is_not_collapsed_into_its_solution()
        {
            using (var dir = new TempDir())
            {
                var sln = dir.Touch("App.sln");
                var filter = dir.Touch("App.slnf");

                var collapsed = PathUtil.CollapseSolutionVariants(new[] { sln, filter });

                Assert.Equal(2, collapsed.Count);
            }
        }

        [Theory]
        [InlineData(@"C:\x\App.sln", WorkspaceKind.Sln)]
        [InlineData(@"C:\x\App.slnx", WorkspaceKind.Slnx)]
        [InlineData(@"C:\x\App.slnf", WorkspaceKind.Slnf)]
        [InlineData(null, WorkspaceKind.None)]
        public void KindOf_reads_the_extension(string file, string expected)
        {
            Assert.Equal(expected, PathUtil.KindOf(file));
        }

        sealed class TempDir : System.IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vsdbgmcp-" + System.Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public string Touch(string name)
            {
                var full = System.IO.Path.Combine(Path, name);
                File.WriteAllText(full, "");
                return full;
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, true); } catch { }
            }
        }
    }
}
