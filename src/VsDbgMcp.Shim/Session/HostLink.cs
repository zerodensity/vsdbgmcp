using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using StreamJsonRpc;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Session
{
    /// <summary>
    /// One connection to one running Visual Studio. Calls go out over the pipe;
    /// debugger events come back in and land on the shared event bus.
    /// </summary>
    public sealed class HostLink : IShimEvents, IDisposable
    {
        readonly EventBus _bus;
        NamedPipeClientStream _pipe;
        JsonRpc _rpc;
        bool _disposed;

        public HostLink(InstanceRecord record, EventBus bus)
        {
            Record = record;
            _bus = bus;
        }

        public InstanceRecord Record { get; private set; }
        public IDebugHost Debug { get; private set; }
        public IProjectSystem Project { get; private set; }
        public string HostVersion { get; private set; }
        public string LastError { get; private set; }

        public bool IsConnected => _rpc != null && !_rpc.IsDisposed && _pipe != null && _pipe.IsConnected;

        public string Id => Record.Id;

        /// <summary>Refreshes the record after rediscovery, keeping the live connection.</summary>
        public void UpdateRecord(InstanceRecord record)
        {
            if (record != null && record.Pid == Record.Pid) Record = record;
        }

        public async Task<bool> ConnectAsync(CancellationToken ct)
        {
            if (IsConnected) return true;
            Teardown();

            try
            {
                _pipe = new NamedPipeClientStream(".", Record.Pipe, PipeDirection.InOut,
                    PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.None);

                await _pipe.ConnectAsync(3000, ct).ConfigureAwait(false);

                // The formatter is stated explicitly rather than left to the default,
                // because the two ends of this pipe are built against different
                // StreamJsonRpc versions and their defaults may not agree.
                var formatter = new JsonMessageFormatter();
                formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;

                _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(_pipe, _pipe, formatter));
                _rpc.AddLocalRpcTarget<IShimEvents>(this, null);
                Debug = _rpc.Attach<IDebugHost>();
                Project = _rpc.Attach<IProjectSystem>();
                _rpc.StartListening();

                HostVersion = await Debug.HandshakeAsync(Names.ContractVersion, Record.Token).ConfigureAwait(false);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Teardown();
                return false;
            }
        }

        void Teardown()
        {
            // Disposing a connection whose far end has already gone throws from the
            // pipe. There is nothing left to salvage either way.
            try { _rpc?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            try { _pipe?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            _rpc = null;
            _pipe = null;
            Debug = null;
            Project = null;
        }

        // ---- pushed from Visual Studio ----

        public Task OnStopAsync(StopEvent stop)
        {
            if (stop != null)
            {
                stop.InstanceId = Id;
                _bus.Publish(stop);
                if (!string.IsNullOrEmpty(stop.Mode)) Record.DebugMode = stop.Mode;
            }
            return Task.CompletedTask;
        }

        public Task OnModuleLoadAsync(ModuleLoadEvent module)
        {
            if (module != null)
            {
                module.InstanceId = Id;
                _bus.PublishModuleLoad(module);
            }
            return Task.CompletedTask;
        }

        public Task OnOutputAsync(OutputEvent output) => Task.CompletedTask;

        public Task OnModeChangedAsync(string instanceId, string mode)
        {
            Record.DebugMode = mode;
            return Task.CompletedTask;
        }

        public Task OnWorkspaceChangedAsync(string instanceId) => Task.CompletedTask;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Teardown();
        }
    }
}
