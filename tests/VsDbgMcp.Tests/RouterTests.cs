using System.Collections.Generic;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// The milestone 1 acceptance criteria, as tests. Routing is the part that has to
    /// be right before anything else matters: a call sent to the wrong Visual Studio
    /// looks like a debugger bug, not a routing bug.
    /// </summary>
    public class RouterTests
    {
        static InstanceRecord Instance(string name, int pid, string root, string file = null,
            string filter = null, params string[] projectDirs)
        {
            return new InstanceRecord
            {
                Pid = pid,
                Pipe = "vsdbgmcp-" + pid,
                Contract = Names.ContractVersion,
                DebugMode = DebugModes.Design,
                ProjectDirs = projectDirs,
                Workspace = new WorkspaceInfo
                {
                    Name = name,
                    Root = root,
                    File = file ?? root + @"\" + name + ".sln",
                    Filter = filter,
                    Kind = filter != null ? WorkspaceKind.Slnf : WorkspaceKind.Sln
                }
            };
        }

        [Fact]
        public void One_instance_owning_the_directory_binds_with_no_configuration()
        {
            var instances = new List<InstanceRecord>
            {
                Instance("Engine", 100, @"D:\repo\Engine"),
                Instance("Editor", 200, @"D:\repo\Editor")
            };

            var route = Router.ByDirectory(instances, @"D:\repo\Engine\src");

            Assert.Equal(RouteOutcome.Resolved, route.Outcome);
            Assert.Equal("Engine#100", route.Instance.Id);
        }

        [Fact]
        public void The_nearest_enclosing_workspace_wins()
        {
            var instances = new List<InstanceRecord>
            {
                Instance("All", 100, @"D:\repo"),
                Instance("Engine", 200, @"D:\repo\Engine")
            };

            var route = Router.ByDirectory(instances, @"D:\repo\Engine\src");

            Assert.Equal(RouteOutcome.Resolved, route.Outcome);
            Assert.Equal("Engine#200", route.Instance.Id);
        }

        [Fact]
        public void A_project_directory_outside_the_solution_directory_still_routes()
        {
            var instances = new List<InstanceRecord>
            {
                Instance("Build", 100, @"D:\repo\build", null, null, @"D:\repo\libs\mesh")
            };

            var route = Router.ByDirectory(instances, @"D:\repo\libs\mesh\src");

            Assert.Equal(RouteOutcome.Resolved, route.Outcome);
        }

        [Fact]
        public void An_agent_started_at_the_repository_root_finds_the_solution_below_it()
        {
            var instances = new List<InstanceRecord>
            {
                Instance("Engine", 100, @"D:\repo\deep\nested\Engine")
            };

            var route = Router.ByDirectory(instances, @"D:\repo");

            Assert.Equal(RouteOutcome.Resolved, route.Outcome);
            Assert.Equal("workspace under cwd", route.How);
        }

        [Fact]
        public void Two_solutions_below_the_working_directory_ask_rather_than_guess()
        {
            var instances = new List<InstanceRecord>
            {
                Instance("Engine", 100, @"D:\repo\Engine"),
                Instance("Editor", 200, @"D:\repo\Editor")
            };

            var route = Router.ByDirectory(instances, @"D:\repo");

            Assert.Equal(RouteOutcome.Ambiguous, route.Outcome);
            Assert.Equal(2, route.Candidates.Count);
        }

        [Fact]
        public void The_same_solution_under_two_filters_asks_and_names_both()
        {
            var instances = new List<InstanceRecord>
            {
                Instance("Core", 100, @"D:\repo\App", null, @"D:\repo\App\Core.slnf"),
                Instance("Tools", 200, @"D:\repo\App", null, @"D:\repo\App\Tools.slnf")
            };

            var route = Router.ByDirectory(instances, @"D:\repo\App\src");
            Assert.Equal(RouteOutcome.Ambiguous, route.Outcome);

            var explanation = Router.Explain(route, @"D:\repo\App\src");
            Assert.Contains("Core#100", explanation);
            Assert.Contains("Tools#200", explanation);
            Assert.Contains("instance=", explanation);
        }

        [Fact]
        public void No_match_lists_what_is_actually_running()
        {
            var instances = new List<InstanceRecord> { Instance("Engine", 100, @"D:\repo\Engine") };

            var route = Router.ByDirectory(instances, @"C:\somewhere\else");

            Assert.Equal(RouteOutcome.NoMatch, route.Outcome);
            Assert.Contains("Engine#100", Router.Explain(route, @"C:\somewhere\else"));
        }

        [Fact]
        public void No_instances_at_all_says_so_plainly()
        {
            var route = Router.ByDirectory(new List<InstanceRecord>(), @"D:\repo");

            Assert.Equal(RouteOutcome.NoInstances, route.Outcome);
            Assert.Contains("No Visual Studio instance", Router.Explain(route, @"D:\repo"));
        }

        [Theory]
        [InlineData("Engine#100", "Engine#100")]
        [InlineData("100", "Engine#100")]
        [InlineData("Eng", "Engine#100")]
        [InlineData("engine#100", "Engine#100")]
        public void Explicit_selection_accepts_id_pid_and_prefix(string spec, string expected)
        {
            var instances = new List<InstanceRecord>
            {
                Instance("Engine", 100, @"D:\repo\Engine"),
                Instance("Editor", 200, @"D:\repo\Editor")
            };

            var route = Router.SelectExplicit(instances, spec);

            Assert.Equal(RouteOutcome.Resolved, route.Outcome);
            Assert.Equal(expected, route.Instance.Id);
        }

        [Fact]
        public void An_ambiguous_prefix_is_refused()
        {
            var instances = new List<InstanceRecord>
            {
                Instance("Engine", 100, @"D:\repo\Engine"),
                Instance("EngineTools", 200, @"D:\repo\EngineTools")
            };

            var route = Router.SelectExplicit(instances, "Engine");

            // "Engine" is an exact id for neither and a prefix of both.
            Assert.Equal(RouteOutcome.Ambiguous, route.Outcome);
        }

        [Fact]
        public void An_unknown_name_lists_the_running_instances()
        {
            var instances = new List<InstanceRecord> { Instance("Engine", 100, @"D:\repo\Engine") };

            var route = Router.SelectExplicit(instances, "Nope");

            Assert.Equal(RouteOutcome.NoMatch, route.Outcome);
            Assert.Single(route.Candidates);
        }
    }
}
