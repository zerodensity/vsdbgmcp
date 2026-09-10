using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Shim.Builds;
using Xunit;

namespace VsDbgMcp.Tests
{
    public class BinaryBuildDiagnosticsTests
    {
        [Fact]
        public async Task Real_build_events_retain_ownership_counts_and_failure_without_rebuilding_on_read()
        {
            var directory = Path.Combine(Path.GetTempPath(), "vsdbg-binlog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var project = Path.Combine(directory, "Example.proj");
                var log = Path.Combine(directory, "build.binlog");
                File.WriteAllText(project, """
                    <Project DefaultTargets="Build">
                      <Target Name="Build">
                        <Warning Text="Repeated diagnostic" Code="TEST001" File="sample.cpp" />
                        <Warning Text="Repeated diagnostic" Code="TEST001" File="sample.cpp" />
                        <Error Text="Expected failure" Code="TEST002" File="sample.cpp" />
                      </Target>
                    </Project>
                    """);
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "msbuild", project, "-nologo", "-nr:false", "-p:Configuration=Debug", "-p:Platform=x64", "-bl:" + log + ";ProjectImports=None" }) start.ArgumentList.Add(arg);
                using (var process = Process.Start(start))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    Assert.Equal(1, process.ExitCode);
                    await Task.WhenAll(stdout, stderr);
                }
                File.Delete(project); // Reading must not require or execute the project.
                var result = BinaryBuildDiagnostics.Read(log, null, null, 100, CancellationToken.None);
                Assert.Null(result.Error);
                Assert.True(result.EventStreamComplete);
                Assert.Equal("failed", result.Outcome);
                Assert.Equal(2, result.Warnings);
                Assert.Equal(1, result.UniqueWarnings);
                Assert.Equal(1, result.Errors);
                Assert.All(result.Diagnostics, d => { Assert.Equal(project, d.Project); Assert.Equal("Debug|x64", d.Configuration); });
                var filtered = BinaryBuildDiagnostics.Read(log, "Example", "Debug", 1, CancellationToken.None);
                Assert.True(filtered.Truncated);
                Assert.Single(filtered.Diagnostics);
                Assert.Equal(3, filtered.MatchingDiagnostics);
                Assert.Equal(2, filtered.Warnings);
                var empty = BinaryBuildDiagnostics.Read(log, "other", null, 100, CancellationToken.None);
                Assert.Empty(empty.Diagnostics);
                Assert.Equal(1, empty.Errors);
                var corrupt = Path.Combine(directory, "corrupt.binlog");
                File.WriteAllBytes(corrupt, File.ReadAllBytes(log).Take(30).ToArray());
                var incomplete = BinaryBuildDiagnostics.Read(corrupt, null, null, 100, CancellationToken.None);
                Assert.False(incomplete.EventStreamComplete);
                Assert.Equal("unknown", incomplete.Outcome);
                Assert.NotNull(incomplete.Error);
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
