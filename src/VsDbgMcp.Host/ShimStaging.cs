using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Copies the bundled shim out to a stable path so an agent can be pointed at it.
    ///
    /// The agent's configuration names the shim by absolute path, once, globally. That
    /// path cannot be inside the extension: Visual Studio installs extensions into a
    /// folder it regenerates on every update, so the configuration would break at the
    /// next version. It goes to %LOCALAPPDATA%\vsdbgmcp\bin instead, which survives
    /// updates and is shared by every Visual Studio on the machine.
    /// </summary>
    static class ShimStaging
    {
        const string SupersededSuffix = ".superseded";

        /// <summary>
        /// Stages the bundled shim if it is newer than what is already there. Returns
        /// what happened, for the log.
        /// </summary>
        public static string Run()
        {
            var source = Path.Combine(
                Path.GetDirectoryName(typeof(ShimStaging).Assembly.Location) ?? "", "shim");
            return Run(source, Names.ShimDir);
        }

        internal static string Run(string source, string target)
        {
            source = Path.GetFullPath(source);
            target = Path.GetFullPath(target);
            // All VS windows share this destination. Do not interleave publication
            // and process retirement with another window's staging pass.
            using (var hash = SHA256.Create())
            using (var mutex = new Mutex(false, @"Local\vsdbgmcp-stage-" +
                BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))))
            {
                bool entered;
                try { entered = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { entered = true; }
                if (!entered) return "shim: another window is staging this installation; reopen this window if it fails";
                try { return Stage(source, target); }
                catch (IOException ex) { return "shim: staging failed; existing processes were not stopped. " + ex.Message; }
                catch (UnauthorizedAccessException ex) { return "shim: staging failed; existing processes were not stopped. " + ex.Message; }
                catch (Win32Exception ex) { return "shim: staging failed; existing processes were not stopped. " + ex.Message; }
                finally { mutex.ReleaseMutex(); }
            }
        }

        static string Stage(string source, string target)
        {
            var sourceExe = Path.Combine(source, Names.Product + ".exe");
            var targetExe = Path.Combine(target, Names.Product + ".exe");

            if (!File.Exists(sourceExe))
                return "shim: not bundled with this build, nothing to stage";

            var bundled = VersionOf(sourceExe);
            var staged = VersionOf(targetExe);

            if (bundled == null) return "shim: bundled executable has no readable version; staging skipped";

            if (staged != null && staged >= bundled)
            {
                Prune(target);
                return "shim: " + targetExe + " is already " + staged;
            }

            // The executable goes last, and only once everything it runs on is in place.
            // Until it does, an agent launching mid-copy gets either the previous shim or
            // nothing, never a half-written one.
            //
            // It is also the file this staging is judged by: the version beside it is
            // what the check above reads next time. Writing a new executable next to
            // files that would not copy leaves a folder that claims to be current and is
            // not, and no later start will disagree, so the whole thing waits instead.
            var copied = 0;
            var locked = 0;
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, sourceExe, StringComparison.OrdinalIgnoreCase)) continue;
                if (Copy(file, Path.Combine(target, Relative(source, file)))) copied++;
                else locked++;
            }

            if (locked > 0)
            {
                return "shim: " + locked + " of " + (copied + locked) + " files are still in use, so " +
                       targetExe + " was left at " + (staged == null ? "nothing" : staged.ToString()) +
                       " rather than claiming " + bundled + ". Restart the agent holding them and " +
                       "reopen this window.";
            }

            Directory.CreateDirectory(target);
            var incoming = targetExe + ".incoming-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(sourceExe, incoming);
                string previous = null;
                if (File.Exists(targetExe) && !MoveAside(targetExe, out previous))
                    return "shim: could not replace " + targetExe + "; existing processes were not stopped";

                // No new process can launch the old executable at the configured path
                // now. Capture handles before publishing so a freshly launched new
                // shim cannot be mistaken for an old one, even if a PID is reused.
                ShimProcesses previousProcesses = null;
                try
                {
                    previousProcesses = ShimProcesses.Capture(targetExe);
                    File.Move(incoming, targetExe);
                }
                catch
                {
                    previousProcesses?.Dispose();
                    if (previous != null && !File.Exists(targetExe)) File.Move(previous, targetExe);
                    throw;
                }
                using (previousProcesses)
                {
                    var retirement = previousProcesses.Stop();
                    Prune(target);
                    return "shim: staged " + bundled + " to " + targetExe + " (" + (copied + 1) +
                           " files). " + retirement;
                }
            }
            finally
            {
                try { File.Delete(incoming); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        static string Relative(string root, string file) =>
            file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);

        static Version VersionOf(string exe)
        {
            if (!File.Exists(exe)) return null;
            Version parsed;
            return Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out parsed)
                ? parsed
                : null;
        }

        static bool Copy(string source, string target)
        {
            var dir = Path.GetDirectoryName(target);
            try
            {
                Directory.CreateDirectory(dir);
                File.Copy(source, target, true);
                return true;
            }
            catch (IOException)
            {
                // Almost always the shim is running and Windows will not overwrite a
                // loaded image. It can be renamed while running, though, so move it out
                // of the way. Processes are stopped only after publication succeeds.
                if (!MoveAside(target, out _)) return false;
            }
            catch (UnauthorizedAccessException)
            {
                if (!MoveAside(target, out _)) return false;
            }

            try
            {
                File.Copy(source, target, true);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static bool MoveAside(string target, out string moved)
        {
            moved = null;
            for (var n = 0; n < 100; n++)
            {
                var aside = target + SupersededSuffix + (n == 0 ? "" : "-" + n);
                if (File.Exists(aside)) continue;
                try
                {
                    File.Move(target, aside);
                    moved = aside;
                    return true;
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
            return false;
        }

        /// <summary>Clears what earlier upgrades moved aside, now that it is free.</summary>
        static void Prune(string dir)
        {
            if (!Directory.Exists(dir)) return;

            try
            {
                foreach (var stale in Directory.GetFiles(dir, "*" + SupersededSuffix + "*"))
                {
                    try { File.Delete(stale); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
