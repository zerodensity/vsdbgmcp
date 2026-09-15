using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Shim.Session;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// One line per notable event on stdout, until it is stopped.
    ///
    /// The server can only answer calls, so a model that starts a program and then
    /// goes off to edit files hears nothing until it calls again. A client that can
    /// watch a process can watch this instead, and gets a stop the moment it happens.
    ///
    /// It has no expectations to register, so it prints a run this session started as
    /// "debugging started" like any other. That is why the wording is neutral.
    /// </summary>
    static class Follow
    {
        public static async Task<int> RunAsync(ShimOptions options)
        {
            Regex match = null;
            if (!string.IsNullOrEmpty(options.Match))
            {
                try
                {
                    match = new Regex(options.Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    Console.Error.WriteLine("vsdbgmcp: --match is not a regular expression: " + ex.Message);
                    return 2;
                }
            }

            // A monitor reads this as it is written, so no line may sit in a buffer.
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

            var watchAll = string.Equals(options.Instance, "any", StringComparison.OrdinalIgnoreCase);
            var sessions = new SessionManager(options.Cwd);
            string target = null;

            bool Watched(string instanceId) =>
                watchAll || string.Equals(instanceId, target, StringComparison.OrdinalIgnoreCase);

            sessions.Log.Subscribe(entry =>
            {
                if (!Watched(entry.InstanceId)) return;

                // Wall clock rather than an age, because this is read beside other logs.
                Console.WriteLine(Stamp() + " " + EventLines.Line(entry, watchAll, entry.At));
            });

            if (match != null)
                sessions.Events.SubscribeOutput((instanceId, text) =>
                {
                    if (!Watched(instanceId) || !Matched(match, text)) return;
                    Console.WriteLine(Stamp() + " output: " + (watchAll ? "[" + instanceId + "] " : "") + text);
                });

            using (var stopping = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; stopping.Cancel(); };

                // The client closing its end is the ordinary way this ends.
                _ = Task.Run(async () =>
                {
                    while (await Console.In.ReadLineAsync().ConfigureAwait(false) != null) { }
                    stopping.Cancel();
                });

                var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var explained = false;

                try
                {
                    await sessions.KeepConnectedAsync(stopping.Token, async links =>
                    {
                        // Connections are this program's own news rather than the
                        // debugger's, so they go to stderr where a monitor leaves them.
                        foreach (var link in links.Where(l => l.IsConnected && connected.Add(l.Id)))
                            Console.Error.WriteLine("connected " + link.Id + " " + (link.Record.Workspace?.Name ?? ""));

                        foreach (var id in connected.Where(id => !links.Any(l => l.IsConnected && l.Id == id)).ToList())
                        {
                            connected.Remove(id);
                            Console.Error.WriteLine("lost " + id);
                        }

                        if (watchAll || target != null || connected.Count == 0) return;

                        try
                        {
                            target = (await sessions.ResolveAsync(options.Instance, stopping.Token).ConfigureAwait(false)).Id;
                            explained = false;
                        }
                        catch (RoutingException ex)
                        {
                            // Said once: the directory may match a window that opens later.
                            if (!explained) Console.Error.WriteLine("vsdbgmcp: " + ex.Message);
                            explained = true;
                        }
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            sessions.Dispose();
            return 0;
        }

        static string Stamp() => DateTime.Now.ToString("HH:mm:ss");

        static bool Matched(Regex pattern, string text)
        {
            try { return pattern.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return false; }
        }
    }
}
