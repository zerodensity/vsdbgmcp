using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    public static class BuildDiagnostics
    {
        // MSVC, MSBuild and linker diagnostics. Unknown/custom output remains in the log.
        static readonly Regex Diagnostic = new Regex(@"^(?:(?<prefix>\d+)>)?(?:(?<file>.+?)\((?<line>\d+)(?:,(?<column>\d+))?\)|(?<tool>[^:]+))\s*:\s*(?:fatal\s+)?(?<severity>error|warning)\s*(?<code>[A-Za-z]+\d+)?\s*:\s*(?<text>.*)$", RegexOptions.IgnoreCase);

        public static List<BuildDiagnostic> Parse(string text, string id, string configuration, DateTime timestamp)
        {
            var result = new List<BuildDiagnostic>();
            foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var match = Diagnostic.Match(line.Trim());
                if (!match.Success) continue;
                int.TryParse(match.Groups["line"].Value, out var row);
                int.TryParse(match.Groups["column"].Value, out var col);
                result.Add(new BuildDiagnostic { OperationId = id, Configuration = configuration,
                    TimestampUtc = timestamp, Source = "build-output", Severity = match.Groups["severity"].Value.ToLowerInvariant(),
                    Code = match.Groups["code"].Value, File = match.Groups["file"].Value, Line = row, Column = col,
                    Text = match.Groups["text"].Value, Project = null });
            }
            return result;
        }
        public static string Key(BuildDiagnostic d) => string.Join("|", d.Severity, d.Code, d.File, d.Line, d.Column, d.Text, d.Project);
        public static int Unique(IEnumerable<BuildDiagnostic> diagnostics, string severity) =>
            diagnostics.Where(d => d.Severity == severity).Select(Key).Distinct(StringComparer.Ordinal).Count();
    }
}
