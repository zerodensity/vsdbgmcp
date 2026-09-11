using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Profiling
{
    public sealed class Captures
    {
        readonly object _gate = new object();
        readonly List<Capture> _taken = new List<Capture>();
        readonly string _directory;
        int _next = 1;
        public Captures(string directory = null)
        {
            _directory = directory;
            if (directory == null) return;
            Directory.CreateDirectory(directory);
            var files = Directory.EnumerateFiles(directory, "*.json").Select(p => new FileInfo(p)).Select(f =>
                new RetainedArtifact { Path = f.FullName, Bytes = f.Length, TimestampUtc = f.LastWriteTimeUtc }).ToList();
            foreach (var expired in RetentionPolicy.Expired(files, DateTime.UtcNow,
                RetentionPolicy.Setting("VSDBGMCP_AGGREGATE_RETENTION_DAYS", 30),
                (long)RetentionPolicy.Setting("VSDBGMCP_AGGREGATE_BUDGET_MB", 512) * 1024 * 1024)) File.Delete(expired.Path);
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var capture = JsonConvert.DeserializeObject<Capture>(File.ReadAllText(path));
                    if (capture == null) continue;
                    capture.Id = _next++;
                    _taken.Add(capture);
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
        }
        public IReadOnlyList<Capture> All { get { lock (_gate) return _taken.ToList(); } }
        public Capture Keep(Capture capture)
        {
            lock (_gate)
            {
                if (capture.CaptureId == null) capture.CaptureId = ShortId.New(id => _taken.Any(c => c.CaptureId == id) ||
                    (_directory != null && File.Exists(Path.Combine(_directory, id + ".json"))));
                if (!ShortId.IsCaptureId(capture.CaptureId)) throw new ArgumentException("Invalid captureId.");
                var existing = _taken.FirstOrDefault(c => c.CaptureId == capture.CaptureId);
                if (existing != null) return existing;
                capture.Id = _next++;
                if (_directory != null)
                {
                    var path = Path.Combine(_directory, capture.CaptureId + ".json");
                    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(capture));
                    File.Move(temp, path, true);
                }
                _taken.Add(capture);
                return capture;
            }
        }
        public Capture Find(int? id) { lock (_gate) return id == null ? _taken.LastOrDefault() : _taken.FirstOrDefault(c => c.Id == id); }
        public Capture FindStable(string id) { lock (_gate) return id == null ? null : _taken.FirstOrDefault(c => c.CaptureId == id); }

        public string Export(Capture capture, string directory, bool raw)
        {
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Use an absolute export directory.");
            if (raw && (capture.Metadata?.Path == null || !File.Exists(capture.Metadata.Path)))
                throw new InvalidOperationException("Raw trace is not retained. JSON and report remain available; export with includeRaw=false.");
            Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, "capture-" + capture.CaptureId);
            var json = stem + ".json";
            var report = stem + ".txt";
            var trace = stem + ".diagsession";
            if (File.Exists(json) || File.Exists(report) || (raw && File.Exists(trace))) throw new IOException("An export with this captureId already exists.");
            using (var writer = new StreamWriter(new FileStream(json, FileMode.CreateNew))) writer.Write(JsonConvert.SerializeObject(capture, Formatting.Indented));
            using (var writer = new StreamWriter(new FileStream(report, FileMode.CreateNew))) writer.Write(Render.Profile(capture, new ProfileQuery { Details = true }, null, All));
            if (raw) File.Copy(capture.Metadata.Path, trace, false);
            return "Exported:\n" + json + "\n" + report + (raw ? "\n" + trace : "");
        }
        static string CollectionDirectory => Path.Combine(Names.InstanceDir, "profiles", "collections");
        public static ProfileCollection ReadCollection(string id)
        {
            if (!ShortId.IsCaptureId(id)) throw new ArgumentException("Invalid captureId. Use the ID returned by profile_status.");
            var collection = JsonConvert.DeserializeObject<ProfileCollection>(File.ReadAllText(Path.Combine(CollectionDirectory, id + ".json")));
            collection.RawAvailable = collection.Path != null && File.Exists(collection.Path);
            return collection;
        }
        public static List<ProfileCollection> Collections()
        {
            var result = new List<ProfileCollection>();
            if (!Directory.Exists(CollectionDirectory)) return result;
            foreach (var path in Directory.EnumerateFiles(CollectionDirectory, "*.json"))
            {
                try { var one = JsonConvert.DeserializeObject<ProfileCollection>(File.ReadAllText(path)); if (one != null) { one.RawAvailable = one.Path != null && File.Exists(one.Path); result.Add(one); } }
                catch (JsonException) { }
                catch (IOException) { }
            }
            return result.OrderByDescending(c => c.StartedUtc).ToList();
        }
    }
}
