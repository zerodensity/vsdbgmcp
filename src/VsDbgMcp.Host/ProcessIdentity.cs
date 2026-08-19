using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Which process a thread or program belongs to.
    ///
    /// A debug session routinely holds several - a launcher and what it starts, a host
    /// and its workers - and a thread id means nothing to a caller who cannot tell which
    /// process it came from.
    /// </summary>
    struct ProcessIdentity
    {
        public int Pid;
        public string Name;

        public bool Known => Pid != 0 || !string.IsNullOrEmpty(Name);

        public string Describe() =>
            string.IsNullOrEmpty(Name) ? (Pid == 0 ? "(unknown process)" : Pid.ToString())
            : Pid == 0 ? Name
            : Name + " (" + Pid + ")";

        /// <summary>Matches a pid written as text, or any part of the process name.</summary>
        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;
            query = query.Trim();

            if (int.TryParse(query, out var pid)) return pid == Pid;

            return !string.IsNullOrEmpty(Name) &&
                   Name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static ProcessIdentity Of(IDebugThread2 thread)
        {
            if (thread == null) return default(ProcessIdentity);
            return thread.GetProgram(out var program) == VSConstants.S_OK ? Of(program) : default(ProcessIdentity);
        }

        public static ProcessIdentity Of(IDebugProgram2 program)
        {
            var identity = default(ProcessIdentity);
            if (program == null) return identity;

            if (program.GetProcess(out var process) != VSConstants.S_OK || process == null)
            {
                // A program that cannot name its process can still name itself.
                if (program.GetName(out var programName) == VSConstants.S_OK) identity.Name = Trim(programName);
                return identity;
            }

            var ids = new AD_PROCESS_ID[1];
            if (process.GetPhysicalProcessId(ids) == VSConstants.S_OK) identity.Pid = (int)ids[0].dwProcessId;
            if (process.GetName(enum_GETNAME_TYPE.GN_FILENAME, out var name) == VSConstants.S_OK)
                identity.Name = Trim(name);

            return identity;
        }

        static string Trim(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return System.IO.Path.GetFileName(path); }
            catch { return path; }
        }
    }
}
