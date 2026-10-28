using System;
using System.Collections.Generic;
using System.Linq;
using VsDbgMcp.Contracts;
using VsDbgMcp.Shim.Profiling;

namespace VsDbgMcp.Shim.Tools
{
    // Model-facing metadata deliberately excludes the collector's private GUID.
    // Persistence and the host RPC contract retain it for collector recovery.
    public sealed class ProfileResponse
    {
        public string CaptureId { get; set; }
        public bool? RawAvailable { get; set; }
        public string InstanceId { get; set; }
        public string HostEpoch { get; set; }
        public int SessionGeneration { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public DateTime? ProcessStartedUtc { get; set; }
        public string Configuration { get; set; }
        public string Executable { get; set; }
        public string Status { get; set; }
        public bool RetainRaw { get; set; }
        public List<ModuleInfo> Modules { get; set; }
        public List<Intervention> Interventions { get; set; }
        public string MetadataPath { get; set; }
        public string Path { get; set; }
        public string ProcessName { get; set; }
        public int Pid { get; set; }
        public double Seconds { get; set; }
        public string Error { get; set; }

        /// <summary>
        /// True when the saved aggregate for this capture is there but cannot be read, so a
        /// report or an export of it is refused and the captureId here is what profile_recover
        /// needs; false when it reads; null when the caller had no capture store to ask. Status
        /// says what the collector recorded when the capture ended, which is not the same thing
        /// as what still survives on disk: a capture can be aggregated and unreadable.
        /// </summary>
        public bool? NeedsRecovery { get; set; }

        public static ProfileResponse From(ProfileCollection profile) => From(profile, null);

        /// <summary>
        /// The metadata as the collector left it, checked against the captures the store cannot
        /// read. A status of aggregated over a file that cannot be parsed is the one state a
        /// model has to be able to see before it spends a call on it. The list is read once by
        /// the caller, because reading it checks every unreadable file on disk.
        /// </summary>
        public static ProfileResponse From(ProfileCollection profile, IReadOnlyList<Captures.Entry> needsRecovery)
        {
            if (profile == null) return null;
            var instance = profile.InstanceId;
            var separator = instance?.LastIndexOf('#') ?? -1;
            if (separator >= 0 && int.TryParse(instance.Substring(separator + 1), out var pid))
                instance = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new ProfileResponse
            {
                CaptureId = profile.CaptureId, RawAvailable = profile.RawAvailable, InstanceId = instance,
                HostEpoch = profile.HostEpoch, SessionGeneration = profile.SessionGeneration,
                StartedUtc = profile.StartedUtc, EndedUtc = profile.EndedUtc, ProcessStartedUtc = profile.ProcessStartedUtc,
                Configuration = profile.Configuration, Executable = profile.Executable, Status = profile.Status,
                RetainRaw = profile.RetainRaw, Modules = profile.Modules, Interventions = profile.Interventions,
                MetadataPath = profile.MetadataPath, Path = profile.Path, ProcessName = profile.ProcessName,
                Pid = profile.Pid, Seconds = profile.Seconds, Error = profile.Error,
                NeedsRecovery = needsRecovery == null
                    ? (bool?) null
                    : needsRecovery.Any(e => string.Equals(IdOf(e), profile.CaptureId, StringComparison.OrdinalIgnoreCase))
            };
        }

        /// <summary>The identity a capture is listed and recovered under.</summary>
        static string IdOf(Captures.Entry entry) =>
            string.IsNullOrEmpty(entry.CaptureId)
                ? (entry.Path == null ? null : System.IO.Path.GetFileNameWithoutExtension(entry.Path))
                : entry.CaptureId;
    }

    public sealed class DebugStateResponse
    {
        public string Mode { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string EvidenceSource { get; set; }
        public string Error { get; set; }
        public bool? BuildBusy { get; set; }
        public int SessionGeneration { get; set; }
        public List<ProcessInfo> Processes { get; set; }
        public Intervention LastTransition { get; set; }
        public ProfileResponse ActiveProfile { get; set; }
        public ProfileResponse LastProfile { get; set; }

        public static DebugStateResponse From(StateObservation state) => new DebugStateResponse
        {
            Mode = state.Mode, TimestampUtc = state.TimestampUtc, EvidenceSource = state.EvidenceSource,
            Error = state.Error, BuildBusy = state.BuildBusy, SessionGeneration = state.SessionGeneration,
            Processes = state.Processes, LastTransition = state.LastTransition,
            ActiveProfile = ProfileResponse.From(state.ActiveProfile), LastProfile = ProfileResponse.From(state.LastProfile)
        };
    }
}
