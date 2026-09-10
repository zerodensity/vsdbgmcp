using System;
using System.Collections;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Builds
{
    public sealed class BinaryBuildReport
    {
        public string Path { get; set; }
        public string Source { get; set; } = "MSBuild binary log events";
        public DateTime? StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        [Description("succeeded, failed, or unknown. Only a complete readable event stream establishes the build outcome.")]
        public string Outcome { get; set; } = "unknown";
        [Description("True only when build boundaries and events were read without replay errors. Text-only diagnostics are outside coverage even then.")]
        public bool EventStreamComplete { get; set; }
        [Description("Structured error occurrences in the entire file, before filtering or truncation.")]
        public int Errors { get; set; }
        [Description("Structured warning occurrences in the entire file, before filtering or truncation.")]
        public int Warnings { get; set; }
        public int UniqueWarnings { get; set; }
        public int MatchingDiagnostics { get; set; }
        public bool Truncated { get; set; }
        public List<BuildDiagnostic> Diagnostics { get; set; } = new List<BuildDiagnostic>();
        public string Error { get; set; }
        public string Coverage { get; set; } = "Counts cover structured warning/error events in this file. Text-only tool messages are excluded. This report does not establish ownership by a live VS operation.";
    }

    public static class BinaryBuildDiagnostics
    {
        public static BinaryBuildReport Read(string path, string project, string configuration, int maxDiagnostics, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("binlog must be an absolute path.");
            path = System.IO.Path.GetFullPath(path);
            if (!path.EndsWith(".binlog", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Give an MSBuild .binlog file.");
            var report = new BinaryBuildReport { Path = path };
            var projects = new Dictionary<string, (string Path, string Config)>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var limit = Math.Max(1, Math.Min(maxDiagnostics, 1000));
            var replay = new BinaryLogReplayEventSource { AllowForwardCompatibility = false };
            bool readError = false;
            replay.RecoverableReadError += _ => readError = true;
            replay.BuildStarted += (_, e) => report.StartedUtc = e.Timestamp.ToUniversalTime();
            replay.BuildFinished += (_, e) => { report.FinishedUtc = e.Timestamp.ToUniversalTime(); report.Outcome = e.Succeeded ? "succeeded" : "failed"; };
            replay.ProjectStarted += (_, e) =>
            {
                string config = null, platform = null;
                if (e.Properties != null)
                    foreach (DictionaryEntry entry in e.Properties)
                    {
                        if (string.Equals(entry.Key?.ToString(), "Configuration", StringComparison.OrdinalIgnoreCase)) config = entry.Value?.ToString();
                        if (string.Equals(entry.Key?.ToString(), "Platform", StringComparison.OrdinalIgnoreCase)) platform = entry.Value?.ToString();
                    }
                if (e.GlobalProperties != null)
                {
                    if (config == null) e.GlobalProperties.TryGetValue("Configuration", out config);
                    if (platform == null) e.GlobalProperties.TryGetValue("Platform", out platform);
                }
                projects[Key(e.BuildEventContext)] = (e.ProjectFile, config == null ? null : config + (platform == null ? "" : "|" + platform));
            };
            void Add(BuildEventArgs e, string severity, string code, string text, string file, int line, int column, string projectFile)
            {
                ct.ThrowIfCancellationRequested();
                projects.TryGetValue(Key(e.BuildEventContext), out var owner);
                var diagnostic = new BuildDiagnostic { Severity = severity, Code = code, Text = text, File = file,
                    Line = line, Column = column, Project = projectFile ?? owner.Path, Configuration = owner.Config,
                    TimestampUtc = e.Timestamp.ToUniversalTime(), Source = report.Source };
                if (severity == "error") report.Errors++;
                else { report.Warnings++; unique.Add(BuildDiagnostics.Key(diagnostic) + "|" + owner.Config); }
                if (!Matches(diagnostic.Project, project) || !Matches(diagnostic.Configuration, configuration)) return;
                report.MatchingDiagnostics++;
                if (report.Diagnostics.Count < limit) report.Diagnostics.Add(diagnostic);
            }
            replay.ErrorRaised += (_, e) => Add(e, "error", e.Code, e.Message, e.File, e.LineNumber, e.ColumnNumber, e.ProjectFile);
            replay.WarningRaised += (_, e) => Add(e, "warning", e.Code, e.Message, e.File, e.LineNumber, e.ColumnNumber, e.ProjectFile);
            try
            {
                // Replay emits events; it never evaluates projects or runs build tasks.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                replay.Replay(stream, ct);
                report.EventStreamComplete = !readError && report.StartedUtc != null && report.FinishedUtc != null;
                if (!report.EventStreamComplete) report.Error = "Build boundaries or events are missing; the log's outcome is not established.";
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { report.Error = ex.Message; }
            if (!report.EventStreamComplete) report.Outcome = "unknown";
            report.UniqueWarnings = unique.Count;
            report.Truncated = report.MatchingDiagnostics > report.Diagnostics.Count;
            return report;
        }

        static string Key(BuildEventContext context) => context == null ? "unknown" : context.NodeId + ":" + context.ProjectContextId;
        static bool Matches(string value, string filter) => string.IsNullOrWhiteSpace(filter) ||
            value?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
