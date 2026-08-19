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
        /// </summary>
        public const int ContractVersion = 2;

        public const string InstanceFilePrefix = "inst-";
        public const string InstanceFileSuffix = ".json";

        /// <summary>Where instances announce themselves. One file per running VS.</summary>
        public static string InstanceDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Product);

        public static string InstanceFile(int pid) =>
            Path.Combine(InstanceDir, InstanceFilePrefix + pid + InstanceFileSuffix);

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
