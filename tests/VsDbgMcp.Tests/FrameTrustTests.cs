using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Which of a frame's values are evidence and which are not.
    ///
    /// The report this feeds prints every variable in one go, and a reader scanning
    /// forty rows will not notice that one of them came from a slot the compiler handed
    /// to two locals. The marks are gathered here so they can be said twice: against the
    /// value, and again as a list at the end of everything that is not to be believed.
    /// </summary>
    public class FrameTrustTests
    {
        [Fact]
        public void A_plain_value_carries_no_marks()
        {
            var marks = FrameTrust.Marks(new VarNode { Name = "total", Value = "220", Type = "int" });

            Assert.Empty(marks);
        }

        /// <summary>
        /// The engine says it could not read this, which in an optimized frame means the
        /// compiler kept nothing. Printing the reason as though it were a value is what
        /// iteration 1 stopped doing; this carries the fact forward into the summary.
        /// </summary>
        [Fact]
        public void A_value_the_compiler_kept_nothing_for_is_marked()
        {
            var marks = FrameTrust.Marks(new VarNode
            {
                Name = "shifted",
                Value = "the debugger cannot read this",
                Readable = false
            });

            Assert.Contains(marks, m => m.Contains("not readable"));
        }

        [Fact]
        public void A_slot_shared_with_other_locals_names_them()
        {
            var marks = FrameTrust.Marks(new VarNode
            {
                Name = "masked",
                Value = "60",
                SameAddressAs = new List<string> { "folded", "carried" }
            });

            Assert.Contains(marks, m => m.Contains("folded") && m.Contains("carried"));
        }

        [Fact]
        public void An_allocator_fill_pattern_is_named_where_it_appears()
        {
            var marks = FrameTrust.Marks(new VarNode
            {
                Name = "state",
                Value = "0xdddddddddddddddd",
                Type = "void *"
            });

            Assert.Contains(marks, m => m.Contains("0xdd") && m.Contains("freed"));
        }

        /// <summary>
        /// The retrospective's TMap. A summary claiming nothing is inside is the one
        /// wrong answer a reader cannot see, because it reads as an answer.
        /// </summary>
        [Fact]
        public void A_container_claiming_to_be_empty_is_marked_for_a_second_reading()
        {
            var marks = FrameTrust.Marks(new VarNode
            {
                Name = "ResourceProperties",
                Value = "Empty",
                Type = "TMap<FName,FProperty *>",
                HasChildren = true
            });

            Assert.Contains(marks, m => m.Contains("empty"));
        }

        /// <summary>
        /// A plain int of zero is not a container lying about its size, and marking it
        /// would bury the marks that matter under every zero in the frame.
        /// </summary>
        [Fact]
        public void A_scalar_that_happens_to_be_zero_is_not_marked_as_an_empty_container()
        {
            var marks = FrameTrust.Marks(new VarNode { Name = "total", Value = "0", Type = "int" });

            Assert.Empty(marks);
        }

        /// <summary>
        /// This is the fixture's own mesh, and a review caught it being marked. A struct
        /// summary that mentions a member which happens to be zero is not a container
        /// claiming to be empty: this one has a name and a reference count. Marking it
        /// puts a plainly good value in the one list that has to be believed.
        /// </summary>
        [Fact]
        public void A_struct_with_an_empty_member_is_not_a_container_claiming_to_be_empty()
        {
            var marks = FrameTrust.Marks(new VarNode
            {
                Name = "mesh",
                Value = "{name=\"terrain\" vertices={ size=0 } refCount=1 }",
                Type = "Mesh &",
                HasChildren = true
            });

            Assert.Empty(marks);
        }

        [Fact]
        public void A_pointer_to_a_struct_with_an_empty_member_is_not_marked_either()
        {
            var marks = FrameTrust.Marks(new VarNode
            {
                Name = "this",
                Value = "0x000001f2c4a0 {name=\"terrain\" vertices={ size=0 } refCount=1 }",
                Type = "Mesh *",
                HasChildren = true
            });

            Assert.Empty(marks);
        }

        /// <summary>
        /// The container's own count, with nothing else in the summary, is the case the
        /// mark exists for and has to survive the guard above.
        /// </summary>
        [Fact]
        public void A_container_whose_whole_summary_is_its_own_zero_count_is_still_marked()
        {
            var marks = FrameTrust.Marks(new VarNode
            {
                Name = "vertices",
                Value = "{ size=0 }",
                Type = "std::vector<float>",
                HasChildren = true
            });

            Assert.Contains(marks, m => m.Contains("empty"));
        }

        [Fact]
        public void The_summary_lists_only_the_values_that_carry_a_mark()
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
        /// Saying nothing here would read as a section that was left out. A frame whose
        /// values all read cleanly is a result, and the one worth stating plainly.
        /// </summary>
        [Fact]
        public void A_frame_where_everything_read_cleanly_says_so_rather_than_nothing()
        {
            var text = FrameTrust.NotEvidence(new List<VarNode>
            {
                new VarNode { Name = "total", Value = "220", Type = "int" }
            });

            Assert.NotNull(text);
            Assert.Contains("read cleanly", text);
        }

        [Fact]
        public void No_variables_at_all_makes_no_claim_about_them()
        {
            Assert.Null(FrameTrust.NotEvidence(new List<VarNode>()));
            Assert.Null(FrameTrust.NotEvidence(null));
        }
    }
}
