using System;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Which build of a binary the debuggee is running.
    ///
    /// The failure these cover: a plugin was deployed to another machine and rebuilt
    /// afterwards, so the running image was older than the local PDB. Nothing in the
    /// module list could say so, because the only time it carried belonged to a file
    /// on this machine that the debuggee had never loaded.
    /// </summary>
    public class ModuleIdentityTests
    {
        static readonly DateTime Stamped = new DateTime(2026, 8, 21, 12, 11, 0, DateTimeKind.Utc);

        [Fact]
        public void An_image_time_is_shown_where_the_reader_is()
        {
            Assert.Equal(Stamped.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), ModuleIdentity.ImageTime(Stamped));
        }

        [Fact]
        public void A_header_with_no_stamp_reports_no_time_rather_than_the_epoch()
        {
            Assert.Equal("", ModuleIdentity.ImageTime(null));
            Assert.Equal("", ModuleIdentity.ImageTime(new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void A_stamp_that_could_not_be_a_build_time_is_not_shown_as_one()
        {
            Assert.Equal("", ModuleIdentity.ImageTime(DateTime.UtcNow.AddYears(30)));
        }

        [Fact]
        public void A_size_reads_both_ways()
        {
            Assert.Equal("1.3 MB (0x145000)", ModuleIdentity.Size(0x145000));
            Assert.Equal("212.0 KB (0x35000)", ModuleIdentity.Size(0x35000));
            Assert.Equal("", ModuleIdentity.Size(0));
        }

        [Fact]
        public void A_module_names_the_binary_it_is_and_keeps_the_two_times_apart()
        {
            var text = Render.Modules(new ModulesResult
            {
                LoadedCount = 483,
                Filter = "plugin",
                Modules =
                {
                    new ModuleInfo
                    {
                        Name = "plugin.dll",
                        Path = @"C:\deploy\plugin.dll",
                        SymbolsLoaded = true,
                        ImageBuilt = "2026-08-21 12:11",
                        Built = "2026-08-21 13:05",
                        Size = "212.0 KB (0x35000)",
                        Address = "0x7ffb00120000",
                        SymbolPath = @"C:\deploy\plugin.pdb"
                    }
                }
            });

            Assert.Contains(@"C:\deploy\plugin.dll", text);
            Assert.Contains("212.0 KB (0x35000)", text);
            Assert.Contains("at 0x7ffb00120000", text);
            Assert.Contains(@"symbols C:\deploy\plugin.pdb", text);

            Assert.Contains("image 2026-08-21 12:11", text);
            Assert.Contains("on this machine was written 2026-08-21 13:05", text);
        }

        [Fact]
        public void A_module_with_no_image_time_says_the_time_it_does_have_belongs_to_a_file()
        {
            var text = Render.Modules(new ModulesResult
            {
                LoadedCount = 1,
                Modules = { new ModuleInfo { Name = "engine.dll", SymbolsLoaded = true, Built = "2026-08-21 13:05" } }
            });

            Assert.Contains("file 2026-08-21 13:05", text);
            Assert.DoesNotContain("image", text);
        }

        [Fact]
        public void A_long_list_stays_one_line_per_module_and_says_how_to_get_the_rest()
        {
            var result = new ModulesResult { LoadedCount = 60 };
            for (var i = 0; i < 60; i++)
            {
                result.Modules.Add(new ModuleInfo
                {
                    Name = "mod" + i + ".dll",
                    Path = @"C:\Windows\System32\mod" + i + ".dll",
                    SymbolsLoaded = true,
                    Size = "1.0 MB (0x100000)"
                });
            }

            var text = Render.Modules(result);

            Assert.Contains("Filter to see each one's path, size and load address", text);
            Assert.DoesNotContain(@"C:\Windows\System32\mod0.dll", text);
            Assert.Equal(61, text.Split('\n').Length);
        }
    }
}
