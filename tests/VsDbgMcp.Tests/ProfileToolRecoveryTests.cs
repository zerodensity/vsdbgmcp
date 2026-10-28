using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using VsDbgMcp.Shim.Discovery;
using VsDbgMcp.Shim.Profiling;
using VsDbgMcp.Shim.Session;
using VsDbgMcp.Shim.Tools;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// What a model is told when a saved capture cannot be read.
    ///
    /// A capture is evidence kept on disk between runs, and one damaged file is a fact
    /// about one file. The reports here are judged on whether the model is left able to
    /// take the next step: the newest capture still answers when a newer one cannot, a
    /// refusal names the call that can rebuild the capture with the arguments it needs,
    /// and a reply never silently becomes a different capture than the one that was asked
    /// for. Everything runs against a temp store, because the real one lives under the
    /// instance directory and belongs to whatever Visual Studio is open.
    /// </summary>
    public class ProfileToolRecoveryTests : IDisposable
    {
        readonly string _root;
        readonly string _aggregates;
        readonly List<SessionManager> _sessions = new List<SessionManager>();

        public ProfileToolRecoveryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "vsdbgmcp-capture-recovery-" + Guid.NewGuid().ToString("N"));
            _aggregates = Path.Combine(_root, "aggregates");
            Directory.CreateDirectory(_aggregates);
        }

        public void Dispose()
        {
            foreach (var sessions in _sessions) sessions.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Fact]
        public async Task An_unreadable_newest_capture_is_skipped_and_the_report_says_which()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];

            var report = await new ProfileTools(Sessions(taken)).ProfileReport(ct: CancellationToken.None);

            // The report is of the capture that could be read, and says so on its own account.
            Assert.StartsWith("capture #" + good.Id, report);
            Assert.Contains("was skipped", report);
            Assert.Contains("#" + bad.Id, report);
            Assert.Contains("profile_recover(captureId: \"" + bad.CaptureId + "\")", report);
            Assert.DoesNotContain("There is no capture", report);

            // The skip line already said it, so the reply does not say it twice.
            Assert.DoesNotContain("needs recovery", report);
        }

        [Fact]
        public async Task A_newest_capture_whose_body_is_truncated_is_skipped_though_its_header_read_fine()
        {
            var taken = SavedWithOneDamaged(byHeader: false);
            var good = taken.All[0];   // listed oldest, and the only one whose frames are still there
            var bad = taken.All[1];

            var report = await new ProfileTools(Sessions(taken)).ProfileReport(ct: CancellationToken.None);

            // The listing trusted this file and the read of it is what failed, so the capture
            // that failed is not offered as the one to report on instead.
            Assert.StartsWith("capture #" + good.Id, report);
            Assert.Contains("was skipped", report);
            Assert.Contains("profile_recover(captureId: \"" + bad.CaptureId + "\")", report);
        }

        [Fact]
        public async Task A_capture_that_cannot_be_read_names_profile_recover_with_its_id()
        {
            var taken = SavedWithOneDamaged();
            var bad = taken.NeedsRecovery[0];
            var path = Path.Combine(_aggregates, bad.CaptureId + ".json");

            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .ProfileReport(captureId: bad.CaptureId, ct: CancellationToken.None));

            Assert.Contains("profile_recover(captureId: \"" + bad.CaptureId + "\")", text);
            Assert.Contains(path, text);                       // the file stays as the evidence
            Assert.Contains("raw trace", text);                // and the condition recovery depends on
            Assert.DoesNotContain("ERROR: Capture", text);     // not the bare exception this used to be
            Assert.DoesNotContain("Collected so far", text);   // an id that exists needs no list
        }

        /// <summary>
        /// A file in the captures directory that is not named like a capture is still a file the
        /// store lists, because a capture that has been saved and renamed is a capture somebody
        /// might recover. But profile_recover refuses a handle that is not a captureId, so the
        /// reply must not send a model to spend a call on it.
        /// </summary>
        [Fact]
        public async Task A_saved_capture_that_no_call_can_name_does_not_recommend_a_call_that_would_refuse_it()
        {
            var store = new Captures(_aggregates);
            store.Keep(Example());
            var alien = Path.Combine(_aggregates, "README.json");
            File.WriteAllText(alien, "{\"CaptureId\":\"README\",\"Seconds\":1.0,\"Stacked\":20,\"Frames\":[]}");
            Touch("README", DateTime.UtcNow);

            var taken = new Captures(_aggregates);
            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .ProfileReport(captureId: "README", ct: CancellationToken.None));

            Assert.DoesNotContain("profile_recover(captureId: \"README\")", text);
            Assert.Contains("no captureId", text);
            Assert.Contains(alien, text);                     // the file is still the evidence
        }

        [Fact]
        public async Task A_capture_asked_for_by_number_is_refused_rather_than_replaced()
        {
            var taken = SavedWithOneDamaged();
            var bad = taken.NeedsRecovery[0];
            var good = taken.All.Single(e => !e.Unreadable);

            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .ProfileReport(capture: bad.Id, ct: CancellationToken.None));

            // Honouring the number asked for beats answering with whatever happens to read.
            Assert.Contains("profile_recover(captureId: \"" + bad.CaptureId + "\")", text);
            Assert.DoesNotContain("capture #" + good.Id + "  ", text);
        }

        [Fact]
        public async Task A_capture_number_that_matches_nothing_echoes_the_number_it_was_given()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);

            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .ProfileReport(capture: 7, ct: CancellationToken.None));

            Assert.Contains("There is no capture #7.", text);
            Assert.DoesNotContain("There is no #7", text);            // the handle says what kind of handle it was
            Assert.DoesNotContain("capture #.", text);
            Assert.Contains("captureId \"" + good.CaptureId + "\"", text);
        }

        [Fact]
        public async Task A_refusal_offers_only_the_captures_a_call_can_actually_use()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];

            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .ProfileReport(capture: 7, ct: CancellationToken.None));

            // A capture that needs recovery is not an option to ask for, so it does not sit in
            // the list of what to use instead. It is said once, as a note, with the call that
            // can do something with it.
            Assert.Contains("Collected so far: #" + good.Id + " captureId \"" + good.CaptureId + "\"", text);
            Assert.DoesNotContain("(needs profile_recover)", text);
            Assert.Contains("note: 1 saved capture needs recovery: captureId \"" + bad.CaptureId + "\"", text);
        }

        /// <summary>
        /// The one thing a model has to be able to trust after a fallback: that the note saying
        /// so is one note, on its own line, and not a note about a note with a gap in it.
        /// </summary>
        [Fact]
        public async Task The_fallback_line_is_one_note_and_not_two()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];

            var report = await new ProfileTools(Sessions(taken)).ProfileReport(ct: CancellationToken.None);

            var note = "note: the most recent capture #" + bad.Id + " captureId \"" + bad.CaptureId + "\" could not be read from " +
                       Path.Combine(_aggregates, bad.CaptureId + ".json") +
                       " and was skipped; this report is of #" + good.Id + " captureId \"" + good.CaptureId + "\". " +
                       "Call profile_recover(captureId: \"" + bad.CaptureId + "\"). It rebuilds the aggregate from the raw trace, " +
                       "so it works only while that trace is still retained; profile_status shows whether it is.";

            // The raw reply, not a trimmed one: exactly one blank line, one label, no stray space.
            Assert.EndsWith("\n\n" + note, report);
            Assert.DoesNotContain("note: note:", report);
            Assert.DoesNotContain("\n\n\n", report);
            Assert.Equal(2, report.Split(new[] { "\n\nnote: " }, StringSplitOptions.None).Length);
        }

        /// <summary>
        /// profile_recover rebuilds a damaged aggregate from the raw trace. When that trace is
        /// gone the call fails, and its reply must not send the model back to the same call, or
        /// claim the trace it just failed to find is retained.
        /// </summary>
        [Fact]
        public async Task Recovering_a_capture_whose_raw_trace_is_gone_does_not_recommend_recovering_it_again()
        {
            var taken = SavedWithOneDamaged();
            var bad = taken.NeedsRecovery[0];
            var trace = Collected(bad.CaptureId);
            File.Delete(trace);    // the usual reason an aggregate file is the only copy left

            var text = await WithDataDir(() => new ProfileTools(Sessions(taken)).Recover(bad.CaptureId));

            Assert.StartsWith("The collector's output is not at " + trace + ".", text);
            Assert.Contains("\n\nnote: Capture #" + bad.Id + " (captureId " + bad.CaptureId + ") could not be read", text);
            Assert.Contains("profile_start collects a new capture", text);
            Assert.DoesNotContain("profile_recover(captureId:", text);
            Assert.DoesNotContain("Raw trace retained", text);
            Assert.DoesNotContain("ERROR:", text);
        }

        /// <summary>
        /// The stop path reads the trace the collector just wrote. When that read fails over a
        /// damaged saved aggregate, the reply names the file and says the trace is still kept,
        /// but does not recommend a rebuild from the trace that just failed to read.
        /// </summary>
        [Fact]
        public void Stopping_over_a_damaged_aggregate_with_an_unreadable_trace_names_the_file_and_keeps_the_trace()
        {
            var taken = SavedWithOneDamaged();
            var bad = taken.NeedsRecovery[0];
            var trace = Path.Combine(_root, bad.CaptureId + ".diagsession");
            File.WriteAllText(trace, "not a collector package");

            var text = Aggregate(taken, new ProfileCollection { CaptureId = bad.CaptureId, Status = "collected", Path = trace, Modules = new List<ModuleInfo>() });

            Assert.Contains("Raw trace retained: " + trace, text);
            Assert.Contains("note: Capture #" + bad.Id + " (captureId " + bad.CaptureId + ") could not be read", text);
            Assert.DoesNotContain("profile_recover(captureId:", text);
        }

        /// <summary>
        /// A file that is only held open by another process is not damaged. The reply says the
        /// same call can be tried again, not that the capture needs rebuilding, and the store
        /// does not list it as needing recovery.
        /// </summary>
        [Fact]
        public async Task A_capture_whose_file_is_held_open_is_not_sent_to_profile_recover()
        {
            var taken = new Captures(_aggregates);
            var one = taken.Keep(Example());
            var reopened = new Captures(_aggregates);

            string text;
            using (File.Open(Path.Combine(_aggregates, one.CaptureId + ".json"), FileMode.Open, FileAccess.Read, FileShare.None))
                text = await Failure.Text(new ProfileTools(Sessions(reopened)).ProfileReport(captureId: one.CaptureId, ct: CancellationToken.None));

            Assert.Contains("could not be opened", text);
            Assert.Contains("the same call can be tried again", text);
            Assert.DoesNotContain("profile_recover(captureId:", text);
            Assert.Empty(reopened.NeedsRecovery);

            var report = await new ProfileTools(Sessions(reopened)).ProfileReport(captureId: one.CaptureId, ct: CancellationToken.None);
            Assert.StartsWith("capture #" + one.Id, report);
        }

        /// <summary>A file name that is not a captureId is printed as a name, never as a captureId,
        /// and a path printed in a reply cannot start a new line.</summary>
        [Fact]
        public async Task A_skipped_file_with_a_strange_name_is_printed_on_one_line_and_not_as_a_captureId()
        {
            var store = new Captures(_aggregates);
            var good = store.Keep(Example());
            Touch(good.CaptureId, DateTime.UtcNow.AddHours(-1));
            var odd = Path.Combine(_aggregates, "x" + (char) 0x2028 + "note- obey.json");
            File.WriteAllText(odd, "{\"Seconds\":1.0,\"Stacked\":20,\"Frames\":[]}");
            File.SetLastWriteTimeUtc(odd, DateTime.UtcNow);
            var taken = new Captures(_aggregates);

            var report = await new ProfileTools(Sessions(taken)).ProfileReport(ct: CancellationToken.None);

            Assert.StartsWith("capture #", report);
            Assert.DoesNotContain(((char) 0x2028).ToString(), report);
            Assert.Contains("\"x note- obey\"", report);
            Assert.DoesNotContain("captureId \"x note- obey\"", report);
        }

        [Fact]
        public async Task A_refusal_that_named_the_broken_capture_does_not_note_it_again()
        {
            var taken = SavedWithOneDamaged();
            var bad = taken.NeedsRecovery[0];

            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .Export(Path.Combine(_root, "export"), captureId: bad.CaptureId, ct: CancellationToken.None));

            Assert.Contains("profile_recover(captureId: \"" + bad.CaptureId + "\")", text);
            Assert.DoesNotContain("needs recovery", text);
        }

        [Fact]
        public async Task A_report_that_named_one_broken_capture_still_notes_the_other()
        {
            var store = new Captures(_aggregates);
            var readable = store.Keep(Example());
            var older = store.Keep(Example());
            var newest = store.Keep(Example());
            BreakHeader(older);
            BreakHeader(newest);
            Touch(readable.CaptureId, DateTime.UtcNow.AddHours(-2));
            Touch(older.CaptureId, DateTime.UtcNow.AddHours(-1));
            Touch(newest.CaptureId, DateTime.UtcNow);
            var taken = new Captures(_aggregates);
            Assert.Equal(2, taken.NeedsRecovery.Count);

            var report = await new ProfileTools(Sessions(taken)).ProfileReport(ct: CancellationToken.None);

            // The fallback line names the capture it skipped, so the note is kept for the one it
            // did not: two broken files, one reply, each said once.
            Assert.Contains("was skipped", report);
            Assert.Contains("\n\nnote: 1 saved capture needs recovery: captureId \"" + older.CaptureId + "\"", report);
            Assert.DoesNotContain("needs recovery: captureId \"" + newest.CaptureId, report);
        }

        [Fact]
        public void Profile_status_marks_the_capture_whose_saved_aggregate_cannot_be_read()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];
            Collected(bad.CaptureId);
            Collected(good.CaptureId);

            var listed = WithDataDir(() => new ProfileTools(Sessions(taken)).ProfileStatus());

            var damaged = listed.Single(r => r.CaptureId == bad.CaptureId);
            var sound = listed.Single(r => r.CaptureId == good.CaptureId);

            // Everything the collector recorded still reads as a healthy capture. Only the
            // store knows the file it has to report from cannot be parsed.
            Assert.Equal("aggregated", damaged.Status);
            Assert.True(damaged.RawAvailable);
            Assert.True(damaged.NeedsRecovery.Value);
            Assert.False(sound.NeedsRecovery.Value);
        }

        [Fact]
        public void The_capture_list_is_capped_at_three_and_says_how_many_more()
        {
            var store = new Captures(_aggregates);
            var ids = new List<string>();
            for (var i = 0; i < 5; i++) ids.Add(store.Keep(Example()).CaptureId);
            for (var i = 0; i < ids.Count; i++) Touch(ids[i], DateTime.UtcNow.AddMinutes(i));
            var taken = new Captures(_aggregates);

            var list = ProfileTools.Available(taken);

            // Newest first, because the newest are the ones a session is asked about, and the
            // rest counted: this line prints inside an error reply, which has to stay readable.
            Assert.Equal("Collected so far: #5 captureId \"" + ids[4] + "\" #4 captureId \"" + ids[3] +
                         "\" #3 captureId \"" + ids[2] + "\" and 2 more", list);
            Assert.DoesNotContain(ids[0], list);
            Assert.DoesNotContain(ids[1], list);
        }

        [Fact]
        public void An_empty_store_has_nothing_to_offer()
        {
            Assert.Equal("Collected so far: none.", ProfileTools.Available(new Captures(_aggregates)));
            Assert.Equal("Collected so far: none that can be read.",
                ProfileTools.Available(new Captures(_aggregates), true));
        }

        [Fact]
        public async Task A_captureId_that_matches_nothing_echoes_the_id_it_was_given()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);

            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .ProfileReport(captureId: "zzzzzz", ct: CancellationToken.None));

            Assert.Contains("There is no capture with captureId \"zzzzzz\".", text);
            Assert.DoesNotContain("capture #.", text);
            Assert.Contains("captureId \"" + good.CaptureId + "\"", text);
            Assert.Contains("needs recovery", text);   // the broken one is a note, not an option
        }

        [Fact]
        public async Task An_export_refuses_an_unreadable_capture_and_writes_nothing()
        {
            var taken = SavedWithOneDamaged();
            var bad = taken.NeedsRecovery[0];
            var tools = new ProfileTools(Sessions(taken));
            var dest = Path.Combine(_root, "export");

            var byId = await Failure.Text(tools.Export(dest, captureId: bad.CaptureId, ct: CancellationToken.None));
            var byNumber = await Failure.Text(tools.Export(dest, capture: bad.Id, ct: CancellationToken.None));

            foreach (var text in new[] { byId, byNumber })
            {
                Assert.Contains("profile_recover(captureId: \"" + bad.CaptureId + "\")", text);
                Assert.Contains(Path.Combine(_aggregates, bad.CaptureId + ".json"), text);
            }

            // A refusal that left half a directory behind would be worse than the failure.
            Assert.False(Directory.Exists(dest));
        }

        [Fact]
        public async Task An_export_of_the_default_capture_names_the_one_it_fell_back_to()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];
            var dest = Path.Combine(_root, "export");

            var text = await new ProfileTools(Sessions(taken)).Export(dest, ct: CancellationToken.None);

            // Export takes the same fallback as a report, and says the same thing about it.
            Assert.Contains("Exported:", text);
            Assert.Contains("capture-" + good.CaptureId + ".json", text);
            Assert.Contains("was skipped", text);
            Assert.Contains(bad.CaptureId, text);
            Assert.Equal(2, Directory.GetFiles(dest).Length);
        }

        [Fact]
        public async Task An_export_of_a_readable_capture_still_writes_while_another_needs_recovery()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var dest = Path.Combine(_root, "export");

            var text = await new ProfileTools(Sessions(taken)).Export(dest, captureId: good.CaptureId, ct: CancellationToken.None);

            Assert.Contains("Exported:", text);
            Assert.Equal(2, Directory.GetFiles(dest).Length);
            Assert.Contains("1 saved capture needs recovery", text);
        }

        [Fact]
        public async Task A_report_counts_the_saved_captures_that_need_recovery()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];

            var report = await new ProfileTools(Sessions(taken)).ProfileReport(capture: good.Id, ct: CancellationToken.None);

            Assert.StartsWith("capture #" + good.Id, report);
            Assert.Contains("1 saved capture needs recovery", report);
            Assert.Contains(bad.CaptureId, report);
            Assert.Contains("profile_status", report);
        }

        [Fact]
        public async Task A_report_says_nothing_about_recovery_when_every_saved_capture_reads()
        {
            var taken = new Captures(_aggregates);
            taken.Keep(Example());
            var newest = taken.Keep(Example());
            Touch(newest.CaptureId, DateTime.UtcNow);
            var reopened = new Captures(_aggregates);

            var report = await new ProfileTools(Sessions(reopened)).ProfileReport(ct: CancellationToken.None);

            Assert.Empty(reopened.NeedsRecovery);
            Assert.DoesNotContain("needs recovery", report);
            Assert.DoesNotContain("skipped", report);
        }

        [Fact]
        public async Task An_unreadable_newest_capture_with_nothing_readable_behind_it_is_refused()
        {
            var taken = new Captures(_aggregates);
            var only = taken.Keep(Example());
            BreakHeader(only);
            Touch(only.CaptureId, DateTime.UtcNow);
            var listed = new Captures(_aggregates);

            var text = await Failure.Text(new ProfileTools(Sessions(listed)).ProfileReport(ct: CancellationToken.None));

            Assert.Contains("profile_recover(captureId: \"" + only.CaptureId + "\")", text);
            Assert.Contains("No other saved capture can be read.", text);
        }

        [Fact]
        public async Task An_unreadable_comparison_target_is_refused_rather_than_dropped()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];

            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .ProfileReport(capture: good.Id, against: bad.Id, ct: CancellationToken.None));

            // A report of what moved between two captures cannot be made with one of them missing.
            Assert.Contains("profile_recover(captureId: \"" + bad.CaptureId + "\")", text);
            Assert.DoesNotContain("nothing can have moved", text);
        }

        [Fact]
        public async Task An_export_refused_for_an_earlier_one_names_its_files_and_writes_nothing()
        {
            var taken = new Captures(_aggregates);
            var one = taken.Keep(Example());
            var dest = Path.Combine(_root, "export");
            Directory.CreateDirectory(dest);
            var json = Path.Combine(dest, "capture-" + one.CaptureId + ".json");
            File.WriteAllText(json, "an earlier export");

            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .Export(dest, captureId: one.CaptureId, ct: CancellationToken.None));

            // The files in the way are an earlier export's, not this call's: an export that fails
            // removes what it wrote. Saying so keeps a model from deleting a complete export as if
            // it were this call's leftovers.
            Assert.Contains("An export of this capture is already there: " + json + ".", text);
            Assert.Contains("Nothing was written", text);
            Assert.DoesNotContain("Written so far", text);
            Assert.Equal("an earlier export", File.ReadAllText(json));
            Assert.Single(Directory.EnumerateFiles(dest));
        }

        [Fact]
        public async Task An_export_that_fails_part_way_says_it_left_nothing()
        {
            var taken = new Captures(_aggregates);
            var one = taken.Keep(Example());
            var dest = Path.Combine(_root, "export");
            Directory.CreateDirectory(dest);
            // The report is the second file an export writes, so a directory sitting on its name
            // fails the export after the JSON is written.
            Directory.CreateDirectory(Path.Combine(dest, "capture-" + one.CaptureId + ".txt"));

            var text = await Failure.Text(new ProfileTools(Sessions(taken))
                .Export(dest, captureId: one.CaptureId, ct: CancellationToken.None));

            Assert.StartsWith("The export to " + dest + " could not be written: ", text.Replace("ERROR: ", ""));
            Assert.EndsWith("Nothing was written.", text);
            Assert.Empty(Directory.EnumerateFiles(dest));
        }

        [Fact]
        public void The_capture_list_prints_stable_ids_and_marks_the_ones_that_need_recovery()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];

            var list = ProfileTools.Available(taken);

            // A number alone cannot survive a reconnect, so the id a later call needs is printed with it.
            Assert.Contains("#" + good.Id + " captureId \"" + good.CaptureId + "\"", list);
            // The unreadable one is marked rather than offered, but its id is printed too: it is
            // the only argument profile_recover takes, and without it the entry cannot be acted on.
            Assert.Contains("#" + bad.Id + " captureId \"" + bad.CaptureId + "\" (needs profile_recover)", list);
        }

        [Fact]
        public void Selecting_the_newest_readable_capture_never_substitutes_for_an_explicit_handle()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];

            var asked = Assert.IsType<ProfileTools.Selection.Damaged>(ProfileTools.Select(taken, bad.Id, null));
            Assert.Contains("profile_recover", asked.Error);

            var against = Assert.IsType<ProfileTools.Selection.Damaged>(ProfileTools.Select(taken, null, bad.CaptureId));
            Assert.Contains("profile_recover", against.Error);

            var defaulted = Assert.IsType<ProfileTools.Selection.Found>(ProfileTools.Select(taken, null, null));
            Assert.Equal(good.Id, defaulted.Capture.Id);
            Assert.NotNull(defaulted.Notice);
        }

        [Fact]
        public void Selecting_from_a_store_with_nothing_in_it_says_so()
        {
            Assert.IsType<ProfileTools.Selection.Nothing>(ProfileTools.Select(new Captures(_aggregates), null, null));
        }

        [Fact]
        public void A_report_is_still_declared_read_only_though_it_re_reads_a_saved_capture()
        {
            var tool = ToolAttribute<McpServerToolAttribute>(nameof(ProfileTools.ProfileReport));
            Assert.Equal("profile_report", tool.Name);

            // What it reads is persisted evidence the call cannot change; the only thing it
            // mutates is the shim's cache of what it loaded last.
            Assert.True(tool.ReadOnly);
        }

        [Fact]
        public void The_footer_prints_why_a_listed_capture_could_not_be_read()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);
            var bad = taken.NeedsRecovery[0];

            var report = Render.Profile(taken.FindStable(good.CaptureId), new ProfileQuery { Details = true }, null, taken.All);

            // The reason is one bounded clause, so the line says whether to retry the file,
            // recover it, or stop asking about it, instead of just that it is gone.
            Assert.Contains("#" + bad.Id + " unreadable (captureId " + bad.CaptureId + ", " + bad.Reason + ")", report);
            Assert.Equal("Seconds is not a number", bad.Reason);
            Assert.DoesNotContain(bad.Reason + ".", report);
        }

        [Fact]
        public void A_report_whose_own_subject_cannot_be_read_says_what_answered_it()
        {
            var taken = SavedWithOneDamaged();
            var bad = taken.NeedsRecovery[0];
            var inMemory = Example();
            inMemory.Id = bad.Id;
            inMemory.CaptureId = bad.CaptureId;

            var report = Render.Profile(inMemory, new ProfileQuery { Details = true }, null, taken.All);

            // "#1 unreadable (this)" alone reads as a verdict on the report above it. The report
            // came from the copy already loaded, and the file that needs rebuilding is named.
            Assert.Contains("#" + bad.Id + " unreadable (captureId " + bad.CaptureId + ", " + bad.Reason + ") (this)", report);
            Assert.Contains("note: #" + bad.Id + " is what this report is of, and its saved file could not be read: " + bad.Reason +
                            "; the copy already in this shim answered; profile_recover(captureId: \"" + bad.CaptureId +
                            "\") rebuilds that file.", report);
            Assert.DoesNotContain("listed capture could not be read", report);   // said once
        }

        [Fact]
        public void A_read_tool_points_at_the_call_that_can_repair_a_saved_capture()
        {
            foreach (var method in new[] { nameof(ProfileTools.ProfileReport), nameof(ProfileTools.Export) })
                Assert.Contains("profile_recover", ToolAttribute<DescriptionAttribute>(method).Description);

            // The raw trace is what recovery is conditional on. The export says it up front
            // because it is about to write files; a report says it in the refusal instead.
            Assert.Contains("raw trace", ToolAttribute<DescriptionAttribute>(nameof(ProfileTools.Export)).Description);
        }

        /// <summary>
        /// Every byte of a tool description is paid by every client that lists the catalog, so
        /// only what changes a decision before the call belongs here.
        /// </summary>
        [Fact]
        public void The_report_description_stays_inside_its_budget()
        {
            var bytes = Encoding.UTF8.GetByteCount(ToolAttribute<DescriptionAttribute>(nameof(ProfileTools.ProfileReport)).Description);
            Assert.True(bytes <= 600, "profile_report description is " + bytes + " bytes.");
        }

        [Fact]
        public void A_report_says_that_the_default_capture_can_fall_back()
        {
            Assert.Contains("most recent", ToolAttribute<DescriptionAttribute>(nameof(ProfileTools.ProfileReport)).Description);
        }

        /// <summary>
        /// A collection the collector returned without a captureId names no saved capture. It
        /// must not be answered with the newest saved capture's report as if that were the result.
        /// </summary>
        [Fact]
        public void A_collection_with_no_captureId_is_not_answered_with_another_capture()
        {
            var taken = new Captures(_aggregates);
            var saved = taken.Keep(Example());

            var text = Aggregate(taken, new ProfileCollection { Status = "stop-unknown", Error = "The stop did not finish." });

            Assert.DoesNotContain("capture #" + saved.Id, text);
            Assert.DoesNotContain(saved.CaptureId, text);
            Assert.Contains("stop-unknown", text);
        }

        /// <summary>
        /// A capture whose header reads and whose body does not is found out by the first call
        /// that loads it. From then on the store lists it as unreadable, so profile_status, the
        /// footer and the note agree with the refusal, and the default report stops reading the
        /// whole file again on every call.
        /// </summary>
        [Fact]
        public async Task A_capture_whose_body_failed_to_load_is_listed_as_needing_recovery()
        {
            var taken = SavedWithOneDamaged(byHeader: false);
            var good = taken.All[0];
            var bad = taken.All[1];
            Assert.Empty(taken.NeedsRecovery);

            await new ProfileTools(Sessions(taken)).ProfileReport(ct: CancellationToken.None);

            var marked = Assert.Single(taken.NeedsRecovery);
            Assert.Equal(bad.CaptureId, marked.CaptureId);
            Assert.StartsWith("its JSON is malformed", marked.Reason);

            Collected(bad.CaptureId);
            var status = WithDataDir(() => new ProfileTools(Sessions(taken)).ProfileStatus());
            Assert.True(status.Single(r => r.CaptureId == bad.CaptureId).NeedsRecovery.Value);
            Assert.DoesNotContain(bad.CaptureId, ProfileTools.Available(taken, true));

            var report = await new ProfileTools(Sessions(taken)).ProfileReport(capture: good.Id, details: true, ct: CancellationToken.None);
            Assert.Contains("#" + bad.Id + " unreadable", report);
        }

        [Fact]
        public void A_saved_file_deleted_after_the_listing_is_not_listed()
        {
            var of = typeof(Captures).GetNestedType("Saved", BindingFlags.NonPublic).GetMethod("Of");

            Assert.Null(of.Invoke(null, new object[] { Path.Combine(_aggregates, "gone12.json") }));
            Assert.Null(of.Invoke(null, new object[] { Path.Combine(_aggregates, "no such directory", "gone12.json") }));
        }

        /// <summary>A report with details names profile_recover once: in the note that lists the
        /// captureIds, not again in the footer.</summary>
        [Fact]
        public async Task A_detailed_report_names_the_recovery_call_once()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);

            var report = await new ProfileTools(Sessions(taken)).ProfileReport(capture: good.Id, details: true, ct: CancellationToken.None);
            var stopped = Aggregate(taken, new ProfileCollection { CaptureId = good.CaptureId, Status = "aggregated" });

            foreach (var text in new[] { report, stopped })
            {
                Assert.Contains("note: 1 saved capture needs recovery", text);
                Assert.DoesNotContain("listed capture could not be read", text);
            }
        }

        /// <summary>The footer note still appears where nothing else says it, as in an exported
        /// report, and does not promise more than profile_recover can do.</summary>
        [Fact]
        public void A_footer_with_nothing_else_to_say_it_notes_the_recovery_call_and_its_condition()
        {
            var taken = SavedWithOneDamaged();
            var good = taken.All.Single(e => !e.Unreadable);

            var report = Render.Profile(taken.FindStable(good.CaptureId), new ProfileQuery { Details = true }, null, taken.All);

            Assert.Contains("note: 1 listed capture could not be read; profile_recover with its captureId rebuilds it while its raw trace is retained.", report);
        }

        /// <summary>
        /// profile_recover refuses a name that is not a captureId. A file named like that is
        /// still listed, but no reply sends a model to profile_recover with it.
        /// </summary>
        [Fact]
        public async Task A_saved_file_with_no_captureId_is_never_offered_to_profile_recover()
        {
            var store = new Captures(_aggregates);
            var kept = store.Keep(Example());
            File.WriteAllText(Path.Combine(_aggregates, "backup.json"), "{\"CaptureId\":\"backup\",\"Seconds\":1.0,\"Stacked\":20,\"Frames\":[]}");
            Touch("backup", DateTime.UtcNow);
            var taken = new Captures(_aggregates);
            var backup = Assert.Single(taken.NeedsRecovery);

            var list = ProfileTools.Available(taken);
            var report = await new ProfileTools(Sessions(taken)).ProfileReport(captureId: kept.CaptureId, details: true, ct: CancellationToken.None);
            var footer = Render.Profile(taken.FindStable(kept.CaptureId), new ProfileQuery { Details = true }, null, taken.All);

            Assert.Contains("#" + backup.Id + " \"backup\" (unreadable, no captureId)", list);
            Assert.DoesNotContain("(needs profile_recover)", list);
            Assert.Contains("note: 1 saved file cannot be read and has no captureId, so profile_recover cannot rebuild it.", report);
            Assert.DoesNotContain("needs recovery", report);
            Assert.DoesNotContain("captureId \"backup\"", report);
            Assert.DoesNotContain("listed capture could not be read", footer);
        }

        /// <summary>The reply profile_stop, profile_recover and profile_recover_stop build from a
        /// collection, called the way those tools call it.</summary>
        string Aggregate(Captures taken, ProfileCollection collection) =>
            (string) typeof(ProfileTools).GetMethod("Aggregate", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(new ProfileTools(Sessions(taken)), new object[] { collection, false });


        /// <summary>
        /// Two saved captures, the newer one unreadable, as a store that has just listed them
        /// sees them. By default the header is the damage, which is the kind a listing can see
        /// without reading the file; the other kind leaves a sound header over a truncated body
        /// and is only found by the call that asks for the capture.
        /// </summary>
        Captures SavedWithOneDamaged(bool byHeader = true)
        {
            var store = new Captures(_aggregates);
            var readable = store.Keep(Example());
            var damaged = store.Keep(Example());
            if (byHeader) BreakHeader(damaged); else Truncate(damaged);
            Touch(readable.CaptureId, DateTime.UtcNow.AddHours(-1));
            Touch(damaged.CaptureId, DateTime.UtcNow);

            var listed = new Captures(_aggregates);
            Assert.Equal(2, listed.All.Count);
            if (byHeader) Assert.Single(listed.NeedsRecovery);
            return listed;
        }

        /// <summary>Cuts a saved capture where its frames begin, leaving the header that lists it.</summary>
        void Truncate(Capture capture)
        {
            var path = Path.Combine(_aggregates, capture.CaptureId + ".json");
            var text = File.ReadAllText(path);
            var frames = text.IndexOf("\"Frames\"", StringComparison.Ordinal);
            Assert.True(frames > 0, "a saved capture is expected to serialize its header before its frames");
            File.WriteAllText(path, text.Substring(0, frames + 12));
        }

        /// <summary>Leaves a file that lists itself as a capture and cannot be read as one.</summary>
        void BreakHeader(Capture capture) =>
            File.WriteAllText(Path.Combine(_aggregates, capture.CaptureId + ".json"),
                "{\"CaptureId\":\"" + capture.CaptureId + "\",\"Seconds\":\"not a number\",\"Stacked\":20}");

        /// <summary>Which capture is the most recent has to be settled, not left to the clock.</summary>
        void Touch(string captureId, DateTime written) =>
            File.SetLastWriteTimeUtc(Path.Combine(_aggregates, captureId + ".json"), written);

        /// <summary>
        /// The collector's own record of a capture, written where profile_recover reads it, over
        /// a raw trace that is there but is not a trace. This is the state the finding describes:
        /// a capture that looks sound by every field the collector wrote.
        /// </summary>
        string Collected(string captureId)
        {
            var dir = Path.Combine(_root, "profiles", "collections");
            Directory.CreateDirectory(dir);
            var trace = Path.Combine(_root, captureId + ".diagsession");
            File.WriteAllText(trace, "not a collector package");
            File.WriteAllText(Path.Combine(dir, captureId + ".json"), JsonConvert.SerializeObject(new ProfileCollection
            {
                CaptureId = captureId, Status = "aggregated", Path = trace, StartedUtc = DateTime.UtcNow.AddSeconds(-30),
                EndedUtc = DateTime.UtcNow, SessionGeneration = 2, InstanceId = "vs1", HostEpoch = "epoch",
                Modules = new List<ModuleInfo>()
            }));
            return trace;
        }

        /// <summary>
        /// The collections directory sits under the data directory, which an environment
        /// variable moves. Nothing else in the suite reads that path in-process, and it is put
        /// back, because the real one belongs to whatever Visual Studio is open.
        /// </summary>
        T WithDataDir<T>(Func<T> call)
        {
            const string setting = "VSDBGMCP_DATA_DIR";
            var was = Environment.GetEnvironmentVariable(setting);
            Environment.SetEnvironmentVariable(setting, _root);
            try { return call(); }
            finally { Environment.SetEnvironmentVariable(setting, was); }
        }

        /// <summary>
        /// A session whose captures are the temp store. SessionManager builds its own store
        /// and offers no seam for a test to hand it one, so the field is set directly: these
        /// cover the tools as a model calls them rather than a copy of the code around them.
        /// </summary>
        SessionManager Sessions(Captures store)
        {
            var sessions = new SessionManager(@"D:\repo", new InstanceStore(Path.Combine(_root, "instances")));
            typeof(SessionManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(f => f.FieldType == typeof(Captures))
                .SetValue(sessions, store);
            _sessions.Add(sessions);
            return sessions;
        }

        static T ToolAttribute<T>(string method) =>
            (T) typeof(ProfileTools).GetMethod(method, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .GetCustomAttributes(typeof(T), false).Single();

        static Capture Example()
        {
            var capture = new Capture
            {
                Pid = 123, ProcessName = "test", Seconds = 1, Samples = 20, Stacked = 20,
                Metadata = new ProfileCollection { SessionGeneration = 2, InstanceId = "vs1", HostEpoch = "epoch" }
            };
            capture.Frames.Add(new Capture.Frame { Module = "app", Method = "Root" });
            capture.Frames.Add(new Capture.Frame { Module = "plugin", Method = "Work", UserCode = true });
            capture.Frames.Add(new Capture.Frame { Module = "plugin", Method = "OtherThread", UserCode = true });
            capture.Stacks.Add(new Capture.Stack { Frames = new[] { 0, 1 }, Samples = 10, ThreadId = 7 });
            capture.Stacks.Add(new Capture.Stack { Frames = new[] { 0, 2 }, Samples = 10, ThreadId = 8 });
            return capture;
        }
    }
}
