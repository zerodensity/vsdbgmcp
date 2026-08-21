using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// What an optimized frame has to look like in a reply. Reading the storage is the
    /// extension's half and needs a live engine; this covers the half that decides what
    /// the caller is told.
    /// </summary>
    public class OptimizedFrameTests
    {
        [Fact]
        public void A_variable_the_engine_could_not_read_says_so()
        {
            var text = Render.Vars(new List<VarNode>
            {
                new VarNode
                {
                    Name = "mode",
                    Value = "Variable is optimized away and not available",
                    Type = "int",
                    Readable = false
                }
            });

            Assert.Contains("not readable here", text);
        }

        [Fact]
        public void Variables_on_one_address_name_each_other()
        {
            // Straight from the report: two locals in a DeckLink frame reading the same
            // pointer, with nothing to say one of them was a slot the optimizer reused.
            var text = Render.Vars(new List<VarNode>
            {
                new VarNode
                {
                    Name = "this",
                    Value = "0x000002894e0f2080",
                    SameAddressAs = new List<string> { "profile" }
                },
                new VarNode
                {
                    Name = "profile",
                    Value = "0x000002894e0f2080",
                    SameAddressAs = new List<string> { "this" }
                }
            });

            Assert.Contains("this = 0x000002894e0f2080  -- same address as profile", text);
            Assert.Contains("profile = 0x000002894e0f2080  -- same address as this", text);
            Assert.Contains("the optimizer reused a slot", text);
        }

        [Fact]
        public void An_ordinary_frame_reads_exactly_as_it_did()
        {
            var text = Render.Vars(new List<VarNode>
            {
                new VarNode { Name = "count", Value = "4", Type = "int" },
                new VarNode { Name = "mesh", Value = "{name=\"terrain\"}", Type = "Mesh *", HasChildren = true, Ref = "mesh" }
            });

            Assert.Equal(
                "  count = 4  (int)\r\n  mesh = {name=\"terrain\"}  (Mesh *)  ... expand mesh".Replace("\r\n", "\n"),
                text.Replace("\r\n", "\n"));
        }
    }
}
