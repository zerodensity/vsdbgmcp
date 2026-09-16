using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    static class HostOperations
    {
        static readonly object DiskGate = new object();
        static readonly string Root = Path.Combine(Names.InstanceDir, "operations");
        public static readonly OperationRegistry Store = new OperationRegistry(Copy, Save, Process.GetCurrentProcess().Id.ToString(), HasHistory,
            epoch => Directory.Exists(Path.Combine(Root, epoch)));
        public static readonly string DirectoryPath = Path.Combine(Root, Store.Epoch);
        public static T Copy<T>(T value) => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value));
        public static void Run(string id, Func<Task> work)
        {
            _ = Task.Run(async () =>
            {
                try { await work().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    var observed = Store.Read(id);
                    var issued = observed.State != "requested";
                    Store.Update(id, o => { o.State = issued ? "unknown" : "failed"; o.Message = ex.Message +
                        (issued ? " The request may remain inside VS; query operation_status before retrying." : ""); o.EvidenceSource = "host exception"; }, !issued);
                }
            });
        }
        static void Save(OperationInfo info)
        {
            // Updates may race; always serialize the newest in-memory snapshot.
            lock (DiskGate)
            {
                {
                    Directory.CreateDirectory(DirectoryPath);
                    var latest = Store.Read(info.OperationId) ?? info;
                    latest.InstanceId = Process.GetCurrentProcess().Id.ToString();
                    var path = Path.Combine(DirectoryPath, info.OperationId + ".json");
                    var temp = path + ".tmp";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(latest, Formatting.Indented));
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
            }
        }
        public static OperationInfo Historical(string id)
        {
            if (id == null || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..")) return null;
            if (!Directory.Exists(Root)) return null;
            foreach (var dir in Directory.EnumerateDirectories(Root))
            {
                var path = Path.Combine(dir, id + ".json");
                if (!File.Exists(path)) continue;
                var record = JsonConvert.DeserializeObject<OperationInfo>(File.ReadAllText(path));
                record.Historical = true;
                if (!record.Terminal)
                {
                    record.State = "unknown";
                    record.Message = "Host restarted or unavailable; the saved record does not establish completion. Inspect the command's effects before deciding whether to start another request.";
                }
                return record;
            }
            return null;
        }

        static bool HasHistory(string id)
        {
            return Directory.Exists(Root) && Directory.EnumerateDirectories(Root).Any(dir => File.Exists(Path.Combine(dir, id + ".json")));
        }
    }
}
