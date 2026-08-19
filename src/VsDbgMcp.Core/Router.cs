using System;
using System.Collections.Generic;
using System.Linq;

namespace VsDbgMcp
{
    public enum RouteOutcome
    {
        Resolved,
        Ambiguous,
        NoMatch,
        NoInstances
    }

    public sealed class RouteResult
    {
        public RouteOutcome Outcome { get; set; }
        public InstanceRecord Instance { get; set; }
        public List<InstanceRecord> Candidates { get; set; } = new List<InstanceRecord>();

        /// <summary>How the match was made, for the status line and for diagnosing surprises.</summary>
        public string How { get; set; }

        public static RouteResult Ok(InstanceRecord r, string how) =>
            new RouteResult { Outcome = RouteOutcome.Resolved, Instance = r, How = how };
    }

    /// <summary>
    /// Picks the Visual Studio instance a request belongs to. Pure logic: the caller
    /// supplies the instances and the working directory.
    /// </summary>
    public static class Router
    {
        /// <summary>
        /// Explicit selection by instance id ("App#42696"), by pid, or by an
        /// unambiguous name prefix. Returns null when the caller did not ask for one.
        /// </summary>
        public static RouteResult SelectExplicit(IReadOnlyList<InstanceRecord> instances, string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            spec = spec.Trim();

            var exact = instances.Where(i => string.Equals(i.Id, spec, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return RouteResult.Ok(exact[0], "id");

            if (int.TryParse(spec, out var pid))
            {
                var byPid = instances.Where(i => i.Pid == pid).ToList();
                if (byPid.Count == 1) return RouteResult.Ok(byPid[0], "pid");
            }

            var byPrefix = instances
                .Where(i => i.Id.StartsWith(spec, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byPrefix.Count == 1) return RouteResult.Ok(byPrefix[0], "prefix");
            if (byPrefix.Count > 1)
                return new RouteResult { Outcome = RouteOutcome.Ambiguous, Candidates = byPrefix };

            return new RouteResult
            {
                Outcome = RouteOutcome.NoMatch,
                Candidates = instances.ToList()
            };
        }

        /// <summary>
        /// Routes by working directory.
        ///
        /// Tier 1, the workspace encloses the directory: the agent is working inside a
        /// solution that some instance has open. The nearest enclosing workspace wins.
        ///
        /// Tier 2, the directory encloses the workspace: the agent was started at a
        /// repository root and the solution lives in a subdirectory. Common enough to
        /// handle, but only when exactly one instance qualifies.
        ///
        /// Anything else asks rather than guesses.
        /// </summary>
        public static RouteResult ByDirectory(IReadOnlyList<InstanceRecord> instances, string cwd)
        {
            if (instances == null || instances.Count == 0)
                return new RouteResult { Outcome = RouteOutcome.NoInstances };

            var enclosing = new List<KeyValuePair<int, InstanceRecord>>();
            foreach (var inst in instances)
            {
                var score = EnclosingScore(inst, cwd);
                if (score > 0) enclosing.Add(new KeyValuePair<int, InstanceRecord>(score, inst));
            }

            if (enclosing.Count > 0)
            {
                var best = enclosing.Max(p => p.Key);
                var winners = enclosing.Where(p => p.Key == best).Select(p => p.Value).ToList();
                if (winners.Count == 1) return RouteResult.Ok(winners[0], "directory");
                return new RouteResult { Outcome = RouteOutcome.Ambiguous, Candidates = winners };
            }

            var under = instances
                .Where(i => i.Workspace != null && PathUtil.Contains(cwd, i.Workspace.Root))
                .ToList();

            if (under.Count == 1) return RouteResult.Ok(under[0], "workspace under cwd");
            if (under.Count > 1)
                return new RouteResult { Outcome = RouteOutcome.Ambiguous, Candidates = under };

            return new RouteResult
            {
                Outcome = RouteOutcome.NoMatch,
                Candidates = instances.ToList()
            };
        }

        static int EnclosingScore(InstanceRecord inst, string cwd)
        {
            var best = 0;
            if (inst.Workspace != null)
                best = Math.Max(best, PathUtil.Specificity(inst.Workspace.Root, cwd));

            if (inst.ProjectDirs != null)
            {
                foreach (var dir in inst.ProjectDirs)
                    best = Math.Max(best, PathUtil.Specificity(dir, cwd));
            }
            return best;
        }

        /// <summary>
        /// The error an agent reads when routing could not decide. It names the
        /// candidates and the literal value to pass, so the next call succeeds.
        /// </summary>
        public static string Explain(RouteResult result, string cwd)
        {
            switch (result.Outcome)
            {
                case RouteOutcome.NoInstances:
                    return "No Visual Studio instance is running with the vsdbgmcp extension loaded. " +
                           "Start Visual Studio and open a solution, then retry.";

                case RouteOutcome.Ambiguous:
                    return "Several instances match. Pass instance= to choose:\n" + Table(result.Candidates);

                case RouteOutcome.NoMatch:
                    return "No running instance matches '" + cwd + "'. Pass instance= to choose one:\n" +
                           Table(result.Candidates);

                default:
                    return null;
            }
        }

        static string Table(List<InstanceRecord> rows)
        {
            if (rows == null || rows.Count == 0) return "  (none)";

            var idWidth = Math.Max(8, rows.Max(r => r.Id.Length));
            var rootWidth = Math.Max(8, rows.Max(r => (r.Workspace?.Root ?? "").Length));

            var lines = rows.Select(r =>
                "  " + r.Id.PadRight(idWidth) +
                "  " + (r.Workspace?.Root ?? "").PadRight(rootWidth) +
                "  " + System.IO.Path.GetFileName(r.Workspace?.File ?? "(no solution)") +
                "  " + (r.DebugMode ?? DebugModes.Design));

            return string.Join("\n", lines);
        }
    }
}
