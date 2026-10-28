using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Diagnostics;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Profiling
{
    /// <summary>
    /// A saved capture that could not be loaded. The message names the capture and the file;
    /// the properties carry them apart from the message, because the next step is a tool call
    /// and a model should not have to take an identifier back out of a sentence.
    /// </summary>
    public abstract class CaptureReadException : InvalidOperationException
    {
        private protected CaptureReadException(string message, Exception inner, string captureId, string path) : base(message, inner)
        {
            CaptureId = captureId;
            Path = path;
        }

        /// <summary>The captureId the capture is listed under.</summary>
        public string CaptureId { get; }

        /// <summary>The saved file this capture should have come from.</summary>
        public string Path { get; }
    }

    /// <summary>A saved capture whose file is gone or damaged, which profile_recover can rebuild
    /// while the raw trace is kept.</summary>
    public sealed class UnreadableCaptureException : CaptureReadException
    {
        public UnreadableCaptureException(string message, Exception inner, string captureId, string path) : base(message, inner, captureId, path) { }
    }

    /// <summary>A saved capture whose file could not be opened just now, because something holds
    /// it or this process may not read it. Nothing says the file is damaged, so rebuilding it is
    /// not the answer; the same call can be tried again.</summary>
    public sealed class CaptureUnavailableException : CaptureReadException
    {
        public CaptureUnavailableException(string message, Exception inner, string captureId, string path) : base(message, inner, captureId, path) { }
    }

    /// <summary>A capture that was collected and could not be saved over the copy on disk. The
    /// capture exists and the raw evidence with it, so the next call is the same one that made
    /// this: the message says which, rather than leaving a path for a person to interpret.</summary>
    public sealed class CaptureUnsavableException : InvalidOperationException
    {
        public CaptureUnsavableException(string message, Exception inner) : base(message, inner) { }
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
            internal Entry(string captureId, string path, Contents contents)
            {
                CaptureId = captureId;
                Path = path;
                Contents = contents;
            }

            public int Id { get; internal set; }
            public string CaptureId { get; }

            /// <summary>What the list knows of the capture: its numbers, or why it could not be read.
            /// Replaced whole, so a reader outside the gate never sees half of a change.</summary>
            public Contents Contents { get; internal set; }

            public bool Unreadable => Contents is Contents.Unreadable;

            /// <summary>What stopped it being read, or null when it reads.</summary>
            public string Reason => (Contents as Contents.Unreadable)?.Reason;

            /// <summary>Null in a store without a directory, which holds every capture in memory.</summary>
            internal string Path { get; }
        }

        /// <summary>
        /// A listed capture's numbers when its file reads, or the reason it does not. An
        /// unreadable capture has no seconds and no samples to print, rather than zeros.
        /// </summary>
        public abstract class Contents
        {
            Contents() { }

            public sealed class Readable : Contents
            {
                internal Readable(double seconds, int stacked)
                {
                    Seconds = seconds;
                    Stacked = stacked;
                }

                public double Seconds { get; }
                public int Stacked { get; }
            }

            public sealed class Unreadable : Contents
            {
                /// <summary>The reason is text out of the file, so it is made one bounded line here,
                /// once, and every reply that prints it can trust that.</summary>
                internal Unreadable(string reason, FileStamp? stamp)
                {
                    Reason = OneLine(reason);
                    Stamp = stamp;
                }

                /// <summary>What stopped it being read, as one clause.</summary>
                public string Reason { get; }

                /// <summary>The file as it was when it was judged, or null when it could not be
                /// looked at. A file that differs from this is judged again.</summary>
                internal FileStamp? Stamp { get; }
            }
        }

        /// <summary>When a file was last written and how long it is: enough to tell that another
        /// shim sharing the directory has rewritten it.</summary>
        internal readonly record struct FileStamp(DateTime WrittenUtc, long Bytes)
        {
            public static FileStamp? Of(string path)
            {
                try
                {
                    var info = new FileInfo(path);
                    return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
                }
                catch (Exception) { return null; }
            }
        }

        /// <summary>How many whole captures stay in memory: a report and the one it is compared against.</summary>
        public const int LoadedLimit = 2;

        /// <summary>
        /// How much of them memory may hold, in megabytes of saved JSON. Two captures is not
        /// a bound on size: two of a hundred megabytes are the same count and a gigabyte of
        /// heap, which is the complaint this store exists to answer. What is counted is the
        /// file's own length, measured rather than guessed; the heap costs three and a half
        /// times that, measured on a twenty megabyte capture, which is why the default is a
        /// quarter of a gigabyte rather than a gigabyte.
        /// </summary>
        public const int LoadedBudgetMb = 256;

        /// <summary>Environment override for the budget, in megabytes.</summary>
        public const string LoadedBudgetSetting = "VSDBGMCP_LOADED_BUDGET_MB";

        /// <summary>A whole capture in memory, and the saved size it is counted against.</summary>
        sealed class Loaded
        {
            public Entry Entry;
            public Capture Capture;
            public long Bytes;
        }

        /// <summary>
        /// Guards the lists and the counter, and nothing else. Reads of a file and writes of
        /// one happen outside it: profile_report is declared read-only, so several calls can
        /// be in this store at once, and a call that only wants the capture list must not
        /// wait behind one big deserialize.
        /// </summary>
        readonly object _gate = new object();
        readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Whole captures in memory, least recently used first.</summary>
        readonly List<Loaded> _loaded = new List<Loaded>();

        /// <summary>
        /// One lock per captureId, so two callers asking for the same capture pay for it once
        /// while callers asking for different captures never wait on each other.
        ///
        /// These are kept for the life of the store rather than bounded. A cap was tried, and it
        /// cost more than it saved: evicting an idle lock lets two readers of one twenty-two
        /// megabyte capture deserialize it at the same time, which measured 721 ms and 721 ms
        /// against one read, counted one copy against the byte budget while the heap held two,
        /// and is the peak this store exists to keep down. The most a long session can accumulate
        /// is one small object per capture it has read, and captures are bounded by retention.
        /// </summary>
        readonly Dictionary<string, SemaphoreSlim> _slots = new Dictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        /// <summary>How long to queue behind someone else's read of the capture being asked for
        /// before reading it here instead. Waiting on a stranger is not the same thing as waiting
        /// on a wedged handle: an SMB share or an antivirus filter can hold a file open for
        /// minutes, and a second read of one capture is cheaper than a caller that never returns.</summary>
        static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(30);

        readonly string _directory;
        readonly long _budget;
        long _loadedBytes;
        int _next = 1;

        public Captures(string directory = null)
        {
            _directory = directory;
            _budget = (long) RetentionPolicy.Setting(LoadedBudgetSetting, LoadedBudgetMb) * 1024 * 1024;
            if (directory == null) return;
            try
            {
                Directory.CreateDirectory(directory);
                SweepInterruptedWrites(directory);
                ListSaved(directory);
            }
            // Nothing in listing what is on disk is allowed to end this constructor. It runs
            // before the transport does, so a throw here is a shim that will not start and a
            // model that never sees a tool list - for this Visual Studio instance, every time,
            // until a person deletes a file. A store that opens with nothing in it is the worst
            // honest answer; a dead process is not an answer at all.
            catch (Exception) { }
        }

        /// <summary>
        /// Lists every saved capture, prunes what retention says has aged out, and numbers the
        /// rest in the order they were written. A file that cannot be looked at is still listed:
        /// one that is skipped entirely reads to a model as a capture that was never taken, and
        /// the model stops looking for it.
        /// </summary>
        void ListSaved(string directory)
        {
            List<Saved> saved;
            try
            {
                // Enumerating and statting are two moments, and another shim's retention pass
                // can delete a file between them. Each file is asked for on its own, so one
                // disappearing is one capture missing from a list, not a list missing.
                saved = Directory.EnumerateFiles(directory, "*.json").Select(Saved.Of).Where(f => f != null).ToList();
            }
            catch (Exception) { return; }

            foreach (var expired in RetentionPolicy.Expired(
                saved.Select(f => new RetainedArtifact { Path = f.Path, Bytes = f.Bytes, TimestampUtc = f.PruneBy }).ToList(),
                DateTime.UtcNow,
                RetentionPolicy.Setting("VSDBGMCP_AGGREGATE_RETENTION_DAYS", 30),
                (long) RetentionPolicy.Setting("VSDBGMCP_AGGREGATE_BUDGET_MB", 512) * 1024 * 1024))
            {
                // A file this process may not delete is a capture the model cannot report on,
                // which the list says plainly. It is not a reason to forget the file exists, so
                // only a delete that worked takes the file out of the listing below.
                try { File.Delete(expired.Path); }
                catch (Exception ex)
                {
                    // A file that went away on its own is the same outcome as a delete. Anything
                    // else - one another shim is reading, one this process may not touch - stays
                    // listed, because the capture is still on disk and still reportable.
                    if (!(ex is FileNotFoundException || ex is DirectoryNotFoundException)) continue;
                }
                saved.RemoveAll(f => string.Equals(f.Path, expired.Path, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var file in saved.OrderBy(f => f.Written))
            {
                var entry = ReadEntry(file.Path);
                entry.Id = _next++;
                _entries.Add(entry);
            }
        }

        /// <summary>A saved file as far as it could be looked at.</summary>
        sealed class Saved
        {
            public string Path;
            public long Bytes;

            /// <summary>The stamp retention prunes by. A timestamp this process could not read
            /// is the newest there is, because pruning oldest first must not act on a guess.</summary>
            public DateTime PruneBy;

            /// <summary>The stamp the list is ordered by. The same file, and the opposite guess:
            /// a file that could not be read is not owed the newest number in the report, nor the
            /// default that <c>profile_report</c> takes from it.</summary>
            public DateTime Written;

            public static Saved Of(string path)
            {
                var saved = new Saved { Path = path };
                try
                {
                    var info = new FileInfo(path);
                    saved.Bytes = info.Length;
                    saved.PruneBy = saved.Written = info.LastWriteTimeUtc;
                }
                // Deleted since the listing, usually by another shim's retention pass. Listing it
                // would send a model to recover a capture that was removed on purpose.
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException) { return null; }
                catch (Exception)
                {
                    // It was in the listing, so it is a capture until reading it says otherwise,
                    // and the reason comes from that rather than from a stat that could not run.
                    // Pruning must not act on a timestamp this process could not confirm, and a
                    // file nobody could date is not owed the newest number in the report either.
                    saved.PruneBy = DateTime.MaxValue;
                    saved.Written = DateTime.MinValue;
                }
                return saved;
            }
        }

        /// <summary>How long before a temporary file with no live owner behind it is taken for
        /// an interrupted write. A capture is written and moved into place within the seconds it
        /// takes to serialize, and the owner's own process says whether it is still around.</summary>
        static readonly TimeSpan InterruptedWriteAfter = TimeSpan.FromMinutes(1);

        /// <summary>However live its owner looks, a temporary this old was not written by anyone
        /// still working: a process number gets reused, and a file that outlives the cap is left
        /// by something that will never come back for it. No capture takes a day to serialize.</summary>
        static readonly TimeSpan InterruptedWriteAnyway = TimeSpan.FromDays(1);

        /// <summary>The process id every temporary file carries, so that a sweep can tell a file
        /// its writer is still building from one its writer left behind.</summary>
        static readonly string Owner = Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);

        /// <summary>A temporary name for a file: the destination, this process, and a nonce so
        /// that two writers of one destination never share a temporary.</summary>
        static string TempName(string path) =>
            path + "." + Owner + "-" + Guid.NewGuid().ToString("N") + ".tmp";

        /// <summary>
        /// Deletes temporary files left by writes that never finished. A capture is written
        /// under a temporary name and moved into place, so one of these is invisible to the
        /// listing and to the retention budget and would sit on the disk forever.
        ///
        /// Shims share one directory, so nothing about a temporary is evidence on its own. Age
        /// comes first because it is always safe: a write in progress is never older than the
        /// grace period. Past that, a temporary names the process that wrote it, and that process
        /// being gone is what permits the delete - age alone would collect a five minute capture
        /// out from under the shim writing it. Files written by an older release, which named
        /// them without a process, are collected once they are past the grace period, and a file
        /// somebody has open is left however dead its owner looks.
        /// </summary>
        static void SweepInterruptedWrites(string directory)
        {
            List<string> temps;
            try { temps = Directory.EnumerateFiles(directory, "*.json.*.tmp").ToList(); }
            catch (Exception) { return; }
            Sweep(temps);
        }

        /// <summary>
        /// Deletes an interrupted write from a directory this store does not own. An export goes
        /// wherever the caller named, so nothing else will ever list what a killed export left
        /// there, and a file no listing sees is a file no retention budget counts either.
        ///
        /// That directory holds the user's own files, so only a name an export writes, carrying
        /// the process that wrote it, is deleted. The no-process names an older release used
        /// are collected only in the store's own directory.
        /// </summary>
        static void SweepExportTemps(string directory)
        {
            var temps = new List<string>();
            foreach (var pattern in new[] { "capture-*.json.*.tmp", "capture-*.txt.*.tmp" })
                try { temps.AddRange(Directory.EnumerateFiles(directory, pattern)); }
                catch (Exception) { }
            Sweep(temps.Where(t => Path.GetFileName(t).StartsWith("capture-", StringComparison.Ordinal) && OwnerOf(t) != null).ToList());
        }

        static void Sweep(List<string> temps)
        {
            var now = DateTime.UtcNow;
            foreach (var temp in temps)
            {
                try
                {
                    DateTime written;
                    try { written = new FileInfo(temp).LastWriteTimeUtc; }
                    // It was listed and is not there to look at: someone removed it.
                    catch (Exception) { continue; }

                    var age = now - written;
                    if (age < InterruptedWriteAfter) continue;

                    var owner = OwnerOf(temp);
                    // A live owner is only evidence while the write it is doing could still be
                    // running, which the hard cap above answers.
                    if (owner != null && IsAlive(owner.Value) && age < InterruptedWriteAnyway) continue;

                    // Even for a dead owner: if the file is open now, something else has it,
                    // and a sweep is not a reason to disturb a write in progress.
                    try { using (File.Open(temp, FileMode.Open, FileAccess.Read, FileShare.None)) { } }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }

                    File.Delete(temp);
                }
                // A sweep that cannot see, cannot tell whose temporary this is, and cannot
                // delete it, costs one file. It is not a reason to start the shim with nothing.
                catch (Exception) { }
            }
        }

        /// <summary>The process a temporary names, or null when it names none: the shape this
        /// product writes is <c>name.json.&lt;pid&gt;-&lt;32 hex digits&gt;.tmp</c>, and anything else —
        /// including the name an older shim used, which carried no process — has no owner to ask.</summary>
        static int? OwnerOf(string path)
        {
            var file = Path.GetFileName(path);
            if (!file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return null;
            var name = file.Substring(0, file.Length - ".tmp".Length);
            var dot = name.LastIndexOf('.');
            if (dot < 0) return null;
            var dash = name.IndexOf('-', dot);
            if (dash < 0) return null;
            var nonce = name.Substring(dash + 1);
            if (nonce.Length != 32 || !nonce.All(Uri.IsHexDigit)) return null;
            return int.TryParse(name.Substring(dot + 1, dash - dot - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
                ? pid : (int?) null;
        }

        static bool IsAlive(int pid)
        {
            try { return !Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return false; }      // no such process: the writer is gone
            catch (InvalidOperationException) { return false; }
            // A process this one may not look at is a process that exists, and may be writing.
            catch (Exception) { return true; }
        }

        /// <summary>
        /// Reads the fields the capture list needs from a saved capture. They are written
        /// before the frames and stacks, so reading stops long before the bulk of the file.
        ///
        /// A header that is missing or mistypes one of them is not a capture that ran for no
        /// time and sampled nothing: it is a file whose numbers are unknown, and reporting
        /// zeros would send a model looking for an idle program. Such a file is listed as
        /// unreadable instead, which is also what happens to one that cannot be opened or
        /// parsed at all. Nothing here throws, because the constructor runs before the
        /// transport does and a file that aborts it would leave the shim dead for good.
        /// </summary>
        static Entry ReadEntry(string path)
        {
            // The file name is the captureId, so a header that lost it still names a capture
            // the model can recover. A name that is not a captureId is still printed, so it is
            // made one bounded line like any other text out of the store's directory.
            var name = Path.GetFileNameWithoutExtension(path);
            var captureId = ShortId.IsCaptureId(name) ? name : OneLine(name);
            return new Entry(captureId, path, ReadHeader(path, captureId));
        }

        static Contents ReadHeader(string path, string captureId)
        {
            // Taken before the read, so a file rewritten during it is judged again next time.
            var stamp = FileStamp.Of(path);
            Contents Bad(string reason) => new Contents.Unreadable(reason, stamp);

            double? seconds = null;
            int? stacked = null;
            try
            {
                using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new JsonTextReader(new StreamReader(file, Encoding.UTF8, true)))
                {
                    // A capture is moved into place once it is whole, so an empty file here is
                    // one being replaced under this process. "Not a saved capture" would blame
                    // the file for a moment; this says what was seen.
                    if (file.Length == 0) return Bad("it is empty, which a write in progress looks like");
                    if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                        return Bad("its JSON is not a saved capture");

                    while (!(seconds != null && stacked != null) && reader.Read() && reader.TokenType == JsonToken.PropertyName)
                    {
                        var name = (string) reader.Value;
                        // The frames are the bulk, and a header field is never inside them.
                        // Stopping here is what keeps a damaged header costing a few kilobytes
                        // rather than the whole file.
                        if (name == nameof(Capture.Frames)) break;
                        if (!reader.Read()) break;
                        switch (name)
                        {
                            case nameof(Capture.CaptureId):
                                if (reader.TokenType == JsonToken.String)
                                {
                                    // The file's name is the identity every handle is offered
                                    // from, so a header claiming a different capture is not
                                    // owed the claim: it would give two files one captureId and
                                    // leave a handle resolving to whichever was written last.
                                    // Compared as written, so a claim that differs only in what
                                    // a reply would trim off is still a different claim.
                                    var claimed = (string) reader.Value;
                                    if (claimed != captureId)
                                        return Bad("it is " + captureId + ".json and its header says " + claimed);
                                }
                                else if (reader.TokenType != JsonToken.Null)
                                    return Bad("CaptureId is not text");
                                break;
                            case nameof(Capture.Seconds):
                                if (reader.TokenType != JsonToken.Integer && reader.TokenType != JsonToken.Float)
                                    return Bad("Seconds is not a number");
                                var read = Convert.ToDouble(reader.Value, CultureInfo.InvariantCulture);
                                // A JSON number too large for a double arrives as infinity
                                // without complaint, and a report printing infinity seconds is
                                // the same lie as one printing zero.
                                if (!double.IsFinite(read) || read < 0)
                                    return Bad("Seconds is not a duration");
                                seconds = read;
                                break;
                            case nameof(Capture.Stacked):
                                if (reader.TokenType != JsonToken.Integer)
                                    return Bad("Stacked is not a whole number");
                                var count = Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture);
                                if (count < 0) return Bad("Stacked is not a count");
                                stacked = count;
                                break;
                            default: reader.Skip(); break;
                        }
                    }
                }
                if (seconds == null) return Bad("its header has no Seconds");
                if (stacked == null) return Bad("its header has no Stacked");
                // What is left as the identity is the file's own name, which is a shape this
                // product chose; a file dropped in here that does not match it is not a capture.
                if (!ShortId.IsCaptureId(captureId)) return Bad(captureId + " is not a captureId");
                return new Contents.Readable(seconds.Value, stacked.Value);
            }
            catch (Exception ex) { return Bad(Why(ex)); }
        }

        const int ReasonLimit = 180;

        /// <summary>Text out of a saved file as one bounded line. A reply is data for a model
        /// rather than a transcript, so a saved capture must not be able to add a line that reads
        /// as the shim's own note, or a paragraph that pushes the report out of the reply.</summary>
        static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "it could not be read";
            var line = Fold(text);
            var cut = line.Length > ReasonLimit;
            // Fold leaves only whole surrogate pairs, so a cut that would land inside one moves
            // back a character rather than leaving half of it.
            if (cut) line = line.Substring(0, char.IsLowSurrogate(line[ReasonLimit]) ? ReasonLimit - 1 : ReasonLimit);
            line = line.TrimEnd();
            if (cut) return line + "...";
            // A fixed reason is a clause and carries no full stop; an exception message is a
            // sentence that does, and the footer prints the reason among clauses.
            return line.TrimEnd('.');
        }

        /// <summary>Text with every character that could start a new line, or change how the text
        /// around it reads, replaced by a space. Used whole for paths, which a reply prints as
        /// they are, and through OneLine for reasons.</summary>
        internal static string Fold(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                // A surrogate pair is kept whole; half of one is not text.
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c).Append(text[++i]);
                    continue;
                }
                sb.Append(BreaksLine(c) ? ' ' : c);
            }
            return sb.ToString();
        }

        /// <summary>Characters that start a new line or change how the text around them reads:
        /// controls, the Unicode line and paragraph separators, invisible format characters such
        /// as direction overrides, and a surrogate with no partner.</summary>
        static bool BreaksLine(char c)
        {
            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.Surrogate:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>What went wrong: the file's own words where they are short enough to quote,
        /// and the kind of failure where they are not.</summary>
        static string Why(Exception ex) =>
            (ex is JsonException ? "its JSON is malformed: " :
             ex is IOException || ex is UnauthorizedAccessException ? "it could not be read: " :
             ex.GetType().Name + ": ") + ex.Message;

        /// <summary>Whether a handle can be given to profile_recover, which refuses anything else.
        /// A file dropped into the directory by hand names a capture this store can list but no
        /// call can act on, and a reply that recommends a call certain to be refused is worse than
        /// one that says there is no call to make.</summary>
        public static bool CanRecover(string captureId) => ShortId.IsCaptureId(captureId);

        SemaphoreSlim Slot(string captureId)
        {
            lock (_gate)
            {
                SemaphoreSlim slot;
                if (!_slots.TryGetValue(captureId, out slot)) _slots[captureId] = slot = new SemaphoreSlim(1, 1);
                return slot;
            }
        }

        /// <summary>
        /// The whole capture, from memory when it was used recently, else from its file. The
        /// read happens outside the gate and is serialised only against other reads of the
        /// same capture, so a big capture costs whoever asked for it and nobody else.
        /// </summary>
        Capture Load(Entry entry, CancellationToken ct)
        {
            var slot = Slot(entry.CaptureId);
            // A caller that has given up is not a reason to keep queueing, and the way it finds
            // out is the ordinary one: the cancellation runs out through the tool as "Cancelled."
            var held = slot.Wait(LoadWait, ct);
            try { return LoadLocked(entry); }
            finally { if (held) slot.Release(); }
        }

        /// <summary>Loads with this capture's slot already held, so Keep can reuse it.</summary>
        Capture LoadLocked(Entry entry)
        {
            lock (_gate)
            {
                var held = _loaded.FirstOrDefault(l => l.Entry.Id == entry.Id);
                if (held != null)
                {
                    _loaded.Remove(held);
                    _loaded.Add(held);
                    return held.Capture;
                }
            }

            Recheck(entry);
            if (entry.Contents is Contents.Unreadable unreadable) throw Cannot(entry, unreadable.Reason, null);
            if (entry.Path == null) throw Cannot(entry, "it was never saved", null);

            var stamp = FileStamp.Of(entry.Path);
            Capture capture;
            long bytes;
            try
            {
                using (var file = File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new JsonTextReader(new StreamReader(file, Encoding.UTF8, true)))
                {
                    capture = JsonSerializer.CreateDefault().Deserialize<Capture>(reader);
                    bytes = file.Length;
                }
            }
            // Gone, usually by another shim's retention pass. It leaves the list as it would have at
            // startup, rather than being listed for recovery for the rest of this shim's life. This
            // call still says what happened and that profile_recover can rebuild it from a kept trace.
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException) { throw Gone(entry, ex); }
            // A file that could not be opened may be held by another process or not readable by
            // this one, which can pass and says nothing about its contents.
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { throw Unavailable(entry, Why(ex), ex); }
            catch (Exception ex) { throw Damaged(entry, Why(ex), ex, stamp); }

            if (capture == null) throw Damaged(entry, "it is empty", null, stamp);
            // The file's name is the identity. A header may carry none, but not a different one.
            if (capture.CaptureId != null && capture.CaptureId != entry.CaptureId)
                throw Damaged(entry, "it is " + entry.CaptureId + ".json and its header says " + capture.CaptureId, null, stamp);
            capture.Id = entry.Id;
            capture.CaptureId = entry.CaptureId;

            // A file can have a sound header and a body that cannot be reported on: a stack
            // naming a frame that is not there, or none at all. Deserializing it is not the
            // failure - reaching it a moment later inside a report is, and by then the call has
            // no idea which capture it came from and answers with a .NET message about an index.
            // Checked here, where the file is known, so the caller gets this capture's identity
            // and the next call to make.
            var damage = Damage(capture);
            if (damage != null) throw Damaged(entry, damage, null, stamp);

            lock (_gate)
            {
                // Keep can replace this entry while its file is open here. The replacement is
                // the capture that exists now, so this one is dropped rather than published.
                if (!_entries.Contains(entry)) throw Unavailable(entry, "it was replaced while it was being read", null);
                Hold(new Loaded { Entry = entry, Capture = capture, Bytes = bytes });
            }
            return capture;
        }

        /// <summary>
        /// Judges an unreadable capture again when its file has changed since it was judged:
        /// another shim sharing the directory may have rebuilt it. Only the header is read, as
        /// at startup; the body is checked by the next load.
        /// </summary>
        void Recheck(Entry entry)
        {
            if (!(entry.Contents is Contents.Unreadable unreadable) || entry.Path == null) return;
            var stamp = FileStamp.Of(entry.Path);
            if (stamp == null || stamp == unreadable.Stamp) return;
            var contents = ReadHeader(entry.Path, entry.CaptureId);
            lock (_gate)
                if (_entries.Contains(entry) && ReferenceEquals(entry.Contents, unreadable)) entry.Contents = contents;
        }

        /// <summary>
        /// The reason a capture could not be read, in the store's own words. What the file said
        /// goes through the same bound as a listing reason: a malformed body quotes back whatever
        /// the file held, and a reply is data for a model rather than a place a saved capture can
        /// write a paragraph, or a second line that reads as the shim's own note.
        /// </summary>
        UnreadableCaptureException Cannot(Entry entry, string why, Exception inner) =>
            new UnreadableCaptureException("Capture #" + entry.Id + " (captureId " + (entry.CaptureId ?? "?") + ") could not be read from " +
                Shown(entry.Path) + ": " + OneLine(why), inner, entry.CaptureId, entry.Path);

        CaptureUnavailableException Unavailable(Entry entry, string why, Exception inner) =>
            new CaptureUnavailableException("Capture #" + entry.Id + " (captureId " + (entry.CaptureId ?? "?") + ") could not be opened at " +
                Shown(entry.Path) + " just now: " + OneLine(why), inner, entry.CaptureId, entry.Path);

        static string Shown(string path) => path == null ? "a store with no directory" : Fold(path);

        /// <summary>A capture whose file is gone or whose body did not read. It is marked unreadable
        /// in the listing, so profile_status, the footer and the default report stop treating it
        /// as sound and stop reading the whole file again on every call, until the file changes.</summary>
        UnreadableCaptureException Damaged(Entry entry, string why, Exception inner, FileStamp? stamp)
        {
            lock (_gate) if (_entries.Contains(entry)) entry.Contents = new Contents.Unreadable(why, stamp);
            return Cannot(entry, why, inner);
        }

        UnreadableCaptureException Gone(Entry entry, Exception inner)
        {
            lock (_gate) _entries.Remove(entry);
            return Cannot(entry, "it is no longer on disk", inner);
        }

        /// <summary>What makes a capture that parsed into one that cannot be reported on, or null
        /// when it can. Every stack indexes into the frames, so a stack naming a frame the file
        /// never carried would fault the report rather than the read. The numbers the header
        /// carries are checked as the listing checks them.</summary>
        static string Damage(Capture capture)
        {
            if (!double.IsFinite(capture.Seconds) || capture.Seconds < 0) return "Seconds is not a duration";
            if (capture.Stacked < 0) return "Stacked is not a count";
            if (capture.Stacks == null) return "it has no stacks at all";
            if (capture.Frames == null) return capture.Stacks.Count > 0 ? "it has stacks and no frames" : (string) null;
            for (var i = 0; i < capture.Frames.Count; i++)
                if (capture.Frames[i] == null) return "frame " + i + " is missing";
            for (var i = 0; i < capture.Stacks.Count; i++)
            {
                var frames = capture.Stacks[i]?.Frames;
                if (frames == null) return "stack " + i + " has no frames";
                for (var f = 0; f < frames.Length; f++)
                    if (frames[f] < 0 || frames[f] >= capture.Frames.Count)
                        return "stack " + i + " names frame " + frames[f] + " of " + capture.Frames.Count;
            }
            return null;
        }

        /// <summary>How many bytes of JSON this capture would occupy, counted without holding
        /// the text: the same quantity a saved capture is counted in.</summary>
        static long SizeOf(Capture capture)
        {
            using (var counted = new CountingStream())
            using (var writer = new StreamWriter(counted, new UTF8Encoding(false)))
            {
                JsonSerializer.CreateDefault().Serialize(new JsonTextWriter(writer), capture);
                writer.Flush();
                return counted.Length;
            }
        }

        /// <summary>A stream that keeps only how many bytes were written into it.</summary>
        sealed class CountingStream : Stream
        {
            long _bytes;
            public override long Length => _bytes;
            public override long Position { get => _bytes; set { } }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override void Write(byte[] buffer, int offset, int count) => _bytes += count;
            public override void Write(ReadOnlySpan<byte> buffer) => _bytes += buffer.Length;
            public override void WriteByte(byte value) => _bytes++;
            public override void Flush() { }
            public override void SetLength(long value) => _bytes = value;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// Publishes a whole capture and drops the least recently used ones over the count or
        /// the byte bound. The capture just held is never the one dropped, however large it
        /// is: a report that evicted its own subject would answer by reading the file again.
        /// A store without a directory has nowhere to go back to, so it holds everything.
        /// </summary>
        void Hold(Loaded loaded)
        {
            var again = _loaded.FindIndex(l => l.Entry.Id == loaded.Entry.Id);
            if (again >= 0)
            {
                _loadedBytes -= _loaded[again].Bytes;
                _loaded.RemoveAt(again);
            }

            _loaded.Add(loaded);
            _loadedBytes += loaded.Bytes;

            if (_directory == null) return;
            while (_loaded.Count > 1 && (_loaded.Count > LoadedLimit || _loadedBytes > _budget))
            {
                _loadedBytes -= _loaded[0].Bytes;
                _loaded.RemoveAt(0);
            }
        }

        /// <summary>Every listed capture, with any unreadable one whose file has since changed
        /// judged again first.</summary>
        public IReadOnlyList<Entry> All
        {
            get
            {
                List<Entry> all;
                lock (_gate) all = _entries.ToList();
                foreach (var entry in all) Recheck(entry);
                return all;
            }
        }

        /// <summary>Saved captures this shim can see but cannot read, in listing order.</summary>
        public IReadOnlyList<Entry> NeedsRecovery => All.Where(e => e.Unreadable).ToList();

        /// <summary>How many whole captures are in memory now.</summary>
        public int LoadedCount { get { lock (_gate) return _loaded.Count; } }

        /// <summary>
        /// Saved bytes of the whole captures in memory, counted as the file is on disk. That
        /// is the figure the budget is set in, and it is measured rather than guessed.
        /// </summary>
        public long LoadedBytes { get { lock (_gate) return _loadedBytes; } }

        public Capture Keep(Capture capture, CancellationToken ct = default)
        {
            if (capture.CaptureId == null)
            {
                // The ids in use come from the list, which is a lock; the files they would
                // collide with are looked up outside it, because a collision check that held
                // the gate would hold it across a disk.
                List<string> known;
                lock (_gate) known = _entries.ConvertAll(e => e.CaptureId);
                capture.CaptureId = ShortId.New(id => known.Contains(id) ||
                    (_directory != null && File.Exists(Path.Combine(_directory, id + ".json"))));
            }
            if (!ShortId.IsCaptureId(capture.CaptureId)) throw new ArgumentException("Invalid captureId.");

            var slot = Slot(capture.CaptureId);
            var held = slot.Wait(LoadWait, ct);
            try
            {
                Entry existing;
                lock (_gate) existing = _entries.FirstOrDefault(e => e.CaptureId == capture.CaptureId);

                // A saved copy that can still be read answers this call; the copy in hand is
                // the same capture. One that cannot be read is replaced by it below.
                if (existing != null)
                {
                    try { return LoadLocked(existing); }
                    catch (CaptureReadException) { }
                }

                string path = null;
                long bytes;
                if (_directory != null)
                {
                    path = Path.Combine(_directory, capture.CaptureId + ".json");
                    bytes = Write(capture, path);
                }
                else
                {
                    // With no directory there is no file to measure, so the bytes a capture would
                    // occupy are counted instead and LoadedBytes means the same thing either way.
                    bytes = SizeOf(capture);
                }

                lock (_gate)
                {
                    var current = _entries.FirstOrDefault(e => e.CaptureId == capture.CaptureId);
                    // The number a report printed is the handle the model holds, so rebuilding
                    // a capture over an unreadable entry keeps that number instead of taking a
                    // fresh one and quietly moving every other capture's number. It keeps its
                    // place in the list too, so it does not become the default capture.
                    capture.Id = current != null ? current.Id : _next++;
                    var entry = new Entry(capture.CaptureId, path, new Contents.Readable(capture.Seconds, capture.Stacked)) { Id = capture.Id };
                    var place = current == null ? -1 : _entries.IndexOf(current);
                    if (place >= 0) _entries[place] = entry;
                    else _entries.Add(entry);
                    Hold(new Loaded { Entry = entry, Capture = capture, Bytes = bytes });
                }
                return capture;
            }
            finally { if (held) slot.Release(); }
        }

        /// <summary>
        /// Writes a capture as JSON into its file, and returns how many bytes that cost. The
        /// text goes straight into a temporary file rather than into one string first: a
        /// capture is tens of megabytes of characters, which as a single allocation lands on
        /// the large object heap and shows up as this process's peak. The move at the end is
        /// what makes a half written capture impossible to read.
        /// </summary>
        long Write(Capture capture, string path)
        {
            var temp = TempName(path);
            try
            {
                long bytes;
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = new StreamWriter(file, new UTF8Encoding(false), 1 << 16))
                    {
                        JsonSerializer.CreateDefault().Serialize(new JsonTextWriter(writer), capture);
                        writer.Flush();
                        bytes = file.Length;
                    }
                }
                Move(temp, path);
                return bytes;
            }
            catch
            {
                // The sweep at startup collects the ones this could not reach, because a
                // process killed outright gets no catch at all.
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        /// <summary>
        /// Puts a finished temporary in place of the saved capture.
        ///
        /// A move over an existing file fails outright on Windows while another process holds
        /// that file open - which is what another shim reading the same capture is doing - so a
        /// retry is worth one moment. What it must not do is fail as a bare path and a .NET
        /// message: the capture has been collected by then, the caller has a name for it, and
        /// the raw trace is still on disk because the caller deletes it only once this returns.
        /// </summary>
        static void Move(string temp, string path)
        {
            Exception failure = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try { File.Move(temp, path, true); return; }
                catch (IOException ex) { failure = ex; }
                catch (UnauthorizedAccessException ex) { failure = ex; }
                Thread.Sleep(50);
            }
            throw new CaptureUnsavableException("It could not replace the saved copy at " + path + ": " +
                OneLine(failure.Message) + ". The saved copy is unchanged and the raw trace is kept, so " +
                "profile_recover(captureId: \"" + Path.GetFileNameWithoutExtension(path) + "\") can try again.", failure);
        }

        public Capture Find(int? id, CancellationToken ct = default)
        {
            Entry entry;
            lock (_gate)
                entry = id == null ? _entries.LastOrDefault() : _entries.FirstOrDefault(e => e.Id == id);
            return entry == null ? null : Load(entry, ct);
        }
        public Capture FindStable(string id, CancellationToken ct = default)
        {
            Entry entry;
            lock (_gate) entry = id == null ? null : _entries.FirstOrDefault(e => e.CaptureId == id);
            return entry == null ? null : Load(entry, ct);
        }

        public string Export(Capture capture, string directory, bool raw)
        {
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Use an absolute export directory.");
            if (raw && (capture.Metadata?.Path == null || !File.Exists(capture.Metadata.Path)))
                throw new InvalidOperationException("Raw trace is not retained. JSON and report remain available; export with includeRaw=false.");
            try { Directory.CreateDirectory(directory); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException("The export directory " + directory + " could not be created: " + OneLine(ex.Message) + ". Nothing was written.", ex);
            }
            SweepExportTemps(directory);
            var stem = Path.Combine(directory, "capture-" + capture.CaptureId);
            var json = stem + ".json";
            var report = stem + ".txt";
            var trace = stem + ".diagsession";
            var there = (raw ? new[] { json, report, trace } : new[] { json, report }).Where(File.Exists).ToList();
            if (there.Count > 0)
                throw new IOException("An export of this capture is already there: " + string.Join(", ", there) +
                    ". Nothing was written; remove those files or export to another directory.");
            // All of it or none of it. An export interrupted one file in would otherwise leave a
            // JSON with no report beside it, and the check above would refuse that capture forever.
            var written = new List<string>();
            try
            {
                WriteMoved(json, writer => JsonSerializer.CreateDefault().Serialize(
                    new JsonTextWriter(writer) { Formatting = Formatting.Indented }, capture));
                written.Add(json);
                WriteMoved(report, writer => writer.Write(Render.Profile(capture, new ProfileQuery { Details = true }, null, All)));
                written.Add(report);
                if (raw) { File.Copy(capture.Metadata.Path, trace, false); written.Add(trace); }
                return "Exported:\n" + json + "\n" + report + (raw ? "\n" + trace : "");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                var left = RollBack(written);
                throw new IOException("The export to " + directory + " could not be written: " + OneLine(ex.Message) + ". " +
                    (left.Count == 0 ? "Nothing was written." : "These files it wrote could not be removed: " + string.Join(", ", left) + "."), ex);
            }
            catch
            {
                RollBack(written);
                throw;
            }
        }

        /// <summary>Deletes what a failed export wrote, and returns the files it could not delete.</summary>
        static List<string> RollBack(List<string> written)
        {
            var left = new List<string>();
            foreach (var file in written)
                try { File.Delete(file); }
                catch (Exception) { left.Add(file); }
            return left;
        }

        /// <summary>Writes a file by writing a temporary one and moving it into place, so that a
        /// destination name is either the whole file or nothing. An existing destination is not
        /// replaced, which is what the export promising not to overwrite means here.</summary>
        static void WriteMoved(string path, Action<StreamWriter> write)
        {
            var temp = TempName(path);
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false), 1 << 16))
                {
                    write(writer);
                    writer.Flush();
                }
                File.Move(temp, path);
            }
            catch
            {
                try { File.Delete(temp); }
                catch (Exception) { }
                throw;
            }
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
