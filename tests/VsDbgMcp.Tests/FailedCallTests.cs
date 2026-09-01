using System.Collections.Generic;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Which replies are failures. A caller should not have to read the words to find
    /// out whether a call did anything, and the panel inside Visual Studio should not
    /// show a read that returned nothing as a read that worked.
    ///
    /// The line is not "there is a message": a filter matching nothing is an answer, and
    /// a frame that cannot be read is not.
    /// </summary>
    public class FailedCallTests
    {
        [Fact]
        public void An_evaluation_the_engine_refused_is_a_failure()
        {
            var reply = Render.Evals(new[]
            {
                new EvalResult
                {
                    Expression = "mesh.refCount",
                    IsValid = false,
                    Error = "identifier \"mesh\" is undefined"
                }
            });

            Assert.True(reply.Failed);
            Assert.Contains("identifier \"mesh\" is undefined", reply.Text);
        }

        [Fact]
        public void A_value_that_read_fine_is_not()
        {
            var reply = Render.Evals(new[]
            {
                new EvalResult { Expression = "count", Value = "3", IsValid = true }
            });

            Assert.False(reply.Failed);
        }

        [Fact]
        public void One_thread_failing_among_many_is_still_a_reading_of_the_pool()
        {
            var reply = Render.Evals(new[]
            {
                new EvalResult { Expression = "m_state", Value = "2", IsValid = true, ThreadId = 10 },
                new EvalResult { Expression = "m_state", IsValid = false, Error = "not in scope here", ThreadId = 11 }
            });

            Assert.False(reply.Failed);
        }

        [Fact]
        public void Every_thread_failing_is_the_same_failure_said_many_times()
        {
            var reply = Render.Evals(new[]
            {
                new EvalResult { Expression = "m_state", IsValid = false, Error = "not in scope here", ThreadId = 10 },
                new EvalResult { Expression = "m_state", IsValid = false, Error = "not in scope here", ThreadId = 11 }
            });

            Assert.True(reply.Failed);
        }

        [Fact]
        public void An_operation_the_debugger_refused_is_a_failure()
        {
            var reply = Render.Op(OpResult.Bad("Nothing is running. Current mode: design."), "Paused.");

            Assert.True(reply.Failed);
            Assert.Contains("Nothing is running", reply.Text);
        }

        [Fact]
        public void An_operation_that_worked_is_not()
        {
            Assert.False(Render.Op(OpResult.Good(), "Paused.").Failed);
        }

        [Fact]
        public void A_frame_that_cannot_be_read_is_a_failure()
        {
            var reply = Render.Vars(new VarsResult
            {
                Message = "the engine would not list variables in this frame",
                Failed = true
            });

            Assert.True(reply.Failed);
        }

        [Fact]
        public void A_filter_that_matched_nothing_is_an_answer()
        {
            var reply = Render.Vars(new VarsResult
            {
                Message = "No variable's name contains 'mesh'. 12 were read in this frame; drop the filter to see them."
            });

            Assert.False(reply.Failed);
            Assert.Contains("drop the filter", reply.Text);
        }

        [Fact]
        public void A_node_whose_contents_could_not_be_read_says_so_where_the_value_is()
        {
            var text = Render.Vars(new List<VarNode>
            {
                new VarNode
                {
                    Name = "mesh",
                    Value = "{...}",
                    Type = "Mesh *",
                    HasChildren = true,
                    Ref = "mesh",
                    Note = "The engine would not list what is inside this (HRESULT 0x80004005)."
                }
            });

            Assert.Contains("The engine would not list what is inside this", text);
        }

        [Fact]
        public void A_partial_listing_is_not_passed_off_as_the_whole_of_it()
        {
            var reply = Render.Vars(new VarsResult
            {
                Nodes = { new VarNode { Name = "[0]", Value = "1" } },
                Message = "These are the first 200 of 4096."
            });

            Assert.False(reply.Failed);
            Assert.Contains("first 200 of 4096", reply.Text);
        }
    }
}
