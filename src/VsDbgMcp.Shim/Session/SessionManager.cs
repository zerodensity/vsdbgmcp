using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Shim.Discovery;

namespace VsDbgMcp.Shim.Session
{
    /// <summary>
    /// Raised when routing cannot decide. The message is written for the agent to act
    /// on directly: it names the candidates and the value to pass next time.
    /// </summary>
    public sealed class RoutingException : Exception
    {
        public RoutingException(string message) : base(message) { }
    }

    /// <summary>
    /// Holds the connections to every running Visual Studio and decides which one a
    /// call belongs to. Connections to all instances are kept open, so wait() can
    /// race across windows; routing only decides the default target.
    /// </summary>
    public sealed class SessionManager : IDisposable
    {
        static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

        readonly InstanceStore _store;
        readonly Dictionary<int, HostLink> _links = new Dictionary<int, HostLink>();
        readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        DateTime _lastRefresh = DateTime.MinValue;
        string _sticky;

        public SessionManager(string cwd, InstanceStore store = null)
        {
            Cwd = PathUtil.Normalize(cwd) ?? Environment.CurrentDirectory;
            _store = store ?? new InstanceStore();
        }

        public string Cwd { get; }
        public EventBus Events { get; } = new EventBus();

        public string StickyInstanceId => _sticky;

        public async Task<IReadOnlyList<HostLink>> RefreshAsync(bool force, CancellationToken ct)
        {
            await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!force && DateTime.UtcNow - _lastRefresh < RefreshInterval && _links.Count > 0)
                    return _links.Values.ToList();

                _lastRefresh = DateTime.UtcNow;
                var records = _store.Discover();
                var seen = new HashSet<int>();

                foreach (var record in records)
                {
                    seen.Add(record.Pid);
                    if (_links.TryGetValue(record.Pid, out var existing))
                    {
                        existing.UpdateRecord(record);
                        if (!existing.IsConnected) await existing.ConnectAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    var link = new HostLink(record, Events);
                    await link.ConnectAsync(ct).ConfigureAwait(false);
                    _links[record.Pid] = link;
                }

                foreach (var pid in _links.Keys.Where(p => !seen.Contains(p)).ToList())
                {
                    _links[pid].Dispose();
                    _links.Remove(pid);
                }

                return _links.Values.ToList();
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        public async Task<IReadOnlyList<HostLink>> AllAsync(CancellationToken ct) =>
            await RefreshAsync(false, ct).ConfigureAwait(false);

        /// <summary>
        /// Picks the instance for a call: an explicit argument wins, then a sticky
        /// default set by use(), then the working directory.
        /// </summary>
        public async Task<HostLink> ResolveAsync(string instance, CancellationToken ct)
        {
            var links = await RefreshAsync(false, ct).ConfigureAwait(false);
            var records = links.Select(l => l.Record).ToList();

            if (records.Count == 0)
            {
                // One forced retry: Visual Studio may have started since the last look.
                links = await RefreshAsync(true, ct).ConfigureAwait(false);
                records = links.Select(l => l.Record).ToList();
            }

            RouteResult route;

            if (!string.IsNullOrWhiteSpace(instance))
            {
                route = Router.SelectExplicit(records, instance);
            }
            else if (!string.IsNullOrEmpty(_sticky) &&
                     records.Any(r => string.Equals(r.Id, _sticky, StringComparison.OrdinalIgnoreCase)))
            {
                route = Router.SelectExplicit(records, _sticky);
            }
            else
            {
                route = Router.ByDirectory(records, Cwd);
            }

            if (route == null || route.Outcome != RouteOutcome.Resolved)
                throw new RoutingException(Router.Explain(route ?? new RouteResult { Outcome = RouteOutcome.NoInstances }, Cwd));

            var link = _links[route.Instance.Pid];
            if (!link.IsConnected && !await link.ConnectAsync(ct).ConfigureAwait(false))
            {
                throw new RoutingException(
                    "Found " + link.Id + " but could not connect to it" +
                    (string.IsNullOrEmpty(link.LastError) ? "." : ": " + link.LastError) +
                    "\nThe instance may still be loading. Retry, or restart Visual Studio if it persists.");
            }

            return link;
        }

        /// <summary>Sets the default target for this session. Empty clears it.</summary>
        public async Task<string> UseAsync(string instance, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(instance))
            {
                _sticky = null;
                return "Default cleared. Routing falls back to the working directory.";
            }

            var links = await RefreshAsync(true, ct).ConfigureAwait(false);
            var route = Router.SelectExplicit(links.Select(l => l.Record).ToList(), instance);
            if (route == null || route.Outcome != RouteOutcome.Resolved)
                throw new RoutingException(Router.Explain(route, Cwd));

            _sticky = route.Instance.Id;
            return "Default instance is now " + _sticky + ".";
        }

        public void Dispose()
        {
            foreach (var link in _links.Values) link.Dispose();
            _links.Clear();
        }
    }
}
