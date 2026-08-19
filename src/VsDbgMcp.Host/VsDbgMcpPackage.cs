using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Loads with Visual Studio, publishes this instance so shims can find it, and
    /// serves the debug contracts over a named pipe.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Without a Window to dock against, Visual Studio gives a new tool window its own
    // floating frame. Naming Solution Explorer puts it in that tab group instead.
    [ProvideToolWindow(typeof(StatusWindow),
        Style = VsDockStyle.Tabbed,
        Orientation = ToolWindowOrientation.Right,
        Window = ToolWindowGuids80.SolutionExplorer)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.Debugging_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class VsDbgMcpPackage : AsyncPackage, IVsSolutionEvents, IVsDebuggerEvents
    {
        public const string PackageGuidString = "a7c33584-60a6-4a88-b454-83e5383271eb";
        static readonly Guid CommandSet = new Guid("ffc9b256-3f52-42fd-a224-b984438ea432");
        const int ShowStatusWindowCommandId = 0x0100;
        const int ShowStatusWindowFromViewCommandId = 0x0101;

        DTE2 _dte;
        IVsSolution _solution;
        IVsShell _shell;
        IVsDebugger _debugger;
        IVsOutputWindowPane _pane;

        PipeServer _server;
        DebugHost _debugHost;
        ProjectSystem _projectSystem;
        DebugEventSink _eventSink;

        uint _solutionCookie;
        uint _debuggerCookie;
        string _token;
        int _pid;
        bool _panelShown;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            MessageFilter.EnsureInstalled();

            _pid = System.Diagnostics.Process.GetCurrentProcess().Id;
            _token = Guid.NewGuid().ToString("N");

            _dte = await GetServiceAsync(typeof(SDTE)) as DTE2;
            _solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            _shell = await GetServiceAsync(typeof(SVsShell)) as IVsShell;
            _debugger = await GetServiceAsync(typeof(SVsShellDebugger)) as IVsDebugger;
            _pane = CreateOutputPane(await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow);

            // Copying the shim out is file work, and nothing in this process waits on
            // it: the agent launches the shim, not us.
            JoinableTaskFactory.RunAsync(async () =>
            {
                Log(await Task.Run(() => ShimStaging.Run()));
            }).FileAndForget("vsdbgmcp/stage-shim");

            _eventSink = new DebugEventSink(Log);
            _debugHost = new DebugHost(this, _dte, _solution, _debugger, _eventSink, JoinableTaskFactory, Log);
            _projectSystem = new ProjectSystem(this, _dte, _solution, JoinableTaskFactory, Log);

            _server = new PipeServer(Names.PipeName(_pid), _token, _debugHost, _projectSystem, Log);
            _debugHost.AttachServer(_server);
            _eventSink.StopOccurred += OnStopOccurred;
            _eventSink.OutputOccurred += OnOutputOccurred;

            _server.Start();

            if (_debugger != null)
            {
                _debugger.AdviseDebuggerEvents(this, out _debuggerCookie);
                _eventSink.Advise(_debugger);
            }
            _solution?.AdviseSolutionEvents(this, out _solutionCookie);

            Activity.PipeName = Names.PipeName(_pid);
            Activity.Changed += OnActivityChanged;
            await RegisterShowWindowCommandAsync();

            PublishRecord();
            Log("listening on pipe " + Names.PipeName(_pid));
        }

        async Task RegisterShowWindowCommandAsync()
        {
            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commands == null) return;

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            commands.AddCommand(new MenuCommand(
                (s, e) => ShowStatusWindow(),
                new CommandID(CommandSet, ShowStatusWindowCommandId)));

            commands.AddCommand(new MenuCommand(
                (s, e) => ShowStatusWindow(),
                new CommandID(CommandSet, ShowStatusWindowFromViewCommandId)));
        }

        void ShowStatusWindow(bool activate = true)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                // A panel that fails to appear must say why; failing quietly is the one
                // outcome that leaves nobody able to see what the agent is doing.
                try
                {
                    var window = await ShowToolWindowAsync(typeof(StatusWindow), 0, create: true,
                        cancellationToken: DisposalToken);

                    if (!(window?.Frame is IVsWindowFrame frame))
                    {
                        Log("panel: no window frame came back");
                        return;
                    }

                    // Showing without activating when an agent turns up: something
                    // attaching to your debugger is worth seeing, but not worth taking
                    // your keyboard.
                    var hr = activate ? frame.Show() : frame.ShowNoActivate();
                    Log("panel: shown (hr " + hr + ")");
                }
                catch (Exception ex)
                {
                    Log("panel: " + ex);
                }
            }).FileAndForget("vsdbgmcp/show-window");
        }

        /// <summary>
        /// Opens the panel the first time an agent attaches. After that it stays wherever
        /// the user put it, including closed.
        /// </summary>
        void OnActivityChanged()
        {
            if (_panelShown || Activity.Clients == 0) return;
            _panelShown = true;
            ShowStatusWindow(activate: false);
        }

        IVsOutputWindowPane CreateOutputPane(IVsOutputWindow window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (window == null) return null;

            var guid = new Guid("6b4a1cf0-2f18-4a4f-9a3c-9f2b6a6d4e11");
            window.CreatePane(ref guid, "vsdbgmcp", 1, 1);
            window.GetPane(ref guid, out var pane);
            return pane;
        }

        internal void Log(string message)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                _pane?.OutputStringThreadSafe(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }).FileAndForget("vsdbgmcp/log");
        }

        /// <summary>
        /// Rewrites the discovery record. Called whenever what this instance has open
        /// changes, so routing never works from a stale picture.
        /// </summary>
        internal void PublishRecord()
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var record = new InstanceRecord
                    {
                        Pid = _pid,
                        Pipe = Names.PipeName(_pid),
                        Token = _token,
                        VsVersion = WorkspaceProbe.ReadVsVersion(_shell),
                        Contract = Names.ContractVersion,
                        Workspace = WorkspaceProbe.Read(_solution),
                        ProjectDirs = WorkspaceProbe.ReadProjectDirs(_solution),
                        Capabilities = new[]
                        {
                            VsDbgMcp.Capabilities.Native,
                            VsDbgMcp.Capabilities.Managed,
                            VsDbgMcp.Capabilities.DataBreakpoints,
                            VsDbgMcp.Capabilities.Disassembly,
                            VsDbgMcp.Capabilities.Dumps,
                            VsDbgMcp.Capabilities.ConsoleIo,
                            VsDbgMcp.Capabilities.WindowCapture
                        },
                        DebugMode = _debugHost?.CurrentMode ?? DebugModes.Design,
                        StartedAt = DateTime.UtcNow.ToString("o")
                    };

                    InstanceFile.Write(record);
                    InstanceDirectory.Restrict();

                    Activity.InstanceId = record.Id;
                    Activity.Mode = record.DebugMode;
                }
                catch (Exception ex)
                {
                    Log("could not publish instance record: " + ex.Message);
                }
            }).FileAndForget("vsdbgmcp/publish");
        }

        void OnStopOccurred(Contracts.StopEvent stop)
        {
            _debugHost?.FillWatches(stop);
            _server?.Broadcast(events => events.OnStopAsync(stop));
        }

        void OnOutputOccurred(Contracts.OutputEvent output)
        {
            _server?.Broadcast(events => events.OnOutputAsync(output));
        }

        // ---- IVsDebuggerEvents ----

        public int OnModeChange(DBGMODE dbgmodeNew)
        {
            var mode = dbgmodeNew == DBGMODE.DBGMODE_Break ? DebugModes.Break
                : dbgmodeNew == DBGMODE.DBGMODE_Run ? DebugModes.Run
                : DebugModes.Design;

            _debugHost?.SetMode(mode);
            Activity.Mode = mode;
            _server?.Broadcast(events => events.OnModeChangedAsync(null, mode));

            // Entering break or run is where Visual Studio comes forward. If the agent
            // asked for it rather than the person at the keyboard, give the foreground
            // back to whatever had it. The guard knows the difference because only an
            // agent command arms it.
            FocusGuard.Restore(mode);

            PublishRecord();
            return Microsoft.VisualStudio.VSConstants.S_OK;
        }

        // ---- IVsSolutionEvents ----

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution) { PublishRecord(); return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnAfterCloseSolution(object pUnkReserved) { PublishRecord(); return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) { PublishRecord(); return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) { return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) { PublishRecord(); return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) { return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) { return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) { return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) { return Microsoft.VisualStudio.VSConstants.S_OK; }
        public int OnBeforeCloseSolution(object pUnkReserved) { return Microsoft.VisualStudio.VSConstants.S_OK; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    Activity.Changed -= OnActivityChanged;
                    InstanceFile.Remove(_pid);
                    _eventSink?.Unadvise(_debugger);

                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (_debuggerCookie != 0) _debugger?.UnadviseDebuggerEvents(_debuggerCookie);
                        if (_solutionCookie != 0) _solution?.UnadviseSolutionEvents(_solutionCookie);
                    });

                    _server?.Dispose();
                }
                catch
                {
                }
            }
            base.Dispose(disposing);
        }
    }
}
