using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using VsDbgMcp.Contracts;

namespace VsDbgMcp
{
    public static class ProfileArtifacts
    {
        public static bool ReconcileClosedTrace(ProfileCollection collection)
        {
            if ((collection.Status != "stopping" && collection.Status != "stop-unknown") || !HasClosedTrace(collection.Path)) return false;
            collection.Status = "interrupted";
            collection.Error = null;
            collection.EndedUtc = File.GetLastWriteTimeUtc(collection.Path);
            collection.Seconds = Math.Max(0, (collection.EndedUtc.Value - collection.StartedUtc).TotalSeconds);
            collection.Interventions = collection.Interventions ?? new List<Intervention>();
            collection.Interventions.Add(new Intervention { Kind = "recovered-closed-package", TimestampUtc = DateTime.UtcNow,
                Detail = "Collection end estimated from package modification time; collector completion callback unavailable." });
            return true;
        }

        public static List<ModuleInfo> MergeModules(IEnumerable<ModuleInfo> saved, IEnumerable<ModuleInfo> observed)
        {
            return (saved ?? Enumerable.Empty<ModuleInfo>()).Concat(observed ?? Enumerable.Empty<ModuleInfo>())
                .GroupBy(m => (m.Path ?? m.Name ?? "") + "|" + m.Address, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.LastOrDefault(m => m.SymbolsLoaded && !string.IsNullOrEmpty(m.SymbolPath)) ?? g.Last()).ToList();
        }

        // The collector writes a ZIP package only when stopping. Require exclusive
        // access and a fully readable ETL entry before reconciling an interrupted stop.
        // TraceReader subsequently validates the ETL itself before persisting results.
        public static bool HasClosedTrace(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".etl", StringComparison.OrdinalIgnoreCase) && e.Length > 0);
                    if (entry == null) return false;
                    using (var stream = entry.Open())
                    {
                        var buffer = new byte[81920];
                        long read = 0;
                        int count;
                        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) read += count;
                        return read == entry.Length;
                    }
                }
            }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
