using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using VsDbgMcp.Contracts;
using Task = System.Threading.Tasks.Task;

namespace VsDbgMcp.Host
{
    partial class DebugHost : IProfileHost
    {
        readonly SemaphoreSlim _profileGate = new SemaphoreSlim(1, 1);
        static string ProfileDirectory => Path.Combine(Names.InstanceDir, "profiles", "collections");

        public Task<OpResult> ProfileStartAsync(CancellationToken ct = default) => ProfileBeginAsync(false, ct);

        public async Task<OpResult> ProfileBeginAsync(bool retainRaw, CancellationToken ct = default)
        {
            var operation = HostOperations.Store.Begin("profile-start", null, retainRaw.ToString(), true, out var created);
            var captureId = ShortId.New(id => File.Exists(Path.Combine(ProfileDirectory, id + ".json")) ||
                File.Exists(Path.Combine(ProfileDirectory, id + ".diagsession")));
            HostOperations.Store.Update(operation.OperationId, o => o.Message = "Reserved capture " + captureId);
            HostOperations.Run(operation.OperationId, async () =>
            {
                if (!await _profileGate.WaitAsync(0).ConfigureAwait(false)) throw new InvalidOperationException("A profile transition is pending.");
                try
                {
                    var collection = await UIAsync(() =>
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        if (_activeProfile != null) throw new InvalidOperationException("Capture " + _activeProfile.CaptureId + " is already active; stop or recover it first.");
                        if (CurrentMode == DebugModes.Design) throw new InvalidOperationException("No debugged process to profile.");
                        if (_profiler == null)
                        {
                            _profiler = Profiler.Find(DevenvPath(), out var missing);
                            if (_profiler == null) throw new InvalidOperationException(missing);
                        }
                        var target = ProfileTarget(out var refusal);
                        if (refusal != null) throw new InvalidOperationException(refusal);
                        PruneRawProfiles();
                        var id = captureId;
                        _profiled = target;
                        var profile = new ProfileCollection { CaptureId = id, CollectorSessionId = Guid.NewGuid().ToString("N"), InstanceId = InstanceId(), HostEpoch = HostOperations.Store.Epoch,
                            SessionGeneration = _sessionGeneration, StartedUtc = DateTime.UtcNow, ProcessName = target.Name, Pid = target.Pid,
                            Configuration = ActiveConfiguration(), Status = "starting", RetainRaw = retainRaw,
                            Path = Path.Combine(ProfileDirectory, id + ".diagsession"), MetadataPath = Path.Combine(ProfileDirectory, id + ".json"),
                            Modules = ProfileModules(target.Pid) };
                        try
                        {
                            using (var process = Process.GetProcessById(target.Pid))
                            { profile.ProcessStartedUtc = process.StartTime.ToUniversalTime(); profile.Executable = process.MainModule?.FileName; }
                        }
                        catch (Exception) { }
                        SaveProfile(profile);
                        _activeProfile = profile;
                        Mark("profile-start-requested");
                        HostOperations.Store.Update(operation.OperationId, o => { o.State = "starting"; o.Message = "Capture " + id; });
                        return profile;
                    }).ConfigureAwait(false);
                    // Starting/stopping a collector must never block the VS UI thread.
                    var failure = await _profiler.StartAsync(collection.Pid, collection.Path, collection.CollectorSessionId).ConfigureAwait(false);
                    collection.Status = failure == null ? "collecting" : "start-unknown";
                    collection.Error = failure;
                    try { SaveProfile(collection); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { HostOperations.Store.Update(operation.OperationId, o => o.PersistenceError = ex.Message); }
                    var result = failure == null ? OpResult.Good("Collecting capture " + collection.CaptureId + ", PID " + collection.Pid + ", generation " + collection.SessionGeneration + ".") : OpResult.Bad(failure + " Capture " + collection.CaptureId + " retained for recovery.");
                    HostOperations.Store.Update(operation.OperationId, o => { o.State = collection.Status; o.Result = result; o.Message = result.Message; }, true);
                }
                finally { _profileGate.Release(); }
            });
            var item = await HostOperations.Store.WaitAsync(operation.OperationId, 30, ct).ConfigureAwait(false);
            var response = item.Result ?? new OpResult { Ok = !item.Terminal, Pending = !item.Terminal,
                Message = item.Message ?? "Profile start pending; query operation_status." };
            response.OperationId = item.OperationId;
            response.State = item.State;
            return response;
        }

        readonly object _profileStopGate = new object();

        public Task<ProfileCollection> ProfileStopAsync(CancellationToken ct = default) => StopProfileAsync(null, ct);

