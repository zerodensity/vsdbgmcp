using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// Exits when the client that launched this process does.
    ///
    /// Closing stdin is the usual signal and the transport handles it, but it is not
    /// guaranteed: a client that is killed outright, or that replaces its own image
    /// during an update, can leave this process running with a pipe nobody will ever
    /// close. An orphan then holds its files open and keeps a connection to Visual
    /// Studio that nothing is driving.
    ///
    /// Watching the parent directly is the only signal that survives all of those.
    /// </summary>
    static class ParentWatch
    {
        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr process, int infoClass,
            ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        public static void ExitWhenParentDoes()
        {
            var parent = Parent();
            if (parent == null) return;

            try
            {
                parent.EnableRaisingEvents = true;
                parent.Exited += (s, e) => Environment.Exit(0);

                // It may already be gone by the time the handler is attached.
                if (parent.HasExited) Environment.Exit(0);
            }
            catch (InvalidOperationException)
            {
                // The process ended between finding it and watching it.
                Environment.Exit(0);
            }
        }

        static Process Parent()
        {
            var self = Process.GetCurrentProcess();

            var info = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(self.Handle, 0, ref info, Marshal.SizeOf(info), out _) != 0)
                return null;

            var parentId = info.InheritedFromUniqueProcessId.ToInt32();
            if (parentId <= 0) return null;

            Process parent;
            try { parent = Process.GetProcessById(parentId); }
            catch (ArgumentException) { return null; }

            // Process ids are reused. A "parent" that started after this process did is
            // some unrelated program that happened to inherit the number.
            try
            {
                if (parent.StartTime > self.StartTime) return null;
            }
            catch (Exception)
            {
                // Some processes refuse to report their start time; watching is still
                // better than not watching.
            }

            return parent;
        }
    }
}
