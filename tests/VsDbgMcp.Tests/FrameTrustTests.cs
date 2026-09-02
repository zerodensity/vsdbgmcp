using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Which of a frame's values are evidence and which are not.
    ///
    /// The row above already carries each fact in a few words. This decides which values
    /// are worth naming again at the end, and what to do about each, because nobody
    /// scanning forty rows sees the marks where they happened.
    /// </summary>
    public class FrameTrustTests
    {
        [Fact]
        public void A_plain_value_has_nothing_against_it()
        {
            Assert.Null(FrameTrust.Reason(new VarNode { Name = "total", Value = "220", Type = "int" }));
        }

        /// <summary>
        /// The engine says it could not read this, which in an optimized frame means the
        /// compiler kept nothing. The text beside it is the engine's reason, and reading
        /// it as a value is the mistake worth naming.
        /// </summary>
        [Fact]
        public void A_value_the_compiler_kept_nothing_for_says_the_text_is_not_a_value()
        {
            var reason = FrameTrust.Reason(new VarNode
            {
                Name = "shifted",
                Value = "the debugger cannot read this",
                Readable = false
            });

            Assert.Contains("not a value", reason);
        }

        [Fact]
        public void A_slot_shared_with_other_locals_names_them()
        {
            var reason = FrameTrust.Reason(new VarNode
            {
                Name = "masked",
                Value = "60",
                SameAddressAs = new List<string> { "folded", "carried" }
            });

            Assert.Contains("folded", reason);
            Assert.Contains("carried", reason);
        }

        [Fact]
        public void An_allocator_fill_pattern_says_the_value_is_not_live_data()
        {
            var reason = FrameTrust.Reason(new VarNode
            {
                Name = "state",
                Value = "0xdddddddddddddddd",
                Type = "void *"
            });

            Assert.Contains("0xdd", reason);
            Assert.Contains("not live data", reason);
        }

        /// <summary>
        /// The retrospective's TMap. A summary claiming nothing is inside is the one
        /// wrong answer a reader cannot see, because it reads as an answer.
        /// </summary>
        [Fact]
        public void A_container_claiming_to_be_empty_is_sent_for_a_second_reading()
        {
            var reason = FrameTrust.Reason(new VarNode
            {
                Name = "ResourceProperties",
                Value = "Empty",
                Type = "TMap<FName,FProperty *>",
                HasChildren = true
            });

            Assert.Contains("expand", reason);
        }

        [Fact]
        public void A_scalar_that_happens_to_be_zero_is_not_an_empty_container()
        {
            Assert.Null(FrameTrust.Reason(new VarNode { Name = "total", Value = "0", Type = "int" }));
        }

        /// <summary>
        /// This is the fixture's own mesh, and a review caught it being flagged. A struct
        /// summary that mentions a member which happens to be zero is not a container
        /// claiming to be empty: this one has a name and a reference count. Flagging it
        /// puts a plainly good value in the one list that has to be believed.
        /// </summary>
        [Fact]
        public void A_struct_with_an_empty_member_is_not_a_container_claiming_to_be_empty()
        {
            Assert.Null(FrameTrust.Reason(new VarNode
            {
                Name = "mesh",
                Value = "{name=\"terrain\" vertices={ size=0 } refCount=1 }",
                Type = "Mesh &",
                HasChildren = true
            }));
        }

        [Fact]
        public void A_pointer_to_such_a_struct_is_not_flagged_either()
        {
            Assert.Null(FrameTrust.Reason(new VarNode
            {
                Name = "this",
                Value = "0x000001f2c4a0 {name=\"terrain\" vertices={ size=0 } refCount=1 }",
                Type = "Mesh *",
                HasChildren = true
            }));
        }

        [Fact]
        public void A_container_whose_whole_summary_is_its_own_zero_count_is_still_flagged()
        {
            var reason = FrameTrust.Reason(new VarNode
            {
                Name = "vertices",
                Value = "{ size=0 }",
                Type = "std::vector<float>",
                HasChildren = true
            });

            Assert.NotNull(reason);
        }

        [Fact]
        public void The_closing_list_names_only_the_values_with_something_against_them()
        {
            var text = FrameTrust.NotEvidence(new List<VarNode>
            {
                new VarNode { Name = "total", Value = "220", Type = "int" },
                new VarNode { Name = "shifted", Value = "no", Readable = false },
                new VarNode { Name = "state", Value = "0xdddddddddddddddd" }
            });

            Assert.Contains("shifted", text);
            Assert.Contains("state", text);
            Assert.DoesNotContain("total", text);
        }

        /// <summary>
        /// The list carries what to do about each, not just the names. Reading the names
        /// alone sends somebody back up the reply to find out why.
        /// </summary>
        [Fact]
        public void The_closing_list_carries_the_reason_beside_each_name()
        {
            var text = FrameTrust.NotEvidence(new List<VarNode>
            {
                new VarNode { Name = "shifted", Value = "no", Readable = false }
            });

            Assert.Contains("shifted", text);
            Assert.Contains("not a value", text);
        }

        /// <summary>
        /// Said in one line. It is the answer on most frames, and a paragraph saying
        /// nothing is wrong is a paragraph nobody finishes - but saying nothing at all
        /// would leave a clean frame looking like a check that never ran.
        /// </summary>
        [Fact]
        public void A_frame_where_everything_read_cleanly_says_so_in_one_line()
        {
            var text = FrameTrust.NotEvidence(new List<VarNode>
            {
                new VarNode { Name = "total", Value = "220", Type = "int" }
            });

            Assert.Contains("read cleanly", text);
            Assert.DoesNotContain("\n", text);
        }

        [Fact]
        public void No_values_at_all_makes_no_claim_about_them()
        {
            Assert.Null(FrameTrust.NotEvidence(new List<VarNode>()));
            Assert.Null(FrameTrust.NotEvidence(null));
        }
    }
}
