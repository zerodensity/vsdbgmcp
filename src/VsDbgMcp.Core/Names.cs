using System;
using System.IO;

namespace VsDbgMcp
{
    /// <summary>
    /// Every name this product claims on the machine, in one place.
    /// </summary>
    public static class Names
    {
        public const string Product = "vsdbgmcp";

        /// <summary>
        /// Bumped when IDebugHost or IProjectSystem change shape.
        /// 2: threads and selection carry the process they belong to.
        /// 3: the shim reports each call so the panel can show what was returned.
        /// 4: module loads are their own event, tracepoints have their own sink,
        ///    and modules report how many were loaded before the filter.
        /// </summary>
        public const int ContractVersion = 4;

        public const string InstanceFilePrefix = "inst-";
        public const string InstanceFileSuffix = ".json";

        /// <summary>Where instances announce themselves. One file per running VS.</summary>
        public static string InstanceDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Product);

        public static string InstanceFile(int pid) =>
            Path.Combine(InstanceDir, InstanceFilePrefix + pid + InstanceFileSuffix);

        /// <summary>
        /// Where the extension stages the shim. An agent names the shim by absolute
        /// path, and Visual Studio regenerates the extension's own folder on every
        /// update, so the path an agent is configured with cannot be that one.
        /// </summary>
        public static string ShimDir => Path.Combine(InstanceDir, "bin");

        public static string ShimExe => Path.Combine(ShimDir, Product + ".exe");

        public static string PipeName(int pid) => Product + "-" + pid;
    }

    /// <summary>What an instance can do. Sent at handshake so the shim can hide tools it cannot serve.</summary>
    public static class Capabilities
    {
        public const string Native = "native";
        public const string Managed = "managed";
        public const string DataBreakpoints = "dataBreakpoints";
        public const string Disassembly = "disasm";
        public const string Dumps = "dumps";
        public const string ConsoleIo = "consoleIo";
        public const string WindowCapture = "windowCapture";
    }
}
