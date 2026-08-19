using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VsDbgMcp.Shim.Discovery
{
    /// <summary>
    /// Reads the instance directory. No daemon: the files are the registry, and a
    /// dead process leaves a file that is pruned the next time anyone looks.
    /// </summary>
    public sealed class InstanceStore
    {
        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        readonly string _dir;

        public InstanceStore(string dir = null)
        {
            _dir = dir ?? Names.InstanceDir;
        }

        public List<InstanceRecord> Discover()
        {
            var result = new List<InstanceRecord>();
            if (!Directory.Exists(_dir)) return result;

            string[] files;
            try
            {
                files = Directory.GetFiles(_dir, Names.InstanceFilePrefix + "*" + Names.InstanceFileSuffix);
            }
            catch
            {
                return result;
            }

            foreach (var file in files)
            {
                var record = Read(file);
                if (record == null)
                {
                    TryDelete(file);
                    continue;
                }

                if (!IsAlive(record.Pid))
                {
                    TryDelete(file);
                    continue;
                }

                result.Add(record);
            }

            result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        static InstanceRecord Read(string file)
        {
            try
            {
                var text = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(text)) return null;

                var record = JsonSerializer.Deserialize<InstanceRecord>(text, JsonOptions);
                if (record == null || record.Pid <= 0 || string.IsNullOrEmpty(record.Pipe)) return null;
                return record;
            }
            catch
            {
                // A half-written or corrupt file is treated as absent. The writer
                // replaces atomically, so this should only happen after a crash.
                return null;
            }
        }

        static bool IsAlive(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited;
            }
            catch
            {
                return false;
            }
        }

        static void TryDelete(string file)
        {
            try { File.Delete(file); } catch { /* another shim may have won the race */ }
        }
    }
}
