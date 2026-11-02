using System;
using EnvDTE;
using Microsoft.VisualStudio.Shell;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// The end of an Output window pane, as text.
    ///
    /// Only the end is read. A pane a hot tracepoint or a long build has filled holds
    /// hundreds of thousands of lines, and reading all of it on the UI thread stalls
    /// Visual Studio and fills its memory. The text is read through an edit point, so
    /// the person's selection in the pane stays where it was.
    /// </summary>
    static class PaneTail
    {
        /// <summary>How many lines at the end of a pane one read covers.</summary>
        public const int Lines = 20000;

        /// <param name="firstLine">The first line read, counting from 1, when the pane is longer than one read covers; otherwise null.</param>
        public static string Read(OutputWindowPane pane, out int? firstLine)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var document = pane.TextDocument;
            var end = document.EndPoint;
            var from = Math.Max(1, end.Line - Lines + 1);
            firstLine = from > 1 ? from : (int?)null;

            var start = document.StartPoint.CreateEditPoint();
            start.MoveToLineAndOffset(from, 1);
            return start.GetText(end) ?? "";
        }
    }
}
