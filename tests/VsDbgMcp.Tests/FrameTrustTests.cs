using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Which of a frame's values may not be what they look like.
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
        /// A value nothing could read is a settled fact, not a doubt: there is no truth
        /// here to be wrong about, and the row already says it is not readable. Listing
        /// it among values that may be wrong states it weaker than it is and repeats a
        /// line the reader has already had.
        /// </summary>
        [Fact]
        public void A_value_nothing_could_read_is_a_fact_rather_than_a_doubt()
        {
            Assert.Null(FrameTrust.Reason(new VarNode
            {
                Name = "shifted",
                Value = "the debugger cannot read this",
                Readable = false
            }));
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

        /// <summary>
        /// 0xdd is the answer, not a reason to doubt one. The row names the pattern; a
        /// list of what may be wrong is for values that are shown and might not be this
        /// variable's.
        /// </summary>
        [Fact]
        public void An_allocator_fill_pattern_is_a_fact_rather_than_a_doubt()
        {
            Assert.Null(FrameTrust.Reason(new VarNode
            {
                Name = "state",
                Value = "0xdddddddddddddddd",
                Type = "void *"
            }));
        }

        /// <summary>
        /// The retrospective's TMap. A summary claiming nothing is inside is the one
        /// wrong answer a reader cannot see, because it reads as an answer.
        /// </summary>
        [Fact]
        public void A_container_claiming_to_be_empty_that_no_raw_read_settled_keeps_the_doubt()
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

        /// <summary>
        /// The whole point of taking the second reading. A container the raw layout
        /// agreed was empty is empty, and saying anything about it afterwards asks the
        /// reader to check something already checked.
        /// </summary>
        [Fact]
        public void A_container_a_raw_read_confirmed_empty_says_nothing()
        {
            Assert.Null(FrameTrust.Reason(new VarNode
            {
                Name = "workers",
                Value = "{ size=0 }",
                Type = "std::vector<std::thread>",
                HasChildren = true,
                Settled = true
            }));
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
            var text = FrameTrust.MayBeWrong(new List<VarNode>
            {
                new VarNode { Name = "total", Value = "220", Type = "int" },
                new VarNode { Name = "folded", Value = "-1", SameAddressAs = new List<string> { "carried" } }
            });

            Assert.Contains("folded", text);
            Assert.DoesNotContain("total", text);
        }

        /// <summary>
        /// The list carries what to do about each, not just the names. Reading the names
        /// alone sends somebody back up the reply to find out why.
        /// </summary>
        [Fact]
        public void The_closing_list_carries_the_reason_beside_each_name()
        {
            var text = FrameTrust.MayBeWrong(new List<VarNode>
            {
                new VarNode { Name = "folded", Value = "-1", SameAddressAs = new List<string> { "carried" } }
            });

            Assert.Contains("folded", text);
            Assert.Contains("may belong to one of those", text);
        }

        /// <summary>
        /// Said in one line. It is the answer on most frames, and a paragraph saying
        /// nothing is wrong is a paragraph nobody finishes - but saying nothing at all
        /// would leave a clean frame looking like a check that never ran.
        /// </summary>
        [Fact]
        public void A_frame_where_everything_read_cleanly_says_so_in_one_line()
        {
            var text = FrameTrust.MayBeWrong(new List<VarNode>
            {
                new VarNode { Name = "total", Value = "220", Type = "int" }
            });

            Assert.Contains("read cleanly", text);
            Assert.DoesNotContain("\n", text);
        }

        [Fact]
        public void No_values_at_all_makes_no_claim_about_them()
        {
            Assert.Null(FrameTrust.MayBeWrong(new List<VarNode>()));
            Assert.Null(FrameTrust.MayBeWrong(null));
        }
    }
}