        async Task<ProfileCollection> StopProfileAsync(string captureId, CancellationToken ct)
        {
            OperationInfo operation;
            bool created;
            lock (_profileStopGate)
            {
                var pending = HostOperations.Store.All().FirstOrDefault(o => o.Kind == "profile-stop" && !o.Terminal);
                if (pending != null)
                {
                    if (captureId != null && pending.Request != captureId) throw new InvalidOperationException("A different capture stop is pending.");
                    operation = pending;
                    created = false;
                }
                else
                {
                    captureId = captureId ?? _activeProfile?.CaptureId;
                    if (captureId == null) return new ProfileCollection { Error = "Nothing is being profiled. Latest capture is available through profile_status." };
                    operation = HostOperations.Store.Begin("profile-stop", null, captureId, true, out created);
                }
            }
            if (created) HostOperations.Run(operation.OperationId, async () =>
            {
                if (!await _profileGate.WaitAsync(0).ConfigureAwait(false)) throw new InvalidOperationException("Profile start/stop still pending.");
                try
                {
                    var collection = _activeProfile;
                    if (collection != null && collection.CaptureId != operation.Request)
                        throw new InvalidOperationException("A different capture is active; it will not be stopped.");
                    collection = collection ?? await ProfileRecoverAsync(operation.Request).ConfigureAwait(false);
                    if (_activeProfile == null) ProfileArtifacts.ReconcileClosedTrace(collection);
                    if (collection.Status != "collected" && collection.Status != "interrupted")
                    {
                        if (_activeProfile == null)
                        {
                            var profiler = Profiler.Find(Process.GetCurrentProcess().MainModule.FileName, out var missing);
                            if (profiler == null) throw new InvalidOperationException(missing);
                            profiler.RecoverSession(collection.CollectorSessionId, collection.Path, collection.StartedUtc);
                            _profiler = profiler;
                            _activeProfile = collection;
                        }
                        await RefreshProfileModulesAsync(collection).ConfigureAwait(false);
                        Mark("profile-stop-requested");
                        collection.Status = "stopping";
                        SaveProfile(collection);
                        HostOperations.Store.Update(operation.OperationId, o => o.State = "stopping");
                        var stopped = await _profiler.StopAsync().ConfigureAwait(false);
                        collection.Seconds = stopped.Seconds;
                        collection.EndedUtc = DateTime.UtcNow;
                        collection.Error = stopped.Error;
                        collection.Status = stopped.Error == null ? "collected" : "stop-unknown";
                        lock (_historyGate) collection.Interventions = (collection.Interventions ?? new List<Intervention>())
                            .Concat(_interventions.Where(m => m.TimestampUtc >= collection.StartedUtc)).ToList();
                        if (collection.Interventions.Any(m => m.Kind == "mode:design") && stopped.Error == null) collection.Status = "interrupted";
                    }
                    // Keep the result on its operation even if the final metadata write
                    // fails. A later stop must never return another capture's result.
                    try { SaveProfile(collection); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        HostOperations.Store.Update(operation.OperationId, o => o.PersistenceError = ex.Message);
                    }
                    _lastProfile = collection;
                    if (collection.Error == null && _activeProfile?.CaptureId == collection.CaptureId) _activeProfile = null;
                    var snapshot = HostOperations.Copy(collection);
                    HostOperations.Store.Update(operation.OperationId, o => { o.State = snapshot.Status; o.Profile = snapshot;
                        o.Message = "Capture " + snapshot.CaptureId + ": " + (snapshot.Error ?? "collected"); }, true);
                }
                catch (Exception ex) when (HostOperations.Store.Read(operation.OperationId).State == "stopping")
                {
                    var snapshot = _activeProfile == null ? null : HostOperations.Copy(_activeProfile);
                    if (snapshot != null) { snapshot.Status = "stop-unknown"; snapshot.Error = ex.Message; }
                    HostOperations.Store.Update(operation.OperationId, o => { o.State = "stop-unknown";
                        o.Profile = snapshot; o.Message = ex.Message; }, true);
                }
                finally { _profileGate.Release(); }
            });
            var completed = await HostOperations.Store.WaitAsync(operation.OperationId, 30, ct).ConfigureAwait(false);
            if (!completed.Terminal) return new ProfileCollection { CaptureId = operation.Request,
                Error = "Stop pending. Query operation_status " + operation.OperationId + ", then profile_recover with the captureId." };
            return completed.Profile ?? new ProfileCollection { CaptureId = operation.Request, Error = completed.Message };
        }

        readonly object _profileModuleGate = new object();
        Task _profileModuleRefresh;
        long _profileModuleRevision;
        ProfileCollection _profileModuleRequestedCollection;

        public void ProfileModulesChanged()
        {
            var collection = _activeProfile;
            if (collection != null) _ = RefreshProfileModulesAsync(collection);
        }

