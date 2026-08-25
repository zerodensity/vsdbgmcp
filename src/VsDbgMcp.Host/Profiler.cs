using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

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

        string _session;
        string _output;
        DateTime _started;

        public bool Running => _output != null;

        Profiler(string collector, string agent)
        {
            _collector = collector;
            _agent = agent;
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
        public string Start(int pid, string outputPath)
        {
            _session = Guid.NewGuid().ToString();
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

            var failure = Run("start " + _session + " /attach:" + pid + " /loadAgent:" + _agent);
            if (failure == null) return null;

            _output = null;
            return failure;
        }

        /// <summary>Stops collecting, or says why it did not stop.</summary>
        public string Stop(out double seconds, out string path)
        {
            seconds = (DateTime.UtcNow - _started).TotalSeconds;
            path = _output;
            _output = null;

            if (path == null) return "Nothing is being profiled.";

            var failure = Run("stop " + _session + " /output:\"" + path + "\"");
            if (failure != null) return failure;

            if (File.Exists(path)) return null;

            return "The collector reported no error and wrote nothing. A collection that is stopped the " +
                   "moment it starts can end with nothing in it; let the program run for a few seconds " +
                   "between profile_start and profile_stop.";
        }

        /// <summary>Gives up on a collection without keeping what it gathered.</summary>
        public void Abandon()
        {
            if (_output == null) return;

            var path = _output;
            _output = null;
            Run("stop " + _session + " /output:\"" + path + "\"");
            Delete(path);
        }

        public static void Delete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Null when the collector was happy, otherwise what it said about not being.</summary>
        string Run(string arguments)
        {
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
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    var errors = process.StandardError.ReadToEnd();

                    // Far longer than starting or stopping takes. It is here so that a
                    // collector which never returns cannot take the editor with it.
                    if (!process.WaitForExit(120000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        return "The collector did not answer within two minutes.";
                    }

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
