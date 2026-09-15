using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VsDbgMcp.Shim.Session;
using VsDbgMcp.Shim.Tools;

namespace VsDbgMcp.Shim
{
    static class Program
    {
        static string Instructions(ShimOptions options) =>
            "Drives the Visual Studio debugger. Call 'status' first to see where things stand.\n" +
            "\n" +
            "Waiting: after launch, go, or step, call 'wait' to find out where the program stopped and why. " +
            "Build, launch and breakpoint requests can return pending operation IDs; use operation_status to recover or wait, and reuse requestId on retries. " +
            "Never poll 'status' in a loop; 'wait' blocks on the debugger's own events and cannot miss a stop. " +
            "Call it after resuming, not instead of resuming: where the program has not run since it last " +
            "stopped, 'wait' says so rather than waiting for a stop that cannot come.\n" +
            "\n" +
            "Events: when something happened in Visual Studio since your previous call - a stop, an exit, a " +
            "build finishing, someone starting or ending debugging - the reply begins with \"Since your last " +
            "call:\" and one line per event. Nothing is added when nothing happened, so there is no block to " +
            "look for and none to skip. 'wait' with for='any' blocks for the next such event, " +
            "for='output:REGEX' for a Debug pane line. 'status' lists what happened recently in one window.\n" +
            "\n" +
            "While the program runs and you have other work, run " + FollowCommand(options) + " under a " +
            "background monitor if your client has one; it prints one line per stop, exit, build completion " +
            "or session change until stopped.\n" +
            "\n" +
            "Instances: several Visual Studio windows can be open at once. Calls go to the one whose solution " +
            "matches the working directory. If that is ambiguous the reply lists the candidates and the exact " +
            "'instance' value to pass. Use 'use' to fix a default for the session, and wait with instance='any' " +
            "to watch every window at once.\n" +
            "\n" +
            "C++: when a breakpoint does not bind or a stack shows addresses instead of names, check 'modules' " +
            "for missing symbols and the Debug pane via 'output'. Use 'triage' after a crash rather than " +
            "assembling the picture by hand, and 'bp_set' with dataExpression to catch memory being overwritten.\n" +
            "\n" +
            "'eval' will not call functions unless allowSideEffects is set, because doing so really runs them.";

        /// <summary>
        /// The exact command to start the event stream, so nothing has to be guessed
        /// from a name. Started through the runtime rather than the executable, this
        /// process is dotnet, and the assembly has to be named as well.
        /// </summary>
        static string FollowCommand(ShimOptions options)
        {
            var exe = Environment.ProcessPath;
            var self = typeof(Program).Assembly.Location;
            var command = exe != null &&
                string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase)
                    ? "\"" + exe + "\" \"" + self + "\""
                    : "\"" + (exe ?? Names.ShimExe) + "\"";

            return command + " --follow --cwd \"" + options.Cwd + "\"" +
                (string.IsNullOrEmpty(options.Instance) ? "" : " --instance " + options.Instance);
        }

        static async Task<int> Main(string[] args)
        {
            var options = ShimOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.Error.WriteLine(ShimOptions.HelpText);
                return 0;
            }

            // Closing stdin is the ordinary way this process ends. Watching the client
            // as well covers the ways that do not close it: being killed outright, or
            // replacing its own image during an update.
            ParentWatch.ExitWhenParentDoes();

            if (options.Follow) return await Follow.RunAsync(options).ConfigureAwait(false);

            var sessions = new SessionManager(options.Cwd);

            var builder = Host.CreateApplicationBuilder();

