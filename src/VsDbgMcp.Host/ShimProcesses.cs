using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace VsDbgMcp.Host
{
    /// <summary>Owns handles to shims from one installation, captured before publication.</summary>
    sealed class ShimProcesses : IDisposable
    {
        readonly List<Process> _processes = new List<Process>();
        int _unavailable;

        public static ShimProcesses Capture(string executable)
        {
            var result = new ShimProcesses();
            foreach (var process in Process.GetProcesses())
            {
                var keep = false;
                var candidate = false;
                try
                {
                    var name = process.ProcessName;
                    candidate = string.Equals(name, Names.Product, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(Names.Product + ".exe.superseded", StringComparison.OrdinalIgnoreCase);
                    if (!candidate) continue;
                    // Cache the OS handle first. Kill will use this same process
                    // identity, never a later occupant of its numeric PID.
                    var handle = process.Handle;
                    if (process.HasExited) continue;
                    var module = process.MainModule;
                    if (module == null)
                    {
                        result._unavailable++;
                        continue;
                    }
                    if (BelongsTo(module.FileName, executable))
                    {
                        result._processes.Add(process);
                        keep = true;
                    }
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { if (candidate) result._unavailable++; }
                finally { if (!keep) process.Dispose(); }
            }
            return result;
        }

        internal static bool BelongsTo(string image, string executable)
        {
            image = Path.GetFullPath(image);
            executable = Path.GetFullPath(executable);
            if (string.Equals(image, executable, StringComparison.OrdinalIgnoreCase)) return true;
            var prefix = executable + ".superseded";
            if (!image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            var suffix = image.Substring(prefix.Length);
            if (suffix.Length == 0) return true;
            if (suffix.Length < 2 || suffix[0] != '-') return false;
            for (var i = 1; i < suffix.Length; i++)
                if (suffix[i] < '0' || suffix[i] > '9') return false;
            return true;
        }

        public string Stop()
        {
            var pending = new List<Process>();
            var stopped = 0;
            var failed = 0;
            foreach (var process in _processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    process.Kill(); // Only this shim; never its parent or children.
                    pending.Add(process);
                }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { failed++; }
            }
            // Bound the total wait, not each process's wait.
            var clock = Stopwatch.StartNew();
            foreach (var process in pending)
            {
                try
                {
                    if (process.WaitForExit(Math.Max(0, 3000 - (int)clock.ElapsedMilliseconds))) stopped++;
                    else failed++;
                }
                catch (Win32Exception) { failed++; }
            }
            var message = "Stopped " + stopped + " previous shim process(es).";
            if (failed > 0) message += " Exit was not confirmed for " + failed + "; restart those MCP servers manually.";
            if (_unavailable > 0) message += " Could not inspect " + _unavailable + " shim process(es); restart affected MCP servers manually.";
            if (stopped > 0)
                message += " MCP clients must reconnect to launch the new version; restart their MCP server if they do not reconnect automatically.";
            return message;
        }

        public void Dispose()
        {
            foreach (var process in _processes) process.Dispose();
        }
    }
}
