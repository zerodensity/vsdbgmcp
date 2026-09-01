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

        // ---------------------------------------------------- a container said to be empty

        /// <summary>
        /// An unordered_map with the visualizer off, which is where the size that the
        /// visualizer did not use is written down.
        /// </summary>
        static List<VarNode> RawMap(string size) => new List<VarNode>
        {
            new VarNode
            {
                Name = "_List",
                Value = "{...}",
                Children = new List<VarNode>
                {
                    new VarNode
                    {
                        Name = "_Mypair",
                        Value = "{...}",
                        Children = new List<VarNode>
                        {
                            new VarNode
                            {
                                Name = "_Myval2",
                                Value = "{...}",
                                Children = new List<VarNode>
                                {
                                    new VarNode { Name = "_Myhead", Value = "0x0000023f1c2a0e80" },
                                    new VarNode { Name = "_Mysize", Value = size }
                                }
                            }
                        }
                    }
                }
            },
            new VarNode { Name = "_Traitsobj", Value = "{...}" }
        };

        [Fact]
        public void The_ways_a_visualizer_says_there_is_nothing_inside()
        {
            Assert.True(ContainerElement.LooksEmpty("Empty"));
            Assert.True(ContainerElement.LooksEmpty("{}"));
            Assert.True(ContainerElement.LooksEmpty("{ size=0 }"));
            Assert.True(ContainerElement.LooksEmpty("{ Num=0 }"));
        }

        [Fact]
        public void A_value_that_is_not_a_claim_of_emptiness_is_left_alone()
        {
            Assert.False(ContainerElement.LooksEmpty("{ size=4 }"));
            Assert.False(ContainerElement.LooksEmpty("0x0000000000000000"));
            Assert.False(ContainerElement.LooksEmpty("{name=\"terrain\" refCount=1}"));
            Assert.False(ContainerElement.LooksEmpty(null));
        }

        [Fact]
        public void A_raw_layout_that_contradicts_an_empty_container_names_the_field()
        {
            var text = ContainerElement.RawView("PortalPinsById", "Empty", 0, RawMap("16"));

            Assert.Contains("renders as 'Empty'", text);
            Assert.Contains("Fields that read like counts and are not zero: " +
                            "_List._Mypair._Myval2._Mysize = 16", text);

            // A capacity is spelled the same way as a size, so the fields are reported and
            // the reader draws the conclusion.
            Assert.Contains("do not settle it on their own", text);
            Assert.DoesNotContain("agree", text);
        }

        [Fact]
        public void A_capacity_beside_a_zero_size_is_not_called_a_disagreement()
        {
            // An empty deque keeps its map allocated: _Mysize is 0 and _Mapsize is 8.
            var raw = new List<VarNode>
            {
                new VarNode { Name = "_Mysize", Value = "0" },
                new VarNode { Name = "_Mapsize", Value = "8" }
            };

            var text = ContainerElement.RawView("q", "{ size=0 }", 0, raw);

            Assert.Contains("are not zero: _Mapsize = 8", text);
            Assert.Contains("Ones that are zero: _Mysize = 0", text);
            Assert.DoesNotContain("disagree", text);
        }

        [Fact]
        public void A_raw_layout_that_agrees_says_that_rather_than_nothing()
        {
            var text = ContainerElement.RawView("Devices", "Empty", 0, RawMap("0"));

            Assert.Contains("Every field that reads like a count is zero: " +
                            "_List._Mypair._Myval2._Mysize = 0", text);
            Assert.Contains("Both views agree it is empty", text);
        }

        [Fact]
        public void A_raw_layout_read_only_part_way_down_claims_no_agreement()
        {
            // The engine says there is more under this row and the read stopped above it.
            var raw = new List<VarNode>
            {
                new VarNode { Name = "ArrayNum", Value = "0" },
                new VarNode { Name = "Pairs", Value = "{...}", HasChildren = true }
            };

            var text = ContainerElement.RawView("Map", "Empty", 0, raw);

            Assert.Contains("1 of those rows have contents that were not read", text);
            Assert.DoesNotContain("Both views agree", text);
        }

        [Fact]
        public void A_raw_layout_with_no_count_in_it_claims_neither_way()
        {
            var raw = new List<VarNode>
            {
                new VarNode { Name = "_Myfirst", Value = "0x0000023f1c2a0e80" },
                new VarNode { Name = "_Mylast", Value = "0x0000023f1c2a0e80" }
            };

            var text = ContainerElement.RawView("v", "{ size=0 }", 0, raw);

            Assert.Contains("No field's name reads like a count", text);
            Assert.Contains("confirms or contradicts", text);
        }

        [Fact]
        public void The_rows_the_visualizer_did_show_are_not_called_nothing()
        {
            var text = ContainerElement.RawView("Map", "Empty", 2, RawMap("16"));

            Assert.Contains("produced 2 rows, none of them an element", text);
        }

        [Fact]
        public void A_field_that_is_not_a_number_is_not_a_count()
        {
            var raw = new List<VarNode> { new VarNode { Name = "ElementCount", Value = "{...}" } };

            Assert.Contains("No field's name reads like a count",
                ContainerElement.RawView("Set", "Empty", 0, raw));
        }

        [Fact]
        public void A_name_that_merely_ends_in_a_counting_word_is_not_a_count()
        {
            var raw = new List<VarNode>
            {
                new VarNode { Name = "bUnused", Value = "1" },
                new VarNode { Name = "TypeEnum", Value = "3" }
            };

            Assert.Contains("No field's name reads like a count",
                ContainerElement.RawView("Set", "Empty", 0, raw));
        }

        [Fact]
        public void A_count_that_has_gone_negative_is_the_one_worth_seeing()
        {
            var raw = new List<VarNode> { new VarNode { Name = "ArrayNum", Value = "-1" } };

            Assert.Contains("are not zero: ArrayNum = -1",
                ContainerElement.RawView("Pins", "Empty", 0, raw));
        }

        [Fact]
        public void A_raw_read_that_produced_nothing_says_it_settled_nothing()
        {
            var text = ContainerElement.RawView("Map", "Empty", 0, new List<VarNode>());

            Assert.Contains("produced no fields either", text);
            Assert.Contains("nothing here tells an empty container from a visualizer that is wrong", text);
        }

        [Fact]
        public void A_raw_read_that_would_not_run_says_so_rather_than_reporting_empty()
        {
            var text = ContainerElement.RawUnreadable("Map", "Empty", 0, "identifier 'Map' is undefined");

            Assert.Contains("Reading it raw with ',!' to check that failed as well", text);
            Assert.Contains("identifier 'Map' is undefined", text);
        }

        // -------------------------------------------------------- walking a raw array

        [Fact]
        public void An_index_is_written_the_way_cpp_writes_it()
        {
            Assert.Equal("(RawParams->Pins)[3]->Name",
                ContainerElement.Indexed("RawParams->Pins", 3, "->Name"));
            Assert.Equal("(items)[0].Size", ContainerElement.Indexed("items", 0, ".Size"));
            Assert.Equal("(items)[7]", ContainerElement.Indexed("items", 7, null));
        }

        [Fact]
        public void A_member_with_no_connector_gets_the_one_that_is_not_a_guess()
        {
            Assert.Equal("(items)[2].Name", ContainerElement.Indexed("items", 2, "Name"));
        }

        [Fact]
        public void A_base_expression_that_is_arithmetic_is_indexed_as_a_whole()
        {
            Assert.Equal("(Pins + 12)[1]", ContainerElement.Indexed("Pins + 12", 1, null));
        }

        [Fact]
        public void A_member_that_only_starts_with_a_minus_is_not_an_arrow()
        {
            Assert.Equal("(p)[0].-1", ContainerElement.Indexed("p", 0, "-1"));
        }

        [Fact]
        public void A_run_of_indexes_counts_how_many_of_the_values_were_distinct()
        {
            var text = ContainerElement.Enumeration(28, "->Name", Names(28, 12));

            Assert.Contains("28 values read, 12 of them distinct.", text);
        }

        [Fact]
        public void A_run_where_every_value_differs_says_nothing_about_duplicates()
        {
            Assert.Equal("", ContainerElement.Enumeration(8, "->Name", Names(8, 8)));
        }

        [Fact]
        public void A_run_cut_short_by_the_cap_says_where_it_stopped()
        {
            var text = ContainerElement.Enumeration(4000, null, Names(256, 256));

            Assert.Contains("4000 indexes were asked for and 256 read, which is the cap", text);
            Assert.Contains("(expr + 256)", text);
        }

        [Fact]
        public void A_run_that_gave_up_early_is_not_reported_as_the_cap()
        {
            var rows = new List<EvalResult>();
            for (var i = 0; i < 3; i++)
                rows.Add(new EvalResult { Expression = "(p)[" + i + "]", Error = "bad ptr", Index = i });

            var text = ContainerElement.Enumeration(28, null, rows);

            Assert.Contains("Stopped after 3 of the 28 asked for", text);
            Assert.DoesNotContain("cap", text);
        }

        [Fact]
        public void An_ordinary_result_is_not_described_as_a_run_of_indexes()
        {
            var one = new List<EvalResult> { new EvalResult { Expression = "x", Value = "1", IsValid = true } };

            Assert.Equal("", ContainerElement.Enumeration(28, null, one));
        }

        [Fact]
        public void A_few_rows_failing_among_answers_is_reported_with_the_engines_words()
        {
            var rows = Names(4, 4);
            rows.Add(new EvalResult { Expression = "(p)[4]", IsValid = false, Index = 4, Error = "cannot read memory" });

            var text = ContainerElement.Enumeration(5, null, rows);

            Assert.Contains("1 of the 5 could not be read: cannot read memory", text);
        }

        [Fact]
        public void Every_row_failing_on_a_bare_member_says_the_connector_may_be_wrong()
        {
            var rows = new List<EvalResult>
            {
                new EvalResult { Expression = "(p)[0].Name", IsValid = false, Index = 0, Error = "expression must have class type" }
            };

            var text = ContainerElement.Enumeration(1, "Name", rows);

            Assert.Contains("read as '.Name'", text);
            Assert.Contains("need '->Name'", text);
        }

        static List<EvalResult> Names(int count, int distinct)
        {
            var rows = new List<EvalResult>();
            for (var i = 0; i < count; i++)
            {
                rows.Add(new EvalResult
                {
                    Expression = "(Pins)[" + i + "]->Name",
                    Value = "\"Pin" + (i % distinct) + "\"",
                    Type = "FName",
                    IsValid = true,
                    Index = i
                });
            }
            return rows;
        }

        // ------------------------------------------------------------------ rendering

        [Fact]
        public void A_run_of_indexes_prints_one_row_each_and_the_type_where_it_changes()
        {
            var text = Render.Evals(Names(3, 3), null).Text;

            Assert.Contains("(Pins)[0]->Name = \"Pin0\"  (FName)", text);
            Assert.Contains("(Pins)[1]->Name = \"Pin1\"", text);

            // Three rows of the same type, said once.
            Assert.Equal(1, text.Split(new[] { "(FName)" }, System.StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void A_run_where_nothing_could_be_read_is_a_failed_call()
        {
            var rows = new List<EvalResult>
            {
                new EvalResult { Expression = "(p)[0]", IsValid = false, Error = "cannot read memory", Index = 0 }
            };

            var reply = Render.Evals(rows, "every one failed");

            Assert.True(reply.Failed);
            Assert.Contains("(p)[0]  -- cannot read memory", reply.Text);
            Assert.Contains("every one failed", reply.Text);
        }

        [Fact]
        public void The_raw_layout_comes_back_under_a_row_that_says_what_it_is()
        {
            var text = Render.Vars(new VarsResult
            {
                Nodes = new List<VarNode>
                {
                    new VarNode
                    {
                        Name = "[raw layout]",
                        Value = "read with ',!' because the visualizer showed no elements",
                        HasChildren = true,
                        Children = RawMap("16")
                    }
                },
                Message = ContainerElement.RawView("PortalPinsById", "Empty", 0, RawMap("16"))
            }).Text;

            Assert.Contains("[raw layout] = read with ',!'", text);
            Assert.Contains("_Mysize = 16", text);
            Assert.Contains("are not zero", text);
        }

        [Fact]
        public void A_chosen_element_hands_back_the_reference_for_the_next_call()
        {
            var chosen = ContainerElement.At(ContainerElement.In(Devices()), 0);
            var text = Render.Vars(new VarsResult
            {
                Ref = chosen.Ref,
                Nodes = new List<VarNode> { chosen }
            }).Text;

            Assert.Contains("ref: ((pair*)&$LinkedListItem(0))->_Myval", text);
            Assert.Contains("first = 3833460691349555506", text);
        }
    }
}
