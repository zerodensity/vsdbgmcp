using System;
using System.Collections.Generic;
using System.IO;

namespace VsDbgMcp
{
    /// <summary>One source line, and whether it is the one about to run.</summary>
    public sealed class SourceLine
    {
        public int Number { get; set; }
        public string Text { get; set; }
        public bool IsCurrent { get; set; }
    }

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
        /// The file's lines, or null when there is nothing readable at that path. A path
        /// from the debug engine can be anything, including something this machine will
        /// not resolve, and a module built elsewhere routinely names a file that is not
        /// here at all.
        /// </summary>
        public static List<string> Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                var lines = new List<string>();
                using (var reader = new StreamReader(path))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
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
        /// Two file times, the same pair iteration 1 settled on, and the wording says so
        /// rather than implying the PDB's checksums were compared.
        /// </summary>
        /// <param name="sourceIsNewer">
        /// Whether the file was written after the binary, or null when either time could
        /// not be read.
        /// </param>
        public static string Warning(bool? sourceIsNewer, string sourceWritten, string binaryBuilt)
        {
            if (sourceIsNewer == false) return null;

            if (sourceIsNewer == null)
            {
                return "Whether this file has been edited since the binary was built could not be " +
                       "checked here, so these lines are not established as the ones running.";
            }

            var times = string.IsNullOrEmpty(sourceWritten) || string.IsNullOrEmpty(binaryBuilt)
                ? ""
                : " (file " + sourceWritten + ", binary " + binaryBuilt + ")";

            return "This file has been written since the module was built" + times +
                   ", so these are not the lines the process is running. The line numbers the " +
                   "debugger reports are the binary's; the text beside them is this file's.";
        }

        static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var trimmed = text.TrimEnd();
            return trimmed.Length <= MaxLineLength ? trimmed : trimmed.Substring(0, MaxLineLength) + " ...";
        }
    }
}
