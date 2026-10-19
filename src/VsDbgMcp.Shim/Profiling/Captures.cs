using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Profiling
{
    /// <summary>A saved capture whose file is gone or damaged. The message names the capture and the file.</summary>
    public sealed class UnreadableCaptureException : InvalidOperationException
    {
        public UnreadableCaptureException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The captures this shim can report on. Saved captures stay on disk until a report
    /// asks for one: together they can be hundreds of megabytes, and every MCP client
    /// starts its own shim.
    /// </summary>
    public sealed class Captures
    {
        /// <summary>A capture as the capture list shows it, and where the rest of it is saved.</summary>
        public sealed class Entry
        {
            public int Id { get; internal set; }
            public string CaptureId { get; internal set; }
            public double Seconds { get; internal set; }
            public int Stacked { get; internal set; }

            /// <summary>Null in a store without a directory, which holds every capture in memory.</summary>
            internal string Path { get; set; }
        }

        /// <summary>How many whole captures stay in memory: a report and the one it is compared against.</summary>
        public const int LoadedLimit = 2;

        readonly object _gate = new object();
        readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Whole captures in memory, least recently used first.</summary>
        readonly List<Capture> _loaded = new List<Capture>();
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
                    var entry = ReadEntry(path);
                    if (entry == null) continue;
                    entry.Id = _next++;
                    _entries.Add(entry);
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
        }

        /// <summary>
        /// Reads the fields the capture list needs from a saved capture. They are written
        /// before the frames and stacks, so reading stops long before the bulk of the file.
        /// </summary>
        static Entry ReadEntry(string path)
        {
            using (var reader = new JsonTextReader(File.OpenText(path)))
            {
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject) return null;
                var entry = new Entry { Path = path };
                bool captureId = false, seconds = false, stacked = false;
                while (!(captureId && seconds && stacked) && reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    var name = (string)reader.Value;
                    reader.Read();
                    switch (name)
                    {
                        case nameof(Capture.CaptureId): entry.CaptureId = reader.Value as string; captureId = true; break;
                        case nameof(Capture.Seconds): entry.Seconds = Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture); seconds = true; break;
                        case nameof(Capture.Stacked): entry.Stacked = Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture); stacked = true; break;
                        default: reader.Skip(); break;
                    }
                }
                return entry;
            }
        }

        /// <summary>The whole capture, from memory when it was used recently, else from its file.</summary>
        Capture Load(Entry entry)
        {
            var held = _loaded.FirstOrDefault(c => c.Id == entry.Id);
            if (held != null)
            {
                _loaded.Remove(held);
                _loaded.Add(held);
                return held;
            }

            Capture capture;
            try
            {
                using (var reader = new JsonTextReader(File.OpenText(entry.Path)))
                    capture = JsonSerializer.CreateDefault().Deserialize<Capture>(reader);
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new UnreadableCaptureException("Capture #" + entry.Id + " (captureId " + entry.CaptureId + ") could not be read from " +
                    entry.Path + ": " + ex.Message, ex);
            }
            if (capture == null) throw new UnreadableCaptureException("Capture #" + entry.Id + " (captureId " + entry.CaptureId + ") is empty: " + entry.Path, null);
            capture.Id = entry.Id;
            Hold(capture);
            return capture;
        }

        void Hold(Capture capture)
        {
            _loaded.Add(capture);
            if (_directory != null && _loaded.Count > LoadedLimit) _loaded.RemoveAt(0);
        }

        public IReadOnlyList<Entry> All { get { lock (_gate) return _entries.ToList(); } }

        /// <summary>How many whole captures are in memory now.</summary>
        public int LoadedCount { get { lock (_gate) return _loaded.Count; } }

        public Capture Keep(Capture capture)
        {
            lock (_gate)
            {
                if (capture.CaptureId == null) capture.CaptureId = ShortId.New(id => _entries.Any(e => e.CaptureId == id) ||
                    (_directory != null && File.Exists(Path.Combine(_directory, id + ".json"))));
                if (!ShortId.IsCaptureId(capture.CaptureId)) throw new ArgumentException("Invalid captureId.");
                var existing = _entries.FirstOrDefault(e => e.CaptureId == capture.CaptureId);
                if (existing != null)
                {
                    // A saved copy that can no longer be read is replaced by this one.
                    try { return Load(existing); }
                    catch (UnreadableCaptureException) { _entries.Remove(existing); }
                }
                capture.Id = _next++;
                string path = null;
                if (_directory != null)
                {
                    path = Path.Combine(_directory, capture.CaptureId + ".json");
                    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temp, JsonConvert.SerializeObject(capture));
                    File.Move(temp, path, true);
                }
                _entries.Add(new Entry { Id = capture.Id, CaptureId = capture.CaptureId, Seconds = capture.Seconds, Stacked = capture.Stacked, Path = path });
                Hold(capture);
                return capture;
            }
        }
        public Capture Find(int? id)
        {
            lock (_gate)
            {
                var entry = id == null ? _entries.LastOrDefault() : _entries.FirstOrDefault(e => e.Id == id);
                return entry == null ? null : Load(entry);
            }
        }
        public Capture FindStable(string id)
        {
            lock (_gate)
            {
                var entry = id == null ? null : _entries.FirstOrDefault(e => e.CaptureId == id);
                return entry == null ? null : Load(entry);
            }
        }

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
