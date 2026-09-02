using System;
using System.Collections.Generic;
using System.IO;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    /// <summary>
    /// The source around where a frame is stopped.
    ///
    /// Everything else in this server reports what the debugger said. This reports what
    /// a file on this machine says, which is a different kind of claim: a source edited
    /// since the binary was built still opens and still has a line 38, and printing it
    /// beside a frame is how somebody comes to reason about code the process is not
    /// running. So the window is never shown without the answer to that question beside
    /// it.
    /// </summary>
    public static class SourceWindow
    {
        /// <summary>How many lines either side. Enough for a statement in context.</summary>
        public const int Radius = 5;

        /// <summary>
        /// Longest line kept. A generated or minified file has lines that would fill a
        /// reply on their own.
        /// </summary>
        public const int MaxLineLength = 400;

        /// <summary>
        /// The lines around <paramref name="line"/>, nearest the start and end of the
        /// file where there are not enough either side. Empty when the file does not
        /// have that line, which is a frame whose source has moved rather than something
        /// to show anyway.
        /// </summary>
        public static List<SourceLine> Around(IReadOnlyList<string> lines, int line, int radius)
        {
            var window = new List<SourceLine>();
            if (lines == null || lines.Count == 0) return window;
            if (line < 1 || line > lines.Count) return window;

            var first = Math.Max(1, line - radius);
            var last = Math.Min(lines.Count, line + radius);

            for (var i = first; i <= last; i++)
            {
                window.Add(new SourceLine
                {
                    Number = i,
                    Text = Shorten(lines[i - 1]),
                    IsCurrent = i == line
                });
            }
            return window;
        }

        /// <summary>
        /// The file's lines up to <paramref name="upTo"/>, or null when there is nothing
        /// readable at that path. A path from the debug engine can be anything,
        /// including something this machine will not resolve, and a module built
        /// elsewhere routinely names a file that is not here at all.
        ///
        /// Reading stops at the last line anyone will be shown. A generated or
        /// amalgamated source runs to hundreds of thousands of lines, and keeping all of
        /// them to print eleven is a cost paid on the debugger's UI thread.
        /// </summary>
        public static List<string> Read(string path, int upTo)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                var lines = new List<string>();
                using (var reader = new StreamReader(path))
                {
                    string line;
                    while (lines.Count < upTo && (line = reader.ReadLine()) != null) lines.Add(line);
                }
                return lines;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }

        /// <summary>
        /// What to say about whether these lines are the ones running, or null when
        /// there is nothing to say.
        ///
        /// Two times, said as two times. A write time moves when nothing in the file
        /// changed - a checkout, a stash pop, a formatter, a save over identical text -
        /// so the pair is evidence and never proof, and the wording stops where the
        /// evidence does. Everywhere else in this server says the same about the same
        /// comparison; escalating it here would teach a reader to distrust source that
        /// is fine.
        /// </summary>
        /// <param name="sourceIsNewer">
        /// Whether the file was written after the binary, or null when either time could
        /// not be read.
        /// </param>
        /// <param name="binaryIsLinkStamp">
        /// True when the binary's time is the stamp in its image rather than the time
        /// its file was written. The two are different clocks, which is why they are
        /// compared at different margins, and the reader is told which one this was.
        /// </param>
        public static string Warning(bool? sourceIsNewer, string sourceWritten, string binaryBuilt,
            bool binaryIsLinkStamp)
        {
            if (sourceIsNewer == false) return null;

            if (sourceIsNewer == null)
            {
                return "Whether this file has been edited since the binary was built could not be " +
                       "checked here, so nothing establishes that these are the lines being run.";
            }

            var which = binaryIsLinkStamp ? "the module's image is stamped " : "its binary was written ";
            var times = string.IsNullOrEmpty(sourceWritten) || string.IsNullOrEmpty(binaryBuilt)
                ? ""
                : ": the file was written " + sourceWritten + " and " + which + binaryBuilt;

            return "This file may not be the source the running binary was built from" + times +
                   ". Two times are not proof the text differs - a checkout or a save over unchanged " +
                   "text moves one - but the line numbers beside these lines are the binary's, so " +
                   "rebuild before reading anything into which line is which." +
                   (binaryIsLinkStamp
                       ? " The binary's time here is the stamp in its image rather than a file time, " +
                         "and a build made reproducible puts a hash of the contents in that field, " +
                         "which is not a time at all."
                       : "");
        }

        static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var trimmed = text.TrimEnd();
            return trimmed.Length <= MaxLineLength ? trimmed : trimmed.Substring(0, MaxLineLength) + " ...";
        }
    }
}
