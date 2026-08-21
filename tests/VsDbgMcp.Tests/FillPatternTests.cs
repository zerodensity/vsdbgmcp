using System;
using System.Collections.Generic;
using System.Text;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// What the reply says about allocator fill patterns, and - just as important - when
    /// it says nothing. A note claiming memory was freed when it was not would send the
    /// reader after the wrong bug, so the near misses here matter more than the hits:
    /// 0x00000000000000dd is the number 221 and has to stay that way.
    /// </summary>
    public class FillPatternTests
    {
        [Theory]
        [InlineData("0xdddddddddddddddd", "0xdd freed heap (debug CRT)")]
        [InlineData("0xdddddddd", "0xdd freed heap (debug CRT)")]
        [InlineData("0xdddd", "0xdd freed heap (debug CRT)")]
        [InlineData("0xcdcdcdcd", "0xcd uninitialized heap (debug CRT)")]
        [InlineData("0xCDCDCDCD", "0xcd uninitialized heap (debug CRT)")]
        [InlineData("0xcccc", "0xcc uninitialized stack, or int 3 padding")]
        [InlineData("0xcccccccccccccccc", "0xcc uninitialized stack, or int 3 padding")]
        [InlineData("0xfdfdfdfd", "0xfd heap guard bytes (debug CRT)")]
        [InlineData("0xfefefefefefefefe", "0xfe heap guard bytes")]
        [InlineData("0xabababab", "0xab past the end of a HeapAlloc block")]
        [InlineData("0xbaadf00d", "0xbaadf00d uninitialized LocalAlloc")]
        [InlineData("0xBaadF00d", "0xbaadf00d uninitialized LocalAlloc")]
        [InlineData("0xbaadf00dbaadf00d", "0xbaadf00d uninitialized LocalAlloc")]
        [InlineData("0xfeeefeee", "0xfeeefeee freed by HeapFree")]
        [InlineData("0xfeeefeeefeeefeee", "0xfeeefeee freed by HeapFree")]
        public void A_value_that_is_nothing_but_the_fill_is_named(string value, string note)
        {
            Assert.Equal(new[] { note }, FillPatterns.Notes(value));
        }

        [Theory]
        [InlineData("0x00000000000000dd")]   // the number 221, not a freed pointer
        [InlineData("0xdd")]                 // one byte carries no evidence of a pattern
        [InlineData("0xcc")]
        [InlineData("221")]
        [InlineData("0xddd")]                // not a whole number of bytes
        [InlineData("0xdddddddd0")]
        [InlineData("0xcdcdcdcd00000000")]   // the fill is only half of it
        [InlineData("0x00007ff6cdcdcdcd")]   // a real address that ends in the fill
        [InlineData("0x00000000baadf00d")]
        [InlineData("0xbaadf00d00000000")]
        [InlineData("0xbaadf00dfeeefeee")]   // two constants is neither of them
        [InlineData("0xdeadbeef")]
        [InlineData("0xffffffff")]
        [InlineData("0x0000000000000000")]
        [InlineData("dddddddd")]             // digits with no value in front of them
        [InlineData("0xdddddddg")]
        [InlineData("id0xdddddddd")]         // part of a name
        [InlineData("-842150452")]           // one off the uninitialized heap fill
        [InlineData("842150451")]
        public void A_value_that_merely_contains_the_fill_is_left_alone(string value)
        {
            Assert.Empty(FillPatterns.Notes(value));
        }

        [Theory]
        [InlineData("-842150451", "0xcd uninitialized heap (debug CRT)")]
        [InlineData("3452816845", "0xcd uninitialized heap (debug CRT)")]
        [InlineData("-572662307", "0xdd freed heap (debug CRT)")]
        [InlineData("-858993460", "0xcc uninitialized stack, or int 3 padding")]
        [InlineData("3131961357", "0xbaadf00d uninitialized LocalAlloc")]
        public void An_integer_printed_in_decimal_is_named_too(string value, string note)
        {
            Assert.Equal(new[] { note }, FillPatterns.Notes(value));
        }

        [Fact]
        public void A_negative_decimal_fill_is_read_as_one_number()
        {
            var wide = unchecked((long)0xccccccccccccccccUL).ToString();

            Assert.Equal(new[] { "0xcc uninitialized stack, or int 3 padding" }, FillPatterns.Notes(wide));
            Assert.Equal(new[] { "0xcd uninitialized heap (debug CRT)" }, FillPatterns.Notes("{ size=-842150451 }"));
        }

        [Fact]
        public void A_fill_inside_a_structure_is_found()
        {
            var value = "0x0000020c9721d740 {EventSemaphore=0xdddddddddddddddd {Handle=??? } }";

            Assert.Equal(new[] { "0xdd freed heap (debug CRT)" }, FillPatterns.Notes(value));
        }

        [Fact]
        public void One_note_per_fill_however_often_it_appears()
        {
            var value = "{a=0xdddddddddddddddd b=0xdddddddddddddddd c=0xcdcdcdcd}";

            Assert.Equal(
                new[] { "0xdd freed heap (debug CRT)", "0xcd uninitialized heap (debug CRT)" },
                FillPatterns.Notes(value));
        }

        [Fact]
        public void Nothing_is_said_about_ordinary_values()
        {
            Assert.Empty(FillPatterns.Notes("0x0000020c9721d740 {count=3 name=0x7ff6a1b2c3d4 \"mesh\"}"));
            Assert.Empty(FillPatterns.Notes(null));
            Assert.Empty(FillPatterns.Notes(""));
        }

        // ------------------------------------------------------------------ rendering

        [Fact]
        public void Vars_names_the_fill_beside_the_value()
        {
            var text = Render.Vars(new[]
            {
                new VarNode { Name = "sem", Value = "0xdddddddddddddddd", Type = "EventSemaphore *" }
            });

            Assert.Equal("  sem = 0xdddddddddddddddd  (EventSemaphore *)  -- 0xdd freed heap (debug CRT)", text);
        }

        [Fact]
        public void Vars_does_not_repeat_a_fill_the_line_above_already_named()
        {
            var text = Render.Vars(new[]
            {
                new VarNode
                {
                    Name = "event",
                    Value = "0x0000020c9721d740 {EventSemaphore=0xdddddddddddddddd {Handle=??? } }",
                    Children = new List<VarNode>
                    {
                        new VarNode
                        {
                            Name = "EventSemaphore",
                            Value = "0xdddddddddddddddd {Handle=??? }",
                            Children = new List<VarNode>
                            {
                                new VarNode { Name = "Handle", Value = "0xdddddddddddddddd" }
                            }
                        }
                    }
                }
            });

            Assert.Equal(1, Occurrences(text, "freed heap"));
            Assert.Contains("event = 0x0000020c9721d740", text.Split('\n')[0]);
            Assert.EndsWith("-- 0xdd freed heap (debug CRT)", text.Split('\n')[0].TrimEnd('\r'));
        }

        [Fact]
        public void Vars_names_the_fill_on_every_variable_that_shows_it()
        {
            var text = Render.Vars(new[]
            {
                new VarNode { Name = "first", Value = "0xdddddddddddddddd" },
                new VarNode { Name = "second", Value = "0xdddddddddddddddd" }
            });

            Assert.Equal(2, Occurrences(text, "freed heap"));
        }

        [Fact]
        public void A_note_never_adds_a_line_of_its_own()
        {
            var text = Render.Vars(new[]
            {
                new VarNode
                {
                    Name = "block",
                    Value = "0xcdcdcdcd",
                    Type = "int",
                    HasChildren = true,
                    Ref = "v7"
                }
            });

            Assert.Single(text.Split('\n'));
            Assert.Equal("  block = 0xcdcdcdcd  (int)  ... expand v7  -- 0xcd uninitialized heap (debug CRT)", text);
        }

        [Fact]
        public void Eval_names_the_fill_and_says_nothing_when_it_failed()
        {
            var hit = Render.Evals(new[]
            {
                new EvalResult { Expression = "event->sem", Value = "0xdddddddddddddddd", IsValid = true, Type = "void *" }
            });
            Assert.Equal("event->sem = 0xdddddddddddddddd  (void *)  -- 0xdd freed heap (debug CRT)", hit);

            var failed = Render.Evals(new[]
            {
                new EvalResult { Expression = "0xdddddddddddddddd", IsValid = false, Error = "no symbol" }
            });
            Assert.DoesNotContain("freed heap", failed);
        }

        [Fact]
        public void Eval_across_threads_names_the_fill_once_per_group()
        {
            var text = Render.Evals(new[]
            {
                new EvalResult { Expression = "p", Value = "0xdddddddddddddddd", IsValid = true, ThreadId = 11 },
                new EvalResult { Expression = "p", Value = "0xdddddddddddddddd", IsValid = true, ThreadId = 12 },
                new EvalResult { Expression = "p", Value = "0x7ff6a1b2c3d4", IsValid = true, ThreadId = 13 }
            });

            Assert.Equal(1, Occurrences(text, "freed heap"));
            Assert.Contains("0xdddddddddddddddd  -- 0xdd freed heap (debug CRT)   threads: 11, 12", text);
        }

        // -------------------------------------------------------------------- memory

        [Fact]
        public void Memory_says_which_region_is_filled_and_with_what()
        {
            var bytes = Live(96);
            for (var i = 32; i < 64; i++) bytes[i] = 0xdd;

            var text = Render.Memory(Read(bytes));

            Assert.Contains("bytes 32-63: 0xdd repeated -- freed heap (debug CRT)", text);
            Assert.Equal(1, Occurrences(text, "freed heap"));
        }

        [Fact]
        public void Memory_recognises_a_repeating_constant_that_is_no_run_of_one_byte()
        {
            var bytes = Live(48);
            for (var i = 16; i < 32; i += 4)
            {
                bytes[i] = 0xee; bytes[i + 1] = 0xfe; bytes[i + 2] = 0xee; bytes[i + 3] = 0xfe;
            }

            var text = Render.Memory(Read(bytes));

            Assert.Contains("bytes 16-31: 0xfeeefeee repeated -- freed by HeapFree", text);
        }

        [Fact]
        public void Memory_ignores_a_run_too_short_to_mean_anything()
        {
            var bytes = Live(64);
            for (var i = 8; i < 15; i++) bytes[i] = 0xdd;

            Assert.DoesNotContain("freed heap", Render.Memory(Read(bytes)));
        }

        [Fact]
        public void Memory_stays_quiet_about_a_dump_it_cannot_count_bytes_in()
        {
            Assert.Empty(FillPatterns.Runs("dddd dddd dddd dddd dddd dddd dddd dddd"));
            Assert.Empty(FillPatterns.Runs("0x00000000  dd dd dd dd dd dd dd dd"));
            Assert.Empty(FillPatterns.Runs(null));
        }

        [Fact]
        public void Memory_lists_a_few_regions_and_counts_the_rest()
        {
            var bytes = Live(320);
            for (var i = 0; i < 320; i += 16)
                for (var j = i; j < i + 8; j++) bytes[j] = 0xcd;

            var runs = FillPatterns.Runs(Hex(bytes));

            Assert.Equal(9, runs.Count);
            Assert.Equal("bytes 0-7: 0xcd repeated -- uninitialized heap (debug CRT)", runs[0]);
            Assert.Equal("bytes 112-119: 0xcd repeated -- uninitialized heap (debug CRT)", runs[7]);
            Assert.Equal("... and 12 more filled runs", runs[8]);
        }

        [Fact]
        public void Memory_reads_a_whole_block_of_one_fill_as_one_region()
        {
            var bytes = new byte[128];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = 0xcc;

            Assert.Equal(
                new[] { "bytes 0-127: 0xcc repeated -- uninitialized stack, or int 3 padding" },
                FillPatterns.Runs(Hex(bytes)));
        }

        // -------------------------------------------------------------------- helpers

        /// <summary>Bytes that look like data rather than filler.</summary>
        static byte[] Live(int length)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++) bytes[i] = (byte)('a' + i % 16);
            return bytes;
        }

        /// <summary>The dump exactly as the extension writes it: two digits a byte, sixteen a line.</summary>
        static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < bytes.Length; i++)
            {
                if (i > 0 && i % 16 == 0) sb.AppendLine();
                else if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        static MemoryResult Read(byte[] bytes)
            => new MemoryResult { Address = "0x0000020c9721d740", Length = bytes.Length, Hex = Hex(bytes) };

        static int Occurrences(string text, string needle)
        {
            var count = 0;
            var at = text.IndexOf(needle, StringComparison.Ordinal);
            while (at >= 0)
            {
                count++;
                at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
            }
            return count;
        }
    }
}
