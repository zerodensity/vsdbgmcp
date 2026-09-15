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
            Captures = store == null ? new Profiling.Captures(System.IO.Path.Combine(Names.InstanceDir, "profiles", "aggregates")) : new Profiling.Captures();
        }

        public string Cwd { get; }
        public EventBus Events { get; } = new EventBus();

        /// <summary>What happened while the model was not looking.</summary>
        public EventLog Log { get; } = new EventLog();

        /// <summary>
        /// How many windows are connected. The digest names the instance only when
        /// there is more than one, because with one it is noise on every line.
        /// </summary>
        public int ConnectedCount
        {
            get { lock (_links) return _links.Values.Count(l => l.IsConnected); }
        }

        /// <summary>The profiles taken in this session, so one can be read against another.</summary>
        public Profiling.Captures Captures { get; }

        public string StickyInstanceId => _sticky;

        public async Task<IReadOnlyList<HostLink>> RefreshAsync(bool force, CancellationToken ct)
        {
            await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Every read of the map takes the lock, not only the writes. Dispose at
                // shutdown is not held off by the refresh gate, so an unlocked
                // enumeration here throws the moment a session is torn down mid-refresh.
                var current = Links();
                if (!force && DateTime.UtcNow - _lastRefresh < RefreshInterval && current.Count > 0)
                    return current;

                _lastRefresh = DateTime.UtcNow;
                var records = _store.Discover();
                var seen = new HashSet<int>();

                foreach (var record in records)
                {
                    seen.Add(record.Pid);

                    HostLink existing;
                    lock (_links) _links.TryGetValue(record.Pid, out existing);
                    if (existing != null)
                    {
                        existing.UpdateRecord(record);
                        if (!existing.IsConnected) await existing.ConnectAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    var link = new HostLink(record, Events, Log);
                    await link.ConnectAsync(ct).ConfigureAwait(false);
                    lock (_links) _links[record.Pid] = link;
                }

                List<int> vanished;
                lock (_links) vanished = _links.Keys.Where(p => !seen.Contains(p)).ToList();

                foreach (var pid in vanished)
                {
                    HostLink link;
                    lock (_links)
                    {
                        if (!_links.TryGetValue(pid, out link)) continue;
                        _links.Remove(pid);
                    }

                    link.ReportGone();
                    link.Dispose();
                }

                return Links();
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        /// <summary>Every link there is, as a list nothing else can change underneath.</summary>
        List<HostLink> Links()
        {
            lock (_links) return _links.Values.ToList();
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

            HostLink link;
            lock (_links) link = _links[route.Instance.Pid];
            if (!link.IsConnected && !await link.ConnectAsync(ct).ConfigureAwait(false))
            {
                throw new RoutingException(
                    "Found " + link.Id + " but could not connect to it" +
                    (string.IsNullOrEmpty(link.LastError) ? "." : ": " + link.LastError) +
                    "\nThe instance may still be loading. Retry, or restart Visual Studio if it persists.");
            }

            return link;
        }

        /// <summary>
        /// Keeps connections to every window open, looking for new ones every couple of
        /// seconds, until the token is cancelled. A call refreshes on its way through,
        /// so the server never needs this; something that only listens does.
        /// </summary>
        public async Task KeepConnectedAsync(CancellationToken ct, Func<IReadOnlyList<HostLink>, Task> after = null)
        {
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<HostLink> links = new List<HostLink>();

                try { links = await RefreshAsync(true, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch { /* one window that will not answer is no reason to stop watching. */ }

                if (after != null) await after(links).ConfigureAwait(false);

                await Task.Delay(RefreshInterval, ct).ConfigureAwait(false);
            }
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
            List<HostLink> links;
            lock (_links)
            {
                links = _links.Values.ToList();
                _links.Clear();
            }

            foreach (var link in links) link.Dispose();
        }
    }
}