            // stdout carries the protocol, so every log line has to go to stderr.
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning);

            builder.Services.AddSingleton(sessions);

            builder.Services
                .AddMcpServer(o =>
                {
                    o.ServerInfo = new Implementation { Name = "vsdbgmcp", Version = ThisVersion() };
                    o.ServerInstructions = Instructions(options);
                })
                .WithStdioServerTransport()
                .WithRequestFilters(f => f
                    .AddCallToolFilter(EventsSinceTheLastCall(sessions))
                    .AddCallToolFilter(FailuresKeepTheirOwnWords))
                .WithTools<SessionTools>()
                .WithTools<LifecycleTools>()
                .WithTools<ExecutionTools>()
                .WithTools<BreakpointTools>()
                .WithTools<InspectionTools>()
                .WithTools<ProfileTools>()
                .WithTools<EvidenceTools>()
                .WithTools<IoTools>()
                .WithTools<BuildTools>()
                .WithTools<OperationTools>();

            using (var host = builder.Build())
            {
                if (!string.IsNullOrEmpty(options.Instance))
                {
                    try { await sessions.UseAsync(options.Instance, default).ConfigureAwait(false); }
                    catch (Exception ex) { Console.Error.WriteLine("vsdbgmcp: " + ex.Message); }
                }

                await host.RunAsync().ConfigureAwait(false);
            }

            sessions.Dispose();
            return 0;
        }

        /// <summary>
        /// A failed call reaches the caller as the reason and nothing else.
        ///
        /// Left to the SDK it arrives behind "An error occurred invoking 'eval'", which
        /// tells the caller only what it already knows and reads as a fault in this
        /// server rather than an answer about the program being debugged. Only this
        /// server's own failures are unwrapped; a fault in the protocol is left as it is.
        /// </summary>
        static McpRequestFilter<CallToolRequestParams, CallToolResult> FailuresKeepTheirOwnWords =>
            next => async (context, ct) =>
            {
                try
                {
                    return await next(context, ct).ConfigureAwait(false);
                }
                catch (ToolFailure failure)
                {
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = { new TextContentBlock { Text = failure.Message } }
                    };
                }
            };

        /// <summary>
        /// What happened in Visual Studio since the model's previous call goes at the
        /// top of this reply.
        ///
        /// At the top because a long result is truncated from the bottom by some
        /// clients, and because "the debuggee exited" changes how everything under it
        /// should be read. On failures too: eval failing for want of a frame, next to
        /// the process exiting, is the pairing that explains it.
        ///
        /// Nothing is added when nothing happened. A fixed line on every reply is a tax
        /// on hundreds of calls, and it teaches the model to skip the block.
        /// </summary>
        static McpRequestFilter<CallToolRequestParams, CallToolResult> EventsSinceTheLastCall(SessionManager sessions) =>
            next => async (context, ct) =>
            {
                var result = await next(context, ct).ConfigureAwait(false);

                var digest = EventLines.Digest(sessions.Log.TakeUnseen(), sessions.ConnectedCount > 1, DateTime.UtcNow);
                if (digest != null) result.Content.Insert(0, new TextContentBlock { Text = digest });

                return result;
            };

        static string ThisVersion() =>
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }

    sealed class ShimOptions
    {
        public const string HelpText =
            "vsdbgmcp - Visual Studio debugger over MCP\n" +
            "\n" +
            "  vsdbgmcp [--cwd DIR] [--instance ID] [-v]\n" +
            "  vsdbgmcp --follow [--cwd DIR] [--instance ID|any] [--match REGEX] [-v]\n" +
            "\n" +
            "Speaks MCP over stdio. Launched by an MCP client, not by hand.\n" +
            "\n" +
            "  --cwd DIR        Route as if started in DIR. Defaults to the current directory.\n" +
            "  --instance ID    Pin to one Visual Studio instance instead of routing by directory.\n" +
            "  --follow         Print one line per debugger event to stdout instead of serving MCP.\n" +
            "                   Ends when stdin closes. Use --instance any to watch every window.\n" +
            "  --match REGEX    With --follow, also print Debug pane lines matching REGEX.\n" +
            "  -v, --verbose    Log to stderr.\n" +
            "  -h, --help       This text.\n";

        public string Cwd { get; private set; }
        public string Instance { get; private set; }
        public string Match { get; private set; }
        public bool Follow { get; private set; }
        public bool Verbose { get; private set; }
        public bool ShowHelp { get; private set; }

        public static ShimOptions Parse(string[] args)
        {
            var options = new ShimOptions { Cwd = Directory.GetCurrentDirectory() };

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--cwd" when i + 1 < args.Length:
                        options.Cwd = args[++i];
                        break;
                    case "--instance" when i + 1 < args.Length:
                        options.Instance = args[++i];
                        break;
                    case "--follow":
                        options.Follow = true;
                        break;
                    case "--match" when i + 1 < args.Length:
                        options.Match = args[++i];
                        break;
                    case "-v":
                    case "--verbose":
                        options.Verbose = true;
                        break;
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    case "stdio":
                        break;
                }
            }

            return options;
        }
    }
}
