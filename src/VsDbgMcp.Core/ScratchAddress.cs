using System;
using System.Globalization;

namespace VsDbgMcp
{
    /// <summary>
    /// Reading a pointer back out of what the expression evaluator printed.
    ///
    /// The evaluator hands a pointer back as display text and decorates it: a hex
    /// address, sometimes the object it points at in braces, sometimes a type in front.
    /// Only the address is wanted here, and one that cannot be read has to fail the
    /// allocation rather than be handed out, because a block whose address is wrong is
    /// memory nobody can free and a cast that lands anywhere.
    /// </summary>
    public static class ScratchAddress
    {
        /// <summary>
        /// The first address in the text, or false when there is none and when it is
        /// null. The allocator returning null is a failed allocation, not a block.
        /// </summary>
        public static bool TryParse(string value, out ulong address)
        {
            address = 0;
            if (string.IsNullOrEmpty(value)) return false;

            var at = value.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;

            var start = at + 2;
            var end = start;
            while (end < value.Length && Uri.IsHexDigit(value[end])) end++;
            if (end == start) return false;

            // More hex digits than a pointer has is not a pointer.
            if (end - start > 16) return false;

            return ulong.TryParse(value.Substring(start, end - start),
                       NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
                   && address != 0;
        }

        /// <summary>Written the way the evaluator takes one back.</summary>
        public static string Hex(ulong address) =>
            "0x" + address.ToString("x16", CultureInfo.InvariantCulture);

        /// <summary>
        /// A count the evaluator printed, for sizeof. It answers in decimal, but a
        /// caller who set a hex format would see the same number written the other way.
        /// </summary>
        public static bool TryCount(string value, out int count)
        {
            count = 0;
            if (string.IsNullOrEmpty(value)) return false;

            var text = value.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (!ulong.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                    return false;
                if (hex > int.MaxValue) return false;
                count = (int)hex;
                return count > 0;
            }

            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return false;
            if (number <= 0 || number > int.MaxValue) return false;

            count = (int)number;
            return true;
        }
    }
}
