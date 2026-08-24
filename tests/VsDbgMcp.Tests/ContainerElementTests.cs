using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Picking one element out of a std container. Producing the rows is the visualizer's
    /// half and needs a live engine; these cover the half that chooses among the rows and
    /// says what it looked at when nothing matched.
    /// </summary>
    public class ContainerElementTests
    {
        /// <summary>
        /// An unordered_map of Device pointers, as the visualizer lays it out - the case
        /// from the report, whose element reference is the expression nobody can proofread.
        /// </summary>
        static List<VarNode> Devices() => new List<VarNode>
        {
            Pair("[0]", "3833460691349555506", "shared_ptr {aa}", "((pair*)&$LinkedListItem(0))->_Myval"),
            Pair("[1]", "9042113400271299201", "shared_ptr {bb}", "((pair*)&$LinkedListItem(1))->_Myval"),
            new VarNode { Name = "[raw view]", Value = "{...}" }
        };

        static VarNode Pair(string name, string key, string value, string reference) => new VarNode
        {
            Name = name,
            Value = "{first=" + key + " second=" + value + "}",
            Ref = reference,
            HasChildren = true,
            Children = new List<VarNode>
            {
                new VarNode { Name = "first", Value = key },
                new VarNode { Name = "second", Value = value }
            }
        };

        [Fact]
        public void The_rows_a_visualizer_numbers_are_the_elements()
        {
            var elements = ContainerElement.In(Devices());

            Assert.Equal(2, elements.Count);
            Assert.Equal("[0]", elements[0].Name);
            Assert.Equal("[1]", elements[1].Name);
        }

        [Fact]
        public void A_row_that_is_not_numbered_is_not_an_element()
        {
            Assert.False(ContainerElement.IsElement("[raw view]"));
            Assert.False(ContainerElement.IsElement("_Mypair"));
            Assert.False(ContainerElement.IsElement("[]"));
            Assert.True(ContainerElement.IsElement("[0]"));
            Assert.True(ContainerElement.IsElement("[137]"));
        }

        [Fact]
        public void An_index_picks_the_row_the_visualizer_numbered_that_way()
        {
            var chosen = ContainerElement.At(ContainerElement.In(Devices()), 1);

            Assert.Equal("[1]", chosen.Name);
            Assert.Equal("((pair*)&$LinkedListItem(1))->_Myval", chosen.Ref);
        }

        [Fact]
        public void A_key_is_matched_against_a_maps_first()
        {
            var chosen = ContainerElement.WithKey(ContainerElement.In(Devices()), "3833460691349555506");

            Assert.Equal("[0]", chosen.Name);
        }

        [Fact]
        public void A_container_with_no_key_of_its_own_matches_on_the_element()
        {
            var set = new List<VarNode>
            {
                new VarNode { Name = "[0]", Value = "11" },
                new VarNode { Name = "[1]", Value = "22" }
            };

            Assert.Equal("[1]", ContainerElement.WithKey(set, "22").Name);
        }

        [Fact]
        public void A_string_key_matches_with_or_without_the_quotes_it_prints_in()
        {
            var names = new List<VarNode> { new VarNode { Name = "[0]", Value = "\"terrain\"" } };

            Assert.NotNull(ContainerElement.WithKey(names, "terrain"));
            Assert.NotNull(ContainerElement.WithKey(names, "\"terrain\""));
        }

        [Fact]
        public void A_key_that_matches_nothing_says_what_it_compared()
        {
            var elements = ContainerElement.In(Devices());
            var text = ContainerElement.NotFound("Devices", elements, null, "42", 200);

            Assert.Contains("No element of 'Devices' has the key 42", text);
            Assert.Contains("walked 2 elements", text);
            Assert.Contains("3833460691349555506", text);
        }

        [Fact]
        public void A_key_search_that_filled_the_limit_says_it_saw_no_further()
        {
            var elements = new List<VarNode>();
            for (var i = 0; i < 200; i++)
                elements.Add(new VarNode { Name = "[" + i + "]", Value = i.ToString() });

            Assert.Contains("as many as one expansion reads",
                ContainerElement.NotFound("v", elements, null, "900", 200));
        }

        [Fact]
        public void An_index_past_the_end_says_how_many_elements_there_were()
        {
            var text = ContainerElement.NotFound("Devices", ContainerElement.In(Devices()), 7, null, 200);

            Assert.Contains("has no element [7]", text);
            Assert.Contains("2 elements, [0] to [1]", text);
        }

        [Fact]
        public void Something_the_visualizer_does_not_lay_out_as_elements_is_refused_by_name()
        {
            var rows = new List<VarNode>
            {
                new VarNode { Name = "_Mypair", Value = "{...}" },
                new VarNode { Name = "_Mysize", Value = "2" }
            };

            var text = ContainerElement.NotAContainer("Devices", rows);

            Assert.Contains("does not expand as elements", text);
            Assert.Contains("_Mypair, _Mysize", text);
        }

        // ------------------------------------------------------------------ rendering

        [Fact]
        public void A_chosen_element_hands_back_the_reference_for_the_next_call()
        {
            var chosen = ContainerElement.At(ContainerElement.In(Devices()), 0);
            var text = Render.Vars(new VarsResult
            {
                Ref = chosen.Ref,
                Nodes = new List<VarNode> { chosen }
            });

            Assert.Contains("ref: ((pair*)&$LinkedListItem(0))->_Myval", text);
            Assert.Contains("first = 3833460691349555506", text);
        }
    }
}
