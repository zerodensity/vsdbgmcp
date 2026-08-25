using VsDbgMcp;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// A scratch block is only as good as the address read back out of it. The
    /// evaluator hands pointers over as display text, and a misread address is memory
    /// nobody can free and a cast that lands somewhere else entirely.
    /// </summary>
    public class ScratchTests
    {
        [Theory]
        [InlineData("0x000001d4b2a0c150")]
        [InlineData("0x000001d4b2a0c150 {0x00000000}")]
        [InlineData("void * 0x000001d4b2a0c150")]
        public void The_address_is_read_out_of_whatever_the_evaluator_printed(string value)
        {
            Assert.True(ScratchAddress.TryParse(value, out var address));
            Assert.Equal(0x000001d4b2a0c150UL, address);
        }

        [Fact]
        public void An_allocator_that_returned_null_did_not_allocate()
        {
            Assert.False(ScratchAddress.TryParse("0x0000000000000000", out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("could not evaluate")]
        [InlineData("0x")]
        public void Nothing_that_is_not_an_address_is_taken_for_one(string value)
        {
            Assert.False(ScratchAddress.TryParse(value, out _));
        }

        [Fact]
        public void More_digits_than_a_pointer_has_is_not_a_pointer()
        {
            Assert.False(ScratchAddress.TryParse("0x00000000000000000001", out _));
        }

        [Fact]
        public void An_address_is_written_the_way_the_evaluator_takes_one_back()
        {
            Assert.Equal("0x000001d4b2a0c150", ScratchAddress.Hex(0x000001d4b2a0c150UL));
        }

        [Theory]
        [InlineData("64", 64)]
        [InlineData("  64  ", 64)]
        [InlineData("0x40", 64)]
        public void A_size_is_read_however_the_evaluator_was_asked_to_print_it(string value, int expected)
        {
            Assert.True(ScratchAddress.TryCount(value, out var count));
            Assert.Equal(expected, count);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-8")]
        [InlineData("incomplete type")]
        public void A_size_that_is_not_a_size_is_refused(string value)
        {
            Assert.False(ScratchAddress.TryCount(value, out _));
        }
    }
}
