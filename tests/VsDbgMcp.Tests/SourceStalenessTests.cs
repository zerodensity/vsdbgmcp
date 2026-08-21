using System;
using System.IO;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// A source file edited after the binary was built is the reason a line breakpoint
    /// refuses to bind while modules and symbols both look healthy. These cover the
    /// comparison, the order the causes are reported in, and what 'modules' prints.
    /// </summary>
    public class SourceStalenessTests
    {
        static readonly DateTime Built = new DateTime(2026, 8, 21, 13, 5, 0, DateTimeKind.Utc);

        [Fact]
        public void A_file_written_after_the_build_is_newer()
        {
            Assert.True(SourceFreshness.SourceIsNewer(Built.AddMinutes(87), Built));
        }

        [Fact]
        public void A_file_written_before_the_build_is_not()
        {
            Assert.False(SourceFreshness.SourceIsNewer(Built.AddMinutes(-1), Built));
        }

        [Fact]
        public void A_second_of_difference_is_not_an_edit()
        {
            Assert.False(SourceFreshness.SourceIsNewer(Built.AddSeconds(1), Built));
        }

        [Fact]
        public void A_time_that_could_not_be_read_stays_unknown()
        {
            Assert.Null(SourceFreshness.SourceIsNewer(Built.AddHours(1), null));
            Assert.Null(SourceFreshness.SourceIsNewer(null, Built));
            Assert.Null(SourceFreshness.SourceIsNewer(null, null));
        }

        [Fact]
        public void A_path_with_no_file_behind_it_has_no_time()
        {
            Assert.Null(SourceFreshness.LastWritten(null));
            Assert.Null(SourceFreshness.LastWritten(""));
            Assert.Null(SourceFreshness.LastWritten(@"D:\repo\Engine\out\nothing-is-here.dll"));
            Assert.Null(SourceFreshness.LastWritten("|not a path|"));
        }

        [Fact]
        public void A_file_that_is_there_reports_when_it_was_written()
        {
            var file = Path.Combine(Path.GetTempPath(), "vsdbgmcp-" + Guid.NewGuid().ToString("N") + ".cpp");
            File.WriteAllText(file, "");
            try
            {
                File.SetLastWriteTimeUtc(file, Built.AddHours(1));

                Assert.True(SourceFreshness.SourceIsNewer(SourceFreshness.LastWritten(file), Built));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void An_unreadable_binary_never_makes_a_source_look_stale()
        {
            var module = new ModuleInfo { Name = "engine.dll", SymbolsLoaded = true };

            var text = BindFailure.Explain(@"D:\repo\Engine\mesh.cpp", module,
                SourceFreshness.SourceIsNewer(Built.AddHours(1), SourceFreshness.LastWritten(@"D:\gone\engine.dll")),
                "2026-08-21 14:32");

            Assert.Equal(BindFailure.NoCodeHere, text);
        }

        [Fact]
        public void A_file_that_belongs_to_no_loaded_module_gets_the_general_answer()
        {
            Assert.Equal(BindFailure.NoCodeHere,
                BindFailure.Explain(@"D:\repo\Engine\mesh.cpp", null, true, "2026-08-21 14:32"));
        }

        [Fact]
        public void A_module_without_symbols_is_reported_before_the_source_times()
        {
            var module = new ModuleInfo
            {
                Name = "engine.dll",
                SymbolsLoaded = false,
                SymbolStatus = "cannot find or open the PDB file"
            };

            var text = BindFailure.Explain(@"D:\repo\Engine\mesh.cpp", module, true, "2026-08-21 14:32");

            Assert.Contains("engine.dll", text);
            Assert.Contains("without symbols", text);
            Assert.Contains("cannot find or open the PDB file", text);
            Assert.DoesNotContain("modified", text);
        }

        [Fact]
        public void An_edited_source_is_named_as_the_reason_with_both_times()
        {
            var module = new ModuleInfo { Name = "engine.dll", SymbolsLoaded = true, Built = "2026-08-21 13:05" };

            var text = BindFailure.Explain(@"D:\repo\Engine\mesh.cpp", module, true, "2026-08-21 14:32");

            Assert.Contains("mesh.cpp", text);
            Assert.Contains("engine.dll", text);
            Assert.Contains("2026-08-21 14:32", text);
            Assert.Contains("2026-08-21 13:05", text);
            Assert.Contains("line numbers no longer match", text);

            // The PDB's own checksums were never read, so nothing here may claim they
            // were compared.
            Assert.DoesNotContain("checksum", text);
        }

        [Fact]
        public void A_source_older_than_its_binary_is_not_the_reason()
        {
            var module = new ModuleInfo { Name = "engine.dll", SymbolsLoaded = true, Built = "2026-08-21 13:05" };

            Assert.Equal(BindFailure.NoCodeHere,
                BindFailure.Explain(@"D:\repo\Engine\mesh.cpp", module, false, "2026-08-20 09:00"));
        }

        [Fact]
        public void A_time_is_shown_where_the_reader_is_and_nothing_is_shown_for_none()
        {
            Assert.Equal("", SourceFreshness.Show(null));
            Assert.Equal(Built.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), SourceFreshness.Show(Built));
        }

        [Fact]
        public void Modules_show_when_each_binary_was_built_and_which_source_outran_it()
        {
            var text = Render.Modules(new ModulesResult
            {
                LoadedCount = 2,
                Modules =
                {
                    new ModuleInfo { Name = "engine.dll", SymbolsLoaded = true, Built = "2026-08-21 13:05", NewerSource = "mesh.cpp" },
                    new ModuleInfo { Name = "ucrtbase.dll", SymbolsLoaded = false, SymbolStatus = "no symbols loaded" }
                }
            });

            Assert.Contains("2 modules, 1 without symbols", text);
            Assert.Contains("built 2026-08-21 13:05", text);
            Assert.Contains("mesh.cpp was edited after this binary was built", text);
            Assert.Contains("no symbols loaded", text);
        }

        [Fact]
        public void A_filtered_list_says_what_it_was_picked_from()
        {
            var text = Render.Modules(new ModulesResult
            {
                LoadedCount = 483,
                Filter = "vulkan",
                Modules = { new ModuleInfo { Name = "vulkan-1.dll", SymbolsLoaded = true } }
            });

            Assert.Contains("1 of 483 loaded modules match 'vulkan'", text);
            Assert.Contains("more can load", text);
        }

        [Fact]
        public void A_filter_that_matches_nothing_does_not_read_as_nothing_being_loaded()
        {
            var text = Render.Modules(new ModulesResult { LoadedCount = 483, Filter = "vulkan" });

            Assert.Contains("No module matches 'vulkan'", text);
            Assert.Contains("483 modules are loaded", text);
            Assert.Contains("more can load", text);
        }

        [Fact]
        public void Nothing_loaded_still_says_nothing_is_loaded()
        {
            Assert.Equal("No modules loaded.", Render.Modules(new ModulesResult()));
            Assert.Equal("No modules loaded.", Render.Modules(null));
        }
    }
}
