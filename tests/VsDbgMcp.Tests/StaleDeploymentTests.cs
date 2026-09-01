using System;
using System.Collections.Generic;
using System.IO;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// The debuggee running an older copy of a module than the build sitting beside
    /// its PDB.
    ///
    /// The failure these cover cost a session several hours: an engine loads its
    /// plugins from a deployment directory, a rebuild only writes the build tree, and
    /// the debugger matches the new PDB against the old image. The module lists as
    /// having symbols, the PDB path resolves, and no breakpoint binds.
    /// </summary>
    public class StaleDeploymentTests : IDisposable
    {
        static readonly DateTime Deployed = new DateTime(2026, 8, 14, 9, 41, 0, DateTimeKind.Utc);
        static readonly DateTime Rebuilt = new DateTime(2026, 8, 21, 13, 5, 0, DateTimeKind.Utc);

        readonly string _root;

        public StaleDeploymentTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "vsdbgmcp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Cache"));
            Directory.CreateDirectory(Path.Combine(_root, "Binaries"));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        string Put(string folder, string name, DateTime writtenUtc)
        {
            var path = Path.Combine(_root, folder, name);
            File.WriteAllText(path, "");
            File.SetLastWriteTimeUtc(path, writtenUtc);
            return path;
        }

        string In(string folder, string name) => Path.Combine(_root, folder, name);

        /// <summary>The check as the extension runs it, with the loaded file's own time read here.</summary>
        static string Check(ModuleInfo module) =>
            ModuleIdentity.StaleDeployment(module, SourceFreshness.LastWritten(module.Path));

        [Fact]
        public void A_loaded_copy_older_than_the_build_beside_its_pdb_names_both()
        {
            var loaded = Put("Cache", "plugin.dll", Deployed);
            var built = Put("Binaries", "plugin.dll", Rebuilt);

            var text = Check(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = loaded,
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb")
            });

            Assert.Contains("plugin.dll may be running an older build", text);
            Assert.Contains(loaded, text);
            Assert.Contains(built, text);
            Assert.Contains(SourceFreshness.Show(Deployed), text);
            Assert.Contains(SourceFreshness.Show(Rebuilt), text);
            Assert.Contains("Redeploy and relaunch before trusting them", text);
        }

        [Fact]
        public void Symbols_sitting_beside_the_loaded_image_say_nothing()
        {
            var loaded = Put("Binaries", "plugin.dll", Deployed);

            Assert.Null(Check(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = loaded,
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb")
            }));
        }

        [Fact]
        public void A_deployment_of_the_same_build_says_nothing()
        {
            // A copy keeps the time of the file it came from, so a deployment made in
            // the same step as the build carries the build's own time.
            var loaded = Put("Cache", "plugin.dll", Rebuilt);
            Put("Binaries", "plugin.dll", Rebuilt);

            Assert.Null(Check(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = loaded,
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb")
            }));
        }

        [Fact]
        public void A_loaded_copy_newer_than_the_build_beside_it_says_nothing()
        {
            var loaded = Put("Cache", "plugin.dll", Rebuilt);
            Put("Binaries", "plugin.dll", Deployed);

            Assert.Null(Check(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = loaded,
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb")
            }));
        }

        [Fact]
        public void Nothing_of_that_name_beside_the_pdb_says_nothing()
        {
            var loaded = Put("Cache", "plugin.dll", Deployed);

            Assert.Null(Check(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = loaded,
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb")
            }));
        }

        [Fact]
        public void A_pdb_in_the_binary_s_own_directory_says_nothing_whatever_it_is_named()
        {
            // One output directory holds every project's binaries. A PDB named for one
            // of them sitting beside this binary would make its neighbour look like a
            // newer build of it, and nothing has been deployed anywhere at all.
            var loaded = Put("Binaries", "Foo.dll", Deployed);
            Put("Binaries", "Bar.dll", Rebuilt);

            Assert.Empty(ModuleIdentity.BesideSymbols(loaded, In("Binaries", "Bar.pdb")));

            Assert.Null(Check(new ModuleInfo
            {
                Name = "Foo.dll",
                Path = loaded,
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "Bar.pdb")
            }));
        }

        [Fact]
        public void A_module_without_symbols_is_not_told_it_is_stale()
        {
            // Without symbols there is no PDB that matched, so the directory one sits
            // in is not known to be this module's build tree.
            var loaded = Put("Cache", "plugin.dll", Deployed);
            Put("Binaries", "plugin.dll", Rebuilt);

            Assert.Null(Check(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = loaded,
                SymbolsLoaded = false,
                SymbolStatus = "cannot find or open the PDB file",
                SymbolPath = In("Binaries", "plugin.pdb")
            }));
        }

        [Fact]
        public void Times_that_differ_by_less_than_the_slack_are_one_build()
        {
            var loaded = Put("Cache", "plugin.dll", Rebuilt);
            Put("Binaries", "plugin.dll", Rebuilt.AddSeconds(2));

            Assert.Null(Check(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = loaded,
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb")
            }));
        }

        [Fact]
        public void A_module_whose_symbols_were_never_found_says_nothing()
        {
            var loaded = Put("Cache", "plugin.dll", Deployed);
            Put("Binaries", "plugin.dll", Rebuilt);

            Assert.Null(Check(new ModuleInfo { Name = "plugin.dll", Path = loaded }));
        }

        [Fact]
        public void A_copy_this_machine_cannot_read_is_told_from_the_image_stamp()
        {
            var built = Put("Binaries", "plugin.dll", Rebuilt);

            // The loaded path belongs to the machine the debuggee runs on, so there is
            // no file here to take a time from.
            var text = ModuleIdentity.StaleDeployment(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = @"C:\remote\deploy\plugin.dll",
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb"),
                ImageStamp = Deployed
            }, null);

            Assert.Contains("plugin.dll may be running an older build", text);
            Assert.Contains(SourceFreshness.Show(Deployed), text);
            Assert.Contains(built, text);
            Assert.Contains("evidence rather than proof", text);

            // Nothing here read the loaded file, so nothing may say it was written.
            Assert.DoesNotContain("was written " + SourceFreshness.Show(Deployed), text);
        }

        [Fact]
        public void A_gap_one_build_could_account_for_is_not_reported()
        {
            Put("Binaries", "plugin.dll", Rebuilt);

            // A link stamps the image when it starts and the file is written when it
            // ends, so minutes between the two are one build rather than two.
            Assert.Null(ModuleIdentity.StaleDeployment(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = @"C:\remote\deploy\plugin.dll",
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb"),
                ImageStamp = Rebuilt.AddMinutes(-10)
            }, null));
        }

        [Fact]
        public void A_stamp_exactly_one_link_gap_older_is_still_one_build()
        {
            Put("Binaries", "plugin.dll", Rebuilt);

            Assert.Null(ModuleIdentity.StaleDeployment(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = @"C:\remote\deploy\plugin.dll",
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb"),
                ImageStamp = Rebuilt.AddHours(-1)
            }, null));
        }

        [Fact]
        public void A_stamp_that_could_not_be_a_time_is_never_compared_against_one()
        {
            Put("Binaries", "plugin.dll", Rebuilt);

            // A build made reproducible stamps a hash of the contents where the time
            // goes, and it lands wherever the hash falls.
            foreach (var stamp in new[] { new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.UtcNow.AddYears(40) })
            {
                Assert.Null(ModuleIdentity.StaleDeployment(new ModuleInfo
                {
                    Name = "plugin.dll",
                    Path = @"C:\remote\deploy\plugin.dll",
                    SymbolsLoaded = true,
                    SymbolPath = In("Binaries", "plugin.pdb"),
                    ImageStamp = stamp
                }, null));
            }
        }

        [Fact]
        public void A_module_with_no_time_at_all_says_nothing()
        {
            Put("Binaries", "plugin.dll", Rebuilt);

            Assert.Null(ModuleIdentity.StaleDeployment(new ModuleInfo
            {
                Name = "plugin.dll",
                Path = @"C:\remote\deploy\plugin.dll",
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "plugin.pdb")
            }, null));
        }

        [Fact]
        public void The_file_to_compare_against_is_the_one_beside_the_symbols()
        {
            Assert.Equal(new[] { @"D:\repo\Binaries\plugin.dll" },
                ModuleIdentity.BesideSymbols(@"D:\game\Cache\Modules\plugin.dll", @"D:\repo\Binaries\plugin.pdb"));

            Assert.Empty(ModuleIdentity.BesideSymbols(@"D:\repo\Binaries\plugin.dll", @"D:\repo\Binaries\plugin.pdb"));
            Assert.Empty(ModuleIdentity.BesideSymbols(@"D:\game\Cache\plugin.dll", null));
            Assert.Empty(ModuleIdentity.BesideSymbols(null, @"D:\repo\Binaries\plugin.pdb"));
            Assert.Empty(ModuleIdentity.BesideSymbols("|not a path|", "|not a path either|"));
        }

        [Fact]
        public void A_pdb_named_differently_from_the_binary_is_also_looked_for()
        {
            // The build that produced nos.sys.vulkan.dll named its symbols
            // nosSysVulkan.pdb, so the file to compare against carries the PDB's name.
            Assert.Equal(
                new[] { @"D:\repo\Binaries\nos.sys.vulkan.dll", @"D:\repo\Binaries\nosSysVulkan.dll" },
                ModuleIdentity.BesideSymbols(@"D:\game\Cache\Modules\nos.sys.vulkan.dll",
                    @"D:\repo\Binaries\nosSysVulkan.pdb"));
        }

        [Fact]
        public void The_build_named_after_the_pdb_is_compared_when_nothing_carries_the_image_name()
        {
            var loaded = Put("Cache", "nos.sys.vulkan.dll", Deployed);
            var built = Put("Binaries", "nosSysVulkan.dll", Rebuilt);

            var text = Check(new ModuleInfo
            {
                Name = "nos.sys.vulkan.dll",
                Path = loaded,
                SymbolsLoaded = true,
                SymbolPath = In("Binaries", "nosSysVulkan.pdb")
            });

            Assert.Contains("nos.sys.vulkan.dll may be running an older build", text);
            Assert.Contains(built, text);
        }

        [Fact]
        public void A_build_time_comes_from_the_image_before_the_file()
        {
            var loaded = Put("Cache", "plugin.dll", Rebuilt);
            var module = new ModuleInfo { Name = "plugin.dll", Path = loaded, ImageStamp = Deployed };

            Assert.Equal(Deployed, ModuleIdentity.BinaryBuilt(module));

            // A stamp that could not be a time falls back to the file rather than
            // taking the whole comparison down with it.
            module.ImageStamp = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(Rebuilt, ModuleIdentity.BinaryBuilt(module));

            Assert.Null(ModuleIdentity.BinaryBuilt(new ModuleInfo { Path = @"D:\gone\plugin.dll" }));
            Assert.Null(ModuleIdentity.BinaryBuilt(null));
        }

        [Fact]
        public void A_stale_deployment_is_the_reason_before_the_source_times_are()
        {
            var module = new ModuleInfo
            {
                Name = "plugin.dll",
                SymbolsLoaded = true,
                Built = "2026-08-21 13:05",
                StaleDeployment = "plugin.dll is running an older copy than the build beside its PDB"
            };

            var text = BindFailure.Explain(@"D:\repo\plugin\mesh.cpp", module, true, "2026-08-21 14:32");

            Assert.Equal(module.StaleDeployment, text);

            // Rebuilding is what the source-time answer advises, and rebuilding is
            // exactly what does not fix this.
            Assert.DoesNotContain("Rebuild the module", text);
        }

        [Fact]
        public void A_module_without_symbols_is_still_the_first_answer()
        {
            var module = new ModuleInfo
            {
                Name = "plugin.dll",
                SymbolsLoaded = false,
                SymbolStatus = "cannot find or open the PDB file",
                StaleDeployment = "plugin.dll is running an older copy than the build beside its PDB"
            };

            var text = BindFailure.Explain(@"D:\repo\plugin\mesh.cpp", module, true, "2026-08-21 14:32");

            Assert.Contains("without symbols", text);
            Assert.DoesNotContain("older copy", text);
        }

        [Fact]
        public void An_edited_source_is_dated_from_the_image_rather_than_the_local_file()
        {
            var module = new ModuleInfo
            {
                Name = "plugin.dll",
                SymbolsLoaded = true,
                Built = "2026-08-21 13:05",
                ImageBuilt = "2026-08-14 09:41"
            };

            var text = BindFailure.Explain(@"D:\repo\plugin\mesh.cpp", module, true, "2026-08-21 14:32");

            Assert.Contains("binary 2026-08-14 09:41", text);
            Assert.DoesNotContain("2026-08-21 13:05", text);
        }

        [Fact]
        public void Nothing_stale_in_the_session_is_said_about_nothing()
        {
            Assert.Null(BindFailure.StaleModules(null));
            Assert.Null(BindFailure.StaleModules(new List<ModuleInfo>()));
            Assert.Null(BindFailure.StaleModules(new List<ModuleInfo> { new ModuleInfo { Name = "engine.dll" } }));
        }

        [Fact]
        public void One_stale_module_is_named_without_claiming_it_owns_the_file()
        {
            var text = BindFailure.StaleModules(new List<ModuleInfo>
            {
                new ModuleInfo { Name = "engine.dll" },
                new ModuleInfo { Name = "plugin.dll", StaleDeployment = "..." }
            });

            Assert.Contains("One loaded module is running an older copy", text);
            Assert.Contains("plugin.dll", text);
            Assert.DoesNotContain("engine.dll", text);
            Assert.Contains("Which module this breakpoint belongs to is not known here", text);
            Assert.Contains("something to rule out rather than the reason", text);
        }

        [Fact]
        public void Several_stale_modules_are_counted_and_the_list_is_capped()
        {
            var loaded = new List<ModuleInfo>();
            for (var i = 0; i < 5; i++)
                loaded.Add(new ModuleInfo { Name = "plugin" + i + ".dll", StaleDeployment = "..." });

            var text = BindFailure.StaleModules(loaded);

            Assert.Contains("5 loaded modules are running an older copy", text);
            Assert.Contains("plugin0.dll, plugin1.dll, plugin2.dll and 2 more", text);
            Assert.DoesNotContain("plugin3.dll", text);
        }

        [Fact]
        public void Modules_carry_the_stale_line_whether_or_not_the_list_is_detailed()
        {
            var note = "plugin.dll is running an older copy than the build beside its PDB";

            var listed = Render.Modules(new ModulesResult
            {
                LoadedCount = 2,
                Modules =
                {
                    new ModuleInfo { Name = "plugin.dll", SymbolsLoaded = true, ImageBuilt = "2026-08-14 09:41", StaleDeployment = note },
                    new ModuleInfo { Name = "engine.dll", SymbolsLoaded = true }
                }
            });

            Assert.Contains(note, listed);
            Assert.Contains("image 2026-08-14 09:41", listed);
        }

        [Fact]
        public void A_list_too_long_for_detail_still_carries_the_stale_line()
        {
            var note = "plugin.dll is running an older copy than the build beside its PDB";
            var result = new ModulesResult { LoadedCount = 60 };
            for (var i = 0; i < 60; i++)
                result.Modules.Add(new ModuleInfo { Name = "mod" + i + ".dll", SymbolsLoaded = true });
            result.Modules[7].StaleDeployment = note;

            var text = Render.Modules(result);

            Assert.Contains("Filter to see each one's path, size and load address", text);
            Assert.Contains(note, text);
        }

        [Fact]
        public void A_breakpoint_that_did_not_bind_carries_what_is_stale_in_the_session()
        {
            var note = "One loaded module is running an older copy than the build beside its PDB: plugin.dll.";

            var set = Render.Breakpoint(new BreakpointInfo
            {
                Id = 3,
                Kind = BreakpointKind.Location,
                File = @"D:\repo\plugin\mesh.cpp",
                Line = 120,
                Bound = false,
                BindState = BindFailure.NoCodeHere,
                StaleModules = note
            });

            Assert.Contains("UNBOUND", set);
            Assert.Contains(BindFailure.NoCodeHere, set);
            Assert.Contains(note, set);
        }

        [Fact]
        public void The_session_wide_note_is_said_once_however_many_breakpoints_missed()
        {
            var note = "One loaded module is running an older copy than the build beside its PDB: plugin.dll.";

            var text = Render.Breakpoints(new List<BreakpointInfo>
            {
                new BreakpointInfo { Id = 1, Kind = BreakpointKind.Location, File = @"D:\a.cpp", Line = 10, Bound = false, BindState = BindFailure.NoCodeHere, StaleModules = note },
                new BreakpointInfo { Id = 2, Kind = BreakpointKind.Location, File = @"D:\b.cpp", Line = 20, Bound = false, BindState = BindFailure.NoCodeHere, StaleModules = note }
            });

            var first = text.IndexOf(note, StringComparison.Ordinal);
            Assert.True(first >= 0);
            Assert.Equal(first, text.LastIndexOf(note, StringComparison.Ordinal));
        }

        [Fact]
        public void A_bound_breakpoint_says_nothing_about_stale_modules()
        {
            var text = Render.Breakpoints(new List<BreakpointInfo>
            {
                new BreakpointInfo { Id = 1, Kind = BreakpointKind.Location, File = @"D:\a.cpp", Line = 10, Bound = true, StaleModules = "should not appear" }
            });

            Assert.DoesNotContain("should not appear", text);
        }

        /// <summary>
        /// Modules belong to a process, and a session holding a launcher and what it
        /// started can be looking at one while the other is the one that stopped. Without
        /// the name, a module list and an eval read as being about the same program.
        /// </summary>
        [Fact]
        public void A_module_list_names_the_process_it_belongs_to()
        {
            var text = Render.Modules(new ModulesResult
            {
                Modules = new List<ModuleInfo> { new ModuleInfo { Name = "plugin.dll", SymbolsLoaded = true } },
                LoadedCount = 1,
                Process = "nosLauncher (60024)"
            });

            Assert.Contains("nosLauncher (60024)", text);
        }

        [Fact]
        public void A_module_list_from_a_session_that_named_no_process_says_nothing_about_one()
        {
            var text = Render.Modules(new ModulesResult
            {
                Modules = new List<ModuleInfo> { new ModuleInfo { Name = "plugin.dll", SymbolsLoaded = true } },
                LoadedCount = 1
            });

            Assert.DoesNotContain(" in ", text);
        }
    }
}