        async Task RefreshProfileModulesAsync(ProfileCollection collection)
        {
            Task refresh;
            lock (_profileModuleGate)
            {
                _profileModuleRevision++;
                _profileModuleRequestedCollection = collection;
                // The shared task lasts until the actual UI work finishes, including
                // after any observer times out. DLL bursts cannot queue another walk.
                if (_profileModuleRefresh == null)
                    _profileModuleRefresh = Task.Run(RefreshProfileModulesLoopAsync);
                refresh = _profileModuleRefresh;
            }
            if (await Task.WhenAny(refresh, Task.Delay(1000)).ConfigureAwait(false) != refresh)
            {
                // Symbol attribution may be incomplete; preserve evidence for export.
                lock (collection) collection.RetainRaw = true;
            }
        }

        async Task RefreshProfileModulesLoopAsync()
        {
            while (true)
            {
                long revision;
                ProfileCollection collection;
                lock (_profileModuleGate)
                {
                    revision = _profileModuleRevision;
                    collection = _profileModuleRequestedCollection;
                }
                await ReadProfileModulesAsync(collection).ConfigureAwait(false);
                lock (_profileModuleGate)
                {
                    // A load or stop arriving after the snapshot needs one fresh
                    // walk. This also transfers the worker to a newer capture.
                    if (revision != _profileModuleRevision) continue;
                    _profileModuleRefresh = null;
                    return;
                }
            }
        }

        async Task ReadProfileModulesAsync(ProfileCollection collection)
        {
            try
            {
                var modules = await UIAsync(() =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    if (_activeProfile != collection || collection.HostEpoch != HostOperations.Store.Epoch ||
                        collection.SessionGeneration != _sessionGeneration || CurrentMode == DebugModes.Design)
                        return null;
                    return ProfileModules(collection.Pid);
                }).ConfigureAwait(false);
                if (modules == null) return;
                lock (collection)
                {
                    collection.Modules = ProfileArtifacts.MergeModules(collection.Modules, modules);
                    SaveProfile(collection);
                }
            }
            catch (Exception)
            {
                lock (collection) collection.RetainRaw = true;
                // Preserve the last catalog when VS cannot inspect modules.
            }
        }

        public Task<ProfileCollection> ProfileRecoverAsync(string captureId, CancellationToken ct = default)
        {
            if (!ShortId.IsCaptureId(captureId)) throw new ArgumentException("Invalid captureId. Use the ID returned by profile_status.");
            var path = Path.Combine(ProfileDirectory, captureId + ".json");
            if (!File.Exists(path)) throw new ArgumentException("No retained capture " + captureId);
            var collection = JsonConvert.DeserializeObject<ProfileCollection>(File.ReadAllText(path));
            return Task.FromResult(collection);
        }

        public Task<ProfileCollection> ProfileStopCaptureAsync(string captureId, CancellationToken ct = default)
        {
            if (!ShortId.IsCaptureId(captureId)) throw new ArgumentException("Invalid captureId. Use the ID returned by profile_status.");
            return StopProfileAsync(captureId, ct);
        }

        static void PruneRawProfiles()
        {
            Directory.CreateDirectory(ProfileDirectory);
            var artifacts = new List<RetainedArtifact>();
            foreach (var path in Directory.EnumerateFiles(ProfileDirectory, "*.diagsession"))
            {
                var file = new FileInfo(path);
                var metadata = System.IO.Path.ChangeExtension(path, ".json");
                var protectedFile = true;
                try
                {
                    if (File.Exists(metadata))
                    {
                        var record = JsonConvert.DeserializeObject<ProfileCollection>(File.ReadAllText(metadata));
                        protectedFile = record.Status != "collected" && record.Status != "interrupted";
                    }
                }
                catch (IOException) { }
                catch (JsonException) { }
                artifacts.Add(new RetainedArtifact { Path = path, TimestampUtc = file.LastWriteTimeUtc, Bytes = file.Length, Protected = protectedFile });
            }
            var limit = (long)RetentionPolicy.Setting("VSDBGMCP_RAW_BUDGET_MB", 2048) * 1024 * 1024;
            foreach (var file in RetentionPolicy.Expired(artifacts, DateTime.UtcNow, RetentionPolicy.Setting("VSDBGMCP_RAW_RETENTION_DAYS", 7), limit))
                File.Delete(file.Path);
            var remaining = Directory.EnumerateFiles(ProfileDirectory, "*.diagsession").Sum(p => new FileInfo(p).Length);
            if (remaining >= limit) throw new IOException("Retained raw traces exceed the configured budget; export or remove old captures before collecting again.");
        }

        static void SaveProfile(ProfileCollection collection)
        {
            lock (collection)
            {
                Directory.CreateDirectory(ProfileDirectory);
                var temp = collection.MetadataPath + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(collection, Formatting.Indented));
                if (File.Exists(collection.MetadataPath)) File.Replace(temp, collection.MetadataPath, null);
                else File.Move(temp, collection.MetadataPath);
            }
        }
    }
}
