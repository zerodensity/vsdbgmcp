using System;
using System.Collections.Generic;
using System.Linq;

namespace VsDbgMcp
{
    public sealed class RetainedArtifact
    {
        public string Path { get; set; }
        public long Bytes { get; set; }
        public DateTime TimestampUtc { get; set; }
        public bool Protected { get; set; }
    }

    public static class RetentionPolicy
    {
        // Only caller-owned paths enter this decision. Active/unknown collectors are protected.
        public static List<RetainedArtifact> Expired(IEnumerable<RetainedArtifact> artifacts, DateTime now, int days, long maxBytes)
        {
            var all = artifacts.OrderBy(a => a.TimestampUtc).ToList();
            var bytes = all.Sum(a => a.Bytes);
            var remove = new List<RetainedArtifact>();
            foreach (var file in all)
            {
                if (file.Protected) continue;
                if (file.TimestampUtc >= now.AddDays(-days) && bytes <= maxBytes) continue;
                remove.Add(file);
                bytes -= file.Bytes;
            }
            return remove;
        }
        public static int Setting(string variable, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) && value > 0 ? value : fallback;
    }
}
