using System;
using System.Diagnostics;
using System.IO;

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
            var sourceExe = Path.Combine(source, Names.Product + ".exe");

            if (!File.Exists(sourceExe))
                return "shim: not bundled with this build, nothing to stage";

            Prune(Names.ShimDir);

            var bundled = VersionOf(sourceExe);
            var staged = VersionOf(Names.ShimExe);

            if (staged != null && staged >= bundled)
                return "shim: " + Names.ShimExe + " is already " + staged;

            // The executable goes last. Until it does, an agent launching mid-copy gets
            // either the previous shim or nothing, never a half-written one.
            var copied = 0;
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, sourceExe, StringComparison.OrdinalIgnoreCase)) continue;
                copied += Copy(file, Path.Combine(Names.ShimDir, Relative(source, file))) ? 1 : 0;
            }
            copied += Copy(sourceExe, Names.ShimExe) ? 1 : 0;

            return "shim: staged " + bundled + " to " + Names.ShimExe + " (" + copied + " files)";
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
                // of the way and leave the copy that owns it to exit on its own.
                if (!MoveAside(target)) return false;
            }
            catch (UnauthorizedAccessException)
            {
                if (!MoveAside(target)) return false;
            }

            try
            {
                File.Copy(source, target, true);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static bool MoveAside(string target)
        {
            for (var n = 0; n < 100; n++)
            {
                var aside = target + SupersededSuffix + (n == 0 ? "" : "-" + n);
                if (File.Exists(aside)) continue;
                try
                {
                    File.Move(target, aside);
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

            foreach (var stale in Directory.GetFiles(dir, "*" + SupersededSuffix + "*"))
            {
                try { File.Delete(stale); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
