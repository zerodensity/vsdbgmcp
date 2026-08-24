using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// One module's symbol story.
    ///
    /// The failure these cover: a PDB was finally in place and still would not bind,
    /// and the only account of why lived in a Modules window dialog. What a reply may
    /// claim is the point - the state after a load attempt, never what the load call
    /// returned.
    /// </summary>
    public class SymbolsTests
    {
        [Fact]
        public void Symbols_that_will_not_load_carry_every_path_the_engine_tried()
        {
            var text = Render.Symbols(new SymbolResult
            {
                Query = "plugin",
                Module = new ModuleInfo
                {
                    Name = "plugin.dll",
                    Path = @"C:\deploy\plugin.dll",
                    SymbolsLoaded = false,
                    SymbolStatus = "cannot find or open the PDB file"
                },
                LoadTried = true,
                SearchInfo = "C:/Symbols/plugin.pdb: Cannot find or open the PDB file.\n" +
                             "C:/deploy/plugin.pdb: PDB does not match image."
            });

            Assert.Contains("NO SYMBOLS", text);
            Assert.Contains("Load Symbols ran and the module still has no symbols.", text);
            Assert.Contains("Where it looked:", text);
            Assert.Contains("PDB does not match image", text);
        }

        [Fact]
        public void A_load_that_worked_says_it_will_not_survive_the_module_reloading()
        {
            var text = Render.Symbols(new SymbolResult
            {
                Query = "plugin",
                Module = new ModuleInfo { Name = "plugin.dll", SymbolsLoaded = true, SymbolPath = @"C:\deploy\plugin.pdb" },
                LoadTried = true
            });

            Assert.Contains("Load Symbols worked", text);
            Assert.Contains("Include/Exclude", text);
            Assert.Contains("applied afresh on every load", text);
        }

        [Fact]
        public void A_load_the_engine_turned_down_is_not_reported_as_a_search_that_failed()
        {
            var text = Render.Symbols(new SymbolResult
            {
                Query = "plugin",
                Module = new ModuleInfo { Name = "plugin.dll", SymbolsLoaded = false, SymbolStatus = "no symbols loaded" },
                LoadTried = true,
                LoadRefused = true
            });

            Assert.Contains("turned the load down rather than searching", text);
        }

        [Fact]
        public void An_engine_that_will_not_discuss_a_module_says_so_and_still_reports_its_state()
        {
            var text = Render.Symbols(new SymbolResult
            {
                Query = "plugin",
                Module = new ModuleInfo { Name = "plugin.dll", SymbolsLoaded = false, SymbolStatus = "no symbols loaded" },
                Message = "This debug engine does not offer symbol loading for plugin.dll, " +
                          "so nothing here can load them or say where it looked."
            });

            Assert.Contains("plugin.dll", text);
            Assert.Contains("does not offer symbol loading", text);
            Assert.DoesNotContain("Load Symbols", text);
        }

        [Fact]
        public void A_name_matching_several_modules_lists_them_instead_of_picking_one()
        {
            var text = Render.Symbols(new SymbolResult
            {
                Query = "nos",
                LoadedCount = 483,
                Candidates = new List<string> { "nos.dll", "nosPlugin.dll" },
                Message = "'nos' matches 2 modules. Name one of them."
            });

            Assert.Contains("matches 2 modules", text);
            Assert.Contains("nos.dll", text);
            Assert.Contains("nosPlugin.dll", text);
        }
    }
}
