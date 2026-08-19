using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// What this Visual Studio has open, in the shape routing needs.
    ///
    /// The shell interfaces report failure through HRESULTs, so this checks return
    /// codes rather than guarding every call. Must be called on the UI thread.
    /// </summary>
    static class WorkspaceProbe
    {
        public static WorkspaceInfo Read(IVsSolution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var info = new WorkspaceInfo { Kind = WorkspaceKind.None };
            if (solution == null) return info;

            if (solution.GetSolutionInfo(out var dir, out var file, out _) != VSConstants.S_OK)
                return info;

            var filter = TryGetFilterPath(solution);

            if (!string.IsNullOrEmpty(filter))
            {
                info.Filter = filter;
                info.Kind = WorkspaceKind.Slnf;
                info.File = filter;
                info.Root = PathUtil.Normalize(Path.GetDirectoryName(filter));
                info.Name = Path.GetFileNameWithoutExtension(filter);
                return info;
            }

            if (!string.IsNullOrEmpty(file))
            {
                info.File = PathUtil.Normalize(file);
                info.Kind = PathUtil.KindOf(info.File);
                info.Root = PathUtil.Normalize(Path.GetDirectoryName(info.File));
                info.Name = Path.GetFileNameWithoutExtension(info.File);
                return info;
            }

            // Open Folder, or a solution that has not finished loading.
            if (!string.IsNullOrEmpty(dir))
            {
                info.Kind = WorkspaceKind.Folder;
                info.Root = PathUtil.Normalize(dir);
                info.Name = new DirectoryInfo(info.Root).Name;
            }

            return info;
        }

        /// <summary>
        /// Best effort only.
        ///
        /// A solution opened through a filter reports the .sln as its file, and this SDK
        /// exposes no property for the filter itself. Two windows holding the same
        /// solution under different filters therefore look alike here; routing still
        /// refuses to guess between them, it just cannot name the filter.
        /// </summary>
        static string TryGetFilterPath(IVsSolution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (solution.GetProperty((int)__VSPROPID.VSPROPID_SolutionFileName, out var value) != VSConstants.S_OK)
                return null;

            return value is string path && path.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
                ? PathUtil.Normalize(path)
                : null;
        }

        /// <summary>
        /// Directories of the loaded projects. Routing uses these as a second chance
        /// when the working directory sits inside a project but outside the solution
        /// directory, which is normal in repositories that keep solutions apart from code.
        /// </summary>
        public static string[] ReadProjectDirs(IVsSolution solution)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dirs = new List<string>();
            if (solution == null) return dirs.ToArray();

            var guid = Guid.Empty;
            if (solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref guid, out var enumerator)
                    != VSConstants.S_OK || enumerator == null)
            {
                return dirs.ToArray();
            }

            var hierarchies = new IVsHierarchy[1];
            while (enumerator.Next(1, hierarchies, out var fetched) == VSConstants.S_OK && fetched == 1)
            {
                if (!(hierarchies[0] is IVsProject project)) continue;
                if (project.GetMkDocument(VSConstants.VSITEMID_ROOT, out var mk) != VSConstants.S_OK) continue;
                if (string.IsNullOrEmpty(mk)) continue;

                var dir = PathUtil.Normalize(Path.GetDirectoryName(mk));
                if (string.IsNullOrEmpty(dir)) continue;
                if (dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
                dirs.Add(dir);
            }

            return dirs.ToArray();
        }

        public static string ReadVsVersion(IVsShell shell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (shell == null) return "unknown";
            if (shell.GetProperty((int)__VSSPROPID5.VSSPROPID_ReleaseVersion, out var value) != VSConstants.S_OK)
                return "unknown";

            return value as string ?? "unknown";
        }
    }
}
