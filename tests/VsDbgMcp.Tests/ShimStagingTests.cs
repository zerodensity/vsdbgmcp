using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VsDbgMcp.Host;
using Xunit;

namespace VsDbgMcp.Tests
{
    // Real Windows executable locks and redirected stdio. Every process and file
    // belongs to this test's temporary installation, never the user's MCP server.
    public class ShimStagingTests : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "vsdbgmcp-stage-test-" + Guid.NewGuid().ToString("N"));
        readonly List<Process> _children = new List<Process>();

        [Fact]
        public async Task Upgrade_retires_old_installation_only_and_next_launch_uses_new_version()
        {
            var oldBundle = Bundle("old", 1);
            var newBundle = Bundle("new", 2);
            var target = Install(oldBundle, "installed");
            var other = Install(oldBundle, "other");
            var first = await Start(target, 1);
            File.Move(Exe(target), Exe(target) + ".superseded-1");
            File.Copy(Exe(oldBundle), Exe(target));
            var second = await Start(target, 1);
            var unrelated = await Start(other, 1);

            var result = ShimStaging.Run(newBundle, target);

            Assert.Contains("staged 0.0.0.2", result);
            Assert.Contains("Stopped 2 previous shim process(es)", result);
            Assert.Contains("reconnect", result);
            Assert.True(first.HasExited);
            Assert.True(second.HasExited);
            Assert.False(unrelated.HasExited);
            var current = await Start(target, 2);
            Assert.Contains("already 0.0.0.2", ShimStaging.Run(newBundle, target));
            Assert.Contains("already 0.0.0.2", ShimStaging.Run(oldBundle, target));
            Assert.False(current.HasExited);
            Assert.False(unrelated.HasExited);
        }

        [Fact]
        public async Task Process_launched_after_capture_is_not_retired()
        {
            var oldBundle = Bundle("old", 1);
            var newBundle = Bundle("new", 2);
            var target = Install(oldBundle, "installed");
            var old = await Start(target, 1);
            File.Move(Exe(target), Exe(target) + ".superseded");
            using var captured = ShimProcesses.Capture(Exe(target));
            File.Copy(Exe(newBundle), Exe(target));
            var current = await Start(target, 2);

            Assert.Contains("Stopped 1", captured.Stop());

            Assert.True(old.HasExited);
            Assert.False(current.HasExited);
        }

        [Fact]
        public async Task Locked_dependency_leaves_existing_process_and_executable_in_place()
        {
            var oldBundle = Bundle("old", 1);
            var newBundle = Bundle("new", 2);
            var target = Install(oldBundle, "installed");
            var old = await Start(target, 1);
            using var locked = new FileStream(Path.Combine(target, "dependency.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);

            var result = ShimStaging.Run(newBundle, target);

            Assert.Contains("files are still in use", result);
            Assert.False(old.HasExited);
            Assert.Equal("0.0.0.1", FileVersionInfo.GetVersionInfo(Exe(target)).FileVersion);
            await Start(target, 1);
        }

        [Fact]
        public async Task Locked_executable_leaves_existing_process_alive_and_retry_succeeds()
        {
            var oldBundle = Bundle("old", 1);
            var newBundle = Bundle("new", 2);
            var target = Install(oldBundle, "installed");
            var old = await Start(target, 1);
            using (var locked = new FileStream(Exe(target), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var result = ShimStaging.Run(newBundle, target);
                Assert.Contains("could not replace", result);
                Assert.False(old.HasExited);
                Assert.Equal("0.0.0.1", FileVersionInfo.GetVersionInfo(Exe(target)).FileVersion);
            }
            Assert.Contains("Stopped 1", ShimStaging.Run(newBundle, target));
            Assert.True(old.HasExited);
            await Start(target, 2);
        }

        [Fact]
        public async Task Another_staging_pass_is_bounded_and_does_not_stop_any_process()
        {
            var oldBundle = Bundle("old", 1);
            var newBundle = Bundle("new", 2);
            var target = Install(oldBundle, "installed");
            var old = await Start(target, 1);
            using var hash = SHA256.Create();
            var name = @"Local\vsdbgmcp-stage-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(target.ToUpperInvariant())));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var owner = Task.Run(() =>
            {
                using var mutex = new Mutex(false, name);
                mutex.WaitOne();
                entered.Set();
                try { release.Wait(TimeSpan.FromSeconds(10)); }
                finally { mutex.ReleaseMutex(); }
            });
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                Assert.Contains("another window is staging", ShimStaging.Run(newBundle, target));
                Assert.False(old.HasExited);
            }
            finally { release.Set(); await owner; }
        }

        [Theory]
        [InlineData(@"D:\test\bin\vsdbgmcp.exe", true)]
        [InlineData(@"D:\TEST\bin\vsdbgmcp.exe.superseded", true)]
        [InlineData(@"D:\test\bin\vsdbgmcp.exe.superseded-99", true)]
        [InlineData(@"D:\test\bin\vsdbgmcp.exe.superseded-other", false)]
        [InlineData(@"D:\test\bin\vsdbgmcp.exe.superseded\vsdbgmcp.exe", false)]
        [InlineData(@"D:\test\bin-other\vsdbgmcp.exe", false)]
        [InlineData(@"D:\test\bin\child\vsdbgmcp.exe", false)]
        public void Process_selection_requires_exact_installation_and_known_executable_name(string image, bool expected)
        {
            Assert.Equal(expected, ShimProcesses.BelongsTo(image, @"D:\test\bin\vsdbgmcp.exe"));
        }

        string Bundle(string name, int version)
        {
            var dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            // Framework csc is installed with Windows/Visual Studio. The fixture has
            // no runtime files, letting tests isolate executable/dependency locks.
            var source = Path.Combine(_root, name + ".cs");
            File.WriteAllText(source,
                "using System; [assembly:System.Reflection.AssemblyFileVersion(\"0.0.0." + version + "\")] " +
                "class Program { static void Main() { Console.WriteLine(\"ready:" + version + "\"); " +
                "while (Console.ReadLine() != null) {} } }");
            var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
            var start = new ProcessStartInfo(compiler)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add("/nologo");
            start.ArgumentList.Add("/out:" + Exe(dir));
            start.ArgumentList.Add(source);
            using var process = Process.Start(start);
            Assert.True(process.WaitForExit(10000));
            Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
            File.WriteAllText(Path.Combine(dir, "dependency.txt"), version.ToString());
            return dir;
        }

        string Install(string source, string name)
        {
            var target = Path.Combine(_root, name);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            return target;
        }

        async Task<Process> Start(string directory, int version)
        {
            var start = new ProcessStartInfo(Exe(directory))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            var process = Process.Start(start);
            _children.Add(process);
            Assert.Equal("ready:" + version, await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            return process;
        }

        static string Exe(string directory) => Path.Combine(directory, "vsdbgmcp.exe");

        public void Dispose()
        {
            foreach (var child in _children)
            {
                if (!child.HasExited) child.Kill();
                child.WaitForExit(5000);
                child.Dispose();
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
