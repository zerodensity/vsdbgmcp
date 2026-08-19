using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using StreamJsonRpc;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Accepts shim connections on a named pipe and serves the debug contracts.
    ///
    /// A pipe rather than a socket: nothing binds a network interface, and Windows
    /// enforces who may connect. Several clients can be attached at once, so two
    /// agents can watch the same Visual Studio.
    /// </summary>
    sealed class PipeServer : IDisposable
    {
        readonly string _pipeName;
        readonly string _token;
        readonly IDebugHost _debug;
        readonly IProjectSystem _projects;
        readonly Action<string> _log;

        readonly List<Connection> _connections = new List<Connection>();
        readonly object _gate = new object();
        CancellationTokenSource _cts;
        Task _listener;

        sealed class Connection : IDisposable
        {
            public JsonRpc Rpc;
            public NamedPipeServerStream Stream;
            public IShimEvents Events;
            public bool Authenticated;

            public void Dispose()
            {
                try { Rpc?.Dispose(); } catch { }
                try { Stream?.Dispose(); } catch { }
            }
        }

        public PipeServer(string pipeName, string token, IDebugHost debug, IProjectSystem projects, Action<string> log)
        {
            _pipeName = pipeName;
            _token = token;
            _debug = debug;
            _projects = projects;
            _log = log ?? (_ => { });
        }

        public int ConnectionCount
        {
            get { lock (_gate) return _connections.Count(c => c.Authenticated); }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = Task.Run(() => ListenAsync(_cts.Token));
        }

        async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream stream = null;
                try
                {
                    stream = CreatePipe();
                    await stream.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    Accept(stream);
                    stream = null; // owned by the connection now
                }
                catch (OperationCanceledException)
                {
                    stream?.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    stream?.Dispose();
                    _log("listener: " + ex.Message);
                    await Task.Delay(500, ct).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                }
            }
        }

        NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            var self = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));

            return new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 64 * 1024,
                outBufferSize: 64 * 1024,
                pipeSecurity: security);
        }

        void Accept(NamedPipeServerStream stream)
        {
            var connection = new Connection { Stream = stream };

            // Stated explicitly because the two ends of this pipe are built against
            // different StreamJsonRpc versions, whose defaults need not agree.
            var formatter = new JsonMessageFormatter();
            formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;

            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
            connection.Rpc = rpc;

            rpc.AddLocalRpcTarget(_debug, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
            rpc.AddLocalRpcTarget(_projects, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
            connection.Events = rpc.Attach<IShimEvents>();

            rpc.Disconnected += (_, __) => Remove(connection);
            rpc.StartListening();

            lock (_gate) _connections.Add(connection);
            Activity.Clients = ConnectionCount;
            Activity.Record("client connected", null, 0, false);
            _log("client connected");
        }

        void Remove(Connection connection)
        {
            lock (_gate) _connections.Remove(connection);
            connection.Dispose();
            Activity.Clients = ConnectionCount;
            Activity.Record("client disconnected", null, 0, false);
            _log("client disconnected");
        }

        /// <summary>
        /// Pushes an event to every attached client. Failures are swallowed: a client
        /// that has gone away must not take the debugger's event pump down with it.
        /// </summary>
        public void Broadcast(Func<IShimEvents, Task> send)
        {
            List<Connection> targets;
            lock (_gate) targets = _connections.ToList();

            foreach (var connection in targets)
            {
                try
                {
                    _ = send(connection.Events).ContinueWith(
                        t => { var _ignored = t.Exception; },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                }
                catch
                {
                }
            }
        }

        /// <summary>Called by the debug host when a shim says hello.</summary>
        public string Handshake(int shimContract, string token)
        {
            if (!string.Equals(token, _token, StringComparison.Ordinal))
                throw new InvalidOperationException("Token does not match this instance.");

            if (shimContract != Names.ContractVersion)
            {
                _log("contract mismatch: shim " + shimContract + ", host " + Names.ContractVersion);
            }

            lock (_gate)
            {
                foreach (var c in _connections) c.Authenticated = true;
            }

            Activity.Clients = ConnectionCount;
            return Names.ContractVersion.ToString();
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }

            List<Connection> targets;
            lock (_gate)
            {
                targets = _connections.ToList();
                _connections.Clear();
            }
            foreach (var c in targets) c.Dispose();

            try { _listener?.Wait(1000); } catch { }
            _cts?.Dispose();
        }
    }
}
