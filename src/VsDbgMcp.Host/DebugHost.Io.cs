using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using VsDbgMcp.Contracts;
using Task = System.Threading.Tasks.Task;

namespace VsDbgMcp.Host
{
    partial class DebugHost
    {
        public Task<ConsoleResult> ConsoleReadAsync(int tailLines, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var pid = FirstDebuggedPid();
            if (pid == 0)
                return new ConsoleResult { Error = "Nothing is being debugged, so there is no console to read." };

            var result = ConsoleBridge.Read(pid);
            if (!string.IsNullOrEmpty(result.Error) || tailLines <= 0) return result;

            var lines = (result.Text ?? "").Split('\n');
            if (lines.Length > tailLines)
                result.Text = string.Join("\n", lines.Skip(lines.Length - tailLines));

            return result;
        });

        public Task<OpResult> ConsoleSendAsync(string text, string keys, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var pid = FirstDebuggedPid();
            if (pid == 0) return OpResult.Bad("Nothing is being debugged.");

            return ConsoleBridge.Send(pid, text, keys);
        });

        int FirstDebuggedPid()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                foreach (EnvDTE.Process process in _dte.Debugger.DebuggedProcesses)
                    return process.ProcessID;
            }
            catch
            {
            }
            return 0;
        }

        public Task<OutputResult> OutputReadAsync(string pane, string pattern, int tailLines, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return ReadPane(pane, pattern, tailLines);
        });

        OutputResult ReadPane(string pane, string pattern, int tailLines)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new OutputResult { Pane = pane };

            // Reading a pane walks the output window's text buffer, which the shell
            // refuses while it is busy writing to it.
            try
            {
                var window = _dte.ToolWindows.OutputWindow;
                OutputWindowPane target = null;

                foreach (OutputWindowPane candidate in window.OutputWindowPanes)
                {
                    if (candidate.Name.IndexOf(pane ?? "Debug", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    target = candidate;
                    break;
                }

                if (target == null)
                {
                    var names = window.OutputWindowPanes.Cast<OutputWindowPane>().Select(p => p.Name);
                    result.Text = "No pane matching '" + pane + "'. Available: " + string.Join(", ", names);
                    return result;
                }

                var document = target.TextDocument;
                var selection = document.Selection;
                selection.StartOfDocument(false);
                selection.EndOfDocument(true);
                var text = selection.Text ?? "";
                selection.StartOfDocument(false);

                var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    Regex regex;
                    try { regex = new Regex(pattern, RegexOptions.IgnoreCase); }
                    catch (Exception ex) { result.Text = "Bad pattern: " + ex.Message; return result; }
                    lines = lines.Where(l => regex.IsMatch(l)).ToList();
                }

                if (tailLines > 0 && lines.Count > tailLines)
                {
                    lines = lines.Skip(lines.Count - tailLines).ToList();
                    result.Truncated = true;
                }

                result.Lines = lines.Count;
                result.Text = string.Join("\n", lines).Trim();
                return result;
            }
            catch (Exception ex)
            {
                result.Text = "Could not read the pane: " + ex.Message;
                return result;
            }
        }

        public Task<CaptureResult> CaptureAsync(int[] region, CancellationToken ct = default) => UIAsync(() =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var pid = FirstDebuggedPid();
            if (pid == 0) return new CaptureResult { Error = "Nothing is being debugged." };

            return WindowCapture.Capture(pid, region);
        });
    }
}
