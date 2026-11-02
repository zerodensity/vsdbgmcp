using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
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
    ///
    /// A client must send the instance token (PipeToken) before anything else. Only
    /// then is JSON-RPC started on its connection, so no call or event reaches a
    /// client that has not proved it read the discovery record.
    /// </summary>
    sealed class PipeServer : IDisposable
    {
        readonly string _pipeName;
        readonly string _token;
        readonly IDebugHost _debug;
        readonly IProjectSystem _projects;
        readonly Action<string> _log;

        readonly List<Connection> _connections = new List<Connection>();

        /// <summary>Connected but not yet admitted: still to send the token.</summary>
        readonly HashSet<NamedPipeServerStream> _pending = new HashSet<NamedPipeServerStream>();
        readonly object _gate = new object();
        CancellationTokenSource _cts;
        Task _listener;
        bool _disposed;

        sealed class Connection : IDisposable
        {
            public JsonRpc Rpc;
            public NamedPipeServerStream Stream;
            public IShimEvents Events;

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
            get { lock (_gate) return _connections.Count; }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = Task.Run(() => ListenAsync(_cts.Token));
        }

        async Task ListenAsync(CancellationToken ct)
        {
            NamedPipeServerStream listening = null;
            var firstInstance = true;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (listening == null)
                    {
                        listening = SecurePipe.Create(_pipeName, firstInstance);
                        firstInstance = false;
                    }
                    await listening.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    // The next instance exists before this one is handed off, so the
                    // name is never left without an instance of ours that another
                    // process could take over.
                    var connected = listening;
                    listening = null;
                    lock (_gate) _pending.Add(connected);
                    try
                    {
                        listening = SecurePipe.Create(_pipeName, false);
                    }
                    finally
                    {
                        // Not awaited: a client that is slow to send its token must
                        // not hold up the next one.
                        _ = AdmitAsync(connected);
                    }
                }
                catch (OperationCanceledException)
                {
                    listening?.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    listening?.Dispose();
                    listening = null;

                    // With no instance of ours left, claim the name afresh so nothing
                    // else can have taken it in the meantime.
                    lock (_gate) firstInstance = _pending.Count == 0 && _connections.Count == 0;
                    _log("listener: " + ex.Message);
                    await Task.Delay(500, ct).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                }
            }
            listening?.Dispose();
        }

        async Task AdmitAsync(NamedPipeServerStream stream)
        {
            var client = SecurePipe.DescribeClient(stream);
            TokenCheck check;
            try
            {
                check = await PipeToken.ReceiveAsync(stream, _token, PipeToken.Timeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    // Dispose has already closed it; nothing was refused.
                    if (!_pending.Remove(stream)) return;
                }
                _log("refused " + client + ": " + ex.Message);
                stream.Dispose();
                return;
            }

            lock (_gate)
            {
                // Dispose has already closed it.
                if (!_pending.Remove(stream)) return;
            }

            if (check != TokenCheck.Accepted)
            {
                stream.Dispose();
                if (check == TokenCheck.Closed) return;

                _log("refused " + client + ": " + Describe(check));
                Activity.Record("client refused", client + ": " + Describe(check), 0, true);
                return;
            }

            try
            {
                Accept(stream, client);
            }
            catch (Exception ex)
            {
                _log("could not serve " + client + ": " + ex.Message);
                stream.Dispose();
            }
        }

        static string Describe(TokenCheck check)
        {
            switch (check)
            {
                case TokenCheck.WrongToken: return "wrong token";
                case TokenCheck.Malformed: return "did not send a token; an MCP client running an older vsdbgmcp must be restarted to update it";
                case TokenCheck.TimedOut: return "sent no token within " + (int)PipeToken.Timeout.TotalSeconds + " seconds";
                default: return check.ToString();
            }
        }

        /// <summary>
        /// Writes each reply straight to the pipe. The Newtonsoft formatter first builds
        /// the whole reply as a tree of JSON objects: a thread listing became megabytes
        /// of them inside Visual Studio's process, which its garbage collector seldom
        /// reclaims, so Visual Studio grew by that much with every call.
        ///
        /// The JSON on the wire stays what the shim's Newtonsoft formatter writes and
        /// reads: property names as declared, matched without regard to case, nulls left
        /// out. Stated explicitly because the two ends of this pipe are built against
        /// different StreamJsonRpc versions, whose defaults need not agree.
        /// </summary>
        static IJsonRpcMessageTextFormatter AvailableFormatter()
        {
            try
            {
                return Formatter();
            }
            catch (Exception ex) when (ex is TypeLoadException || ex is System.IO.FileNotFoundException || ex is MissingMethodException)
            {
                // An early Visual Studio 2022 ships a StreamJsonRpc without the System.Text.Json
                // formatter. The Newtonsoft one writes the same JSON, and costs memory.
                var formatter = new JsonMessageFormatter();
                formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;
                return formatter;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static SystemTextJsonFormatter Formatter() => new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = null,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            }
        };

        void Accept(NamedPipeServerStream stream, string client)
        {
            var connection = new Connection { Stream = stream };

            var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, AvailableFormatter()));
            connection.Rpc = rpc;

            rpc.AddLocalRpcTarget<IDebugHost>(_debug, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
            rpc.AddLocalRpcTarget<IProjectSystem>(_projects, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
            if (_debug is IOperationHost operations) rpc.AddLocalRpcTarget<IOperationHost>(operations, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
            if (_debug is IProfileHost profiles) rpc.AddLocalRpcTarget<IProfileHost>(profiles, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
            connection.Events = rpc.Attach<IShimEvents>();

            rpc.Disconnected += (_, __) => Remove(connection);

            lock (_gate)
            {
                if (_disposed)
                {
                    connection.Dispose();
                    return;
                }
                _connections.Add(connection);
            }

            rpc.StartListening();
            Activity.Clients = ConnectionCount;
            Activity.Record("client connected", client, 0, false);
            _log("client connected: " + client);
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

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }

            List<Connection> targets;
            List<NamedPipeServerStream> pending;
            lock (_gate)
            {
                _disposed = true;
                targets = _connections.ToList();
                _connections.Clear();
                pending = _pending.ToList();
                _pending.Clear();
            }
            foreach (var c in targets) c.Dispose();
            foreach (var stream in pending) stream.Dispose();

            try { _listener?.Wait(1000); } catch { }
            _cts?.Dispose();
        }
    }
}
