using System;
using System.Collections.Generic;
using System.Globalization;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// Allocators write recognisable bytes over memory they hand out and memory they take
    /// back, so a value read from a dead object says what happened to it: 0xdd is a block
    /// the debug CRT has already freed, 0xcd is one that was never written. Knowing that
    /// is what turns "some pointer is bad" into "this object was deleted twice", and it
    /// is exactly the kind of table nobody should have to keep in their head.
    ///
    /// Matching is strict on purpose. Saying memory was freed when it was not sends the
    /// reader somewhere false, which is worse than saying nothing, so only a whole number
    /// that is nothing but the fill counts: 0xdddddddddddddddd is a freed pointer,
    /// 0x00000000000000dd is the number 221.
    /// </summary>
    public static class FillPatterns
    {
        sealed class Pattern
        {
            public Pattern(string label, string meaning)
            {
                Label = label;
                Meaning = meaning;
                Note = label + " " + meaning;
            }

            /// <summary>The fill itself, for a line that has to name which bytes it means.</summary>
            public string Label { get; }

            /// <summary>What the fill says happened to the memory.</summary>
            public string Meaning { get; }

            /// <summary>What to print beside a value, where the fill is not named otherwise.</summary>
            public string Note { get; }
        }

        /// <summary>
        /// Fills that are one byte repeated, so they read the same at every width.
        /// </summary>
        static readonly Dictionary<int, Pattern> ByteFills = new Dictionary<int, Pattern>
        {
            { 0xcd, new Pattern("0xcd", "uninitialized heap (debug CRT)") },
            { 0xdd, new Pattern("0xdd", "freed heap (debug CRT)") },
            { 0xfd, new Pattern("0xfd", "heap guard bytes (debug CRT)") },

            // The fence byte the debug CRT writes is 0xfd. 0xfe guards blocks as well but
            // comes from elsewhere, so its note stops short of naming a runtime.
            { 0xfe, new Pattern("0xfe", "heap guard bytes") },
            { 0xcc, new Pattern("0xcc", "uninitialized stack, or int 3 padding") },
            { 0xab, new Pattern("0xab", "past the end of a HeapAlloc block") },
        };

        /// <summary>
        /// Fills that only make sense four bytes at a time. A wider value holds the same
        /// constant again, and in a memory dump they read as a repeating group rather than
        /// a run of one byte.
        /// </summary>
        static readonly Dictionary<uint, Pattern> WordFills = new Dictionary<uint, Pattern>
        {
            { 0xbaadf00d, new Pattern("0xbaadf00d", "uninitialized LocalAlloc") },
            { 0xfeeefeee, new Pattern("0xfeeefeee", "freed by HeapFree") },
        };

        /// <summary>
        /// The same fills written out in decimal, because that is how the debugger prints
        /// an integer: an uninitialized int reads -842150451, not 0xcdcdcdcd. Only the
        /// exact 32- and 64-bit renderings are here, signed and unsigned, so the lookup is
        /// a whole-token comparison and cannot fire on part of a number.
        /// </summary>
        static readonly Dictionary<string, Pattern> DecimalFills = BuildDecimalFills();

        static Dictionary<string, Pattern> BuildDecimalFills()
        {
            var map = new Dictionary<string, Pattern>(StringComparer.Ordinal);
            foreach (var fill in ByteFills) AddDecimals(map, (uint)fill.Key * 0x01010101u, fill.Value);
            foreach (var fill in WordFills) AddDecimals(map, fill.Key, fill.Value);
            return map;
        }

        static void AddDecimals(Dictionary<string, Pattern> map, uint word, Pattern pattern)
        {
            var wide = ((ulong)word << 32) | word;
            map[word.ToString(CultureInfo.InvariantCulture)] = pattern;
            map[((int)word).ToString(CultureInfo.InvariantCulture)] = pattern;
            map[wide.ToString(CultureInfo.InvariantCulture)] = pattern;
            map[((long)wide).ToString(CultureInfo.InvariantCulture)] = pattern;
        }

        /// <summary>
        /// A run shorter than this is a coincidence, not a fill. Eight bytes is one pointer
        /// slot, which is the smallest region worth pointing at, and no live data holds
        /// eight of these bytes in a row by accident.
        /// </summary>
        const int ShortestRun = 8;

        /// <summary>Enough runs to show where the dead regions are without burying the dump.</summary>
        const int MostRunsShown = 8;

        /// <summary>
        /// What the fills in a rendered value mean, in the order they appear and each one
        /// only once.
        ///
        /// A value from the debugger is often a whole structure - 0x20c9721d740
        /// {sem=0xdddddddddddddddd {...}} - so every number in it is tested, but each one
        /// only as a whole. Digits that merely contain the fill are left alone.
        /// </summary>
        public static List<string> Notes(string text)
        {
            var notes = new List<string>();
            if (string.IsNullOrEmpty(text)) return notes;

            var i = 0;
            while (i < text.Length)
            {
                if (!IsTokenChar(text[i])) { i++; continue; }

                var start = i;
                while (i < text.Length && IsTokenChar(text[i])) i++;

                // A minus sign belongs to the number unless something ran into it, which
                // would make it a subtraction rather than a negative value.
                if (start > 0 && text[start - 1] == '-' && (start == 1 || !IsTokenChar(text[start - 2])))
                    start--;

                var pattern = Match(text.Substring(start, i - start));
                if (pattern != null && !notes.Contains(pattern.Note)) notes.Add(pattern.Note);
            }
            return notes;
        }

        /// <summary>
        /// Which parts of a hex dump are nothing but filler, and what that filler means.
        ///
        /// One line per filled region, because the fact worth having is which part of the
        /// block is dead - not that byte 41 happens to be 0xdd. Anything that is not the
        /// plain two-digits-per-byte dump the reader is looking at is left alone, since
        /// guessing offsets out of some other layout would put the note in the wrong place.
        /// </summary>
        public static List<string> Runs(string hex)
        {
            var lines = new List<string>();
            var bytes = ReadDump(hex);
            if (bytes == null) return lines;

            var more = 0;
            var i = 0;
            while (i < bytes.Length)
            {
                var end = RunEnd(bytes, i, out var pattern);
                if (end == i) { i++; continue; }

                if (lines.Count < MostRunsShown)
                    lines.Add("bytes " + i + "-" + (end - 1) + ": " + pattern.Label + " repeated -- " + pattern.Meaning);
                else
                    more++;
                i = end;
            }

            if (more > 0) lines.Add("... and " + more + (more == 1 ? " more filled run" : " more filled runs"));
            return lines;
        }

        /// <summary>
        /// Where the filled run starting at this byte ends, or the start itself when there
        /// is no run here. A repeating four-byte constant is checked as well as a run of
        /// one byte, because a freed Windows heap block reads ee fe ee fe and looks like
        /// nothing at all byte by byte.
        /// </summary>
        static int RunEnd(byte[] bytes, int start, out Pattern pattern)
        {
            if (ByteFills.TryGetValue(bytes[start], out pattern))
            {
                var end = start;
                while (end < bytes.Length && bytes[end] == bytes[start]) end++;
                if (end - start >= ShortestRun) return end;
            }

            if (start + ShortestRun <= bytes.Length)
            {
                var word = WordAt(bytes, start);
                if (WordFills.TryGetValue(word, out pattern))
                {
                    var end = start;
                    while (end + 4 <= bytes.Length && WordAt(bytes, end) == word) end += 4;
                    if (end - start >= ShortestRun) return end;
                }
            }

            pattern = null;
            return start;
        }

        static uint WordAt(byte[] bytes, int index)
        {
            return bytes[index]
                 | ((uint)bytes[index + 1] << 8)
                 | ((uint)bytes[index + 2] << 16)
                 | ((uint)bytes[index + 3] << 24);
        }

        /// <summary>
        /// The bytes of a hex dump, or null when it is not written as two digits per byte.
        /// </summary>
        static byte[] ReadDump(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;

            var bytes = new List<byte>();
            var i = 0;
            while (i < hex.Length)
            {
                if (char.IsWhiteSpace(hex[i])) { i++; continue; }

                if (i + 1 >= hex.Length) return null;
                var high = HexValue(hex[i]);
                var low = HexValue(hex[i + 1]);
                if (high < 0 || low < 0) return null;

                // Three digits in a row means the dump is grouped some other way, and the
                // byte offsets counted from here would not line up with what is on screen.
                if (i + 2 < hex.Length && HexValue(hex[i + 2]) >= 0) return null;

                bytes.Add((byte)((high << 4) | low));
                i += 2;
            }
            return bytes.Count == 0 ? null : bytes.ToArray();
        }

        /// <summary>
        /// The fill a single number is, or null. Hex has to be an even number of digits
        /// wide - two bytes at the very least, since one byte on its own carries no
        /// evidence of a pattern - and every byte of it has to be the fill.
        /// </summary>
        static Pattern Match(string token)
        {
            if (token.Length > 2 && token[0] == '0' && (token[1] == 'x' || token[1] == 'X'))
                return MatchHex(token, 2);

            // Every key here is a whole number written out, so a token that is not one
            // cannot be found.
            DecimalFills.TryGetValue(token, out var pattern);
            return pattern;
        }

        static Pattern MatchHex(string token, int start)
        {
            var digits = token.Length - start;
            if (digits < 4 || digits % 2 != 0) return null;
            for (var i = start; i < token.Length; i++)
                if (HexValue(token[i]) < 0) return null;

            var first = ByteOf(token, start);
            var sameByte = true;
            for (var i = start + 2; i < token.Length; i += 2)
                if (ByteOf(token, i) != first) { sameByte = false; break; }
            if (sameByte && ByteFills.TryGetValue(first, out var byteFill)) return byteFill;

            if (digits % 8 != 0) return null;

            var word = WordOf(token, start);
            for (var i = start + 8; i < token.Length; i += 8)
                if (WordOf(token, i) != word) return null;

            WordFills.TryGetValue(word, out var wordFill);
            return wordFill;
        }

        static int ByteOf(string token, int index)
            => (HexValue(token[index]) << 4) | HexValue(token[index + 1]);

        static uint WordOf(string token, int index)
        {
            var value = 0u;
            for (var i = index; i < index + 8; i++) value = (value << 4) | (uint)HexValue(token[i]);
            return value;
        }

        static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>
        /// What counts as part of a number. Letters and underscores are in so that a name
        /// with digits in it is read as one token and never mistaken for a value.
        /// </summary>
        static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
