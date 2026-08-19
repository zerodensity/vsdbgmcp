using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VsDbgMcp.Host
{
    static class InstanceDirectory
    {
        /// <summary>
        /// Locks the discovery directory to the current user. The token in each record
        /// is the real guard; this stops another account from reading tokens at all.
        /// </summary>
        public static void Restrict()
        {
            try
            {
                var dir = Names.InstanceDir;
                Directory.CreateDirectory(dir);

                var info = new DirectoryInfo(dir);
                var security = info.GetAccessControl();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                var user = WindowsIdentity.GetCurrent().User;
                security.AddAccessRule(new FileSystemAccessRule(
                    user,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                info.SetAccessControl(security);
            }
            catch
            {
                // Not fatal. %LOCALAPPDATA% is already user scoped by default.
            }
        }
    }
}
