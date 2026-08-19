using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using VsDbgMcp.Shim.Session;
using VsDbgMcp.Shim.Tools;

namespace VsDbgMcp.Shim
{
    static class Program
    {
        const string Instructions =
            "Drives the Visual Studio debugger. Call 'status' first to see where things stand.\n" +
            "\n" +
            "Waiting: after launch, go, or step, call 'wait' to find out where the program stopped and why. " +
            "Never poll 'status' in a loop; 'wait' blocks on the debugger's own events and cannot miss a stop.\n" +
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
                    o.ServerInstructions = Instructions;
                })
                .WithStdioServerTransport()
                .WithTools<SessionTools>()
                .WithTools<LifecycleTools>()
                .WithTools<ExecutionTools>()
                .WithTools<BreakpointTools>()
                .WithTools<InspectionTools>()
                .WithTools<EvidenceTools>()
                .WithTools<IoTools>()
                .WithTools<BuildTools>();

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

        static string ThisVersion() =>
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }

    sealed class ShimOptions
    {
        public const string HelpText =
            "vsdbgmcp - Visual Studio debugger over MCP\n" +
            "\n" +
            "  vsdbgmcp [--cwd DIR] [--instance ID] [-v]\n" +
            "\n" +
            "Speaks MCP over stdio. Launched by an MCP client, not by hand.\n" +
            "\n" +
            "  --cwd DIR        Route as if started in DIR. Defaults to the current directory.\n" +
            "  --instance ID    Pin to one Visual Studio instance instead of routing by directory.\n" +
            "  -v, --verbose    Log to stderr.\n" +
            "  -h, --help       This text.\n";

        public string Cwd { get; private set; }
        public string Instance { get; private set; }
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
