using System.Collections.Generic;
using VsDbgMcp;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// The one-call picture of a frame.
    ///
    /// Everything it prints is readable one call at a time already. What it has to get
    /// right is the things a reader would not have thought to ask: which binary the code
    /// came from, whether the file on disk is still that binary's source, and which of
    /// the values in front of them are not evidence.
    /// </summary>
    public class FrameReportTests
    {
        static FrameReport At(params VarNode[] locals) => new FrameReport
        {
            Frame = new Frame
            {
                Index = 0,
                Function = "Upload",
                File = @"D:\repo\main.cpp",
                Line = 38,
                Module = "DebugTarget.exe"
            },
            ThreadId = 57700,
            ProcessName = "DebugTarget.exe",
            Pid = 70632,
            Sections = new List<VarSection>
            {
                new VarSection { Name = "locals", Nodes = new List<VarNode>(locals) }
            }
        };

        [Fact]
        public void A_refusal_is_the_whole_reply_and_is_marked_failed()
        {
            var reply = Render.Frame(new FrameReport { Refusal = "the debugger is not stopped" });

            Assert.True(reply.Failed);
            Assert.Contains("not stopped", reply.Text);
        }

        [Fact]
        public void The_frame_names_its_thread_and_process()
        {
            var text = Render.Frame(At()).Text;

            Assert.Contains("57700", text);
            Assert.Contains("DebugTarget.exe (70632)", text);
        }

        /// <summary>
        /// The line the program is about to run has to be findable at a glance, or the
        /// window is just a paragraph of code.
        /// </summary>
        [Fact]
        public void The_current_source_line_is_marked_and_the_others_are_not()
        {
            var report = At();
            report.Source = new List<SourceLine>
            {
                new SourceLine { Number = 37, Text = "int total = 0;" },
                new SourceLine { Number = 38, Text = "total += 1;", IsCurrent = true },
                new SourceLine { Number = 39, Text = "return total;" }
            };

            var text = Render.Frame(report).Text;

            Assert.Contains(">     38  total += 1;", text);
            Assert.Contains("      37  int total = 0;", text);
        }

        /// <summary>
        /// Showing source is showing something the debugger did not say. A file edited
        /// since the binary was built still opens and still has that line number, and
        /// printing it without this is how somebody reasons about code that is not
        /// running.
        /// </summary>
        [Fact]
        public void Source_that_may_not_match_the_binary_says_so_beside_it()
        {
            var report = At();
            report.Source = new List<SourceLine> { new SourceLine { Number = 38, Text = "x;", IsCurrent = true } };
            report.SourceWarning = "This file has been written since the module was built";

            Assert.Contains("written since the module was built", Render.Frame(report).Text);
        }

        /// <summary>
        /// Without symbols the engine is naming addresses, not variables. Printing the
        /// values under a heading and saying nothing would pass a guess off as a reading.
        /// </summary>
        [Fact]
        public void A_module_without_symbols_says_the_values_below_are_not_to_be_trusted()
        {
            var report = At();
            report.Module = new ModuleInfo { Name = "plugin.dll", SymbolsLoaded = false, SymbolStatus = "no PDB found" };

            Assert.Contains("no symbols loaded", Render.Frame(report).Text);
        }

        /// <summary>
        /// An absent heading reads as a section left out. A frame that genuinely has no
        /// locals is a fact worth stating.
        /// </summary>
        [Fact]
        public void A_scope_with_nothing_in_it_says_so_rather_than_being_left_out()
        {
            var text = Render.Frame(At()).Text;

            Assert.Contains("== locals ==", text);
            Assert.Contains("none in scope here", text);
        }

        /// <summary>
        /// An empty list and a list the engine would not produce are different answers,
        /// and only the reader knows which it gave.
        /// </summary>
        [Fact]
        public void A_scope_the_engine_would_not_list_says_that_instead_of_none_in_scope()
        {
            var report = At();
            report.Sections[0].Note = "the engine would not list variables in this frame";

            var text = Render.Frame(report).Text;

            Assert.Contains("would not list", text);
            Assert.DoesNotContain("none in scope here", text);
        }

        [Fact]
        public void Every_value_carries_what_is_wrong_with_it()
        {
            var text = Render.Frame(At(
                new VarNode { Name = "total", Value = "220", Type = "int" },
                new VarNode { Name = "shifted", Value = "no", Readable = false })).Text;

            Assert.Contains("total = 220", text);
            Assert.Contains("not readable here", text);
        }

        /// <summary>
        /// The part that earns the call. Every mark is also against its own value above,
        /// and nobody scanning forty rows sees them there.
        /// </summary>
        [Fact]
        public void The_closing_list_names_only_the_values_that_are_not_evidence()
        {
            var text = Render.Frame(At(
                new VarNode { Name = "total", Value = "220", Type = "int" },
                new VarNode { Name = "shifted", Value = "no", Readable = false })).Text;

            Assert.Contains("== values that may be wrong ==", text);
            Assert.Contains("shifted", text.Substring(text.IndexOf("== values that may be wrong ==")));
            Assert.DoesNotContain("total", text.Substring(text.IndexOf("== values that may be wrong ==")));
        }

        [Fact]
        public void A_frame_whose_values_all_read_cleanly_says_that_outright()
        {
            var text = Render.Frame(At(new VarNode { Name = "total", Value = "220", Type = "int" })).Text;

            Assert.Contains("read cleanly", text);
        }

        /// <summary>
        /// A short list is otherwise a complete one, and the note belongs with the
        /// section it is about rather than collected at the bottom where it reads as
        /// belonging to whatever came last.
        /// </summary>
        [Fact]
        public void A_capped_scope_says_how_many_there_were_beside_that_scope()
        {
            var report = At(new VarNode { Name = "a", Value = "1" });
            report.Sections[0].Note = "312 were in scope and the first 40 are shown; " +
                                     "vars(scope: \"locals\") reads the rest.";

            var text = Render.Frame(report).Text;
            var locals = text.IndexOf("== locals ==");

            Assert.Contains("312 were in scope", text);
            Assert.True(text.IndexOf("312 were in scope") > locals);
            Assert.True(text.IndexOf("312 were in scope") < text.IndexOf("== values that may be wrong =="));
        }

        [Fact]
        public void This_is_shown_when_the_frame_has_one()
        {
            var report = At();
            report.Sections.Add(new VarSection
            {
                Name = "this",
                Nodes = new List<VarNode> { new VarNode { Name = "this", Value = "0x1", Type = "AActor *" } }
            });

            var text = Render.Frame(report).Text;

            Assert.Contains("== this ==", text);
            Assert.Contains("0x1", text);
        }

        [Fact]
        public void A_free_function_says_nothing_about_this()
        {
            Assert.DoesNotContain("== this ==", Render.Frame(At()).Text);
        }
    }
}
