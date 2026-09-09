using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Visual Studio's own sampling collector, driven from the command line.
    ///
    /// This is the machinery behind the Performance Profiler window. It collects
    /// without elevation and against a process the debugger is already attached to,
    /// which is the only reason profiling can happen in the middle of a debug session
    /// rather than instead of one.
    ///
    /// Nothing here reads the result. The collector writes tens of megabytes for a few
    /// seconds of a small program, and parsing that is the shim's job, in the shim's
    /// process, because the alternative is spending it inside the editor.
    /// </summary>
    sealed class Profiler
    {
        readonly string _collector;
        readonly string _agent;
        readonly Func<string, Task<string>> _runCommand;

        string _session;
        string _output;
        DateTime _started;

        public bool Running => _output != null;

        internal Profiler(string collector, string agent, Func<string, Task<string>> runCommand = null)
        {
            _collector = collector;
            _agent = agent;
            _runCommand = runCommand;
        }

        /// <summary>
        /// The collector beside the Visual Studio that is running this, or null with a
        /// reason. It arrives with the profiling tools, so an installation without those
        /// does not have one.
        /// </summary>
        public static Profiler Find(string devenvPath, out string error)
        {
            error = null;

            var ide = Path.GetDirectoryName(devenvPath ?? "");
            var root = string.IsNullOrEmpty(ide) ? null : Path.GetDirectoryName(Path.GetDirectoryName(ide));
            var collector = root == null
                ? null
                : Path.Combine(root, "Team Tools", "DiagnosticsHub", "Collector", "VSDiagnostics.exe");

            if (collector == null || !File.Exists(collector))
            {
                error = "This Visual Studio has no diagnostics collector" +
                        (collector == null ? "." : " at " + collector + ".") +
                        " It comes with the profiling tools, so installing those adds it.";
                return null;
            }

            var agent = AgentId(Path.GetDirectoryName(collector), out error);
            return agent == null ? null : new Profiler(collector, agent);
        }

        /// <summary>
        /// The CPU sampling agent, named the way the collector's command line wants it.
        ///
        /// The id is read out of the shipped configuration rather than handing the
        /// collector that configuration to load, because loading it goes through an
        /// assembly binding that is broken on at least one Visual Studio build and
        /// throws before the session starts. Naming the agent skips the JSON entirely.
        /// </summary>
        static string AgentId(string collectorDir, out string error)
        {
            error = null;
            var config = Path.Combine(collectorDir, "AgentConfigs", "CpuUsageBase.json");

            if (!File.Exists(config))
            {
                error = "The CPU sampling agent's configuration is not at " + config + ".";
                return null;
            }

            string text;
            try
            {
                text = File.ReadAllText(config);
            }
            catch (IOException ex)
            {
                error = "Could not read " + config + ": " + ex.Message;
                return null;
            }

            var clsid = Match(text, "CLSID");
            var name = Match(text, "Name");

            if (clsid == null || name == null)
            {
                error = "Could not find the CPU sampling agent in " + config + ".";
                return null;
            }

            return clsid + ";" + name;
        }

        static string Match(string text, string key)
        {
            var found = Regex.Match(text, "\"" + key + "\"\\s*:\\s*\"([^\"]+)\"");
            return found.Success ? found.Groups[1].Value : null;
        }

        /// <summary>
        /// Starts collecting against a process, and returns null when it started.
        ///
        /// The session is named with a fresh guid rather than a number. The collector
        /// takes either, but a number has to be between 0 and 255, and anything worked
        /// out from a process id lands outside that range and is refused - while two
        /// windows picking numbers independently would eventually pick the same one.
        /// </summary>
        public async Task<string> StartAsync(int pid, string outputPath, string sessionId)
        {
            _session = sessionId;
            _output = outputPath;
            _started = DateTime.UtcNow;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            }
            catch (IOException ex)
            {
                _output = null;
                return ex.Message;
            }

            var failure = await RunAsync("start " + _session + " /attach:" + pid + " /loadAgent:" + _agent).ConfigureAwait(false);
            if (failure == null) return null;

            // A timeout does not establish that start failed. Preserve ownership for an explicit stop.
            return failure;
        }

        /// <summary>Stops collecting, or says why it did not stop.</summary>
        public void RecoverSession(string sessionId, string path, DateTime started)
        {
            if (!Guid.TryParse(sessionId, out var ignored)) throw new ArgumentException("Invalid retained collector session ID.");
            if (Running) throw new InvalidOperationException("A collector is already active.");
            _session = sessionId; _output = path; _started = started;
        }

        public sealed class StopResult
        {
            public double Seconds;
            public string Path;
            public string Error;
        }
        public async Task<StopResult> StopAsync()
        {
            var result = new StopResult { Seconds = (DateTime.UtcNow - _started).TotalSeconds, Path = _output };
            if (_output == null) { result.Error = "Nothing is being profiled."; return result; }
            if (ProfileArtifacts.HasClosedTrace(_output)) { _output = null; return result; }
            result.Error = await RunAsync("stop " + _session + " /output:\"" + _output + "\"").ConfigureAwait(false);
            if (result.Error != null && ProfileArtifacts.HasClosedTrace(_output)) result.Error = null;
            if (result.Error == null && !File.Exists(_output)) result.Error = "Collector returned without a trace; collection coverage unknown.";
            if (result.Error == null) _output = null;
            return result;
        }

        public static void Delete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Null when the collector was happy, otherwise what it said about not being.</summary>
        async Task<string> RunAsync(string arguments)
        {
            if (_runCommand != null) return await _runCommand(arguments).ConfigureAwait(false);
            var start = new ProcessStartInfo(_collector, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_collector)
            };

            try
            {
                using (var process = new Process { StartInfo = start, EnableRaisingEvents = true })
                {
                    var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    process.Exited += (_, __) => exited.TrySetResult(true);
                    process.Start();
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    var finished = Task.WhenAll(exited.Task, outputTask, errorTask);
                    if (await Task.WhenAny(finished, Task.Delay(120000)).ConfigureAwait(false) != finished)
                    {
                        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                        _ = finished.ContinueWith(t => { var ignored = t.Exception; }, System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        return "The collector did not finish within two minutes; collection state unknown.";
                    }
                    await finished.ConfigureAwait(false);
                    var output = await outputTask.ConfigureAwait(false);
                    var errors = await errorTask.ConfigureAwait(false);
                    if (process.ExitCode == 0 && !Complained(output + errors)) return null;
                    return Tidy(output + " " + errors);
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// The collector prints some of its complaints and still exits zero, so the exit
        /// code alone would report a session that never started as a session in progress.
        /// </summary>
        static bool Complained(string output)
        {
            foreach (var complaint in Complaints)
            {
                if (output.IndexOf(complaint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// What the collector says when it has not done what was asked. It says some of
        /// these and still exits zero, and one of them - an id it will not accept - was
        /// read as a session that had started, so the failure only surfaced later as a
        /// collection that wrote nothing.
        /// </summary>
        static readonly string[] Complaints =
        {
            "error", "invalid", "failed", "cannot", "unable", "does not exist", "Exception"
        };

        static string Tidy(string text)
        {
            var kept = new List<string>();

            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("Microsoft (R)", StringComparison.Ordinal)) continue;

                kept.Add(trimmed);
                if (kept.Count == 4) break;
            }

            return kept.Count == 0 ? "the collector gave no reason" : string.Join(" ", kept.ToArray());
        }
    }
}
