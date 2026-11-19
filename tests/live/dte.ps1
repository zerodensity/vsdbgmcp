# Windows PowerShell 5.1. Reaches the automation model of one test Visual Studio, found
# by process id in the running object table, to set up what the MCP tools cannot.
#   -Options A,B  prints Debugging options, named Page.Name, as Page.Name=Value lines.
#   -Show         shows the main window, which run.ps1 starts hidden. A dialog of a
#                 hidden main window has no owner, so it is not detected as modal.
#   -Command X    runs the Visual Studio command X; a modal one returns when it closes.
param(
    [Parameter(Mandatory)][int]$VsPid,
    [string]$Options = '',
    [switch]$Show,
    [string]$Command
)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class RunningObjects {
    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx context);
    public static object Dte(int pid) {
        IRunningObjectTable table; GetRunningObjectTable(0, out table);
        IEnumMoniker monikers; table.EnumRunning(out monikers);
        var moniker = new IMoniker[1];
        while (monikers.Next(1, moniker, IntPtr.Zero) == 0) {
            IBindCtx context; CreateBindCtx(0, out context);
            string name; moniker[0].GetDisplayName(context, null, out name);
            if (name.StartsWith("!VisualStudio.DTE.") && name.EndsWith(":" + pid)) {
                object dte; table.GetObject(moniker[0], out dte); return dte;
            }
        }
        return null;
    }
}

// Retries calls a busy Visual Studio rejects, as every automation client must.
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleMessageFilter {
    [PreserveSig] int HandleInComingCall(int callType, IntPtr caller, int tick, IntPtr info);
    [PreserveSig] int RetryRejectedCall(IntPtr callee, int tick, int rejectType);
    [PreserveSig] int MessagePending(IntPtr callee, int tick, int pendingType);
}
public class RetryFilter : IOleMessageFilter {
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IOleMessageFilter filter, out IOleMessageFilter old);
    public static void Register() { IOleMessageFilter old; CoRegisterMessageFilter(new RetryFilter(), out old); }
    public int HandleInComingCall(int callType, IntPtr caller, int tick, IntPtr info) { return 0; }
    public int RetryRejectedCall(IntPtr callee, int tick, int rejectType) { return rejectType == 2 && tick < 60000 ? 100 : -1; }
    public int MessagePending(IntPtr callee, int tick, int pendingType) { return 2; }
}
'@
[RetryFilter]::Register()
$dte = [RunningObjects]::Dte($VsPid)
if (!$dte) { throw "Visual Studio $VsPid has no automation object in the running object table." }
foreach ($option in $Options.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
    $page, $name = $option.Split('.', 2)
    '{0}={1}' -f $option, $dte.Properties('Debugging', $page).Item($name).Value
}
if ($Show) { $dte.MainWindow.Visible = $true }
if ($Command) { $dte.ExecuteCommand($Command) }
