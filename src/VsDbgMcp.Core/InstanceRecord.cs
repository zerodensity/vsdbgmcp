using System;

namespace VsDbgMcp
{
    /// <summary>
    /// What a running Visual Studio publishes about itself, written to
    /// %LOCALAPPDATA%\vsdbgmcp\inst-&lt;pid&gt;.json.
    /// </summary>
    public sealed class InstanceRecord
    {
        public int Pid { get; set; }
        public string Pipe { get; set; }
        public string Token { get; set; }
        public string VsVersion { get; set; }
        public int Contract { get; set; }
        public WorkspaceInfo Workspace { get; set; }
        public string[] ProjectDirs { get; set; }
        public string[] Capabilities { get; set; }
        public string DebugMode { get; set; }
        public string StartedAt { get; set; }

        /// <summary>
        /// Short, human-typable, stable for the life of the process: "App#42696".
        /// </summary>
        public string Id
        {
            get
            {
                var name = Workspace != null && !string.IsNullOrEmpty(Workspace.Name)
                    ? Workspace.Name
                    : "vs";
                return name + "#" + Pid;
            }
        }
    }

    public sealed class WorkspaceInfo
    {
        /// <summary>sln, slnx, slnf, folder, or none.</summary>
        public string Kind { get; set; }

        /// <summary>
        /// The directory routing matches against. Never compare solution file paths;
        /// a directory holding both App.sln and App.slnx is one workspace, not two.
        /// </summary>
        public string Root { get; set; }

        /// <summary>Full path of whatever VS actually has open. May be null.</summary>
        public string File { get; set; }

        /// <summary>Solution filter path when the instance was opened through a .slnf.</summary>
        public string Filter { get; set; }

        /// <summary>Display name without extension, used to build the instance id.</summary>
        public string Name { get; set; }
    }

    public static class WorkspaceKind
    {
        public const string Sln = "sln";
        public const string Slnx = "slnx";
        public const string Slnf = "slnf";
        public const string Folder = "folder";
        public const string None = "none";
    }

    public static class DebugModes
    {
        public const string Design = "design";
        public const string Run = "run";
        public const string Break = "break";
    }
}
